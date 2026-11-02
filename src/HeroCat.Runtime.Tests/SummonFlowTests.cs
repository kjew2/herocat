using System;
using System.Linq;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Summons;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The kill-safe summon flow (01 section 8.5.1, AC 8 local variant; issue #68).</summary>
    public class SummonFlowTests
    {
        sealed class Killed : Exception { }

        static long Cost(int count) => (long)TestContent.Db.Banners[Banner.WEAPON].PullCostDiamonds * count;

        static long Pulls(GameState s) => s.Banners.TryGetValue(Banner.WEAPON, out var b) && b != null ? b.TotalPulls : 0;

        static long Items(GameState s) => s.Weapons.Items.Values.Sum(i => i?.Copies ?? 0);

        [TestCase(SummonFlowPoint.BeforeIntentSave, false)]
        [TestCase(SummonFlowPoint.AfterIntentSave, true)]
        [TestCase(SummonFlowPoint.AfterServiceReturned, true)]
        [TestCase(SummonFlowPoint.AfterApply, true)]
        [TestCase(SummonFlowPoint.AfterFinalSave, true)]
        public void AKill_AtAnyPoint_EndsWithNoChargeAndNoItems_OrOneChargeAndTheItems(SummonFlowPoint killAt, bool charged)   // 01 AC 8
        {
            var store = new MemorySaveStore();
            var first = TestSession.Open(store: store);
            long diamonds = first.Sim.State.Wallet.Diamond;
            long itemsBefore = Items(first.Sim.State);
            byte[] primary = null, backup = null;
            first.Runner.Summons.Checkpoint = p =>
            {
                if (p != killAt) return;
                primary = store.Primary?.ToArray();   // what is on disk at the instant of the kill
                backup = store.Backup?.ToArray();
                throw new Killed();
            };
            Assert.Throws<Killed>(() => first.Runner.Summons.SummonAsync(Banner.WEAPON, 10, SummonPayment.Diamonds).GetAwaiter().GetResult());

            // Relaunch from the bytes on disk, then resolve the intent as boot does once the offline modal is closed.
            var relaunched = new MemorySaveStore { Primary = primary, Backup = backup };
            var second = TestSession.Open(store: relaunched);
            var flow = second.Runner.Summons;
            string savedId = flow.Pending?.RequestId;
            Assert.That(flow.Pending != null, Is.EqualTo(killAt != SummonFlowPoint.BeforeIntentSave && killAt != SummonFlowPoint.AfterFinalSave),
                        "the intent is on disk exactly between its save and the post-apply save");
            if (flow.ShouldResolve)
            {
                var r = flow.ResolveAsync().GetAwaiter().GetResult();
                Assert.That(r.IsOk, Is.True, r.ToString());
                Assert.That(r.Value.RequestId, Is.EqualTo(savedId), "re-run with the saved requestId");
            }
            Assert.That(flow.ShouldResolve, Is.False);

            var s = second.Sim.State;
            Assert.That(s.Pending.Summon, Is.Null);
            Assert.That(s.Wallet.Diamond, Is.EqualTo(charged ? diamonds - Cost(10) : diamonds));
            Assert.That(Pulls(s), Is.EqualTo(charged ? 10 : 0));
            Assert.That(Items(s) - itemsBefore, Is.EqualTo(charged ? 10 : 0), "never partial");

            // And the resolved state is what reaches disk.
            var third = TestSession.Open(store: relaunched);
            Assert.That(third.Sim.State.Wallet.Diamond, Is.EqualTo(s.Wallet.Diamond));
            Assert.That(third.Sim.State.Pending.Summon, Is.Null);
        }

        [Test]
        public void TheIntent_IsOnDisk_BeforeTheRoll_WithItsRequestId()   // 01 section 8.5.1 step 2
        {
            var store = new MemorySaveStore();
            var t = TestSession.Open(store: store);
            PendingSummon onDisk = null;
            t.Runner.Summons.Checkpoint = p =>
            {
                if (p != SummonFlowPoint.AfterIntentSave) return;
                onDisk = TestSession.Open(store: new MemorySaveStore { Primary = store.Primary?.ToArray() }).Sim.State.Pending.Summon;
            };
            var r = t.Runner.SummonAsync(Banner.WEAPON, 1, SummonPayment.Diamonds).GetAwaiter().GetResult();
            Assert.That(r.IsOk, Is.True, r.ToString());
            Assert.That(onDisk, Is.Not.Null);
            Assert.That(onDisk.RequestId, Is.EqualTo(r.Value.RequestId));
            Assert.That(onDisk.RequestId, Does.Match("^[0-9a-f]{32}$"), "GUID \"N\"");
            Assert.That(onDisk.Banner, Is.EqualTo(Banner.WEAPON));
            Assert.That(onDisk.Count, Is.EqualTo(1));
            Assert.That(t.Sim.State.Pending.Summon, Is.Null, "cleared after apply");
        }

        [Test]
        public void ASecondSummon_WhileOneIsPending_IsRefused()   // issue #68 acceptance 2
        {
            var t = TestSession.Open();
            var flow = t.Runner.Summons;
            Result<SummonResultPayload, SummonError>? nested = null;
            flow.Checkpoint = p =>
            {
                if (p == SummonFlowPoint.AfterIntentSave && nested == null)
                    nested = flow.SummonAsync(Banner.WEAPON, 1, SummonPayment.Diamonds).GetAwaiter().GetResult();
            };
            long diamonds = t.Sim.State.Wallet.Diamond;
            Assert.That(flow.SummonAsync(Banner.WEAPON, 1, SummonPayment.Diamonds).GetAwaiter().GetResult().IsOk, Is.True);
            Assert.That(nested.HasValue && !nested.Value.IsOk && nested.Value.Error == SummonError.InFlight, Is.True, nested.ToString());
            Assert.That(t.Sim.State.Wallet.Diamond, Is.EqualTo(diamonds - Cost(1)), "one charge");
        }

        [Test]
        public void TheSameRequestId_ResolvedTwice_IsOneDebit()   // issue #68 acceptance 3
        {
            var t = TestSession.Open();
            var s = t.Sim.State;
            long diamonds = s.Wallet.Diamond;
            var intent = new PendingSummon("0123456789abcdef0123456789abcdef", Banner.WEAPON, 1, SummonPayment.Diamonds.ToString(), TestContent.T0);
            s.Pending.Summon = intent;
            Assert.That(t.Runner.Summons.ResolveAsync().GetAwaiter().GetResult().IsOk, Is.True);
            s.Pending.Summon = intent;   // a stale copy of the intent comes back (an older save)
            var again = t.Runner.Summons.ResolveAsync().GetAwaiter().GetResult();
            Assert.That(again.IsOk, Is.False);
            Assert.That(again.Error, Is.EqualTo(SummonError.Replayed));
            Assert.That(s.Wallet.Diamond, Is.EqualTo(diamonds - Cost(1)));
            Assert.That(Pulls(s), Is.EqualTo(1));
            Assert.That(s.Pending.Summon, Is.Null);
        }

        [Test]
        public void AnIntentThatCannotBeSaved_IsNeverRolled()   // 01 section 8.5.1 step 2
        {
            var store = new MemorySaveStore();
            var t = TestSession.Open(store: store);
            long diamonds = t.Sim.State.Wallet.Diamond;
            long counter = t.Sim.State.Rng[HeroCat.Sim.Rng.RngStreams.SummonWeapon].Counter;
            store.FailWrites = true;
            var r = t.Runner.SummonAsync(Banner.WEAPON, 1, SummonPayment.Diamonds).GetAwaiter().GetResult();
            Assert.That(r.Error, Is.EqualTo(SummonError.SaveFailed));
            Assert.That(t.Sim.State.Pending.Summon, Is.Null);
            Assert.That(t.Sim.State.Wallet.Diamond, Is.EqualTo(diamonds));
            Assert.That(t.Sim.State.Rng[HeroCat.Sim.Rng.RngStreams.SummonWeapon].Counter, Is.EqualTo(counter));
        }

        [Test]
        public void ARefusal_ClearsTheIntent_AndChargesNothing()
        {
            var t = TestSession.Open();
            var r = t.Runner.SummonAsync(Banner.WEAPON, 1, SummonPayment.Tickets).GetAwaiter().GetResult();
            Assert.That(r.Error, Is.EqualTo(SummonError.NotEnoughTickets));
            Assert.That(t.Sim.State.Pending.Summon, Is.Null);
            Assert.That(t.Runner.Summons.ShouldResolve, Is.False);
        }

        [Test]
        public void APendingIntent_WaitsForTheOfflineReward_ThenResolvesOncePerResume()   // 01 section 8.2 step 3
        {
            var t = TestSession.Open();
            var s = t.Sim.State;
            t.Runner.OnPause();
            t.Clock.Advance(3 * 3600);
            t.Runner.OnResume();
            Assert.That(s.Offline.Pending, Is.Not.Null);
            s.Pending.Summon = new PendingSummon("fedcba9876543210fedcba9876543210", Banner.WEAPON, 1, SummonPayment.Diamonds.ToString(), TestContent.T0);
            Assert.That(t.Runner.Summons.ShouldResolve, Is.False, "SCR_OFFLINE_REWARD is open");
            t.Runner.Issue(new ClaimOfflineReward());
            Assert.That(t.Runner.Summons.ShouldResolve, Is.True);
            Assert.That(t.Runner.Summons.ResolveAsync().GetAwaiter().GetResult().IsOk, Is.True);
            Assert.That(t.Runner.Summons.ShouldResolve, Is.False);
        }

        [Test]
        public void AnAppliedSummon_TracksOneSummonEvent()   // 04 section 13, AC 12
        {
            var t = TestSession.Open();
            var r = t.Runner.SummonAsync(Banner.WEAPON, 10, SummonPayment.Diamonds).GetAwaiter().GetResult();
            Assert.That(r.IsOk, Is.True);
            var e = t.Analytics.Events.Single(x => x.Name == "summon");
            Assert.That(e.Props["requestId"], Is.EqualTo(r.Value.RequestId));
            Assert.That(e.Props["count"], Is.EqualTo(10));
            Assert.That(e.Props["diamondsSpent"], Is.EqualTo(Cost(10)));
            Assert.That(e.Props["source"], Is.EqualTo("LOCAL"));
            Assert.That(t.Log.Records.OfType<ApplySummonResult>().Count(), Is.EqualTo(1));
            Assert.That(e.Props.Keys, Is.EquivalentTo(new[]
            {
                "requestId", "banner", "count", "payment", "diamondsSpent", "ticketsSpent", "pityBefore", "pityAfter", "bannerLevelBefore",
                "bannerLevelAfter", "results", "lplusCount", "guaranteeApplied", "source", "latencyMs",
            }), "04 section 13");
            Assert.That(e.Props["payment"], Is.EqualTo("DIAMOND"));
            Assert.That(e.Props["ticketsSpent"], Is.EqualTo(0L));
            var rows = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)e.Props["results"];
            Assert.That(rows, Has.Count.EqualTo(10));
            Assert.That(rows[0].Keys, Is.EquivalentTo(new[]
            {
                "itemId", "rarity", "isNew", "levelBefore", "levelAfter", "overflowRockCandy", "guaranteeApplied",
            }));
            var pulls = r.Value.Pulls;
            Assert.That(rows.Select(x => (string)x["itemId"]), Is.EqualTo(pulls.Select(p => p.ItemId)));
            Assert.That(e.Props["guaranteeApplied"], Is.EqualTo(pulls.Any(p => p.GuaranteeApplied)), "true exactly when a slot was forced");
            Assert.That(e.Props["lplusCount"], Is.EqualTo(pulls.Count(p => p.Rarity >= Rarity.LEGENDARY)));
        }

        [Test]
        public void AnAppliedSummon_TracksOneItemAcquiredPerResult_WithTheRequestId()   // 04 section 13
        {
            var t = TestSession.Open();
            int before = t.Analytics.Events.Count;
            var r = t.Runner.SummonAsync(Banner.WEAPON, 10, SummonPayment.Diamonds).GetAwaiter().GetResult();
            var rows = t.Analytics.Events.Skip(before).Where(x => x.Name == "item_acquired").ToList();
            Assert.That(rows, Has.Count.EqualTo(10));
            for (int i = 0; i < rows.Count; i++)
            {
                var p = r.Value.Pulls[i];
                var e = rows[i].Props;
                Assert.That(e.Keys, Is.EquivalentTo(new[]
                {
                    "banner", "itemId", "rarity", "isNew", "copiesAfter", "levelBefore", "levelAfter", "acquireReason", "requestId",
                }));
                Assert.That((e["banner"], e["itemId"], e["rarity"], e["isNew"], e["copiesAfter"], e["levelBefore"], e["levelAfter"]),
                            Is.EqualTo(("WEAPON", p.ItemId, p.Rarity.ToString(), p.IsNew, p.CopiesAfter, p.LevelBefore, p.LevelAfter)));
                Assert.That((e["acquireReason"], e["requestId"]), Is.EqualTo(("SUMMON", r.Value.RequestId)));
            }
        }

        [Test]
        public void AReplayedRequestId_TracksNoSummonAndNoItems()   // 04 AC 12
        {
            var t = TestSession.Open();
            var s = t.Sim.State;
            var intent = new PendingSummon("0123456789abcdef0123456789abcdef", Banner.WEAPON, 1, SummonPayment.Diamonds.ToString(), TestContent.T0);
            s.Pending.Summon = intent;
            Assert.That(t.Runner.Summons.ResolveAsync().GetAwaiter().GetResult().IsOk, Is.True);
            int before = t.Analytics.Events.Count;
            s.Pending.Summon = intent;
            Assert.That(t.Runner.Summons.ResolveAsync().GetAwaiter().GetResult().Error, Is.EqualTo(SummonError.Replayed));
            Assert.That(t.Analytics.Events.Skip(before).Count(x => x.Name == "summon" || x.Name == "item_acquired"), Is.Zero);
        }

        [Test]
        public void ANewProfile_TracksTheStarterWeapon_AsATutorialItemAcquired()   // 04 section 13
        {
            var t = TestSession.Open();
            var acquired = t.Analytics.Events.Where(x => x.Name == "item_acquired").ToList();
            Assert.That(acquired.Count(x => (string)x.Props["banner"] == "COMPANION"), Is.EqualTo(5), "the five starter companions (04 section 8.3)");
            Assert.That(acquired.Count(x => (string)x.Props["banner"] == "WEAPON"), Is.EqualTo(5), "the five starter weapons (00 decision 111)");
            var id = TestContent.Db.TutorialWeaponId;
            var e = acquired.First(x => (string)x.Props["banner"] == "WEAPON" && (string)x.Props["itemId"] == id);
            Assert.That((e.Props["banner"], e.Props["itemId"], e.Props["isNew"], e.Props["levelBefore"], e.Props["acquireReason"], e.Props["requestId"]),
                        Is.EqualTo(("WEAPON", id, true, 0, "TUTORIAL", (object)null)));
            Assert.That(e.Props["copiesAfter"], Is.EqualTo(t.Sim.State.Weapons.Items[id].Copies));
            Assert.That(e.Props["rarity"], Is.EqualTo(TestContent.Db.Weapons[id].Rarity.ToString()));
        }

        [Test]
        public void AWeaponSummon_AutoEquip_TracksItemEquipped()   // 04 section 13
        {
            var t = TestSession.Open();
            var s = t.Sim.State;
            string before = s.Party.Slots[0].Weapon;
            for (int i = 0; i < 5 && s.Party.Slots[0].Weapon == before; i++)
                t.Runner.SummonAsync(Banner.WEAPON, 10, SummonPayment.Diamonds).GetAwaiter().GetResult();
            Assume.That(s.Party.Slots[0].Weapon, Is.Not.EqualTo(before), "a better weapon was pulled");
            var e = t.Analytics.Events.First(x => x.Name == "item_equipped");
            Assert.That(e.Props.Keys, Is.EquivalentTo(new[] { "banner", "slot", "itemId", "previousItemId", "auto" }));
            Assert.That((e.Props["banner"], e.Props["slot"], e.Props["previousItemId"], e.Props["auto"]), Is.EqualTo(("WEAPON", 0, before, true)));
        }
    }

    /// <summary>01 section 8.2 step 2 (issue #65): a clock sync that jumps past CLOCK_JUMP_WARN_SEC.</summary>
    public class ClockJumpTests
    {
        [Test]
        public void ALongerRealGap_IsCreditedAsASecondCatchupWindow()
        {
            var t = TestSession.Open();
            t.Play(5);
            long extrapolated = t.Clock.NowServerSeconds;
            long jump = 2 * 3600;
            t.Runner.OnClockSynced(extrapolated, extrapolated + jump);
            var pending = t.Sim.State.Offline.Pending;
            Assert.That(pending, Is.Not.Null);
            Assert.That(pending.ElapsedSec, Is.EqualTo(jump).Within(5));
            Assert.That(t.Analytics.Events.Single(e => e.Name == "clock_anomaly").Props["kind"], Is.EqualTo("resync"));
            Assert.That(t.Runner.TicksRunning, Is.False, "the reward modal holds ticks");
        }

        [Test]
        public void ASecondWindow_SumsIntoThePendingReward()
        {
            var t = TestSession.Open();
            t.Runner.OnPause();
            t.Clock.Advance(3600);
            t.Runner.OnResume();
            long first = t.Sim.State.Offline.Pending.ElapsedSec;
            long now = t.Clock.NowServerSeconds;
            t.Runner.OnClockSynced(now, now + 1800);
            Assert.That(t.Sim.State.Offline.Pending.ElapsedSec, Is.EqualTo(first + 1800));
        }

        [Test]
        public void AShorterRealGap_TakesNothingBack_AndASmallJumpIsIgnored()
        {
            var t = TestSession.Open();
            t.Play(5);
            string before = t.Sim.StateHash();
            long now = t.Clock.NowServerSeconds;
            t.Runner.OnClockSynced(now, now + HeroCat.Runtime.RuntimeConfig.CLOCK_JUMP_WARN_SEC);
            Assert.That(t.Analytics.Count("clock_anomaly"), Is.EqualTo(0));
            t.Runner.OnClockSynced(now, now - 3600);
            Assert.That(t.Analytics.Count("clock_anomaly"), Is.EqualTo(1));
            Assert.That(t.Sim.StateHash(), Is.EqualTo(before));
            Assert.That(t.Sim.State.Offline.Pending, Is.Null);
        }

        [Test]
        public void ABackwardsResync_UsesTheSameThresholdAsForwards()   // 02 section 11.3, issue #257
        {
            var t = TestSession.Open();
            t.Play(5);
            long now = t.Clock.NowServerSeconds;
            long warn = HeroCat.Runtime.RuntimeConfig.CLOCK_JUMP_WARN_SEC;
            t.Runner.OnClockSynced(now, now - 6);      // past CLOCK_BACKWARDS_TOLERANCE_S, but no resync event
            t.Runner.OnClockSynced(now, now - warn);
            Assert.That(t.Analytics.Count("clock_anomaly"), Is.EqualTo(0));
            t.Runner.OnClockSynced(now, now - warn - 1);
            var e = t.Analytics.Events.Single(x => x.Name == "clock_anomaly");
            Assert.That(e.Props["kind"], Is.EqualTo("resync"));
            Assert.That(e.Props["negative"], Is.EqualTo(true));
            Assert.That(e.Props["seconds"], Is.EqualTo(-warn - 1));
        }

        [Test]
        public void AServerClockObservation_LandsOnTheNextFrame()
        {
            var device = new FakeClock(TestContent.T0);
            var clock = new ServerClock(device: device);
            var t = TestSession.Open(clock: device, clockOverride: clock);
            clock.Observe(device.NowServerSeconds + 4 * 3600, 0);   // the device clock was 4 h behind the server
            Assert.That(t.Sim.State.Offline.Pending, Is.Null, "nothing happens off the frame loop");
            t.Mono.Advance(0.1);
            t.Runner.OnFrame();
            Assert.That(t.Sim.State.Offline.Pending, Is.Not.Null);
            Assert.That(t.Sim.State.Offline.Pending.ElapsedSec, Is.EqualTo(4 * 3600).Within(5));
        }
    }
}
