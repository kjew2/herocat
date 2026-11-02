using System.Net;
using System.Text;
using System.Text.Json;
using HeroCat.Server.Features.Analytics;
using HeroCat.Server.Features.Auth;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Idempotency;
using HeroCat.Server.Tests.Pipeline;
using HeroCat.Server.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Analytics;

/// <summary>
/// <c>POST /analytics/events</c> end to end against a scratch PostgreSQL (specs/08 section 3.5; issue #209 acceptance), the
/// server-side economy events, and the worker's partition job (specs/02 section 12; issue #262). Runs only when
/// HEROCAT_TEST_POSTGRES is set.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresAnalyticsTests
{
    ScratchDatabase _db = null!;
    FakeClock _clock = null!;
    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;
    readonly ServerKeyRing _keys = ServerKeyRing.Ephemeral();

    [OneTimeSetUp]
    public async Task CreateDatabase() => _db = await ScratchDatabase.CreateAsync("herocat_analytics");

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_db != null) await _db.DisposeAsync();
    }

    [SetUp]
    public void StartApi()
    {
        // Real time (whole seconds): the migration created today's events partition from the database's now().
        _clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("HeroCat:Database:ConnectionString", _db.ConnectionString);
            b.UseSetting("HeroCat:Env", "test");
            b.UseSetting("HeroCat:Analytics:PlayerIdSalt", "test-salt");
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(_clock);
                s.AddSingleton(new Lazy<ServerKeyRing>(_keys));
                s.AddSingleton<IRateLimitStore>(new InMemoryRateLimitStore());
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

    string Token(Guid player, int ageGate = 2) =>
        _factory.Services.GetRequiredService<AccessTokenService>().Issue(player, Guid.NewGuid().ToString("D"), ageGate).Token;

    static string NewId() => Guid.NewGuid().ToString("N");

    Dictionary<string, object?> Event(string name = "currency_changed", object? props = null, long? ts = null, int ageGate = 0) => new()
    {
        ["name"] = name, ["eventId"] = NewId(), ["ts"] = ts ?? _clock.GetUtcNow().ToUnixTimeSeconds(), ["sessionId"] = NewId(),
        ["playerIdHash"] = "0000", ["deviceId"] = NewId(), ["clientVersion"] = "1.0.0", ["contentVersion"] = "2026.11.03",
        ["schemaVersion"] = 1, ["debugTainted"] = false, ["clockAuthoritative"] = true, ["dayIndex"] = 2, ["stage"] = 12, ["ageGate"] = ageGate,
        ["props"] = props ?? new { currency = "DIAMOND", delta = 50, balanceAfter = 640, reason = "QUEST" },
    };

    async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Raw)> Post(string token, string requestId, params object[] events)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/v1/analytics/events");
        r.Headers.Add(ApiHeaders.ClientVersion, PipelineHost.ClientVersion);
        r.Headers.Add(ApiHeaders.ContentVersion, PipelineHost.ContentVersion);
        r.Headers.Add(ApiHeaders.RequestId, requestId);
        r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        r.Content = new StringContent(JsonSerializer.Serialize(new { events }), Encoding.UTF8, "application/json");
        var resp = await _client.SendAsync(r);
        string text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text).RootElement.Clone(), resp);
    }

    Task<long> Stored(Guid player) =>
        _db.CountAsync("select count(*) from events where player_id_hash = @h", ("h", AnalyticsEventRules.HashPlayerId(player, "test-salt")));

    [Test]
    public async Task A_batch_is_stored_once_when_retried_with_the_same_request_id()
    {
        var player = Guid.NewGuid();
        string token = Token(player);
        var events = new object[] { Event(), Event(name: "summon", props: new { bannerId = "weapon", pulls = 10 }), Event() };
        string rid = NewId();

        var first = await Post(token, rid, events);
        Assert.That(first.Status, Is.EqualTo(HttpStatusCode.OK), first.Body.ToString());
        Assert.That(first.Body.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "accepted", "duplicates", "rejected", "serverTime" }));
        Assert.That(first.Body.GetProperty("accepted").GetInt32(), Is.EqualTo(3));
        Assert.That(await Stored(player), Is.EqualTo(3));

        var replay = await Post(token, rid, events);
        Assert.That(replay.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(replay.Raw.Headers.Contains("X-Idempotent-Replay"), Is.True);
        Assert.That(replay.Body.GetProperty("accepted").GetInt32(), Is.EqualTo(3), "the stored response replays");
        Assert.That(await Stored(player), Is.EqualTo(3));

        // The same events under another request id (the idempotency row expired, or a re-batched queue): per-event dedupe.
        var again = await Post(token, NewId(), events);
        Assert.That(again.Body.GetProperty("accepted").GetInt32(), Is.Zero);
        Assert.That(again.Body.GetProperty("duplicates").GetInt32(), Is.EqualTo(3));
        Assert.That(await Stored(player), Is.EqualTo(3));
    }

    [Test]
    public async Task A_reason_outside_the_closed_enum_is_rejected_per_event_not_per_batch()
    {
        var player = Guid.NewGuid();
        var bad = Event(props: new { currency = "DIAMOND", delta = 999, balanceAfter = 999, reason = "HACKED" });
        var stale = Event(ts: _clock.GetUtcNow().AddDays(-20).ToUnixTimeSeconds());   // in the age window, but no partition that day
        var r = await Post(Token(player), NewId(), Event(), bad, Event(name: "session_start"), stale, Event());
        Assert.That(r.Status, Is.EqualTo(HttpStatusCode.OK), r.Body.ToString());
        Assert.That(r.Body.GetProperty("accepted").GetInt32(), Is.EqualTo(2));
        var rejected = r.Body.GetProperty("rejected").EnumerateArray()
            .Select(x => (x.GetProperty("index").GetInt32(), x.GetProperty("eventId").GetString(), x.GetProperty("code").GetString())).ToList();
        Assert.That(rejected.Select(x => (x.Item1, x.Item3)), Is.EqualTo(new[]
        {
            (1, AnalyticsEventCodes.EVENT_REASON_INVALID), (2, AnalyticsEventCodes.EVENT_NAME_INVALID), (3, AnalyticsEventCodes.EVENT_TS_OUT_OF_RANGE),
        }));
        Assert.That(rejected[0].Item2, Is.EqualTo(bad["eventId"]));
        Assert.That(rejected[2].Item2, Is.EqualTo(stale["eventId"]));
        Assert.That(await Stored(player), Is.EqualTo(2));
        Assert.That(await _db.CountAsync("select count(*) from events where props->>'reason' = 'HACKED'"), Is.Zero);
    }

    [Test]
    public async Task Under_age_events_are_stored_with_age_gate_and_no_advertising_id()
    {
        var player = Guid.NewGuid();
        var e = Event(props: new { currency = "DIAMOND", delta = 50, balanceAfter = 640, reason = "IAP", advertisingId = "38400000-8cf0-11bd-b23e-10b96e40000d", gaid = "x" },
                      ageGate: 0);   // the client's value is ignored: the token says under-13
        var r = await Post(Token(player, ageGate: 1), NewId(), e);
        Assert.That(r.Body.GetProperty("accepted").GetInt32(), Is.EqualTo(1), r.Body.ToString());
        var h = ("h", (object)AnalyticsEventRules.HashPlayerId(player, "test-salt"));
        Assert.That(await _db.CountAsync("select count(*) from events where player_id_hash = @h and age_gate = 1", h), Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from events where player_id_hash = @h and (props ? 'advertisingId' or props ? 'gaid')", h), Is.Zero);
        Assert.That(await _db.CountAsync(
            "select count(*) from events where player_id_hash = @h and source = 'client' and env = 'test' and stage = 12 and day_index = 2 " +
            "and schema_version = 1 and client_version = '1.0.0' and clock_authoritative and not debug_tainted and event_id = @id::uuid",
            h, ("id", (string)e["eventId"]!)), Is.EqualTo(1));
    }

    [Test]
    public async Task Batch_shape_errors_are_validation()
    {
        var token = Token(Guid.NewGuid());
        var empty = await Post(token, NewId());
        Assert.That(empty.Status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(empty.Body.GetProperty("code").GetString(), Is.EqualTo(ErrorCodes.VALIDATION));
        var tooMany = await Post(token, NewId(), Enumerable.Range(0, 101).Select(_ => (object)Event()).ToArray());
        Assert.That(tooMany.Status, Is.EqualTo(HttpStatusCode.BadRequest));
        var full = await Post(token, NewId(), Enumerable.Range(0, 100).Select(_ => (object)Event()).ToArray());
        Assert.That(full.Body.GetProperty("accepted").GetInt32(), Is.EqualTo(100));
    }

    [Test]
    public async Task No_token_is_unauthenticated()
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/v1/analytics/events");
        r.Headers.Add(ApiHeaders.ClientVersion, PipelineHost.ClientVersion);
        r.Headers.Add(ApiHeaders.RequestId, NewId());
        r.Content = new StringContent("{\"events\":[]}", Encoding.UTF8, "application/json");
        using var resp = await _client.SendAsync(r);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Server_side_currency_changed_is_written_in_the_callers_transaction()
    {
        var events = _factory.Services.GetRequiredService<ServerEconomyEvents>();
        var player = Guid.NewGuid();
        var row = events.CurrencyChanged(player, accountAgeGate: 1, "DIAMOND", -300, 340, "SUMMON", "weapon");
        Assert.Throws<ArgumentException>(() => events.CurrencyChanged(player, 0, "DIAMOND", 1, 1, "BONUS", null));

        await using (var c = await _db.Factory.OpenAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            Assert.That(await ServerEconomyEvents.WriteAsync(c, tx, new[] { row }, CancellationToken.None), Is.EqualTo(1));
            await tx.RollbackAsync();
        }
        Assert.That(await Stored(player), Is.Zero, "rolled back with the ledger write");

        await using (var c = await _db.Factory.OpenAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await ServerEconomyEvents.WriteAsync(c, tx, new[] { row }, CancellationToken.None);
            await tx.CommitAsync();
        }
        Assert.That(await _db.CountAsync(
            "select count(*) from events where player_id_hash = @h and source = 'server' and age_gate = 1 and name = 'currency_changed' " +
            "and props->>'reason' = 'SUMMON' and (props->>'delta')::int = -300 and props->>'refId' = 'weapon'",
            ("h", AnalyticsEventRules.HashPlayerId(player, "test-salt"))), Is.EqualTo(1));
    }

    // ---- the worker's partition job (issue #262) ----

    PartitionMaintenanceService Job(DateTimeOffset now) =>
        new(_db.Factory, new FakeClock(now), Options.Create(new ServerOptions { Limits = new ServerLimits { PartitionAheadMonths = 3, PartitionAheadDays = 14 } }),
            NullLogger<PartitionMaintenanceService>.Instance);

    [Test]
    public async Task Partition_job_keeps_every_table_ahead_of_now_and_is_idempotent()
    {
        // A year after the migration: nothing covers that time until the job runs.
        var later = DateTimeOffset.UtcNow.AddYears(1);
        Assert.That(await _db.CountAsync($"select count(*) from pg_class where relname = 'events_p{later:yyyyMMdd}'"), Is.Zero);

        var first = await Job(later).EnsureOnceAsync(CancellationToken.None);
        Assert.That(first.Select(h => h.Table), Is.EqualTo(new[] { "diamond_ledger", "summon_log", "events" }));
        Assert.That(first.Single(h => h.Table == "diamond_ledger").Created, Is.EqualTo(4), "this month plus 3");
        Assert.That(first.Single(h => h.Table == "events").Created, Is.EqualTo(15), "today plus 14");
        Assert.That(first.All(h => !h.BelowOnePeriod));
        Assert.That(first.Single(h => h.Table == "summon_log").HorizonDays, Is.GreaterThanOrEqualTo(89));
        Assert.That(first.Single(h => h.Table == "events").HorizonDays, Is.GreaterThan(14));   // no upper bound: another test may have made later partitions
        Assert.That(PartitionMaintenanceService.LastPartitionEnds.Keys, Is.SupersetOf(new[] { "diamond_ledger", "summon_log", "events" }));

        var rerun = await Job(later).EnsureOnceAsync(CancellationToken.None);
        Assert.That(rerun.Sum(h => h.Created), Is.Zero);

        // A day later only the new last day is missing.
        var next = await Job(later.AddDays(1)).EnsureOnceAsync(CancellationToken.None);
        Assert.That(next.Single(h => h.Table == "events").Created, Is.EqualTo(1));

        // Rows at that time now insert: a ledger row at the far end of the horizon and an event today.
        var p = Guid.NewGuid();
        await _db.ExecAsync("insert into accounts (player_id, display_name, region) values (@p, 'Cat', 'JP')", ("p", p));
        await _db.ExecAsync("insert into diamond_ledger (player_id, delta, reason, created_at) values (@p, 1, 'ADMIN', @t)", ("p", p), ("t", later.AddMonths(3)));
        await _db.ExecAsync("insert into events (ts, name, player_id_hash, env) values (@t, 'currency_changed', '\\x00', 'test')", ("t", later));
        await _db.ExecAsync("delete from diamond_ledger where player_id = @p", ("p", p));
        await _db.ExecAsync("delete from accounts where player_id = @p", ("p", p));
    }

    [Test]
    public async Task Partition_job_reports_a_horizon_below_one_period_until_it_runs()
    {
        // Far ahead of every partition (the job has not run for years): measured as below one period, then repaired.
        var job = Job(DateTimeOffset.UtcNow.AddYears(3));
        var before = await job.MeasureAsync(CancellationToken.None);
        Assert.That(before.All(h => h.BelowOnePeriod && h.Created == 0 && h.HorizonDays < 0), string.Join(", ", before));
        var after = await job.EnsureOnceAsync(CancellationToken.None);
        Assert.That(after.All(h => !h.BelowOnePeriod), string.Join(", ", after));
    }
}
