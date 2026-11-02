using System.Net;
using System.Text.Json.Nodes;
using HeroCat.Server.Features.Config;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Config;

/// <summary>
/// <c>config_versions</c> in PostgreSQL (specs/08 section 6.2; issue #204): publishing, the single-row activation flip,
/// a refused activation, and a kill switch reaching a second api instance. Runs only when HEROCAT_TEST_POSTGRES is set.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresConfigTests
{
    ScratchDatabase _db = null!;

    [OneTimeSetUp]
    public async Task CreateDatabase() => _db = await ScratchDatabase.CreateAsync("herocat_config");

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_db != null) await _db.DisposeAsync();
    }

    [Test]
    public async Task The_store_round_trips_payloads_and_keeps_one_active_row_per_env()
    {
        var store = new PostgresConfigStore(_db.Factory);
        string env = "store_" + Guid.NewGuid().ToString("N")[..8];
        var payload = ConfigHost.Payload(new FakeCdn());
        var cols = ConfigPayload.Normalise(payload);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_793_670_900);

        int a = await store.InsertAsync(env, payload, cols, "ops", now, default);
        int b = await store.InsertAsync(env, payload, cols, "ops", now, default);
        int other = await store.InsertAsync(env + "x", payload, cols, "ops", now, default);
        Assert.That(await store.GetActiveAsync(env, default), Is.Null);

        Assert.That(await store.ActivateAsync(env, a, default), Is.True);
        Assert.That(await store.ActivateAsync(env + "x", other, default), Is.True);
        var active = await store.GetActiveAsync(env, default);
        Assert.That(active!.Id, Is.EqualTo(a));
        Assert.That(active.CreatedBy, Is.EqualTo("ops"));
        Assert.That(active.Payload["content"]!["version"]!.GetValue<string>(), Is.EqualTo("2026.11.10"));
        Assert.That(JsonNode.DeepEquals(active.Payload, payload), "jsonb round trip (key order aside)");
        Assert.That(new ActiveConfig(active.Id, active.Payload).SummonEnabled("RUNE"), Is.False);

        // Concurrent activations of one env still leave exactly one row active.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => store.ActivateAsync(env, i % 2 == 0 ? a : b, default)));
        Assert.That(await _db.CountAsync("select count(*) from config_versions where env = @env and active", ("env", env)), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from config_versions where env = @env and active", ("env", env + "x")), Is.EqualTo(1), "envs are independent");

        Assert.That(await store.ActivateAsync(env, other, default), Is.False, "another env's row");
        Assert.That(await _db.CountAsync("select count(*) from config_versions where env = @env and active", ("env", env)), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select rollout_pct from config_versions where id = @id", ("id", a)), Is.EqualTo(100));
    }

    [Test]
    public async Task A_kill_switch_reaches_a_second_instance_and_a_bad_bundle_never_activates()
    {
        // Each test uses its own env so rows of other tests in this database never interfere.
        string env = "inst_" + Guid.NewGuid().ToString("N")[..8];
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
        var cdn = new FakeCdn();
        using var a = new ConfigHost(clock, cdn: cdn, connectionString: _db.ConnectionString, env: env);
        using var b = new ConfigHost(clock, cdn: cdn, connectionString: _db.ConnectionString, env: env);
        using var ca = a.Client();
        using var cb = b.Client();

        int first = await ConfigHost.PublishAndActivate(ca, ConfigHost.Payload(cdn, weaponSummon: true));
        using (var r = await cb.SendAsync(ConfigHost.GetConfig()))
            Assert.That((await ConfigHost.Body(r)).GetProperty("configVersion").GetInt32(), Is.EqualTo(first));

        int second = await ConfigHost.PublishAndActivate(ca, ConfigHost.Payload(cdn, weaponSummon: false));
        var sourceB = b.Services.GetRequiredService<IActiveConfigSource>();
        Assert.That((await sourceB.GetActiveAsync(default))!.Id, Is.EqualTo(first), "within the refresh interval");
        clock.Advance(b.Services.GetRequiredService<ActiveConfigCache>().RefreshInterval);
        var now = await sourceB.GetActiveAsync(default);
        Assert.That(now!.Id, Is.EqualTo(second));
        Assert.That(now.SummonEnabled("WEAPON"), Is.False);

        var bad = ConfigHost.Payload(cdn, version: "2026.12.01");
        int third = await ConfigHost.Publish(ca, bad);
        cdn.Put(FakeCdn.Url("2026.12.01"), "not the manifest"u8.ToArray());
        using (var r = await ca.SendAsync(ConfigHost.Admin($"/v1/admin/config/{third}/activate", new { })))
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await _db.CountAsync("select id from config_versions where env = @env and active", ("env", env)), Is.EqualTo(second));
    }
}
