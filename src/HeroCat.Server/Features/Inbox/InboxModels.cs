using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeroCat.Server.Features.Inbox;

/// <summary>The Inbox endpoints' own error codes (specs/08 section 3.2) and their statuses.</summary>
public static class InboxErrors
{
    /// <summary>404: no such item for this player (also an id that is not a UUID, and an expired row the nightly purge removed).</summary>
    public const string INBOX_NOT_FOUND = "INBOX_NOT_FOUND";
    /// <summary>409: a scheduled package portion whose <c>available_at</c> is still ahead (issue #51).</summary>
    public const string INBOX_NOT_AVAILABLE = "INBOX_NOT_AVAILABLE";
    /// <summary>409, success-equivalent: <c>details</c> carries the original <c>{ inboxItemId, grants, reason }</c> (issue #176).</summary>
    public const string INBOX_ALREADY_CLAIMED = "INBOX_ALREADY_CLAIMED";
    /// <summary>410: <c>expires_at</c> reached before the claim.</summary>
    public const string INBOX_EXPIRED = "INBOX_EXPIRED";
    /// <summary>
    /// 409: the item carries an item line (<c>kind: item</c>), which the client's <c>ApplyInboxGrant</c> cannot apply
    /// until #271 decides how; the item stays unclaimed and nothing is written.
    /// </summary>
    public const string INBOX_ITEM_GRANT_UNSUPPORTED = "INBOX_ITEM_GRANT_UNSUPPORTED";
    /// <summary>409: a grant line that is not a known currency with a positive whole amount (a server data error, logged).</summary>
    public const string INBOX_BAD_GRANT = "INBOX_BAD_GRANT";

    public static int Status(string code) => code switch
    {
        INBOX_NOT_FOUND => StatusCodes.Status404NotFound,
        INBOX_EXPIRED => StatusCodes.Status410Gone,
        _ => StatusCodes.Status409Conflict,
    };
}

/// <summary>One grant line, 08 section 3.2: <c>{ "kind": "currency", "id": "DIAMOND", "amount": 2400 }</c>.</summary>
public sealed record InboxGrantLine(string Kind, string Id, long Amount);

/// <summary>One <c>GET /inbox</c> row.</summary>
public sealed record InboxItemDto(
    string InboxItemId, string Kind, string TitleKey, string BodyKey, JsonElement Params, IReadOnlyList<InboxGrantLine> Grants,
    long CreatedAt, long? ExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? AvailableAt);

/// <summary><c>GET /inbox</c> 200.</summary>
public sealed record InboxListResponse(IReadOnlyList<InboxItemDto> Items, long ServerTime);

/// <summary><c>POST /inbox/claim</c> body.</summary>
public sealed class InboxClaimRequest
{
    [Required] public string? InboxItemId { get; set; }
}

/// <summary><c>POST /inbox/claim</c> 200.</summary>
public sealed record InboxClaimResponse(string InboxItemId, IReadOnlyList<InboxGrantLine> Grants, IReadOnlyDictionary<string, long> Balances,
                                        string Reason, long ServerTime);

/// <summary>The <c>details</c> of <c>409 INBOX_ALREADY_CLAIMED</c>: the claim's item shape.</summary>
public sealed record InboxClaimedDetails(string InboxItemId, IReadOnlyList<InboxGrantLine> Grants, string Reason);

/// <summary>
/// One <c>POST /inbox/claim-all</c> row: <c>{ inboxItemId, grants, reason }</c> when claimed, <c>{ inboxItemId, code }</c>
/// when skipped (issue #176).
/// </summary>
public sealed record InboxClaimAllRow(
    string InboxItemId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<InboxGrantLine>? Grants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code);

/// <summary><c>POST /inbox/claim-all</c> 200.</summary>
public sealed record InboxClaimAllResponse(IReadOnlyList<InboxClaimAllRow> Claimed, IReadOnlyDictionary<string, long> Balances);
