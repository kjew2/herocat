using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Mvc;

namespace HeroCat.Server.Features.Dungeon;

/// <summary><c>POST /dungeon/seed</c> (specs/08 section 3.3, issue #205). The wire shape is the one <c>HeroCat.Remote.Services.DungeonSeedClient</c> reads.</summary>
[ApiController]
public sealed class DungeonController : ControllerBase
{
    readonly DungeonSeedService _seeds;

    public DungeonController(DungeonSeedService seeds) => _seeds = seeds;

    /// <summary>
    /// Issues a run seed for one floor. Errors: <c>400 VALIDATION</c> (kind, floor outside <c>[1, floors]</c>),
    /// <c>403 DUNGEON_LOCKED</c> (more than one floor past the last upload's <c>BestFloor + 1</c>), <c>429 DUNGEON_SEED_CAP</c>
    /// (<c>DUNGEON_SEEDS_PER_DAY</c> seeds of this kind issued today, expired or not), <c>503 DUNGEONS_DISABLED</c> (kill switch).
    /// </summary>
    [HttpPost("v1/dungeon/seed"), Auth(AuthPolicy.Jwt), RateLimit(20), RateLimit(60, RateLimitAttribute.Day), Idempotent]
    [ProducesResponseType(typeof(DungeonSeedResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Seed(DungeonSeedRequest request, CancellationToken ct)
    {
        var player = HttpContext.PlayerId() ?? throw ApiException.Of(ErrorCodes.UNAUTHENTICATED);
        var idempotency = HttpContext.Features.Get<IIdempotencyFeature>();
        string requestId = idempotency?.RequestId ?? RequestContext.RequestId(HttpContext);
        return _seeds.SeedAsync(player, requestId, request, idempotency, ct);
    }
}
