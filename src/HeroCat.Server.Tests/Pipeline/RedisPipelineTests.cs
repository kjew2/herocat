using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Redis;
using HeroCat.Server.Pipeline;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Pipeline;

/// <summary>
/// The Redis-backed rate-limit store and maintenance flag against a real Redis, with the same expectations as the
/// in-memory ones. Runs only when HEROCAT_TEST_REDIS is a StackExchange.Redis configuration string (<c>localhost:6379</c>);
/// elsewhere it is ignored. Keys are unique per run and expire on their own; the <c>maint</c> test restores the key.
/// </summary>
[TestFixture]
[Category("Redis")]
[NonParallelizable]
public sealed class RedisPipelineTests
{
    public const string EnvVar = "HEROCAT_TEST_REDIS";

    RedisConnection _redis = null!;

    [OneTimeSetUp]
    public void Connect()
    {
        string config = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        if (string.IsNullOrWhiteSpace(config)) Assert.Ignore($"{EnvVar} not set: no Redis");
        _redis = new RedisConnection(Options.Create(new ServerOptions { Redis = { Configuration = config } }));
    }

    [OneTimeTearDown]
    public void Disconnect() => _redis?.Dispose();

    static string Key() => RedisKeys.RateLimit("test/" + Guid.NewGuid().ToString("N"), Guid.NewGuid());

    [Test]
    public async Task The_window_admits_its_limit_and_slides()
    {
        var store = new RedisRateLimitStore(_redis);
        var rules = new[] { new RateLimitRule(5, 60) };
        string key = Key();
        long t0 = 1_793_670_900_000;
        for (int i = 0; i < 5; i++) Assert.That((await store.AcquireAsync(key, rules, t0 + i * 10_000, default)).Allowed, $"request {i + 1}");
        var denied = await store.AcquireAsync(key, rules, t0 + 45_000, default);
        Assert.That(denied.Allowed, Is.False);
        Assert.That(denied.RetryAfterMs, Is.EqualTo(15_000));
        Assert.That((await store.AcquireAsync(key, rules, t0 + 59_999, default)).Allowed, Is.False, "denials are not recorded");
        Assert.That((await store.AcquireAsync(key, rules, t0 + 60_000, default)).Allowed);

        var db = await _redis.DatabaseAsync();
        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert.That(ttl, Is.Not.Null.And.LessThanOrEqualTo(TimeSpan.FromSeconds(60)), "the log expires after its longest window");
    }

    [Test]
    public async Task Several_windows_share_one_log_and_the_tightest_binds()
    {
        var store = new RedisRateLimitStore(_redis);
        var rules = new[] { new RateLimitRule(6, 60), new RateLimitRule(8, 86400) };
        string key = Key();
        long t = 1_793_670_900_000;
        for (int i = 0; i < 6; i++) Assert.That((await store.AcquireAsync(key, rules, t + i, default)).Allowed);
        var minute = await store.AcquireAsync(key, rules, t + 10, default);
        Assert.That(minute.Allowed, Is.False);
        Assert.That(minute.RetryAfterMs, Is.EqualTo(60_000 - 10));
        t += 60_000;
        for (int i = 0; i < 2; i++) Assert.That((await store.AcquireAsync(key, rules, t + i, default)).Allowed);
        var day = await store.AcquireAsync(key, rules, t + 10, default);
        Assert.That(day.Allowed, Is.False);
        Assert.That(day.RetryAfterMs, Is.EqualTo(86_400_000 - 60_010));
    }

    [Test]
    public async Task Concurrent_callers_never_exceed_the_limit()
    {
        var store = new RedisRateLimitStore(_redis);
        var rules = new[] { new RateLimitRule(20, 60) };
        string key = Key();
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => store.AcquireAsync(key, rules, 1_793_670_900_000, default)));
        Assert.That(results.Count(r => r.Allowed), Is.EqualTo(20));
    }

    [Test]
    public async Task The_maint_key_turns_maintenance_on_and_off()
    {
        var db = await _redis.DatabaseAsync();
        var before = await db.StringGetAsync(RedisKeys.Maintenance);
        try
        {
            var flag = new RedisMaintenanceFlag(_redis);
            await db.KeyDeleteAsync(RedisKeys.Maintenance);
            Assert.That(await flag.GetAsync(default), Is.Null);
            await db.StringSetAsync(RedisKeys.Maintenance, "{\"active\":true,\"messageKey\":\"maint.db_upgrade\",\"until\":1793680000}");
            Assert.That(await flag.GetAsync(default), Is.EqualTo(new MaintenanceState(true, "maint.db_upgrade", 1793680000)));
        }
        finally
        {
            if (before.HasValue) await db.StringSetAsync(RedisKeys.Maintenance, before);
            else await db.KeyDeleteAsync(RedisKeys.Maintenance);
        }
    }
}
