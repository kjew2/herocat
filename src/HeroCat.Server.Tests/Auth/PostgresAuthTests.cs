using System.Net;
using System.Text;
using System.Text.Json;
using HeroCat.Server.Features.Auth;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Idempotency;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Auth;

/// <summary>
/// The Auth endpoints end to end (specs/08 section 2.2, acceptance 19; issue #195): the real api with its pipeline, a
/// scratch Postgres with schema v1, generated keys, a fake clock and in-memory rate limits. Runs only when
/// HEROCAT_TEST_POSTGRES is set.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresAuthTests
{
    ScratchDatabase _db = null!;
    FakeClock _clock = null!;
    InMemoryRateLimitStore _limits = null!;
    CapturingLoggerProvider _logs = null!;
    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;
    readonly ServerKeyRing _keys = ServerKeyRing.Ephemeral();

    [OneTimeSetUp]
    public async Task CreateDatabase() => _db = await ScratchDatabase.CreateAsync("herocat_auth");

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_db != null) await _db.DisposeAsync();
    }

    [SetUp]
    public void StartApi()
    {
        _clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
        _limits = new InMemoryRateLimitStore();
        _logs = new CapturingLoggerProvider();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("HeroCat:Database:ConnectionString", _db.ConnectionString);
            b.ConfigureLogging(l => l.AddProvider(_logs).AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(_clock);
                s.AddSingleton(new Lazy<ServerKeyRing>(_keys));
                s.AddSingleton<IRateLimitStore>(_limits);
                s.AddSingleton<IMaintenanceFlag>(new InMemoryMaintenanceFlag());
            });
        });
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void StopApi()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    static string NewDevice() => Guid.NewGuid().ToString("D");
    static string NewKey() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    static string NewId() => Guid.NewGuid().ToString("N");

    static HttpRequestMessage Json(HttpMethod m, string path, object? body, string? device, string? requestId = null, string? token = null)
    {
        var r = new HttpRequestMessage(m, path);
        r.Headers.Add(ApiHeaders.ClientVersion, PipelineHost.ClientVersion);
        r.Headers.Add(ApiHeaders.ContentVersion, PipelineHost.ContentVersion);
        if (device != null) r.Headers.Add(ApiHeaders.DeviceId, device);
        if (requestId != null) r.Headers.Add(ApiHeaders.RequestId, requestId);
        if (token != null) r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body != null) r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return r;
    }

    static HttpRequestMessage GuestRequest(string device, string key, string? requestId = null, string? bodyDevice = null) =>
        Json(HttpMethod.Post, "/v1/auth/guest",
             new { deviceId = bodyDevice ?? device, deviceKey = key, model = "Pixel 7", os = "Android 14", clientVersion = "1.4.2+318", region = "us" },
             device, requestId ?? NewId());

    static async Task<JsonElement> Body(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    static async Task AssertError(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        string text = await r.Content.ReadAsStringAsync();
        Assert.That(r.StatusCode, Is.EqualTo(status), text);
        Assert.That(JsonDocument.Parse(text).RootElement.GetProperty("code").GetString(), Is.EqualTo(code), text);
    }

    sealed record Session(Guid PlayerId, string Device, string Key, string Access, string Refresh, JsonElement Body);

    async Task<Session> Guest(string? device = null)
    {
        device ??= NewDevice();
        string key = NewKey();
        using var r = await _client.SendAsync(GuestRequest(device, key));
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Created), await r.Content.ReadAsStringAsync());
        var b = await Body(r);
        return new Session(b.GetProperty("playerId").GetGuid(), device, key, b.GetProperty("accessToken").GetString()!,
                           b.GetProperty("refreshToken").GetString()!, b);
    }

    /// <summary>The session with tokens valid at the current (fake) time, through /auth/refresh.</summary>
    async Task<Session> Renew(Session s)
    {
        using var r = await Refresh(s.Refresh, s.Device);
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), await r.Content.ReadAsStringAsync());
        var b = await Body(r);
        return s with { Access = b.GetProperty("accessToken").GetString()!, Refresh = b.GetProperty("refreshToken").GetString()! };
    }

    async Task<HttpResponseMessage> Refresh(string token, string device) =>
        await _client.SendAsync(Json(HttpMethod.Post, "/v1/auth/refresh", new { refreshToken = token, deviceId = device }, device));

    // ---- /auth/guest ----

    [Test]
    public async Task Guest_creates_the_account_device_and_token_family_and_returns_the_spec_shape()
    {
        var s = await Guest();
        var b = s.Body;
        Assert.That(b.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "playerId", "accessToken", "refreshToken", "accessExpiresAt", "profile", "serverTime" }));
        Assert.That(b.GetProperty("serverTime").GetInt64(), Is.EqualTo(1_793_670_900));
        Assert.That(b.GetProperty("accessExpiresAt").GetInt64(), Is.EqualTo(1_793_670_900 + 3600));

        var profile = b.GetProperty("profile");
        Assert.That(profile.GetProperty("playerId").GetGuid(), Is.EqualTo(s.PlayerId));
        Assert.That(profile.GetProperty("displayName").GetString(), Is.EqualTo(AuthService.DefaultDisplayName(s.PlayerId)));
        Assert.That(profile.GetProperty("avatarId").GetString(), Is.EqualTo("avatar_default"));
        Assert.That(profile.GetProperty("isGuest").GetBoolean(), Is.True);
        Assert.That(profile.GetProperty("links").GetArrayLength(), Is.Zero);
        Assert.That(profile.GetProperty("region").GetString(), Is.EqualTo("US"));
        Assert.That(profile.GetProperty("ageGate").GetInt32(), Is.Zero);
        Assert.That(profile.GetProperty("supportCode").GetString(), Is.EqualTo(SupportCodes.For(s.PlayerId, _keys.RecoveryKey)));

        var claims = new AccessTokenService(new Lazy<ServerKeyRing>(_keys), _clock, Microsoft.Extensions.Options.Options.Create(new Hosting.ServerOptions())).Validate(s.Access);
        Assert.That(claims.Claims, Is.EqualTo(new AccessTokenClaims(s.PlayerId, s.Device, 0)));

        Assert.That(await _db.CountAsync("select count(*) from accounts where player_id = @p", ("p", s.PlayerId)), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from refresh_tokens where player_id = @p and token_hash = @h", ("p", s.PlayerId),
                                         ("h", RefreshTokens.Hash(s.Refresh))), Is.EqualTo(1), "only the hash is stored");
        await using var cmd = _db.DataSource.CreateCommand("select hmac_key_enc from devices where device_id = @d");
        cmd.Parameters.AddWithValue("d", Guid.Parse(s.Device));
        var enc = (byte[])(await cmd.ExecuteScalarAsync())!;
        Assert.That(Convert.ToBase64String(enc), Does.Not.Contain(s.Key));
        Assert.That(new SecretProtector(_keys).Unprotect(enc, Encoding.UTF8.GetBytes("device/" + s.Device)), Is.EqualTo(Convert.FromBase64String(s.Key)));
    }

    [Test]
    public async Task Guest_is_idempotent_by_request_id_and_its_stored_body_holds_no_live_token()
    {
        string device = NewDevice(), key = NewKey();
        byte[] first = await IdempotencyReplay.AssertReplayAsync(_client, id => GuestRequest(device, key, id),
            () => _db.CountAsync("select count(*) from accounts"));
        string refresh = JsonDocument.Parse(first).RootElement.GetProperty("refreshToken").GetString()!;
        await using var cmd = _db.DataSource.CreateCommand("select response::text from idempotency where route = 'post/auth/guest'");
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            Assert.That(rd.GetString(0), Does.Not.Contain(refresh));
            Assert.That(rd.GetString(0), Does.Not.Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes(refresh))[..20]));
        }
    }

    [Test]
    public async Task A_second_guest_for_the_device_is_409_DEVICE_ALREADY_BOUND_with_the_playerId()
    {
        var s = await Guest();
        using var again = await _client.SendAsync(GuestRequest(s.Device, NewKey()));
        await AssertError(again, HttpStatusCode.Conflict, "DEVICE_ALREADY_BOUND");
        Assert.That((await Body(again)).GetProperty("details").GetProperty("playerId").GetGuid(), Is.EqualTo(s.PlayerId));
    }

    [Test]
    public async Task Concurrent_guests_for_one_device_create_one_account()
    {
        string device = NewDevice();
        var rs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _client.SendAsync(GuestRequest(device, NewKey()))));
        Assert.That(rs.Count(r => r.StatusCode == HttpStatusCode.Created), Is.EqualTo(1));
        Assert.That(rs.Count(r => r.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(3));
        Assert.That(await _db.CountAsync("select count(*) from devices where device_id = @d", ("d", Guid.Parse(device))), Is.EqualTo(1));
        foreach (var r in rs) r.Dispose();
    }

    [Test]
    public async Task Guest_validates_its_body()
    {
        string device = NewDevice();
        using var mismatch = await _client.SendAsync(GuestRequest(device, NewKey(), bodyDevice: NewDevice()));
        await AssertError(mismatch, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        using var shortKey = await _client.SendAsync(GuestRequest(device, Convert.ToBase64String(new byte[16])));
        await AssertError(shortKey, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        using var notGuid = await _client.SendAsync(GuestRequest("not-a-guid", NewKey()));
        await AssertError(notGuid, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        using var noId = await _client.SendAsync(Json(HttpMethod.Post, "/v1/auth/guest", new { deviceId = device, deviceKey = NewKey() }, device));
        await AssertError(noId, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
    }

    // ---- /auth/refresh ----

    [Test]
    public async Task Refresh_rotates_and_reusing_a_rotated_out_token_revokes_the_family_AC19()
    {
        var s = await Guest();
        _clock.Advance(TimeSpan.FromMinutes(70));
        using var r1 = await Refresh(s.Refresh, s.Device);
        Assert.That(r1.StatusCode, Is.EqualTo(HttpStatusCode.OK), await r1.Content.ReadAsStringAsync());
        var b1 = await Body(r1);
        Assert.That(b1.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "accessToken", "refreshToken", "accessExpiresAt", "serverTime" }));
        string current = b1.GetProperty("refreshToken").GetString()!;
        Assert.That(current, Is.Not.EqualTo(s.Refresh));
        Assert.That(b1.GetProperty("accessExpiresAt").GetInt64(), Is.EqualTo(_clock.GetUtcNow().ToUnixTimeSeconds() + 3600));

        _clock.Advance(TimeSpan.FromSeconds(31));   // past REFRESH_REUSE_GRACE_S
        using var reuse = await Refresh(s.Refresh, s.Device);
        await AssertError(reuse, HttpStatusCode.Unauthorized, "TOKEN_REUSED");
        using var afterTheft = await Refresh(current, s.Device);
        await AssertError(afterTheft, HttpStatusCode.Unauthorized, "TOKEN_REVOKED");
        Assert.That(await _db.CountAsync("select count(*) from refresh_tokens where player_id = @p and revoked_at is null", ("p", s.PlayerId)), Is.Zero);
    }

    [Test]
    public async Task Refresh_retry_within_the_grace_rotates_the_live_token_issue_273()
    {
        var s = await Guest();
        using var lost = await Refresh(s.Refresh, s.Device);   // the client never sees this response
        string lostToken = (await Body(lost)).GetProperty("refreshToken").GetString()!;

        _clock.Advance(TimeSpan.FromSeconds(10));
        using var retry = await Refresh(s.Refresh, s.Device);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.OK), await retry.Content.ReadAsStringAsync());
        string current = (await Body(retry)).GetProperty("refreshToken").GetString()!;
        Assert.That(current, Is.Not.EqualTo(lostToken));
        Assert.That(await _db.CountAsync("select count(*) from refresh_tokens where player_id = @p and revoked_at is null", ("p", s.PlayerId)), Is.EqualTo(1));

        using var mismatch = await Refresh(s.Refresh, NewDevice());   // the grace is same-device only
        await AssertError(mismatch, HttpStatusCode.Forbidden, "DEVICE_MISMATCH");

        using var next = await Refresh(current, s.Device);
        Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.OK), await next.Content.ReadAsStringAsync());
        _clock.Advance(TimeSpan.FromSeconds(31));
        using var late = await Refresh(s.Refresh, s.Device);
        await AssertError(late, HttpStatusCode.Unauthorized, "TOKEN_REUSED");
    }

    [Test]
    public async Task Refresh_errors_expired_device_mismatch_and_unknown()
    {
        var s = await Guest();
        using var mismatch = await Refresh(s.Refresh, NewDevice());
        await AssertError(mismatch, HttpStatusCode.Forbidden, "DEVICE_MISMATCH");
        using var unknown = await Refresh(RefreshTokens.New(), s.Device);
        await AssertError(unknown, HttpStatusCode.Unauthorized, ErrorCodes.UNAUTHENTICATED);

        _clock.Advance(TimeSpan.FromDays(60));
        using var expired = await Refresh(s.Refresh, s.Device);
        await AssertError(expired, HttpStatusCode.Unauthorized, ErrorCodes.TOKEN_EXPIRED);
    }

    [Test]
    public async Task Logout_revokes_the_family_only()
    {
        var a = await Guest();
        var b = await Guest();
        using var logout = await _client.SendAsync(Json(HttpMethod.Post, "/v1/auth/logout", new { refreshToken = a.Refresh }, a.Device, NewId(), a.Access));
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), await logout.Content.ReadAsStringAsync());
        using var dead = await Refresh(a.Refresh, a.Device);
        await AssertError(dead, HttpStatusCode.Unauthorized, "TOKEN_REVOKED");

        using var notMine = await _client.SendAsync(Json(HttpMethod.Post, "/v1/auth/logout", new { refreshToken = b.Refresh }, a.Device, NewId(), a.Access));
        Assert.That(notMine.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var alive = await Refresh(b.Refresh, b.Device);
        Assert.That(alive.StatusCode, Is.EqualTo(HttpStatusCode.OK), "another player's token is untouched");

        using var noToken = await _client.SendAsync(Json(HttpMethod.Post, "/v1/auth/logout", new { refreshToken = a.Refresh }, a.Device, NewId()));
        await AssertError(noToken, HttpStatusCode.Unauthorized, ErrorCodes.UNAUTHENTICATED);
    }

    [Test]
    public async Task Logout_releases_the_device_so_the_next_guest_is_a_new_account()   // 00 decision 117, issue #293
    {
        var a = await Guest();
        var b = await Guest();
        // Another player's token releases nothing: b's device stays bound.
        using var notMine = await _client.SendAsync(Json(HttpMethod.Post, "/v1/auth/logout", new { refreshToken = b.Refresh }, a.Device, NewId(), a.Access));
        Assert.That(notMine.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var stillBound = await _client.SendAsync(GuestRequest(b.Device, NewKey()));
        await AssertError(stillBound, HttpStatusCode.Conflict, "DEVICE_ALREADY_BOUND");

        using var logout = await _client.SendAsync(Json(HttpMethod.Post, "/v1/auth/logout", new { refreshToken = a.Refresh }, a.Device, NewId(), a.Access));
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await _db.CountAsync("select count(*) from devices where device_id = @d and player_id = @p and released_at is not null",
                                         ("d", Guid.Parse(a.Device)), ("p", a.PlayerId)), Is.EqualTo(1));

        var again = await Guest(a.Device);
        Assert.That(again.PlayerId, Is.Not.EqualTo(a.PlayerId), "a new guest, not the old account");
        Assert.That(await _db.CountAsync("select count(*) from devices where device_id = @d and player_id = @p and released_at is null",
                                         ("d", Guid.Parse(a.Device)), ("p", again.PlayerId)), Is.EqualTo(1), "the row is rebound, not duplicated");
        using var fresh = await Refresh(again.Refresh, again.Device);
        Assert.That(fresh.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the new guest's family works on the rebound device");
        using var bound = await _client.SendAsync(GuestRequest(a.Device, NewKey()));
        await AssertError(bound, HttpStatusCode.Conflict, "DEVICE_ALREADY_BOUND");
    }

    // ---- /devices ----

    async Task<HttpResponseMessage> Devices(Session s, string key, string? device = null) =>
        await _client.SendAsync(Json(HttpMethod.Post, "/v1/devices",
            new { deviceId = device ?? s.Device, deviceKey = key, model = "Pixel 8", os = "Android 15", clientVersion = "1.5.0+1", pushToken = "push" },
            s.Device, NewId(), s.Access));

    async Task<byte[]> StoredKey(string device)
    {
        await using var cmd = _db.DataSource.CreateCommand("select hmac_key_enc from devices where device_id = @d");
        cmd.Parameters.AddWithValue("d", Guid.Parse(device));
        return new SecretProtector(_keys).Unprotect((byte[])(await cmd.ExecuteScalarAsync())!, Encoding.UTF8.GetBytes("device/" + device));
    }

    [Test]
    public async Task Devices_updates_metadata_and_rotates_the_key_within_24h_of_guest()
    {
        var s = await Guest();
        using (var same = await Devices(s, s.Key)) Assert.That(same.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), await same.Content.ReadAsStringAsync());
        Assert.That(await _db.CountAsync("select count(*) from devices where device_id = @d and model = 'Pixel 8' and push_token = 'push'", ("d", Guid.Parse(s.Device))), Is.EqualTo(1));

        _clock.Advance(TimeSpan.FromHours(23));
        s = await Renew(s);
        string key2 = NewKey();
        using (var rotate = await Devices(s, key2)) Assert.That(rotate.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await StoredKey(s.Device), Is.EqualTo(Convert.FromBase64String(key2)));
    }

    [Test]
    public async Task Devices_refuses_a_late_rotation_unless_a_save_verified_under_the_old_key()
    {
        var s = await Guest();
        _clock.Advance(TimeSpan.FromHours(25));
        var now = await Renew(s);

        using (var late = await Devices(now, NewKey()))
        {
            await AssertError(late, HttpStatusCode.Forbidden, ErrorCodes.FORBIDDEN);
            Assert.That((await Body(late)).GetProperty("details").GetProperty("reason").GetString(), Is.EqualTo("KEY_ROTATION_NOT_ALLOWED"));
        }
        Assert.That(await StoredKey(s.Device), Is.EqualTo(Convert.FromBase64String(s.Key)), "unchanged");

        await _db.ExecAsync("insert into saves (player_id, save_seq, device_id, schema_version, content_version, client_version, last_saved_server_time, " +
                            "clock_authoritative, size_bytes, sha256, hmac_valid, blob_key, etag, progress_score, summary) " +
                            "values (@p, 1, @d, 1, '2026.11.03', '1.4.2+318', 1793670900, true, 10, '\\x00', true, 'k', 'e', 0, '{}')",
                            ("p", s.PlayerId), ("d", Guid.Parse(s.Device)));
        string key3 = NewKey();
        using (var verified = await Devices(now, key3)) Assert.That(verified.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), await verified.Content.ReadAsStringAsync());
        Assert.That(await StoredKey(s.Device), Is.EqualTo(Convert.FromBase64String(key3)));
    }

    [Test]
    public async Task Devices_leaves_a_released_device_alone_until_guest_rebinds_it()   // issue #335
    {
        var s = await Guest();
        using (var logout = await _client.SendAsync(Json(HttpMethod.Post, "/v1/auth/logout", new { refreshToken = s.Refresh }, s.Device, NewId(), s.Access)))
            Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        // The logged-out player's access token is still unexpired; within 24 h of the guest call a new key would be allowed.
        using (var same = await Devices(s, s.Key)) await AssertError(same, HttpStatusCode.Conflict, "DEVICE_ALREADY_BOUND");
        using (var rotate = await Devices(s, NewKey())) await AssertError(rotate, HttpStatusCode.Conflict, "DEVICE_ALREADY_BOUND");
        Assert.That(await StoredKey(s.Device), Is.EqualTo(Convert.FromBase64String(s.Key)), "the released row's key is unchanged");
        Assert.That(await _db.CountAsync("select count(*) from devices where device_id = @d and player_id = @p and released_at is not null " +
                                         "and push_token is null",
                                         ("d", Guid.Parse(s.Device)), ("p", s.PlayerId)), Is.EqualTo(1), "nor its metadata, and it stays released");

        var again = await Guest(s.Device);
        Assert.That(again.PlayerId, Is.Not.EqualTo(s.PlayerId));
        using (var mine = await Devices(again, again.Key)) Assert.That(mine.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), await mine.Content.ReadAsStringAsync());
        Assert.That(await _db.CountAsync("select count(*) from devices where device_id = @d and player_id = @p and released_at is null and push_token = 'push'",
                                         ("d", Guid.Parse(s.Device)), ("p", again.PlayerId)), Is.EqualTo(1), "the rebound row takes /devices again");
        using (var old = await Devices(s, s.Key)) await AssertError(old, HttpStatusCode.Conflict, "DEVICE_ALREADY_BOUND");
    }

    [Test]
    public async Task Devices_only_accepts_the_tokens_own_device()
    {
        var s = await Guest();
        using var other = await Devices(s, NewKey(), device: NewDevice());
        await AssertError(other, HttpStatusCode.Forbidden, ErrorCodes.FORBIDDEN);
        Assert.That((await Body(other)).GetProperty("details").GetProperty("reason").GetString(), Is.EqualTo("DEVICE_MISMATCH"));
    }

    [Test]
    public async Task Devices_registration_is_idempotent_for_the_same_device_and_key()   // issue #150
    {
        var s = await Guest();
        string id = NewId();
        for (int i = 0; i < 2; i++)
        {
            using var again = await _client.SendAsync(Json(HttpMethod.Post, "/v1/devices",
                new { deviceId = s.Device, deviceKey = s.Key, model = "Pixel 8", os = "Android 15", clientVersion = "1.5.0+1" }, s.Device, id, s.Access));
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), $"replay {i}: " + await again.Content.ReadAsStringAsync());
        }
        using (var fresh = await Devices(s, s.Key)) Assert.That(fresh.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "a new request id, the same key");
        Assert.That(await _db.CountAsync("select count(*) from devices where device_id = @d", ("d", Guid.Parse(s.Device))), Is.EqualTo(1));
        Assert.That(await StoredKey(s.Device), Is.EqualTo(Convert.FromBase64String(s.Key)));
    }

    [Test]
    public async Task The_device_key_never_reaches_the_logs()   // 02 section 10.1
    {
        var s = await Guest();
        await Devices(s, s.Key);
        string rotated = NewKey();
        _clock.Advance(TimeSpan.FromHours(25));
        var now = await Renew(s);
        using (var refused = await Devices(now, rotated)) Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        string shortKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(31));
        using (var bad = await Devices(now, shortKey)) Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using (var bad = await _client.SendAsync(GuestRequest(NewDevice(), shortKey))) Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var lines = _logs.Lines;
        Assert.That(lines, Is.Not.Empty, "the capture works");
        foreach (string key in new[] { s.Key, rotated, shortKey })
        {
            string hex = Convert.ToHexString(Convert.FromBase64String(key));
            Assert.That(lines.Where(l => l.Contains(key) || l.Contains(hex, StringComparison.OrdinalIgnoreCase)), Is.Empty);
        }
    }

    /// <summary>Every log line (message and exception) of the api, at every level.</summary>
    sealed class CapturingLoggerProvider : ILoggerProvider
    {
        readonly List<string> _lines = new();
        public string[] Lines { get { lock (_lines) return _lines.ToArray(); } }
        public ILogger CreateLogger(string category) => new Logger(this, category);
        public void Dispose() { }

        sealed class Logger(CapturingLoggerProvider p, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format)
            {
                lock (p._lines) p._lines.Add($"{category} {format(state, e)} {e}");
            }
        }
    }

    // ---- rate limits ----

    [Test]
    public async Task Guest_is_limited_to_5_per_minute_and_20_per_day_per_ip()
    {
        for (int i = 0; i < 5; i++)
        {
            using var ok = await _client.SendAsync(GuestRequest(NewDevice(), NewKey()));
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        }
        using (var sixth = await _client.SendAsync(GuestRequest(NewDevice(), NewKey())))
            await AssertError(sixth, HttpStatusCode.TooManyRequests, ErrorCodes.RATE_LIMITED);

        for (int minute = 1; minute < 4; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            for (int i = 0; i < 5; i++)
            {
                using var ok = await _client.SendAsync(GuestRequest(NewDevice(), NewKey()));
                Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.Created), $"minute {minute}, call {i}");
            }
        }
        _clock.Advance(TimeSpan.FromMinutes(1));
        using var day = await _client.SendAsync(GuestRequest(NewDevice(), NewKey()));
        await AssertError(day, HttpStatusCode.TooManyRequests, ErrorCodes.RATE_LIMITED);
        Assert.That((await Body(day)).GetProperty("retryAfterSec").GetInt32(), Is.GreaterThan(3600), "the day window binds");
    }

    [Test]
    public async Task Refresh_is_limited_to_30_per_minute()
    {
        string device = NewDevice();
        for (int i = 0; i < 30; i++)
        {
            using var r = await Refresh(RefreshTokens.New(), device);
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        using var limited = await Refresh(RefreshTokens.New(), device);
        await AssertError(limited, HttpStatusCode.TooManyRequests, ErrorCodes.RATE_LIMITED);
    }
}
