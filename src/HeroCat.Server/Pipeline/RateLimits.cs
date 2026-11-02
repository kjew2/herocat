using System.Globalization;
using HeroCat.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace HeroCat.Server.Pipeline;

/// <summary>
/// Declares one of an endpoint's rate limits (specs/08 endpoint lines, "5/min", "20/day", "1 per 7 days"): at most
/// <see cref="Limit"/> accepted requests in any <see cref="WindowSeconds"/>-long sliding window, per player (per client
/// IP on an <c>auth none</c> route). An endpoint may declare several; a request must fit all of them. The declared value
/// is the default: the active config's <c>limits</c> overrides two of them by name (<see cref="RemoteLimits"/>, issue #265).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RateLimitAttribute : Attribute
{
    public const int Minute = 60;
    public const int Hour = 3600;
    public const int Day = 86400;
    public const int Week = 7 * Day;

    public RateLimitAttribute(int limit, int windowSeconds = Minute)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (windowSeconds < 1) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        Limit = limit;
        WindowSeconds = windowSeconds;
    }

    public int Limit { get; }
    public int WindowSeconds { get; }

    public RateLimitRule Rule => new(Limit, WindowSeconds);
}

/// <summary>
/// The active config's <c>limits</c> (specs/08 section 6.1): <c>summonPerMinute</c> replaces the per-minute rule of
/// <c>POST /summon/pull</c> and <c>saveUploadsPerDay</c> the per-day rule of <c>PUT /save</c>; null keeps the declared
/// value (issue #265).
/// </summary>
public sealed record RemoteLimits(int? SummonPerMinute, int? SaveUploadsPerDay)
{
    /// <summary>The overridable rules: route key (<see cref="RateLimitRoutes.Of"/>), the window it replaces, and its value.</summary>
    static readonly (string route, int window, Func<RemoteLimits, int?> value)[] Overrides =
    {
        ("post/summon/pull", RateLimitAttribute.Minute, l => l.SummonPerMinute),
        ("put/save", RateLimitAttribute.Day, l => l.SaveUploadsPerDay),
    };

    /// <summary>The declared rules with this config's overrides applied (a non-positive value is ignored).</summary>
    public IReadOnlyList<RateLimitRule> Apply(string route, IReadOnlyList<RateLimitRule> declared)
    {
        List<RateLimitRule>? result = null;
        foreach (var (r, window, value) in Overrides)
        {
            if (!string.Equals(r, route, StringComparison.Ordinal) || value(this) is not int limit || limit < 1) continue;
            result ??= declared.ToList();
            for (int i = 0; i < result.Count; i++)
                if (result[i].WindowSeconds == window) result[i] = new RateLimitRule(limit, window);
        }
        return result ?? declared;
    }
}

public sealed record RateLimitRule(int Limit, int WindowSeconds)
{
    /// <summary>The OpenAPI <c>x-herocat-rate-limits</c> spelling: <c>5/60s</c>.</summary>
    public override string ToString() => $"{Limit}/{WindowSeconds}s";
}

/// <param name="RetryAfterMs">0 when allowed; else how long until the request would fit every window.</param>
public sealed record RateLimitDecision(bool Allowed, long RetryAfterMs)
{
    public static readonly RateLimitDecision Allow = new(true, 0);

    /// <summary>Whole seconds for <c>Retry-After</c> and <c>retryAfterSec</c>, rounded up, at least 1.</summary>
    public int RetryAfterSec => (int)Math.Max(1, Math.Min(int.MaxValue, (RetryAfterMs + 999) / 1000));
}

/// <summary>
/// A sliding-window log per key: the accepted requests' timestamps. A request is accepted when, for every rule, fewer
/// than <c>Limit</c> accepted requests fall in the last <c>WindowSeconds</c>; only accepted requests are recorded, so a
/// caller that keeps hammering a full window recovers as soon as the window slides.
/// </summary>
public interface IRateLimitStore
{
    Task<RateLimitDecision> AcquireAsync(string key, IReadOnlyList<RateLimitRule> rules, long nowMs, CancellationToken ct);
}

/// <summary>The route segment of the rate-limit keys (<see cref="RedisKeys.RateLimit"/>, specs/02 section 12).</summary>
public static class RateLimitRoutes
{
    /// <summary>The lowercase method and route template below <c>/v1</c>, parameters as <c>_</c>: <c>put/save</c>, <c>get/admin/players/_</c>.</summary>
    public static string Of(string method, string template)
    {
        string t = "/" + template.Trim('/');
        if (t.StartsWith(RouteRules.ApiBase + "/", StringComparison.OrdinalIgnoreCase)) t = t[RouteRules.ApiBase.Length..];
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\{[^}]*\}", "_").Replace(':', '_').Replace(' ', '_');
        return method.ToLowerInvariant() + t.ToLowerInvariant();
    }
}

