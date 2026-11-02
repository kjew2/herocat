using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Client.Core;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Errors;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>SCR_HERO (07 section 4.4; issue #115).</summary>
    public class HeroScreenTests
    {
        static ClientFixture Rich(Action<GameState> more = null) => ClientFixture.Open(s =>
        {
            s.Wallet.Gold = 1e9;   // test setup only: the sim's Ledger is the one path in play
            more?.Invoke(s);
        });

        [Test]
        public void HpAndDef_HaveNoTrainingRow_PerDecision10()
        {
            var f = Rich();
            var stats = f.Vm.Hero.Rows.Select(r => r.Stat).ToList();
            Assert.That(stats, Does.Not.Contain(Stat.HP));
            Assert.That(stats, Does.Not.Contain(Stat.DEF));
            Assert.That(stats, Does.Contain(Stat.ATK));
        }

        [Test]
        public void Plus_SendsTrainStat_WithTheSelectedStepsCount()
        {
            var f = Rich();
            f.Vm.Router.Replace(ScreenIds.Hero);
            f.Vm.Hero.SelectStep(1);   // x10
            f.Vm.Refresh();
            Assert.That(f.Vm.Hero.Row(Stat.ATK).Count.Value, Is.EqualTo(10));
            f.Vm.Hero.OnTrain(Stat.ATK);
            var t = f.LoggedOf<TrainStat>().Single();
            Assert.That((t.Stat, t.Count), Is.EqualTo((Stat.ATK, 10)));
            Assert.That(f.Prefs.TrainStep, Is.EqualTo(1), "the step persists in ClientPrefs");
        }

        [Test]
        public void MaxStep_LabelsTheLevelsItBuys()
        {
            var f = Rich();
            f.Vm.Router.Replace(ScreenIds.Hero);
            f.Vm.Hero.SelectStep(f.Vm.Hero.StepCount - 1);
            f.Vm.Refresh();
            var row = f.Vm.Hero.Row(Stat.ATK);
            Assert.That(row.Buy.Label.Key, Is.EqualTo(UiKeys.HeroBuyLevels));
            Assert.That(row.Count.Value, Is.GreaterThan(1));
            f.Vm.Hero.OnTrain(Stat.ATK);
            Assert.That(f.LoggedOf<TrainStat>().Single().Count, Is.EqualTo(row.Count.Value));
        }

        [Test]
        public void AStatAtItsCap_ShowsMax_AndADisabledButton()
        {
            var f = Rich(s => s.Training.Level[(int)Stat.CRIT_RATE] = TestContent.Db.Stat(Stat.CRIT_RATE).Cap.Value);
            f.Vm.Router.Replace(ScreenIds.Hero);
            f.Vm.Refresh();
            var row = f.Vm.Hero.Row(Stat.CRIT_RATE);
            Assert.That(row.AtCap.Value, Is.True);
            Assert.That(row.Buy.Enabled.Value, Is.False);
            Assert.That(row.Buy.Label.Key, Is.EqualTo(UiKeys.HeroMax));
        }

        [Test]
        public void Unaffordable_NamesTheShortfall()
        {
            var f = ClientFixture.Open();
            f.Vm.Router.Replace(ScreenIds.Hero);
            f.Vm.Refresh();
            var row = f.Vm.Hero.Row(Stat.ATK);
            Assert.That(row.Buy.Enabled.Value, Is.False);
            Assert.That(row.Buy.Reason.Key, Is.EqualTo(UiKeys.NeedMore));
        }

        [Test]
        public void AC20_HeroRefresh_IsChangeOnly()
        {
            var f = Rich();
            f.Vm.Router.Replace(ScreenIds.Hero);
            f.Vm.Refresh();
            var counters = new List<Func<int>>();
            SinkBinder.BindAll(f.Vm.Hero, counters);
            foreach (var r in f.Vm.Hero.Rows) SinkBinder.BindAll(r, counters);
            f.Vm.Refresh();
            int n = SinkBinder.Total(counters);
            f.Vm.Refresh();
            Assert.That(SinkBinder.Total(counters), Is.EqualTo(n));
        }
    }

    /// <summary>SCR_WEAPONS, SCR_SKILLS, SCR_ITEM_DETAIL (07 sections 4.5, 4.6, 4.19; issue #116).</summary>
    public class CollectionScreenTests
    {
        [Test]
        public void Weapons_EquipIsDisabledWithAReason_WhenEquippedOrUnowned()
        {
            var f = ClientFixture.Open();
            var w = f.Vm.Weapons;
            f.Vm.Router.Replace(ScreenIds.Weapons);
            f.Vm.Refresh();
            string starter = TestContent.Db.TutorialWeaponId;
            Assert.That(w.Tiles.Count, Is.EqualTo(TestContent.Db.Weapons.Count));
            w.OnTile(starter);
            Assert.That(w.Equip.Enabled.Value, Is.False);
            Assert.That(w.Equip.Reason.Key, Is.EqualTo(UiKeys.CollEquipped));
            var unowned = w.Tiles.First(t => !t.Owned);
            w.OnTile(unowned.Id);
            Assert.That(w.Equip.Enabled.Value, Is.False);
            Assert.That(w.Equip.Reason.Key, Is.EqualTo(UiKeys.CollNotOwned));
        }

        [Test]
        public void Weapons_ItemAcquired_FlipsTheTile_NewClearsOnTap()
        {
            var f = ClientFixture.Open();
            var w = f.Vm.Weapons;
            f.Vm.Main.OnTab(MainTab.Summon);
            f.Vm.Summon.SelectBanner(Banner.WEAPON);
            f.Vm.Refresh();
            f.Vm.Summon.OnPull10();
            f.Vm.SummonResult.OnClose();
            var newIds = f.Vm.SummonResult.Cards.Where(c => c.IsNew).Select(c => c.ItemId).Distinct().ToList();
            Assume.That(newIds, Is.Not.Empty);
            f.Vm.Router.Replace(ScreenIds.Weapons);
            f.Frame();
            var tile = w.Tiles.First(t => t.Id == newIds[0]);
            Assert.That(tile.Owned, Is.True);
            Assert.That(tile.IsNew, Is.True);
            Assert.That(f.Vm.Main.TabBadges[(int)MainTab.Collection].Count, Is.GreaterThan(0));
            w.OnTile(tile.Id);
            Assert.That(f.Prefs.IsNew(Banner.WEAPON, tile.Id), Is.False, "a ClientPrefs update, not a sim command");
            w.OnEquip();
            Assert.That(f.LoggedOf<EquipWeapon>().Last().WeaponId, Is.EqualTo(tile.Id));
        }

        [Test]
        public void Skills_SlotTapWithASelectionEquips_WithoutOneUnequips()
        {
            string skill = TestContent.Db.Banners[Banner.SKILL].Pool[Rarity.COMMON][0];
            var f = ClientFixture.Open(s => s.Skills.Items[skill] = new OwnedItem { Copies = 1 });
            var sk = f.Vm.Skills;
            f.Vm.Router.Replace(ScreenIds.Skills);
            f.Vm.Refresh();
            sk.OnTile(skill);
            sk.OnSlot(0);
            var e = f.LoggedOf<EquipSkill>().Single();
            Assert.That((e.Slot, e.SkillId), Is.EqualTo((0, skill)));
            f.Frame();
            sk.ClearSelection();
            sk.OnSlot(0);
            Assert.That(f.LoggedOf<UnequipSkill>().Single().Slot, Is.EqualTo(0));
        }

        [Test]
        public void Chips_ReplaceTheTopFullScreen()
        {
            var f = ClientFixture.Open();
            f.Vm.Main.OnTab(MainTab.Collection);
            f.Vm.Weapons.OnChip(Banner.SKILL);
            Assert.That(f.Vm.Router.TopFull.Id, Is.EqualTo(ScreenIds.Skills));
            Assert.That(f.Vm.Router.Depth, Is.EqualTo(2));
        }

        [Test]
        public void EveryRarity_HasItsShapeGlyph()
        {
            var glyphs = Enum.GetValues(typeof(Rarity)).Cast<Rarity>().Select(Palette.ShapeGlyph).ToList();
            Assert.That(glyphs.Distinct().Count(), Is.EqualTo(5));
        }

        [Test]
        public void ItemDetail_NotOwned_HasNoEquip_Owned_EquipsAndShowsTheLevelBlock()
        {
            var f = ClientFixture.Open();
            var unowned = TestContent.Db.Banners[Banner.WEAPON].Pool[Rarity.RARE][0];
            f.Vm.ItemDetail.Open(Banner.WEAPON, unowned);
            Assert.That(f.Vm.Router.Modal?.Id, Is.EqualTo(ScreenIds.ItemDetail));
            Assert.That(f.Vm.ItemDetail.Equip.Visible.Value, Is.False);
            Assert.That(f.Vm.ItemDetail.LevelLine.Key, Is.EqualTo(UiKeys.CollNotOwned));
            f.Vm.ItemDetail.Close();
            f.Vm.ItemDetail.Open(Banner.WEAPON, TestContent.Db.TutorialWeaponId);
            Assert.That(f.Vm.ItemDetail.Owned.Value, Is.True);
            Assert.That(f.Vm.ItemDetail.LevelLine.Key, Is.EqualTo(UiKeys.DetailLevel));
            Assert.That(f.Vm.ItemDetail.Equip.Enabled.Value, Is.False, "already equipped");
        }

        [Test]
        public void ItemDetail_LimitBreak_ShowsTheCost_SendsTheCommand_AndDisablesWhenShortOrCapped()   // 04 sections 5.2, 11; #255
        {
            var db = TestContent.Db;
            string w = db.TutorialWeaponId;
            long perRank = db.Rarities[db.Weapons[w].Rarity].LimitBreakCost;
            int ranks = db.Constants.LimitBreakRanks;

            var poor = ClientFixture.Open(s => s.Wallet.RockCandy = perRank - 1);   // test setup only
            poor.Vm.ItemDetail.Open(Banner.WEAPON, w);
            var lb = poor.Vm.ItemDetail.LimitBreak;
            Assert.That(lb.Visible.Value, Is.True, "owned");
            Assert.That(lb.Enabled.Value, Is.False);
            Assert.That(lb.Reason.Key, Is.EqualTo(UiKeys.RejectNotEnoughRockCandy));
            Assert.That((lb.Label.Key, lb.Label.Arg(0)), Is.EqualTo((UiKeys.DetailLimitBreak, LabelArg.Int(perRank))));
            poor.Vm.ItemDetail.Close();
            poor.Vm.ItemDetail.Open(Banner.WEAPON, db.Banners[Banner.WEAPON].Pool[Rarity.MYTHIC][0]);
            Assert.That(poor.Vm.ItemDetail.LimitBreak.Visible.Value, Is.False, "unowned");

            var f = ClientFixture.Open(s => s.Wallet.RockCandy = perRank * 15);   // exactly ranks 1..5: 1+2+3+4+5
            var d = f.Vm.ItemDetail;
            d.Open(Banner.WEAPON, w);
            Assert.That(d.LimitBreakLine.Key, Is.EqualTo(UiKeys.DetailLimitBreaks));
            Assert.That((d.LimitBreakLine.Arg(0), d.LimitBreakLine.Arg(1)), Is.EqualTo((LabelArg.Int(0), LabelArg.Int(ranks))));
            for (int rank = 1; rank <= ranks; rank++)
            {
                Assert.That(d.LimitBreak.Enabled.Value, Is.True, "rank " + rank);
                Assert.That(d.LimitBreak.Label.Arg(0), Is.EqualTo(LabelArg.Int(perRank * rank)));
                d.OnLimitBreak();
                f.Frame();
                Assert.That(f.S.Sim.State.Weapons.Items[w].LimitBreaks, Is.EqualTo(rank));
                Assert.That(d.LimitBreakLine.Arg(0), Is.EqualTo(LabelArg.Int(rank)), "the modal refreshes on ItemLimitBroken");
            }
            Assert.That(f.Logged<LimitBreak>(), Is.EqualTo(ranks));
            Assert.That(f.S.Sim.State.Wallet.RockCandy, Is.EqualTo(0));
            Assert.That(d.LimitBreak.Enabled.Value, Is.False);
            Assert.That(d.LimitBreak.Reason.Key, Is.EqualTo(UiKeys.RejectLimitBreakCapped));
            Assert.That(d.LimitBreak.Label.Key, Is.EqualTo(UiKeys.RejectLimitBreakCapped));
        }

        [Test]
        public void AC20_CollectionRefresh_IsChangeOnly()
        {
            var f = ClientFixture.Open();
            f.Vm.Router.Replace(ScreenIds.Weapons);
            f.Vm.Refresh();
            f.Vm.Weapons.OnTile(TestContent.Db.TutorialWeaponId);
            var counters = new List<Func<int>>();
            SinkBinder.BindAll(f.Vm.Weapons, counters);
            f.Vm.Refresh();
            int n = SinkBinder.Total(counters);
            f.Vm.Refresh();
            Assert.That(SinkBinder.Total(counters), Is.EqualTo(n));
        }
    }

    /// <summary>SCR_SUMMON and SCR_SUMMON_RESULT with the RevealSequencer (07 sections 4.10, 4.11, 6; AC 5, 10, 15; #117).</summary>
    public class SummonScreenTests
    {
        static Rarity[] Ten(params int[] lplusAt)
        {
            var r = Enumerable.Repeat(Rarity.COMMON, 10).ToArray();
            foreach (int i in lplusAt) r[i] = Rarity.LEGENDARY;
            return r;
        }

        [Test]
        public void AC15_CardStartTimes_IncludeTheLplusHold()
        {
            var s = new RevealSequencer();
            s.Start(Ten(2, 5), 100.0, reducedMotion: false);
            for (int i = 0; i < 10; i++)
            {
                int before = (i > 2 ? 1 : 0) + (i > 5 ? 1 : 0);
                Assert.That(s.StartOf(i), Is.EqualTo(0.3 + 0.12 * i + 0.4 * before).Within(1e-9), "card " + i);
            }
            Assert.That(s.FlipOf(2) - s.StartOf(2), Is.EqualTo(0.4).Within(1e-9), "an L card glows 0.4 s before flipping");
            s.Update(100.0 + s.StartOf(4) + 0.01);
            Assert.That(s.State(0), Is.EqualTo(CardState.FaceUp));
            Assert.That(s.State(9), Is.EqualTo(CardState.FaceDown));
            s.Skip();
            for (int i = 0; i < 10; i++) Assert.That(s.State(i), Is.EqualTo(CardState.FaceUp), "Skip faces every card up in the same frame");
            Assert.That(s.Complete, Is.True);
        }

        [Test]
        public void AC10_ReducedMotion_TenPullCompletesWithinPoint7Seconds()
        {
            var s = new RevealSequencer();
            s.Start(Ten(1, 4, 7), 0, reducedMotion: true);
            Assert.That(s.Duration, Is.LessThanOrEqualTo(0.7));
            s.Update(0.7);
            Assert.That(s.Complete, Is.True);
        }

        [Test]
        public void SinglePull_TakesPoint6Seconds_ATapCompletesIt()
        {
            var s = new RevealSequencer();
            s.Start(new[] { Rarity.RARE }, 0, false);
            Assert.That(s.Duration, Is.EqualTo(0.6).Within(1e-9));
            s.Update(0.3);
            Assert.That(s.State(0), Is.EqualTo(CardState.Flipping));
            s.Tap(0.31);
            Assert.That(s.Complete, Is.True);
        }

        [Test]
        public void TapToSpeed_HalvesTheRemainingStagger_ASecondTapSkips()
        {
            var s = new RevealSequencer();
            s.Start(Ten(), 0, false);
            s.Update(0.3 + 0.12 * 2 + 0.1);   // card 2 flipping
            s.Tap(0.64);
            Assert.That(s.State(2), Is.EqualTo(CardState.FaceUp));
            Assert.That(s.StartOf(4) - s.StartOf(3), Is.EqualTo(0.06).Within(1e-9));
            s.Tap(0.65);
            Assert.That(s.Complete, Is.True);
        }

        [Test]
        public void TenPull_OpensTheOverlay_AgainIsDisabledUntilTheRevealCompletes()
        {
            var f = ClientFixture.Open();
            f.Vm.Main.OnTab(MainTab.Summon);
            f.Vm.Summon.SelectBanner(Banner.WEAPON);
            f.Vm.Refresh();
            Assert.That(f.Vm.Summon.Pull10.Enabled.Value, Is.True, "1,500 tutorial diamonds afford a 1,000 x10");
            long before = f.S.Sim.State.Wallet.Diamond;
            f.Vm.Summon.OnPull10();
            Assert.That(f.Vm.Router.IsOpen(ScreenIds.SummonResult), Is.True);
            Assert.That(f.S.Sim.State.Wallet.Diamond, Is.LessThan(before), "the reveal never gates state");
            Assert.That(f.Vm.SummonResult.Cards.Count, Is.EqualTo(10));
            f.Frame();
            Assert.That(f.Vm.SummonResult.Running, Is.True);
            Assert.That(f.Vm.SummonResult.Again.Enabled.Value, Is.False, "disabled during the reveal");
            Assert.That(f.Vm.Back(), Is.EqualTo(BackResult.SkippedReveal), "back during a reveal skips it (AC 5)");
            f.Frame();
            Assert.That(f.Vm.SummonResult.Running, Is.False);
            Assert.That(f.Vm.SummonResult.Again.Enabled.Value, Is.False, "500 left cannot afford another x10");
            Assert.That(f.Vm.SummonResult.Again.Reason.Key, Is.EqualTo(UiKeys.NeedMore));
            Assert.That(f.Vm.SummonResult.SummaryVisible.Value, Is.True);
            f.Vm.SummonResult.OnClose();
            Assert.That(f.Vm.Router.IsOpen(ScreenIds.SummonResult), Is.False);
        }

        [Test]
        public void SummonAgain_KeepsTheOverlay_AndRunsOneNewRequest()
        {
            var f = ClientFixture.Open();
            f.Vm.Main.OnTab(MainTab.Summon);
            f.Vm.Summon.SelectBanner(Banner.WEAPON);
            f.Vm.Refresh();
            f.Vm.Summon.OnPull1();
            f.Vm.SummonResult.OnSkip();
            f.Frame();
            Assert.That(f.Vm.SummonResult.Again.Enabled.Value, Is.True);
            long pulls = f.S.Sim.State.Banners[Banner.WEAPON].TotalPulls;
            f.Vm.SummonResult.OnAgain();
            Assert.That(f.S.Sim.State.Banners[Banner.WEAPON].TotalPulls, Is.EqualTo(pulls + 1));
            Assert.That(f.Vm.Router.IsOpen(ScreenIds.SummonResult), Is.True);
            Assert.That(f.Vm.SummonResult.Running, Is.True, "the cards are face down again");
        }

        [Test]
        public void UnaffordablePull_NamesTheShortfall()
        {
            var f = ClientFixture.Open(s => { });
            f.Vm.Main.OnTab(MainTab.Summon);
            f.Vm.Summon.SelectBanner(Banner.SKILL);
            f.Vm.Refresh();
            var cost10 = (long)TestContent.Db.Banners[Banner.SKILL].PullCostDiamonds * 10;
            if (f.S.Sim.State.Wallet.Diamond >= cost10) Assert.Ignore("content lets the starter diamonds afford a skill x10");
            Assert.That(f.Vm.Summon.Pull10.Enabled.Value, Is.False);
            Assert.That(f.Vm.Summon.Pull10.Reason.Key, Is.EqualTo(UiKeys.NeedMore));
            Assert.That(f.Vm.Summon.Ticket.Visible.Value, Is.False, "the ticket button hides at 0");
        }

        [Test]
        public void AC20_SummonRefresh_IsChangeOnly()
        {
            var f = ClientFixture.Open();
            f.Vm.Main.OnTab(MainTab.Summon);
            f.Vm.Refresh();
            var counters = new List<Func<int>>();
            SinkBinder.BindAll(f.Vm.Summon, counters);
            f.Vm.Refresh();
            int n = SinkBinder.Total(counters);
            f.Vm.Refresh();
            Assert.That(SinkBinder.Total(counters), Is.EqualTo(n));
        }
    }

    /// <summary>SCR_BOOT, SCR_OFFLINE_REWARD, SCR_CONFIRM, SCR_ERROR (07 sections 4.1, 4.18, 4.26, 4.27; AC 5, 21; #118).</summary>
    public class SystemScreenTests
    {
        [Test]
        public void Boot_ProgressIsWeightedPerStep()
        {
            var b = new BootVm();
            b.OnStep(BootStep.Content);
            Assert.That(b.Progress01, Is.EqualTo(0));
            b.OnStep(BootStep.Save);
            Assert.That(b.Progress01, Is.EqualTo(0.6).Within(1e-9), "content 40 + config 10 + auth 10");
            Assert.That(b.StepKey.Key, Is.EqualTo("boot.step.save"));
            b.OnStep(BootStep.Handoff);
            Assert.That(b.Progress01, Is.EqualTo(0.9).Within(1e-9));
            b.OnDone();
            Assert.That(b.Progress01, Is.EqualTo(1));
        }

        [Test]
        public void Error_ANewOneReplacesTheVisibleOne_AndTheOldCodeIsLogged()
        {
            var r = new ScreenRouter();
            var e = new ErrorVm(null, r);
            var logged = new List<string>();
            e.Log = logged.Add;
            var sid = new SupportId(Guid.Empty, 0);
            e.Show(ErrorClassifier.ForCode(ErrorClassifier.SRV_5XX, sid));
            e.Show(ErrorClassifier.ForCode("MAINTENANCE", sid));
            Assert.That(e.Code, Is.EqualTo("MAINTENANCE"));
            Assert.That(logged, Is.EqualTo(new[] { ErrorClassifier.SRV_5XX }));
            Assert.That(e.Title.Key, Is.EqualTo("err.maintenance.title"));
            Assert.That(e.Body.Key, Is.EqualTo("err.maintenance.body"));
            Assert.That(r.BlockingError, Is.Not.Null);
        }

        [Test]
        public void Error_ClientTooOld_OffersOnlyUpdate_BootFailureOffersRetry_OfflineIsACard()
        {
            var sid = new SupportId(Guid.Empty, 0);
            var e = new ErrorVm(null, null);
            e.Show(ErrorClassifier.ForCode("CLIENT_TOO_OLD", sid), bootFailure: true);
            Assert.That(e.Actions, Is.EqualTo(new[] { ErrorAction.UpdateApp }));
            e.Show(ErrorClassifier.ForCode(ErrorClassifier.CONTENT_CORRUPT, sid), bootFailure: true);
            Assert.That(e.Actions, Does.Contain(ErrorAction.Retry));
            Assert.That(e.Blocking, Is.True);
            Assert.That(e.CodeLine.Arg(0).ToString(), Is.EqualTo(ErrorClassifier.CONTENT_CORRUPT), "the code in small text for support");
            e.Show(ErrorClassifier.ForCode(ErrorClassifier.NET_OFFLINE, sid));
            Assert.That(e.CardVisible.Value, Is.True);
            Assert.That(e.Blocking, Is.False);
        }

        [Test]
        public void BootFailure_ThroughClientSession_ShowsTheBlockingErrorWithRetry()
        {
            var cs = ClientSession.Start(new Func<ContentDb>[] { () => throw new InvalidOperationException("broken") },
                                         (db, state, wrap) => HeroCat.Runtime.Services.GameServices.Build(HeroCat.Runtime.Services.BackendMode.Local,
                                             new HeroCat.Runtime.Services.ServicesOptions { Clock = new HeroCat.Runtime.Services.FakeClock(TestContent.T0), Slots = new HeroCat.Runtime.Save.MemorySaveStore(), State = state, Content = db, WrapClock = wrap, InstallId = TestContent.Device }),
                                         runner: new HeroCat.Runtime.Session.SessionRunnerOptions { Mono = new HeroCat.Runtime.Session.FakeMonoClock(), TickBudgetMs = 0 });
            Assert.That(cs.IsReady, Is.False);
            Assert.That(cs.BootError.Blocking, Is.True);
            Assert.That(cs.BootError.Actions, Does.Contain(ErrorAction.Retry));
        }

        [Test]
        public void Confirm_CancelSendsNothing_ConfirmRuns_TypedConfirmationGates()
        {
            var f = ClientFixture.Open();
            int confirmed = 0, cancelled = 0;
            var req = new ConfirmRequest
            {
                TitleKey = "scr.confirm.test", BodyKey = "scr.confirm.test_body", CostCurrency = Currency.ROCK_CANDY, CostAmount = 100, BalanceAfter = 20,
                OnConfirm = () => confirmed++, OnCancel = () => cancelled++,
            };
            int logged = f.S.Log.Count;
            f.Vm.Confirm.Ask(req);
            Assert.That(f.Vm.Router.Modal?.Id, Is.EqualTo(ScreenIds.Confirm));
            Assert.That(f.Vm.Confirm.CostVisible.Value, Is.True);
            Assert.That(f.Vm.Back(), Is.EqualTo(BackResult.PoppedModal), "back equals Cancel");
            Assert.That(cancelled, Is.EqualTo(1));
            Assert.That(confirmed, Is.EqualTo(0));
            Assert.That(f.S.Log.Count, Is.EqualTo(logged), "Cancel sends nothing");

            f.Vm.Confirm.Ask(new ConfirmRequest { TitleKey = "t", BodyKey = "b", TypedConfirmation = "DELETE", OnConfirm = () => confirmed++ });
            Assert.That(f.Vm.Confirm.Confirm.Enabled.Value, Is.False);
            f.Vm.Confirm.OnTyped("DELETE");
            f.Vm.Confirm.OnConfirm();
            Assert.That(confirmed, Is.EqualTo(1));
            Assert.That(f.Vm.Router.Modal, Is.Null);
        }

        [Test]
        public void AC21_OfflineReward_PausesTicks_IgnoresBack_ClaimResumes()
        {
            var pending = new CatchupResult(ElapsedSec: 43440, CreditedSec: 43440, Capped: false, Gold: 997000, Exp: 1.2e6, TankFill: 10, Stage: 1,
                                            KillTimeSec: 1.5, ComputedServerTime: TestContent.T0);
            var f = ClientFixture.Open(s => s.Offline.Pending = pending);
            Assert.That(f.Vm.Router.Modal?.Id, Is.EqualTo(ScreenIds.OfflineReward));
            Assert.That(f.Vm.TicksRunning, Is.False);
            Assert.That(f.S.Runner.TicksHeldForOfflineReward, Is.True, "the runner holds ticks while Offline.Pending is set");
            Assert.That(f.Vm.OfflineReward.Gold.Value, Is.EqualTo(997000), "the modal shows the pending record");
            long tick = f.S.Sim.Tick;
            f.Frames(30, 0.1);
            Assert.That(f.S.Sim.Tick, Is.EqualTo(tick), "no tick runs while SCR_OFFLINE_REWARD is open");
            Assert.That(f.Vm.Back(), Is.EqualTo(BackResult.Ignored));
            Assert.That(f.Vm.Router.ScrimTap(), Is.False);
            double gold = f.S.Sim.State.Wallet.Get(Currency.GOLD);
            f.Vm.OfflineReward.OnClaim();
            Assert.That(f.Vm.Router.Modal, Is.Null);
            Assert.That(f.S.Sim.State.Offline.Pending, Is.Null, "Claim sends ClaimOfflineReward (#108)");
            Assert.That(f.S.Sim.State.Wallet.Get(Currency.GOLD), Is.EqualTo(gold + 997000), "the claim credits the pending gold");
            Assert.That(f.S.Runner.TicksHeldForOfflineReward, Is.False, "the claim releases the runner's tick hold");
            Assert.That(f.Vm.TicksRunning, Is.True);
            f.Frames(10, 0.1);
            Assert.That(f.S.Sim.Tick, Is.GreaterThan(tick));
        }

        [Test]
        public void AC20_SystemVms_AreChangeOnly()
        {
            var pending = new CatchupResult(600, 600, false, 1000, 2000, 0, 1, 1.0, TestContent.T0);
            var f = ClientFixture.Open(s => s.Offline.Pending = pending);
            var counters = new List<Func<int>>();
            SinkBinder.BindAll(f.Vm.OfflineReward, counters);
            f.Vm.OfflineReward.OnShown(f.S.Sim);
            int n = SinkBinder.Total(counters);
            f.Vm.OfflineReward.OnShown(f.S.Sim);
            f.Vm.Refresh();
            Assert.That(SinkBinder.Total(counters), Is.EqualTo(n));
        }
    }
}
