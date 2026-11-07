using System.Diagnostics;
using HeroCat.Server.Hosting;
using HeroCat.Server.Pipeline;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HeroCat.Server.Infrastructure.Redis;

/// <summary>A held lock; disposing releases it if this holder still owns it.</summary>
public interface ILockLease : IAsyncDisposable
{
    /// <summary>False when the lock service was unreachable and the caller proceeds unlocked (specs/08 section 9.1).</summary>
    bool Held { get; }
}

/// <summary>
/// A mutex with a TTL, keyed by name (specs/08 section 4.3 <c>summon:lock:{player}</c>). <see cref="TryAcquireAsync"/>
/// retries until <paramref name="maxWait"/> and returns null when someone else still holds the key.
/// </summary>
public interface IDistributedLock
{
    Task<ILockLease?> TryAcquireAsync(string key, TimeSpan ttl, TimeSpan maxWait, CancellationToken ct);
}

/// <summary>Lock metrics (specs/08 section 9.4), exported with the rest of <see cref="Observability.MetricCatalog"/>.</summary>
public static class LockMetrics
{
    public const string WaitSeconds = "redis_lock_wait_seconds";

    static readonly System.Diagnostics.Metrics.Histogram<double> Wait =
        ServerMetrics.Meter.CreateHistogram<double>(WaitSeconds, "s", "Time spent acquiring a Redis lock, by lock and outcome.");

    /// <param name="outcome"><c>acquired</c>, <c>timeout</c> or <c>unavailable</c>.</param>
    public static void RecordWait(string lockName, string outcome, TimeSpan waited) =>
        Wait.Record(waited.TotalSeconds, new KeyValuePair<string, object?>("lock", lockName), new KeyValuePair<string, object?>("outcome", outcome));
}

/// <summary>
/// <c>SET key token NX PX ttl</c>, polled every <see cref="PollInterval"/>; released by a compare-and-delete script so an
/// expired holder cannot free a successor's lock. If Redis is unreachable the lock fails open (logged): the idempotency
/// rows in Postgres still stop a double roll for one request id, and the handler's row locks serialize the rest.
/// </summary>
public sealed class RedisDistributedLock : IDistributedLock
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    internal const string ReleaseScript = @"
if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
return 0
";

    readonly IRedisConnection _redis;
    readonly TimeProvider _clock;
    readonly ILogger<RedisDistributedLock> _log;

    public RedisDistributedLock(IRedisConnection redis, TimeProvider clock, ILogger<RedisDistributedLock> log)
    {
        _redis = redis;
        _clock = clock;
        _log = log;
    }

    public async Task<ILockLease?> TryAcquireAsync(string key, TimeSpan ttl, TimeSpan maxWait, CancellationToken ct)
    {
        string token = Guid.NewGuid().ToString("N");
        string name = LockName(key);
        long start = Stopwatch.GetTimestamp();
        try
        {
            var db = await _redis.DatabaseAsync().ConfigureAwait(false);
            while (true)
            {
                if (await db.StringSetAsync(key, token, ttl, When.NotExists).ConfigureAwait(false))
                {
                    LockMetrics.RecordWait(name, "acquired", Stopwatch.GetElapsedTime(start));
                    return new Lease(db, key, token, _log);
                }
                if (Stopwatch.GetElapsedTime(start) + PollInterval > maxWait)
                {
                    LockMetrics.RecordWait(name, "timeout", Stopwatch.GetElapsedTime(start));
                    return null;
                }
                await Task.Delay(PollInterval, _clock, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            _log.LogWarning(e, "lock {Lock} unavailable; proceeding unlocked", name);
            LockMetrics.RecordWait(name, "unavailable", Stopwatch.GetElapsedTime(start));
            return UnheldLease.Instance;
        }
    }

    /// <summary>The key's prefix without its id (<c>summon:lock</c>), as the metric's <c>lock</c> tag.</summary>
    internal static string LockName(string key)
    {
        int i = key.LastIndexOf(':');
        return i > 0 ? key[..i] : key;
    }

    sealed class Lease : ILockLease
    {
        readonly IDatabase _db;
        readonly string _key;
        readonly string _token;
        readonly ILogger _log;

        public Lease(IDatabase db, string key, string token, ILogger log)
        {
            _db = db;
            _key = key;
            _token = token;
            _log = log;
        }

        public bool Held => true;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _db.ScriptEvaluateAsync(ReleaseScript, new RedisKey[] { _key }, new RedisValue[] { _token }).ConfigureAwait(false);
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                _log.LogWarning(e, "could not release lock {Key}; it expires on its TTL", _key);
            }
        }
    }
}

