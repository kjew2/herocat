using System.Net;
using System.Text.Json;
using HeroCat.Server.Pipeline;
using NUnit.Framework;
using StackExchange.Redis;

namespace HeroCat.Server.Tests.Pipeline;

/// <summary>The shared request pipeline of specs/08 section 1.1 over stand-in routes (issue #193).</summary>
[TestFixture]
public sealed class PipelineTests
{
    PipelineHost _host = null!;
    HttpClient _client = null!;

    [SetUp]
    public void Start()
    {
        _host = new PipelineHost();
        _client = _host.Client();
    }

    [TearDown]
    public void Stop()
    {
        _client.Dispose();
        _host.Dispose();
    }

    static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    async Task<HttpResponseMessage> Send(HttpMethod m, string path, Guid? player = null, string? clientVersion = PipelineHost.ClientVersion,
                                         string? contentVersion = PipelineHost.ContentVersion, string? deviceId = PipelineHost.DeviceId,
                                         string? token = null, Action<HttpRequestMessage>? edit = null)
    {
        var req = PipelineHost.Request(m, path, player, clientVersion, contentVersion, deviceId, token);
        edit?.Invoke(req);
        return await _client.SendAsync(req);
    }

    static async Task AssertError(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        Assert.That(r.StatusCode, Is.EqualTo(status), await r.Content.ReadAsStringAsync());
        Assert.That(r.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
        var b = await Body(r);
        Assert.That(b.GetProperty("code").GetString(), Is.EqualTo(code));
        Assert.That(b.GetProperty("message").GetString(), Is.Not.Empty);
        Assert.That(b.GetProperty("requestId").GetString(), Is.EqualTo(r.Headers.GetValues(ApiHeaders.RequestId).Single()));
        Assert.That(b.TryGetProperty("retryAfterSec", out _), "retryAfterSec is always present (null when none)");
        Assert.That(b.TryGetProperty("details", out _), "details is always present (null when none)");
    }

    // ---- headers and the error body ----

    [Test]
    public async Task Every_response_echoes_the_request_id_and_carries_server_time()
    {
        using var ok = await Send(HttpMethod.Get, "/v1/time", edit: r => r.Headers.Add(ApiHeaders.RequestId, "0123456789abcdef0123456789abcdef"));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ok.Headers.GetValues(ApiHeaders.RequestId).Single(), Is.EqualTo("0123456789abcdef0123456789abcdef"));
        Assert.That(ok.Headers.GetValues(ApiHeaders.ServerTime).Single(), Is.EqualTo("1793670900"));

        using var missing = await _client.GetAsync("/v1/nope");
        await AssertError(missing, HttpStatusCode.NotFound, ErrorCodes.NOT_FOUND);
        Assert.That(missing.Headers.GetValues(ApiHeaders.ServerTime).Single(), Is.EqualTo("1793670900"));
        Assert.That(missing.Headers.GetValues(ApiHeaders.RequestId).Single(), Does.Match("^[0-9a-f]{32}$"), "a server-made id when none was sent");
    }

    [Test]
    public async Task An_unusable_request_id_is_replaced_not_echoed()
    {
        using var r = await Send(HttpMethod.Get, "/v1/time", edit: m => m.Headers.TryAddWithoutValidation(ApiHeaders.RequestId, new string('x', 65)));
        Assert.That(r.Headers.GetValues(ApiHeaders.RequestId).Single(), Does.Match("^[0-9a-f]{32}$"));
    }

    [Test]
    public async Task An_unhandled_exception_is_500_INTERNAL_with_the_error_body()
    {
        using var r = await Send(HttpMethod.Get, "/v1/_test/throw");
        await AssertError(r, HttpStatusCode.InternalServerError, ErrorCodes.INTERNAL);
        Assert.That(await r.Content.ReadAsStringAsync(), Does.Not.Contain("boom"), "no exception text leaks");
        Assert.That(r.Headers.Contains(ApiHeaders.ServerTime));
    }

