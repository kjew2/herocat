using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Mvc;

namespace HeroCat.Server.Features.Summon;

/// <summary><c>POST /summon/pull</c>, <c>GET /summon/state</c>, <c>GET /summon/history</c> (specs/08 sections 4.3-4.5, issue #199).</summary>
[ApiController]
public sealed class SummonController : ControllerBase
{
    readonly SummonService _summons;

    public SummonController(SummonService summons) => _summons = summons;

    /// <summary>
    /// One pull or a 10-pull, rolled on the server with the shared Sim algorithm. Errors: <c>402 INSUFFICIENT_FUNDS</c>,
    /// <c>409 PAYMENT_MISMATCH</c> (diamonds while holding enough tickets), <c>409 BANNER_CLOSED</c>,
    /// <c>409 CONTENT_TOO_OLD</c>, <c>409 IDEMPOTENCY_IN_FLIGHT</c> (another pull of this player holds the lock),
    /// <c>503 SUMMON_DISABLED</c> (kill switch).
    /// </summary>
    [HttpPost("v1/summon/pull"), Auth(AuthPolicy.JwtDevice), RateLimit(20), Idempotent]
    [ProducesResponseType(typeof(SummonPullResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Pull(SummonPullRequest request, CancellationToken ct)
    {
        var player = HttpContext.PlayerId() ?? throw ApiException.Of(ErrorCodes.UNAUTHENTICATED);
        var idempotency = HttpContext.Features.Get<IIdempotencyFeature>();
        string requestId = idempotency?.RequestId ?? RequestContext.RequestId(HttpContext);
        return _summons.PullAsync(player, requestId, request, idempotency, ct);
    }

    /// <summary>The player's pity, banner level, cumulative EXP and total pulls per banner; the client overwrites its mirrors at boot.</summary>
    [HttpGet("v1/summon/state"), Auth(AuthPolicy.Jwt), RateLimit(30)]
    [ProducesResponseType(typeof(SummonStateResponse), StatusCodes.Status200OK)]
    public Task<SummonStateResponse> State(CancellationToken ct)
    {
        var player = HttpContext.PlayerId() ?? throw ApiException.Of(ErrorCodes.UNAUTHENTICATED);
        return _summons.StateAsync(player, ct);
    }

    /// <summary>The player's own pulls, newest first (item, rarity, pity, time; never the uniforms).</summary>
    [HttpGet("v1/summon/history"), Auth(AuthPolicy.Jwt), RateLimit(10)]
    [ProducesResponseType(typeof(SummonHistoryResponse), StatusCodes.Status200OK)]
    public Task<SummonHistoryResponse> History([FromQuery] string? banner, [FromQuery] int? limit, CancellationToken ct)
    {
        var player = HttpContext.PlayerId() ?? throw ApiException.Of(ErrorCodes.UNAUTHENTICATED);
        return _summons.HistoryAsync(player, banner, limit, ct);
    }
}
