using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Profile;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Content.Dto;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>Daily and weekly quests (06 sections 5.1-5.3, 5.5, 4.2; 06 acceptance 1, 5-8, 24, 26, 29, 30; issue #170).</summary>
    public class QuestTests
    {
        const long Day = 86_400;
        static readonly long T0 = TestContent.T0;   // Thursday 2026-01-01; the next Monday is T0 + 4 days

        static ContentDb Mvp => TestContent.Mvp;

        static GameState NewState(ContentDb db, long now) =>
            ProfileFactory.Create(db, Guid.Empty, now, new Guid("de71ce00-0000-4000-8000-000000000001"), TestContent.Root);

        /// <summary>An MVP sim anchored at <paramref name="now"/> (day rolled, d_login done); quests unlocked unless told not to.</summary>
        static Simulation NewSim(ContentDb db = null, long now = -1, bool unlocked = true)
        {
            db ??= Mvp;
            long t = now < 0 ? T0 : now;
            var sim = new Simulation(NewState(db, t), db);
            if (unlocked) sim.State.Progress.HighestCleared = db.FeatureUnlocks["QUESTS"].Threshold.Value;
            sim.Apply(new ApplyServerTime(t));
            sim.DrainEvents();
            return sim;
        }

        static QuestDef Q(string id) => Mvp.Quest(id) ?? throw new ArgumentException("no quest " + id);

        static QuestStatus St(Simulation sim, string id) => sim.QuestStateOf(sim.Db.Quest(id));

        static long Pr(Simulation sim, string id) => sim.QuestProgressOf(sim.Db.Quest(id));

        static void Progress(Simulation sim, QuestGoal goal, long delta = 1, int filter = -1) => sim.QuestsProgress(goal, delta, filter);

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
        public void Content_DailyAndWeeklySets_BySortOrder_P1RowFiltered_M0HasNone()
        {
            Assert.That(Mvp.QuestsDaily.Select(q => q.Id), Is.EqualTo(new[]
            {
                "d_login", "d_kills", "d_boss", "d_summon_weapon", "d_summon_skill", "d_summon_companion", "d_release", "d_merge",
                "d_dungeon_fish", "d_dungeon_candy", "d_dungeon_exp", "d_bonus_5", "d_bonus_8",
            }), "d_watch_ad is P1 and filtered out at an MVP build");
            Assert.That(Mvp.QuestsWeekly.Select(q => q.Id), Is.EqualTo(new[]
            {
                "w_login", "w_kills", "w_dailies", "w_summons", "w_merges", "w_dungeons", "w_train", "w_bonus_all_skill", "w_bonus_all_companion",
            }));
            Assert.That(Mvp.QuestsDaily.Where(q => q.Goal != QuestGoal.DAILIES_COMPLETED && q.Id != "d_login").Select(q => q.RewardAmount).Distinct(), Is.EqualTo(new[] { 20L }));
            Assert.That((Q("d_login").RewardAmount, Q("d_bonus_5").RewardAmount, Q("d_bonus_8").RewardAmount), Is.EqualTo((0L, 30L, 70L)));
            Assert.That(Mvp.QuestsWeekly.Where(q => q.Goal != QuestGoal.WEEKLIES_COMPLETED).Select(q => (q.RewardCurrency, q.RewardAmount)).Distinct(),
                        Is.EqualTo(new[] { (Currency.DIAMOND, 50L) }));
            Assert.That(Mvp.QuestsWeekly.Where(q => q.Goal == QuestGoal.WEEKLIES_COMPLETED).Select(q => (q.Id, q.Target, q.RewardCurrency, q.RewardAmount)),
                        Is.EqualTo(new[]
                        {
                            ("w_bonus_all_skill", 7L, Currency.TICKET_SUMMON_SKILL, 1L), ("w_bonus_all_companion", 7L, Currency.TICKET_SUMMON_COMPANION, 1L),
                        }), "06 section 5.3: the two-ticket bonus is two rows (issue #170)");
            Assert.That((Q("d_summon_skill").Filter, Q("d_dungeon_exp").Filter, Q("w_summons").Filter), Is.EqualTo(("SKILL", "EXP", (string)null)));
            Assert.That(Q("w_train").Goal, Is.EqualTo(QuestGoal.TRAIN_LEVELS));
            Assert.That(TestContent.Db.QuestsDaily, Is.Empty, "quest rows are MVP; the M0 build has none");
            Assert.That(TestContent.Db.QuestsWeekly, Is.Empty);
            Assert.That(Mvp.Quest("no_such_quest"), Is.Null);
        }

        // ------------------------------------------------------------------------------------------------ login (AC 26)

        [Test]
        public void AC26_Rollover_LoginPaysOnce_DLoginClaimedWithNoQuestPay_WLoginCounts()
        {
            var sim = new Simulation(NewState(Mvp, T0), Mvp);
            sim.Apply(new ApplyServerTime(T0));
            var events = sim.DrainEvents();
            Assert.That(St(sim, "d_login"), Is.EqualTo(QuestStatus.CLAIMED), "reward 0: straight to CLAIMED");
            Assert.That(Pr(sim, "w_login"), Is.EqualTo(1));
            Assert.That(events.OfType<QuestCompleted>().Select(e => e.QuestId), Is.EqualTo(new[] { "ach_companions_01", "d_login" }),
                        "the five starter companions complete the first OWN_COMPANIONS rung on load (00 decision 109)");
            Assert.That(events.OfType<CurrencyChanged>().Where(e => e.Reason == CurrencyReason.QUEST), Is.Empty);
            Assert.That(events.OfType<CurrencyChanged>().Count(e => e.Reason == CurrencyReason.LOGIN), Is.EqualTo(1));
            Assert.That(Pr(sim, "d_bonus_5"), Is.EqualTo(1), "d_login counts toward the bonus rows");

            sim.Apply(new ApplyServerTime(T0 + 3600));   // same day key
            Assert.That(sim.DrainEvents(), Is.Empty);
            Assert.That(Pr(sim, "w_login"), Is.EqualTo(1));
            AssertRejectIsPure(sim, new ClaimQuest("d_login"), RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED);
            sim.State.Progress.HighestCleared = 10;
            AssertRejectIsPure(sim, new ClaimQuest("d_login"), RejectCode.AlreadyClaimed, QuestRejectReasons.QUEST_ALREADY_CLAIMED);
        }

        // ------------------------------------------------------------------------------------------------ bonus rows (AC 7)

        [Test]
        public void AC7_DBonus8_CompletesOnTheEighthDailyRow_LoginCounted_WhateverTheClaimState()
        {
            var sim = NewSim();   // d_login: 1 of 11
            Progress(sim, QuestGoal.KILL_ENEMIES, 300);
            Progress(sim, QuestGoal.ATTEMPT_BOSS, 3);
            Progress(sim, QuestGoal.SUMMON, 1, (int)Banner.WEAPON);
            Assert.That(St(sim, "d_bonus_5"), Is.EqualTo(QuestStatus.IN_PROGRESS), "4 of 11");
            Assert.That(sim.Apply(new ClaimQuest("d_kills")).Ok, Is.True, "claiming does not change what counts");
            Progress(sim, QuestGoal.RELEASE_FISH, 30);
            Assert.That(St(sim, "d_bonus_5"), Is.EqualTo(QuestStatus.COMPLETED), "5 of 11");
            Progress(sim, QuestGoal.MERGE_FISH, 15);
            sim.QuestsOnDungeonCleared(Dungeon.FISH_COIN);
            Assert.That(St(sim, "d_bonus_8"), Is.EqualTo(QuestStatus.IN_PROGRESS), "7 of 11");
            sim.DrainEvents();
            sim.QuestsOnDungeonCleared(Dungeon.ROCK_CANDY);
            Assert.That(St(sim, "d_bonus_8"), Is.EqualTo(QuestStatus.COMPLETED), "8 of 11");
            Assert.That(sim.DrainEvents().OfType<QuestCompleted>().Select(e => e.QuestId), Is.EqualTo(new[] { "d_dungeon_candy", "d_bonus_8" }));
            Assert.That(Pr(sim, "d_bonus_8"), Is.EqualTo(8));
            Assert.That(Pr(sim, "w_dailies"), Is.EqualTo(8), "w_dailies counts the same daily missions, not the bonus rows");
        }

        // ------------------------------------------------------------------------------------------------ rollover (AC 5)

        [Test]
        public void AC5_Rollover_ForfeitsACompletedUnclaimedDaily_NoCurrencyChanged()
        {
            var sim = NewSim();
            Progress(sim, QuestGoal.KILL_ENEMIES, 300);
            Assert.That(St(sim, "d_kills"), Is.EqualTo(QuestStatus.COMPLETED));
            Progress(sim, QuestGoal.MERGE_FISH, 4);
            double diamonds = sim.State.Wallet.Diamond;
            sim.DrainEvents();

            sim.Apply(new ApplyServerTime(T0 + Day));   // Friday: a new day key, the same week
            var events = sim.DrainEvents();
            Assert.That((St(sim, "d_kills"), Pr(sim, "d_kills")), Is.EqualTo((QuestStatus.IN_PROGRESS, 0L)));
            Assert.That(Pr(sim, "d_merge"), Is.EqualTo(0));
            Assert.That(events.OfType<CurrencyChanged>().Where(e => e.Reason != CurrencyReason.LOGIN && e.Reason != CurrencyReason.DAILY_RESET), Is.Empty);
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(diamonds + Mvp.Constants.LoginDiamonds), "only the login grant");
            Assert.That(St(sim, "d_login"), Is.EqualTo(QuestStatus.CLAIMED), "the new day's login counts again");
            Assert.That((Pr(sim, "w_kills"), Pr(sim, "w_merges"), Pr(sim, "w_login"), Pr(sim, "w_dailies")), Is.EqualTo((300L, 4L, 2L, 3L)),
                        "weekly progress persists across a day rollover (2 logins + d_kills)");
            AssertRejectIsPure(sim, new ClaimQuest("d_kills"), RejectCode.NotAvailable, QuestRejectReasons.QUEST_NOT_COMPLETED);
        }

        [Test]
        public void WeeklyReset_RunsOnTheWeekKey_MondayZeroUtc_AlsoWhenTheAppIsOpenAcrossMidnight()
        {
            var sim = NewSim();                           // Thursday
            Progress(sim, QuestGoal.KILL_ENEMIES, 3000);
            Assert.That(St(sim, "w_kills"), Is.EqualTo(QuestStatus.COMPLETED));
            sim.Apply(new ApplyServerTime(T0 + 3 * Day + 23 * 3600));   // Sunday 23:00
            Assert.That((St(sim, "w_kills"), Pr(sim, "w_login")), Is.EqualTo((QuestStatus.COMPLETED, 2L)), "Sunday is the same week");
            Progress(sim, QuestGoal.KILL_ENEMIES, 5);
            sim.DrainEvents();
            sim.Advance(2 * 3600 * Mvp.Constants.TickHz);   // ticks across Monday 00:00 UTC with the app open
            var events = sim.DrainEvents();
            Assert.That(events.OfType<WeekRolledOver>().Count(), Is.EqualTo(1));
            Assert.That((St(sim, "w_kills"), Pr(sim, "w_kills") < 3005, Pr(sim, "w_login")), Is.EqualTo((QuestStatus.IN_PROGRESS, true, 1L)),
                        "forfeited unclaimed, reset, and Monday's login counted");
        }

        // ------------------------------------------------------------------------------------------------ AC 6, AC 8

        [Test]
        public void AC6_DBoss_CompletesAfterThreeAttemptBoss_OnAWalledStage_WithZeroBossKills()
        {
            var sim = NewSim();
            int stage = Mvp.Constants.BossEvery * 40;   // far past what the starting hero can clear
            sim.State.Combat.Stage = stage;
            sim.State.Combat.KillsInStage = Mvp.Constants.KillsPerStage - 1;
            sim.State.Combat.Boss = BossPhase.Walled;
            sim.State.Combat.Fight = null;
            for (int i = 1; i <= 3; i++)
            {
                Assert.That(St(sim, "d_boss"), Is.EqualTo(QuestStatus.IN_PROGRESS), "before attempt " + i);
                Assert.That(sim.Apply(new AttemptBoss()).Ok, Is.True);
                Combat.AdvanceUntil<BossFailed>(sim);
                Assert.That(Pr(sim, "d_boss"), Is.EqualTo(i));
            }
            Assert.That(St(sim, "d_boss"), Is.EqualTo(QuestStatus.COMPLETED));
            Assert.That(sim.State.Stats.BossKills, Is.EqualTo(0));
        }

        [Test]
        public void AC8_HundredMergeFish_AdvanceDMergeAndWMergesPastTarget_DMergePaysOnce()
        {
            var sim = NewSim();
            var cells = sim.State.Fish.Cells;
            sim.DrainEvents();
            for (int i = 0; i < 100; i++)
            {
                cells[0] = 1; cells[1] = 1;
                Assert.That(sim.Apply(new MergeFish(0, 1)).Ok, Is.True);
                cells[1] = 0;
            }
            Assert.That((Pr(sim, "d_merge"), Pr(sim, "w_merges")), Is.EqualTo((100L, 100L)), "progress counts past the target");
            Assert.That((St(sim, "d_merge"), St(sim, "w_merges")), Is.EqualTo((QuestStatus.COMPLETED, QuestStatus.COMPLETED)));
            Assert.That(sim.DrainEvents().OfType<QuestCompleted>().Count(e => e.QuestId == "d_merge"), Is.EqualTo(1));

            double diamonds = sim.State.Wallet.Diamond;
            Assert.That(sim.Apply(new ClaimQuest("d_merge")).Ok, Is.True);
            var ev = sim.DrainEvents();
            Assert.That(ev.Select(e => e.GetType()), Is.EqualTo(new[] { typeof(QuestClaimed), typeof(CurrencyChanged) }));
            Assert.That(ev[0], Is.EqualTo(new QuestClaimed("d_merge", Currency.DIAMOND, 20) { Tick = ev[0].Tick }));
            var cc = (CurrencyChanged)ev[1];
            Assert.That((cc.Currency, cc.Delta, cc.Reason), Is.EqualTo((Currency.DIAMOND, 20.0, CurrencyReason.QUEST)));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(diamonds + 20));
            AssertRejectIsPure(sim, new ClaimQuest("d_merge"), RejectCode.AlreadyClaimed, QuestRejectReasons.QUEST_ALREADY_CLAIMED);
            Assert.That(sim.State.Stats.ClientDiamondsEarned[CurrencyReason.QUEST], Is.EqualTo(20), "06 AC 32: the upload counter");
        }

        // ------------------------------------------------------------------------------------------------ AC 24, 29, 30

        [Test]
        public void AC24_ClaimAll_NothingCompleted_Rejected_ThreeCompleted_ThreeClaimedAndOneBulk()
        {
            var sim = NewSim();
            AssertRejectIsPure(sim, new ClaimAllQuests(QuestKind.DAILY), RejectCode.NotAvailable, QuestRejectReasons.NOTHING_TO_CLAIM);
            Progress(sim, QuestGoal.RELEASE_FISH, 30);
            Progress(sim, QuestGoal.KILL_ENEMIES, 300);
            Progress(sim, QuestGoal.ATTEMPT_BOSS, 3);
            sim.DrainEvents();
            double diamonds = sim.State.Wallet.Diamond;
            Assert.That(sim.Apply(new ClaimAllQuests(QuestKind.DAILY)).Ok, Is.True);
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<QuestClaimed>().Select(e => e.QuestId), Is.EqualTo(new[] { "d_kills", "d_boss", "d_release" }), "claims in sort order");
            var bulk = ev.OfType<QuestsBulkClaimed>().Single();
            Assert.That((bulk.Kind, bulk.Count), Is.EqualTo((QuestKind.DAILY, 3)));
            Assert.That(bulk.Totals.Select(t => (t.Currency, t.Amount)), Is.EqualTo(new[] { (Currency.DIAMOND, 60.0) }));
            Assert.That(ev.Last(), Is.SameAs(bulk));
            Assert.That(ev.OfType<CurrencyChanged>().Count(e => e.Reason == CurrencyReason.QUEST), Is.EqualTo(3));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(diamonds + 60));
            Assert.That(St(sim, "w_kills"), Is.EqualTo(QuestStatus.IN_PROGRESS), "a DAILY claim-all leaves weeklies alone");
            AssertRejectIsPure(sim, new ClaimAllQuests(QuestKind.DAILY), RejectCode.NotAvailable, QuestRejectReasons.NOTHING_TO_CLAIM);
            AssertRejectIsPure(sim, new ClaimAllQuests(QuestKind.WEEKLY), RejectCode.NotAvailable, QuestRejectReasons.NOTHING_TO_CLAIM);
        }

        [Test]
        public void AC29_EveryQuestReject_HasItsCodeAndReason_ChangesNothing_EmitsNothing()
        {
            var sim = NewSim();
            Assert.That(sim.Apply(new ClaimAllQuests(QuestKind.ACHIEVEMENT)).Ok, Is.True, "the starter party's OWN_COMPANIONS rung");
            sim.DrainEvents();
            Progress(sim, QuestGoal.KILL_ENEMIES, 10);
            AssertRejectIsPure(sim, new ClaimQuest("no_such_quest"), RejectCode.InvalidTarget, QuestRejectReasons.UNKNOWN_ID);
            AssertRejectIsPure(sim, new ClaimQuest(null), RejectCode.InvalidTarget, QuestRejectReasons.UNKNOWN_ID);
            AssertRejectIsPure(sim, new ClaimQuest("d_watch_ad"), RejectCode.InvalidTarget, QuestRejectReasons.UNKNOWN_ID);   // P1 row, not in an MVP build
            AssertRejectIsPure(sim, new ClaimQuest("d_kills"), RejectCode.NotAvailable, QuestRejectReasons.QUEST_NOT_COMPLETED);
            AssertRejectIsPure(sim, new ClaimQuest("d_merge"), RejectCode.NotAvailable, QuestRejectReasons.QUEST_NOT_COMPLETED);   // no entry yet
            AssertRejectIsPure(sim, new ClaimAllQuests((QuestKind)99), RejectCode.InvalidTarget, QuestRejectReasons.UNKNOWN_ID);
            AssertRejectIsPure(sim, new ClaimAllQuests(QuestKind.ACHIEVEMENT), RejectCode.NotAvailable, QuestRejectReasons.NOTHING_TO_CLAIM);
        }

        [Test]
        public void AC30_BeforeTheQuestsGate_ClaimsAreLocked_ProgressStillCounts_AfterItTheyPay()
        {
            var sim = NewSim(unlocked: false);
            Progress(sim, QuestGoal.KILL_ENEMIES, 300);
            Assert.That(St(sim, "d_kills"), Is.EqualTo(QuestStatus.COMPLETED));
            AssertRejectIsPure(sim, new ClaimQuest("d_kills"), RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED);
            AssertRejectIsPure(sim, new ClaimQuest("no_such_quest"), RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED);
            AssertRejectIsPure(sim, new ClaimAllQuests(QuestKind.DAILY), RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED);
            sim.State.Progress.HighestCleared = Mvp.FeatureUnlocks["QUESTS"].Threshold.Value;
            Assert.That(sim.Apply(new ClaimQuest("d_kills")).Ok, Is.True);
            Assert.That(St(sim, "d_kills"), Is.EqualTo(QuestStatus.CLAIMED));
        }

        [Test]
        public void Claims_RunEnsureDayFirst_SoYesterdaysCompletedQuestIsGone()
        {
            var sim = NewSim();
            Progress(sim, QuestGoal.KILL_ENEMIES, 300);
            sim.State.Time.AnchorServerTime += Day;     // the virtual clock is on the next day, with no tick or sync crossing it yet
            var r = sim.Apply(new ClaimQuest("d_kills"));
            Assert.That((r.Code, r.Reason), Is.EqualTo((RejectCode.NotAvailable, QuestRejectReasons.QUEST_NOT_COMPLETED)));
            Assert.That(sim.DrainEvents().OfType<DayRolledOver>().Count(), Is.EqualTo(1), "the rollover ran inside the command");
        }

        // ------------------------------------------------------------------------------------------------ goals and filters

        [Test]
        public void Goals_IncrementFromTheirSites_SummonFilter_TrainingLevels_Kills_Release()
        {
            var sim = NewSim();
            Progress(sim, QuestGoal.SUMMON, 10, (int)Banner.SKILL);
            Assert.That((Pr(sim, "d_summon_skill"), Pr(sim, "d_summon_weapon"), Pr(sim, "w_summons")), Is.EqualTo((10L, 0L, 10L)), "w_summons is any banner");
            sim.QuestsOnDungeonCleared(Dungeon.EXP);
            Assert.That((Pr(sim, "d_dungeon_exp"), Pr(sim, "d_dungeon_fish"), Pr(sim, "w_dungeons")), Is.EqualTo((1L, 0L, 1L)));

            Combat.Give(sim, Currency.GOLD, 1e12);
            Assert.That(sim.Apply(new TrainStat(Stat.ATK, 25)).Ok, Is.True);
            Assert.That(Pr(sim, "w_train"), Is.EqualTo(25), "levels bought");

            sim.State.Fish.TankCount = 3;
            Assert.That(sim.Apply(new ReleaseFish()).Ok, Is.True);
            Assert.That(Pr(sim, "d_release"), Is.EqualTo(1));

            long kills = sim.State.Stats.Kills;
            Combat.AdvanceUntil<EnemyKilled>(sim);
            Assert.That(Pr(sim, "d_kills"), Is.EqualTo(sim.State.Stats.Kills - kills));
            Assert.That(Pr(sim, "w_kills"), Is.EqualTo(Pr(sim, "d_kills")));
        }

        [Test]
        public void AttemptBoss_CountsTheAutomaticAttempt_OnReachingABossStage()
        {
            var sim = NewSim();
            var k = Mvp.Constants;
            sim.State.Combat.Stage = k.BossEvery;
            sim.State.Combat.KillsInStage = k.KillsPerStage - 2;
            sim.State.Combat.Fight = null;
            Combat.AdvanceUntil<EnemySpawned>(sim, where: e => e.IsBoss);
            Assert.That(Pr(sim, "d_boss"), Is.EqualTo(1));
        }

        [Test]
        public void WeekliesCompleted_BothBonusRowsCompleteOnTheSeventhMission_DoNotCountEachOther_AndPayOneTicketEach()
        {
            var sim = NewSim();
            var bonus = new[] { Q("w_bonus_all_skill"), Q("w_bonus_all_companion") };
            Assert.That(Mvp.QuestsWeekly.Count(q => q.Goal != QuestGoal.WEEKLIES_COMPLETED), Is.EqualTo(7));
            Progress(sim, QuestGoal.KILL_ENEMIES, 3000);              // w_kills (and d_kills -> w_dailies 2 with d_login)
            Progress(sim, QuestGoal.SUMMON, 30, (int)Banner.WEAPON);  // w_summons (any banner)
            Progress(sim, QuestGoal.MERGE_FISH, 100);                 // w_merges
            for (int i = 0; i < 25; i++) sim.QuestsOnDungeonCleared(Dungeon.FISH_COIN);   // w_dungeons (any dungeon)
            Progress(sim, QuestGoal.TRAIN_LEVELS, 300);               // w_train
            Progress(sim, QuestGoal.LOGIN_DAYS, 4);                   // w_login: 1 + 4
            foreach (var b in bonus)
                Assert.That((sim.QuestStateOf(b), sim.QuestProgressOf(b)), Is.EqualTo((QuestStatus.IN_PROGRESS, 6L)), b.Id);
            Assert.That(Pr(sim, "w_dailies"), Is.EqualTo(5), "d_login, d_kills, d_summon_weapon, d_merge, d_dungeon_fish");
            sim.DrainEvents();
            Progress(sim, QuestGoal.DAILIES_COMPLETED, 35);           // w_dailies 40: the seventh weekly row
            Assert.That(sim.DrainEvents().OfType<QuestCompleted>().Select(e => e.QuestId).Where(id => id.StartsWith("w_")).ToArray(),
                        Is.EqualTo(new[] { "w_dailies", "w_bonus_all_skill", "w_bonus_all_companion" }), "the test helper also feeds d_bonus_8");
            foreach (var b in bonus)
                Assert.That((sim.QuestStateOf(b), sim.QuestProgressOf(b)), Is.EqualTo((QuestStatus.COMPLETED, 7L)),
                            b.Id + ": the other bonus row's completion counts toward nothing");

            Assert.That(sim.Apply(new ClaimQuest("w_bonus_all_skill")).Ok, Is.True);
            var cc = sim.DrainEvents().OfType<CurrencyChanged>().Single();
            Assert.That((cc.Currency, cc.Delta, cc.Reason), Is.EqualTo((Currency.TICKET_SUMMON_SKILL, 1.0, CurrencyReason.QUEST)));
            Assert.That(Pr(sim, "w_bonus_all_companion"), Is.EqualTo(7), "a claim counts toward nothing either");
            Assert.That(sim.Apply(new ClaimQuest("w_bonus_all_companion")).Ok, Is.True);
            cc = sim.DrainEvents().OfType<CurrencyChanged>().Single();
            Assert.That((cc.Currency, cc.Delta, cc.Reason), Is.EqualTo((Currency.TICKET_SUMMON_COMPANION, 1.0, CurrencyReason.QUEST)));
            Assert.That(sim.State.Stats.ClientTicketsEarned[Banner.SKILL][CurrencyReason.QUEST], Is.EqualTo(1));
            Assert.That(sim.State.Stats.ClientTicketsEarned[Banner.COMPANION][CurrencyReason.QUEST], Is.EqualTo(1));
        }

        [Test]
        public void ClaimAllWeekly_PaysTheSevenMissionsAndBothBonusTickets_TotalsInCurrencyOrder()
        {
            var sim = NewSim();
            Progress(sim, QuestGoal.KILL_ENEMIES, 3000);
            Progress(sim, QuestGoal.SUMMON, 30, (int)Banner.WEAPON);
            Progress(sim, QuestGoal.MERGE_FISH, 100);
            for (int i = 0; i < 25; i++) sim.QuestsOnDungeonCleared(Dungeon.FISH_COIN);
            Progress(sim, QuestGoal.TRAIN_LEVELS, 300);
            Progress(sim, QuestGoal.LOGIN_DAYS, 4);
            Progress(sim, QuestGoal.DAILIES_COMPLETED, 35);
            sim.DrainEvents();
            Assert.That(sim.Apply(new ClaimAllQuests(QuestKind.WEEKLY)).Ok, Is.True);
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<QuestClaimed>().Select(e => e.QuestId), Is.EqualTo(Mvp.QuestsWeekly.Select(q => q.Id)), "claims in sort order");
            var bulk = ev.OfType<QuestsBulkClaimed>().Single();
            Assert.That((bulk.Kind, bulk.Count), Is.EqualTo((QuestKind.WEEKLY, 9)));
            Assert.That(bulk.Totals.Select(t => (t.Currency, t.Amount)),
                        Is.EqualTo(new[] { (Currency.DIAMOND, 350.0), (Currency.TICKET_SUMMON_SKILL, 1.0), (Currency.TICKET_SUMMON_COMPANION, 1.0) }));
        }

        // ------------------------------------------------------------------------------------------------ AC 1, 06:251

        [Test]
        public void AC1_ReferenceDay_LoginEightDailiesBothBonusesAndASeventhOfTheWeeklySet_PlusFourFirstClears_Is400()
        {
            var sim = NewSim(now: T0 - 60);   // the day before, so the reference day starts with its own rollover
            sim.Apply(new ApplyServerTime(T0));
            double start = sim.State.Wallet.Diamond - Mvp.Constants.LoginDiamonds;   // the reference day's login is in
            // The F2P set (06 section 5.2): no diamond spend, the weapon pull from the daily gift ticket.
            Progress(sim, QuestGoal.KILL_ENEMIES, 300);
            Progress(sim, QuestGoal.ATTEMPT_BOSS, 3);
            Progress(sim, QuestGoal.SUMMON, 1, (int)Banner.WEAPON);
            Progress(sim, QuestGoal.RELEASE_FISH, 30);
            Progress(sim, QuestGoal.MERGE_FISH, 15);
            sim.QuestsOnDungeonCleared(Dungeon.FISH_COIN);
            sim.QuestsOnDungeonCleared(Dungeon.ROCK_CANDY);
            sim.QuestsOnDungeonCleared(Dungeon.EXP);
            int done = Mvp.QuestsDaily.Count(q => q.Goal != QuestGoal.DAILIES_COMPLETED && sim.QuestStateOf(q) != QuestStatus.IN_PROGRESS);
            Assert.That(done, Is.EqualTo(9), "06:251: F2P reaches 9 of 11 (>= 8) with no diamond spend");
            Assert.That(sim.State.Stats.DiamondsSpent, Is.EqualTo(0));

            // The reference day counts REF_DAILIES_DONE = 8 paying missions: leave the EXP dungeon's unclaimed.
            foreach (var id in new[] { "d_kills", "d_boss", "d_summon_weapon", "d_release", "d_merge", "d_dungeon_fish", "d_dungeon_candy" })
                Assert.That(sim.Apply(new ClaimQuest(id)).Ok, Is.True, id);
            Assert.That(sim.Apply(new ClaimQuest("d_bonus_5")).Ok, Is.True);
            Assert.That(sim.Apply(new ClaimQuest("d_bonus_8")).Ok, Is.True);
            Progress(sim, QuestGoal.TRAIN_LEVELS, 300);
            Assert.That(sim.Apply(new ClaimQuest("w_train")).Ok, Is.True, "one seventh of the weekly set");
            double questsAndLogin = sim.State.Wallet.Diamond - start;
            Assert.That(questsAndLogin, Is.EqualTo(50 + 7 * 20 + 30 + 70 + 50), "login + 7 claimed paying dailies + both bonuses + one weekly");

            // The eighth paying daily and the four first clears complete the day (first clears are 03's, paid per boss stage).
            Assert.That(sim.Apply(new ClaimQuest("d_dungeon_exp")).Ok, Is.True);
            var k = Mvp.Constants;
            Assert.That(sim.State.Wallet.Diamond - start + k.FirstClearDiamonds * k.RefNewBosses, Is.EqualTo(k.DiamondsPerDayF2p));
        }

        // ------------------------------------------------------------------------------------------------ id-keyed, replay

        [Test]
        public void ContentReorder_ChangesNoProgress_QuestsAreKeyedById()
        {
            var dtos = ContentLoader.LoadDtos(new FileContentSource(TestContent.Dir));
            var rows = dtos.QuestsDaily.Reverse().ToArray();
            for (int i = 0; i < rows.Length; i++) rows[i].SortOrder = i + 1;
            dtos.QuestsDaily = rows;
            var reordered = ContentDb.Build(dtos, Tier.MVP);
            Assert.That(reordered.QuestsDaily.First().Id, Is.EqualTo("d_bonus_8"));

            string Script(ContentDb db)
            {
                var sim = NewSim(db);
                Progress(sim, QuestGoal.KILL_ENEMIES, 400);
                Progress(sim, QuestGoal.SUMMON, 2, (int)Banner.SKILL);
                Progress(sim, QuestGoal.MERGE_FISH, 9);
                sim.Apply(new ClaimQuest("d_kills"));
                return sim.StateHash();
            }
            Assert.That(Script(reordered), Is.EqualTo(Script(Mvp)));
        }

        [Test]
        public void ACommandLogReplay_AcrossADayAndWeekRollover_ReproducesTheQuestStateAndHash()
        {
            var log = new ListCommandLog();
            var live = new Simulation(NewState(Mvp, T0), Mvp) { CommandLog = log };
            live.State.Progress.HighestCleared = 10;
            var genesis = NewState(Mvp, T0);
            genesis.Progress.HighestCleared = 10;
            int hz = Mvp.Constants.TickHz;

            live.Apply(new ApplyServerTime(T0 + 3 * Day + 22 * 3600));   // Sunday 22:00
            for (int round = 0; round < 40; round++)
            {
                live.Apply(new ReleaseMax());
                live.Apply(new MergeAll());
                live.Advance(5 * 60 * hz);             // kills, boss attempts, tank refills; round 24 crosses Monday 00:00 UTC
                live.Apply(new ClaimAllQuests(QuestKind.DAILY));
                live.Apply(new ClaimQuest("w_merges"));
                if (round == 20) live.Apply(new ApplyServerTime(live.VirtualNowServerSeconds + 5));
            }
            Assert.That(live.State.Time.WeekKey, Is.EqualTo(DayKeys.WeekKey((int)((T0 + 4 * Day) / Day))));
            Assert.That(live.State.Quests.Daily.Values.Any(q => q.State == QuestStatus.CLAIMED && q.Progress > 1), Is.True, "something was claimed");

            var replay = new Simulation(genesis, Mvp);
            replay.Replay(log.Records, live.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(live.StateHash()));
            Assert.That(replay.State.Quests.Daily.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Progress, kv.Value.State)),
                        Is.EqualTo(live.State.Quests.Daily.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Progress, kv.Value.State))));
        }

        [Test]
        public void OfflineCatchup_AdvancesNoQuest()
        {
            var sim = NewSim();
            Progress(sim, QuestGoal.KILL_ENEMIES, 7);
            var before = sim.State.Quests.Daily.ToDictionary(kv => kv.Key, kv => (kv.Value.Progress, kv.Value.State));
            sim.Apply(new ComputeOfflineCatchup(T0 + 6 * 3600));   // same day key
            if (sim.State.Offline.Pending != null) sim.Apply(new ClaimOfflineReward());
            Assert.That(sim.State.Quests.Daily.ToDictionary(kv => kv.Key, kv => (kv.Value.Progress, kv.Value.State)), Is.EqualTo(before));
        }
    }
}
