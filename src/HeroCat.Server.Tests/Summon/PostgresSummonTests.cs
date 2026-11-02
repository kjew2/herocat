using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Summon;

/// <summary>Every request fits (the summon tests make more than 20 pulls a minute on a frozen clock).</summary>
sealed class NoRateLimits : IRateLimitStore
{
    public Task<RateLimitDecision> AcquireAsync(string key, IReadOnlyList<RateLimitRule> rules, long nowMs, CancellationToken ct) =>
        Task.FromResult(RateLimitDecision.Allow);
}

sealed class MutableSummonConfig : ISummonConfigSource
{
    public SummonSwitches Switches { get; set; } = SummonSwitches.AllOpen;
    public ValueTask<SummonSwitches> GetAsync(CancellationToken ct) => ValueTask.FromResult(Switches);
}

/// <summary>
/// The Summon endpoints end to end (specs/08 sections 4.3-4.5, acceptance 3-7 and 17; issue #199): the real api with
/// its pipeline, a scratch Postgres with schema v1, guest accounts from /auth/guest, a seeded uniform stream in place
/// of the CSPRNG (so the server's rolls can be checked against the Sim's), and no rate limits. Runs only when
/// HEROCAT_TEST_POSTGRES is set.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresSummonTests
{
    const ulong Seed = 0x5EED_0199;

    ScratchDatabase _db = null!;
    FakeClock _clock = null!;
    MutableSummonConfig _config = null!;
    SeededUniforms _uniforms = null!;
    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;
    readonly ServerKeyRing _keys = ServerKeyRing.Ephemeral();

    static readonly Lazy<IContentSets> Content = new(() => new DirectoryContentSets(Microsoft.Extensions.Options.Options.Create(new ContentSetOptions())));
    static ContentDb Db => Content.Value.Active;
    static string Version => Db.Version;

    [OneTimeSetUp]
    public async Task CreateDatabase() => _db = await ScratchDatabase.CreateAsync("herocat_summon");

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_db != null) await _db.DisposeAsync();
    }

    [SetUp]
    public void StartApi()
    {
        _clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
        _config = new MutableSummonConfig();
        _uniforms = new SeededUniforms(Seed);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("HeroCat:Database:ConnectionString", _db.ConnectionString);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(_clock);
                s.AddSingleton(new Lazy<ServerKeyRing>(_keys));
                s.AddSingleton<IRateLimitStore>(new NoRateLimits());
                s.AddSingleton<IMaintenanceFlag>(new InMemoryMaintenanceFlag());
                s.AddSingleton<ISummonConfigSource>(_config);
                s.AddSingleton<IUniformSourceFactory>(_uniforms);
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

    sealed record Player(Guid Id, string Device, string Access);

    static string NewId() => Guid.NewGuid().ToString("N");

    static HttpRequestMessage Request(HttpMethod m, string path, object? body, Player? p, string? requestId = null)
    {
        var r = new HttpRequestMessage(m, path);
        r.Headers.Add(ApiHeaders.ClientVersion, PipelineHost.ClientVersion);
        r.Headers.Add(ApiHeaders.ContentVersion, PipelineHost.ContentVersion);
        if (p != null)
        {
            r.Headers.Add(ApiHeaders.DeviceId, p.Device);
            r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.Access);
        }
        if (requestId != null) r.Headers.Add(ApiHeaders.RequestId, requestId);
        if (body != null) r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return r;
    }

    async Task<Player> NewPlayer(long diamonds = 0, long weaponTickets = 0)
    {
        string device = Guid.NewGuid().ToString("D");
        var guest = Request(HttpMethod.Post, "/v1/auth/guest",
            new { deviceId = device, deviceKey = Convert.ToBase64String(new byte[32]), model = "m", os = "o", clientVersion = "1.4.2+318", region = "US" },
            null, NewId());
        guest.Headers.Add(ApiHeaders.DeviceId, device);
        using var r = await _client.SendAsync(guest);
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Created), await r.Content.ReadAsStringAsync());
        var b = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        var p = new Player(b.GetProperty("playerId").GetGuid(), device, b.GetProperty("accessToken").GetString()!);
        if (diamonds != 0) await Credit(p, diamonds);
        if (weaponTickets != 0) await CreditTickets(p, Currency.TICKET_SUMMON_WEAPON, weaponTickets);
        return p;
    }

    Task Credit(Player p, long diamonds) =>
        _db.ExecAsync("insert into diamond_ledger (player_id, delta, reason) values (@p, @d, 'ADMIN')", ("p", p.Id), ("d", (int)diamonds));

    Task CreditTickets(Player p, Currency ticket, long n) =>
        _db.ExecAsync("insert into ticket_ledger (player_id, ticket, delta, reason) values (@p, @t, @d, 'ADMIN')",
                      ("p", p.Id), ("t", ticket.ToString()), ("d", (int)n));

    Task<HttpResponseMessage> Pull(Player p, int count = 10, string payment = "DIAMOND", string banner = "WEAPON", string? requestId = null,
                                   string? version = null) =>
        _client.SendAsync(Request(HttpMethod.Post, "/v1/summon/pull",
                                  new { banner, count, payment, clientContentVersion = version ?? Version }, p, requestId ?? NewId()));

    static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        string text = await r.Content.ReadAsStringAsync();
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    static async Task<JsonElement> Error(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        string text = await r.Content.ReadAsStringAsync();
        Assert.That(r.StatusCode, Is.EqualTo(status), text);
        var e = JsonDocument.Parse(text).RootElement.Clone();
        Assert.That(e.GetProperty("code").GetString(), Is.EqualTo(code), text);
        return e;
    }

    Task<long> Diamonds(Player p) => _db.CountAsync("select coalesce(sum(delta), 0) from diamond_ledger where player_id = @p", ("p", p.Id));
    Task<long> Tickets(Player p) => _db.CountAsync("select coalesce(sum(delta), 0) from ticket_ledger where player_id = @p", ("p", p.Id));
    Task<long> LogRows(Player p) => _db.CountAsync("select count(*) from summon_log where player_id = @p", ("p", p.Id));
    Task<long> SummonCharges(Player p) =>
        _db.CountAsync("select (select count(*) from diamond_ledger where player_id = @p and reason = 'SUMMON') + " +
                       "(select count(*) from ticket_ledger where player_id = @p and reason = 'SUMMON')", ("p", p.Id));

    // ---- the pull ----

    [Test]
    public async Task A_diamond_10_pull_charges_logs_ten_rows_updates_pity_and_matches_the_sim_for_the_same_uniforms()
    {
        var p = await NewPlayer(diamonds: 2500);
        long counterBefore = _uniforms.Counter;
        var b = await Ok(await Pull(p));

        Assert.That(b.EnumerateObject().Select(x => x.Name), Is.EqualTo(new[]
        {
            "requestId", "banner", "count", "pulls", "pityBefore", "pityAfter", "bannerLevelBefore", "bannerLevelAfter", "bannerExpAfter",
            "debits", "balances", "rateTableHash", "serverTime",
        }));
        Assert.That(b.GetProperty("count").GetInt32(), Is.EqualTo(10));
        Assert.That(b.GetProperty("pulls").GetArrayLength(), Is.EqualTo(10));
        Assert.That(b.GetProperty("pityBefore").GetInt32(), Is.Zero);
        Assert.That(b.GetProperty("bannerExpAfter").GetInt64(), Is.EqualTo(10));
        Assert.That(b.GetProperty("serverTime").GetInt64(), Is.EqualTo(1_793_670_900));
        Assert.That(b.GetProperty("debits")[0].GetProperty("currency").GetString(), Is.EqualTo("DIAMOND"));
        Assert.That(b.GetProperty("debits")[0].GetProperty("amount").GetInt64(), Is.EqualTo(-1000));
        Assert.That(b.GetProperty("balances").GetProperty("DIAMOND").GetInt64(), Is.EqualTo(1500));
        Assert.That(b.GetProperty("balances").GetProperty("TICKET_SUMMON_WEAPON").GetInt64(), Is.Zero);
        Assert.That(_uniforms.Counter - counterBefore, Is.EqualTo(30), "exactly 30 uniforms per 10-pull (acceptance 3)");

        // The oracle: the Sim's algorithm on the same uniforms, with the starter weapon every account owns.
        var def = Db.Banners[Banner.WEAPON];
        var st = new BannerState();
        var col = new CollectionState();
        foreach (var g in Db.ProfileCreatedGrants.Where(g => g.ItemKind == "WEAPON")) col.Items[g.ItemId] = new OwnedItem { Copies = (int)g.Amount };
        var expected = PullAlgorithm.Pull10(Db, def, st, col, new StreamUniformSource(Seed, counterBefore));
        var pulls = b.GetProperty("pulls").EnumerateArray().ToArray();
        for (int i = 0; i < 10; i++)
        {
            Assert.That(pulls[i].GetProperty("itemId").GetString(), Is.EqualTo(expected[i].ItemId), $"slot {i + 1}");
            Assert.That(pulls[i].GetProperty("rarity").GetString(), Is.EqualTo(RarityCodes.Of(expected[i].Rarity)));
            Assert.That(pulls[i].GetProperty("isNew").GetBoolean(), Is.EqualTo(expected[i].IsNew));
            Assert.That(pulls[i].GetProperty("copiesAfter").GetInt32(), Is.EqualTo(expected[i].CopiesAfter));
            Assert.That(pulls[i].GetProperty("levelBefore").GetInt32(), Is.EqualTo(expected[i].LevelBefore));
            Assert.That(pulls[i].GetProperty("levelAfter").GetInt32(), Is.EqualTo(expected[i].LevelAfter));
            Assert.That(pulls[i].GetProperty("guaranteeApplied").GetBoolean(), Is.EqualTo(expected[i].GuaranteeApplied));
        }
        Assert.That(b.GetProperty("pityAfter").GetInt32(), Is.EqualTo(st.Pity));
        Assert.That(b.GetProperty("bannerLevelAfter").GetInt32(), Is.EqualTo(def.LevelFor(st.Exp)));
        Assert.That(b.GetProperty("rateTableHash").GetString(), Is.EqualTo(RateTableHash.Hex(RateTableHash.Of(def, def.LevelFor(st.Exp)))));

        Assert.That(await LogRows(p), Is.EqualTo(10));
        Assert.That(await Diamonds(p), Is.EqualTo(1500));
        Assert.That(await _db.CountAsync("select count(*) from diamond_ledger where player_id = @p and reason = 'SUMMON' and delta = -1000 and ref_type = 'summon'",
                                         ("p", p.Id)), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select pity from banner_pity where player_id = @p and banner = 'WEAPON'", ("p", p.Id)), Is.EqualTo(st.Pity));
        Assert.That(await _db.CountAsync("select exp from banner_pity where player_id = @p and banner = 'WEAPON'", ("p", p.Id)), Is.EqualTo(10));
        Assert.That(await _db.CountAsync("select total_pulls from banner_pity where player_id = @p and banner = 'WEAPON'", ("p", p.Id)), Is.EqualTo(10));
        Assert.That(await _db.CountAsync("select count(distinct batch_index) from summon_log where player_id = @p and payment = 'DIAMOND'", ("p", p.Id)), Is.EqualTo(10));

        // A second pull continues from the server's own state: the collection (isNew, copies) and pity carry over.
        long counter2 = _uniforms.Counter;
        var b2 = await Ok(await Pull(p));
        var expected2 = PullAlgorithm.Pull10(Db, def, st, col, new StreamUniformSource(Seed, counter2));
        Assert.That(b2.GetProperty("pityBefore").GetInt32(), Is.EqualTo(b.GetProperty("pityAfter").GetInt32()));
        var pulls2 = b2.GetProperty("pulls").EnumerateArray().ToArray();
        for (int i = 0; i < 10; i++)
        {
            Assert.That(pulls2[i].GetProperty("itemId").GetString(), Is.EqualTo(expected2[i].ItemId));
            Assert.That(pulls2[i].GetProperty("isNew").GetBoolean(), Is.EqualTo(expected2[i].IsNew));
            Assert.That(pulls2[i].GetProperty("copiesAfter").GetInt32(), Is.EqualTo(expected2[i].CopiesAfter));
        }
    }

    [Test]
    public async Task A_retry_after_commit_replays_byte_identically_and_charges_once()   // acceptance 4
    {
        var p = await NewPlayer(diamonds: 1000);
        string id = NewId();
        using var first = await Pull(p, requestId: id);
        byte[] firstBytes = await first.Content.ReadAsByteArrayAsync();
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK), Encoding.UTF8.GetString(firstBytes));

        using var retry = await Pull(p, requestId: id);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(retry.Headers.GetValues(IdempotencyMiddleware.ReplayHeader).Single(), Is.EqualTo("true"));
        Assert.That(await retry.Content.ReadAsByteArrayAsync(), Is.EqualTo(firstBytes));
        Assert.That(JsonDocument.Parse(firstBytes).RootElement.GetProperty("requestId").GetString(), Is.EqualTo(id));

        Assert.That(await _db.CountAsync("select count(*) from diamond_ledger where player_id = @p and reason = 'SUMMON'", ("p", p.Id)), Is.EqualTo(1));
        Assert.That(await LogRows(p), Is.EqualTo(10));
        Assert.That(await _db.CountAsync("select count(*) from summon_log where request_id = @r", ("r", id)), Is.EqualTo(10));
    }

    [Test]
    public async Task Concurrent_pulls_never_roll_against_the_same_pity()   // acceptance 5
    {
        var p = await NewPlayer(diamonds: 100_000);
        var tasks = Enumerable.Range(0, 8).Select(_ => Pull(p)).ToArray();
        var responses = await Task.WhenAll(tasks);
        int ok = 0;
        foreach (var r in responses)
        {
            if (r.StatusCode == HttpStatusCode.OK) ok++;
            else await Error(r, HttpStatusCode.Conflict, ErrorCodes.IDEMPOTENCY_IN_FLIGHT);
            r.Dispose();
        }
        Assert.That(ok, Is.GreaterThanOrEqualTo(1));
        Assert.That(await LogRows(p), Is.EqualTo(10 * ok));
        Assert.That(await SummonCharges(p), Is.EqualTo(ok));

        // Each batch starts where the previous one ended: no two batches share a pity_before chain.
        var rows = new List<(string req, int idx, int before, int after)>();
        await using (var cmd = _db.DataSource.CreateCommand("select request_id, batch_index, pity_before, pity_after from summon_log where player_id = @p order by id"))
        {
            cmd.Parameters.AddWithValue("p", p.Id);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) rows.Add((rd.GetString(0), rd.GetInt16(1), rd.GetInt16(2), rd.GetInt16(3)));
        }
        Assert.That(rows.Select(r => r.req).Distinct().Count(), Is.EqualTo(ok));
        for (int i = 1; i < rows.Count; i++) Assert.That(rows[i].before, Is.EqualTo(rows[i - 1].after), $"row {i}");
    }

    [Test]
    public async Task Diamonds_while_holding_enough_tickets_is_PAYMENT_MISMATCH_and_charges_nothing()   // acceptance 6
    {
        var p = await NewPlayer(diamonds: 5000, weaponTickets: 10);
        var e = await Error(await Pull(p), HttpStatusCode.Conflict, SummonErrors.PAYMENT_MISMATCH);
        Assert.That(e.GetProperty("details").GetProperty("expected").GetString(), Is.EqualTo("TICKET"));
        Assert.That((await Diamonds(p), await Tickets(p), await LogRows(p)), Is.EqualTo((5000L, 10L, 0L)));

        // Tickets but too few diamonds is still 409, not 402 (issue #199: mismatch first).
        var q = await NewPlayer(diamonds: 50, weaponTickets: 1);
        await Error(await Pull(q, count: 1), HttpStatusCode.Conflict, SummonErrors.PAYMENT_MISMATCH);
        Assert.That(await SummonCharges(q), Is.Zero);

        // Fewer tickets than the count: diamonds are fine.
        var b = await Ok(await Pull(p, count: 10, payment: "TICKET"));
        Assert.That(b.GetProperty("debits")[0].GetProperty("currency").GetString(), Is.EqualTo("TICKET_SUMMON_WEAPON"));
        Assert.That(b.GetProperty("debits")[0].GetProperty("amount").GetInt64(), Is.EqualTo(-10));
        Assert.That(b.GetProperty("balances").GetProperty("TICKET_SUMMON_WEAPON").GetInt64(), Is.Zero);
        Assert.That(b.GetProperty("balances").GetProperty("DIAMOND").GetInt64(), Is.EqualTo(5000));
        Assert.That(await _db.CountAsync("select count(*) from summon_log where player_id = @p and payment = 'TICKET'", ("p", p.Id)), Is.EqualTo(10));
        await Ok(await Pull(p, count: 1, payment: "DIAMOND"));
        Assert.That(await Diamonds(p), Is.EqualTo(4900));
    }

    [Test]
    public async Task Too_few_diamonds_or_tickets_is_402_with_need_and_have_and_replays_as_itself()
    {
        var p = await NewPlayer(diamonds: 999);
        string id = NewId();
        var e = await Error(await Pull(p, requestId: id), HttpStatusCode.PaymentRequired, SummonErrors.INSUFFICIENT_FUNDS);
        Assert.That(e.GetProperty("details").GetProperty("need").GetInt64(), Is.EqualTo(1000));
        Assert.That(e.GetProperty("details").GetProperty("have").GetInt64(), Is.EqualTo(999));
        using var replay = await Pull(p, requestId: id);
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.PaymentRequired));
        Assert.That(replay.Headers.Contains(IdempotencyMiddleware.ReplayHeader), Is.True);

        await Error(await Pull(p, count: 1, payment: "TICKET"), HttpStatusCode.PaymentRequired, SummonErrors.INSUFFICIENT_FUNDS);
        Assert.That((await Diamonds(p), await LogRows(p)), Is.EqualTo((999L, 0L)));
    }

    [Test]
    public async Task The_kill_switch_is_503_SUMMON_DISABLED_and_is_not_stored()   // acceptance 17
    {
        var p = await NewPlayer(diamonds: 1000);
        _config.Switches = new SummonSwitches(new Dictionary<Banner, bool> { [Banner.WEAPON] = false }, Array.Empty<BannerWindow>());
        string id = NewId();
        await Error(await Pull(p, requestId: id), HttpStatusCode.ServiceUnavailable, SummonErrors.SUMMON_DISABLED);
        Assert.That(await LogRows(p), Is.Zero);
        _config.Switches = SummonSwitches.AllOpen;
        await Ok(await Pull(p, requestId: id));   // the same id rolls once the switch is lifted
    }

    [Test]
    public async Task Closed_banners_old_content_and_malformed_requests_are_refused()
    {
        var p = await NewPlayer(diamonds: 10_000);
        _config.Switches = new SummonSwitches(new Dictionary<Banner, bool>(),
                                              new[] { new BannerWindow(Banner.WEAPON, _clock.GetUtcNow().ToUnixTimeSeconds() + 60, null) });
        await Error(await Pull(p), HttpStatusCode.Conflict, SummonErrors.BANNER_CLOSED);
        _config.Switches = SummonSwitches.AllOpen;
        await Error(await Pull(p, banner: "RUNE"), HttpStatusCode.Conflict, SummonErrors.BANNER_CLOSED);   // disabled in content

        var old = await Error(await Pull(p, version: "2020.01.01"), HttpStatusCode.Conflict, ErrorCodes.CONTENT_TOO_OLD);
        Assert.That(old.GetProperty("details").GetProperty("minContentVersion").GetString(), Is.EqualTo(Version));
        await Error(await Pull(p, count: 5), HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        await Error(await Pull(p, payment: "GOLD"), HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        await Error(await Pull(p, banner: "weapon"), HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        await Error(await _client.SendAsync(Request(HttpMethod.Post, "/v1/summon/pull", new { banner = "WEAPON", count = 1, payment = "DIAMOND" }, p, NewId())),
                    HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        Assert.That((await Diamonds(p), await LogRows(p)), Is.EqualTo((10_000L, 0L)));
    }

    [Test]
    public async Task Pull_needs_a_token_for_this_device()
    {
        var p = await NewPlayer(diamonds: 1000);
        await Error(await Pull(p with { Device = Guid.NewGuid().ToString("D") }), HttpStatusCode.Forbidden, ErrorCodes.FORBIDDEN);
        await Error(await _client.SendAsync(Request(HttpMethod.Post, "/v1/summon/pull",
                                                    new { banner = "WEAPON", count = 1, payment = "DIAMOND", clientContentVersion = Version }, null, NewId())),
                    HttpStatusCode.Unauthorized, ErrorCodes.UNAUTHENTICATED);
    }

    // ---- state and history ----

    [Test]
    public async Task State_and_history_show_the_players_own_pulls()
    {
        var p = await NewPlayer(diamonds: 10_000, weaponTickets: 1);
        var pull = await Ok(await Pull(p, count: 1, payment: "TICKET"));
        await Ok(await Pull(p, count: 10, payment: "DIAMOND"));
        var other = await NewPlayer(diamonds: 1000);
        await Ok(await Pull(other));

        var state = await Ok(await _client.SendAsync(Request(HttpMethod.Get, "/v1/summon/state", null, p)));
        var banners = state.GetProperty("banners");
        Assert.That(banners.EnumerateObject().Select(x => x.Name), Is.SupersetOf(new[] { "WEAPON", "SKILL", "COMPANION" }));
        var w = banners.GetProperty("WEAPON");
        Assert.That(w.GetProperty("totalPulls").GetInt64(), Is.EqualTo(11));
        Assert.That(w.GetProperty("bannerExp").GetInt64(), Is.EqualTo(11));
        Assert.That(w.GetProperty("level").GetInt32(), Is.EqualTo(Db.Banners[Banner.WEAPON].LevelFor(11)));
        Assert.That(w.GetProperty("pity").GetInt32(), Is.EqualTo(await _db.CountAsync("select pity from banner_pity where player_id = @p and banner = 'WEAPON'", ("p", p.Id))));
        Assert.That(banners.GetProperty("SKILL").GetProperty("totalPulls").GetInt64(), Is.Zero);

        var history = await Ok(await _client.SendAsync(Request(HttpMethod.Get, "/v1/summon/history?banner=WEAPON&limit=100", null, p)));
        var rows = history.GetProperty("pulls").EnumerateArray().ToArray();
        Assert.That(rows.Length, Is.EqualTo(11));
        Assert.That(rows.Select(r => r.EnumerateObject().Select(x => x.Name)).First(), Is.EquivalentTo(new[]
        {
            "requestId", "banner", "batchIndex", "itemId", "rarity", "pityBefore", "pityAfter", "bannerLevel", "guarantee", "payment", "createdAt",
        }));
        var oldest = rows[^1];
        Assert.That(oldest.GetProperty("payment").GetString(), Is.EqualTo("TICKET"));
        Assert.That(oldest.GetProperty("itemId").GetString(), Is.EqualTo(pull.GetProperty("pulls")[0].GetProperty("itemId").GetString()));
        Assert.That(oldest.GetProperty("rarity").GetString(), Is.EqualTo(pull.GetProperty("pulls")[0].GetProperty("rarity").GetString()));

        var limited = await Ok(await _client.SendAsync(Request(HttpMethod.Get, "/v1/summon/history?limit=3", null, p)));
        Assert.That(limited.GetProperty("pulls").GetArrayLength(), Is.EqualTo(3));
        var none = await Ok(await _client.SendAsync(Request(HttpMethod.Get, "/v1/summon/history?banner=SKILL", null, p)));
        Assert.That(none.GetProperty("pulls").GetArrayLength(), Is.Zero);
        await Error(await _client.SendAsync(Request(HttpMethod.Get, "/v1/summon/history?limit=0", null, p)), HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
        await Error(await _client.SendAsync(Request(HttpMethod.Get, "/v1/summon/history?banner=NOPE", null, p)), HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
    }

    // ---- the audit log ----

    [Test]
    public async Task Ten_thousand_logged_pulls_reproduce_from_their_rate_table_hash_pity_and_uniforms_within_the_latency_target()   // acceptance 7
    {
        // CSPRNG draws here, as in production: the replay must not lean on the seeded stream.
        using var csprng = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("HeroCat:Database:ConnectionString", _db.ConnectionString);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(_clock);
                s.AddSingleton(new Lazy<ServerKeyRing>(_keys));
                s.AddSingleton<IRateLimitStore>(new NoRateLimits());
                s.AddSingleton<IMaintenanceFlag>(new InMemoryMaintenanceFlag());
            });
        });
        _client.Dispose();
        _client = csprng.CreateClient();

        var players = new List<Player>();
        for (int i = 0; i < 4; i++) players.Add(await NewPlayer(diamonds: 1_000_000));
        var latencies = new List<double>();
        for (int batch = 0; batch < 1000; batch++)
        {
            var p = players[batch % players.Count];
            string banner = batch % 2 == 0 ? "WEAPON" : "SKILL";
            var sw = Stopwatch.StartNew();
            using var r = await Pull(p, banner: banner);
            sw.Stop();
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), await r.Content.ReadAsStringAsync());
            if (batch >= 20) latencies.Add(sw.Elapsed.TotalMilliseconds);
        }

        var byHash = SummonEngineTests.HashIndex(Db);
        int n = 0;
        await using (var cmd = _db.DataSource.CreateCommand(
            "select banner, item_id, rarity, pity_before, pity_after, banner_level, guarantee, rate_table_hash, u1, u2, u3 from summon_log " +
            "where player_id = any(@ps)"))
        {
            cmd.Parameters.AddWithValue("ps", players.Select(x => x.Id).ToArray());
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var (def, level) = byHash[RateTableHash.Hex((byte[])rd[7])];
                Assert.That(def.Id.ToString(), Is.EqualTo(rd.GetString(0)));
                Assert.That(level, Is.EqualTo(rd.GetInt16(5)));
                var replay = SummonEngine.Replay(Db, def, rd.GetInt16(3), level, rd.GetBoolean(6), rd.GetDouble(8), rd.GetDouble(9), rd.GetDouble(10));
                Assert.That(RarityCodes.Of(replay.Rarity), Is.EqualTo(rd.GetString(2).Trim()));
                Assert.That(replay.ItemId, Is.EqualTo(rd.GetString(1)));
                Assert.That(replay.PityAfter, Is.EqualTo(rd.GetInt16(4)));
                n++;
            }
        }
        Assert.That(n, Is.EqualTo(10_000));

        latencies.Sort();
        double p99 = latencies[(int)Math.Ceiling(latencies.Count * 0.99) - 1];
        TestContext.Progress.WriteLine($"summon pull latency p50 {latencies[latencies.Count / 2]:F1} ms, p99 {p99:F1} ms");
        Assert.That(p99, Is.LessThan(150), "specs/08 section 4.3: p99 under 150 ms at low load");
    }
}