/// <summary>Per-process locks: tests and a local run without Redis.</summary>
public sealed class InMemoryDistributedLock : IDistributedLock
{
    readonly Dictionary<string, (string token, DateTimeOffset until)> _held = new(StringComparer.Ordinal);
    readonly TimeProvider _clock;

    public InMemoryDistributedLock(TimeProvider clock) => _clock = clock;

    public async Task<ILockLease?> TryAcquireAsync(string key, TimeSpan ttl, TimeSpan maxWait, CancellationToken ct)
    {
        string token = Guid.NewGuid().ToString("N");
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            lock (_held)
            {
                var now = _clock.GetUtcNow();
                if (!_held.TryGetValue(key, out var h) || h.until <= now)
                {
                    _held[key] = (token, now + ttl);
                    LockMetrics.RecordWait(RedisDistributedLock.LockName(key), "acquired", Stopwatch.GetElapsedTime(start));
                    return new Lease(this, key, token);
                }
            }
            if (Stopwatch.GetElapsedTime(start) + RedisDistributedLock.PollInterval > maxWait)
            {
                LockMetrics.RecordWait(RedisDistributedLock.LockName(key), "timeout", Stopwatch.GetElapsedTime(start));
                return null;
            }
            await Task.Delay(RedisDistributedLock.PollInterval, ct).ConfigureAwait(false);
        }
    }

    sealed class Lease : ILockLease
    {
        readonly InMemoryDistributedLock _owner;
        readonly string _key;
        readonly string _token;

        public Lease(InMemoryDistributedLock owner, string key, string token)
        {
            _owner = owner;
            _key = key;
            _token = token;
        }

        public bool Held => true;

        public ValueTask DisposeAsync()
        {
            lock (_owner._held)
            {
                if (_owner._held.TryGetValue(_key, out var h) && h.token == _token) _owner._held.Remove(_key);
            }
            return ValueTask.CompletedTask;
        }
    }
}

sealed class UnheldLease : ILockLease
{
    public static readonly UnheldLease Instance = new();
    public bool Held => false;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The summon handler's per-player mutex (specs/08 section 4.3: <c>summon:lock:{player}</c>, <c>SUMMON_LOCK_TTL_S</c>):
/// "at most one in flight per player". A second pull waits up to <see cref="MaxWait"/> and is then
/// <c>409 IDEMPOTENCY_IN_FLIGHT</c> with <c>retryAfterSec: 1</c>.
/// </summary>
public sealed class SummonLock
{
    /// <summary>How long a contended pull waits for the lock before the 409 (issue #194; the client's own retry is 1 s too).</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(1);

    readonly IDistributedLock _locks;
    readonly TimeSpan _ttl;

    public SummonLock(IDistributedLock locks, IOptions<ServerOptions> options)
    {
        _locks = locks;
        int s = options.Value.Limits.SummonLockTtlSeconds;
        _ttl = TimeSpan.FromSeconds(s > 0 ? s : RedisKeys.SummonLockTtl.TotalSeconds);
    }

    public async Task<ILockLease> AcquireAsync(Guid playerId, CancellationToken ct) =>
        await _locks.TryAcquireAsync(RedisKeys.SummonLock(playerId), _ttl, MaxWait, ct).ConfigureAwait(false)
        ?? throw ApiException.Of(ErrorCodes.IDEMPOTENCY_IN_FLIGHT, "Another summon for this player is in flight.", retryAfterSec: 1);
}
