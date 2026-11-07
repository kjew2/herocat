using System;
using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Runtime.Inbox;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>
    /// The red-dot policy and the shared Locked presentation (07 section 10 rules 2 and 8, section 3, section 4.3 Locked
    /// elements, AC 18; issue #180).
    /// </summary>
    public class BadgesAndLockedTests
    {
        /// <summary>An MVP-content session (Quests, Dungeons, Research, Shop rows) with the client on top.</summary>
        static ClientFixture OpenMvp(Action<GameState> setup = null)
        {
            var f = new ClientFixture { S = TestSession.Open(content: TestContent.Mvp) };
            setup?.Invoke(f.S.Sim.State);
            f.S.Sim.Query.MarkDirty();
            f.S.Sim.Advance(0);
            f.Vm = new GameViewModel(f.S.Runner, null, new GameVmOptions
            {
                Prefs = f.Prefs,
                Now = () => f.S.Mono.Ticks / (double)f.S.Mono.Frequency,
            });
            f.Vm.Refresh();
            f.Vm.Toasts.Clear();
            return f;
        }

        static void ClearQuests(GameState s)
        {
            s.Quests.Daily.Clear();
            s.Quests.Weekly.Clear();
            s.Quests.Achievement.Clear();
        }

        static LocalInboxService LocalInbox(ClientFixture f) => (LocalInboxService)f.S.Runner.Services.Inbox;

        static InboxItem Mail(string id) =>
            new InboxItem(id, new InboxGrantPayload(id, new[] { new GrantLine("currency", "DIAMOND", 100) }, CurrencyReason.MAIL),
                          InboxRules.NeverExpires, 0, "MAIL");

        static string ToastText(ClientFixture f) => f.Vm.Toasts.TryCurrent(f.Now, out var t) ? t.Text : null;

        [Test]
        public void AC18_OneOfEach_Shows111_EachClaimClearsItsDotImmediately_AndAffordableTrainingNeverDots()
        {
            var f = OpenMvp(s =>
            {
                s.Progress.HighestCleared = 30;   // Quests (10) and Dungeons (30) open
                ClearQuests(s);
                s.Quests.Daily["d_kills"] = new QuestProgress { Progress = 300, State = QuestStatus.COMPLETED };
                s.Wallet.DungeonTicket = new[] { 1, 0, 0 };
                s.Dungeons.BestFloor[(int)Dungeon.FISH_COIN] = 1;   // something to sweep
                s.Shop.DailyGiftClaimed = true;   // keep the Shop tab out of this count
                s.Wallet.Gold = 1e30;             // every Training level affordable
            });
            LocalInbox(f).Add(Mail("ibx_one"));
            f.S.Runner.Inbox.RefreshAsync().GetAwaiter().GetResult();
            f.Vm.Refresh();

            var m = f.Vm.Main;
            Assert.That((m.QuestsPill.Count, m.DungeonsPill.Count, m.InboxPill.Count), Is.EqualTo((1, 1, 1)));
            Assert.That(m.TabBadges[(int)MainTab.Quests].Count, Is.EqualTo(1));
            Assert.That(m.TabBadges[(int)MainTab.Dungeons].Count, Is.EqualTo(1));
            Assert.That(f.Vm.Hud.Inbox.Count, Is.EqualTo(1), "HUD inbox badge");
            Assert.That(m.TabBadges[(int)MainTab.Party].Visible, Is.False, "no dot for affordable Training");
            Assert.That(f.Vm.Send(new TrainStat(Stat.ATK, 1)).Ok, Is.True, "Training was affordable");
            f.Vm.Refresh();
            Assert.That(m.TabBadges[(int)MainTab.Party].Visible, Is.False);

            Assert.That(f.Vm.Send(new ClaimQuest("d_kills")).Ok, Is.True);
            f.Vm.Refresh();   // no tick: the same frame
            Assert.That(m.QuestsPill.Visible, Is.False);
            Assert.That(m.TabBadges[(int)MainTab.Quests].Visible, Is.False);

            Assert.That(f.S.Runner.Inbox.ClaimAsync("ibx_one").GetAwaiter().GetResult().Applied, Is.True);
            f.Vm.Refresh();
            Assert.That(m.InboxPill.Visible, Is.False);
            Assert.That(f.Vm.Hud.Inbox.Visible, Is.False);

            Assert.That(f.Vm.Send(new SweepDungeon(Dungeon.FISH_COIN)).Ok, Is.True);   // uses the ticket
            f.Vm.Refresh();
            Assert.That(m.DungeonsPill.Visible, Is.False);
            Assert.That(m.TabBadges[(int)MainTab.Dungeons].Visible, Is.False);
        }

        [Test]
        public void NoDot_BehindALockedGate_OrAnOffKillSwitch()   // a dot means the thing can be taken now
        {
            var f = OpenMvp(s =>
            {
                s.Progress.HighestCleared = 5;   // below Quests (10) and Dungeons (30)
                ClearQuests(s);
                s.Quests.Daily["d_kills"] = new QuestProgress { Progress = 300, State = QuestStatus.COMPLETED };
                s.Wallet.DungeonTicket = new[] { 2, 2, 2 };
            });
            var m = f.Vm.Main;
            Assert.That(m.QuestsPill.Visible, Is.False, "missions completed before the gate wait for it");
            Assert.That(m.DungeonsPill.Visible, Is.False);

            f.S.Sim.State.Progress.HighestCleared = 30;
            f.Vm.Refresh();
            Assert.That(m.QuestsPill.Count, Is.EqualTo(1), "claimable once the gate opens (06 section 5.1)");
            Assert.That(m.DungeonsPill.Count, Is.EqualTo(6));

            var off = new KillSwitches(false, false, true, true, new Dictionary<Banner, bool>());
            f.Vm.Context.Flags = () => off;
            f.Vm.Refresh();
            Assert.That(m.DungeonsPill.Visible, Is.False, "dungeonsEnabled off");
            Assert.That(m.TabBadges[(int)MainTab.Shop].Visible, Is.False, "shopEnabled off");
        }

        [Test]
        public void DailyGift_DotsTheShopTab_UntilClaimed()
        {
            var f = OpenMvp(s => s.Shop.DailyGiftClaimed = false);
            var m = f.Vm.Main;
            Assert.That(m.TabBadges[(int)MainTab.Shop].Count, Is.EqualTo(1));
            Assert.That(f.Vm.Send(new ClaimDailyGift()).Ok, Is.True);
            f.Vm.Refresh();
            Assert.That(m.TabBadges[(int)MainTab.Shop].Visible, Is.False);
        }

        [Test]
        public void NewCollectibles_DotTheCollectionTab_UntilSeen_AcrossAllThreeCollections()
        {
            var f = OpenMvp();
            var m = f.Vm.Main;
            Assert.That(m.TabBadges[(int)MainTab.Collection].Visible, Is.False);
            f.Prefs.MarkNew(Banner.COMPANION, "c_any");
            f.Prefs.MarkNew(Banner.WEAPON, "w_any");
            f.Vm.Refresh();
            Assert.That(m.TabBadges[(int)MainTab.Collection].Count, Is.EqualTo(2));
            f.Prefs.ClearNew(Banner.COMPANION, "c_any");
            f.Prefs.ClearNew(Banner.WEAPON, "w_any");
            f.Vm.Refresh();
            Assert.That(m.TabBadges[(int)MainTab.Collection].Visible, Is.False, "cleared when seen, not on a timer");
        }

        [Test]
        public void ResearchButton_LockedBeforeAwakening1_ThenDotsAnAffordableNode()
        {
            var f = OpenMvp(s => s.Wallet.FishCoin = 1e12);
            var m = f.Vm.Main;
            Assert.That(m.ResearchVisible.Value, Is.True, "MVP content has Research nodes");
            Assert.That(m.ResearchLock.IsLocked, Is.True);
            Assert.That(m.ResearchLock.Condition.Key, Is.EqualTo("feature.research.locked"));
            Assert.That(m.Badges.ResearchAffordable.Visible, Is.False, "no dot while locked, however many coins");

            m.OnResearch();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Main), "a locked tap opens nothing");
            Assert.That(ToastText(f), Is.EqualTo("feature.research.locked"), "the toast names the condition");

            f.S.Sim.State.Party.Slots[0].AwakenRank = 1;
            f.Vm.Refresh();
            Assert.That(m.ResearchLock.IsLocked, Is.False);
            Assert.That(m.Badges.ResearchAffordable.Visible, Is.True);
            m.OnResearch();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Research));

            f.S.Sim.State.Wallet.FishCoin = 0;
            f.Vm.Refresh();
            Assert.That(m.Badges.ResearchAffordable.Visible, Is.False);
        }

        [Test]
        public void LockedTabs_AreShownWithTheirCondition_TapToastsIt_AndOpensOnceUnlocked()   // rule 8
        {
            var f = OpenMvp(s => s.Progress.HighestCleared = 0);
            var m = f.Vm.Main;
            var quests = m.TabLocks[(int)MainTab.Quests];
            Assert.That(quests.IsLocked, Is.True);
            Assert.That(m.TabLocked[(int)MainTab.Quests], Is.SameAs(quests.Locked), "the tab's lock glyph flag");
            Assert.That(quests.Condition.Key, Is.EqualTo("feature.quests.locked"));
            Assert.That(m.TabLocks[(int)MainTab.Dungeons].Condition.Key, Is.EqualTo("feature.dungeons.locked"));
            Assert.That(m.TabLocked[(int)MainTab.Shop].Value, Is.False, "the Shop is never locked");

            m.OnTab(MainTab.Quests);
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Main));
            Assert.That(ToastText(f), Is.EqualTo("feature.quests.locked"));
            f.Vm.Toasts.Clear();
            m.OnQuestsPill();
            Assert.That(ToastText(f), Is.EqualTo("feature.quests.locked"), "the pill takes the tab's route");

            f.S.Sim.State.Progress.HighestCleared = 10;
            f.Vm.Refresh();
            Assert.That(quests.IsLocked, Is.False);
            m.OnTab(MainTab.Quests);
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Quests));
            Assert.That(m.TabLocks[(int)MainTab.Dungeons].IsLocked, Is.True, "Dungeons still need stage 30");
        }

        [Test]
        public void RockCandyChip_IsShownLocked_UntilTheFirstAwakeningStage()   // rule 8: shown, never hidden
        {
            var f = OpenMvp();
            var hud = f.Vm.Hud;
            int gate = TestContent.Mvp.Awakening[0].StageGate;
            Assert.That(hud.RockCandyVisible.Value, Is.True);
            Assert.That(hud.RockCandyLock.IsLocked, Is.True);
            Assert.That(hud.RockCandyLock.Condition.Key, Is.EqualTo(UiKeys.PartyAwakenGate));
            Assert.That(hud.RockCandyLock.Condition.Text, Does.Contain(gate.ToString()));
            hud.OnRockCandyChip();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Main));
            Assert.That(ToastText(f), Is.Not.Null);

            f.S.Sim.State.Progress.HighestCleared = gate;
            f.Vm.Refresh();
            Assert.That(hud.RockCandyLock.IsLocked, Is.False);
            hud.OnRockCandyChip();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Party));
        }

        [Test]
        public void M0_FeaturesWithoutARow_AreLockedAsComingSoon_AndDotFree()
        {
            var f = ClientFixture.Open();
            var m = f.Vm.Main;
            Assert.That(m.TabLocks[(int)MainTab.Quests].IsLocked, Is.True);
            Assert.That(m.TabLocks[(int)MainTab.Quests].Condition.Key, Is.EqualTo(UiKeys.ComingSoon));
            Assert.That(m.ResearchVisible.Value, Is.False, "the [Research] button is MVP");
            Assert.That(f.Vm.Hud.RockCandyVisible.Value, Is.False, "M0 has no Rock Candy");
            for (int i = 0; i < m.TabBadges.Length; i++) Assert.That(m.TabBadges[i].Visible, Is.False, ((MainTab)i).ToString());
            Assert.That(m.QuestsPill.Visible || m.DungeonsPill.Visible || m.InboxPill.Visible, Is.False);
        }

        [Test]
        public void LockedVm_WithoutAFeatureRow_TakesTheCallersCondition()   // the reusable helper other screens call
        {
            var f = OpenMvp();
            var l = new LockedVm(f.Vm.Context);
            Assert.That(l.Tap(), Is.True, "unlocked by default: the caller acts");
            l.Set(true, UiKeys.PartyAwakenGate, 150);
            Assert.That(l.Tap(), Is.False);
            Assert.That(ToastText(f), Is.EqualTo(l.Condition.Text));
            Assert.That(LockedVm.ConditionKey(TestContent.Mvp, "NO_SUCH_FEATURE"), Is.EqualTo(UiKeys.ComingSoon));
            Assert.That(LockedVm.ConditionKey(TestContent.Mvp, "SHOP"), Is.EqualTo(UiKeys.ComingSoon), "a row with no text key");
        }

        [Test]
        public void MvpRefresh_WritesOnlyChanges_AndAllocatesNothing()   // AC 12 and AC 20 with every badge source live
        {
            var f = OpenMvp(s =>
            {
                s.Progress.HighestCleared = 30;
                s.Wallet.FishCoin = 1e12;
                s.Party.Slots[0].AwakenRank = 1;
                s.Wallet.DungeonTicket = new[] { 1, 1, 1 };
            });
            LocalInbox(f).Add(Mail("ibx_a"));
            f.S.Runner.Inbox.RefreshAsync().GetAwaiter().GetResult();
            f.Frames(120);

            var counters = new List<Func<int>>();
            int bound = SinkBinder.BindAll(f.Vm.Main.Badges, counters) + SinkBinder.BindAll(f.Vm.Main.ResearchLock, counters)
                        + SinkBinder.BindAll(f.Vm.Hud.RockCandyLock, counters);
            foreach (var l in f.Vm.Main.TabLocks) bound += SinkBinder.BindAll(l, counters);
            Assert.That(bound, Is.GreaterThan(10));
            f.Vm.Refresh();
            int after1 = SinkBinder.Total(counters);
            f.Vm.Refresh();
            f.Vm.Refresh();
            Assert.That(SinkBinder.Total(counters), Is.EqualTo(after1), "nothing changed, nothing written");

            long total = 0;
            for (int i = 0; i < 300; i++)
            {
                f.S.Frame(1.0 / 60);
                long before = GC.GetAllocatedBytesForCurrentThread();
                f.Vm.Frame();
                f.Vm.Refresh();
                total += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.That(total, Is.EqualTo(0), "Frame + Refresh with quests, tickets, inbox and Research live");
        }
    }
}
