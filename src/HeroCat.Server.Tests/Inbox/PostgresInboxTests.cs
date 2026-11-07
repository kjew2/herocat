using System.Net;
using System.Text;
using System.Text.Json;
using ApiClient = HeroCat.Remote.Api.ApiClient;
using ApiClientOptions = HeroCat.Remote.Api.ApiClientOptions;
using IApiCredentials = HeroCat.Remote.Api.IApiCredentials;
using HeroCat.Remote.Services;
using HeroCat.Runtime.Errors;
using HeroCat.Server.Features.Inbox;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Idempotency;
using HeroCat.Server.Tests.Pipeline;
using HeroCat.Sim.Economy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Inbox;

/// <summary>
/// The Inbox endpoints end to end (specs/08 section 3.2, acceptance 11; specs/06 section 9, acceptance 23; issue #201):
/// the real api with its pipeline over a scratch Postgres with schema v1, a fake clock and fake tokens. The last tests
/// drive the client's own <see cref="RemoteInboxService"/> against the server, so the wire shape is the one it reads.
/// Runs only when HEROCAT_TEST_POSTGRES is set.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresInboxTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_793_670_900);
    const string Device = PipelineHost.DeviceId;

    ScratchDatabase _db = null!;
    FakeClock _clock = null!;
    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;
    Guid _player;

    [OneTimeSetUp]
    public async Task CreateDatabase()
    {
        _db = await ScratchDatabase.CreateAsync("herocat_inbox");
        // A claim's currency_changed rows are stamped with the fake clock's day (specs/08 section 3.5), which may be outside
        // the migration's partition horizon.
        await _db.ExecAsync("select ensure_partitions('events', 'day', @t, @t)", ("t", T0));
    }

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_db != null) await _db.DisposeAsync();
    }

    [SetUp]
    public async Task StartApi()
    {
        _clock = new FakeClock(T0);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("HeroCat:Database:ConnectionString", _db.ConnectionString);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(_clock);
                s.AddSingleton<IAccessTokenValidator>(new FakeTokens());
                s.AddSingleton<IRateLimitStore>(new InMemoryRateLimitStore());
                s.AddSingleton<IMaintenanceFlag>(new InMemoryMaintenanceFlag());
            });
        });
        _client = _factory.CreateClient();
        _player = await NewPlayer();
    }

    [TearDown]
    public void StopApi()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---- fixtures ----

    async Task<Guid> NewPlayer()
    {
        var p = Guid.NewGuid();
        await _db.ExecAsync("insert into accounts (player_id, display_name, region) values (@p, 'Cat#TEST', 'US')", ("p", p));
        return p;
    }

    static string Diamonds(int n) => $"[{{\"kind\":\"currency\",\"id\":\"DIAMOND\",\"amount\":{n}}}]";

    /// <summary>Inserts an inbox row; times default to "created and available an hour before T0, never expires".</summary>
    async Task<Guid> Seed(string grants, string kind = "mail", Guid? player = null, DateTimeOffset? createdAt = null, DateTimeOffset? availableAt = null,
                          DateTimeOffset? expiresAt = null, string? sourceRef = null, string paramsJson = "{}")
    {
        var id = Guid.NewGuid();
        var created = createdAt ?? T0.AddHours(-1);
        await using var cmd = _db.DataSource.CreateCommand(
            "insert into inbox_items (inbox_item_id, player_id, kind, title_key, body_key, params, grants, source_ref, created_at, available_at, expires_at) " +
            "values (@id, @p, @kind, 'inbox.t', 'inbox.b', @params::jsonb, @grants::jsonb, @src, @created, @avail, @exp)");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("p", player ?? _player);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("params", paramsJson);
        cmd.Parameters.AddWithValue("grants", grants);
        cmd.Parameters.AddWithValue("src", (object?)sourceRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("created", created);
        cmd.Parameters.AddWithValue("avail", availableAt ?? created);
        cmd.Parameters.AddWithValue("exp", expiresAt.HasValue ? expiresAt.Value : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    HttpRequestMessage Req(HttpMethod m, string path, object? body = null, string? requestId = null, Guid? player = null)
    {
        var r = new HttpRequestMessage(m, path);
        r.Headers.Add(ApiHeaders.ClientVersion, PipelineHost.ClientVersion);
        r.Headers.Add(ApiHeaders.ContentVersion, PipelineHost.ContentVersion);
        r.Headers.Add(ApiHeaders.DeviceId, Device);
        r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", FakeTokens.For(player ?? _player, Device));
        if (m == HttpMethod.Post)
        {
            r.Headers.Add(ApiHeaders.RequestId, requestId ?? NewId());
            r.Content = new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json");
        }
        return r;
    }

    static string NewId() => Guid.NewGuid().ToString("N");

    Task<HttpResponseMessage> List(Guid? player = null) => _client.SendAsync(Req(HttpMethod.Get, "/v1/inbox", player: player));
    Task<HttpResponseMessage> Claim(object id, string? requestId = null) =>
        _client.SendAsync(Req(HttpMethod.Post, "/v1/inbox/claim", new { inboxItemId = id.ToString() }, requestId));
    Task<HttpResponseMessage> ClaimAll(string? requestId = null) => _client.SendAsync(Req(HttpMethod.Post, "/v1/inbox/claim-all", null, requestId));

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
        var b = JsonDocument.Parse(text).RootElement.Clone();
        Assert.That(b.GetProperty("code").GetString(), Is.EqualTo(code), text);
        return b;
    }

    Task<long> DiamondRows(Guid id) => _db.CountAsync("select count(*) from diamond_ledger where ref_type = 'inbox_item' and ref_id = @r", ("r", id.ToString("D")));
    Task<long> TicketRows(Guid id) => _db.CountAsync("select count(*) from ticket_ledger where ref_type = 'inbox_item' and ref_id = @r", ("r", id.ToString("D")));
    /// <summary>The server-side <c>currency_changed</c> rows of an item: (currency, delta, balanceAfter, reason), by currency then balance.</summary>
    async Task<(string, long, long, string)[]> ServerEvents(Guid id)
    {
        await using var cmd = _db.DataSource.CreateCommand(
            "select props->>'currency', (props->>'delta')::bigint, (props->>'balanceAfter')::bigint, props->>'reason' from events " +
            "where name = 'currency_changed' and source = 'server' and props->>'refId' = @r order by 1, 3");
        cmd.Parameters.AddWithValue("r", id.ToString("D"));
        var rows = new List<(string, long, long, string)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3)));
        return rows.ToArray();
    }

    Task<long> Claimed(Guid id) => _db.CountAsync("select count(*) from inbox_items where inbox_item_id = @i and claimed_at is not null", ("i", id));
    Task<long> DiamondBalance() => _db.CountAsync("select coalesce(sum(delta), 0) from diamond_ledger where player_id = @p", ("p", _player));

    static string[] Ids(JsonElement array, string prop = "inboxItemId") => array.EnumerateArray().Select(e => e.GetProperty(prop).GetString()!).ToArray();

    // ---- GET /inbox ----

    [Test]
    public async Task List_returns_unclaimed_available_unexpired_rows_oldest_first_in_the_spec_shape()
    {
        var iap = await Seed(Diamonds(2400), kind: "iap", createdAt: T0.AddHours(-2), paramsJson: "{\"sku\":\"dia_3\"}");
        var comp = await Seed("[{\"kind\":\"currency\",\"id\":\"DIAMOND\",\"amount\":300},{\"kind\":\"currency\",\"id\":\"TICKET_SUMMON_WEAPON\",\"amount\":5}]",
                              kind: "compensation", expiresAt: T0.AddDays(30));
        await Seed(Diamonds(1), expiresAt: T0);                                   // expired at now
        await Seed(Diamonds(1), availableAt: T0.AddHours(1));                     // future, no package: hidden
        await Seed(Diamonds(1), player: await NewPlayer());                       // someone else's
        var claimed = await Seed(Diamonds(1));
        await _db.ExecAsync("update inbox_items set claimed_at = now() where inbox_item_id = @i", ("i", claimed));

        var b = await Ok(await List());
        Assert.That(b.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "items", "serverTime" }));
        Assert.That(b.GetProperty("serverTime").GetInt64(), Is.EqualTo(T0.ToUnixTimeSeconds()));
        var items = b.GetProperty("items");
        Assert.That(Ids(items), Is.EqualTo(new[] { iap.ToString("D"), comp.ToString("D") }));

        var first = items[0];
        Assert.That(first.EnumerateObject().Select(p => p.Name),
                    Is.EquivalentTo(new[] { "inboxItemId", "kind", "titleKey", "bodyKey", "params", "grants", "createdAt", "expiresAt" }),
                    "availableAt only on an upcoming portion");
        Assert.That(first.GetProperty("kind").GetString(), Is.EqualTo("iap"));
        Assert.That(first.GetProperty("params").GetProperty("sku").GetString(), Is.EqualTo("dia_3"));
        Assert.That(first.GetProperty("expiresAt").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(first.GetProperty("createdAt").GetInt64(), Is.EqualTo(T0.AddHours(-2).ToUnixTimeSeconds()));
        var line = first.GetProperty("grants")[0];
        Assert.That((line.GetProperty("kind").GetString(), line.GetProperty("id").GetString(), line.GetProperty("amount").GetInt64()),
                    Is.EqualTo(("currency", "DIAMOND", 2400L)));
        Assert.That(items[1].GetProperty("expiresAt").GetInt64(), Is.EqualTo(T0.AddDays(30).ToUnixTimeSeconds()));
        Assert.That(items[1].GetProperty("grants").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task List_shows_only_the_next_future_portion_of_each_package()
    {
        string pkg = Guid.NewGuid().ToString("D");
        var today = await Seed(Diamonds(100), kind: "iap", sourceRef: pkg, createdAt: T0.AddMinutes(-1), availableAt: T0.AddMinutes(-1));
        var tomorrow = await Seed(Diamonds(100), kind: "iap", sourceRef: pkg, createdAt: T0.AddMinutes(-1), availableAt: T0.AddDays(1));
        await Seed(Diamonds(100), kind: "iap", sourceRef: pkg, createdAt: T0.AddMinutes(-1), availableAt: T0.AddDays(2));

        var items = (await Ok(await List())).GetProperty("items");
        Assert.That(Ids(items), Is.EqualTo(new[] { today.ToString("D"), tomorrow.ToString("D") }));
        Assert.That(items[1].GetProperty("availableAt").GetInt64(), Is.EqualTo(T0.AddDays(1).ToUnixTimeSeconds()));

        await Error(await Claim(tomorrow), HttpStatusCode.Conflict, InboxErrors.INBOX_NOT_AVAILABLE);
        Assert.That(await Claimed(tomorrow), Is.Zero);
    }

    // ---- POST /inbox/claim ----

    [Test]
    public async Task Claim_writes_the_ledgers_marks_the_row_and_returns_grants_balances_and_reason()
    {
        var id = await Seed("[{\"kind\":\"currency\",\"id\":\"DIAMOND\",\"amount\":300},{\"kind\":\"currency\",\"id\":\"TICKET_SUMMON_WEAPON\",\"amount\":5}," +
                            "{\"kind\":\"currency\",\"id\":\"ROCK_CANDY\",\"amount\":40}]", kind: "compensation");
        await _db.ExecAsync("insert into diamond_ledger (player_id, delta, reason) values (@p, 1000, 'LOGIN')", ("p", _player));
        string rid = NewId();

        var b = await Ok(await Claim(id, rid));
        Assert.That(b.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "inboxItemId", "grants", "balances", "reason", "serverTime" }));
        Assert.That(b.GetProperty("inboxItemId").GetString(), Is.EqualTo(id.ToString("D")));
        Assert.That(b.GetProperty("reason").GetString(), Is.EqualTo("MAIL"));
        Assert.That(b.GetProperty("grants").GetArrayLength(), Is.EqualTo(3));
        var balances = b.GetProperty("balances");
        Assert.That(balances.EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())),
                    Is.EquivalentTo(new[] { ("DIAMOND", 1300L), ("TICKET_SUMMON_WEAPON", 5L) }));

        Assert.That(await DiamondRows(id), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from diamond_ledger where ref_id = @r and delta = 300 and reason = 'MAIL' and request_id = @rid",
                                         ("r", id.ToString("D")), ("rid", rid)), Is.EqualTo(1));
        Assert.That(await _db.CountAsync(
            "select count(*) from ticket_ledger where ref_id = @r and ticket = 'TICKET_SUMMON_WEAPON' and delta = 5 and reason = 'MAIL' and request_id = @rid",
            ("r", id.ToString("D")), ("rid", rid)), Is.EqualTo(1));
        // One server-side currency_changed per ledger line (specs/08 section 3.5; issue #296); Rock Candy is the client's to report.
        Assert.That(await ServerEvents(id), Is.EqualTo(new[] { ("DIAMOND", 300L, 1300L, "MAIL"), ("TICKET_SUMMON_WEAPON", 5L, 5L, "MAIL") }));
        Assert.That(await _db.CountAsync("select count(*) from inbox_items where inbox_item_id = @i and claimed_request_id = @rid and claimed_at = @t",
                                         ("i", id), ("rid", rid), ("t", T0)), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from ticket_ledger where ticket = 'ROCK_CANDY'"), Is.Zero, "Rock Candy has no server ledger");

        Assert.That((await Ok(await List())).GetProperty("items").GetArrayLength(), Is.Zero, "a claimed item leaves the list");
    }

    [Test]
    public async Task Item_lines_are_claimed_and_returned_for_the_client_with_no_ledger_row()   // 00 decision 116, issue #271
    {
        var id = await Seed("[{\"kind\":\"item\",\"id\":\"weapon_m_01\",\"amount\":2},{\"kind\":\"currency\",\"id\":\"DIAMOND\",\"amount\":10}]",
                            kind: "admin");
        var b = await Ok(await Claim(id));
        var lines = b.GetProperty("grants").EnumerateArray().Select(l => (l.GetProperty("kind").GetString(), l.GetProperty("id").GetString(),
                                                                           l.GetProperty("amount").GetInt64())).ToList();
        Assert.That(lines, Is.EqualTo(new[] { ("item", "weapon_m_01", 2L), ("currency", "DIAMOND", 10L) }));
        Assert.That(await Claimed(id), Is.EqualTo(1));
        Assert.That(await DiamondRows(id), Is.EqualTo(1), "only the currency line has a ledger row");
        Assert.That(await _db.CountAsync("select count(*) from ticket_ledger where ref_id = @r", ("r", id.ToString("D"))), Is.Zero);
        Assert.That(await _db.CountAsync("select count(*) from granted_items where player_id = @p and item_id = 'weapon_m_01' and amount = 2 and source_ref = @r",
                                         ("p", _player), ("r", id.ToString("D"))), Is.EqualTo(1), "ITEM_WITHOUT_PULL's ledger (specs/08 section 7)");
    }

    [TestCase("iap", "IAP")]
    [TestCase("package_daily", "IAP")]
    [TestCase("refund", "REFUND")]
    [TestCase("admin", "ADMIN")]
    [TestCase("coupon", "MAIL")]
    public async Task Claim_reason_follows_the_kind_as_the_client_maps_it(string kind, string reason)
    {
        var id = await Seed(Diamonds(10), kind: kind);
        Assert.That((await Ok(await Claim(id))).GetProperty("reason").GetString(), Is.EqualTo(reason));
        Assert.That(await _db.CountAsync("select count(*) from diamond_ledger where ref_id = @r and reason = @reason", ("r", id.ToString("D")), ("reason", reason)),
                    Is.EqualTo(1));
        Assert.That(HeroCat.Runtime.Services.InboxRules.ReasonFor(kind.ToUpperInvariant()).ToString(), Is.EqualTo(reason), "the client's own mapping agrees");
    }

    [Test]
    public async Task Claiming_twice_is_409_INBOX_ALREADY_CLAIMED_with_the_original_grants_and_one_ledger_row()
    {
        // specs/08 acceptance 11.
        var id = await Seed(Diamonds(2400), kind: "iap");
        await Ok(await Claim(id));
        var e = await Error(await Claim(id), HttpStatusCode.Conflict, InboxErrors.INBOX_ALREADY_CLAIMED);
        var d = e.GetProperty("details");
        Assert.That(d.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "inboxItemId", "grants", "reason" }));
        Assert.That(d.GetProperty("inboxItemId").GetString(), Is.EqualTo(id.ToString("D")));
        Assert.That(d.GetProperty("reason").GetString(), Is.EqualTo("IAP"));
        Assert.That(d.GetProperty("grants")[0].GetProperty("amount").GetInt64(), Is.EqualTo(2400));
        Assert.That(await DiamondRows(id), Is.EqualTo(1));
        Assert.That(await DiamondBalance(), Is.EqualTo(2400));
    }

    [Test]
    public async Task A_replayed_requestId_returns_the_original_result_and_grants_nothing_twice()
    {
        // specs/06 acceptance 23.
        var id = await Seed(Diamonds(500));
        string rid = NewId();
        using var first = await Claim(id, rid);
        string firstBody = await first.Content.ReadAsStringAsync();
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK), firstBody);
        _clock.Advance(TimeSpan.FromMinutes(1));
        using var again = await Claim(id, rid);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(again.Headers.GetValues(IdempotencyMiddleware.ReplayHeader).Single(), Is.EqualTo("true"));
        Assert.That(await again.Content.ReadAsStringAsync(), Is.EqualTo(firstBody), "byte-identical");
        Assert.That(await DiamondRows(id), Is.EqualTo(1));
        Assert.That(await DiamondBalance(), Is.EqualTo(500));
    }

    [Test]
    public async Task Claim_refusals_write_nothing()
    {
        var other = await Seed(Diamonds(1), player: await NewPlayer());
        await Error(await Claim(other), HttpStatusCode.NotFound, InboxErrors.INBOX_NOT_FOUND);
        await Error(await Claim(Guid.NewGuid()), HttpStatusCode.NotFound, InboxErrors.INBOX_NOT_FOUND);
        await Error(await Claim("ibx_not_a_uuid"), HttpStatusCode.NotFound, InboxErrors.INBOX_NOT_FOUND);

        var expired = await Seed(Diamonds(1), expiresAt: T0);
        await Error(await Claim(expired), HttpStatusCode.Gone, InboxErrors.INBOX_EXPIRED);

        // An item line must name a weapon, skill or companion of the content, with a positive amount (00 decision 116).
        var item = await Seed("[{\"kind\":\"currency\",\"id\":\"DIAMOND\",\"amount\":5},{\"kind\":\"item\",\"id\":\"wpn_017\",\"amount\":1}]");
        await Error(await Claim(item), HttpStatusCode.Conflict, InboxErrors.INBOX_BAD_GRANT);
        var noCopies = await Seed("[{\"kind\":\"item\",\"id\":\"weapon_c_01\",\"amount\":0}]");
        await Error(await Claim(noCopies), HttpStatusCode.Conflict, InboxErrors.INBOX_BAD_GRANT);

        var bad = await Seed("[{\"kind\":\"currency\",\"id\":\"NOT_A_CURRENCY\",\"amount\":5}]");
        await Error(await Claim(bad), HttpStatusCode.Conflict, InboxErrors.INBOX_BAD_GRANT);
        var negative = await Seed(Diamonds(-5));
        await Error(await Claim(negative), HttpStatusCode.Conflict, InboxErrors.INBOX_BAD_GRANT);

        foreach (var id in new[] { other, expired, item, noCopies, bad, negative }) Assert.That(await Claimed(id), Is.Zero);
        Assert.That(await _db.CountAsync("select count(*) from diamond_ledger where player_id = @p", ("p", _player)), Is.Zero);
        Assert.That(await _db.CountAsync("select count(*) from granted_items where player_id = @p", ("p", _player)), Is.Zero);

        using var noBody = await _client.SendAsync(Req(HttpMethod.Post, "/v1/inbox/claim", new { }));
        await Error(noBody, HttpStatusCode.BadRequest, ErrorCodes.VALIDATION);
    }

    [Test]
    public async Task Concurrent_claims_of_one_item_with_different_requestIds_grant_once()
    {
        var id = await Seed(Diamonds(70));
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Claim(id)));
        Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.OK), Is.EqualTo(1));
        Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(5));
        Assert.That(await DiamondRows(id), Is.EqualTo(1));
        foreach (var r in responses) r.Dispose();
    }

    // ---- POST /inbox/claim-all ----

    [Test]
    public async Task Claim_all_writes_every_ticket_row_under_its_request_id_with_running_balances_in_the_events()
    {
        // Issue #294: several ticket rows of one request (two lines of one item, and two items) all carry the request id.
        string twoLines = "[{\"kind\":\"currency\",\"id\":\"TICKET_SUMMON_SKILL\",\"amount\":2},{\"kind\":\"currency\",\"id\":\"TICKET_SUMMON_SKILL\",\"amount\":3}]";
        var first = await Seed(twoLines, createdAt: T0.AddMinutes(-20));
        var second = await Seed("[{\"kind\":\"currency\",\"id\":\"TICKET_SUMMON_SKILL\",\"amount\":4}]", kind: "admin", createdAt: T0.AddMinutes(-10));
        await _db.ExecAsync("insert into ticket_ledger (player_id, ticket, delta, reason) values (@p, 'TICKET_SUMMON_SKILL', 10, 'QUEST')", ("p", _player));
        string rid = NewId();

        var b = await Ok(await ClaimAll(rid));
        Assert.That(b.GetProperty("balances").GetProperty("TICKET_SUMMON_SKILL").GetInt64(), Is.EqualTo(19));
        Assert.That(await _db.CountAsync("select count(*) from ticket_ledger where player_id = @p and request_id = @rid and ref_type = 'inbox_item'",
                                         ("p", _player), ("rid", rid)), Is.EqualTo(3));
        Assert.That(await ServerEvents(first), Is.EqualTo(new[] { ("TICKET_SUMMON_SKILL", 2L, 12L, "MAIL"), ("TICKET_SUMMON_SKILL", 3L, 15L, "MAIL") }));
        Assert.That(await ServerEvents(second), Is.EqualTo(new[] { ("TICKET_SUMMON_SKILL", 4L, 19L, "ADMIN") }));

        using var replay = await ClaimAll(rid);
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await _db.CountAsync("select count(*) from events where source = 'server' and props->>'refId' in (@a, @b)",
                                         ("a", first.ToString("D")), ("b", second.ToString("D"))), Is.EqualTo(3), "a replay writes no second event");
    }

    [Test]
    public async Task Claim_all_over_60_items_claims_the_50_oldest()
    {
        var ids = new List<Guid>();
        for (int i = 0; i < 60; i++) ids.Add(await Seed(Diamonds(10), createdAt: T0.AddMinutes(-120 + i)));

        var b = await Ok(await ClaimAll());
        Assert.That(b.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "claimed", "balances" }));
        var claimed = b.GetProperty("claimed");
        Assert.That(Ids(claimed), Is.EqualTo(ids.Take(50).Select(g => g.ToString("D"))), "oldest first");
        Assert.That(claimed[0].EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "inboxItemId", "grants", "reason" }));
        Assert.That(b.GetProperty("balances").GetProperty("DIAMOND").GetInt64(), Is.EqualTo(500));
        Assert.That(await DiamondBalance(), Is.EqualTo(500));
        foreach (var id in ids.Skip(50)) Assert.That(await Claimed(id), Is.Zero);

        var rest = await Ok(await ClaimAll());
        Assert.That(Ids(rest.GetProperty("claimed")), Is.EqualTo(ids.Skip(50).Select(g => g.ToString("D"))));
        Assert.That(rest.GetProperty("balances").GetProperty("DIAMOND").GetInt64(), Is.EqualTo(600));
    }

    [Test]
    public async Task Claim_all_skips_and_reports_an_item_that_expired_after_the_list()
    {
        var a = await Seed(Diamonds(10), createdAt: T0.AddMinutes(-3));
        var expiring = await Seed(Diamonds(20), createdAt: T0.AddMinutes(-2), expiresAt: T0.AddSeconds(5));
        var item = await Seed("[{\"kind\":\"item\",\"id\":\"wpn_017\",\"amount\":1}]", createdAt: T0.AddMinutes(-1));
        var tickets = await Seed("[{\"kind\":\"currency\",\"id\":\"TICKET_SUMMON_SKILL\",\"amount\":3}]", createdAt: T0.AddSeconds(-30));
        var longGone = await Seed(Diamonds(99), createdAt: T0.AddDays(-40), expiresAt: T0.AddDays(-10));
        Assert.That(Ids((await Ok(await List())).GetProperty("items")), Does.Contain(expiring.ToString("D")));

        _clock.Advance(TimeSpan.FromSeconds(10));
        var b = await Ok(await ClaimAll());
        var rows = b.GetProperty("claimed").EnumerateArray().ToList();
        Assert.That(rows.Select(r => r.GetProperty("inboxItemId").GetString()),
                    Is.EqualTo(new[] { a, expiring, item, tickets }.Select(g => g.ToString("D"))), "a row expired 10 days ago is not reported");
        Assert.That(rows[1].EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "inboxItemId", "code" }));
        Assert.That(rows[1].GetProperty("code").GetString(), Is.EqualTo(InboxErrors.INBOX_EXPIRED));
        Assert.That(rows[2].GetProperty("code").GetString(), Is.EqualTo(InboxErrors.INBOX_BAD_GRANT), "wpn_017 is not a content id");
        Assert.That(rows[3].GetProperty("reason").GetString(), Is.EqualTo("MAIL"));
        Assert.That(b.GetProperty("balances").EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())),
                    Is.EquivalentTo(new[] { ("DIAMOND", 10L), ("TICKET_SUMMON_SKILL", 3L) }));
        Assert.That(await Claimed(a) + await Claimed(tickets), Is.EqualTo(2));
        Assert.That(await Claimed(expiring) + await Claimed(item) + await Claimed(longGone), Is.Zero);
        Assert.That(await TicketRows(tickets), Is.EqualTo(1));
    }

    [Test]
    public async Task A_replayed_claim_all_grants_nothing_twice_and_an_empty_inbox_claims_nothing()
    {
        var empty = await Ok(await ClaimAll());
        Assert.That(empty.GetProperty("claimed").GetArrayLength(), Is.Zero);
        Assert.That(empty.GetProperty("balances").GetProperty("DIAMOND").GetInt64(), Is.Zero);

        var id = await Seed(Diamonds(42));
        string rid = NewId();
        using var first = await ClaimAll(rid);
        string body = await first.Content.ReadAsStringAsync();
        await Seed(Diamonds(1000));                                               // arrives after the first answer
        using var again = await ClaimAll(rid);
        Assert.That(again.Headers.GetValues(IdempotencyMiddleware.ReplayHeader).Single(), Is.EqualTo("true"));
        Assert.That(await again.Content.ReadAsStringAsync(), Is.EqualTo(body));
        Assert.That(await DiamondRows(id), Is.EqualTo(1));
        Assert.That(await DiamondBalance(), Is.EqualTo(42));
    }

    // ---- nightly purge ----

    [Test]
    public async Task The_purge_removes_unclaimed_rows_expired_past_the_report_window_only()
    {
        var old = await Seed(Diamonds(1), expiresAt: T0 - InboxStore.ExpiredReportWindow - TimeSpan.FromSeconds(1));
        var recent = await Seed(Diamonds(1), expiresAt: T0 - TimeSpan.FromMinutes(5));
        var live = await Seed(Diamonds(1), expiresAt: T0.AddDays(1));
        var never = await Seed(Diamonds(1), kind: "iap");
        var claimedOld = await Seed(Diamonds(1), expiresAt: T0.AddDays(-3));
        await _db.ExecAsync("update inbox_items set claimed_at = @t where inbox_item_id = @i", ("t", T0.AddDays(-4)), ("i", claimedOld));

        var purge = new InboxPurgeService(_db.Factory, _clock, NullLoggerFactory.Instance);
        Assert.That(await purge.PurgeOnceAsync(CancellationToken.None), Is.GreaterThanOrEqualTo(1));
        async Task<bool> Exists(Guid i) => await _db.CountAsync("select count(*) from inbox_items where inbox_item_id = @i", ("i", i)) == 1;
        Assert.That(await Exists(old), Is.False);
        Assert.That(await Exists(recent) && await Exists(live) && await Exists(never) && await Exists(claimedOld), Is.True);
    }

    // ---- the client's RemoteInboxService against this server ----

    sealed class TokenCredentials : IApiCredentials
    {
        public TokenCredentials(string token) => AccessToken = token;
        public string AccessToken { get; }
        public Task<bool> RefreshAsync(CancellationToken ct) => Task.FromResult(false);
    }

    RemoteInboxService Remote()
    {
        var api = new ApiClient(new ApiClientOptions
        {
            BaseUrl = "http://localhost/v1",
            Handler = _factory.Server.CreateHandler(),
            ClientVersion = PipelineHost.ClientVersion,
            ContentVersion = () => PipelineHost.ContentVersion,
            DeviceId = Device,
            Credentials = new TokenCredentials(FakeTokens.For(_player, Device)),
            Delay = (_, _) => Task.CompletedTask,
        });
        return new RemoteInboxService(api);
    }

    [Test]
    public async Task The_client_service_reads_the_list_claim_and_already_claimed_answers()
    {
        string pkg = Guid.NewGuid().ToString("D");
        var iap = await Seed(Diamonds(2400), kind: "iap", createdAt: T0.AddHours(-2), paramsJson: "{\"sku\":\"dia_3\"}");
        var comp = await Seed("[{\"kind\":\"currency\",\"id\":\"DIAMOND\",\"amount\":300},{\"kind\":\"currency\",\"id\":\"TICKET_SUMMON_WEAPON\",\"amount\":5}]",
                              kind: "compensation", expiresAt: T0.AddDays(30));
        var next = await Seed(Diamonds(100), kind: "iap", sourceRef: pkg, availableAt: T0.AddDays(1));
        var svc = Remote();

        var items = await svc.ListAsync();
        Assert.That(items.Select(i => i.Id), Is.EqualTo(new[] { iap, comp, next }.Select(g => g.ToString("D"))));
        Assert.That((items[0].Type, items[0].ExpiresAtServerSeconds, items[0].Args["sku"], items[0].Grant.Reason),
                    Is.EqualTo(("IAP", HeroCat.Runtime.Services.InboxRules.NeverExpires, "dia_3", CurrencyReason.IAP)));
        Assert.That(items[1].ExpiresAtServerSeconds, Is.EqualTo(T0.AddDays(30).ToUnixTimeSeconds()));
        Assert.That(items[1].Grant.Lines.Select(l => (l.Kind, l.Id, l.Amount)),
                    Is.EqualTo(new[] { ("currency", "DIAMOND", 300L), ("currency", "TICKET_SUMMON_WEAPON", 5L) }));
        long now = T0.ToUnixTimeSeconds();
        Assert.That(HeroCat.Runtime.Services.InboxRules.IsAvailable(items[0], now) && HeroCat.Runtime.Services.InboxRules.IsAvailable(items[1], now), Is.True);
        Assert.That(HeroCat.Runtime.Services.InboxRules.IsUpcoming(items[2], now), Is.True);

        var claim = await svc.ClaimAsync(NewId(), comp.ToString("D"));
        Assert.That(claim.AlreadyClaimed, Is.False);
        Assert.That((claim.Grant.InboxItemId, claim.Grant.Reason, claim.Grant.Lines.Length), Is.EqualTo((comp.ToString("D"), CurrencyReason.MAIL, 2)));

        var again = await svc.ClaimAsync(NewId(), comp.ToString("D"));
        Assert.That(again.AlreadyClaimed, Is.True);
        Assert.That((again.Grant.InboxItemId, again.Grant.Reason), Is.EqualTo((comp.ToString("D"), CurrencyReason.MAIL)));
        Assert.That(again.Grant.Lines.Select(l => l.Amount), Is.EqualTo(new[] { 300L, 5L }));

        var e = Assert.ThrowsAsync<ServiceException>(() => svc.ClaimAsync(NewId(), next.ToString("D")))!;
        Assert.That((e.Code, e.Kind), Is.EqualTo((InboxErrors.INBOX_NOT_AVAILABLE, ErrorClass.Rejected)));
        e = Assert.ThrowsAsync<ServiceException>(() => svc.ClaimAsync(NewId(), Guid.NewGuid().ToString("D")))!;
        Assert.That(e.Code, Is.EqualTo(InboxErrors.INBOX_NOT_FOUND));
        Assert.That(await DiamondRows(comp), Is.EqualTo(1));
    }

    [Test]
    public async Task The_client_service_reads_claim_all_rows_and_skips()
    {
        var a = await Seed(Diamonds(10), kind: "iap", createdAt: T0.AddMinutes(-2));
        var expiring = await Seed(Diamonds(20), createdAt: T0.AddMinutes(-1), expiresAt: T0.AddSeconds(5));
        _clock.Advance(TimeSpan.FromSeconds(10));

        var result = await Remote().ClaimAllAsync(NewId());
        Assert.That(result.Items.Select(i => (i.InboxItemId, i.Ok, i.ErrorCode)),
                    Is.EqualTo(new[] { (a.ToString("D"), true, (string?)null), (expiring.ToString("D"), false, InboxErrors.INBOX_EXPIRED) }));
        Assert.That(result.Items[0].Grant.Reason, Is.EqualTo(CurrencyReason.IAP));
        Assert.That(result.Items[0].Grant.Lines.Single().Amount, Is.EqualTo(10));
    }
}

