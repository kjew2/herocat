using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Summons;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Content.Dto;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>Achievement ladders (06 section 5.4 and acceptance 8, 31; 09 section 5.23; 00 pillar 4; issue #171).</summary>
    public class AchievementTests
    {
        const long Day = 86_400;
        static readonly long T0 = TestContent.T0;

        static ContentDb Mvp => TestContent.Mvp;

        static GameState NewState(ContentDb db, long now = -1) =>
            ProfileFactory.Create(db, Guid.Empty, now < 0 ? T0 : now, new Guid("de71ce00-0000-4000-8000-000000000001"), TestContent.Root);

        /// <summary>An MVP sim anchored at T0 with the QUESTS gate open (unless told not to), events drained.</summary>
        static Simulation NewSim(ContentDb db = null, GameState state = null, bool unlocked = true)
        {
            db ??= Mvp;
            state ??= NewState(db);
            if (unlocked && state.Progress.HighestCleared < db.FeatureUnlocks["QUESTS"].Threshold.Value)
                state.Progress.HighestCleared = db.FeatureUnlocks["QUESTS"].Threshold.Value - 1;   // stage 9: rung 10 not yet reached
            var sim = new Simulation(state, db);
            sim.Apply(new ApplyServerTime(T0));
            sim.DrainEvents();
            return sim;
        }

        /// <summary>Opens the QUESTS gate without touching the Stage ladder (the gate reads HighestCleared; tests set it directly).</summary>
        static void Unlock(Simulation sim) => sim.State.Progress.HighestCleared = Math.Max(sim.State.Progress.HighestCleared, Mvp.FeatureUnlocks["QUESTS"].Threshold.Value);

        static QuestStatus St(Simulation sim, string id) => sim.QuestStateOf(sim.Db.Quest(id));

        static long Pr(Simulation sim, string id) => sim.QuestProgressOf(sim.Db.Quest(id));

        static void Merge(Simulation sim, int level)
        {
            var cells = sim.State.Fish.Cells;
            cells[0] = level; cells[1] = level;
            Assert.That(sim.Apply(new MergeFish(0, 1)).Ok, Is.True);
            cells[1] = 0;
        }

        static SummonResultPayload Roll(Simulation sim, Banner b, int count) =>
            new LocalSummonService(() => sim.State, sim.Db, () => T0).Pull(new SummonRequest(Guid.NewGuid().ToString("N"), b, count, SummonPayment.Diamonds)).Value;

        static void AssertRejectIsPure(Simulation sim, Command c, RejectCode code, string reason)
        {
            sim.DrainEvents();
            string before = sim.StateHash();
            var r = sim.Apply(c);
            Assert.That((r.Ok, r.Code, r.Reason), Is.EqualTo((false, code, reason)), c.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(before), c.ToString());
            Assert.That(sim.DrainEvents(), Is.Empty, c.ToString());
        }

        // ------------------------------------------------------------------------------------------------ content

        [Test]
        public void Content_NineLadders_RungIdsAndTargets_Total6260_OwnTopRungsBelowThePool_M0HasNone()
        {
            Assert.That(Mvp.AchievementLadders.Keys, Is.EquivalentTo(new[]
                { "stage", "hero_level", "weapons", "skills", "companions", "merges", "fish_level", "awakening", "summons" }));
            Assert.That(Mvp.Achievements.Count, Is.EqualTo(54));
            Assert.That(Mvp.Achievements.Sum(a => a.RewardAmount), Is.EqualTo(6260), "06 section 5.4 total");
            Assert.That(Mvp.Achievements.Select(a => a.RewardCurrency).Distinct(), Is.EqualTo(new[] { Currency.DIAMOND }));
            Assert.That(Mvp.Achievements.All(a => a.Kind == QuestKind.ACHIEVEMENT && a.Id == $"ach_{a.Ladder}_{a.Rung:00}"), Is.True);
            Assert.That(Mvp.Achievements.Select(a => a.SortOrder), Is.EqualTo(Enumerable.Range(1, 54)));
            foreach (var ladder in Mvp.AchievementLadders.Values)
            {
                Assert.That(ladder.Select(r => r.Rung), Is.EqualTo(Enumerable.Range(1, ladder.Count)));
                Assert.That(ladder.Select(r => r.Target), Is.Ordered.Ascending);
                Assert.That(ladder.Select(r => r.Goal).Distinct().Count(), Is.EqualTo(1));
            }
            Assert.That(Mvp.AchievementLadders["stage"].Select(r => r.Target),
                        Is.EqualTo(new long[] { 10, 30, 50, 100, 150, 200, 300, 400, 500, 700, 1000 }));
            // AC 31 / issue #44: count-owned top rungs stay below the pool (30 weapons, 40 skills, 50 companions).
            Assert.That((Mvp.AchievementLadders["weapons"].Last().Target, Mvp.AchievementLadders["skills"].Last().Target,
                         Mvp.AchievementLadders["companions"].Last().Target), Is.EqualTo((25L, 35L, 45L)));
            Assert.That(Mvp.AchievementLadders["weapons"].Last().Target, Is.LessThan(Mvp.Weapons.Count));
            Assert.That(Mvp.AchievementLadders["skills"].Last().Target, Is.LessThan(Mvp.Skills.Count));
            Assert.That(Mvp.QuestsOf(QuestKind.ACHIEVEMENT), Is.SameAs(Mvp.Achievements));
            Assert.That(Mvp.Quest("ach_merges_04").Target, Is.EqualTo(1000));
            Assert.That(Mvp.AchievementsFor(QuestGoal.LIFETIME_MERGES).Select(a => a.Id).First(), Is.EqualTo("ach_merges_01"));
            Assert.That(Mvp.AchievementsFor(QuestGoal.KILL_ENEMIES), Is.Empty);
            Assert.That(TestContent.Db.Achievements, Is.Empty, "achievement rows are MVP; the M0 build has none");
        }

        // ------------------------------------------------------------------------------------------------ 06 AC 8

        [Test]
        public void AC8_HundredMergeFish_CompleteLifetimeMergesRung50_Rung200StaysInProgress()
        {
            var sim = NewSim();
            for (int i = 0; i < 100; i++) Merge(sim, 1);
            Assert.That((St(sim, "ach_merges_01"), Pr(sim, "ach_merges_01")), Is.EqualTo((QuestStatus.COMPLETED, 100L)), "counts past the target");
            Assert.That((St(sim, "ach_merges_02"), Pr(sim, "ach_merges_02")), Is.EqualTo((QuestStatus.IN_PROGRESS, 100L)));
            Assert.That(sim.DrainEvents().OfType<QuestCompleted>().Count(e => e.QuestId == "ach_merges_01"), Is.EqualTo(1));
        }

        // ------------------------------------------------------------------------------------------------ stage jump, claims

        [Test]
        public void ReachingStage30DirectlyFrom9_CompletesRungs10And30Together_ClaimableInEitherOrder_ReasonAchievement()
        {
            var sim = NewSim();
            Assert.That(sim.State.Progress.HighestCleared, Is.EqualTo(9));
            Combat.GoTo(sim, 30, kills: Mvp.Constants.KillsPerStage - 1, boss: BossPhase.Pending);
            Combat.AdvanceUntil<EnemySpawned>(sim);
            sim.State.Combat.Fight.EnemyHp = 1;
            var ev = new List<Event>();
            while (sim.State.Combat.Stage == 30) { sim.Advance(1); ev.AddRange(sim.DrainEvents()); }
            Assert.That(sim.State.Progress.HighestCleared, Is.EqualTo(30));
            Assert.That(ev.OfType<QuestCompleted>().Select(e => e.QuestId).Where(id => id.StartsWith("ach_")), Is.EqualTo(new[] { "ach_stage_01", "ach_stage_02" }));
            Assert.That((St(sim, "ach_stage_03"), Pr(sim, "ach_stage_03")), Is.EqualTo((QuestStatus.IN_PROGRESS, 30L)));

            double diamonds = sim.State.Wallet.Diamond;
            Assert.That(sim.Apply(new ClaimQuest("ach_stage_02")).Ok, Is.True, "the higher rung first");
            var claimed = sim.DrainEvents();
            Assert.That(claimed.Select(e => e.GetType()), Is.EqualTo(new[] { typeof(QuestClaimed), typeof(CurrencyChanged) }));
            var cc = (CurrencyChanged)claimed[1];
            Assert.That((cc.Currency, cc.Delta, cc.Reason), Is.EqualTo((Currency.DIAMOND, 30.0, CurrencyReason.ACHIEVEMENT)));
            Assert.That(sim.Apply(new ClaimQuest("ach_stage_01")).Ok, Is.True);
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(diamonds + 50));
            Assert.That(sim.State.Stats.ClientDiamondsEarned[CurrencyReason.ACHIEVEMENT], Is.EqualTo(50), "06 AC 32: the upload counter");
            Assert.That(sim.State.Stats.ClientDiamondsEarned.ContainsKey(CurrencyReason.QUEST), Is.False);
            AssertRejectIsPure(sim, new ClaimQuest("ach_stage_01"), RejectCode.AlreadyClaimed, QuestRejectReasons.QUEST_ALREADY_CLAIMED);
            AssertRejectIsPure(sim, new ClaimQuest("ach_stage_03"), RejectCode.NotAvailable, QuestRejectReasons.QUEST_NOT_COMPLETED);
        }

        [Test]
        public void ClaimAll_Achievement_ClaimsEveryCompletedRungInLadderOrder_OneBulk_GateApplies()
        {
            var state = NewState(Mvp);
            state.Progress.HighestCleared = 9;
            var sim = NewSim(state: state, unlocked: false);
            for (int i = 0; i < 50; i++) Merge(sim, 1);
            sim.State.Party.Exp = 0;
            Ledger.Apply(sim.State, new CurrencyDelta(Currency.PARTY_EXP, 1e9), CurrencyReason.ADMIN, sim);   // levels past 10
            Assert.That(St(sim, "ach_hero_level_01"), Is.EqualTo(QuestStatus.COMPLETED));
            AssertRejectIsPure(sim, new ClaimAllQuests(QuestKind.ACHIEVEMENT), RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED);
            AssertRejectIsPure(sim, new ClaimQuest("ach_merges_01"), RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED);

            Unlock(sim);
            var completed = Mvp.Achievements.Where(a => sim.QuestStateOf(a) == QuestStatus.COMPLETED).Select(a => a.Id).ToArray();
            Assert.That(completed, Does.Contain("ach_hero_level_01").And.Contain("ach_merges_01"));
            Assert.That(sim.Apply(new ClaimAllQuests(QuestKind.ACHIEVEMENT)).Ok, Is.True);
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<QuestClaimed>().Select(e => e.QuestId), Is.EqualTo(completed), "claims in SortOrder: ladder, then rung");
            Assert.That(ev.OfType<CurrencyChanged>().All(e => e.Reason == CurrencyReason.ACHIEVEMENT), Is.True);
            var bulk = ev.OfType<QuestsBulkClaimed>().Single();
            Assert.That((bulk.Kind, bulk.Count), Is.EqualTo((QuestKind.ACHIEVEMENT, completed.Length)));
            Assert.That(bulk.Totals.Single().Amount, Is.EqualTo(completed.Sum(id => Mvp.Quest(id).RewardAmount)));
            AssertRejectIsPure(sim, new ClaimAllQuests(QuestKind.ACHIEVEMENT), RejectCode.NotAvailable, QuestRejectReasons.NOTHING_TO_CLAIM);
            Assert.That(St(sim, "d_merge"), Is.EqualTo(QuestStatus.COMPLETED), "an ACHIEVEMENT claim-all leaves dailies alone");
        }

        // ------------------------------------------------------------------------------------------------ never reset

        [Test]
        public void AnAchievement_NeverReturnsToInProgress_AcrossDayAndWeekRollovers()
        {
            var sim = NewSim();
            for (int i = 0; i < 60; i++) Merge(sim, 1);
            sim.State.Party.Exp = 0;
            Ledger.Apply(sim.State, new CurrencyDelta(Currency.PARTY_EXP, 1e9), CurrencyReason.ADMIN, sim);
            Unlock(sim);
            Assert.That(sim.Apply(new ClaimQuest("ach_hero_level_01")).Ok, Is.True);
            var before = sim.State.Quests.Achievement.ToDictionary(kv => kv.Key, kv => (kv.Value.Progress, kv.Value.State));
            Assert.That(before["ach_merges_01"], Is.EqualTo((60L, QuestStatus.COMPLETED)));

            for (int d = 1; d <= 9; d++)   // Friday .. the next week's Saturday: day rollovers and one week rollover
            {
                sim.Apply(new ApplyServerTime(T0 + d * Day));
                Assert.That(sim.State.Quests.Achievement.ToDictionary(kv => kv.Key, kv => (kv.Value.Progress, kv.Value.State)), Is.EqualTo(before), "day " + d);
            }
            Assert.That(sim.DrainEvents().OfType<WeekRolledOver>().Count(), Is.EqualTo(1));
            Assert.That(St(sim, "ach_merges_01"), Is.EqualTo(QuestStatus.COMPLETED));
            Assert.That(St(sim, "ach_hero_level_01"), Is.EqualTo(QuestStatus.CLAIMED));
            Assert.That(St(sim, "d_merge"), Is.EqualTo(QuestStatus.IN_PROGRESS), "the daily reset the same merges");
        }

        // ------------------------------------------------------------------------------------------------ max goals

        [Test]
        public void MaxGoals_FromTheirSites_HeroLevel_FishLevel_Awakening_OwnedDistinct_AndLifetimeSummons()
        {
            var sim = NewSim();

            sim.State.Party.Exp = 0;
            Ledger.Apply(sim.State, new CurrencyDelta(Currency.PARTY_EXP, 1e9), CurrencyReason.ADMIN, sim);
            int level = sim.State.Party.Level;
            Assert.That(level, Is.GreaterThan(10));
            Assert.That(Pr(sim, "ach_hero_level_01"), Is.EqualTo(level));

            Merge(sim, 4);   // creates level 5: discovered, HighestLevel 5
            Assert.That((St(sim, "ach_fish_level_01"), Pr(sim, "ach_fish_level_02")), Is.EqualTo((QuestStatus.COMPLETED, 5L)));
            Merge(sim, 2);   // a lower level never lowers the max
            Assert.That(Pr(sim, "ach_fish_level_02"), Is.EqualTo(5));

            sim.State.Progress.HighestCleared = 50;
            sim.State.Wallet.RockCandy = 100;
            Assert.That(sim.Apply(new Awaken(0)).Ok, Is.True);
            Assert.That((St(sim, "ach_awakening_01"), Pr(sim, "ach_awakening_02")), Is.EqualTo((QuestStatus.COMPLETED, 1L)));

            Combat.Give(sim, Currency.DIAMOND, 100_000);
            int owned = sim.State.Weapons.Items.Count;
            Assert.That(sim.Apply(new ApplySummonResult(Roll(sim, Banner.WEAPON, 10))).Ok, Is.True);
            int distinct = sim.State.Weapons.Items.Count;
            Assert.That(distinct, Is.GreaterThan(owned));
            Assert.That(Pr(sim, "ach_weapons_01"), Is.EqualTo(distinct), "distinct ids, not copies");
            Assert.That(St(sim, "ach_weapons_01"), Is.EqualTo(distinct >= 5 ? QuestStatus.COMPLETED : QuestStatus.IN_PROGRESS));
            Assert.That(sim.State.Quests.Achievement.ContainsKey("ach_skills_01"), Is.False, "a weapon pull moves only the weapon ladder");
            Assert.That((St(sim, "ach_summons_01"), Pr(sim, "ach_summons_02")), Is.EqualTo((QuestStatus.COMPLETED, 10L)), "LIFETIME_SUMMONS counts each pull");
            Assert.That(sim.Apply(new ApplySummonResult(Roll(sim, Banner.SKILL, 1))).Ok, Is.True);
            Assert.That((Pr(sim, "ach_skills_01"), Pr(sim, "ach_summons_02")), Is.EqualTo((1L, 11L)));
        }

        [Test]
        public void OnLoad_TheMaxLaddersCatchUpWithTheState_AndANewRungCompletesWithoutWaiting()
        {
            var state = NewState(Mvp);
            state.Progress.HighestCleared = 120;
            state.Party.Level = 30;
            state.Party.Slots[0].AwakenRank = 2;
            state.Fish.HighestLevel = 8;
            state.Stats.Merges = 600;
            state.Stats.Pulls = 120;
            state.Quests.Achievement["ach_stage_01"] = new QuestProgress { Progress = 10, State = QuestStatus.CLAIMED };
            var sim = new Simulation(state, Mvp);
            var ev = sim.DrainEvents().OfType<QuestCompleted>().Select(e => e.QuestId).ToArray();
            Assert.That(ev, Is.EqualTo(new[]
            {
                "ach_stage_02", "ach_stage_03", "ach_stage_04", "ach_hero_level_01", "ach_hero_level_02", "ach_weapons_01", "ach_companions_01",
                "ach_fish_level_01", "ach_fish_level_02", "ach_awakening_01", "ach_awakening_02",
                "ach_merges_01", "ach_merges_02", "ach_merges_03", "ach_summons_01", "ach_summons_02", "ach_summons_03",
            }));
            Assert.That((St(sim, "ach_stage_01"), Pr(sim, "ach_stage_01")), Is.EqualTo((QuestStatus.CLAIMED, 120L)), "a claimed rung stays claimed");
            Assert.That(Pr(sim, "ach_stage_05"), Is.EqualTo(120));
            Assert.That(Pr(sim, "ach_weapons_01"), Is.EqualTo(state.Weapons.Items.Count), "the starter weapon counts");
            string hash = sim.StateHash();
            Assert.That(new Simulation(sim.State, Mvp).StateHash(), Is.EqualTo(hash), "idempotent: a second load changes nothing");

            // A content update adds a rung below what the player already has: it completes on the next load.
            var dtos = ContentLoader.LoadDtos(new FileContentSource(TestContent.Dir));
            dtos.Achievements = dtos.Achievements.Concat(new[]
            {
                new AchievementRowDto { Id = "ach_cats_01", Ladder = "cats", Rung = 1, Goal = "REACH_STAGE", Target = 110, RewardCurrency = "DIAMOND",
                                        RewardAmount = 5, Tier = "MVP", NameKey = "achievement.stage_01.name" },
                new AchievementRowDto { Id = "ach_cats_02", Ladder = "cats", Rung = 2, Goal = "LIFETIME_MERGES", Target = 500, RewardCurrency = "DIAMOND",
                                        RewardAmount = 5, Tier = "MVP", NameKey = "achievement.stage_01.name" },
            }).ToArray();
            Assert.That(() => ContentDb.Build(dtos, Tier.MVP), Throws.TypeOf<ContentBuildException>().With.Message.Contains("E056"), "one goal per ladder");
            dtos.Achievements = dtos.Achievements.Where(a => a.Id != "ach_cats_02").ToArray();
            var updated = ContentDb.Build(dtos, Tier.MVP);
            var reloaded = new Simulation(sim.State, updated);
            Assert.That(reloaded.DrainEvents().OfType<QuestCompleted>().Select(e => e.QuestId), Is.EqualTo(new[] { "ach_cats_01" }));
            Assert.That(reloaded.QuestStateOf(updated.Quest("ach_cats_01")), Is.EqualTo(QuestStatus.COMPLETED));
        }

        [Test]
        public void M0Build_HasNoRungs_LoadAndMergesWriteNoAchievementEntry()
        {
            var sim = TestContent.NewSim();
            sim.State.Fish.Cells[0] = 1; sim.State.Fish.Cells[1] = 1;
            Assert.That(sim.Apply(new MergeFish(0, 1)).Ok, Is.True);
            Assert.That(sim.State.Quests.Achievement, Is.Empty);
        }

        // ------------------------------------------------------------------------------------------------ SCR_QUESTS reads

        [Test]
        public void AchievementRows_ShowTheLowestUnclaimedRungPerLadder_AndTheClaimedCount()
        {
            var sim = NewSim();
            Assert.That(sim.AchievementRows().Select(r => r.Id), Is.EqualTo(Mvp.AchievementLadders.Values.Select(l => l[0].Id)));
            sim.State.Quests.Achievement.Clear();
            sim.AchievementsSetMax(QuestGoal.REACH_STAGE, 30);
            Unlock(sim);
            Assert.That(sim.Apply(new ClaimQuest("ach_stage_02")).Ok, Is.True);
            Assert.That((sim.LowestUnclaimedRung("stage").Id, sim.ClaimedRungs("stage")), Is.EqualTo(("ach_stage_01", 1)), "claim order does not skip rung 1");
            Assert.That(sim.Apply(new ClaimQuest("ach_stage_01")).Ok, Is.True);
            Assert.That((sim.LowestUnclaimedRung("stage").Id, sim.ClaimedRungs("stage")), Is.EqualTo(("ach_stage_03", 2)));
            Assert.That(sim.AchievementRows().First().Id, Is.EqualTo("ach_stage_03"));
            foreach (var rung in Mvp.AchievementLadders["awakening"]) sim.State.Quests.Achievement[rung.Id] = new QuestProgress { Progress = 5, State = QuestStatus.CLAIMED };
            Assert.That(sim.LowestUnclaimedRung("awakening"), Is.Null);
            Assert.That(sim.AchievementRows().Select(r => r.Ladder), Does.Not.Contain("awakening"), "a fully claimed ladder drops out");
            Assert.That((sim.LowestUnclaimedRung("no_such"), sim.ClaimedRungs(null)), Is.EqualTo(((QuestDef)null, 0)));
        }

        // ------------------------------------------------------------------------------------------------ determinism

        [Test]
        public void ACommandLogReplay_WithMergesSummonsAndClaims_ReproducesTheAchievementStateAndHash()
        {
            GameState Genesis()
            {
                var s = NewState(Mvp);
                s.Progress.HighestCleared = 9;
                Assert.That(Ledger.Apply(s, new CurrencyDelta(Currency.DIAMOND, 50_000), CurrencyReason.ADMIN, null), Is.True);
                return s;
            }
            var log = new ListCommandLog();
            var live = new Simulation(Genesis(), Mvp) { CommandLog = log };
            int hz = Mvp.Constants.TickHz;
            live.Apply(new ApplyServerTime(T0 + 3 * Day + 23 * 3600));   // Sunday 23:00: the run crosses a week rollover
            for (int round = 0; round < 30; round++)
            {
                live.Apply(new ReleaseMax());
                live.Apply(new MergeAll());
                if (round % 5 == 0) live.Apply(new ApplySummonResult(Roll(live, round % 2 == 0 ? Banner.WEAPON : Banner.SKILL, 10)));
                live.Advance(5 * 60 * hz);
                live.Apply(new ClaimAllQuests(QuestKind.ACHIEVEMENT));
            }
            Assert.That(live.State.Quests.Achievement.Values.Any(q => q.State == QuestStatus.CLAIMED), Is.True, "something was claimed");
            Assert.That(live.State.Stats.ClientDiamondsEarned.ContainsKey(CurrencyReason.ACHIEVEMENT), Is.True);

            var replay = new Simulation(Genesis(), Mvp);
            replay.Replay(log.Records, live.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(live.StateHash()));
            Assert.That(replay.State.Quests.Achievement.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Progress, kv.Value.State)),
                        Is.EqualTo(live.State.Quests.Achievement.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Progress, kv.Value.State))));

            // From a mid-run save: the load-time recompute is a function of the state, so replaying the tail matches too.
            var mid = new Simulation(Genesis(), Mvp);
            int half = log.Records.Count / 2;
            mid.Replay(log.Records.Take(half).ToList());
            var resumed = new Simulation(StateCopy.DeepClone(mid.State), Mvp);
            resumed.Replay(log.Records.Skip(half).ToList(), live.Tick);
            Assert.That(resumed.StateHash(), Is.EqualTo(live.StateHash()));
        }

        [Test]
        public void OfflineCompute_AdvancesNoAchievement()
        {
            var sim = NewSim();
            for (int i = 0; i < 3; i++) Merge(sim, 1);
            var before = sim.State.Quests.Achievement.ToDictionary(kv => kv.Key, kv => (kv.Value.Progress, kv.Value.State));
            sim.Apply(new ComputeOfflineCatchup(T0 + 6 * 3600));
            Assert.That(sim.State.Quests.Achievement.ToDictionary(kv => kv.Key, kv => (kv.Value.Progress, kv.Value.State)), Is.EqualTo(before));
        }
    }
}
