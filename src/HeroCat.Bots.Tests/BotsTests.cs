using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using HeroCat.Bots;
using HeroCat.Bots.Acceptance;
using HeroCat.Bots.Golden;
using HeroCat.Bots.Measure;
using HeroCat.Bots.Policies;
using HeroCat.Bots.Report;
using HeroCat.Runtime.Replay;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Fish;
using HeroCat.Sim.State;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Bots.Tests
{
    /// <summary>The two-session bot day (10 sections 3.2-3.3; issue #134).</summary>
    public class BotRunnerTests
    {
        [Test]
        public void Constants_EpochIsAMonday0900Utc_AndEachDaySumsTo24Hours()
        {
            var e = HarnessConstants.Epoch;
            Assert.That(e.DayOfWeek, Is.EqualTo(DayOfWeek.Monday));
            Assert.That((e.Hour, e.Minute, e.Offset), Is.EqualTo((9, 0, TimeSpan.Zero)));
            Assert.That(HarnessConstants.SessionsPerDay, Is.EqualTo(2));
            Assert.That(HarnessConstants.DecisionsPerSession, Is.EqualTo(60));
            int activeSec = HarnessConstants.ActiveMinutesPerDay * 60;
            foreach (int day in new[] { 1, 2 })
                Assert.That(HarnessConstants.SessionGapSeconds(day, 0) + HarnessConstants.SessionGapSeconds(day, 1) + activeSec, Is.EqualTo(86400), $"day {day}");
            Assert.That(HarnessConstants.PlayerIdFor(7), Is.Not.EqualTo(Guid.Empty));
            Assert.That(HarnessConstants.PlayerIdFor(7), Is.Not.EqualTo(HarnessConstants.PlayerIdFor(8)));
        }

        [Test]
        public void OneDay_TwoSessions_120Decisions_72000Ticks()
        {
            var p = new CountingPolicy();
            var run = new BotRunner(Content.Db).Run(p, 7, 1);
            Assert.That(p.SessionStarts, Is.EqualTo(2));
            Assert.That(p.SessionEnds, Is.EqualTo(2));
            Assert.That(p.Decisions, Is.EqualTo(120));
            Assert.That(run.FinalTick, Is.EqualTo(HarnessConstants.ActiveMinutesPerDay * 60L * Content.Db.Constants.TickHz));
            Assert.That(run.FinalTick, Is.EqualTo(72_000));
            Assert.That(run.Rows.Single().Day, Is.EqualTo(1));
            Assert.That(run.FinalServerSec, Is.EqualTo(BotRunner.EpochSeconds + 86400), "the day ends at the next day's 09:00");
        }

        [Test]
        public void ThirtyDays_ExerciseWeeklyKeys_DailyRollover_AndTheInSessionRollover()
        {
            var p = new CountingPolicy();
            var weekKeys = new HashSet<int>();
            new BotRunner(Content.Db) { AfterDay = (d, ctx) => weekKeys.Add(ctx.State.Time.WeekKey) }.Run(p, 7, 30);
            Assert.That(weekKeys.Count, Is.GreaterThanOrEqualTo(5), "30 days from a Monday cross at least four week boundaries");
            var morning = p.DayKeys.Where(x => x.session == 0).Select(x => x.keyAtStart).ToList();
            Assert.That(morning, Is.EqualTo(Enumerable.Range(morning[0], 30)), "one rollover a day: each morning session starts on the next day key");
            // Even days: the evening session (23:30-00:30) is open across 00:00 UTC; odd days: no session spans midnight.
            foreach (var (day, session, start, end) in p.DayKeys)
                Assert.That(end - start, Is.EqualTo(day % 2 == 0 && session == 1 ? 1 : 0), $"day {day} session {session}");
        }

        [Test]
        public void CatchUp_RunsTwicePerDay_AndNeverHitsTheOfflineCap()
        {
            var log = new ListCommandLogShim();
            new BotRunner(Content.Db) { CommandLog = log }.Run(new CountingPolicy(), 7, 4);
            var catchups = log.Records.OfType<ComputeOfflineCatchup>().ToList();
            Assert.That(catchups.Count, Is.EqualTo(2 * 4));
            long capSec = (long)(Content.Db.Constants.OfflineCapH * 3600);
            for (int i = 1; i < catchups.Count; i++)
            {
                long gap = catchups[i].NowServerSeconds - catchups[i - 1].NowServerSeconds;
                Assert.That(gap, Is.LessThan(capSec));
                Assert.That(gap, Is.GreaterThanOrEqualTo(8.5 * 3600 + 3600 - 1), "every gap is at least 8.5 h plus the 1 h session");
            }
        }

        [Test]
        public void Diamonds_ContentIsTheDefault_FlatIsBlockedUntilInboxGrantsLand()
        {
            Assert.That(new BotRunner(Content.Db).Diamonds, Is.EqualTo(DiamondMode.Content));
            Assert.That(new BotRunner(Content.Db).Run(new GreedyF2PBot(), 7, 1).Diamonds, Is.EqualTo(DiamondMode.Content));
            // TODO(#176): ApplyInboxGrant's body is MVP; --diamonds flat runs once it lands (then assert the daily grant here).
            var e = Assert.Throws<NotSupportedException>(() => new BotRunner(Content.Db) { Diamonds = DiamondMode.Flat }.Run(new GreedyF2PBot(), 7, 1));
            Assert.That(e.Message, Does.Contain("#176"));
        }

        [Test]
        public void SameSeedTwice_ByteIdentical_DifferentSeedDiffers()
        {
            string Table(ulong seed) => TableWriter.Table(new BotRunner(Content.Db).Run(new GreedyF2PBot(), seed, 2), allDays: true);
            Assert.That(Table(7), Is.EqualTo(Table(7)), "10 acceptance criterion 5");
            Assert.That(Table(8), Is.Not.EqualTo(Table(7)));
        }

        [Test]
        public void DayHashes_AreTakenAfterEachDaysFirstCatchUp()
        {
            var run = new BotRunner(Content.Db).Run(new CountingPolicy(), 3, 3);
            Assert.That(run.DayHashes.Select(h => h.Day), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(run.DayHashes.Select(h => h.Tick), Is.EqualTo(new[] { 0L, 72_000L, 144_000L }));
        }
    }

    /// <summary>A command log that keeps the records (ListCommandLog is Sim's; this one is just shorter to name).</summary>
    sealed class ListCommandLogShim : HeroCat.Sim.Replay.ICommandLog
    {
        public readonly List<Command> Records = new List<Command>();
        public void Append(Command c) => Records.Add(c);
    }

    /// <summary>Walls, pulls and diamonds (10 section 3.5).</summary>
    public class MeasureTests
    {
        [Test]
        public void Wall_RecordsWallClockHours_IncludingGaps_AndActiveMinutes_TicksOnly()
        {
            var ctx = BotRunner.CreateContext(Content.Db, 7);
            var sink = ctx.Sink;
            var t = ctx.State.Time;
            int hz = Content.Db.Constants.TickHz;
            t.AnchorServerTime = 1_000_000;
            t.AnchorTick = 0;
            sink.Day = 1;
            sink.OnEvent(new BossFailed(130, 1, 0.4), 30 * 60 * hz);   // 30 active minutes into session 1
            sink.OnEvent(new BossFailed(130, 2, 0.3), 40 * 60 * hz);   // a retry does not reopen it
            // session 1 ends at 60 min; an 11 h gap; session 2 resumes (re-anchored) and the boss falls 15 min in
            t.AnchorServerTime = 1_000_000 + 3600 + 11 * 3600;
            t.AnchorTick = 60 * 60 * hz;
            sink.Day = 2;
            sink.OnEvent(new StageCleared(130, 131, true), (60 + 15) * 60 * hz);

            var w = sink.Walls.Single();
            Assert.That((w.Stage, w.OpenedDay, w.ClosedDay, w.Fails, w.IsOpen), Is.EqualTo((130, 1, 2, 2, false)));
            Assert.That(w.ActiveMinutes(0, hz), Is.EqualTo(45.0), "30 min left of session 1 + 15 min of session 2");
            Assert.That(w.WallClockHours(0), Is.EqualTo(0.5 + 11 + 0.25), "the gap counts toward what the player feels");
            Assert.That(sink.OpenWall, Is.Null);
        }

        [Test]
        public void StageClearedOfAnotherStage_DoesNotCloseTheWall()
        {
            var ctx = BotRunner.CreateContext(Content.Db, 7);
            ctx.Sink.OnEvent(new BossFailed(70, 1, 0.5), 100);
            ctx.Sink.OnEvent(new StageCleared(69, 70, false), 200);
            Assert.That(ctx.Sink.OpenWall.Stage, Is.EqualTo(70));
        }

        [Test]
        public void DiamondsByReason_SumToDiamondsEarned_AndIncludeTheTutorialGrant()
        {
            var run = new BotRunner(Content.Db).Run(new GreedyF2PBot(), 7, 3);
            Assert.That(run.DiamondsByReason.Values.Sum(), Is.EqualTo(run.Rows.Last().DiamondsEarned), "#137: report --diamonds sums to DiamondsEarned");
            for (int d = 1; d <= 3; d++)
                Assert.That(run.Rows[d - 1].DiamondsEarned, Is.EqualTo(Enumerable.Range(1, d).Sum(x => Reports.DayTotal(run, x))));
            Assert.That(run.DiamondsByReason[(1, HeroCat.Sim.Economy.CurrencyReason.TUTORIAL)], Is.EqualTo(1500));
            Assert.That(run.DiamondsByReason[(1, HeroCat.Sim.Economy.CurrencyReason.LOGIN)], Is.EqualTo(Content.Db.Constants.LoginDiamonds));
        }

        [Test]
        public void LayerShares_SumOfLayersIsOne()
        {
            var row = new BotRunner(Content.Db).Run(new GreedyF2PBot(), 7, 1).Rows[0];
            double sum = row.Shares.All().Where(x => x.name != "Base").Sum(x => x.share);
            Assert.That(sum, Is.EqualTo(1.0).Within(1e-9), "ln(DPS / BASE_ATK) is the sum of the layers' ln factors");
            Assert.That(row.Shares.Companions, Is.EqualTo(0), "no companions at M0");
            Assert.That(row.Dps, Is.GreaterThan(Content.Db.Constants.BaseAtk));
        }
    }

    /// <summary>greedy-f2p over fixture states (03 section 8.3, issue #135).</summary>
    public class GreedyPolicyTests
    {
        static BotContext Started(ulong seed = 7)
        {
            var ctx = BotRunner.CreateContext(Content.Db, seed);
            ctx.Runner.Start();
            return ctx;
        }

        [Test]
        public void Training_PicksAtkFirst()
        {
            var ctx = Started();
            ctx.State.Wallet.Gold = 1e6;   // fixture state: the policy never writes state, the test may
            Assert.That(GreedyF2PBot.BestTraining(ctx, out _), Is.EqualTo(Stat.ATK));
        }

        [Test]
        public void Training_ThenTheStatWithTheBestMarginalLnDpsPerGold()
        {
            var ctx = Started();
            ctx.State.Training.Level[(int)Stat.ATK] = 50;
            ctx.State.Wallet.Gold = 1e12;
            ctx.Query.MarkDirty();
            var best = GreedyF2PBot.BestTraining(ctx, out double v);
            Assert.That(best, Is.Not.EqualTo(Stat.ATK));
            foreach (var s in new[] { Stat.ATK, Stat.CRIT_RATE, Stat.CRIT_DMG, Stat.ATK_SPD, Stat.GOLD_GAIN, Stat.EXP_GAIN })
                Assert.That(GreedyF2PBot.MarginalLnDps(ctx.Content, ctx.State, s) / ctx.Query.TrainCost(s), Is.LessThanOrEqualTo(v), s.ToString());
        }

        [Test]
        public void SpendGold_BuysUntilNothingIsAffordable_ThroughCommandsOnly()
        {
            var log = new ListCommandLogShim();
            var ctx = BotRunner.CreateContext(Content.Db, 7, log);
            ctx.Runner.Start();
            ctx.State.Wallet.Gold = 5000;
            GreedyF2PBot.SpendGold(ctx);
            Assert.That(log.Records.OfType<TrainStat>().Count(), Is.GreaterThan(1));
            Assert.That(GreedyF2PBot.BestTraining(ctx, out _), Is.Null);
        }

        [Test]
        public void Diamonds_TenPullPreferredWhenAffordable()
        {
            var ctx = Started();
            ctx.State.Wallet.Diamond = 5000;   // fixture: enough for a 10-pull on either M0 banner
            var b = GreedyF2PBot.BestBanner(ctx, out double g);
            Assert.That(b, Is.Not.Null);
            Assert.That(g, Is.GreaterThan(0));
            GreedyF2PBot.SpendDiamonds(ctx);
            long pulls = ctx.State.Banners.Values.Sum(x => x.TotalPulls);
            Assert.That(pulls % 10, Is.EqualTo(0), "only 10-pulls with full pity left");
            Assert.That(pulls, Is.GreaterThanOrEqualTo(10));
            Assert.That(ctx.State.Loadout.Weapon != null || ctx.State.Loadout.Skills.Any(x => x != null), Is.True, "auto-equipped after the pull");
        }

        [Test]
        public void Diamonds_SingleOnlyWhenPityIsWithinTen()
        {
            var ctx = Started();
            var def = ctx.Content.Banners[Banner.WEAPON];
            ctx.State.Wallet.Diamond = def.PullCostDiamonds * 3;   // a single is affordable, a 10-pull is not
            long before = ctx.State.Banners.Values.Sum(x => x.TotalPulls);
            GreedyF2PBot.SpendDiamonds(ctx);
            Assert.That(ctx.State.Banners.Values.Sum(x => x.TotalPulls), Is.EqualTo(before), "pity far from PITY_HARD: save up");

            ctx.State.Banners[Banner.WEAPON].Pity = def.PityHard - 5;
            ctx.State.Banners[Banner.SKILL].Pity = ctx.Content.Banners[Banner.SKILL].PityHard - 5;
            GreedyF2PBot.SpendDiamonds(ctx);
            Assert.That(ctx.State.Banners.Values.Sum(x => x.TotalPulls), Is.GreaterThan(before), "pity within 10: singles");
        }

        [Test]
        public void Fish_MergesLowestLevelFirst()
        {
            var ctx = Started();
            var cells = ctx.State.Fish.Cells;
            Array.Clear(cells, 0, cells.Length);
            cells[0] = 3; cells[1] = 3; cells[2] = 1; cells[5] = 1;
            ctx.State.Fish.TankCount = 0;
            Assert.That(GreedyF2PBot.LowestPair(ctx), Is.EqualTo((2, 5)));
            GreedyF2PBot.PlayFishBoard(ctx);
            Assert.That(cells.Count(c => c != 0), Is.EqualTo(2), "1+1 -> 2, then 3+3 -> 4");
            Assert.That(cells.Where(c => c != 0).OrderBy(c => c), Is.EqualTo(new[] { 2, 4 }));
        }

        [Test]
        public void Fish_RespectsTheSixtyMergesPerMinuteCap()
        {
            var ctx = Started();
            Array.Clear(ctx.State.Fish.Cells, 0, ctx.State.Fish.Cells.Length);
            ctx.State.Fish.TankCount = 500;   // a fixture tank far above TankCap
            long before = ctx.State.Fish.TotalMerges;
            GreedyF2PBot.PlayFishBoard(ctx);
            Assert.That(ctx.State.Fish.TotalMerges - before, Is.EqualTo(GreedyF2PBot.MergesPerMinuteCap));
        }

        [Test]
        public void Fish_AJammedBoardAtItsCeilingIsLeftAlone()
        {
            var ctx = Started();
            var cells = ctx.State.Fish.Cells;
            int cap = ctx.Sim.FishMaxLvl;
            int capFish = cells.Length - cap + 1;
            for (int i = 0; i < cells.Length; i++) cells[i] = i < capFish ? cap : i - capFish + 1;   // 11 cap fish + one each of 1..9
            ctx.State.Fish.TankCount = 5;
            var snapshot = (int[])cells.Clone();
            GreedyF2PBot.PlayFishBoard(ctx);
            Assert.That(cells, Is.EqualTo(snapshot));
            Assert.That(ctx.State.Stats.FishDiscards, Is.EqualTo(0));
        }

        [Test]
        public void Boss_RetriedOnlyWhenClearable()
        {
            var ctx = Started();
            var c = ctx.State.Combat;
            c.Stage = 300;
            c.Boss = BossPhase.Walled;
            ctx.Query.MarkDirty();
            Assert.That(ctx.Query.BossClearable(300), Is.False);
            GreedyF2PBot.RetryBoss(ctx);
            Assert.That(c.Boss, Is.EqualTo(BossPhase.Walled));
            c.Stage = 10;
            GreedyF2PBot.RetryBoss(ctx);
            Assert.That(c.Boss, Is.EqualTo(BossPhase.Pending));
        }

        [Test]
        public void FourteenDaySweep_RunsEndToEnd()
        {
            var sw = Sweep.Run(Content.Db, "greedy-f2p", "1-3", 14, 3);
            Assert.That(sw.Runs.All(r => r.Rows.Length == 14));
            Assert.That(sw.Runs.All(r => r.Rows[13].Stage > r.Rows[0].Stage), "the bot progresses");
            Assert.That(sw.Runs.All(r => r.Rows[0].Pulls >= 10), "day 1 spends the tutorial diamonds");
        }
    }

    /// <summary>Report verbs and compare (10 section 3.7; issue #137).</summary>
    public class ReportTests
    {
        [Test]
        public void Sweep_IndependentOfParallelism()
        {
            var one = Sweep.Table(Sweep.Run(Content.Db, "greedy-f2p", "1-4", 2, 1), "x");
            var many = Sweep.Table(Sweep.Run(Content.Db, "greedy-f2p", "1-4", 2, 8), "x");
            Assert.That(many, Is.EqualTo(one), "10 acceptance criterion 5");
        }

        [Test]
        public void LayerShareMedians_AreTheMediansOfThePerSeedSharesAc2Reads()
        {
            var sw = Sweep.Run(Content.Db, "greedy-f2p", "1-3", 3, 3);
            var med = Reports.MedianShares(sw, 3);
            foreach (var n in LayerShares.Names)
                Assert.That(med.Get(n), Is.EqualTo(sw.Runs.Select(r => r.Rows[2].Shares.Get(n)).OrderBy(x => x).ElementAt(1)), n);
            string text = Reports.LayerSharesReport(sw, "x", 0.40, 0.05);
            Assert.That(text, Does.Contain("HeroLevel"));
        }

        [Test]
        public void ReportsAndCompare_Render()
        {
            var sw = Sweep.Run(Content.Db, "greedy-f2p", "1-2", 2, 2);
            Assert.That(Reports.LongestWalls(sw, "x"), Does.Contain("unblockedBy"));
            Assert.That(Reports.Diamonds(sw, "x"), Does.Contain("TUTORIAL"));
            var cmp = Reports.Compare(new[] { sw, sw }, "x");
            Assert.That(cmp.Split('\n')[2], Does.EndWith(" 0"), "a bot compared with itself has a zero gap");
        }

        [Test]
        public void Usage_ListsEveryVerbAndOptionOfSection38()
        {
            foreach (var w in new[] { "run", "sweep", "compare", "acceptance", "report", "golden", "stage-bound", "save-fixture", "bench",
                                      "--bot", "--seed", "--days", "--all-days", "--record", "--format", "--diamonds", "--content", "--tier",
                                      "--seeds", "--parallel", "--bots", "--table", "--verbose", "--targets", "--time-budget-s",
                                      "--longest-walls", "--layer-shares", "--check", "--update", "--replay", "--ticks", "--stage", "--max-ms" })
                Assert.That(Program.Usage, Does.Contain(w), w);
        }

        [Test]
        public void Row_FormatsInvariant_EvenUnderGermanCulture()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var row = new BotRunner(Content.Db).Run(new GreedyF2PBot(), 7, 1).Rows[0];
                Assert.That(TableWriter.FormatRow(row), Does.Not.Contain(","));
                Assert.That(TableWriter.Header().Length, Is.EqualTo(TableWriter.FormatRow(row).Length));
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
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

    /// <summary>The acceptance gate (10 section 3.6; issue #136).</summary>
    public class AcceptanceTests
    {
        static readonly AcceptanceTargets T = AcceptanceTargets.LoadDefault();

        [Test]
        public void Targets_MatchSpec10Section36()
        {
            Assert.That(T.SweepBot, Is.EqualTo("greedy-f2p"));
            Assert.That((T.Seeds, T.Days), Is.EqualTo(("1-20", 30)));
            Assert.That(T.StageCheckDays, Is.EqualTo(new[] { 1, 7, 30 }));
            Assert.That(T.ShareMaxCheckDays, Is.EqualTo(new[] { 1, 7, 30 }));
            Assert.That(T.ShareMinCheckDays, Is.EqualTo(new[] { 7, 30 }));
            Assert.That((T.ShareMax, T.ShareMin), Is.EqualTo((0.40, 0.05)));
            Assert.That(T.ShareFloorExcluded, Is.EquivalentTo(new[] { "Base", "Speed" }));
            Assert.That(T.ExemptFor(Tier.M0), Is.EquivalentTo(new[] { "Research", "Companions" }));
            Assert.That(T.ExemptFor(Tier.MVP), Is.Empty);
            Assert.That((T.FirstWallMinStage, T.FirstWallMaxStage), Is.EqualTo((60, 200)));
            Assert.That(T.MaxWallActiveMinutesThroughDay14, Is.EqualTo(240));
            Assert.That(T.LateWallMaxActiveMinutes, Is.Null);
            Assert.That((T.LazyFraction.Day7, T.LazyFraction.Day30), Is.EqualTo((0.55, 0.60)));
            Assert.That(T.WhaleGapStagesDay7, Is.EqualTo(new[] { 25, 60 }));
            Assert.That(T.DiamondsPerDayTolerance, Is.EqualTo(0.15));
            Assert.That(T.TimeBudgetSeconds, Is.EqualTo(240));
        }

        [Test]
        public void StageTargets_ComeFromProgressTargets()
        {
            Assert.That(Content.Db.ProgressTargets.Select(t => (t.Day, t.Stage)),
                        Is.EqualTo(new[] { (1, 240), (3, 330), (7, 430), (14, 480), (30, 550) }));
        }

        [Test]
        public void ChangingProgressTargets_ChangesAc1_WithNoCodeChange()
        {
            string dir = ScratchContent.Make(("progress_targets", rows =>
            {
                foreach (var r in rows) { r["stage"] = 10; r["tolerance"] = 0.5; }
            }));
            var db = ScratchContent.Load(dir);
            var sw = Sweep.Run(db, "greedy-f2p", "1-2", 1, 2);
            var ac1 = AcceptanceChecker.Check(sw, db, T, null).Single(r => r.Id == "AC1");
            Assert.That(ac1.Message, Does.Contain("(target 10)"));
            Assert.That(ac1.Message, Does.Contain("5..15"));
        }

        [Test]
        public void ForcingHpGrowthTo110_FailsAc1OnDays7And30_WithTheMeasuredMedians()
        {
            string dir = ScratchContent.Make(("curves", rows =>
            {
                foreach (var r in rows) if ((string)r["id"] == "enemy_hp") r["growth"] = 1.10;
            }));
            var db = ScratchContent.Load(dir);
            var sw = Sweep.Run(db, "greedy-f2p", "1-3", 30, 3);
            var ac1 = AcceptanceChecker.Check(sw, db, T, null).Where(r => r.Id == "AC1").ToList();
            foreach (int day in new[] { 7, 30 })
            {
                var r = ac1.Single(x => x.Message.StartsWith($"day {day}:"));
                int med = sw.Runs.Select(x => x.Rows[day - 1].Stage).OrderBy(x => x).ElementAt(1);
                Assert.That(r.Status, Is.EqualTo(CheckStatus.Fail), r.Line);
                Assert.That(r.Message, Does.Contain($"median stage {med} outside"));
            }
        }

        [Test]
        public void TierM0WithFourteenDays_ClipsTheCheckDays()
        {
            var sw = Sweep.Run(Content.Db, "greedy-f2p", "1-2", 14, 2);
            var r = AcceptanceChecker.Check(sw, Content.Db, T, null);
            Assert.That(r.Where(x => x.Id == "AC1").Select(x => x.Message.Split(':')[0]), Is.EqualTo(new[] { "day 1", "day 7" }));
            Assert.That(r.Single(x => x.Id == "AC11").Status, Is.EqualTo(CheckStatus.Skip), "no awakening at M0");
            Assert.That(r.Where(x => x.Id == "AC2" && x.Message.Contains("Research")).All(x => x.Status != CheckStatus.Fail), "M0 exemption");
        }

        [Test]
        public void Ac4_FailsAWallOverTheBound_WithItsActiveMinutes()
        {
            var sw = Sweep.Run(Content.Db, "greedy-f2p", "1", 2, 1);
            var strict = AcceptanceTargets.LoadDefault();
            strict.MaxWallActiveMinutesThroughDay14 = 0;
            var ac4 = AcceptanceChecker.Check(sw, Content.Db, strict, null).First(r => r.Id == "AC4");
            Assert.That(ac4.Status, Is.EqualTo(sw.Runs[0].Walls.Count > 0 ? CheckStatus.Fail : CheckStatus.Pass));
        }

        [Test]
        public void AcceptanceTwiceInOneProcess_IsByteIdentical_ApartFromTheMeasuredWallTime()
        {
            string Once()
            {
                var w = new StringWriter();
                Program.AcceptanceCmd(new CliArgs(new[] { "acceptance", "--seeds", "1-2", "--days", "3", "--table", "--verbose", "--golden-dir",
                                                          Path.Combine(Path.GetTempPath(), "no-golden-" + Guid.NewGuid().ToString("N")) }), w);
                return string.Join("\n", w.ToString().Split('\n').Where(l => !l.Contains(" AC10 ")));
            }
            string a = Once();
            Assert.That(Once(), Is.EqualTo(a));
            Assert.That(a, Does.Contain("PASS AC9"));
            Assert.That(a, Does.Contain("stageMed"));
        }
    }

    /// <summary>Golden-file plumbing (10 section 2; issue #138).</summary>
    public class GoldenPlumbingTests
    {
        [Test]
        public void Update_IsRefusedWithoutAContentHashChangeOrTheSimChangeFlag()
        {
            string dir = Path.Combine(Path.GetTempPath(), "golden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Assert.That(GoldenFiles.UpdateRefusal(Content.Db, dir, false), Is.Null, "no content-hash.txt yet: first generation");
            File.WriteAllText(Path.Combine(dir, GoldenFiles.ContentHashFile), Content.Db.ContentHash + "\n");
            if (Environment.GetEnvironmentVariable(GoldenFiles.SimChangeEnv) != "1")
                Assert.That(GoldenFiles.UpdateRefusal(Content.Db, dir, false), Does.Contain("refused"));
            Assert.That(GoldenFiles.UpdateRefusal(Content.Db, dir, true), Is.Null, "--allow-sim-change");
            File.WriteAllText(Path.Combine(dir, GoldenFiles.ContentHashFile), "other\n");
            Assert.That(GoldenFiles.UpdateRefusal(Content.Db, dir, false), Is.Null, "the content hash changed");
        }

        [Test]
        public void HashesText_RoundTripsTheFinalTick()
        {
            string text = GoldenFiles.HashesText(new[] { new DayHash(1, 0, "ab") }, 72_000, "cd");
            Assert.That(text, Is.EqualTo("day,tick,stateHash\n1,0,ab\nfinal,72000,cd\n"));
            Assert.That(GoldenFiles.FinalTick(text), Is.EqualTo(72_000));
        }
    }

    /// <summary>The v1 save fixture comes from a played greedy-f2p profile.</summary>
    public class SaveFixtureTests
    {
        [Test]
        public void SaveFixture_IsDeterministic()
        {
            Assert.That(SaveFixture.Generate(Content.Db), Is.EqualTo(SaveFixture.Generate(Content.Db)));
        }
    }

    [Category("Golden")]
    public class GoldenTests
    {
        /// <summary>10 section 2 and acceptance criterion 3 (x64 Linux part): the committed log replays to the committed hashes.</summary>
        [Test]
        public void GoldenReplay_MatchesCommittedDayHashes()
        {
            string diff = GoldenFiles.CheckReplay(Content.Db, GoldenFiles.DefaultDir(Content.Dir));
            Assert.That(diff, Is.Null, diff);
        }

        /// <summary>`golden --check`: every golden file equals what the bot CLI generates now.</summary>
        [Test]
        public void GoldenCheck_Passes()
        {
            var files = GoldenFiles.Generate(Content.Db);
            string dir = GoldenFiles.DefaultDir(Content.Dir);
            var diffs = GoldenFiles.FileNames.Select(n => GoldenFiles.Compare(dir, n, files[n])).Where(d => d != null).ToList();
            Assert.That(diffs, Is.Empty, string.Join("\n", diffs));
        }
    }
}
