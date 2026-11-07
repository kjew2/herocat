using System.Data.Common;
using System.Text.Json;
using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Mvc;

namespace HeroCat.Server.Features.Inbox;

/// <summary>
/// <c>GET /inbox</c>, <c>POST /inbox/claim</c>, <c>POST /inbox/claim-all</c> (specs/08 section 3.2, specs/06 section 9;
/// issue #201). The wire shape is the one <c>HeroCat.Remote.Services.RemoteInboxService</c> reads.
/// </summary>
[ApiController]
public sealed class InboxController : ControllerBase
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly InboxStore _store;
    readonly TimeProvider _clock;

    public InboxController(InboxStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    /// <summary>
    /// The player's unclaimed, unexpired, available items, oldest first, plus the next scheduled portion of each package
    /// with <c>availableAt</c> set (issue #51). <c>expiresAt</c> is null for an item that never expires (IAP, 06 rule 9.1).
    /// </summary>
    [HttpGet("v1/inbox"), Auth(AuthPolicy.Jwt), RateLimit(30)]
    [ProducesResponseType(typeof(InboxListResponse), StatusCodes.Status200OK)]
    public async Task<InboxListResponse> List(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var rows = await _store.ListAsync(HttpContext.PlayerId()!.Value, now, ct);
        var items = rows.Select(r => new InboxItemDto(
            InboxStore.IdString(r.Id), r.Kind, r.TitleKey, r.BodyKey, ParseParams(r.ParamsJson),
            (IReadOnlyList<InboxGrantLine>?)InboxStore.ParseGrants(r.GrantsJson) ?? Array.Empty<InboxGrantLine>(),
            r.CreatedAt.ToUnixTimeSeconds(), r.ExpiresAt?.ToUnixTimeSeconds(),
            r.AvailableAt > now ? r.AvailableAt.ToUnixTimeSeconds() : null)).ToList();
        return new InboxListResponse(items, now.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Claims one item: its DIAMOND and summon-ticket lines go to the server ledgers (<c>ref_type = inbox_item</c>) and the
    /// row is marked claimed, in one transaction with the idempotency row. Errors: <c>404 INBOX_NOT_FOUND</c>,
    /// <c>409 INBOX_ALREADY_CLAIMED</c> (<c>details</c> = the original <c>{ inboxItemId, grants, reason }</c>),
    /// <c>409 INBOX_NOT_AVAILABLE</c>, <c>410 INBOX_EXPIRED</c> and <c>409 INBOX_BAD_GRANT</c> (a malformed line). Item lines
    /// are only returned: the client's <c>ApplyInboxGrant</c> adds the copies (00 decision 116).
    /// </summary>
    [HttpPost("v1/inbox/claim"), Auth(AuthPolicy.Jwt), RateLimit(30), Idempotent]
    [ProducesResponseType(typeof(InboxClaimResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Claim(InboxClaimRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.InboxItemId, out var id))
            throw new ApiException(StatusCodes.Status404NotFound, InboxErrors.INBOX_NOT_FOUND, "No such inbox item.");
        var (requestId, complete) = Completion();
        return await _store.ClaimAsync(HttpContext.PlayerId()!.Value, id, requestId, _clock.GetUtcNow(), complete, ct);
    }

    /// <summary>
    /// Claims up to 50 available items oldest first. A row that cannot be claimed (expired since the list, an unsupported
    /// grant) is reported as <c>{ inboxItemId, code }</c> and skipped, not an error.
    /// </summary>
    [HttpPost("v1/inbox/claim-all"), Auth(AuthPolicy.Jwt), RateLimit(10), Idempotent]
    [ProducesResponseType(typeof(InboxClaimAllResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ClaimAll(CancellationToken ct)
    {
        var (requestId, complete) = Completion();
        return await _store.ClaimAllAsync(HttpContext.PlayerId()!.Value, requestId, _clock.GetUtcNow(), complete, ct);
    }

    /// <summary>The request id and how the store commits the final idempotency row with its writes (specs/08 section 4.3).</summary>
    (string requestId, CompleteWithin complete) Completion()
    {
        var feature = HttpContext.Features.Get<IIdempotencyFeature>()
                      ?? throw new InvalidOperationException("an [Idempotent] inbox route ran without the idempotency middleware");
        return (feature.RequestId,
                (DbConnection c, DbTransaction tx, object body, CancellationToken ct) => feature.CompleteWithinAsync(c, tx, StatusCodes.Status200OK, body, ct));
    }

    static JsonElement ParseParams(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
