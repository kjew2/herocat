using System.Linq;
using HeroCat.Sim.Events;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    public class ClockTests
    {
        static readonly long T0 = TestContent.T0;

        [Test]
        public void InStep_ClockCreditsNothing()
        {
            var sim = TestContent.NewSim();
            sim.Advance(600);
            double gold = sim.State.Currencies.Gold;
            var r = sim.SyncWallClock(T0 + 60);
            Assert.That(r.Gold, Is.EqualTo(0));
            Assert.That(sim.State.Currencies.Gold, Is.EqualTo(gold));
        }

        [Test]
        public void OfflineGap_ClosedForm_AndCap()
        {
            var db = TestContent.Db;
            var sim = TestContent.NewSim();
            double expected = 3 * 3600 * 0.5 * db.GoldPerKill(1) / sim.Query.KillTimeSeconds(1);
            var r = sim.SyncWallClock(T0 + 3 * 3600);
            Assert.That(r.Gold, Is.EqualTo(expected).Within(1e-9));
            Assert.That(sim.State.Currencies.Gold, Is.EqualTo(expected).Within(1e-9));

            var sim2 = TestContent.NewSim();
            var r2 = sim2.SyncWallClock(T0 + 30 * 3600);
            Assert.That(r2.Capped, Is.True);
            Assert.That(r2.CreditedSec, Is.EqualTo(8 * 3600));

            var sim3 = TestContent.NewSim();
            Assert.That(sim3.SyncWallClock(T0 + 59).Gold, Is.EqualTo(0));

            // catch-up consumes no RNG and pushes no stage
            Assert.That(sim2.State.Rng.Streams.Values.All(s => s.Counter == 0), Is.True);
            Assert.That(sim2.State.Stage.Stage, Is.EqualTo(1));
        }

        [Test]
        public void DayRollover_ByTicksAndBySync()
        {
            var box = new EventOutbox();
            long created = T0 + 86400 - 30;    // 23:59:30
            var sim = TestContent.NewSim(sink: box, created: created);
            sim.Tick();
            Assert.That(sim.State.Daily.DayKey, Is.EqualTo(created / 86400));
            Assert.That(sim.Pull().Reason, Is.EqualTo(Reject.None));     // free daily
            Assert.That(sim.State.Currencies.FreePullsDaily, Is.EqualTo(2));
            sim.Advance(300);                                            // crosses midnight in virtual time
            Assert.That(sim.State.Daily.DayKey, Is.EqualTo(created / 86400 + 1));
            Assert.That(sim.State.Currencies.FreePullsDaily, Is.EqualTo(3));
            Assert.That(box.Drain().OfType<DayRolledOver>().Count(), Is.EqualTo(2));

            sim.SyncWallClock(created + 31 + 2 * 86400);                 // two days away: exactly one more rollover
            Assert.That(box.Drain().OfType<DayRolledOver>().Count(), Is.EqualTo(1));
            Assert.That(sim.State.Currencies.Gems, Is.EqualTo(3 * 50));  // three login rewards
        }

        [Test]
        public void ClockBackwards_ChangesNothing()
        {
            var box = new EventOutbox();
            var sim = TestContent.NewSim(sink: box);
            sim.SyncWallClock(T0 + 7200);
            double gold = sim.State.Currencies.Gold;
            long day = sim.State.Daily.DayKey;
            box.Drain();
            sim.SyncWallClock(T0);
            Assert.That(box.Drain().OfType<ClockWentBackwards>().Count(), Is.EqualTo(1));
            Assert.That(sim.State.Currencies.Gold, Is.EqualTo(gold));
            Assert.That(sim.State.Daily.DayKey, Is.EqualTo(day));
        }

        [Test]
        public void IdleChest_ReadyAfterTimer()
        {
            var sim = TestContent.NewSim();
            Assert.That(sim.ClaimIdleChest().Reason, Is.EqualTo(Reject.ChestNotReady));
            sim.SyncWallClock(T0 + 4 * 3600);
            Assert.That(sim.ClaimIdleChest().Ok, Is.True);
            Assert.That(sim.ClaimIdleChest().Reason, Is.EqualTo(Reject.ChestNotReady));
        }
    }
}
