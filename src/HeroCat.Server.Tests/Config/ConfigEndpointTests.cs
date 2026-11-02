using System.Net;
using System.Text.Json;
using HeroCat.Server.Features.Config;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Config;

/// <summary>
/// <c>GET /config</c> and config publishing end to end over the in-memory store (specs/08 section 3.4, 6, 8.4;
/// acceptance 16, 17 and 18's config half; issue #204).
/// </summary>
[TestFixture]
public sealed class ConfigEndpointTests
{
    ConfigHost _host = null!;
    HttpClient _client = null!;

    [SetUp]
    public void Start()
    {
        _host = new ConfigHost();
        _client = _host.Client();
    }

    [TearDown]
    public void Stop()
    {
        _client.Dispose();
        _host.Dispose();
    }

    static async Task AssertError(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        Assert.That(r.StatusCode, Is.EqualTo(status), await r.Content.ReadAsStringAsync());
        Assert.That((await ConfigHost.Body(r)).GetProperty("code").GetString(), Is.EqualTo(code));
    }

    [Test]
    public async Task Without_an_active_config_GET_config_is_404()
    {
        using var r = await _client.SendAsync(ConfigHost.GetConfig());
        await AssertError(r, HttpStatusCode.NotFound, ErrorCodes.NOT_FOUND);
    }

