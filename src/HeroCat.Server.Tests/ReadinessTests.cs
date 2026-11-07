using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using HeroCat.Server.Health;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Infrastructure.Redis;
using HeroCat.Server.Tests.Observability;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using StackExchange.Redis;

namespace HeroCat.Server.Tests;

/// <summary><c>GET /ready</c> (specs/08 section 9.1, issue #319): Postgres and Redis readiness, separate from <c>/health</c>.</summary>
[TestFixture]
public sealed class ReadinessTests
{
    static readonly ServerOptions Configured = new()
    {
        Database = { ConnectionString = "Host=fake" },
        Redis = { Configuration = "fake:6379" },
    };

    sealed class FakeDb : IDbConnectionFactory
    {
        public Func<CancellationToken, Task>? Before;
        public int Opens;

        public async ValueTask<DbConnection> OpenAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opens);
            if (Before != null) await Before(ct);
            return new DbQueryMetricsTests.FakeConnection();
        }
    }

    sealed class FakeRedis : IRedisConnection
    {
        public Func<Task>? Ping;

        public Task<IDatabase> DatabaseAsync()
        {
            var db = DispatchProxy.Create<IDatabase, RedisPingProxy>();
            ((RedisPingProxy)(object)db).Ping = Ping ?? (() => Task.CompletedTask);
            return Task.FromResult(db);
        }
    }

    /// <summary>An <see cref="IDatabase"/> that only answers <c>PingAsync</c>.</summary>
    public class RedisPingProxy : DispatchProxy
    {
        public Func<Task> Ping = () => Task.CompletedTask;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IDatabase.PingAsync)) throw new NotSupportedException(method?.Name);
            return Run();
            async Task<TimeSpan> Run()
            {
                await Ping();
                return TimeSpan.FromMilliseconds(1);
            }
        }
    }

    /// <summary>A clock whose timestamps move only when told to (the probe's cache window).</summary>
    sealed class StepClock : TimeProvider
    {
        long _ticks = 1_000_000;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => Stopwatch.Frequency;
        public void Advance(TimeSpan by) => _ticks += (long)(by.TotalSeconds * Stopwatch.Frequency);
    }

    static ReadinessProbe Probe(FakeDb db, FakeRedis redis, ServerOptions? options = null, TimeProvider? clock = null, TimeSpan? timeout = null) =>
        new(db, redis, Options.Create(options ?? Configured), clock ?? TimeProvider.System, NullLogger<ReadinessProbe>.Instance)
        {
            CheckTimeout = timeout ?? ReadinessProbe.DefaultCheckTimeout,
            CacheFor = TimeSpan.Zero,
        };

    [Test]
    public async Task Ready_when_postgres_and_redis_answer()
    {
        var body = await Probe(new FakeDb(), new FakeRedis()).CheckAsync();
        Assert.That(body.Status, Is.EqualTo(ReadyBody.Ready));
        Assert.That(body.Checks, Is.EqualTo(new Dictionary<string, string> { ["postgres"] = "ok", ["redis"] = "ok" }));
    }

    [Test]
    public async Task A_failing_dependency_is_not_ready_and_names_which()
    {
        var db = new FakeDb { Before = _ => throw new InvalidOperationException("connection refused") };
        var body = await Probe(db, new FakeRedis()).CheckAsync();
        Assert.That(body.Status, Is.EqualTo(ReadyBody.NotReady));
        Assert.That(body.Checks["postgres"], Is.EqualTo(ReadyCheck.Fail));
        Assert.That(body.Checks["redis"], Is.EqualTo(ReadyCheck.Ok));

        var redis = new FakeRedis { Ping = () => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down") };
        body = await Probe(new FakeDb(), redis).CheckAsync();
        Assert.That(body.Checks, Is.EqualTo(new Dictionary<string, string> { ["postgres"] = "ok", ["redis"] = "fail" }));
    }

    [Test]
    public async Task A_hanging_dependency_times_out_within_the_check_timeout()
    {
        var db = new FakeDb { Before = ct => Task.Delay(Timeout.Infinite, ct) };
        var redis = new FakeRedis { Ping = () => Task.Delay(Timeout.Infinite) };   // ignores cancellation
        var sw = Stopwatch.StartNew();
        var body = await Probe(db, redis, timeout: TimeSpan.FromMilliseconds(200)).CheckAsync();
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "both checks run at once and are bounded");
        Assert.That(body.Status, Is.EqualTo(ReadyBody.NotReady));
        Assert.That(body.Checks, Is.EqualTo(new Dictionary<string, string> { ["postgres"] = "timeout", ["redis"] = "timeout" }));
    }

    [Test]
    public async Task An_unconfigured_dependency_is_not_ready_and_is_not_touched()
    {
        var db = new FakeDb();
        var body = await Probe(db, new FakeRedis(), new ServerOptions()).CheckAsync();
        Assert.That(body.Checks, Is.EqualTo(new Dictionary<string, string> { ["postgres"] = "unconfigured", ["redis"] = "unconfigured" }));
        Assert.That(body.Status, Is.EqualTo(ReadyBody.NotReady));
        Assert.That(db.Opens, Is.Zero);
    }

    [Test]
    public async Task A_result_is_shared_within_the_cache_window()
    {
        var clock = new StepClock();
        var db = new FakeDb();
        var probe = new ReadinessProbe(db, new FakeRedis(), Options.Create(Configured), clock, NullLogger<ReadinessProbe>.Instance);
        Assert.That(probe.CacheFor, Is.EqualTo(ReadinessProbe.DefaultCacheFor));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => probe.CheckAsync()));
        Assert.That(db.Opens, Is.EqualTo(1), "concurrent callers share one probe");
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await probe.CheckAsync();
        Assert.That(db.Opens, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await probe.CheckAsync();
        Assert.That(db.Opens, Is.EqualTo(2), "a new probe once the window ends");
    }

    static WebApplicationFactory<Program> Api(Action<IServiceCollection>? services = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (services != null) b.ConfigureServices(services);
        });

    [Test]
    public async Task The_route_is_200_when_ready_and_503_otherwise_while_health_stays_200()
    {
        using (var up = Api(s => s.AddSingleton(Probe(new FakeDb(), new FakeRedis()))))
        using (var client = up.CreateClient())
        using (var r = await client.GetAsync("/ready"))
        {
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ready"));
            Assert.That(body.GetProperty("checks").GetProperty("postgres").GetString(), Is.EqualTo("ok"));
            Assert.That(body.GetProperty("checks").GetProperty("redis").GetString(), Is.EqualTo("ok"));
            Assert.That(body.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "status", "checks" }));
        }

        // The default test host configures neither dependency.
        using var down = Api();
        using var c2 = down.CreateClient();
        using (var r = await c2.GetAsync("/ready"))
        {
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("not_ready"));
            Assert.That(body.GetProperty("checks").GetProperty("postgres").GetString(), Is.EqualTo("unconfigured"));
        }
        using (var h = await c2.GetAsync("/health"))
            Assert.That(h.StatusCode, Is.EqualTo(HttpStatusCode.OK), "liveness never reads a dependency");
    }

    [Test]
    public void Ready_is_outside_the_client_gates()
    {
        Assert.That(HeroCat.Server.Pipeline.RouteRules.IsClientRoute("/ready"), Is.False);
        Assert.That(HeroCat.Server.Pipeline.RouteRules.BlockedByMaintenance("/ready"), Is.False);
    }

    /// <summary>Against the real Postgres and Redis of the server check mode, when both are named.</summary>
    [Test]
    [Category("Redis")]
    public async Task Ready_against_real_postgres_and_redis()
    {
        string pg = Environment.GetEnvironmentVariable(PostgresMigrationTests.EnvVar) ?? "";
        string redis = Environment.GetEnvironmentVariable(RedisPipelineTests.EnvVar) ?? "";
        if (string.IsNullOrWhiteSpace(pg) || string.IsNullOrWhiteSpace(redis)) Assert.Ignore("needs HEROCAT_TEST_POSTGRES and HEROCAT_TEST_REDIS");
        var options = Options.Create(new ServerOptions { Database = { ConnectionString = pg }, Redis = { Configuration = redis } });
        await using var db = new NpgsqlConnectionFactory(options);
        await using var r = new RedisConnection(options);
        var probe = new ReadinessProbe(db, r, options, TimeProvider.System, NullLogger<ReadinessProbe>.Instance);
        var body = await probe.CheckAsync();
        Assert.That(body.Checks, Is.EqualTo(new Dictionary<string, string> { ["postgres"] = "ok", ["redis"] = "ok" }));
        Assert.That(body.Status, Is.EqualTo(ReadyBody.Ready));
    }
}
