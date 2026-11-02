using System.Diagnostics.Metrics;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure;
using HeroCat.Server.Infrastructure.Redis;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Idempotency;

/// <summary>The per-player lock (specs/08 section 4.3 <c>summon:lock:{player}</c>, issue #194), shared expectations.</summary>
static class LockExpectations
{
    public static async Task ExclusiveUntilReleasedOrExpired(IDistributedLock locks, string key, Action<TimeSpan>? expire = null)
    {
        await using (var a = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.Zero, default))
        {
            Assert.That(a, Is.Not.Null);
            Assert.That(a!.Held);
            Assert.That(await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), default), Is.Null, "held elsewhere");
        }
        await using (var b = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.Zero, default))
            Assert.That(b, Is.Not.Null, "released on dispose");

        // A waiter gets the lock as soon as the holder releases it.
        var holder = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.Zero, default);
        var waiter = locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), default);
        await Task.Delay(100);
        await holder!.DisposeAsync();
        await using (var got = await waiter) Assert.That(got, Is.Not.Null);
    }
}

[TestFixture]
public sealed class LockTests
{
    [Test]
    public async Task The_in_memory_lock_is_exclusive_until_released_or_expired()
    {
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
        var locks = new InMemoryDistributedLock(clock);
        string key = RedisKeys.SummonLock(Guid.NewGuid());
        await LockExpectations.ExclusiveUntilReleasedOrExpired(locks, key);

        var held = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.Zero, default);
        clock.Advance(TimeSpan.FromSeconds(11));
        var next = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.Zero, default);
        Assert.That(next, Is.Not.Null, "the TTL frees a lock whose holder died");
        await held!.DisposeAsync();
        Assert.That(await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.Zero, default), Is.Null, "an expired holder cannot free its successor");
    }

    [Test]
    public async Task The_summon_lock_is_409_IDEMPOTENCY_IN_FLIGHT_when_contended_and_records_its_wait()
    {
        var waits = new List<(double seconds, string outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == ServerMetrics.MeterName && i.Name == LockMetrics.WaitSeconds) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<double>((_, v, tags, _) =>
        {
            foreach (var t in tags) if (t.Key == "outcome") lock (waits) waits.Add((v, (string)t.Value!));
        });
        listener.Start();

        var clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
        var summon = new SummonLock(new InMemoryDistributedLock(clock), Options.Create(new ServerOptions { Limits = { SummonLockTtlSeconds = 10 } }));
        var player = Guid.NewGuid();
        await using (await summon.AcquireAsync(player, default))
        {
            var e = Assert.ThrowsAsync<ApiException>(async () => await summon.AcquireAsync(player, default));
            Assert.That(e!.Status, Is.EqualTo(409));
            Assert.That(e.Code, Is.EqualTo(ErrorCodes.IDEMPOTENCY_IN_FLIGHT));
            Assert.That(e.RetryAfterSec, Is.EqualTo(1));
            await using (await summon.AcquireAsync(Guid.NewGuid(), default)) { } // other players are not blocked
        }
        await using (await summon.AcquireAsync(player, default)) { }

        listener.RecordObservableInstruments();
        lock (waits)
        {
            Assert.That(waits.Count(w => w.outcome == "acquired"), Is.GreaterThanOrEqualTo(3));
            Assert.That(waits.Single(w => w.outcome == "timeout").seconds, Is.GreaterThanOrEqualTo(SummonLock.MaxWait.TotalSeconds - 0.1));
        }
    }

    [Test]
    public async Task An_unreachable_redis_fails_open()
    {
        using var down = new RedisConnection(Options.Create(new ServerOptions { Redis = { Configuration = "127.0.0.1:1,connectTimeout=200,abortConnect=true" } }));
        var locks = new RedisDistributedLock(down, TimeProvider.System, NullLogger<RedisDistributedLock>.Instance);
        await using var lease = await locks.TryAcquireAsync(RedisKeys.SummonLock(Guid.NewGuid()), TimeSpan.FromSeconds(10), TimeSpan.Zero, default);
        Assert.That(lease, Is.Not.Null);
        Assert.That(lease!.Held, Is.False, "proceeds unlocked; Postgres idempotency still guards the request (specs/08 section 9.1)");
    }

    [Test]
    public void The_metric_lock_tag_is_the_key_prefix() =>
        Assert.That(RedisDistributedLock.LockName(RedisKeys.SummonLock(Guid.NewGuid())), Is.EqualTo("summon:lock"));
}

/// <summary>The Redis lock against a real Redis (HEROCAT_TEST_REDIS), like <see cref="RedisPipelineTests"/>.</summary>
[TestFixture]
[Category("Redis")]
[NonParallelizable]
public sealed class RedisLockTests
{
    RedisConnection _redis = null!;
    RedisDistributedLock _locks = null!;

    [OneTimeSetUp]
    public void Connect()
    {
        string config = Environment.GetEnvironmentVariable(RedisPipelineTests.EnvVar) ?? "";
        if (string.IsNullOrWhiteSpace(config)) Assert.Ignore($"{RedisPipelineTests.EnvVar} not set: no Redis");
        _redis = new RedisConnection(Options.Create(new ServerOptions { Redis = { Configuration = config } }));
        _locks = new RedisDistributedLock(_redis, TimeProvider.System, NullLogger<RedisDistributedLock>.Instance);
    }

    [OneTimeTearDown]
    public void Disconnect() => _redis?.Dispose();

    [Test]
    public async Task The_redis_lock_is_exclusive_until_released() =>
        await LockExpectations.ExclusiveUntilReleasedOrExpired(_locks, RedisKeys.SummonLock(Guid.NewGuid()));

    [Test]
    public async Task The_key_carries_the_ttl_and_an_expired_holder_cannot_free_its_successor()
    {
        string key = RedisKeys.SummonLock(Guid.NewGuid());
        var db = await _redis.DatabaseAsync();
        var first = await _locks.TryAcquireAsync(key, TimeSpan.FromMilliseconds(300), TimeSpan.Zero, default);
        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert.That(ttl, Is.Not.Null.And.LessThanOrEqualTo(TimeSpan.FromMilliseconds(300)));
        await Task.Delay(400);
        await using var second = await _locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.Zero, default);
        Assert.That(second, Is.Not.Null, "expired");
        await first!.DisposeAsync();
        Assert.That(await db.KeyExistsAsync(key), "the stale holder's release left the successor's lock alone");
    }

    [Test]
    public async Task Concurrent_acquirers_get_one_lock()
    {
        string key = RedisKeys.SummonLock(Guid.NewGuid());
        var leases = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _locks.TryAcquireAsync(key, TimeSpan.FromSeconds(10), TimeSpan.Zero, default)));
        Assert.That(leases.Count(l => l != null), Is.EqualTo(1));
        foreach (var l in leases) if (l != null) await l.DisposeAsync();
    }
}