    [Test]
    public async Task The_active_config_is_served_with_server_fields_and_cache_control()
    {
        int id = await ConfigHost.PublishAndActivate(_client, ConfigHost.Payload(_host.Cdn));
        using var r = await _client.SendAsync(ConfigHost.GetConfig());
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), await r.Content.ReadAsStringAsync());
        Assert.That(r.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromSeconds(60)));
        var b = await ConfigHost.Body(r);
        Assert.That(b.GetProperty("configVersion").GetInt32(), Is.EqualTo(id));
        Assert.That(b.GetProperty("serverTime").GetInt64(), Is.EqualTo(_host.Clock.GetUtcNow().ToUnixTimeSeconds()));
        string[] keys =
        {
            "configVersion", "serverTime", "minClientVersion", "latestClientVersion", "storeUrls", "content", "minContentVersionByClient",
            "killSwitches", "maintenance", "banners", "events", "limits", "features",
        };
        Assert.That(b.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(keys), "the specs/08 section 6.1 body; defaultContent is never served");
        Assert.That(b.GetProperty("content").GetProperty("version").GetString(), Is.EqualTo("2026.11.10"));
        Assert.That(b.GetProperty("content").TryGetProperty("inBucket", out _), Is.False, "no JWT: inBucket omitted");
    }

    [Test]
    public async Task Without_a_jwt_a_partial_rollout_serves_the_default_content_and_with_one_the_bucket_decides()
    {
        await ConfigHost.PublishAndActivate(_client, ConfigHost.Payload(_host.Cdn, rolloutPct: 25));

        using (var anon = await _client.SendAsync(ConfigHost.GetConfig()))
        {
            var c = (await ConfigHost.Body(anon)).GetProperty("content");
            Assert.That(c.GetProperty("version").GetString(), Is.EqualTo("2026.11.03"), "the non-rollout default");
            Assert.That(c.TryGetProperty("inBucket", out _), Is.False);
        }

        // An invalid token is ignored on a none route: still the default.
        var bad = ConfigHost.GetConfig();
        bad.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "garbage");
        using (var r = await _client.SendAsync(bad))
            Assert.That((await ConfigHost.Body(r)).GetProperty("content").TryGetProperty("inBucket", out _), Is.False);

        for (int i = 0; i < 12; i++)
        {
            var player = Guid.NewGuid();
            bool expected = RolloutBucket.InBucket(player, "2026.11.10", 25);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                using var r = await _client.SendAsync(ConfigHost.GetConfig(player));
                var c = (await ConfigHost.Body(r)).GetProperty("content");
                Assert.That(c.GetProperty("inBucket").GetBoolean(), Is.EqualTo(expected), "same player, same bucket");
                Assert.That(c.GetProperty("version").GetString(), Is.EqualTo(expected ? "2026.11.10" : "2026.11.03"));
            }
        }
    }

    [Test]
    public async Task Publishing_stores_an_inactive_row_with_the_actor_and_activation_flips_it()
    {
        var store = _host.Services.GetRequiredService<IConfigStore>();
        int first = await ConfigHost.PublishAndActivate(_client, ConfigHost.Payload(_host.Cdn));
        int second = await ConfigHost.Publish(_client, ConfigHost.Payload(_host.Cdn, version: "2026.11.17"));

        var row = await store.GetAsync("dev", second, CancellationToken.None);
        Assert.That(row!.Active, Is.False);
        Assert.That(row.CreatedBy, Is.EqualTo("ops@herocat.example"));
        Assert.That((await store.GetActiveAsync("dev", CancellationToken.None))!.Id, Is.EqualTo(first));

        using (var a = await _client.SendAsync(ConfigHost.Admin($"/v1/admin/config/{second}/activate", new { })))
        {
            Assert.That(a.StatusCode, Is.EqualTo(HttpStatusCode.OK), await a.Content.ReadAsStringAsync());
            var b = await ConfigHost.Body(a);
            Assert.That(b.GetProperty("active").GetBoolean());
            Assert.That(b.GetProperty("contentVersion").GetString(), Is.EqualTo("2026.11.17"));
        }
        using (var r = await _client.SendAsync(ConfigHost.GetConfig()))
            Assert.That((await ConfigHost.Body(r)).GetProperty("configVersion").GetInt32(), Is.EqualTo(second), "this instance sees its own activation at once");

        // Rollback is activating the previous id.
        await (await _client.SendAsync(ConfigHost.Admin($"/v1/admin/config/{first}/activate", new { }))).Content.ReadAsStringAsync();
        Assert.That((await store.GetActiveAsync("dev", CancellationToken.None))!.Id, Is.EqualTo(first));
        Assert.That((await store.GetAsync("dev", second, CancellationToken.None))!.Active, Is.False, "one active row");
    }

    [Test]
    public async Task A_bundle_hash_mismatch_is_refused_and_the_previous_config_stays_active()
    {
        int first = await ConfigHost.PublishAndActivate(_client, ConfigHost.Payload(_host.Cdn));
        var payload = ConfigHost.Payload(_host.Cdn, version: "2026.11.17");
        int second = await ConfigHost.Publish(_client, payload);
        _host.Cdn.Put(FakeCdn.Url("2026.11.17"), "tampered"u8.ToArray());

        using (var r = await _client.SendAsync(ConfigHost.Admin($"/v1/admin/config/{second}/activate", new { })))
        {
            await AssertError(r, HttpStatusCode.BadRequest, ConfigErrors.CONTENT_HASH_MISMATCH);
            var d = (await ConfigHost.Body(r)).GetProperty("details");
            Assert.That(d.GetProperty("field").GetString(), Is.EqualTo("content"));
            Assert.That(d.GetProperty("actual").GetString(), Is.Not.EqualTo(d.GetProperty("expected").GetString()));
        }
        using (var g = await _client.SendAsync(ConfigHost.GetConfig()))
            Assert.That((await ConfigHost.Body(g)).GetProperty("configVersion").GetInt32(), Is.EqualTo(first));

        // A manifest that is not on the CDN at all is refused the same way, also for the default content.
        int third = await ConfigHost.Publish(_client, ConfigHost.Payload(_host.Cdn, version: "2026.11.24", rolloutPct: 5, defaultVersion: "2026.11.04"));
        _host.Cdn.Remove(FakeCdn.Url("2026.11.04"));
        using (var r = await _client.SendAsync(ConfigHost.Admin($"/v1/admin/config/{third}/activate", new { })))
        {
            await AssertError(r, HttpStatusCode.BadRequest, ConfigErrors.CONTENT_HASH_MISMATCH);
            var d = (await ConfigHost.Body(r)).GetProperty("details");
            Assert.That(d.GetProperty("field").GetString(), Is.EqualTo("defaultContent"));
            Assert.That(d.GetProperty("actual").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
        Assert.That((await _host.Services.GetRequiredService<IConfigStore>().GetActiveAsync("dev", CancellationToken.None))!.Id, Is.EqualTo(first));
    }

    [Test]
    public async Task Publishing_validates_the_payload()
    {
        var noDefault = ConfigHost.Payload(_host.Cdn, rolloutPct: 25, defaultVersion: null);
        using (var r = await _client.SendAsync(ConfigHost.Admin("/v1/admin/config", noDefault.ToJsonString())))
        {
            await AssertError(r, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
            Assert.That((await ConfigHost.Body(r)).GetProperty("details").GetProperty("field").GetString(), Is.EqualTo("defaultContent"));
        }
        var badSha = ConfigHost.Payload(_host.Cdn);
        badSha["content"]!["sha256"] = "ab12";
        using (var r = await _client.SendAsync(ConfigHost.Admin("/v1/admin/config", badSha.ToJsonString())))
            await AssertError(r, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        using (var r = await _client.SendAsync(ConfigHost.Admin("/v1/admin/config", "[1]")))
            await AssertError(r, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        using (var r = await _client.SendAsync(ConfigHost.Admin("/v1/admin/config/999/activate", new { })))
            await AssertError(r, HttpStatusCode.NotFound, ErrorCodes.NOT_FOUND);
    }

    [Test]
    public async Task The_admin_routes_need_an_admin()
    {
        var r = ConfigHost.Admin("/v1/admin/config", ConfigHost.Payload(_host.Cdn).ToJsonString());
        r.Headers.Remove(FakeAdmins.Header);
        using var resp = await _client.SendAsync(r);
        await AssertError(resp, HttpStatusCode.Forbidden, ErrorCodes.FORBIDDEN);
    }

    [Test]
    public async Task Maintenance_is_set_and_cleared_by_the_admin_route_and_config_still_answers()
    {
        await ConfigHost.PublishAndActivate(_client, ConfigHost.Payload(_host.Cdn));
        using (var set = await _client.SendAsync(ConfigHost.Admin("/v1/admin/maintenance", new { active = true, messageKey = "maint.db_upgrade", until = 1_793_680_000 })))
            Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK), await set.Content.ReadAsStringAsync());
        Assert.That(await _host.Maintenance.GetAsync(CancellationToken.None), Is.EqualTo(new MaintenanceState(true, "maint.db_upgrade", 1_793_680_000)));

        using (var g = await _client.SendAsync(ConfigHost.GetConfig()))
        {
            Assert.That(g.StatusCode, Is.EqualTo(HttpStatusCode.OK), "acceptance 18: /config answers during maintenance");
            var m = (await ConfigHost.Body(g)).GetProperty("maintenance");
            Assert.That(m.GetProperty("active").GetBoolean());
            Assert.That(m.GetProperty("messageKey").GetString(), Is.EqualTo("maint.db_upgrade"));
            Assert.That(m.GetProperty("until").GetInt64(), Is.EqualTo(1_793_680_000));
        }

        using (var clear = await _client.SendAsync(ConfigHost.Admin("/v1/admin/maintenance", new { active = false })))
            Assert.That(clear.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await _host.Maintenance.GetAsync(CancellationToken.None), Is.Null);
        using (var g = await _client.SendAsync(ConfigHost.GetConfig()))
            Assert.That((await ConfigHost.Body(g)).GetProperty("maintenance").GetProperty("active").GetBoolean(), Is.False);
    }

    [Test]
    public async Task The_active_config_gates_client_versions()
    {
        var payload = ConfigHost.Payload(_host.Cdn);
        payload["minClientVersion"] = "1.5.0";
        await ConfigHost.PublishAndActivate(_client, payload);
        var r = new HttpRequestMessage(HttpMethod.Post, "/v1/devices");
        r.Headers.Add(ApiHeaders.ClientVersion, "1.4.2");
        using var resp = await _client.SendAsync(r);
        await AssertError(resp, (HttpStatusCode)426, ErrorCodes.CLIENT_TOO_OLD);
    }

    [Test]
    public async Task A_kill_switch_reaches_another_instance_within_the_refresh_interval()
    {
        // Acceptance 17, config half: two api instances over one store; the second learns of the activation by refreshing.
        var store = new InMemoryConfigStore();
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
        var cdn = new FakeCdn();
        using var a = new ConfigHost(clock, store, cdn);
        using var b = new ConfigHost(clock, store, cdn);
        using var ca = a.Client();
        await ConfigHost.PublishAndActivate(ca, ConfigHost.Payload(cdn, weaponSummon: true));

        var sourceB = b.Services.GetRequiredService<IActiveConfigSource>();
        Assert.That((await sourceB.GetActiveAsync(CancellationToken.None))!.SummonEnabled("WEAPON"), Is.True);

        await ConfigHost.PublishAndActivate(ca, ConfigHost.Payload(cdn, weaponSummon: false));
        Assert.That((await a.Services.GetRequiredService<IActiveConfigSource>().GetActiveAsync(CancellationToken.None))!.SummonEnabled("WEAPON"), Is.False);
        Assert.That((await sourceB.GetActiveAsync(CancellationToken.None))!.SummonEnabled("WEAPON"), Is.True, "B still serves its copy");

        var refresh = b.Services.GetRequiredService<ActiveConfigCache>().RefreshInterval;
        Assert.That(refresh, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(60)));
        clock.Advance(refresh);
        Assert.That((await sourceB.GetActiveAsync(CancellationToken.None))!.SummonEnabled("WEAPON"), Is.False);
    }
}
