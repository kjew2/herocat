using System.Text;
using HeroCat.Server.Infrastructure.Idempotency;
using HeroCat.Server.Tests.Pipeline;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Idempotency;

/// <summary>
/// The idempotency store on the real <c>idempotency</c> table (specs/02 section 12, issue #194), directly and through
/// the pipeline. Runs only when HEROCAT_TEST_POSTGRES is set.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresIdempotencyTests
{
    static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_793_670_900);

    ScratchDatabase _db = null!;
    PostgresIdempotencyStore _store = null!;

    [OneTimeSetUp]
    public async Task Create()
    {
        _db = await ScratchDatabase.CreateAsync("herocat_idem");
        _store = new PostgresIdempotencyStore(_db.Factory, Ttl);
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_db != null) await _db.DisposeAsync();
    }

    static string Id() => Guid.NewGuid().ToString("N");

    static StoredResponse Response(string json, int status = 200) =>
        new(status, "application/json; charset=utf-8", new Dictionary<string, string> { ["ETag"] = "\"e\"" }, Encoding.UTF8.GetBytes(json));

    [Test]
    public async Task Begin_complete_replay_keeps_the_exact_bytes()
    {
        var player = Guid.NewGuid();
        string id = Id();
        Assert.That((await _store.BeginAsync(player, id, "post/x", "h1", T0, default)).Outcome, Is.EqualTo(IdempotencyOutcome.Started));
        Assert.That((await _store.BeginAsync(player, id, "post/x", "h1", T0, default)).Outcome, Is.EqualTo(IdempotencyOutcome.InFlight));
        Assert.That(await _db.CountAsync("select status_code from idempotency where player_id = @p and request_id = @r", ("p", player), ("r", id)), Is.Zero,
                    "in_flight is status_code 0");

        // Whitespace and key order a jsonb value would normalise away.
        const string json = "{ \"z\":1,  \"a\" : [ 1.50, \"\\u00e9\" ] }";
        await _store.CompleteAsync(player, id, Response(json, 201), default);
        var replay = await _store.BeginAsync(player, id, "post/x", "h1", T0.AddHours(1), default);
        Assert.That(replay.Outcome, Is.EqualTo(IdempotencyOutcome.Replay));
        Assert.That(replay.Response!.StatusCode, Is.EqualTo(201));
        Assert.That(Encoding.UTF8.GetString(replay.Response.Body), Is.EqualTo(json));
        Assert.That(replay.Response.Headers["ETag"], Is.EqualTo("\"e\""));
        Assert.That(replay.Response.ContentType, Is.EqualTo("application/json; charset=utf-8"));

        Assert.That((await _store.BeginAsync(player, id, "post/x", "h2", T0, default)).Outcome, Is.EqualTo(IdempotencyOutcome.Mismatch));
        Assert.That((await _store.BeginAsync(Guid.NewGuid(), id, "post/x", "h1", T0, default)).Outcome, Is.EqualTo(IdempotencyOutcome.OtherPlayer));
    }

    [Test]
    public async Task Abandon_frees_the_id_and_expired_rows_execute_fresh_then_purge()
    {
        var player = Guid.NewGuid();
        string id = Id();
        await _store.BeginAsync(player, id, "post/x", "h", T0, default);
        await _store.AbandonAsync(player, id, default);
        Assert.That((await _store.BeginAsync(player, id, "post/x", "h", T0, default)).Outcome, Is.EqualTo(IdempotencyOutcome.Started));
        await _store.CompleteAsync(player, id, Response("{}"), default);
        await _store.AbandonAsync(player, id, default);
        Assert.That((await _store.BeginAsync(player, id, "post/x", "h", T0, default)).Outcome, Is.EqualTo(IdempotencyOutcome.Replay), "abandon leaves a final row alone");

        var later = T0 + Ttl + TimeSpan.FromSeconds(1);
        Assert.That((await _store.BeginAsync(player, id, "post/x", "h", later, default)).Outcome, Is.EqualTo(IdempotencyOutcome.Started),
                    "00 decision 28: older than IDEMPOTENCY_TTL_H executes fresh");
        Assert.That((await _store.BeginAsync(Guid.NewGuid(), Id(), "post/x", "h", T0, default)).Outcome, Is.EqualTo(IdempotencyOutcome.Started));

        string old = Id();
        await _store.BeginAsync(player, old, "post/x", "h", T0 - TimeSpan.FromHours(30), default);
        int purged = await _store.PurgeAsync(later - Ttl, default);
        Assert.That(purged, Is.GreaterThanOrEqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from idempotency where request_id = @r", ("r", old)), Is.Zero);
        Assert.That(await _db.CountAsync("select count(*) from idempotency where request_id = @r", ("r", id)), Is.EqualTo(1), "the fresh row stays");
    }

    [Test]
    public async Task An_old_row_of_another_player_does_not_count_as_reuse()
    {
        string id = Id();
        await _store.BeginAsync(Guid.NewGuid(), id, "post/x", "h", T0, default);
        var later = T0 + Ttl + TimeSpan.FromMinutes(1);
        Assert.That((await _store.BeginAsync(Guid.NewGuid(), id, "post/x", "h", later, default)).Outcome, Is.EqualTo(IdempotencyOutcome.Started));
    }

    [Test]
    public async Task Concurrent_begins_with_one_id_start_once()
    {
        var player = Guid.NewGuid();
        string id = Id();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => _store.BeginAsync(player, id, "post/x", "h", T0, default)));
        Assert.That(outcomes.Count(o => o.Outcome == IdempotencyOutcome.Started), Is.EqualTo(1));
        Assert.That(outcomes.Count(o => o.Outcome == IdempotencyOutcome.InFlight), Is.EqualTo(15));
    }

    [Test]
    public async Task Completing_inside_the_handlers_transaction_commits_or_rolls_back_with_it()
    {
        var player = Guid.NewGuid();
        string id = Id();
        await _store.BeginAsync(player, id, "post/x", "h", T0, default);
        await using (var c = await _db.Factory.OpenAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await _store.CompleteAsync(c, tx, player, id, Response("{\"ok\":true}"), default);
            await tx.RollbackAsync();
        }
        Assert.That((await _store.BeginAsync(player, id, "post/x", "h", T0, default)).Outcome, Is.EqualTo(IdempotencyOutcome.InFlight), "rolled back with the handler");

        await using (var c = await _db.Factory.OpenAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await _store.CompleteAsync(c, tx, player, id, Response("{\"ok\":true}"), default);
            await tx.CommitAsync();
        }
        var replay = await _store.BeginAsync(player, id, "post/x", "h", T0, default);
        Assert.That(replay.Outcome, Is.EqualTo(IdempotencyOutcome.Replay));
        Assert.That(Encoding.UTF8.GetString(replay.Response!.Body), Is.EqualTo("{\"ok\":true}"));
    }

    [Test]
    public async Task Through_the_pipeline_a_replay_is_byte_identical_and_writes_once()
    {
        using var host = new PipelineHost { IdempotencyOverride = _store };
        using var client = host.Client();
        await IdempotencyReplay.AssertReplayAsync(client, id =>
        {
            var r = PipelineHost.Request(HttpMethod.Post, "/v1/_test/idem/write");
            r.Headers.Remove("X-Request-Id");
            r.Headers.Add("X-Request-Id", id);
            r.Content = new StringContent("{\"value\":\"pg\"}", Encoding.UTF8, "application/json");
            return r;
        }, () => Task.FromResult(Interlocked.Read(ref host.Probe.Writes)));
        Assert.That(host.Probe.Writes, Is.EqualTo(1));
        Assert.That(await _db.CountAsync("select count(*) from idempotency where player_id = @p and route = 'post/_test/idem/write'", ("p", PipelineHost.Player)),
                    Is.GreaterThanOrEqualTo(1));
    }
}
