using System;
using System.Linq;
using HeroCat.Client.Core;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>SCR_RESEARCH and its entry points (07 sections 4.3, 4.4, 4.7 and 4.9; issue #181).</summary>
    public class ResearchScreenTests
    {
        static ClientFixture Mvp(int awakenRank = 1, double fishCoin = 0, Action<GameState> more = null) => ClientFixture.Open(s =>
        {
            s.Party.Slots[0].AwakenRank = awakenRank;   // test setup only: the sim's commands are the one path in play
            s.Wallet.FishCoin = fishCoin;
            more?.Invoke(s);
        }, content: TestContent.Mvp);

        static ResearchVm Open(ClientFixture f)
        {
            f.Vm.Hud.OnFishCoinChip();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Research));
            f.Vm.Refresh();
            return f.Vm.Research;
        }

        [Test]
        public void M0Tier_HidesTheEntryPoints_AndTheChipToastsInsteadOfOpening()
        {
            var f = ClientFixture.Open();
            Assert.That(f.Vm.Research.Rows, Is.Empty, "M0 content has no Research rows");
            Assert.That(f.Vm.Main.Research.Visible.Value, Is.False);
            f.Vm.Hud.OnFishCoinChip();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Main));
            Assert.That(f.Vm.Toasts.Current(f.Now), Is.Not.Null);
        }

        [Test]
        public void BeforeTheGate_TheScreenIsLocked_WithItsCondition_AndEveryBuyIsDisabled()   // AC 3
        {
            var f = Mvp(awakenRank: 0, fishCoin: 1e9);
            var r = Open(f);
            Assert.That(r.Locked.Value, Is.True);
            Assert.That(r.LockedLine.Key, Is.EqualTo(TestContent.Mvp.FeatureUnlocks["RESEARCH"].LockedTextKey));
            Assert.That(r.CoachMark.Value, Is.False, "the coach mark waits for the unlock");
            Assert.That(r.Rows, Has.Count.EqualTo(TestContent.Mvp.Research.Count));
            foreach (var row in r.Rows)
            {
                Assert.That(row.Buy.Enabled.Value, Is.False, row.NodeId);
                Assert.That(row.Buy.Reason.Key, Is.EqualTo(r.LockedLine.Key), row.NodeId);
            }
            r.OnBuy("res_tank_cap");
            Assert.That(f.Vm.Toasts.Current(f.Now), Does.Contain("reject.research_locked"), "a forced tap still goes to the sim, which rejects");
        }

        [Test]
        public void FishBoardButton_IsLockedWithAToast_WhileThePartyAndCompanionEntriesOpenTheLockedScreen()   // 07 sections 4.3, 4.4, 4.7
        {
            var f = Mvp(awakenRank: 0);
            var board = f.Vm.Main.Research;
            Assert.That((board.Visible.Value, board.Locked.Value), Is.EqualTo((true, true)));
            board.OnTap();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Main), "locked: a toast, no push");
            Assert.That(f.Vm.Toasts.Current(f.Now), Does.Contain("feature.research.locked"));

            f.Vm.Router.Replace(ScreenIds.Party);
            f.Vm.Refresh();
            f.Vm.Party.Research.OnTap();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Research));
            f.Vm.Router.PopFull();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Party), "Research is pushed: back returns");

            f.Vm.Router.Replace(ScreenIds.Companions);
            f.Vm.Refresh();
            Assert.That(f.Vm.Companions.Research.Visible.Value, Is.True);
            f.Vm.Companions.Research.OnTap();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Research));

            var open = Mvp(awakenRank: 1);
            open.Vm.Main.Research.OnTap();
            Assert.That(open.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Research), "after the gate the Fish Board button opens it");
        }

        [Test]
        public void Rows_FollowTheLayout_WithLevelCurrentNextAndCost()   // 07 section 4.9, 03 section 6
        {
            var f = Mvp(fishCoin: 812);
            var r = Open(f);
            var db = TestContent.Mvp;
            var expected = db.Research.Values.OrderBy(n => n.LayoutRow).ThenBy(n => n.LayoutCol).Select(n => n.Id).ToList();
            Assert.That(r.Rows.Select(x => x.NodeId), Is.EqualTo(expected));
            Assert.That(r.Rows[0].NodeId, Is.EqualTo("res_tank_cap"), "Tank capacity first, as in the wireframe");
            Assert.That(r.Locked.Value, Is.False);
            Assert.That(r.FishCoin.Value, Is.EqualTo(812));

            var tank = r.Row("res_tank_cap");
            Assert.That((tank.Level.Value, tank.MaxLevel.Value), Is.EqualTo((0L, 10L)));
            Assert.That((tank.CurrentText.Text, tank.NextText.Text), Is.EqualTo(("10", "15")));
            Assert.That(tank.Cost.Value, Is.EqualTo(200));
            Assert.That(tank.Name.Key, Is.EqualTo("research.res_tank_cap.name"));
            Assert.That((tank.Buy.Enabled.Value, tank.CanAfford.Value, tank.Buy.Label.Key), Is.EqualTo((true, true, UiKeys.ResearchBuy)));
            Assert.That(tank.Buy.Reason.Key, Is.Null);

            var refill = r.Row("res_tank_refill");
            Assert.That((refill.CurrentText.Text, refill.NextText.Text), Is.EqualTo(("6.0s", "5.6s")));
            var offline = r.Row("res_offline_cap");
            Assert.That((offline.CurrentText.Key, offline.CurrentText.Arg(0).ToString(), offline.NextText.Arg(0).ToString()),
                        Is.EqualTo((UiKeys.ResearchHours, "24", "26")));
            var regen = r.Row("res_mp_regen");
            Assert.That(regen.CurrentText.Key, Is.EqualTo(UiKeys.ResearchPerSec));
            Assert.That(r.Row("res_atk").NextText.Text, Is.EqualTo("+5.0%"));
        }

        [Test]
        public void DisabledBuy_NamesTheReason_NeedMoreOrMaxLevel()   // AC 4
        {
            var f = Mvp(fishCoin: 100, more: s => s.Research.Level["res_offline_cap"] = 6);
            var r = Open(f);
            var tank = r.Row("res_tank_cap");
            Assert.That((tank.Buy.Enabled.Value, tank.CanAfford.Value), Is.EqualTo((false, false)));
            Assert.That(tank.Buy.Reason.Key, Is.EqualTo(UiKeys.NeedMore));
            Assert.That(tank.Buy.Reason.Arg(0).ToString(), Is.EqualTo("100"), "200 - 100");

            var off = r.Row("res_offline_cap");
            Assert.That((off.AtMax.Value, off.Buy.Enabled.Value), Is.EqualTo((true, false)));
            Assert.That((off.Buy.Label.Key, off.Buy.Reason.Key), Is.EqualTo((UiKeys.ResearchMax, UiKeys.ResearchMaxLevel)));
            Assert.That(off.NextText.Text, Is.Empty);
            Assert.That(off.Cost.Value, Is.EqualTo(0));
        }

        [Test]
        public void Buy_SendsResearch_FlashesTheRow_AndTheTankGaugeFollowsTheSnapshot()   // AC 2
        {
            var f = Mvp(fishCoin: 1000);
            var r = Open(f);
            int cap0 = (int)f.Vm.Board.TankCap.Value;
            var tank = r.Row("res_tank_cap");
            int serial = tank.FlashSerial;
            r.OnBuy("res_tank_cap");
            Assert.That(f.LoggedOf<Research>().Single().NodeId, Is.EqualTo("res_tank_cap"));
            f.Frame();
            Assert.That(tank.FlashSerial, Is.EqualTo(serial + 1), "ResearchLeveled flashes the row");
            Assert.That(tank.Level.Value, Is.EqualTo(1));
            Assert.That(r.FishCoin.Value, Is.EqualTo(800));
            Assert.That(f.Vm.Board.TankCap.Value, Is.EqualTo(cap0 + 5), "SCR_MAIN's tank gauge reads the new cap from the snapshot");
        }

        [Test]
        public void CoachMark_ShowsOnceAfterTheUnlock_AndStaysDismissed()   // 07 section 4.9 first-time
        {
            var f = Mvp(fishCoin: 1000);
            var r = Open(f);
            Assert.That(r.CoachMark.Value, Is.True);
            Assert.That(r.CoachText.Key, Is.EqualTo(UiKeys.ResearchCoachTank));
            r.OnCoachMarkDismissed();
            f.Vm.Refresh();
            Assert.That(r.CoachMark.Value, Is.False);
            Assert.That(f.Prefs.SeenCoachMark(ResearchVm.CoachMarkId), Is.True);
        }
    }
}
