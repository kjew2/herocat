using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Infrastructure.Redis;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Health;

/// <summary>The outcome of one dependency check in <c>GET /ready</c>.</summary>
public static class ReadyCheck
{
    public const string Ok = "ok";
    public const string Fail = "fail";
    public const string Timeout = "timeout";
    public const string Unconfigured = "unconfigured";
}

/// <param name="Status"><c>ready</c> when every check is <c>ok</c>, else <c>not_ready</c>.</param>
/// <param name="Checks">One entry per dependency (<c>postgres</c>, <c>redis</c>): a <see cref="ReadyCheck"/> value, never an error message.</param>
public sealed record ReadyBody(string Status, IReadOnlyDictionary<string, string> Checks)
{
    public const string Ready = "ready";
    public const string NotReady = "not_ready";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsReady => Status == Ready;
}

/// <summary>
/// The dependency checks behind <c>GET /ready</c> (specs/08 section 9.1, issue #319): Postgres answers <c>select 1</c>
/// and Redis answers <c>PING</c>, both at once, each within <see cref="CheckTimeout"/>. A result is reused for
/// <see cref="CacheFor"/> and concurrent callers share one probe, so the unauthenticated route cannot flood the
/// connection pool. A dependency that is not configured (a local run on in-memory stores) is not ready.
/// </summary>
public sealed class ReadinessProbe
{
    public static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultCacheFor = TimeSpan.FromSeconds(1);

    readonly IDbConnectionFactory _db;
    readonly IRedisConnection _redis;
    readonly ServerOptions _options;
    readonly TimeProvider _clock;
    readonly ILogger<ReadinessProbe> _log;
    readonly object _gate = new();
    Task<ReadyBody>? _last;
    long _lastStarted;

    public ReadinessProbe(IDbConnectionFactory db, IRedisConnection redis, IOptions<ServerOptions> options, TimeProvider clock, ILogger<ReadinessProbe> log)
    {
        _db = db;
        _redis = redis;
        _options = options.Value;
        _clock = clock;
        _log = log;
    }

    public TimeSpan CheckTimeout { get; init; } = DefaultCheckTimeout;
    public TimeSpan CacheFor { get; init; } = DefaultCacheFor;

    /// <summary>The current readiness: the running or recent probe if one started within <see cref="CacheFor"/>, else a new one.</summary>
    public Task<ReadyBody> CheckAsync()
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (_last != null && _clock.GetElapsedTime(_lastStarted, now) < CacheFor) return _last;
            _lastStarted = now;
            return _last = RunAsync();
        }
    }

    async Task<ReadyBody> RunAsync()
    {
        var pg = CheckAsync("postgres", _options.Database.ConnectionString, PingPostgresAsync);
        var redis = CheckAsync("redis", _options.Redis.Configuration, PingRedisAsync);
        var checks = new Dictionary<string, string> { ["postgres"] = await pg.ConfigureAwait(false), ["redis"] = await redis.ConfigureAwait(false) };
        return new ReadyBody(checks.Values.All(v => v == ReadyCheck.Ok) ? ReadyBody.Ready : ReadyBody.NotReady, checks);
    }

    async Task<string> CheckAsync(string name, string configured, Func<CancellationToken, Task> ping)
    {
        if (string.IsNullOrWhiteSpace(configured)) return ReadyCheck.Unconfigured;
        using var cts = new CancellationTokenSource(CheckTimeout, _clock);
        try
        {
            // WaitAsync bounds a check whose client ignores the token (a Redis connect in progress).
            await ping(cts.Token).WaitAsync(CheckTimeout, _clock).ConfigureAwait(false);
            return ReadyCheck.Ok;
        }
        catch (Exception e) when (e is TimeoutException || (e is OperationCanceledException && cts.IsCancellationRequested))
        {
            _log.LogWarning("readiness: {Dependency} did not answer within {TimeoutMs} ms", name, (int)CheckTimeout.TotalMilliseconds);
            return ReadyCheck.Timeout;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "readiness: {Dependency} check failed", name);
            return ReadyCheck.Fail;
        }
    }

    async Task PingPostgresAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("ready.postgres", "select 1", null);
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    async Task PingRedisAsync(CancellationToken ct)
    {
        var db = await _redis.DatabaseAsync().ConfigureAwait(false);
        await db.PingAsync().ConfigureAwait(false);
    }
}
