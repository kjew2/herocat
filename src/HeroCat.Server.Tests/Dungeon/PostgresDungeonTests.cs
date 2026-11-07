using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeroCat.Remote.Api;
using HeroCat.Remote.Services;
using HeroCat.Runtime.Errors;
using HeroCat.Server.Features.Config;
using HeroCat.Server.Features.Dungeon;
using HeroCat.Server.Features.Save;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Infrastructure.Blobs;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Idempotency;
using HeroCat.Server.Tests.Pipeline;
using HeroCat.Sim.Content;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using ApiHeaders = HeroCat.Server.Pipeline.ApiHeaders;
using ClientSeedRequest = HeroCat.Runtime.Services.DungeonSeedRequest;

namespace HeroCat.Server.Tests.Dungeon;

/// <summary>
/// <c>POST /dungeon/seed</c> and the <c>PUT /save</c> seed correlation end to end (specs/08 sections 3.3 and 7.3; issue
/// #205): the real api over a scratch Postgres with the migration set, an in-memory blob store, a fake clock and in-memory
/// rate limits; players come from <c>POST /auth/guest</c>. The last tests drive the client's own
/// <see cref="DungeonSeedClient"/> against the server. Runs only when HEROCAT_TEST_POSTGRES is set.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresDungeonTests
{
    const string Bucket = "herocat-saves-test";
    const long Now = 1_793_670_900;
    const ulong FixedSeed = 0x9f86_d081_884c_7d65UL;

    static readonly Lazy<IContentSets> Content = new(() => new DirectoryContentSets(Microsoft.Extensions.Options.Options.Create(new ContentSetOptions())));
    static string Version => Content.Value.Active.Version;
    static int Floors => Content.Value.Active.DungeonOf(Sim.Content.Dungeon.FISH_COIN)!.FloorDefs.Count;

    ScratchDatabase _db = null!;
    FakeClock _clock = null!;
    MutableActiveConfig _config = null!;
    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;
    readonly ServerKeyRing _keys = ServerKeyRing.Ephemeral();

    [OneTimeSetUp]
    public async Task CreateDatabase() => _db = await ScratchDatabase.CreateAsync("herocat_dungeon");

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_db != null) await _db.DisposeAsync();
    }

    [SetUp]
    public void StartApi()
    {
        _clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(Now));
        _config = new MutableActiveConfig();
        _etag = null;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("HeroCat:Database:ConnectionString", _db.ConnectionString);
            b.UseSetting("HeroCat:Blobs:SavesBucket", Bucket);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(_clock);
                s.AddSingleton(new Lazy<ServerKeyRing>(_keys));
                s.AddSingleton<IRateLimitStore>(new InMemoryRateLimitStore());
                s.AddSingleton<IMaintenanceFlag>(new InMemoryMaintenanceFlag());
                s.AddSingleton<IBlobStore>(new InMemoryBlobStore());
                s.AddSingleton<IActiveConfigSource>(_config);
                s.AddSingleton<IDungeonSeedSource>(new SequenceSeeds(FixedSeed));
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

    sealed class MutableActiveConfig : IActiveConfigSource
    {
        public ActiveConfig? Config { get; set; }
        public ValueTask<ActiveConfig?> GetActiveAsync(CancellationToken ct) => ValueTask.FromResult(Config);
        public void Invalidate() { }
    }

    /// <summary>FixedSeed, FixedSeed + 1, ...: distinct per request, so a replay is visible as the same value.</summary>
    sealed class SequenceSeeds : IDungeonSeedSource
    {
        ulong _next;
        public SequenceSeeds(ulong first) => _next = first;
        public ulong Next() => Interlocked.Increment(ref _next) - 1;
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

    static void Headers(HttpRequestMessage r, string? device, string? requestId, string? token)
    {
        r.Headers.Add(ApiHeaders.ClientVersion, PipelineHost.ClientVersion);
        r.Headers.Add(ApiHeaders.ContentVersion, Version);
        if (device != null) r.Headers.Add(ApiHeaders.DeviceId, device);
        if (requestId != null) r.Headers.Add(ApiHeaders.RequestId, requestId);
        if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    HttpRequestMessage SeedRequest(Player p, string kind, int floor, string requestId)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/v1/dungeon/seed");
        Headers(r, p.Device.ToString("D"), requestId, p.Access);
        r.Content = new StringContent(JsonSerializer.Serialize(new { kind, floor, clientContentVersion = Version }), Encoding.UTF8, "application/json");
        return r;
    }

    async Task<(HttpStatusCode status, JsonElement body)> Seed(Player p, int floor, string kind = "FISH_COIN", string? requestId = null)
    {
        using var resp = await _client.SendAsync(SeedRequest(p, kind, floor, requestId ?? NewId()));
        string text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    async Task<JsonElement> SeedOk(Player p, int floor, string kind = "FISH_COIN")
    {
        var (status, body) = await Seed(p, floor, kind);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        return body;
    }

    static void AssertError((HttpStatusCode status, JsonElement body) r, HttpStatusCode status, string code)
    {
        Assert.That(r.status, Is.EqualTo(status), r.body.ToString());
        Assert.That(r.body.GetProperty("code").GetString(), Is.EqualTo(code), r.body.ToString());
    }

    static string Body(Player p, long seq, int[] bestFloor, params (string grantId, string kind, int floor, bool cleared)[] runs) =>
        new JsonObject
        {
            ["SchemaVersion"] = 1, ["ContentVersion"] = "2026.11.03", ["PlayerId"] = p.Id.ToString(), ["DeviceId"] = p.Device.ToString(),
            ["SaveSeq"] = seq, ["Time"] = new JsonObject { ["LastSavedServerTime"] = Now },
            ["Dungeons"] = new JsonObject
            {
                ["BestFloor"] = new JsonArray(bestFloor.Select(f => (JsonNode)f).ToArray()),
                ["RecentRuns"] = new JsonArray(runs.Select(r => (JsonNode)new JsonObject
                {
                    ["GrantId"] = r.grantId, ["Kind"] = r.kind, ["Floor"] = r.floor, ["Cleared"] = r.cleared,
                }).ToArray()),
            },
        }.ToJsonString();

    string? _etag;

    /// <summary>Uploads a save carrying <paramref name="bestFloor"/> and <paramref name="runs"/>; returns the response's flags.</summary>
    async Task<string[]> Upload(Player p, long seq, int[] bestFloor, params (string grantId, string kind, int floor, bool cleared)[] runs)
    {
        var r = new HttpRequestMessage(HttpMethod.Put, "/v1/save");
        Headers(r, p.Device.ToString("D"), NewId(), p.Access);
        r.Headers.TryAddWithoutValidation(SaveController.IfMatchHeader, _etag ?? "*");
        r.Headers.TryAddWithoutValidation(SaveController.SaveMetaHeaderName, JsonSerializer.Serialize(new
        {
            playerId = p.Id, deviceId = p.Device, saveSeq = seq, clockAuthoritative = true, progressScore = 1.0,
            summary = new { stage = 1 },
        }));
        r.Content = new ByteArrayContent(SaveEnvelope.Encode(Body(p, seq, bestFloor, runs), p.Key));
        r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _client.SendAsync(r);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        _etag = body.GetProperty("etag").GetString();
        return body.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();
    }

    // ---- POST /dungeon/seed ----

    [Test]
    public async Task A_seed_is_stored_with_its_ttl_and_the_same_requestId_replays_it()
    {
        var p = await Guest();
        string requestId = NewId();
        byte[] first = await IdempotencyReplay.AssertReplayAsync(_client, id => SeedRequest(p, "FISH_COIN", 1, id),
            () => _db.CountAsync("select count(*) from dungeon_seeds where player_id = @p", ("p", p.Id)), requestId);
        var body = JsonDocument.Parse(first).RootElement;
        Assert.That(body.EnumerateObject().Select(x => x.Name), Is.EquivalentTo(new[] { "grantId", "seed", "expiresAt", "serverTime" }));
        Assert.That(body.GetProperty("seed").GetString(), Is.EqualTo("9f86d081884c7d65"));
        Assert.That(body.GetProperty("expiresAt").GetInt64(), Is.EqualTo(Now + 600));
        Assert.That(body.GetProperty("serverTime").GetInt64(), Is.EqualTo(Now));
        var grant = Guid.Parse(body.GetProperty("grantId").GetString()!);
        Assert.That(await _db.CountAsync(
            "select count(*) from dungeon_seeds where grant_id = @g and player_id = @p and request_id = @r and kind = 'FISH_COIN' and floor = 1 " +
            "and seed = @s and expires_at = @e and result is null",
            ("g", grant), ("p", p.Id), ("r", requestId), ("s", unchecked((long)FixedSeed)), ("e", DateTimeOffset.FromUnixTimeSeconds(Now + 600))),
            Is.EqualTo(1));
    }

    [Test]
    public async Task The_eleventh_seed_of_a_kind_and_day_is_DUNGEON_SEED_CAP_expired_or_not()
    {
        var p = await Guest();
        for (int i = 0; i < 10; i++) await SeedOk(p, 1);
        AssertError(await Seed(p, 1), HttpStatusCode.TooManyRequests, DungeonErrors.DUNGEON_SEED_CAP);
        await SeedOk(p, 1, "ROCK_CANDY");   // per kind
        _clock.Advance(TimeSpan.FromSeconds(601));   // expired seeds still count (specs/08 section 3.3, 00 decision 114)
        AssertError(await Seed(p, 1), HttpStatusCode.TooManyRequests, DungeonErrors.DUNGEON_SEED_CAP);
    }

    [Test]
    public async Task The_unlock_reads_the_last_uploaded_BestFloor_and_tolerates_one_floor()
    {
        var p = await Guest();
        await SeedOk(p, 2);   // no upload yet: BestFloor 0, floors 1-2
        AssertError(await Seed(p, 3), HttpStatusCode.Forbidden, DungeonErrors.DUNGEON_LOCKED);

        var g = await SeedOk(p, 1);
        await Upload(p, 1, new[] { 0, 0, 0 });
        await SeedOk(p, 2);
        Assert.That(await _db.CountAsync("select count(*) from dungeon_progress where player_id = @p and best_floor = 0", ("p", p.Id)), Is.EqualTo(3));

        // BestFloor 5 is three floors above 2 with seeds for floors 1 and 2 only: accepted and flagged, still the last upload.
        await Upload(p, 2, new[] { 5, 0, 0 }, (g.GetProperty("grantId").GetString()!, "FISH_COIN", 1, true));
        await SeedOk(p, 7);   // 2 above BestFloor
        AssertError(await Seed(p, 8), HttpStatusCode.Forbidden, DungeonErrors.DUNGEON_LOCKED);
    }

    [Test]
    public async Task Floors_kinds_and_the_kill_switch_are_checked()
    {
        var p = await Guest();
        AssertError(await Seed(p, 0), HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        AssertError(await Seed(p, Floors + 1), HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        AssertError(await Seed(p, 1, "GUILD_BOSS"), HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        AssertError(await Seed(p, 1, "fish_coin"), HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);

        _config.Config = new ActiveConfig(1, JsonNode.Parse("{\"killSwitches\":{\"dungeonsEnabled\":false}}")!.AsObject());
        string id = NewId();
        AssertError(await Seed(p, 1, requestId: id), HttpStatusCode.ServiceUnavailable, DungeonErrors.DUNGEONS_DISABLED);
        _config.Config = null;
        await SeedOk(p, 1);
        var (status, _) = await Seed(p, 1, requestId: id);   // the 503 was not stored: the same id is served once lifted
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await _db.CountAsync("select count(*) from dungeon_seeds where player_id = @p", ("p", p.Id)), Is.EqualTo(2));
    }

    // ---- PUT /save correlation (specs/08 section 7.3) ----

    [Test]
    public async Task An_upload_fills_results_for_issued_grants_and_flags_runs_and_clears_without_a_seed()
    {
        var p = await Guest();
        string g1 = (await SeedOk(p, 1)).GetProperty("grantId").GetString()!;
        string g2 = (await SeedOk(p, 2)).GetProperty("grantId").GetString()!;

        var flags = await Upload(p, 1, new[] { 1, 0, 0 }, (g1, "FISH_COIN", 1, true), (g2, "FISH_COIN", 2, false));
        Assert.That(flags, Is.Empty);
        Assert.That(await _db.CountAsync("select count(*) from dungeon_seeds where grant_id = @g and result = 'cleared'", ("g", Guid.Parse(g1))), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from dungeon_seeds where grant_id = @g and result = 'failed'", ("g", Guid.Parse(g2))), Is.EqualTo(1));

        // BestFloor rises to 3 with no seed for floor 3 (the fixture's clear without a seed).
        flags = await Upload(p, 2, new[] { 3, 0, 0 }, (g1, "FISH_COIN", 1, true), (g2, "FISH_COIN", 2, true));
        Assert.That(flags, Is.EqualTo(new[] { DungeonFlags.DUNGEON_WITHOUT_SEED }));
        Assert.That(await _db.CountAsync("select count(*) from dungeon_seeds where grant_id = @g and result = 'cleared'", ("g", Guid.Parse(g2))), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from saves where player_id = @p and @f = any(flags)",
                                         ("p", p.Id), ("f", DungeonFlags.DUNGEON_WITHOUT_SEED)), Is.EqualTo(1));

        // A run whose grant was not issued for that floor, or not to this player, or is not a grant at all.
        var other = await Guest();
        string theirs = (await SeedOk(other, 1)).GetProperty("grantId").GetString()!;
        Assert.That(await Upload(p, 3, new[] { 3, 0, 0 }, (g1, "FISH_COIN", 2, true)), Is.EqualTo(new[] { DungeonFlags.DUNGEON_WITHOUT_SEED }));
        Assert.That(await Upload(p, 4, new[] { 3, 0, 0 }, (theirs, "FISH_COIN", 1, true)), Is.EqualTo(new[] { DungeonFlags.DUNGEON_WITHOUT_SEED }));
        Assert.That(await Upload(p, 5, new[] { 3, 0, 0 }, ("", "FISH_COIN", 1, true)), Is.EqualTo(new[] { DungeonFlags.DUNGEON_WITHOUT_SEED }));
        Assert.That(await Upload(p, 6, new[] { 3, 0, 0 }), Is.Empty, "no new floor and no unmatched run");
    }

    // ---- the client's DungeonSeedClient against this server ----

    sealed class TokenCredentials : IApiCredentials
    {
        public TokenCredentials(string token) => AccessToken = token;
        public string AccessToken { get; }
        public Task<bool> RefreshAsync(CancellationToken ct) => Task.FromResult(false);
    }

    DungeonSeedClient Remote(Player p) =>
        new(new ApiClient(new ApiClientOptions
        {
            BaseUrl = "http://localhost/v1",
            Handler = _factory.Server.CreateHandler(),
            ClientVersion = PipelineHost.ClientVersion,
            ContentVersion = () => Version,
            DeviceId = p.Device.ToString("D"),
            Credentials = new TokenCredentials(p.Access),
            Delay = (_, _) => Task.CompletedTask,
        }), () => Version);

    [Test]
    public async Task The_client_service_reads_the_grant_replays_by_requestId_and_maps_the_refusals()
    {
        var p = await Guest();
        var svc = Remote(p);
        string id = NewId();
        var grant = await svc.RequestSeedAsync(new ClientSeedRequest(id, Sim.Content.Dungeon.FISH_COIN, 1));
        Assert.That((grant.Seed, grant.LocalSeed, grant.ExpiresAtServerSeconds), Is.EqualTo((FixedSeed, false, Now + 600)));
        Assert.That(Guid.TryParse(grant.GrantId, out _), Is.True);
        var again = await svc.RequestSeedAsync(new ClientSeedRequest(id, Sim.Content.Dungeon.FISH_COIN, 1));
        Assert.That((again.Seed, again.GrantId), Is.EqualTo((grant.Seed, grant.GrantId)), "a retry with the same requestId is the server's replay");

        var e = Assert.ThrowsAsync<ServiceException>(() => svc.RequestSeedAsync(new ClientSeedRequest(NewId(), Sim.Content.Dungeon.FISH_COIN, 3)))!;
        Assert.That((e.Code, e.Kind, e.Status), Is.EqualTo((DungeonErrors.DUNGEON_LOCKED, ErrorClass.Rejected, (int?)403)));
        for (int i = 0; i < 9; i++) await svc.RequestSeedAsync(new ClientSeedRequest(NewId(), Sim.Content.Dungeon.FISH_COIN, 1));
        e = Assert.ThrowsAsync<ServiceException>(() => svc.RequestSeedAsync(new ClientSeedRequest(NewId(), Sim.Content.Dungeon.FISH_COIN, 1)))!;
        Assert.That((e.Code, e.Kind, e.Retryable), Is.EqualTo((DungeonErrors.DUNGEON_SEED_CAP, ErrorClass.Rejected, false)));
    }
}
