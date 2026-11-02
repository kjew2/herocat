using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using HeroCat.Server.Features.Config;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Hosting;
using HeroCat.Server.Pipeline;
using HeroCat.Sim;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SimDungeon = HeroCat.Sim.Content.Dungeon;

namespace HeroCat.Server.Features.Dungeon;

/// <summary>Where seeds come from: the server CSPRNG (specs/08 section 3.3); tests pass a fixed source.</summary>
public interface IDungeonSeedSource
{
    ulong Next();
}

public sealed class CsprngDungeonSeedSource : IDungeonSeedSource
{
    public ulong Next() => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
}

/// <summary>
/// The <c>POST /dungeon/seed</c> handler (specs/08 section 3.3, issue #205). The idempotency middleware has already
/// replayed a known request id, so a retry gets the same seed and grant. Checks, in order: the body (<c>kind</c> a
/// <c>Dungeon</c> with a row in the client's content, <c>floor</c> in <c>[1, floors]</c>), the <c>dungeonsEnabled</c> kill
/// switch (<c>503 DUNGEONS_DISABLED</c>, never stored, so a retry after the switch is lifted is served), then in one
/// transaction holding the account row: the unlock against the last uploaded save (<c>floor &lt;= BestFloor + 2</c>, else
/// <c>403 DUNGEON_LOCKED</c>) and the daily cap (<c>DUNGEON_SEEDS_PER_DAY</c> unexpired seeds of this kind and day key,
/// else <c>429 DUNGEON_SEED_CAP</c>); then 64 CSPRNG bits go to <c>dungeon_seeds</c> with
/// <c>expires_at = now + DUNGEON_SEED_TTL_S</c>, committed with the idempotency row.
/// </summary>
public sealed class DungeonSeedService
{
    readonly DungeonStore _store;
    readonly IContentSets _content;
    readonly IActiveConfigSource _config;
    readonly IDungeonSeedSource _seeds;
    readonly TimeProvider _clock;
    readonly ServerLimits _limits;

    public DungeonSeedService(DungeonStore store, IContentSets content, IActiveConfigSource config, IDungeonSeedSource seeds, TimeProvider clock,
                              IOptions<ServerOptions> options)
    {
        _store = store;
        _content = content;
        _config = config;
        _seeds = seeds;
        _clock = clock;
        _limits = options.Value.Limits;
    }

    /// <summary><c>dungeonsEnabled</c> in the active config's kill switches; absent (or no active config) is enabled.</summary>
    public const string KillSwitch = "dungeonsEnabled";

    /// <summary>A floor may be at most this many above the last upload's best (the client may be one clear ahead of it).</summary>
    public const int UnlockTolerance = 2;

    int PerDay => _limits.DungeonSeedsPerDay > 0 ? _limits.DungeonSeedsPerDay : 10;
    int TtlSeconds => _limits.DungeonSeedTtlSeconds > 0 ? _limits.DungeonSeedTtlSeconds : 600;

    public async Task<IActionResult> SeedAsync(Guid player, string requestId, DungeonSeedRequest req, IIdempotencyFeature? idempotency, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Kind) || !Enum.TryParse(req.Kind, false, out SimDungeon kind) || !Enum.IsDefined(kind) ||
            req.Kind != kind.ToString())
            throw Validation("kind must be a dungeon kind.", "kind");
        if (req.Floor is not int floor) throw Validation("floor is required.", "floor");
        if (string.IsNullOrWhiteSpace(req.ClientContentVersion)) throw Validation("clientContentVersion is required.", "clientContentVersion");

        var db = _content.Resolve(req.ClientContentVersion);
        var def = db.DungeonOf(kind);
        if (def == null) throw Validation("This content has no such dungeon.", "kind");
        if (floor < 1 || floor > def.FloorDefs.Count) throw Validation($"floor must be in [1, {def.FloorDefs.Count}].", "floor");

        var config = await _config.GetActiveAsync(ct).ConfigureAwait(false);
        if (config != null && !config.Enabled(KillSwitch))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, DungeonErrors.DUNGEONS_DISABLED, "Dungeons are paused.");

        var now = _clock.GetUtcNow();
        long nowSec = now.ToUnixTimeSeconds();
        int dayKey = DayKeys.DayKey(nowSec, db.Constants.DaySeconds);
        string kindName = kind.ToString();

        await using var c = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await DungeonStore.LockAccountAsync(c, tx, player, ct).ConfigureAwait(false))
            throw ApiException.Of(ErrorCodes.UNAUTHENTICATED, "No account for this token.");

        int best = await DungeonStore.BestFloorAsync(c, tx, player, kindName, ct).ConfigureAwait(false);
        if (floor > best + UnlockTolerance)
            throw new ApiException(StatusCodes.Status403Forbidden, DungeonErrors.DUNGEON_LOCKED, "This floor is not unlocked yet.",
                                   new { kind = kindName, floor, bestFloor = best });
        int outstanding = await DungeonStore.OutstandingAsync(c, tx, player, kindName, dayKey, now, ct).ConfigureAwait(false);
        if (outstanding >= PerDay)
            throw new ApiException(StatusCodes.Status429TooManyRequests, DungeonErrors.DUNGEON_SEED_CAP, "Too many dungeon runs today.",
                                   new { kind = kindName, dayKey, cap = PerDay });

        var grantId = Guid.NewGuid();
        ulong seed = _seeds.Next();
        var expires = now.AddSeconds(TtlSeconds);
        await DungeonStore.InsertAsync(c, tx, grantId, player, requestId, kindName, floor, dayKey, seed, now, expires, ct).ConfigureAwait(false);

        var body = new DungeonSeedResponse(grantId.ToString("D"), SeedHex(seed), expires.ToUnixTimeSeconds(), nowSec);
        IActionResult result = idempotency != null
            ? await idempotency.CompleteWithinAsync(c, tx, StatusCodes.Status200OK, body, ct).ConfigureAwait(false)
            : new OkObjectResult(body);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>The wire form of a seed: 16 lowercase hex digits.</summary>
    public static string SeedHex(ulong seed) => seed.ToString("x16", CultureInfo.InvariantCulture);

    static ApiException Validation(string message, string field) => ApiException.Of(ErrorCodes.VALIDATION, message, new { field });
}
