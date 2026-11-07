using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HeroCat.Client.Core;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>SCR_QUESTS and SCR_STAGE_SELECT view-models (07 sections 4.14-4.15; issue #183), on MVP content.</summary>
    public class QuestsAndStageSelectTests
    {
        const long Day = 86400;

        static readonly Lazy<Localizer> En = new Lazy<Localizer>(() =>
            Localizer.FromJson(File.ReadAllText(Path.Combine(TestContent.Dir, "localization.en.json"))));

        /// <summary>A booted MVP session at <paramref name="now"/> (a fake wall clock) with the client vm on top.</summary>
        static ClientFixture Open(Action<GameState> setup = null, long? now = null)
        {
            var f = new ClientFixture { S = TestSession.Open(clock: new FakeClock(now ?? TestContent.T0 + 3600), content: TestContent.Mvp) };
            setup?.Invoke(f.S.Sim.State);
            f.S.Sim.Query.MarkDirty();
            f.S.Sim.Advance(0);
            f.Vm = new GameViewModel(f.S.Runner, null, new GameVmOptions
            {
                Prefs = f.Prefs, Localizer = En.Value, Now = () => f.S.Mono.Ticks / (double)f.S.Mono.Frequency,
            });
            f.Vm.Refresh();
            f.Vm.Toasts.Clear();
            return f;
        }

        /// <summary>Past the QUESTS gate (stage 10), on frontier stage <paramref name="frontier"/>.</summary>
        static void Unlocked(GameState s, int frontier = 11)
        {
            s.Combat.Stage = frontier;
            s.Combat.KillsInStage = 0;
            s.Progress.HighestCleared = frontier - 1;
            s.Progress.HighestFirstClearBoss = (frontier - 1) / 10 * 10;
        }

        static void Completed(GameState s, string id, long progress)
        {
            var map = id.StartsWith("d_") ? s.Quests.Daily : id.StartsWith("w_") ? s.Quests.Weekly : s.Quests.Achievement;
            map[id] = new QuestProgress { Progress = progress, State = QuestStatus.COMPLETED };
        }

        static List<string> DrainToasts(ClientFixture f)
        {
            var list = new List<string>();
            double t = 1e6;
            while (f.Vm.Toasts.TryCurrent(t, out var toast)) { list.Add(toast.Display); t += 100; }
            return list;
        }

        static ClientFixture OpenQuests(ClientFixture f)
        {
            f.Vm.Router.Replace(ScreenIds.Quests);
            f.Vm.Refresh();
            return f;
        }

        // ------------------------------------------------------------------------------------------ SCR_QUESTS

        [Test]
        public void ClaimAll_ClaimsEveryCompletedRowOfTheTab_AndToastsTheSum()
        {
            var f = OpenQuests(Open(s =>
            {
                Unlocked(s);
                Completed(s, "d_kills", 300);
                Completed(s, "d_boss", 3);
                Completed(s, "w_kills", 3000);   // another tab: untouched
            }));
            var q = f.Vm.Quests;
            Assert.That(q.TabBadges[(int)QuestKind.DAILY].Count, Is.EqualTo(2));
            Assert.That(q.TabBadges[(int)QuestKind.WEEKLY].Count, Is.EqualTo(1));
            Assert.That(q.ClaimAll.Visible.Value && q.ClaimAll.Enabled.Value, Is.True);
            Assert.That(q.ClaimAll.Label.Text, Is.EqualTo("Claim all (2)"));
            long before = f.S.Sim.State.Wallet.Diamond;

            Assert.That(q.OnClaimAll().Ok, Is.True);
            f.Frame();   // the bus delivers QuestsBulkClaimed on the frame

            Assert.That(f.LoggedOf<ClaimAllQuests>().Single().Kind, Is.EqualTo(QuestKind.DAILY));
            Assert.That(f.S.Sim.State.Wallet.Diamond - before, Is.EqualTo(40));
            Assert.That(q.Rows.Single(r => r.Id == "d_kills").State.Value, Is.EqualTo(QuestRowState.Claimed));
            Assert.That(q.Rows.Single(r => r.Id == "d_boss").State.Value, Is.EqualTo(QuestRowState.Claimed));
            Assert.That(f.S.Sim.State.Quests.Weekly["w_kills"].State, Is.EqualTo(QuestStatus.COMPLETED));
            Assert.That(q.TabBadges[(int)QuestKind.DAILY].Count, Is.EqualTo(0));
            Assert.That(q.ClaimAll.Visible.Value, Is.False);
            Assert.That(DrainToasts(f), Does.Contain("+40 Diamonds"));
        }

        [Test]
        public void SummaryText_JoinsEveryCurrencyOfTheBulkClaim()
        {
            var text = QuestsVm.SummaryText(En.Value, new[]
            {
                new HeroCat.Sim.Economy.CurrencyDelta(Currency.DIAMOND, 140),
                new HeroCat.Sim.Economy.CurrencyDelta(Currency.TICKET_SUMMON_SKILL, 1),
            });
            Assert.That(text, Is.EqualTo("+140 Diamonds, +1 Skill Summon Tickets"));
        }

        [Test]
        public void DailyTab_ShowsMissionsAsRows_AndTheBonusRowsAsChips()
        {
            var f = OpenQuests(Open(s => { Unlocked(s); Completed(s, "d_kills", 300); }));
            var q = f.Vm.Quests;
            var db = TestContent.Mvp;
            int missions = db.QuestsDaily.Count(d => !QuestsVm.IsBonus(d));
            Assert.That(q.Rows.Count, Is.EqualTo(missions));
            Assert.That(q.Bonus.Select(b => b.Id), Is.EqualTo(new[] { "d_bonus_5", "d_bonus_8" }));
            Assert.That(q.MissionCount.Value, Is.EqualTo(missions));
            Assert.That(q.CompletedCount.Value, Is.EqualTo(2), "d_login (auto-claimed on login) and d_kills");
            Assert.That(q.ProgressVisible.Value, Is.True);
            var login = q.Rows.Single(r => r.Id == "d_login");
            Assert.That(login.CountsOnly && login.State.Value == QuestRowState.Claimed, Is.True, "d_login pays nothing and shows done");
            var summon = q.Rows.Single(r => r.Id == "d_summon_weapon");
            Assert.That(summon.State.Value, Is.EqualTo(QuestRowState.InProgress));
            Assert.That(summon.GoScreen, Is.EqualTo(ScreenIds.Summon));
            q.OnGo(summon);
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Summon));
        }

        [Test]
        public void ClaimOne_SendsClaimQuest_AndTheRowBecomesClaimed()
        {
            var f = OpenQuests(Open(s => { Unlocked(s); Completed(s, "d_kills", 412); }));
            var q = f.Vm.Quests;
            var row = q.Rows.Single(r => r.Id == "d_kills");
            Assert.That(row.Progress.Value, Is.EqualTo(300), "progress counts past target; the row clamps it");
            Assert.That(q.OnClaim("d_kills").Ok, Is.True);
            f.Vm.Refresh();
            Assert.That(f.LoggedOf<ClaimQuest>().Single().QuestId, Is.EqualTo("d_kills"));
            Assert.That(row.State.Value, Is.EqualTo(QuestRowState.Claimed));
        }

        [Test]
        public void ExpiryCaption_ShowsOnCompletedDailies_InTheLastHourBeforeRollover_FakeClock()
        {
            // 3,700 s before 00:00 UTC: just outside UI_QUEST_EXPIRY_WARN_SEC.
            var f = OpenQuests(Open(s => { Unlocked(s); Completed(s, "d_kills", 300); }, TestContent.T0 + Day - 3700));
            var q = f.Vm.Quests;
            QuestRowVm Kills() => q.Rows.Single(r => r.Id == "d_kills");
            Assert.That(Kills().ExpiresSoon.Value, Is.False);

            f.S.Play(200, 10);   // 3,500 s left
            f.Vm.Refresh();
            Assert.That(Kills().ExpiresSoon.Value, Is.True);
            Assert.That(q.Rows.Where(r => r.Id != "d_kills").Any(r => r.ExpiresSoon.Value), Is.False, "only COMPLETED rows warn");

            f.Vm.Toasts.Clear();
            f.S.Play(3600, 10);   // across 00:00 UTC: the rollover forfeits it
            f.Vm.Refresh();
            Assert.That(Kills().State.Value, Is.EqualTo(QuestRowState.InProgress));
            Assert.That(Kills().ExpiresSoon.Value, Is.False);
            Assert.That(DrainToasts(f), Does.Contain(En.Value.Text(UiKeys.QuestsToastReset)), "Missions reset, the screen being open");
        }

        [Test]
        public void ExpiresSoon_RuleBoundaries()
        {
            long midnight = TestContent.T0 + Day;   // 2026-01-02 00:00 UTC, a Friday
            Assert.That(QuestsVm.ExpiresSoon(midnight - 3601, (int)Day, QuestKind.DAILY, 3600), Is.False);
            Assert.That(QuestsVm.ExpiresSoon(midnight - 3600, (int)Day, QuestKind.DAILY, 3600), Is.True);
            Assert.That(QuestsVm.ExpiresSoon(midnight - 3600, (int)Day, QuestKind.WEEKLY, 3600), Is.False, "the week rolls on Monday");
            long monday = TestContent.T0 + 4 * Day;   // 2026-01-05 00:00 UTC
            Assert.That(QuestsVm.SecondsToRollover(monday - 10, (int)Day, QuestKind.WEEKLY), Is.EqualTo(10));
            Assert.That(QuestsVm.ExpiresSoon(monday - 10, (int)Day, QuestKind.WEEKLY, 3600), Is.True);
            Assert.That(QuestsVm.SecondsToRollover(monday, (int)Day, QuestKind.ACHIEVEMENT), Is.Null, "achievements never expire");
        }

        [Test]
        public void AchievementsTab_OneRowPerLadder_WithClaimedRungs_AndTheNextRungAfterAClaim()
        {
            var f = OpenQuests(Open(s =>
            {
                Unlocked(s);
                s.Quests.Achievement["ach_stage_01"] = new QuestProgress { Progress = 10, State = QuestStatus.COMPLETED };
            }));
            var q = f.Vm.Quests;
            q.OnTab(QuestKind.ACHIEVEMENT);
            f.Vm.Refresh();
            var db = TestContent.Mvp;
            Assert.That(q.Rows.Count, Is.EqualTo(f.S.Sim.AchievementRows().Count));
            Assert.That(q.Rows.Select(r => r.Def.Ladder).Distinct().Count(), Is.EqualTo(q.Rows.Count), "one row per ladder");
            Assert.That(q.ProgressVisible.Value, Is.False);
            Assert.That(q.Bonus, Is.Empty);
            var stage = q.Rows.Single(r => r.Def.Ladder == "stage");
            Assert.That(stage.Id, Is.EqualTo("ach_stage_01"));
            int rungs = db.AchievementLadders["stage"].Count;
            Assert.That(stage.Rungs.Text, Is.EqualTo($"0 / {rungs} rungs"));

            int version = q.RowsVersion;
            Assert.That(q.OnClaim("ach_stage_01").Ok, Is.True);
            f.Vm.Refresh();
            Assert.That(q.RowsVersion, Is.GreaterThan(version), "the claimed rung is replaced by the next one");
            var next = q.Rows.Single(r => r.Def.Ladder == "stage");
            Assert.That(next.Id, Is.EqualTo("ach_stage_02"));
            Assert.That(next.Rungs.Text, Is.EqualTo($"1 / {rungs} rungs"));
        }

        [Test]
        public void Locked_BeforeTheQuestsUnlock_TabToastsAndTheScreenRefusesClaims()
        {
            var f = Open(s => Completed(s, "d_kills", 300));   // a new profile: stage 1
            Assert.That(f.Vm.Main.TabLocked[(int)MainTab.Quests].Value, Is.True);
            f.Vm.Main.OnTab(MainTab.Quests);
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Main), "a locked tab does not open");
            Assert.That(DrainToasts(f), Does.Contain(En.Value.Text("feature.quests.locked")));

            f.Vm.Router.Replace(ScreenIds.Quests);   // a deep link still shows the Locked state
            f.Vm.Refresh();
            var q = f.Vm.Quests;
            Assert.That(q.Locked.Value, Is.True);
            Assert.That(q.LockedText.Text, Is.EqualTo(En.Value.Text("feature.quests.locked")));
            Assert.That(q.ClaimAll.Enabled.Value, Is.False);
            Assert.That(q.OnClaimAll().Ok, Is.False);
            Assert.That(f.Logged<ClaimAllQuests>(), Is.EqualTo(0), "no interaction while locked");
        }

        [Test]
        public void UnlockedTab_OpensQuests()
        {
            var f = Open(s => Unlocked(s));
            f.Vm.Main.OnTab(MainTab.Quests);
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Quests));
            Assert.That(f.Vm.Main.ActiveTab.Value, Is.EqualTo(-1), "set on the next refresh");
            f.Vm.Refresh();
            Assert.That(f.Vm.Main.ActiveTab.Value, Is.EqualTo((int)MainTab.Quests));
        }

        [Test]
        public void Rollover_RebuildsEveryRow()
        {
            var f = OpenQuests(Open(s => { Unlocked(s); Completed(s, "d_kills", 300); }, TestContent.T0 + Day - 30));
            var q = f.Vm.Quests;
            int version = q.RowsVersion;
            var old = q.Rows.Single(r => r.Id == "d_kills");
            Assert.That(old.State.Value, Is.EqualTo(QuestRowState.Completed));
            f.S.Play(60, 10);
            f.Vm.Refresh();
            Assert.That(q.RowsVersion, Is.GreaterThan(version), "DayRolledOver rebuilds the list");
            Assert.That(q.Rows.Single(r => r.Id == "d_kills").State.Value, Is.EqualTo(QuestRowState.InProgress));
            Assert.That(q.Rows.Single(r => r.Id == "d_login").State.Value, Is.EqualTo(QuestRowState.Claimed), "the new day's login counted");
        }

        [Test]
        public void GoScreen_CoversEveryGoal()
        {
            Assert.That(QuestsVm.GoScreenOf(QuestGoal.KILL_ENEMIES), Is.EqualTo(ScreenIds.Main));
            Assert.That(QuestsVm.GoScreenOf(QuestGoal.TRAIN_LEVELS), Is.EqualTo(ScreenIds.Party));
            Assert.That(QuestsVm.GoScreenOf(QuestGoal.CLEAR_DUNGEON), Is.EqualTo(ScreenIds.Dungeons));
            Assert.That(QuestsVm.GoScreenOf(QuestGoal.LOGIN), Is.Null);
            foreach (QuestGoal g in Enum.GetValues(typeof(QuestGoal))) Assert.DoesNotThrow(() => QuestsVm.GoScreenOf(g));
        }

        // ------------------------------------------------------------------------------------------ SCR_STAGE_SELECT

        static ClientFixture OpenStageSelect(Action<GameState> setup)
        {
            var f = Open(setup);
            f.Vm.Main.Combat.OnStageLabel();
            Assert.That(f.Vm.Router.Modal?.Id, Is.EqualTo(ScreenIds.StageSelect), "the stage label tap opens the modal");
            f.Vm.Refresh();
            return f;
        }

        [Test]
        public void StageChips_RunToTheFrontier_AndOnlyClearedStagesAreOffered()
        {
            var f = OpenStageSelect(s => Unlocked(s, 57));
            var v = f.Vm.StageSelect;
            var k = TestContent.Mvp.Constants;
            Assert.That(v.HighestCleared.Value, Is.EqualTo(56));
            Assert.That(v.Frontier.Value, Is.EqualTo(57));
            Assert.That(v.Chapter.Value, Is.EqualTo(k.ChapterOf(57)), "opens on the fought stage's chapter");
            Assert.That(v.Chapters.Count, Is.EqualTo(k.ChapterOf(57)));
            Assert.That(v.Stages.First().Number, Is.EqualTo((k.ChapterOf(57) - 1) * k.ChapterWidth + 1));
            Assert.That(v.Stages.Last().Number, Is.EqualTo(57), "no chip above the frontier");
            Assert.That(v.Stages.Last().IsFrontier && !v.Stages.Last().Cleared, Is.True);
            Assert.That(v.Stages.Where(c => c.Number <= 56).All(c => c.Cleared), Is.True);
            Assert.That(v.Stages.Single(c => c.IsSelected).Number, Is.EqualTo(57));
            Assert.That(v.Return.Visible.Value, Is.False);

            // n > HighestCleared (and not the frontier) is never sent.
            Assert.That(v.OnStage(58).Ok, Is.False);
            Assert.That(f.Logged<SelectStage>(), Is.EqualTo(0));
            Assert.That(StageSelectVm.Selectable(58, 56, 57), Is.False);
            Assert.That(StageSelectVm.Selectable(0, 56, 57), Is.False);
            Assert.That(StageSelectVm.Selectable(56, 56, 57) && StageSelectVm.Selectable(57, 56, 57), Is.True);

            Assert.That(v.OnStage(55).Ok, Is.True);
            f.Vm.Refresh();
            Assert.That(f.LoggedOf<SelectStage>().Single().Stage, Is.EqualTo(55));
            Assert.That(v.Selected.Value, Is.EqualTo(55));
            Assert.That(v.Stages.Single(c => c.IsSelected).Number, Is.EqualTo(55));
            Assert.That(v.Return.Visible.Value, Is.True);
            Assert.That(v.FarmingLine.Text, Is.EqualTo("Farming: 55"));
            Assert.That(f.S.Sim.State.Combat.Stage, Is.EqualTo(57), "the frontier is kept");
            Assert.That(f.S.Sim.State.Progress.HighestCleared, Is.EqualTo(56), "rewards still read the highest stage");

            Assert.That(v.OnStage(57).Ok, Is.True, "the frontier chip equals Return");
            f.Vm.Refresh();
            Assert.That(f.LoggedOf<SelectStage>().Last().Stage, Is.Null);
            Assert.That(v.Return.Visible.Value, Is.False);
            Assert.That(v.FarmingLine.Text, Is.Empty);
        }

        [Test]
        public void ChapterDropdown_ShowsThatChaptersCleared_Stages()
        {
            var f = OpenStageSelect(s => Unlocked(s, 57));
            var v = f.Vm.StageSelect;
            int width = TestContent.Mvp.Constants.ChapterWidth;
            int version = v.Version;
            v.OnChapter(1);
            f.Vm.Refresh();
            Assert.That(v.Version, Is.GreaterThan(version));
            Assert.That(v.Stages.Count, Is.EqualTo(width));
            Assert.That(v.Stages.All(c => c.Cleared && !c.IsFrontier), Is.True);
            Assert.That(v.ChapterLine.Text, Is.EqualTo($"Chapter 1 (1-{width})"));
            Assert.That(v.Stages.Where(c => c.IsBoss).Select(c => c.Number), Is.All.Matches<int>(n => TestContent.Mvp.Constants.IsBossStage(n)));
            v.OnChapter(99);
            f.Vm.Refresh();
            Assert.That(v.Chapter.Value, Is.EqualTo(TestContent.Mvp.Constants.ChapterOf(57)), "clamped to the frontier's chapter");
        }

        [Test]
        public void Walled_ChallengeBoss_AttemptsTheFrontierBoss_AndCloses()
        {
            var f = OpenStageSelect(s =>
            {
                Unlocked(s, 20);
                s.Combat.KillsInStage = TestContent.Mvp.Constants.KillsPerStage - 1;
                s.Combat.Boss = BossPhase.Walled;
                s.Combat.Selected = 12;
            });
            var v = f.Vm.StageSelect;
            Assert.That(v.Walled.Value && v.Challenge.Visible.Value, Is.True);
            Assert.That(v.FrontierLine.Text, Is.EqualTo("Frontier: 20 (walled)"));
            Assert.That(v.OnChallenge().Ok, Is.True);
            Assert.That(f.Logged<AttemptBoss>(), Is.EqualTo(1));
            Assert.That(f.S.Sim.State.Combat.Selected, Is.Null, "the boss challenge returns to the frontier");
            Assert.That(f.Vm.Router.Modal, Is.Null);
        }

        [Test]
        public void OfflineNote_IsShown()
        {
            Assert.That(En.Value.Text(UiKeys.StageSelectOfflineNote), Is.EqualTo("Rewards for offline time use your highest stage"));
        }
    }
}
