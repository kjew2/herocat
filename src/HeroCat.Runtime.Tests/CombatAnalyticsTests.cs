using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>
    /// 03 section 11's combat rows and 02 section 14.3's session_end (issue #274). Mapping tests publish sim events on a
    /// bare bus; the session tests run real ticks through SessionRunner.
    /// </summary>
    public class CombatAnalyticsTests
    {
        sealed class Harness
        {
            public readonly EventBus Bus = new EventBus();
            public readonly MemoryAnalytics Analytics = new MemoryAnalytics();
            public readonly GameState State;
            public readonly ContentDb Db;

            public Harness(Action<GameState> setup = null)
            {
                Db = TestContent.Db;
                State = HeroCat.Runtime.Profile.ProfileFactory.Create(Db, Guid.Empty, TestContent.T0, TestContent.Device, TestContent.Root);
                setup?.Invoke(State);
                new FeatureAnalytics(Bus, Analytics, Db, () => State);
            }

            public void Publish(params Event[] events) => Bus.Publish(events);

            public List<MemoryAnalytics.Entry> Rows(string name) => Analytics.Events.Where(e => e.Name == name).ToList();
        }

        /// <summary>Stamps the tick the sim would have (Event.Tick has an internal setter).</summary>
        static T At<T>(long tick, T e) where T : Event
        {
            typeof(Event).GetProperty(nameof(Event.Tick)).SetValue(e, tick);
            return e;
        }

        static void AssertProps(MemoryAnalytics.Entry e, IDictionary<string, object> expected)
        {
            Assert.That(e.Props.Keys, Is.EquivalentTo(expected.Keys), e.Name);
            foreach (var kv in expected) Assert.That(e.Props[kv.Key], Is.EqualTo(kv.Value), e.Name + "." + kv.Key);
        }

        [Test]
        public void Training_LevelUp_ResearchBought_MapOneRowPerEvent()
        {
            var h = new Harness();
            h.Publish(new StatTrained(Stat.ATK, 4, 14, 1234.5), new LevelUp(2), new LevelUp(3), new ResearchLeveled("res_atk", 3, 2500));
            AssertProps(h.Rows("training").Single(), new Dictionary<string, object>
            {
                ["stat"] = "ATK", ["levelFrom"] = 4, ["levelTo"] = 14, ["goldCost"] = 1234.5,
            });
            Assert.That(h.Rows("level_up").Select(r => r.Props["heroLevel"]), Is.EqualTo(new object[] { 2, 3 }));
            AssertProps(h.Rows("research_bought").Single(), new Dictionary<string, object>
            {
                ["researchId"] = "res_atk", ["level"] = 3, ["fishCoinCost"] = 2500.0,
            });
        }

        [Test]
        public void ANormalClear_HasNoAttempts_AndCountsTicksFromTheStageEntry()
        {
            var h = new Harness();   // a new profile: stage 1 entered at tick 0
            h.Publish(At(300, new StageCleared(1, 2, false)), At(700, new StageCleared(2, 3, false)));
            var rows = h.Rows("stage_cleared");
            double dps = PowerFormula.Compute(h.Db, h.State).RatedDps;
            AssertProps(rows[0], new Dictionary<string, object>
            {
                ["clearedStage"] = 1, ["isBoss"] = false, ["firstClear"] = true, ["diamondsGranted"] = 0L, ["ticksOnStage"] = 300L, ["dps"] = dps,
            });
            Assert.That((rows[1].Props["clearedStage"], rows[1].Props["ticksOnStage"]), Is.EqualTo((2, 400L)));
            Assert.That(h.Rows("wall_broken"), Is.Empty);
        }

        [Test]
        public void AWalledBoss_SendsBossFailedRows_ThenStageCleared_AndWallBrokenWithTheWallsTimeAndLayers()
        {
            int hz = TestContent.Db.Constants.TickHz;
            var h = new Harness(s => { s.Time.AnchorServerTime = TestContent.T0; s.Time.AnchorTick = 0; });
            h.Publish(At(100, new StageCleared(9, 10, false)), At(500, new BossFailed(10, 1, 0.25)));
            h.State.Party.Level += 5;   // the Hero layer moves during the wall
            h.Publish(At(800, new BossFailed(10, 2, 0.1)));
            h.State.Party.Level += 1;
            h.Publish(At(1100, new StageFirstCleared(10, 10)), At(1100, new StageCleared(10, 11, true)));

            var fails = h.Rows("boss_failed");
            AssertProps(fails[0], new Dictionary<string, object> { ["bossStage"] = 10, ["bossHpLeftPct"] = 25.0, ["attempt"] = 1 });
            Assert.That(fails[1].Props["attempt"], Is.EqualTo(2));
            Assert.That((double)fails[1].Props["bossHpLeftPct"], Is.EqualTo(10.0).Within(1e-9));

            var clear = h.Rows("stage_cleared").Last();
            AssertProps(clear, new Dictionary<string, object>
            {
                ["clearedStage"] = 10, ["isBoss"] = true, ["firstClear"] = true, ["diamondsGranted"] = 10L, ["attempts"] = 3,
                ["ticksOnStage"] = 1000L, ["dps"] = PowerFormula.Compute(h.Db, h.State).RatedDps,
            });
            AssertProps(h.Rows("wall_broken").Single(), new Dictionary<string, object>
            {
                ["wallStage"] = 10, ["wallSec"] = (long)(600 / hz), ["activeSecDuringWall"] = 600.0 / hz, ["attempts"] = 3,
                ["layersChangedDuringWall"] = new List<string> { "PartyLevel" },
            });
        }

        [Test]
        public void ABossClearedFirstTry_IsOneAttempt_AndNoWall()
        {
            var h = new Harness();
            h.Publish(At(50, new StageFirstCleared(10, 10)), At(50, new StageCleared(10, 11, true)));
            var clear = h.Rows("stage_cleared").Single();
            Assert.That((clear.Props["attempts"], clear.Props["diamondsGranted"]), Is.EqualTo((1, 10L)));
            Assert.That(h.Rows("wall_broken"), Is.Empty);
        }

        [Test]
        public void AWallOpenBeforeThisProcess_ReportsAttemptsFromTheSave_AndNoTiming()
        {
            var h = new Harness(s =>
            {
                s.Time.Tick = 50_000;
                s.Combat.Stage = 20;
                s.Combat.Boss = BossPhase.Walled;
                s.Combat.BossFailsOnStage = 2;
                s.Progress.HighestCleared = 19;
            });
            h.Publish(At(50_400, new BossFailed(20, 3, 0.05)), At(50_900, new StageCleared(20, 21, true)));
            var clear = h.Rows("stage_cleared").Single();
            Assert.That(clear.Props.ContainsKey("ticksOnStage"), Is.False, "the stage was entered before this process");
            Assert.That((clear.Props["attempts"], clear.Props["firstClear"], clear.Props["diamondsGranted"]), Is.EqualTo((4, true, 0L)));
            AssertProps(h.Rows("wall_broken").Single(), new Dictionary<string, object> { ["wallStage"] = 20, ["attempts"] = 4 });
        }

        [Test]
        public void ARecleared_Stage_IsNotAFirstClear()
        {
            var h = new Harness(s => s.Progress.HighestCleared = 30);
            h.Publish(At(10, new StageCleared(5, 6, false)));
            Assert.That(h.Rows("stage_cleared").Single().Props["firstClear"], Is.False);
        }

        [Test]
        public void CombatEvents_UseKnownNames_AndCamelCaseProps_WithoutEnvelopeFields()
        {
            FeatureAnalyticsTests.AssertCatalogue(new AnalyticsEvent[]
            {
                new AnalyticsEvents.StageCleared(1, true, true, 10, 1, 5, 2.5), new AnalyticsEvents.BossFailed(1, 50, 1),
                new AnalyticsEvents.WallBroken(1, 2, 3, 4, new[] { "Hero" }), new AnalyticsEvents.Training(Stat.ATK, 1, 2, 3),
                new AnalyticsEvents.LevelUp(2), new AnalyticsEvents.ResearchBought("res_atk", 1, 2),
                new AnalyticsEvents.SessionEnd(1, 1, 2, 3, 4, AnalyticsEvents.SessionEnd.PAUSE),
            });
        }

        // ------------------------------------------------------------------------------------------- through a session

        [Test]
        public void PlayingASession_SendsStageClearedAndLevelUp_FromRealTicks()
        {
            var s = TestSession.Open();
            s.Play(120);
            s.Runner.OnPause();
            var cleared = s.Analytics.Events.Where(e => e.Name == "stage_cleared").ToList();
            Assert.That(cleared, Is.Not.Empty);
            Assert.That(cleared.Count, Is.EqualTo(s.Sim.State.Combat.Stage - 1), "one row per frontier advance");
            Assert.That(cleared.Select(e => (int)e.Props["clearedStage"]), Is.EqualTo(Enumerable.Range(1, cleared.Count)));
            Assert.That(cleared.All(e => (long)e.Props["ticksOnStage"] > 0 && (double)e.Props["dps"] > 0), Is.True);
            Assert.That(s.Analytics.Count("level_up"), Is.EqualTo(s.Sim.State.Party.Level - 1));
        }

        [Test]
        public void SessionEnd_OnPause_NotAgainOnAQuitAfterIt_AndAResumedSessionCountsFromZero()
        {
            var s = TestSession.Open();
            long ticks = s.Play(60);
            s.Runner.OnPause();
            s.Runner.OnQuit();
            var end = s.Analytics.Events.Single(e => e.Name == "session_end");
            var st = s.Sim.State;
            AssertProps(end, new Dictionary<string, object>
            {
                ["durationSec"] = 60L, ["stageStart"] = 1, ["stageEnd"] = st.Combat.Stage, ["kills"] = st.Stats.Kills, ["ticks"] = ticks,
                ["trigger"] = "pause",
            });
            Assert.That(s.Analytics.Events.FindIndex(e => e.Name == "session_end"),
                        Is.LessThan(s.Analytics.Events.FindIndex(e => e.Name == "fish_board_snapshot")), "tracked before the flush");

            s.Runner.OnResume();
            int stage = st.Combat.Stage;
            long kills = st.Stats.Kills;
            long more = s.Play(10);
            s.Runner.OnQuit();
            var quit = s.Analytics.Events.Last(e => e.Name == "session_end");
            AssertProps(quit, new Dictionary<string, object>
            {
                ["durationSec"] = 10L, ["stageStart"] = stage, ["stageEnd"] = st.Combat.Stage, ["kills"] = st.Stats.Kills - kills, ["ticks"] = more,
                ["trigger"] = "quit",
            });
        }

        [Test]
        public void AnInAppGap_EndsTheSessionAtTheLastFrame_AndOpensANewOne()
        {
            var s = TestSession.Open();
            s.Play(5);
            s.Frame(RuntimeConfig.IN_APP_GAP_SEC + 10);
            var gap = s.Analytics.Events.Single(e => e.Name == "session_end");
            Assert.That((gap.Props["trigger"], gap.Props["durationSec"]), Is.EqualTo(("gap", 5L)));
            s.Play(3);
            s.Runner.OnPause();
            var next = s.Analytics.Events.Last(e => e.Name == "session_end");
            Assert.That((next.Props["trigger"], next.Props["durationSec"]), Is.EqualTo(("pause", 3L)));
        }
    }
}
