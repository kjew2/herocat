using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using HeroCat.Bots;
using HeroCat.Bots.Acceptance;
using HeroCat.Bots.Golden;
using HeroCat.Bots.Measure;
using HeroCat.Bots.Policies;
using HeroCat.Bots.Report;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Bots.Tests
{
    internal static class Content
    {
        static readonly Lazy<string> _dir = new Lazy<string>(() =>
            FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory)
            ?? throw new InvalidOperationException("content/manifest.json not found above the test directory"));

        static readonly Lazy<ContentDb> _db = new Lazy<ContentDb>(() => ContentLoader.LoadFromDirectory(_dir.Value, Tier.M0));

        public static string Dir => _dir.Value;
        public static ContentDb Db => _db.Value;
    }

    /// <summary>Counts calls; spends nothing.</summary>
    sealed class CountingPolicy : IBotPolicy
    {
        public int Decisions, DayStarts;
        public string Name => "counting";
        public void OnDayStart(BotContext ctx) => DayStarts++;
        public void Decide(BotContext ctx) => Decisions++;
    }

    public class BotRunnerTests
    {
        [Test]
        public void OneDay_Schedule()
        {
            var p = new CountingPolicy();
            long ticks = 0;
            var run = new BotRunner(Content.Db) { AfterDay = (d, ctx) => ticks = ctx.Sim.Tick }.Run(p, 7, 1);
            int k = Content.Db.Constants.TickHz;
            Assert.That(p.Decisions, Is.EqualTo(HarnessConstants.ActiveMinutesPerDay * 60 / HarnessConstants.DecisionIntervalSeconds));
            Assert.That(p.DayStarts, Is.EqualTo(1));
            Assert.That(ticks, Is.EqualTo(HarnessConstants.ActiveMinutesPerDay * 60L * k));
            Assert.That(run.Rows.Single().Day, Is.EqualTo(1));
        }

        [Test]
        public void IdleDays_RollTheDay_AndPayOneLoginPerDay()
        {
            var run = new BotRunner(Content.Db).Run(new IdleBot(), 7, 3);
            var k = Content.Db.Constants;
            Assert.That(run.Rows.Select(r => r.Day), Is.EqualTo(new[] { 1, 2, 3 }));
            // tutorial grant + day 1 login at the start, then one login per night sync
            Assert.That(run.Rows.Select(r => r.Diamonds), Is.EqualTo(new[] { 1500L + 2 * k.LoginDiamonds, 1500L + 3 * k.LoginDiamonds, 1500L + 4 * k.LoginDiamonds }));
            Assert.That(run.Rows.All(r => r.Stage == 1), Is.True, "TODO(#81): no combat yet");
        }

        [Test]
        public void SameSeed_IdenticalTable_DifferentSeed_DifferentHash()
        {
            string a = TableWriter.Table(new BotRunner(Content.Db).Run(new IdleBot(), 7, 2), allDays: true);
            string b = TableWriter.Table(new BotRunner(Content.Db).Run(new IdleBot(), 7, 2), allDays: true);
            Assert.That(a, Is.EqualTo(b));
            var c = new BotRunner(Content.Db).Run(new IdleBot(), 8, 2);
            Assert.That(c.Rows[1].StateHash, Is.Not.EqualTo(new BotRunner(Content.Db).Run(new IdleBot(), 7, 2).Rows[1].StateHash), "RngRoot differs");
        }

        [Test]
        public void Sweep_IndependentOfParallelism()
        {
            var one = Sweep.Table(Sweep.Run(Content.Db, "idle", "1-4", 2, 1), "x");
            var many = Sweep.Table(Sweep.Run(Content.Db, "idle", "1-4", 2, 4), "x");
            Assert.That(many, Is.EqualTo(one));
        }

        [Test]
        public void Row_FormatsInvariant_EvenUnderGermanCulture()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var row = new DayRow(3, 120, 119, 40, 1234567.0, 1650, 0.5, 0, "abcdef0123456789");
                Assert.That(TableWriter.FormatRow(row), Does.Contain("1.23e+06"));
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }

        [Test]
        public void Header_AlignsWithRow()
        {
            var row = new DayRow(30, 550, 549, 120, 1e12, 99999, 1e5, 300, "abcdef0123456789");
            Assert.That(TableWriter.Header().Length, Is.EqualTo(TableWriter.FormatRow(row).Length));
        }

        [Test]
        public void Percentile_NearestRank()
        {
            var xs = Enumerable.Range(1, 20).ToList();
            Assert.That(Sweep.Percentile(xs, 0.5), Is.EqualTo(10));
            Assert.That(Sweep.Percentile(xs, 0.1), Is.EqualTo(2));
            Assert.That(Sweep.Percentile(xs, 0.9), Is.EqualTo(18));
        }

        [Test]
        public void SeedLists()
        {
            Assert.That(Sweep.ParseSeeds("1-3,9"), Is.EqualTo(new ulong[] { 1, 2, 3, 9 }));
            Assert.Throws<FormatException>(() => Sweep.ParseSeeds("3-1"));
            Assert.Throws<FormatException>(() => Sweep.ParseSeeds("0"));
        }
    }

    public class AcceptanceTests
    {
        [Test]
        public void StageTargets_ComeFromProgressTargets()
        {
            Assert.That(Content.Db.ProgressTargets.Select(t => (t.Day, t.Stage)),
                        Is.EqualTo(new[] { (1, 240), (3, 330), (7, 430), (14, 480), (30, 550) }));
            Assert.That(Content.Db.ProgressTargets.All(t => t.Tolerance == 0.2), Is.True);
        }

        [Test]
        public void Checker_ReportsAc1PerCheckDay_AndDeterminism()
        {
            var sw = Sweep.Run(Content.Db, "idle", "1-2", 7, 2);
            var r = AcceptanceChecker.Check(sw, Content.Db, AcceptanceTargets.LoadDefault(), () => null);
            Assert.That(r.Select(x => x.Id), Is.EqualTo(new[] { "AC1", "AC1", "AC6" }));
            Assert.That(r[0].Pass, Is.False, "the idle skeleton stays on stage 1 (TODO(#136))");
            Assert.That(r[2].Pass, Is.True);
        }

        [Test]
        public void SaveFixture_IsDeterministic()
        {
            Assert.That(SaveFixture.Generate(Content.Db), Is.EqualTo(SaveFixture.Generate(Content.Db)));
        }
    }

    [Category("Golden")]
    public class GoldenTests
    {
        [TestCaseSource(nameof(Names))]
        public void MatchesCommittedGolden(string fileName)
        {
            var e = GoldenFiles.All.Single(x => x.FileName == fileName);
            string diff = GoldenFiles.Compare(GoldenFiles.DefaultDir(Content.Dir), e, e.Generate(Content.Db));
            Assert.That(diff, Is.Null, diff);
        }

        static string[] Names => GoldenFiles.All.Select(e => e.FileName).ToArray();
    }
}