/// <summary>Per-process windows: tests, and a local run without Redis (each api instance would count on its own).</summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    readonly Dictionary<string, List<long>> _logs = new(StringComparer.Ordinal);

    public Task<RateLimitDecision> AcquireAsync(string key, IReadOnlyList<RateLimitRule> rules, long nowMs, CancellationToken ct)
    {
        lock (_logs)
        {
            if (!_logs.TryGetValue(key, out var log)) _logs[key] = log = new List<long>();
            long maxWindowMs = rules.Max(r => r.WindowSeconds * 1000L);
            log.RemoveAll(t => t <= nowMs - maxWindowMs);
            long retry = 0;
            foreach (var r in rules)
            {
                long windowMs = r.WindowSeconds * 1000L;
                var inWindow = log.Where(t => t > nowMs - windowMs).OrderBy(t => t).ToList();
                if (inWindow.Count < r.Limit) continue;
                // The request fits once (count - limit + 1) entries have left the window.
                long wait = inWindow[inWindow.Count - r.Limit] + windowMs - nowMs;
                retry = Math.Max(retry, wait);
            }
            if (retry > 0) return Task.FromResult(new RateLimitDecision(false, retry));
            log.Add(nowMs);
            return Task.FromResult(RateLimitDecision.Allow);
        }
    }
}

/// <summary>
/// The distributed windows (specs/08 section 1.1, "sliding window in Redis"): one sorted set per key holding the
/// accepted requests' millisecond timestamps, checked and appended in one Lua script so concurrent api instances
/// cannot both take the last slot. The key expires after the longest window, so idle callers cost nothing.
/// </summary>
public sealed class RedisRateLimitStore : IRateLimitStore
{
    // KEYS[1] key; ARGV[1] now ms; ARGV[2] unique member; ARGV[3] longest window ms; then (limit, window ms) pairs.
    // Returns 0 when accepted (and recorded), else the milliseconds until the request would fit every window.
    internal const string Script = @"
local now = tonumber(ARGV[1])
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - tonumber(ARGV[3]))
local retry = 0
for i = 4, #ARGV, 2 do
  local limit = tonumber(ARGV[i])
  local win = tonumber(ARGV[i + 1])
  local from = '(' .. (now - win)
  local count = redis.call('ZCOUNT', KEYS[1], from, '+inf')
  if count >= limit then
    local e = redis.call('ZRANGEBYSCORE', KEYS[1], from, '+inf', 'WITHSCORES', 'LIMIT', count - limit, 1)
    local wait = tonumber(e[2]) + win - now
    if wait > retry then retry = wait end
  end
end
if retry > 0 then return retry end
redis.call('ZADD', KEYS[1], now, ARGV[2])
redis.call('PEXPIRE', KEYS[1], ARGV[3])
return 0
";

    readonly IRedisConnection _redis;

    public RedisRateLimitStore(IRedisConnection redis) => _redis = redis;

    public async Task<RateLimitDecision> AcquireAsync(string key, IReadOnlyList<RateLimitRule> rules, long nowMs, CancellationToken ct)
    {
        var db = await _redis.DatabaseAsync().ConfigureAwait(false);
        long maxWindowMs = rules.Max(r => r.WindowSeconds * 1000L);
        var args = new List<RedisValue>
        {
            nowMs,
            nowMs.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"),
            maxWindowMs,
        };
        foreach (var r in rules)
        {
            args.Add(r.Limit);
            args.Add(r.WindowSeconds * 1000L);
        }
        var result = await db.ScriptEvaluateAsync(Script, new RedisKey[] { key }, args.ToArray()).ConfigureAwait(false);
        long retry = (long)result;
        return retry <= 0 ? RateLimitDecision.Allow : new RateLimitDecision(false, retry);
    }
}

public static class RateLimitGate
{
    /// <summary>The endpoint's declared rules, or empty when it declares none.</summary>
    public static IReadOnlyList<RateLimitRule> RulesOf(Endpoint? endpoint) =>
        endpoint?.Metadata.GetOrderedMetadata<RateLimitAttribute>().Select(a => a.Rule).Distinct().ToList() ?? new List<RateLimitRule>();

    /// <summary>
    /// The window's key: per player on an authenticated route, per client IP on an <c>auth none</c> route
    /// (specs/08 section 1.1), even when a <c>none</c> route also carries a token. Behind the load balancer the client IP
    /// is the trusted hop's <c>X-Forwarded-For</c> entry (<see cref="ForwardedNetwork"/>, issue #263).
    /// </summary>
    public static string KeyFor(HttpContext ctx, AuthPolicy policy)
    {
        string route = RouteOf(ctx);
        if (policy != AuthPolicy.None && ctx.PlayerId() is Guid player) return RedisKeys.RateLimit(route, player);
        return RedisKeys.RateLimitByIp(route, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    public static string RouteOf(HttpContext ctx)
    {
        string template = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? ctx.Request.Path.Value ?? "";
        return RateLimitRoutes.Of(ctx.Request.Method, template);
    }
}