/// <summary>The Inbox's pure rules, without a database.</summary>
[TestFixture]
public sealed class InboxUnitTests
{
    [Test]
    public void Grants_parse_08s_lines_and_reject_anything_else()
    {
        Assert.That(InboxStore.ParseGrants("[{\"kind\":\"currency\",\"id\":\"DIAMOND\",\"amount\":1200},{\"kind\":\"item\",\"id\":\"wpn_017\",\"amount\":1}]"),
                    Is.EqualTo(new[] { new InboxGrantLine("currency", "DIAMOND", 1200), new InboxGrantLine("item", "wpn_017", 1) }));
        Assert.That(InboxStore.ParseGrants("[]"), Is.Empty);
        Assert.That(InboxStore.ParseGrants("{}"), Is.Null);
        Assert.That(InboxStore.ParseGrants("[{\"kind\":\"currency\",\"id\":\"DIAMOND\",\"amount\":1.5}]"), Is.Null);
        Assert.That(InboxStore.ParseGrants("[{\"kind\":\"currency\",\"amount\":1}]"), Is.Null);
        Assert.That(InboxStore.ParseGrants("not json"), Is.Null);
    }

    [Test]
    public void Reasons_and_statuses_match_the_spec_and_the_client()
    {
        foreach (string kind in new[] { "iap", "package_daily", "mail", "compensation", "coupon", "refund", "admin" })
            Assert.That(InboxStore.ReasonFor(kind), Is.EqualTo(HeroCat.Runtime.Services.InboxRules.ReasonFor(kind.ToUpperInvariant()).ToString()), kind);
        Assert.That(InboxErrors.Status(InboxErrors.INBOX_NOT_FOUND), Is.EqualTo(404));
        Assert.That(InboxErrors.Status(InboxErrors.INBOX_EXPIRED), Is.EqualTo(410));
        Assert.That(InboxErrors.Status(InboxErrors.INBOX_ALREADY_CLAIMED), Is.EqualTo(409));
        Assert.That(InboxErrors.Status(InboxErrors.INBOX_NOT_AVAILABLE), Is.EqualTo(409));
        Assert.That(InboxErrors.INBOX_BAD_GRANT, Is.EqualTo(HeroCat.Sim.Commands.InboxRejectReasons.INBOX_BAD_GRANT));
        Assert.That(InboxStore.ClaimAllMax, Is.EqualTo(HeroCat.Runtime.Services.InboxRules.ClaimAllMax));
        Assert.That(InboxStore.LedgerTickets, Is.EquivalentTo(new[] { "TICKET_SUMMON_WEAPON", "TICKET_SUMMON_SKILL", "TICKET_SUMMON_COMPANION", "TICKET_SUMMON_RUNE" }));
    }
}
