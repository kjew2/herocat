using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Pipeline;

[TestFixture]
public sealed class VersionTests
{
    static ClientVersion C(string s)
    {
        Assert.That(ClientVersion.TryParse(s, out var v), s);
        return v;
    }

    [TestCase("1.4.2+318", "1.4.2", 0)]
    [TestCase("1.4.10", "1.4.9", 1)]
    [TestCase("1.10.0", "1.9.9", 1)]
    [TestCase("2.0.0", "1.99.99", 1)]
    [TestCase("1.4.2-beta.1", "1.4.2", -1)]
    [TestCase("1.4.2-beta.2", "1.4.2-beta.10", -1)]
    [TestCase("1.4.2-alpha", "1.4.2-beta", -1)]
    [TestCase("1.4.2-1", "1.4.2-alpha", -1)]
    public void Client_versions_order_by_semver_precedence(string a, string b, int sign) =>
        Assert.That(Math.Sign(C(a).CompareTo(C(b))), Is.EqualTo(sign));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("1.4")]
    [TestCase("v1.4.2")]
    [TestCase("1.4.2.7")]
    public void Malformed_client_versions_do_not_parse(string? s) => Assert.That(ClientVersion.TryParse(s, out _), Is.False);

    [Test]
    public void Content_versions_order_by_date_then_n()
    {
        Assert.That(ContentVersion.TryParse("2026.11.03", out var a));
        Assert.That(ContentVersion.TryParse("2026.11.03.1", out var b));
        Assert.That(ContentVersion.TryParse("2026.10.20", out var c));
        Assert.That(a < b && c < a);
        Assert.That(ContentVersion.TryParse("2026.11.3", out _), Is.False);
    }

    [Test]
    public void The_content_floor_is_the_highest_listed_client_version_at_or_below()
    {
        var g = new ClientGateConfig("1.4.0", new Dictionary<string, string> { ["1.4.0"] = "2026.10.20", ["1.4.1"] = "2026.11.03", ["bad"] = "x" });
        Assert.That(g.MinContentVersionFor(C("1.3.9"))?.ToString(), Is.Null);
        Assert.That(g.MinContentVersionFor(C("1.4.0+1")), Is.EqualTo(new ContentVersion(2026, 10, 20, 0)));
        Assert.That(g.MinContentVersionFor(C("1.4.1")), Is.EqualTo(new ContentVersion(2026, 11, 3, 0)));
        Assert.That(g.MinContentVersionFor(C("1.9.0")), Is.EqualTo(new ContentVersion(2026, 11, 3, 0)));
    }
}

[TestFixture]
public sealed class RouteRulesTests
{
    [TestCase("/v1/config", false, false)]
    [TestCase("/v1/time", false, false)]
    [TestCase("/v1/auth/guest", false, true)]
    [TestCase("/v1/save/meta", true, true)]
    [TestCase("/v1/summon/pull", true, true)]
    [TestCase("/v1/admin/maintenance", false, false)]
    [TestCase("/health", false, false)]
    [TestCase("/v1/configuration", true, true)]
    public void Client_version_and_maintenance_coverage(string path, bool versionGated, bool maintenance)
    {
        Assert.That(RouteRules.ChecksClientVersion(path), Is.EqualTo(versionGated), "version");
        Assert.That(RouteRules.BlockedByMaintenance(path), Is.EqualTo(maintenance), "maintenance");
    }

    [TestCase("POST", "/v1/summon/pull", true)]
    [TestCase("POST", "/v1/dungeon/seed", true)]
    [TestCase("PUT", "/v1/save", true)]
    [TestCase("PUT", "/v1/save/", true)]
    [TestCase("GET", "/v1/save", false)]
    [TestCase("GET", "/v1/save/meta", false)]
    [TestCase("GET", "/v1/summon/state", false)]
    public void Content_version_coverage(string method, string path, bool gated) =>
        Assert.That(RouteRules.ChecksContentVersion(method, new PathString(path)), Is.EqualTo(gated));

    [Test]
    public void Rate_limit_routes_are_key_segments()
    {
        Assert.That(RateLimitRoutes.Of("PUT", "v1/save"), Is.EqualTo("put/save"));
        Assert.That(RateLimitRoutes.Of("GET", "v1/admin/players/{id}"), Is.EqualTo("get/admin/players/_"));
    }
}

[TestFixture]
public sealed class RateLimitStoreTests
{
    static readonly RateLimitRule[] FivePerMinute = { new(5, 60) };

    [Test]
    public async Task A_window_admits_its_limit_and_frees_slots_as_it_slides()
    {
        var store = new InMemoryRateLimitStore();
        for (int i = 0; i < 5; i++)
            Assert.That((await store.AcquireAsync("k", FivePerMinute, i * 10_000, default)).Allowed);
        var denied = await store.AcquireAsync("k", FivePerMinute, 45_000, default);
        Assert.That(denied.Allowed, Is.False);
        Assert.That(denied.RetryAfterMs, Is.EqualTo(15_000), "the t=0 entry leaves at t=60 s");
        Assert.That((await store.AcquireAsync("k", FivePerMinute, 59_999, default)).Allowed, Is.False);
        Assert.That((await store.AcquireAsync("k", FivePerMinute, 60_000, default)).Allowed, "exactly one window later");
        Assert.That((await store.AcquireAsync("other", FivePerMinute, 60_000, default)).Allowed, "keys are independent");
    }

    [Test]
    public async Task Denied_requests_are_not_counted()
    {
        var store = new InMemoryRateLimitStore();
        var one = new[] { new RateLimitRule(1, 60) };
        Assert.That((await store.AcquireAsync("k", one, 0, default)).Allowed);
        for (int t = 1_000; t < 60_000; t += 1_000) Assert.That((await store.AcquireAsync("k", one, t, default)).Allowed, Is.False);
        Assert.That((await store.AcquireAsync("k", one, 60_000, default)).Allowed);
    }

    [Test]
    public void Retry_after_rounds_up_to_whole_seconds_and_is_at_least_one()
    {
        Assert.That(new RateLimitDecision(false, 1).RetryAfterSec, Is.EqualTo(1));
        Assert.That(new RateLimitDecision(false, 1_001).RetryAfterSec, Is.EqualTo(2));
        Assert.That(new RateLimitDecision(false, 40_000).RetryAfterSec, Is.EqualTo(40));
    }
}

[TestFixture]
public sealed class MaintenanceFlagTests
{
    [Test]
    public void The_maint_value_is_on_unless_active_is_false()
    {
        Assert.That(RedisMaintenanceFlag.Parse(null), Is.Null);
        Assert.That(RedisMaintenanceFlag.Parse("{\"active\":false,\"messageKey\":\"m\"}"), Is.Null);
        Assert.That(RedisMaintenanceFlag.Parse("{\"active\":true,\"messageKey\":\"maint.db_upgrade\",\"until\":1793680000}"),
                    Is.EqualTo(new MaintenanceState(true, "maint.db_upgrade", 1793680000)));
        Assert.That(RedisMaintenanceFlag.Parse("{\"messageKey\":\"m\"}"), Is.EqualTo(new MaintenanceState(true, "m", null)));
        Assert.That(RedisMaintenanceFlag.Parse("not json"), Is.EqualTo(new MaintenanceState(true, null, null)), "fail closed on a botched write");
    }
}