    [Test]
    public async Task An_ApiException_is_its_status_code_and_details()
    {
        using var r = await Send(HttpMethod.Get, "/v1/_test/api-error");
        Assert.That((int)r.StatusCode, Is.EqualTo(402));
        var b = await Body(r);
        Assert.That(b.GetProperty("code").GetString(), Is.EqualTo("INSUFFICIENT_FUNDS"));
        Assert.That(b.GetProperty("message").GetString(), Is.EqualTo("Not enough diamonds."));
        Assert.That(b.GetProperty("details").GetProperty("need").GetInt32(), Is.EqualTo(1000));
        Assert.That(b.GetProperty("retryAfterSec").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task A_bare_status_gets_the_error_body()
    {
        using var r = await Send(HttpMethod.Get, "/v1/_test/bare-404");
        await AssertError(r, HttpStatusCode.NotFound, ErrorCodes.NOT_FOUND);
        using var wrongMethod = await Send(HttpMethod.Delete, "/v1/time");
        await AssertError(wrongMethod, HttpStatusCode.MethodNotAllowed, ErrorCodes.NOT_FOUND);
    }

    [Test]
    public async Task Model_validation_is_400_VALIDATION_with_the_failing_fields()
    {
        using var r = await Send(HttpMethod.Post, "/v1/auth/guest", token: "");
        await AssertError(r, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        var errors = (await Body(r)).GetProperty("details").GetProperty("errors");
        Assert.That(errors.EnumerateObject().Select(p => p.Name), Does.Contain("DeviceId"));
    }

    // ---- maintenance (specs/08 section 8.4, acceptance 18) ----

    [Test]
    public async Task Maintenance_is_503_on_summon_pull_and_200_on_config_and_time()   // 08 AC 18
    {
        _host.Maintenance.Set(new MaintenanceState(true, "maint.db_upgrade", 1_793_680_000));

        using var pull = await Send(HttpMethod.Post, "/v1/summon/pull");
        await AssertError(pull, HttpStatusCode.ServiceUnavailable, ErrorCodes.MAINTENANCE);
        var b = await Body(pull);
        Assert.That(b.GetProperty("details").GetProperty("messageKey").GetString(), Is.EqualTo("maint.db_upgrade"));
        Assert.That(b.GetProperty("details").GetProperty("until").GetInt64(), Is.EqualTo(1_793_680_000));
        Assert.That(b.GetProperty("retryAfterSec").GetInt32(), Is.EqualTo(1_793_680_000 - 1_793_670_900));
        Assert.That(pull.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(1_793_680_000 - 1_793_670_900)));

        using var config = await Send(HttpMethod.Get, "/v1/config");
        Assert.That(config.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var time = await Send(HttpMethod.Get, "/v1/time");
        Assert.That(time.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        foreach (var (m, path) in new[] { (HttpMethod.Get, "/v1/save/meta"), (HttpMethod.Put, "/v1/save"), (HttpMethod.Post, "/v1/auth/guest") })
        {
            using var r = await Send(m, path);
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), path);
        }

        _host.Maintenance.Set(null);
        using var after = await Send(HttpMethod.Post, "/v1/summon/pull");
        Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Maintenance_does_not_block_the_admin_api_or_health()
    {
        _host.Maintenance.Set(new MaintenanceState(true, null, null));
        using var admin = await Send(HttpMethod.Get, "/v1/admin/ping", edit: r => r.Headers.Add(FakeAdmins.Header, "ops@example"));
        Assert.That(admin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var pull = await Send(HttpMethod.Post, "/v1/summon/pull");
        Assert.That((await Body(pull)).GetProperty("retryAfterSec").ValueKind, Is.EqualTo(JsonValueKind.Null), "no until, no retry hint");
    }

    // ---- version gates ----

    [Test]
    public async Task An_old_client_gets_426_on_save_meta_and_200_on_config()   // issue #193 acceptance
    {
        _host.Gates.Config = new ClientGateConfig("1.4.0", new Dictionary<string, string>());
        using var meta = await Send(HttpMethod.Get, "/v1/save/meta", clientVersion: "1.3.9+300");
        await AssertError(meta, (HttpStatusCode)426, ErrorCodes.CLIENT_TOO_OLD);
        Assert.That((await Body(meta)).GetProperty("details").GetProperty("minClientVersion").GetString(), Is.EqualTo("1.4.0"));

        using var config = await Send(HttpMethod.Get, "/v1/config", clientVersion: "1.3.9+300");
        Assert.That(config.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var time = await Send(HttpMethod.Get, "/v1/time", clientVersion: "1.3.9+300");
        Assert.That(time.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var guest = await _client.SendAsync(Guest("1.3.9+300"));
        Assert.That(guest.StatusCode, Is.EqualTo(HttpStatusCode.Created), "/auth/* is exempt");

        using var current = await Send(HttpMethod.Get, "/v1/save/meta", clientVersion: "1.4.0+301");
        Assert.That(current.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    static HttpRequestMessage Guest(string clientVersion)
    {
        var r = PipelineHost.Request(HttpMethod.Post, "/v1/auth/guest", clientVersion: clientVersion, token: "");
        r.Content = new StringContent("{\"deviceId\":\"d\"}", System.Text.Encoding.UTF8, "application/json");
        return r;
    }

    [Test]
    public async Task A_missing_client_version_is_400_on_gated_routes_only()
    {
        using var meta = await Send(HttpMethod.Get, "/v1/save/meta", clientVersion: null);
        await AssertError(meta, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        using var config = await Send(HttpMethod.Get, "/v1/config", clientVersion: null);
        Assert.That(config.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Stale_content_gets_409_on_the_sim_sensitive_routes_only()
    {
        _host.Gates.Config = new ClientGateConfig("1.4.0", new Dictionary<string, string> { ["1.4.0"] = "2026.10.20", ["1.4.1"] = "2026.11.03" });
        // 1.4.2 is not listed: it inherits 1.4.1's floor.
        foreach (var (m, path) in new[] { (HttpMethod.Post, "/v1/summon/pull"), (HttpMethod.Post, "/v1/dungeon/seed"), (HttpMethod.Put, "/v1/save") })
        {
            using var r = await Send(m, path, contentVersion: "2026.11.02");
            await AssertError(r, HttpStatusCode.Conflict, ErrorCodes.CONTENT_TOO_OLD);
            Assert.That((await Body(r)).GetProperty("details").GetProperty("minContentVersion").GetString(), Is.EqualTo("2026.11.03"));
            using var ok = await Send(m, path, contentVersion: "2026.11.03.1");
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK), path);
        }
        using var meta = await Send(HttpMethod.Get, "/v1/save/meta", contentVersion: "2026.01.01");
        Assert.That(meta.StatusCode, Is.EqualTo(HttpStatusCode.OK), "GET /save/meta is not content-gated");
        using var older = await Send(HttpMethod.Post, "/v1/summon/pull", clientVersion: "1.4.0+300", contentVersion: "2026.10.20");
        Assert.That(older.StatusCode, Is.EqualTo(HttpStatusCode.OK), "1.4.0's own floor");
        using var none = await Send(HttpMethod.Post, "/v1/summon/pull", contentVersion: null);
        await AssertError(none, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
    }

    // ---- auth policies ----

    [Test]
    public async Task Jwt_routes_need_a_valid_token()
    {
        using var none = await Send(HttpMethod.Get, "/v1/save/meta", token: "");
        await AssertError(none, HttpStatusCode.Unauthorized, ErrorCodes.UNAUTHENTICATED);
        using var bad = await Send(HttpMethod.Get, "/v1/save/meta", token: "garbage");
        await AssertError(bad, HttpStatusCode.Unauthorized, ErrorCodes.UNAUTHENTICATED);
        using var expired = await Send(HttpMethod.Get, "/v1/save/meta", token: "expired");
        await AssertError(expired, HttpStatusCode.Unauthorized, ErrorCodes.TOKEN_EXPIRED);
        using var ok = await Send(HttpMethod.Get, "/v1/save/meta");
        Assert.That((await Body(ok)).GetProperty("player").GetString(), Is.EqualTo(PipelineHost.Player.ToString("D")));
    }

    [Test]
    public async Task An_endpoint_without_a_declared_policy_is_jwt()
    {
        using var r = await Send(HttpMethod.Get, "/v1/_test/undeclared", token: "");
        await AssertError(r, HttpStatusCode.Unauthorized, ErrorCodes.UNAUTHENTICATED);
    }

    [Test]
    public async Task Jwt_device_routes_need_the_tokens_device_in_X_Device_Id()
    {
        using var other = await Send(HttpMethod.Post, "/v1/summon/pull", deviceId: "another-device");
        await AssertError(other, HttpStatusCode.Forbidden, ErrorCodes.FORBIDDEN);
        using var missing = await Send(HttpMethod.Post, "/v1/summon/pull", deviceId: null);
        await AssertError(missing, HttpStatusCode.Forbidden, ErrorCodes.FORBIDDEN);
        using var ok = await Send(HttpMethod.Post, "/v1/summon/pull", deviceId: PipelineHost.DeviceId.ToUpperInvariant());
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task None_routes_need_no_token_but_read_a_valid_one()
    {
        using var anon = await Send(HttpMethod.Get, "/v1/config", token: "");
        Assert.That((await Body(anon)).GetProperty("player").ValueKind, Is.EqualTo(JsonValueKind.Null));
        using var bad = await Send(HttpMethod.Get, "/v1/config", token: "expired");
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var authed = await Send(HttpMethod.Get, "/v1/config");
        Assert.That((await Body(authed)).GetProperty("player").GetString(), Is.EqualTo(PipelineHost.Player.ToString("D")));
    }

    [Test]
    public async Task A_banned_player_gets_403_ACCOUNT_BANNED_with_the_appeal_url()   // 08 section 7.4
    {
        _host.Bans.Banned.Add(PipelineHost.Player);
        using var r = await Send(HttpMethod.Get, "/v1/save/meta");
        await AssertError(r, HttpStatusCode.Forbidden, ErrorCodes.ACCOUNT_BANNED);
        Assert.That((await Body(r)).GetProperty("details").GetProperty("appealUrl").GetString(), Is.EqualTo("https://support.example/appeal"));
        using var other = await Send(HttpMethod.Get, "/v1/save/meta", player: Guid.NewGuid());
        Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Admin_routes_need_the_admin_authenticator()
    {
        using var denied = await Send(HttpMethod.Get, "/v1/admin/ping");
        await AssertError(denied, HttpStatusCode.Forbidden, ErrorCodes.FORBIDDEN);
        using var ok = await Send(HttpMethod.Get, "/v1/admin/ping", clientVersion: null, edit: r => r.Headers.Add(FakeAdmins.Header, "ops@example"));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK), "no client headers needed");
        Assert.That((await Body(ok)).GetProperty("actor").GetString(), Is.EqualTo("ops@example"));
    }

    // ---- rate limits ----

    [Test]
    public async Task Exceeding_a_routes_limit_is_429_with_Retry_After_and_the_error_body()   // issue #193 acceptance
    {
        for (int i = 0; i < 20; i++)
        {
            using var ok = await Send(HttpMethod.Post, "/v1/summon/pull");
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"pull {i + 1} of 20/min");
            _host.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        using var limited = await Send(HttpMethod.Post, "/v1/summon/pull");
        await AssertError(limited, (HttpStatusCode)429, ErrorCodes.RATE_LIMITED);
        // The first pull was at t0 and it is now t0 + 20 s: the window frees its first slot at t0 + 60 s.
        Assert.That((await Body(limited)).GetProperty("retryAfterSec").GetInt32(), Is.EqualTo(40));
        Assert.That(limited.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(40)));

        using var otherPlayer = await Send(HttpMethod.Post, "/v1/summon/pull", player: Guid.NewGuid());
        Assert.That(otherPlayer.StatusCode, Is.EqualTo(HttpStatusCode.OK), "limits are per player");
        using var otherRoute = await Send(HttpMethod.Get, "/v1/save/meta");
        Assert.That(otherRoute.StatusCode, Is.EqualTo(HttpStatusCode.OK), "limits are per route");

        _host.Clock.Advance(TimeSpan.FromSeconds(40));
        using var again = await Send(HttpMethod.Post, "/v1/summon/pull");
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the window slid");
    }

    [Test]
    public async Task Every_declared_window_applies()
    {
        // PUT /save: 6/min and 400/day. Six per minute for 66 minutes fill the day window at 396; then 4 more.
        int accepted = 0;
        for (int minute = 0; minute < 70 && accepted < 400; minute++)
        {
            for (int i = 0; i < 6 && accepted < 400; i++)
            {
                using var r = await Send(HttpMethod.Put, "/v1/save");
                Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"upload {accepted + 1}");
                accepted++;
            }
            using var seventh = await Send(HttpMethod.Put, "/v1/save");
            Assert.That((int)seventh.StatusCode, Is.EqualTo(429), "7th in a minute");
            _host.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.That(accepted, Is.EqualTo(400));
        using var day = await Send(HttpMethod.Put, "/v1/save");
        await AssertError(day, (HttpStatusCode)429, ErrorCodes.RATE_LIMITED);
        int retry = (await Body(day)).GetProperty("retryAfterSec").GetInt32();
        Assert.That(retry, Is.GreaterThan(60), "the day window, not the minute window, is the one that binds");
    }

    [Test]
    public async Task Unauthenticated_routes_are_limited_per_ip()
    {
        for (int i = 0; i < 5; i++)
        {
            using var ok = await _client.SendAsync(Guest(PipelineHost.ClientVersion));
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        }
        using var limited = await _client.SendAsync(Guest(PipelineHost.ClientVersion));
        await AssertError(limited, (HttpStatusCode)429, ErrorCodes.RATE_LIMITED);
    }

    [Test]
    public async Task A_failing_rate_limit_store_fails_open()
    {
        using var host = new PipelineHost { LimitStoreOverride = new ThrowingStore() };
        using var client = host.Client();
        for (int i = 0; i < 25; i++)
        {
            using var r = await client.SendAsync(PipelineHost.Request(HttpMethod.Post, "/v1/summon/pull"));
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }

    sealed class ThrowingStore : IRateLimitStore
    {
        public Task<RateLimitDecision> AcquireAsync(string key, IReadOnlyList<RateLimitRule> rules, long nowMs, CancellationToken ct) =>
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
    }
}
