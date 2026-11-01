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
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Bots.Tests
{
    internal static class Content
    {
        static readonly Lazy<string> _dir = new Lazy<string>(() =>
            FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory)
            ?? throw new InvalidOperationException("Content/manifest.json not found above the test directory"));

        static readonly Lazy<ContentDb> _db = new Lazy<ContentDb>(() => ContentDb.LoadFromDirectory(_dir.Value));

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
            long ticks = 0; int credits = 0;
            var runner = new BotRunner(Content.Db) { AfterDay = (d, ctx) => { ticks = ctx.State.Tick; credits = ctx.Stats.OfflineCredits; } };
            var run = runner.Run(p, 7, 1);
            int k = Content.Db.Constants.TickHz;
            Assert.That(p.Decisions, Is.EqualTo(HarnessConstants.ActiveMinutesPerDay * 60 / HarnessConstants.DecisionIntervalSeconds));
            Assert.That(p.DayStarts, Is.EqualTo(1));
            Assert.That(ticks, Is.EqualTo(HarnessConstants.ActiveMinutesPerDay * 60L * k));
            Assert.That(credits, Is.EqualTo(1));
            Assert.That(run.DayEndUtcMs[0], Is.EqualTo((HarnessConstants.Epoch + TimeSpan.FromDays(1)).ToUnixTimeMilliseconds()));
        }

        [Test]
        public void ThreeDays_RowsAndNonDecreasingStage()
        {
            var run = new BotRunner(Content.Db).Run(new GreedyF2PBot(), 7, 3);
            Assert.That(run.Rows.Select(r => r.Day), Is.EqualTo(new[] { 1, 2, 3 }));
            for (int i = 1; i < 3; i++) Assert.That(run.Rows[i].Stage, Is.GreaterThanOrEqualTo(run.Rows[i - 1].Stage));
            Assert.That(run.Rows[2].Stage, Is.GreaterThan(50));
        }

        [Test]
        public void Whale_PullsHundredExtraOnDayOne()
        {
            int pullsAfterDay1 = 0;
            var runner = new BotRunner(Content.Db) { AfterDay = (d, ctx) => { if (d == 1) pullsAfterDay1 = (int)ctx.State.HeroBanner.TotalPulls; } };
            runner.Run(new WhaleBot(), 7, 1);
            var banner = Content.Db.HeroBanner;
            Assert.That(pullsAfterDay1, Is.GreaterThanOrEqualTo(banner.FreePullsPerDay + WhaleBot.Day1ExtraPulls));
        }
    }

    public class DeterminismTests
    {
        [Test]
        public void SameSeed_IdenticalTable()
        {
            string a = TableWriter.Table(new BotRunner(Content.Db).Run(new GreedyF2PBot(), 7, 10), allDays: true);
            string b = TableWriter.Table(new BotRunner(Content.Db).Run(new GreedyF2PBot(), 7, 10), allDays: true);
            Assert.That(b, Is.EqualTo(a));
            string c = TableWriter.Table(new BotRunner(Content.Db).Run(new GreedyF2PBot(), 8, 10), allDays: true);
            Assert.That(c, Is.Not.EqualTo(a), "different seeds should differ");
        }

        [Test]
        public void Sweep_IndependentOfParallelism()
        {
            string tag = TableWriter.ContentTag(Content.Db.Version, Content.Db.ContentHash);
            string one = Sweep.Table(Sweep.Run(Content.Db, "greedy-f2p", "1-6", 5, 1), tag);
            string many = Sweep.Table(Sweep.Run(Content.Db, "greedy-f2p", "1-6", 5, 8), tag);
            Assert.That(many, Is.EqualTo(one));
        }
    }

    public class ReportTests
    {
        static readonly DayRow Row = new DayRow(7, 371, 1.14e12, 300, 16.0, 6.18e8, 3.4, 3.375, 42, 0, 70, 3.1);

        [Test]
        public void Row_FormatsInvariant_EvenUnderGermanCulture()
        {
            const string expected = "  7   371 1.14e+12     300    16.00   6.18e+08      3.40      3.38    42   0        70         3.10";
            Assert.That(TableWriter.FormatRow(Row), Is.EqualTo(expected));
            var old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.That(TableWriter.FormatRow(Row), Is.EqualTo(expected));
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        }

        [Test]
        public void Header_AlignsWithRow()
        {
            Assert.That(TableWriter.Header().Length, Is.EqualTo(TableWriter.FormatRow(Row).Length));
            Assert.That(TableWriter.Header().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, Is.EqualTo(12));
        }

        [Test]
        public void Shares_SumToOne()
        {
            var row = Row with { Dps = 10 * 16.0 * 6.18e8 * 3.4 * 3.375 };   // consistent with the multipliers
            var s = LayerShares.Of(row, 10);
            Assert.That(s.Stat + s.Gear + s.Hero + s.Class + s.Base, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(s.Stat, Is.EqualTo(Math.Log(16.0) / Math.Log(row.Dps)).Within(1e-12));
        }
    }

    public class SweepTests
    {
        [Test]
        public void Percentile_NearestRank()
        {
            var v = Enumerable.Range(1, 20).ToList();
            Assert.That(Sweep.Percentile(v, 0.1), Is.EqualTo(2));
            Assert.That(Sweep.Percentile(v, 0.5), Is.EqualTo(10));
            Assert.That(Sweep.Percentile(v, 0.9), Is.EqualTo(18));
            Assert.That(Sweep.Percentile(new[] { 42 }, 0.5), Is.EqualTo(42));
            Assert.That(Sweep.Percentile(new[] { 1, 2, 3 }, 1.0), Is.EqualTo(3));
        }

        [Test]
        public void SeedLists()
        {
            Assert.That(Sweep.ParseSeeds("1-20"), Is.EqualTo(Enumerable.Range(1, 20).Select(i => (ulong)i).ToArray()));
            Assert.That(Sweep.ParseSeeds("3,5,9"), Is.EqualTo(new ulong[] { 3, 5, 9 }));
            Assert.That(Sweep.ParseSeeds("7"), Is.EqualTo(new ulong[] { 7 }));
            Assert.Throws<FormatException>(() => Sweep.ParseSeeds("0-5"));
            Assert.Throws<FormatException>(() => Sweep.ParseSeeds("5-1"));
        }
    }

    public class AcceptanceSmokeTests
    {
        [Test]
        public void ShortSweep_ProducesEveryCheck_AndStructuralChecksPass()
        {
            var targets = AcceptanceTargets.LoadDefault();
            Assert.That(targets.StageTargets["30"], Is.EqualTo(550));
            var sw = Sweep.Run(Content.Db, "greedy-f2p", "1-4", 7, Environment.ProcessorCount);
            var results = AcceptanceChecker.Check(sw, targets, () => null);
            TestContext.WriteLine(string.Join("\n", results.Select(r => (r.Pass ? "PASS " : "FAIL ") + r.Id + " " + r.Message)));
            Assert.That(results.Select(r => r.Id).Distinct(), Is.EqualTo(new[] { "AC1", "AC2", "AC3", "AC4", "AC5", "AC6" }));
            Assert.That(results.Count(r => r.Id == "AC1"), Is.EqualTo(2), "days 1 and 7 checked; 30 is beyond a 7-day run");
            Assert.That(results.Single(r => r.Id == "AC5").Pass, Is.True);
            Assert.That(results.Single(r => r.Id == "AC6").Pass, Is.True);
        }

        [Test]
        public void Checker_FlagsOutOfRangeStage()
        {
            var targets = AcceptanceTargets.Parse(
                "{\"stageTargets\":{\"1\":5000},\"stageTolerance\":0.2,\"stageCheckDays\":[1],\"shareCheckDays\":[],\"shareMin\":0," +
                "\"shareMax\":1,\"firstWallMinStage\":0,\"firstWallMaxStage\":100000,\"maxWallHoursThroughDay\":1,\"maxWallHours\":1000," +
                "\"statLevelSlope\":100,\"statLevelIntercept\":0}");
            var sw = Sweep.Run(Content.Db, "greedy-f2p", "1-2", 1, 2);
            var ac1 = AcceptanceChecker.Check(sw, targets, null).Single(r => r.Id == "AC1");
            Assert.That(ac1.Pass, Is.False);
            StringAssert.Contains("target 5000", ac1.Message);
        }
    }

    [Category("Golden")]
    public class GoldenTests
    {
        [TestCaseSource(nameof(Files))]
        public void MatchesCommittedGolden(string fileName)
        {
            var e = GoldenFiles.All.Single(x => x.FileName == fileName);
            string diff = GoldenFiles.Compare(GoldenFiles.DefaultDir(Content.Dir), e, e.Generate(Content.Db));
            Assert.That(diff, Is.Null, diff);
        }

        static string[] Files() => GoldenFiles.All.Select(e => e.FileName).ToArray();
    }
}
