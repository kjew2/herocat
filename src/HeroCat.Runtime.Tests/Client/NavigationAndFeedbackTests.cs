using System.Collections.Generic;
using HeroCat.Client.Core;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>ScreenRouter (07 sections 1, 10 rule 4, 16; AC 3, 4, 5; issue #110), toasts and change-only setters (#109).</summary>
    public class ScreenRouterTests
    {
        sealed class Host : IScreenHost
        {
            public readonly List<string> Shown = new List<string>(), Hidden = new List<string>(), Evicted = new List<string>();
            public void Show(ScreenEntry e) => Shown.Add(e.Id);
            public void Hide(ScreenEntry e) => Hidden.Add(e.Id);
            public void Evict(string id) => Evicted.Add(id);
        }

        [Test]
        public void Main_IsTheRoot_AndIsNeverPopped()
        {
            var r = new ScreenRouter();
            Assert.That(r.TopFull.Id, Is.EqualTo(ScreenIds.Main));
            r.Pop();
            r.PopFull();
            Assert.That(r.Depth, Is.EqualTo(1));
            Assert.That(r.TopFull.Id, Is.EqualTo(ScreenIds.Main));
        }

        [Test]
        public void TabReplace_KeepsDepthTwo_AndBackReturnsToMain()
        {
            var r = new ScreenRouter();
            r.Replace(ScreenIds.Hero);
            r.Replace(ScreenIds.Summon);
            r.Replace(ScreenIds.Weapons);
            Assert.That(r.Depth, Is.EqualTo(2));
            Assert.That(r.TopFull.Id, Is.EqualTo(ScreenIds.Weapons));
            Assert.That(r.CombatVisible, Is.False, "Full screens hide the combat viewport (rule 1)");
            Assert.That(r.Back(0), Is.EqualTo(BackResult.PoppedFull));
            Assert.That(r.TopFull.Id, Is.EqualTo(ScreenIds.Main));
            Assert.That(r.CombatVisible, Is.True);
        }

        [Test]
        public void AC3_ThreeModalsInOneFrame_ShowOneAtATime_InOrder()
        {
            var host = new Host();
            var r = new ScreenRouter(host);
            r.ShowModal(ScreenIds.ItemDetail, "a");
            r.ShowModal(ScreenIds.Confirm, "b");
            r.ShowModal(ScreenIds.ItemDetail, "c");
            Assert.That(r.Modal.Args, Is.EqualTo("a"));
            Assert.That(r.QueuedModals, Is.EqualTo(2));
            int visible = 0;
            var seen = new List<object>();
            while (r.Modal != null)
            {
                visible = r.Modal != null ? 1 : 0;
                Assert.That(visible, Is.EqualTo(1));
                seen.Add(r.Modal.Args);
                r.CloseModal();
            }
            Assert.That(seen, Is.EqualTo(new object[] { "a", "b", "c" }));
            Assert.That(host.Shown.FindAll(s => s != ScreenIds.Main).Count, Is.EqualTo(3));
        }

        [Test]
        public void AC4_DuringABoss_ANonMandatoryModalShiftsToTheBoard_AndTheOfflineRewardWaits()
        {
            bool boss = true;
            var r = new ScreenRouter { EnemyIsBoss = () => boss };
            var detail = r.ShowModal(ScreenIds.ItemDetail);
            Assert.That(detail.Placement, Is.EqualTo(ModalPlacement.BoardRegion), "never covers the combat viewport");
            r.CloseModal();

            r.ShowModal(ScreenIds.OfflineReward);
            Assert.That(r.Modal, Is.Null, "SCR_OFFLINE_REWARD is never shown mid-boss");
            Assert.That(r.IsQueued(ScreenIds.OfflineReward), Is.True);
            r.Update();
            Assert.That(r.Modal, Is.Null);
            boss = false;
            r.Update();
            Assert.That(r.Modal?.Id, Is.EqualTo(ScreenIds.OfflineReward));
            Assert.That(r.Modal.Placement, Is.EqualTo(ModalPlacement.Center));
        }

        [Test]
        public void AC5_BackOnBareMain_ShowsExitConfirmOnce_QuitsOnlyOnASecondPressWithinTwoSeconds()
        {
            var r = new ScreenRouter();
            Assert.That(r.Back(10.0), Is.EqualTo(BackResult.ShowedExitConfirm));
            Assert.That(r.Modal?.Id, Is.EqualTo(ScreenIds.Confirm));
            Assert.That(r.Back(11.5), Is.EqualTo(BackResult.Quit));

            var late = new ScreenRouter();
            late.Back(10.0);
            Assert.That(late.Back(12.5), Is.EqualTo(BackResult.PoppedModal), "after the window the second press only closes the confirm");
            Assert.That(late.Modal, Is.Null);
            Assert.That(late.Back(13.0), Is.EqualTo(BackResult.ShowedExitConfirm), "and the next press asks again");
        }

        [Test]
        public void AC5_BackIsIgnoredOnTheOfflineReward_AndSkipsARunningReveal()
        {
            var r = new ScreenRouter();
            r.ShowModal(ScreenIds.OfflineReward);
            Assert.That(r.Back(0), Is.EqualTo(BackResult.Ignored));
            Assert.That(r.ScrimTap(), Is.False);
            Assert.That(r.Modal?.Id, Is.EqualTo(ScreenIds.OfflineReward));

            bool running = true, skipped = false;
            var s = new ScreenRouter { RevealRunning = () => running, SkipReveal = () => { skipped = true; running = false; } };
            s.ShowOverlay(ScreenIds.SummonResult);
            Assert.That(s.Back(0), Is.EqualTo(BackResult.SkippedReveal));
            Assert.That(skipped, Is.True);
            Assert.That(s.IsOpen(ScreenIds.SummonResult), Is.True, "skip keeps the overlay");
            Assert.That(s.Back(0), Is.EqualTo(BackResult.PoppedOverlay));
        }

        [Test]
        public void BlockingError_SwallowsBack_AndSitsAboveEverything()
        {
            var r = new ScreenRouter();
            r.Replace(ScreenIds.Hero);
            r.ShowModal(ScreenIds.Error, "CONTENT_CORRUPT");
            Assert.That(r.BlockingError, Is.Not.Null);
            Assert.That(r.Back(0), Is.EqualTo(BackResult.Ignored));
            r.CloseBlockingError();
            Assert.That(r.Back(0), Is.EqualTo(BackResult.PoppedFull));
        }

        [Test]
        public void FullScreenLru_KeepsThree_EvictsTheLeastRecentOffStack()
        {
            var host = new Host();
            var r = new ScreenRouter(host);
            r.Replace(ScreenIds.Hero);
            r.Replace(ScreenIds.Weapons);
            r.Replace(ScreenIds.Skills);
            r.Replace(ScreenIds.Summon);
            Assert.That(host.Evicted, Is.EqualTo(new[] { ScreenIds.Hero }));
            Assert.That(r.CachedFull.Count, Is.EqualTo(UiConstants.ScreenCache));
        }
    }

    public class FeedbackTests
    {
        [Test]
        public void Toasts_OneAtATime_ForTheirDuration_IdenticalOnesCollapse()
        {
            var q = new ToastQueue();
            q.Push("a");
            q.Push("a");
            q.Push("a");
            q.Push("b", ToastStyle.Rare, UiConstants.ToastRareSec);
            Assert.That(q.Current(0), Is.EqualTo("a x3"));
            Assert.That(q.Current(1.9), Is.EqualTo("a x3"));
            Assert.That(q.Current(2.0), Is.EqualTo("b"));
            Assert.That(q.Current(5.9), Is.EqualTo("b"), "L/M acquisitions show 4 s");
            Assert.That(q.Current(6.0), Is.Null);
        }

        [Test]
        public void RejectToast_CarriesItsAnchor()
        {
            var q = new ToastQueue();
            q.Push("reject.not_enough_gold", ToastStyle.Reject, UiConstants.ToastSec, "hero.train.ATK");
            Assert.That(q.TryCurrent(0, out var t), Is.True);
            Assert.That(t.AnchorId, Is.EqualTo("hero.train.ATK"));
            Assert.That(t.Style, Is.EqualTo(ToastStyle.Reject));
        }

        [Test]
        public void ChangeOnlySetters_WriteOnlyWhenTheValueChanged()
        {
            var n = new NumberLabel();
            var s = new CountingSink();
            n.Bind(s);
            n.Set(12345);
            n.Set(12345);
            Assert.That(s.Writes, Is.EqualTo(1));
            Assert.That(s.LastText, Is.EqualTo("12.3K"));
            var key = new TextKeyLabel(new Localizer(new Dictionary<string, string> { ["k"] = "Lv {0}" }));
            var ks = new CountingSink();
            key.Bind(ks);
            key.Set("k", 3);
            key.Set("k", 3);
            key.Set("k", 4);
            Assert.That(ks.Writes, Is.EqualTo(2));
            Assert.That(ks.LastText, Is.EqualTo("Lv 4"));
            var bar = new BarFill();
            var bs = new CountingSink();
            bar.Bind(bs);
            bar.Set(0.5);
            bar.Set(0.50001);
            Assert.That(bs.Writes, Is.EqualTo(1), "quantized to 1/1000");
        }

        [Test]
        public void ErrorAndRejectKeys_AreLowercase_PerIssue248()
        {
            Assert.That(HeroCat.Runtime.Errors.ErrorClassifier.ErrTitleKey("NET_OFFLINE"), Is.EqualTo("err.net_offline.title"));
            Assert.That(HeroCat.Runtime.Errors.ErrorClassifier.ErrBodyKey("CLIENT_TOO_OLD"), Is.EqualTo("err.client_too_old.body"));
            Assert.That(HeroCat.Runtime.Errors.ErrorClassifier.RejectReasonKey("BANNER_CLOSED"), Is.EqualTo("reject.banner_closed"));
            Assert.That(HeroCat.Runtime.Errors.ErrorClassifier.RejectKey(HeroCat.Sim.Commands.ApplyResult.Reject(HeroCat.Sim.Commands.RejectCode.CannotAfford, "NOT_ENOUGH_GOLD")),
                        Is.EqualTo("reject.not_enough_gold"));
            Assert.That(HeroCat.Runtime.Errors.ErrorClassifier.RejectKey(HeroCat.Sim.Commands.ApplyResult.Reject(HeroCat.Sim.Commands.RejectCode.CannotAfford)),
                        Is.EqualTo("reject.cannot_afford"));
        }

        [Test]
        public void ClientPrefs_RoundTripThroughText()
        {
            var p = new MemoryClientPrefs { TrainStep = 2, Sort = CollectionSort.Newest, Filter = CollectionFilter.Epic, ReducedMotion = true };
            p.MarkNew(HeroCat.Sim.Content.Banner.WEAPON, "weapon_c_01");
            p.MarkCoachMark("hero_atk");
            var q = MemoryClientPrefs.Deserialize(p.Serialize());
            Assert.That(q.TrainStep, Is.EqualTo(2));
            Assert.That(q.Sort, Is.EqualTo(CollectionSort.Newest));
            Assert.That(q.Filter, Is.EqualTo(CollectionFilter.Epic));
            Assert.That(q.ReducedMotion, Is.True);
            Assert.That(q.IsNew(HeroCat.Sim.Content.Banner.WEAPON, "weapon_c_01"), Is.True);
            Assert.That(q.SeenCoachMark("hero_atk"), Is.True);
        }
    }
}
