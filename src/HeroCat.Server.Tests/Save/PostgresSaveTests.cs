using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HeroCat.Server.Features.Auth;
using HeroCat.Server.Features.Save;
using HeroCat.Server.Infrastructure.Blobs;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Save;

/// <summary>
/// The cloud-save endpoints end to end (specs/08 section 3.1, acceptance 12; specs/02 sections 9-10; issue #198): the real
/// api with its pipeline, a scratch Postgres with the migration set, an in-memory blob store, a fake clock and in-memory
/// rate limits. Players come from <c>POST /auth/guest</c>. Runs only when HEROCAT_TEST_POSTGRES is set.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresSaveTests
{
    const string Bucket = "herocat-saves-test";
    const long Now = 1_793_670_900;

    ScratchDatabase _db = null!;
    FakeClock _clock = null!;
    InMemoryRateLimitStore _limits = null!;
    PresigningBlobStore _blobs = null!;
    FixedSampler _sampler = null!;
    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;
    readonly ServerKeyRing _keys = ServerKeyRing.Ephemeral();

    [OneTimeSetUp]
    public async Task CreateDatabase() => _db = await ScratchDatabase.CreateAsync("herocat_save");

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_db != null) await _db.DisposeAsync();
    }

    [SetUp]
    public void StartApi()
    {
        _clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(Now));
        _limits = new InMemoryRateLimitStore();
        _blobs = new PresigningBlobStore();
        _sampler = new FixedSampler();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("HeroCat:Database:ConnectionString", _db.ConnectionString);
            b.UseSetting("HeroCat:Blobs:SavesBucket", Bucket);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(_clock);
                s.AddSingleton(new Lazy<ServerKeyRing>(_keys));
                s.AddSingleton<IRateLimitStore>(_limits);
                s.AddSingleton<IMaintenanceFlag>(new InMemoryMaintenanceFlag());
                s.AddSingleton<IBlobStore>(_blobs);
                s.AddSingleton<IResimSampler>(_sampler);
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [TearDown]
    public void StopApi()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    sealed class FixedSampler : IResimSampler
    {
        public bool Answer { get; set; }
        public bool Sample(IReadOnlyList<PlausibilityFlag> flags) => Answer;
    }

    /// <summary>The in-memory store, signing URLs when <see cref="Sign"/> is on (as S3 does).</summary>
    sealed class PresigningBlobStore : IBlobStore
    {
        public readonly InMemoryBlobStore Inner = new();
        public bool Sign { get; set; }
        public Task PutAsync(string bucket, string key, byte[] body, string contentType, CancellationToken ct = default) => Inner.PutAsync(bucket, key, body, contentType, ct);
        public Task<byte[]?> GetAsync(string bucket, string key, CancellationToken ct = default) => Inner.GetAsync(bucket, key, ct);
        public Task DeleteAsync(string bucket, string key, CancellationToken ct = default) => Inner.DeleteAsync(bucket, key, ct);
        public Task<Uri?> PresignGetAsync(string bucket, string key, TimeSpan lifetime, CancellationToken ct = default) =>
            Task.FromResult(Sign ? new Uri($"https://{bucket}.s3.example/{key}?X-Amz-Expires={(int)lifetime.TotalSeconds}") : null);
    }

    sealed record Player(Guid Id, Guid Device, byte[] Key, string Access);

    static string NewId() => Guid.NewGuid().ToString("N");

    async Task<Player> Guest()
    {
        var device = Guid.NewGuid();
        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var r = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/guest");
        Headers(r, device.ToString("D"), NewId(), null);
        r.Content = new StringContent(JsonSerializer.Serialize(new { deviceId = device, deviceKey = Convert.ToBase64String(key), region = "US" }),
                                      Encoding.UTF8, "application/json");
        using var resp = await _client.SendAsync(r);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Created), await resp.Content.ReadAsStringAsync());
        var b = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return new Player(b.GetProperty("playerId").GetGuid(), device, key, b.GetProperty("accessToken").GetString()!);
    }

    /// <summary>A second device of the same player (link-login is #196): its row and key, and a token for it.</summary>
    async Task<Player> SecondDevice(Player p)
    {
        var device = Guid.NewGuid();
        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var auth = _factory.Services.GetRequiredService<AuthService>();
        await _db.ExecAsync("insert into devices (device_id, player_id, hmac_key_enc) values (@d, @p, @k)",
                            ("d", device), ("p", p.Id), ("k", auth.ProtectDeviceKey(device, key)));
        var token = _factory.Services.GetRequiredService<AccessTokenService>().Issue(p.Id, device.ToString("D"), 0);
        return new Player(p.Id, device, key, token.Token);
    }

    static void Headers(HttpRequestMessage r, string? device, string? requestId, string? token)
    {
        r.Headers.Add(ApiHeaders.ClientVersion, PipelineHost.ClientVersion);
        r.Headers.Add(ApiHeaders.ContentVersion, PipelineHost.ContentVersion);
        if (device != null) r.Headers.Add(ApiHeaders.DeviceId, device);
        if (requestId != null) r.Headers.Add(ApiHeaders.RequestId, requestId);
        if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    static object Meta(Player p, long seq, double score = 100) => new
    {
        playerId = p.Id, deviceId = p.Device, saveSeq = seq, lastSavedServerTime = Now, schemaVersion = 1, contentVersion = "2026.11.03",
        clientVersion = "1.4.2+318", sizeBytes = 0, sha256 = "", hmac = "", clockAuthoritative = true, progressScore = score,
        summary = new { stage = seq, heroLevel = 1, diamonds = 0, totalPulls = 0, highestFish = 1, awakenRank = 0 },
    };

    HttpRequestMessage Put(Player p, byte[] file, string? ifMatch, object? meta, string? requestId = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Put, "/v1/save");
        Headers(r, p.Device.ToString("D"), requestId ?? NewId(), p.Access);
        if (ifMatch != null) r.Headers.TryAddWithoutValidation(SaveController.IfMatchHeader, ifMatch);
        if (meta != null) r.Headers.TryAddWithoutValidation(SaveController.SaveMetaHeaderName, JsonSerializer.Serialize(meta));
        r.Content = new ByteArrayContent(file);
        r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return r;
    }

    static byte[] File(Player p, long seq, byte[]? key = null, long lastSaved = Now, string pad = "") =>
        SaveEnvelope.Encode(SaveUnitTests.Body(p.Id, p.Device, seq, lastSaved, pad), key ?? p.Key);

    async Task<(HttpResponseMessage resp, JsonElement body)> Upload(Player p, long seq, string? ifMatch, byte[]? file = null, object? meta = null,
                                                                   string? requestId = null)
    {
        var resp = await _client.SendAsync(Put(p, file ?? File(p, seq), ifMatch, meta ?? Meta(p, seq), requestId));
        string text = await resp.Content.ReadAsStringAsync();
        return (resp, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    async Task<string> UploadOk(Player p, long seq, string? ifMatch)
    {
        var (resp, body) = await Upload(p, seq, ifMatch);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        return body.GetProperty("etag").GetString()!;
    }

    async Task<HttpResponseMessage> Get(Player p, string path)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        Headers(r, p.Device.ToString("D"), null, p.Access);
        return await _client.SendAsync(r);
    }

    static void AssertError(HttpResponseMessage r, JsonElement body, HttpStatusCode status, string code)
    {
        Assert.That(r.StatusCode, Is.EqualTo(status), body.ToString());
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo(code), body.ToString());
    }

    static string[] Flags(JsonElement body) => body.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();

    // ---- first upload, meta, download ----

    [Test]
    public async Task First_upload_with_If_Match_star_stores_blob_row_and_history_and_meta_and_download_serve_it()
    {
        var p = await Guest();
        using (var meta404 = await Get(p, "/v1/save/meta"))
            AssertError(meta404, JsonDocument.Parse(await meta404.Content.ReadAsStringAsync()).RootElement, HttpStatusCode.NotFound, SaveErrors.NO_SAVE);

        byte[] file = File(p, 1875);
        var (resp, body) = await Upload(p, 1875, "*", file);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        Assert.That(body.EnumerateObject().Select(x => x.Name),
                    Is.EquivalentTo(new[] { "etag", "saveSeq", "serverTime", "flags", "diamondReconcile", "requestCmdLog" }));
        Assert.That(body.GetProperty("saveSeq").GetInt64(), Is.EqualTo(1875));
        Assert.That(body.GetProperty("serverTime").GetInt64(), Is.EqualTo(Now));
        Assert.That(Flags(body), Is.Empty);
        Assert.That(body.GetProperty("diamondReconcile").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(body.GetProperty("requestCmdLog").GetBoolean(), Is.False);
        string etag = body.GetProperty("etag").GetString()!;

        string key = $"saves/{p.Id:D}/1875-{p.Device.ToString("N")[..8]}.sav";
        Assert.That(await _blobs.Inner.GetAsync(Bucket, key), Is.EqualTo(file));
        Assert.That(await _db.CountAsync("select count(*) from saves where player_id = @p and hmac_valid and save_seq = 1875 and blob_key = @k",
                                         ("p", p.Id), ("k", $"s3://{Bucket}/{key}")), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from save_history where player_id = @p", ("p", p.Id)), Is.EqualTo(1));

        using var metaResp = await Get(p, "/v1/save/meta");
        var m = JsonDocument.Parse(await metaResp.Content.ReadAsStringAsync()).RootElement;
        Assert.That(metaResp.StatusCode, Is.EqualTo(HttpStatusCode.OK), m.ToString());
        Assert.That(m.EnumerateObject().Select(x => x.Name), Is.EquivalentTo(new[]
        {
            "playerId", "deviceId", "saveSeq", "lastSavedServerTime", "schemaVersion", "contentVersion", "clientVersion", "sizeBytes",
            "sha256", "hmac", "clockAuthoritative", "progressScore", "summary", "etag",
        }));
        var env = SaveEnvelope.Parse(file);
        Assert.That(m.GetProperty("etag").GetString(), Is.EqualTo(etag));
        Assert.That(m.GetProperty("sizeBytes").GetInt32(), Is.EqualTo(file.Length));
        Assert.That(m.GetProperty("sha256").GetString(), Is.EqualTo(Convert.ToHexString(env.Sha256).ToLowerInvariant()));
        Assert.That(m.GetProperty("hmac").GetString(), Is.EqualTo(env.HmacHex));
        Assert.That(m.GetProperty("deviceId").GetGuid(), Is.EqualTo(p.Device));
        Assert.That(m.GetProperty("lastSavedServerTime").GetInt64(), Is.EqualTo(Now));
        Assert.That(m.GetProperty("summary").GetProperty("stage").GetInt64(), Is.EqualTo(1875));

        using var dl = await Get(p, "/v1/save");
        Assert.That(dl.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(dl.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(dl.Headers.ETag!.ToString(), Is.EqualTo(etag));
        Assert.That(JsonDocument.Parse(dl.Headers.GetValues(SaveController.SaveMetaHeaderName).Single()).RootElement.GetProperty("etag").GetString(),
                    Is.EqualTo(etag));
        Assert.That(await dl.Content.ReadAsByteArrayAsync(), Is.EqualTo(file));
    }

    [Test]
    public async Task If_Match_star_with_a_save_present_conflicts_with_the_server_meta()
    {
        var p = await Guest();
        string etag = await UploadOk(p, 1, "*");
        var (resp, body) = await Upload(p, 2, "*");
        AssertError(resp, body, HttpStatusCode.Conflict, SaveErrors.SAVE_CONFLICT);
        var server = body.GetProperty("details").GetProperty("serverMeta");
        Assert.That(server.GetProperty("etag").GetString(), Is.EqualTo(etag));
        Assert.That(server.GetProperty("saveSeq").GetInt64(), Is.EqualTo(1));
    }

    [Test]
    public async Task A_stale_If_Match_is_SAVE_CONFLICT_with_server_meta_and_the_current_etag_is_accepted()
    {
        var p = await Guest();
        string first = await UploadOk(p, 1, "*");
        string second = await UploadOk(p, 2, first);
        Assert.That(second, Is.Not.EqualTo(first));

        var (resp, body) = await Upload(p, 3, first);
        AssertError(resp, body, HttpStatusCode.Conflict, SaveErrors.SAVE_CONFLICT);
        Assert.That(body.GetProperty("details").GetProperty("serverMeta").GetProperty("etag").GetString(), Is.EqualTo(second));
        await UploadOk(p, 3, second);
    }

    [Test]
    public async Task An_etag_when_there_is_no_save_conflicts_with_null_server_meta_and_a_missing_If_Match_is_VALIDATION()
    {
        var p = await Guest();
        var (resp, body) = await Upload(p, 1, "W/\"1-00000000\"");
        AssertError(resp, body, HttpStatusCode.Conflict, SaveErrors.SAVE_CONFLICT);
        Assert.That(body.GetProperty("details").GetProperty("serverMeta").ValueKind, Is.EqualTo(JsonValueKind.Null));

        (resp, body) = await Upload(p, 1, null);
        AssertError(resp, body, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        Assert.That(body.GetProperty("details").GetProperty("header").GetString(), Is.EqualTo("If-Match"));
    }

    // ---- saveSeq ----

    [Test]
    public async Task A_lower_or_equal_saveSeq_from_the_same_device_is_SAVE_STALE()
    {
        var p = await Guest();
        string etag = await UploadOk(p, 10, "*");
        foreach (long seq in new long[] { 9, 10 })
        {
            var (resp, body) = await Upload(p, seq, etag);
            AssertError(resp, body, HttpStatusCode.Conflict, SaveErrors.SAVE_STALE);
        }
        Assert.That(await _db.CountAsync("select save_seq from saves where player_id = @p", ("p", p.Id)), Is.EqualTo(10));
    }

    [Test]
    public async Task A_lower_saveSeq_from_another_device_is_accepted_and_flagged_SEQ_REGRESSION_and_the_same_device_check_still_holds()
    {
        var a = await Guest();
        var b = await SecondDevice(a);
        string etag = await UploadOk(a, 50, "*");

        var (resp, body) = await Upload(b, 7, etag);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        Assert.That(Flags(body), Is.EqualTo(new[] { SaveFlags.SEQ_REGRESSION }));
        etag = body.GetProperty("etag").GetString()!;
        Assert.That(await _db.CountAsync("select count(*) from saves where player_id = @p and @f = any(flags)", ("p", a.Id), ("f", SaveFlags.SEQ_REGRESSION)),
                    Is.EqualTo(1));

        // device A again, below its own 50: stale even though the save of record is B's 7
        (resp, body) = await Upload(a, 49, etag);
        AssertError(resp, body, HttpStatusCode.Conflict, SaveErrors.SAVE_STALE);
        (resp, body) = await Upload(a, 51, etag);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        Assert.That(Flags(body), Is.Empty);
    }

    [Test]
    public async Task Two_devices_uploading_one_saveSeq_keep_both_blobs_and_both_history_entries()
    {
        // Issue #295: B's accepted SEQ_REGRESSION upload of A's saveSeq replaces neither A's blob nor A's history entry.
        var a = await Guest();
        var b = await SecondDevice(a);
        string etag = await UploadOk(a, 7, "*");
        _clock.Advance(TimeSpan.FromSeconds(61));
        etag = await UploadOk(a, 8, etag);
        _clock.Advance(TimeSpan.FromSeconds(61));
        byte[] fromB = File(b, 7);
        var (resp, body) = await Upload(b, 7, etag, fromB);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        Assert.That(Flags(body), Is.EqualTo(new[] { SaveFlags.SEQ_REGRESSION }));

        string keyA = SaveController.BlobKey(a.Id, 7, a.Device), keyB = SaveController.BlobKey(a.Id, 7, b.Device);
        Assert.That(keyA, Is.Not.EqualTo(keyB));
        Assert.That(await _blobs.Inner.GetAsync(Bucket, keyA), Is.Not.Null.And.Not.EqualTo(fromB));
        Assert.That(await _blobs.Inner.GetAsync(Bucket, keyB), Is.EqualTo(fromB));
        Assert.That(await _db.CountAsync("select count(*) from save_history where player_id = @p and save_seq = 7", ("p", a.Id)), Is.EqualTo(2));
        Assert.That(await _db.CountAsync("select count(*) from save_history where player_id = @p and save_seq = 7 and device_id = @d and blob_key = @k",
                                         ("p", a.Id), ("d", a.Device), ("k", SaveController.BlobUri(Bucket, keyA))), Is.EqualTo(1));

        using var hist = await Get(a, "/v1/save/history");
        var list = JsonDocument.Parse(await hist.Content.ReadAsStringAsync()).RootElement;
        Assert.That(list.EnumerateArray().Select(e => (e.GetProperty("saveSeq").GetInt64(), e.GetProperty("deviceId").GetGuid())),
                    Is.EqualTo(new[] { (7L, b.Device), (8L, a.Device), (7L, a.Device) }));
    }

    // ---- integrity ----

    [Test]
    public async Task An_invalid_or_missing_HMAC_is_accepted_with_HMAC_INVALID_and_recorded()
    {
        var p = await Guest();
        var (resp, body) = await Upload(p, 1, "*", File(p, 1, key: new byte[32]));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        Assert.That(Flags(body), Is.EqualTo(new[] { SaveFlags.HMAC_INVALID }));
        Assert.That(await _db.CountAsync("select count(*) from saves where player_id = @p and not hmac_valid", ("p", p.Id)), Is.EqualTo(1));

        string etag = body.GetProperty("etag").GetString()!;
        (resp, body) = await Upload(p, 2, etag, SaveEnvelope.Encode(SaveUnitTests.Body(p.Id, p.Device, 2), null));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        Assert.That(Flags(body), Is.EqualTo(new[] { SaveFlags.HMAC_INVALID }));
    }

    [Test]
    public async Task A_save_stamped_beyond_the_clock_skew_is_accepted_with_CLOCK_AHEAD()
    {
        var p = await Guest();
        var (resp, body) = await Upload(p, 1, "*", File(p, 1, lastSaved: Now + 120));
        Assert.That(Flags(body), Is.Empty, body.ToString());
        (resp, body) = await Upload(p, 2, body.GetProperty("etag").GetString(), File(p, 2, lastSaved: Now + 121));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        Assert.That(Flags(body), Is.EqualTo(new[] { SaveFlags.CLOCK_AHEAD }));
    }

    [Test]
    public async Task A_corrupt_file_is_SAVE_MALFORMED_and_one_over_SAVE_BLOB_MAX_BYTES_is_SAVE_TOO_LARGE()
    {
        var p = await Guest();
        byte[] bad = File(p, 1);
        bad[^2] ^= 0x01;
        var (resp, body) = await Upload(p, 1, "*", bad);
        AssertError(resp, body, HttpStatusCode.BadRequest, SaveErrors.SAVE_MALFORMED);

        byte[] big = File(p, 1, pad: new string('x', 262_144));
        (resp, body) = await Upload(p, 1, "*", big);
        AssertError(resp, body, (HttpStatusCode)413, SaveErrors.SAVE_TOO_LARGE);
        Assert.That(body.GetProperty("details").GetProperty("maxBytes").GetInt32(), Is.EqualTo(262_144));
        Assert.That(await _db.CountAsync("select count(*) from saves where player_id = @p", ("p", p.Id)), Is.Zero);
    }

    [Test]
    public async Task The_save_must_be_the_callers_and_the_meta_must_agree_with_it()
    {
        var p = await Guest();
        var other = await Guest();
        var (resp, body) = await Upload(p, 1, "*", SaveEnvelope.Encode(SaveUnitTests.Body(other.Id, p.Device, 1), p.Key));
        AssertError(resp, body, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        Assert.That(body.GetProperty("details").GetProperty("field").GetString(), Is.EqualTo("PlayerId"));

        (resp, body) = await Upload(p, 1, "*", meta: Meta(p, 2));
        AssertError(resp, body, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        Assert.That(body.GetProperty("details").GetProperty("field").GetString(), Is.EqualTo("saveSeq"));

        var r = Put(p, File(p, 1), "*", null);
        using var noMeta = await _client.SendAsync(r);
        Assert.That(noMeta.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- history, rollback, sampler, idempotency, limits ----

    [Test]
    public async Task History_keeps_the_newest_SAVE_HISTORY_KEEP_newest_first_with_summaries()
    {
        var p = await Guest();
        string etag = "*";
        for (long seq = 1; seq <= 12; seq++)
        {
            etag = await UploadOk(p, seq, etag);
            _clock.Advance(TimeSpan.FromSeconds(61));
        }
        using var resp = await Get(p, "/v1/save/history");
        var list = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), list.ToString());
        Assert.That(list.EnumerateArray().Select(e => e.GetProperty("saveSeq").GetInt64()), Is.EqualTo(Enumerable.Range(3, 10).Reverse().Select(i => (long)i)));
        var first = list[0];
        Assert.That(first.EnumerateObject().Select(x => x.Name), Is.EquivalentTo(new[] { "saveSeq", "deviceId", "uploadedAt", "summary" }));
        Assert.That(first.GetProperty("deviceId").GetGuid(), Is.EqualTo(p.Device));
        Assert.That(first.GetProperty("summary").GetProperty("stage").GetInt64(), Is.EqualTo(12));
        Assert.That(await _db.CountAsync("select count(*) from save_history where player_id = @p", ("p", p.Id)), Is.EqualTo(10));
        for (long seq = 1; seq <= 12; seq++)
            Assert.That(await _blobs.Inner.GetAsync(Bucket, SaveController.BlobKey(p.Id, seq, p.Device)), Is.Not.Null, $"blob {seq}");
    }

    [Test]
    public async Task A_pending_rollback_refuses_uploads_until_the_save_is_downloaded()
    {
        var p = await Guest();
        string etag = await UploadOk(p, 1, "*");
        await _db.ExecAsync("update saves set rollback_pending = true where player_id = @p", ("p", p.Id));

        var (resp, body) = await Upload(p, 2, etag);
        AssertError(resp, body, HttpStatusCode.Conflict, SaveErrors.SAVE_ROLLED_BACK);
        Assert.That(body.GetProperty("details").GetProperty("serverMeta").GetProperty("saveSeq").GetInt64(), Is.EqualTo(1));

        using (var dl = await Get(p, "/v1/save")) Assert.That(dl.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await _db.CountAsync("select count(*) from saves where player_id = @p and rollback_pending", ("p", p.Id)), Is.Zero);
        await UploadOk(p, 2, etag);
    }

    [Test]
    public async Task A_sampled_upload_requests_the_command_log_until_cleared()
    {
        var p = await Guest();
        _sampler.Answer = true;
        var (resp, body) = await Upload(p, 1, "*");
        Assert.That(body.GetProperty("requestCmdLog").GetBoolean(), Is.True, body.ToString());
        _sampler.Answer = false;
        (resp, body) = await Upload(p, 2, body.GetProperty("etag").GetString());
        Assert.That(body.GetProperty("requestCmdLog").GetBoolean(), Is.True, "saves.cmdlog_requested stays until an upload carries the log (#207)");
        Assert.That(await _db.CountAsync("select count(*) from saves where player_id = @p and cmdlog_requested", ("p", p.Id)), Is.EqualTo(1));
    }

    [Test]
    public async Task A_retried_upload_replays_the_first_answer()
    {
        var p = await Guest();
        string id = NewId();
        byte[] file = File(p, 1);
        var (first, firstBody) = await Upload(p, 1, "*", file, requestId: id);
        var (again, againBody) = await Upload(p, 1, "*", file, requestId: id);
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(again.Headers.GetValues(IdempotencyMiddleware.ReplayHeader).Single(), Is.EqualTo("true"));
        Assert.That(againBody.ToString(), Is.EqualTo(firstBody.ToString()));
        Assert.That(await _db.CountAsync("select count(*) from save_history where player_id = @p", ("p", p.Id)), Is.EqualTo(1));
    }

    [Test]
    public async Task Uploads_are_limited_to_6_a_minute()
    {
        var p = await Guest();
        string etag = "*";
        for (long seq = 1; seq <= 6; seq++) etag = await UploadOk(p, seq, etag);
        var (resp, body) = await Upload(p, 7, etag);
        AssertError(resp, body, HttpStatusCode.TooManyRequests, ErrorCodes.RATE_LIMITED);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await UploadOk(p, 7, etag);
    }

    [Test]
    public async Task A_large_save_is_served_by_redirect_to_a_presigned_url_where_the_store_can_sign()
    {
        var p = await Guest();
        byte[] big = File(p, 1, pad: new string('x', 70_000));
        var (resp, body) = await Upload(p, 1, "*", big);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());

        using (var inline = await Get(p, "/v1/save"))
            Assert.That(await inline.Content.ReadAsByteArrayAsync(), Is.EqualTo(big), "no signer: inline");

        _blobs.Sign = true;
        using var dl = await Get(p, "/v1/save");
        Assert.That(dl.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(dl.Headers.Location!.AbsoluteUri, Does.StartWith($"https://{Bucket}.s3.example/saves/{p.Id:D}/1-{p.Device.ToString("N")[..8]}.sav"));
        Assert.That(dl.Headers.Location.AbsoluteUri, Does.Contain("X-Amz-Expires=300"));
        Assert.That(dl.Headers.ETag, Is.Not.Null);

        // a small save is inline even with a signer
        (resp, body) = await Upload(p, 2, body.GetProperty("etag").GetString());
        using var small = await Get(p, "/v1/save");
        Assert.That(small.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
