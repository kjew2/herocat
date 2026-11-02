using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Client.Core;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>SCR_MAIN: HUD, combat band, Tap Zap, damage numbers (07 section 4.3, AC 12, 13, 20; issues #111, #113).</summary>
    public class MainScreenTests
    {
        [Test]
        public void TenTapsInOneSecond_SendAtMostTapZapMaxPerSec()
        {
            var f = ClientFixture.Open();
            int max = TestContent.Db.Constants.TapZapMaxPerSec;
            int accepted = 0;
            for (int i = 0; i < 10; i++)
            {
                if (f.Vm.Combat.Tap(f.Now)) accepted++;
                f.Frame(0.09);
            }
            var taps = f.LoggedOf<TapZap>();
            Assert.That(accepted, Is.EqualTo(max));
            Assert.That(taps.Count, Is.LessThanOrEqualTo(max));
            Assert.That(taps.Sum(t => t.Count), Is.EqualTo(max), "extra taps play the VFX but send nothing");
        }

        [Test]
        public void TapsInOneFrame_AreOneTapZap()
        {
            var f = ClientFixture.Open();
            for (int i = 0; i < 3; i++) f.Vm.Combat.Tap(f.Now);
            f.Frame();
            Assert.That(f.LoggedOf<TapZap>().Single().Count, Is.EqualTo(3));
            f.Frames(5);
            Assert.That(f.Logged<TapZap>(), Is.EqualTo(1), "no taps, no command");
        }

        [Test]
        public void AC13_DamageNumberPool_NeverExceeds32_RecyclesTheOldest_MergesSameSourceInATick()
        {
            var pool = new DamageNumberPool();
            for (int i = 0; i < 100; i++) pool.Add(10, false, DamageSource.Attack, i, i * 0.001);
            Assert.That(pool.LiveCount, Is.EqualTo(UiConstants.DamageLabelPool));
            Assert.That(pool.Recycled, Is.EqualTo(100 - UiConstants.DamageLabelPool));
            double oldest = double.MaxValue;
            for (int i = 0; i < pool.Capacity; i++) oldest = Math.Min(oldest, pool[i].SpawnTime);
            Assert.That(oldest, Is.EqualTo((100 - UiConstants.DamageLabelPool) * 0.001).Within(1e-9), "the oldest were recycled");

            var p2 = new DamageNumberPool();
            p2.Add(5, false, DamageSource.Attack, 7, 0);
            int slot = p2.Add(6, true, DamageSource.Attack, 7, 0);
            p2.Add(1, false, DamageSource.Fish, 7, 0);
            Assert.That(p2.LiveCount, Is.EqualTo(2));
            Assert.That(p2[slot].Amount, Is.EqualTo(11));
            Assert.That(p2[slot].Crit, Is.True);
            p2.Update(UiConstants.DamageNumberLifeSec);
            Assert.That(p2.LiveCount, Is.EqualTo(0));
        }

        [Test]
        public void Farming_FillsDamageNumbersFromHitLanded_WithinThePool()
        {
            var f = ClientFixture.Open();
            for (int i = 0; i < 300; i++)
            {
                f.Frame();
                Assert.That(f.Vm.Combat.DamageNumbers.LiveCount, Is.LessThanOrEqualTo(UiConstants.DamageLabelPool));
            }
            Assert.That(f.Vm.Combat.DamageNumbers.Recycled + f.Vm.Combat.DamageNumbers.LiveCount, Is.GreaterThan(0));
        }

        [Test]
        public void BossButton_VisibleOnlyWhileWalled_LabelFromTheNeedRatio()
        {
            var f = ClientFixture.Open();
            Assert.That(f.Vm.Combat.Boss.Visible.Value, Is.False);
            var walled = ClientFixture.Open(s => { s.Combat.Stage = 10; s.Combat.Boss = BossPhase.Walled; s.Progress.HighestCleared = 9; });
            var b = walled.Vm.Combat.Boss;
            Assert.That(b.Visible.Value, Is.True);
            Assert.That(walled.Vm.Combat.Walled.Value, Is.True);
            bool clearable = walled.Vm.Combat.BossClearable.Value;
            Assert.That(b.Label.Key, Is.EqualTo(clearable ? UiKeys.MainBossClearable : UiKeys.MainBossNeed));
            if (!clearable) Assert.That(walled.Vm.Combat.NeedDpsRatio.Value, Is.GreaterThan(1));
            walled.Vm.Combat.OnBoss();
            Assert.That(walled.Logged<AttemptBoss>(), Is.EqualTo(1));
        }

        [Test]
        public void GoldChip_PulsesOnBossAndOffline_NotPerKill()
        {
            var f = ClientFixture.Open();
            var hud = f.Vm.Hud;
            f.Vm.Cues.Clear();
            hud.OnCurrency(new CurrencyChanged(Currency.GOLD, 5, 5, CurrencyReason.KILL));
            Assert.That(f.Vm.Cues.Contains(CueKind.GoldPulse), Is.False);
            hud.OnCurrency(new CurrencyChanged(Currency.GOLD, 50, 55, CurrencyReason.BOSS));
            hud.OnCurrency(new CurrencyChanged(Currency.GOLD, 50, 105, CurrencyReason.OFFLINE));
            Assert.That(f.Vm.Cues.CountOf(CueKind.GoldPulse), Is.EqualTo(2));
            hud.OnCurrency(new CurrencyChanged(Currency.DIAMOND, 1, 1, CurrencyReason.QUEST));
            Assert.That(f.Vm.Cues.Contains(CueKind.DiamondPulse), Is.True);
        }

        [Test]
        public void HudChips_And_Tabs_RoutePerSection2_MvpTargetsToast()
        {
            var f = ClientFixture.Open();
            f.Vm.Hud.OnGoldChip();
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Party));
            f.Vm.Main.OnTab(MainTab.Summon);
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Summon));
            Assert.That(f.Vm.Router.Depth, Is.EqualTo(2));
            f.Vm.Main.OnTab(MainTab.Shop);
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Summon), "SCR_SHOP is MVP: a toast, no push");
            Assert.That(f.Vm.Toasts.Current(f.Now), Is.Not.Null);
            f.Vm.Main.OnTab(MainTab.Collection);
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Weapons));
            f.Frame();
            Assert.That(f.Vm.Main.ActiveTab.Value, Is.EqualTo((int)MainTab.Collection));
        }

        [Test]
        public void ARejectedCommand_ToastsItsLowercaseKeyUnderTheButton_InTheSameFrame()
        {
            var f = ClientFixture.Open();
            var r = f.Vm.Send(new TrainStat(Stat.ATK, 1), "hero.train.ATK");
            Assert.That(r.Ok, Is.False);
            Assert.That(f.Vm.Toasts.TryCurrent(f.Now, out var t), Is.True);
            Assert.That(t.Text, Is.EqualTo("reject.not_enough_gold"));
            Assert.That(t.AnchorId, Is.EqualTo("hero.train.ATK"));
            Assert.That(f.Vm.Cues.Contains(CueKind.Shake), Is.True);
        }

        [Test]
        public void AC12_SteadyFarming_RefreshAndFrameAllocateNothing_WithNoViewBound()
        {
            var f = ClientFixture.Open();
            f.Frames(120);   // warm up: fights spawned, caches filled
            long total = 0;
            for (int i = 0; i < 600; i++)
            {
                f.S.Frame(1.0 / 60);
                long before = GC.GetAllocatedBytesForCurrentThread();
                f.Vm.Frame();
                f.Vm.Refresh();
                total += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.That(total, Is.EqualTo(0), "GameViewModel.Frame + Refresh over 600 frames");
        }

        [Test]
        public void AC20_MainRefresh_WritesALabelOnlyWhenItsValueChanged()
        {
            var f = ClientFixture.Open();
            f.Frames(30);
            var counters = new List<Func<int>>();
            int bound = SinkBinder.BindAll(f.Vm.Main, counters) + SinkBinder.BindAll(f.Vm.Hud, counters)
                        + SinkBinder.BindAll(f.Vm.Combat, counters) + SinkBinder.BindAll(f.Vm.Board, counters);
            Assert.That(bound, Is.GreaterThan(30));
            f.Vm.Refresh();
            int after1 = SinkBinder.Total(counters);
            f.Vm.Refresh();
            f.Vm.Refresh();
            Assert.That(SinkBinder.Total(counters), Is.EqualTo(after1), "nothing changed, nothing written");
        }

        [Test]
        public void StageLabel_ReadsTheSnapshotsSelectedStage()   // 07 section 4.1, issue #254
        {
            var f = ClientFixture.Open(st =>
            {
                st.Combat.Stage = 12;
                st.Progress.HighestCleared = 11;
                st.Combat.Selected = 5;
            });
            var label = f.Vm.Combat.StageLabel;
            Assert.That(label.Key, Is.EqualTo(UiKeys.MainStageFarming));
            Assert.That((label.Arg(0), label.Arg(1)), Is.EqualTo((LabelArg.Int(5), LabelArg.Int(12))));
            f.S.Sim.State.Combat.Selected = null;
            f.Vm.Refresh();
            Assert.That(label.Key, Is.EqualTo(UiKeys.MainStageFarming), "the label follows the snapshot, not GameState");
            f.S.Sim.Advance(0);
            f.Vm.Refresh();
            Assert.That(label.Key, Is.EqualTo(UiKeys.MainStage));
            Assert.That(label.Arg(0), Is.EqualTo(LabelArg.Int(12)));
        }
    }
}
