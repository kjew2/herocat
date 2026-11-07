namespace HeroCat.Server.Features.Dungeon;

/// <summary><c>POST /dungeon/seed</c> body (specs/08 section 3.3). <c>kind</c> is a <c>Dungeon</c> name (<c>FISH_COIN</c>, ...).</summary>
public sealed record DungeonSeedRequest(string? Kind, int? Floor, string? ClientContentVersion);

/// <summary>
/// <c>POST /dungeon/seed</c> 200: the grant (a UUID, <c>D</c> format), the 64-bit seed as 16 lowercase hex, its expiry and
/// the server time, in unix seconds.
/// </summary>
public sealed record DungeonSeedResponse(string GrantId, string Seed, long ExpiresAt, long ServerTime);

/// <summary>The Dungeon endpoint's own error codes (specs/08 section 3.3).</summary>
public static class DungeonErrors
{
    /// <summary>403: the floor is more than one above the last uploaded save's <c>BestFloor + 1</c>.</summary>
    public const string DUNGEON_LOCKED = "DUNGEON_LOCKED";

    /// <summary>429: <c>DUNGEON_SEEDS_PER_DAY</c> seeds already issued for this kind and day key, expired or not.</summary>
    public const string DUNGEON_SEED_CAP = "DUNGEON_SEED_CAP";

    /// <summary>503: the active config's <c>killSwitches.dungeonsEnabled</c> is false (as SUMMON_DISABLED; issue #205).</summary>
    public const string DUNGEONS_DISABLED = "DUNGEONS_DISABLED";
}

/// <summary>The plausibility flag the seed/save correlation raises (specs/08 sections 7.2-7.4).</summary>
public static class DungeonFlags
{
    /// <summary><c>certain</c>: a <c>RecentRuns</c> entry or a <c>BestFloor</c> increase with no seed issued for that (kind, floor).</summary>
    public const string DUNGEON_WITHOUT_SEED = "DUNGEON_WITHOUT_SEED";
}
