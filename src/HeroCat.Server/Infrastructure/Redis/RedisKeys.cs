using System.Globalization;

namespace HeroCat.Server.Infrastructure.Redis;

/// <summary>
/// The Redis key layout of specs/02 section 12 (issue #154), in one place the 08 handlers use: every key name and its
/// TTL. Player ids are formatted as lowercase 36-character GUIDs ("D"), week keys as the integer of 00 section 8 item 7
/// (<c>floor((dayKey - 4) / 7)</c>). A test diffs <see cref="Patterns"/> and the TTLs against the spec table.
/// </summary>
public static class RedisKeys
{
    /// <summary>Weekly stage leaderboard (P1), ZSET: member playerId, score <c>HighestCleared * 1e6 + (1e6 - secondsIntoWeek/60)</c>.</summary>
    public static string StageBoard(long weekKey) => "lb:stage:" + weekKey.ToString(CultureInfo.InvariantCulture);

    /// <summary>Weekly fish leaderboard (P1), ZSET: score <c>HighestFish</c>.</summary>
    public static string FishBoard(long weekKey) => "lb:fish:" + weekKey.ToString(CultureInfo.InvariantCulture);

    /// <summary>Display name + avatar for leaderboard rows (P1), STRING.</summary>
    public static string BoardName(Guid playerId) => "lb:name:" + Id(playerId);

    /// <summary>Sliding-window rate-limit counter for one route and player (08 section 1.1), STRING. TTL is the window length.</summary>
    public static string RateLimit(string route, Guid playerId)
    {
        if (string.IsNullOrEmpty(route) || route.Contains(' ') || route.Contains('{') || route.Contains('}'))
            throw new ArgumentException("route must be a non-empty key segment", nameof(route));
        return "rl:" + route + ":" + Id(playerId);
    }

    /// <summary>Session HASH: last device, last ip hash, last activity.</summary>
    public static string Session(Guid playerId) => "sess:" + Id(playerId);

    /// <summary>Per-player mutex for the summon handler (08 section 4.3), STRING.</summary>
    public static string SummonLock(Guid playerId) => "summon:lock:" + Id(playerId);

    /// <summary>Maintenance JSON (08 section 8.4), STRING, no TTL.</summary>
    public const string Maintenance = "maint";

    public static readonly TimeSpan BoardTtl = TimeSpan.FromDays(14);
    public static readonly TimeSpan BoardNameTtl = TimeSpan.FromDays(1);
    public static readonly TimeSpan SessionTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan SummonLockTtl = TimeSpan.FromSeconds(10);

    /// <summary>The TTL of a rate-limit counter: its window.</summary>
    public static TimeSpan RateLimitTtl(TimeSpan window) => window;

    /// <summary>Every key pattern in spec order, with its type and TTL.</summary>
    public static readonly IReadOnlyList<RedisKeySpec> Patterns = new RedisKeySpec[]
    {
        new("lb:stage:{weekKey}", "ZSET", BoardTtl),
        new("lb:fish:{weekKey}", "ZSET", BoardTtl),
        new("lb:name:{playerId}", "STRING", BoardNameTtl),
        new("rl:{route}:{playerId}", "STRING", null, TtlIsWindow: true),
        new("sess:{playerId}", "HASH", SessionTtl),
        new("summon:lock:{playerId}", "STRING", SummonLockTtl),
        new("maint", "STRING", null),
    };

    static string Id(Guid playerId) => playerId.ToString("D");
}

/// <summary>One row of the key table: <see cref="Ttl"/> is null for no TTL, and for the rate limit, whose TTL is its window (<see cref="TtlIsWindow"/>).</summary>
public sealed record RedisKeySpec(string Pattern, string Type, TimeSpan? Ttl, bool TtlIsWindow = false);
