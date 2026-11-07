using HeroCat.Server.Features.Summon;
using HeroCat.Server.Hosting;
using HeroCat.Server.Tests.Pipeline;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Summon;

/// <summary>
/// The realised-rate job against Postgres (specs/08 section 10.3, acceptance 22; issue #200): pulls rolled by the
/// server's pull path and written by the pull handler's own <c>summon_log</c> insert are counted over the trailing 30
/// days only, the honest log stays quiet, and one batch logged under a hash no content publishes alarms in one run.
/// Runs only when HEROCAT_TEST_POSTGRES is set.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresRateDriftTests
{
    sealed class CapturingAlarm : IRateDriftAlarm
    {
        public readonly List<RateDriftReport> Reports = new();
        public void Report(RateDriftReport report) => Reports.Add(report);
    }

    static readonly Lazy<IContentSets> Content = new(() => new DirectoryContentSets(Options.Create(new ContentSetOptions())));

    ScratchDatabase _db = null!;
    readonly Guid _player = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task CreateDatabase()
    {
        _db = await ScratchDatabase.CreateAsync("herocat_ratedrift");
        await _db.ExecAsync("select ensure_partitions('summon_log', 'month', now() - interval '3 months', now() + interval '1 month')");
        await _db.ExecAsync("insert into accounts (player_id, display_name, region) values (@p, 'drift', 'US')", ("p", _player));
    }

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_db != null) await _db.DisposeAsync();
    }

    RateDriftJob Job(CapturingAlarm alarm) =>
        new(_db.Factory, Content.Value, alarm, new FakeClock(DateTimeOffset.UtcNow.AddMinutes(1)),
            Options.Create(new ServerOptions { Database = new DatabaseOptions { ConnectionString = _db.ConnectionString } }),
            NullLogger<RateDriftJob>.Instance);

    async Task LogPullsAsync(SummonRoll roll, string requestId)
    {
        await using var c = await _db.Factory.OpenAsync(CancellationToken.None);
        await using var tx = await c.BeginTransactionAsync();
        await SummonStore.InsertLogAsync(c, tx, _player, requestId, Banner.WEAPON, SummonPayments.Diamond, roll, CancellationToken.None);
        await tx.CommitAsync();
    }

    [Test]
    public async Task The_job_counts_the_trailing_window_and_alarms_on_an_unpublished_table()
    {
        var db = Content.Value.Active;
        var def = db.Banners[Banner.WEAPON];
        var st = new BannerState();
        var col = new CollectionState();
        var rng = new StreamUniformSource(0x5EED_0200, 0);
        long logged = 0;
        for (int i = 0; i < 60; i++)
        {
            var roll = SummonEngine.Roll(db, def, st, col, rng, 10);
            await LogPullsAsync(roll, "honest-" + i);
            logged += roll.Results.Length;
        }
        // Forty days old: outside the window, and logged with a hash no content set publishes.
        var old = SummonEngine.Roll(db, def, new BannerState(), new CollectionState(), rng, 10);
        var forged = old with { Audits = old.Audits.Select(a => a with { RateTableHash = new byte[32] }).ToArray() };
        await LogPullsAsync(forged, "old");
        await _db.ExecAsync("update summon_log set created_at = now() - interval '40 days' where request_id = 'old'");

        var alarm = new CapturingAlarm();
        var report = await Job(alarm).RunOnceAsync(CancellationToken.None);
        Assert.That(alarm.Reports, Is.EqualTo(new[] { report }));
        Assert.That(report.UnknownTables, Is.Empty, "the 40-day-old rows are outside the 30-day window");
        Assert.That(report.Rarities.GroupBy(r => r.Level).Sum(g => g.First().Pulls), Is.EqualTo(logged));
        Assert.That(report.Alarm, Is.False, string.Join(", ", report.Drifted.Select(d => $"{d.Level} {d.Rarity}")));
        Assert.That(report.To - report.From, Is.EqualTo(TimeSpan.FromDays(30)));

        // One fresh batch rolled with a table nobody published raises RB-SUMMON-RATES in the next run.
        await LogPullsAsync(forged, "forged");
        var next = await Job(alarm).RunOnceAsync(CancellationToken.None);
        Assert.That(next.Alarm, Is.True);
        Assert.That(next.UnknownTables.Sum(u => u.Pulls), Is.EqualTo(10));
        Assert.That(next.UnknownTables.Select(u => u.RateTableHash).Distinct().Single(), Is.EqualTo(new string('0', 64)));
    }
}
