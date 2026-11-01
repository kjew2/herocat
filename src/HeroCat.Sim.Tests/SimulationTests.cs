using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>A test-only command whose handler re-enters Apply (01 AC 12).</summary>
    sealed record ReentrantCommand : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.Apply(new ApplyServerTime(1));
    }

    /// <summary>The command door, events and snapshot (01 section 2; issue #64).</summary>
    public class SimulationTests
    {
        static readonly long T0 = TestContent.T0;

        [Test]
        public void Apply_StampsIssuedAtTick_AndLogsBeforeApplying()
        {
            var sim = TestContent.NewSim();
            var log = new ListCommandLog();
            sim.CommandLog = log;
            sim.Advance(17);
            var r = sim.Apply(new ApplyServerTime(T0 + 2));
            Assert.That(r.Ok, Is.True);
            Assert.That(log.Records.Single().IssuedAtTick, Is.EqualTo(17));
            sim.Apply(new ApplyServerTime(-5));
            Assert.That(log.Count, Is.EqualTo(2), "rejected commands are logged too");
        }

        [Test]
        public void Reject_IsPure_StateHashUnchanged_NoEvents()   // 01 AC 2
        {
            foreach (var c in new Command[]
            {
                new ApplyServerTime(-1), new ComputeOfflineCatchup(-1), new ApplySummonResult(null), new ApplyInboxGrant(null),
            })
            {
                var sim = TestContent.NewSim();
                sim.DrainEvents();
                string before = sim.StateHash();
                var r = sim.Apply(c);
                Assert.That(r.Ok, Is.False, c.ToString());
                Assert.That(sim.StateHash(), Is.EqualTo(before), c.ToString());
                Assert.That(sim.DrainEvents(), Is.Empty, c.ToString());
            }
            var x = TestContent.NewSim().Apply(new ApplyServerTime(-1));
            Assert.That((x.Code, x.Reason), Is.EqualTo((RejectCode.BadArgument, SimRejectReasons.BAD_SERVER_TIME)));
            var y = TestContent.NewSim().Apply(new ApplyInboxGrant(null));
            Assert.That((y.Code, y.Reason), Is.EqualTo((RejectCode.NotAvailable, SimRejectReasons.NOT_IMPLEMENTED)));
            var z = TestContent.NewSim().Apply(new ApplySummonResult(null));
            Assert.That((z.Code, z.Reason), Is.EqualTo((RejectCode.BadArgument, CollectionRejectReasons.SUMMON_BAD_PAYLOAD)));
        }

        [Test]
        public void Fuzz_ApplyNeverThrows_ForEveryCommandType()   // 01 AC 3
        {
            var rng = new Random(1234);
            var types = typeof(Command).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(Command).IsAssignableFrom(t)).ToList();
            Assert.That(types.Select(t => t.Name), Is.SupersetOf(new[] { "ApplyServerTime", "ComputeOfflineCatchup", "ApplySummonResult", "ApplyInboxGrant" }));
            foreach (var t in types)
            {
                var ctor = t.GetConstructors().Single(c => c.GetParameters().Length > 0 || t.GetConstructors().Length == 1);
                var sim = TestContent.NewSim();
                for (int i = 0; i < 10_000; i++)
                {
                    var args = ctor.GetParameters().Select(p => RandomArg(p.ParameterType, rng)).ToArray();
                    var cmd = (Command)ctor.Invoke(args);
                    Assert.DoesNotThrow(() => sim.Apply(cmd), $"{t.Name}({string.Join(", ", args)})");
                    if (i % 97 == 0) sim.Advance(rng.Next(0, 50));
                }
            }
        }

        static object RandomArg(Type t, Random r)
        {
            if (t == typeof(long)) return r.Next(4) switch { 0 => long.MinValue, 1 => long.MaxValue, 2 => (long)r.Next(-1000, 1000), _ => T0 + r.Next(-100000, 10_000_000) };
            if (t == typeof(int)) return r.Next(4) switch { 0 => int.MinValue, 1 => int.MaxValue, _ => r.Next(-1000, 1000) };
            if (t == typeof(string)) return r.Next(3) == 0 ? null : "x" + r.Next();
            if (t.IsEnum) { var v = Enum.GetValues(t); return v.GetValue(r.Next(v.Length)); }
            return null;   // payload records: null is the malformed case
        }

        [Test]
        public void Apply_ReenteredFromAHandler_Throws()   // 01 AC 12 (engine-free half)
        {
            var sim = TestContent.NewSim();
            Assert.Throws<InvalidOperationException>(() => sim.Apply(new ReentrantCommand()));
            Assert.That(sim.Apply(new ApplyServerTime(T0)).Ok, Is.True, "the guard resets after the throw");
            Assert.Throws<ArgumentNullException>(() => sim.Apply(null));
        }

        [Test]
        public void ApplyServerTime_RollsTheFirstDay_AndPaysTheLogin()
        {
            var sim = TestContent.NewSim(sync: false);
            Assert.That(sim.State.Time.DayKey, Is.EqualTo(-1));
            sim.Apply(new ApplyServerTime(T0));
            var k = TestContent.Db.Constants;
            Assert.That(sim.State.Time.DayKey, Is.EqualTo((int)(T0 / k.DaySeconds)));
            Assert.That(sim.State.Time.WeekKey, Is.EqualTo((int)Math.Floor((T0 / k.DaySeconds - 4) / 7.0)));
            Assert.That(sim.State.Time.LoginDayKey, Is.EqualTo(sim.State.Time.DayKey));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(1500 + k.LoginDiamonds));
            var ev = sim.DrainEvents();
            Assert.That(ev.Select(e => e.GetType().Name), Is.EqualTo(new[] { "WeekRolledOver", "DayRolledOver", "CurrencyChanged" }));
            var cc = (CurrencyChanged)ev[2];
            Assert.That((cc.Currency, cc.Delta, cc.BalanceAfter, cc.Reason), Is.EqualTo((Currency.DIAMOND, (double)k.LoginDiamonds, 1550.0, CurrencyReason.LOGIN)));
            Assert.That(sim.State.Stats.ClientDiamondsEarned[CurrencyReason.LOGIN], Is.EqualTo(k.LoginDiamonds));
        }

        [Test]
        public void Ticks_CrossMidnight_RollTheDayOnce_InVirtualTime()
        {
            long beforeMidnight = T0 + 86400 - 30;
            var sim = TestContent.NewSim(beforeMidnight);
            sim.DrainEvents();
            int day = sim.State.Time.DayKey;
            sim.Advance(299);   // 29.9 s
            Assert.That(sim.State.Time.DayKey, Is.EqualTo(day));
            sim.Advance(1);     // the tick that reaches 00:00:00
            Assert.That(sim.State.Time.DayKey, Is.EqualTo(day + 1));
            Assert.That(sim.VirtualNowServerSeconds, Is.EqualTo(T0 + 86400));
            sim.Advance(36000);
            Assert.That(sim.DrainEvents().OfType<DayRolledOver>().Count(), Is.EqualTo(1));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(1500 + 2 * TestContent.Db.Constants.LoginDiamonds));
        }

        [Test]
        public void ThreeDaysAway_OneRollover_OneLogin()   // 06 section 4.2 rule 1
        {
            var sim = TestContent.NewSim();
            long d = sim.State.Wallet.Diamond;
            sim.DrainEvents();
            sim.Apply(new ApplyServerTime(T0 + 3 * 86400 + 5));
            Assert.That(sim.DrainEvents().OfType<DayRolledOver>().Count(), Is.EqualTo(1));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(d + TestContent.Db.Constants.LoginDiamonds));
        }

        [Test]
        public void ClockBackwards_EmitsOnce_GrantsNothing()
        {
            var sim = TestContent.NewSim();
            sim.Apply(new ApplyServerTime(T0 + 7200));
            sim.DrainEvents();
            string diamonds = sim.State.Wallet.Diamond.ToString();
            sim.Apply(new ApplyServerTime(T0));
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<ClockWentBackwards>().Single().DeltaSeconds, Is.EqualTo(7200));
            Assert.That(ev.OfType<DayRolledOver>(), Is.Empty);
            Assert.That(sim.State.Wallet.Diamond.ToString(), Is.EqualTo(diamonds));
        }

        [Test]
        public void ComputeOfflineCatchup_MovesTheAnchorForwardOnly()
        {
            var sim = TestContent.NewSim();
            sim.Apply(new ComputeOfflineCatchup(T0 + 3600));
            Assert.That(sim.State.Time.LastTickServerTime, Is.EqualTo(T0 + 3600));
            sim.Apply(new ComputeOfflineCatchup(T0));
            Assert.That(sim.State.Time.LastTickServerTime, Is.EqualTo(T0 + 3600));
            Assert.That(sim.State.Offline.Pending, Is.Null, "TODO(#108)");
        }

        [Test]
        public void Coalescing_PastTheThreshold_IsDeterministic()   // 01 section 2.3
        {
            string Run()
            {
                var sim = TestContent.NewSim();
                sim.DrainEvents();
                for (int i = 0; i < SimConfig.EVENT_COALESCE_AT + 500; i++)
                {
                    sim.Emit(new CurrencyChanged(Currency.GOLD, 2, 2 * (i + 1), CurrencyReason.KILL));
                    sim.Emit(new EnemyKilled(1 + i / 10, false, 2, 5));
                }
                var ev = sim.DrainEvents();
                var batch = ev.OfType<EnemyKilledBatch>().Single();
                Assert.That(ev.Count, Is.EqualTo(SimConfig.EVENT_COALESCE_AT + 1));
                Assert.That(batch.Count + ev.OfType<EnemyKilled>().Count(), Is.EqualTo(SimConfig.EVENT_COALESCE_AT + 500));
                Assert.That(batch.GoldTotal, Is.EqualTo(2.0 * batch.Count));
                return string.Join("|", ev.Select(e => e.ToString()));
            }
            Assert.That(Run(), Is.EqualTo(Run()));
        }

        [Test]
        public void ForceCoalesce_BatchesFromTheFirstKill()
        {
            var sim = TestContent.NewSim();
            sim.DrainEvents();
            sim.ForceCoalesce = true;
            for (int i = 0; i < 5; i++) sim.Emit(new EnemyKilled(3, false, 1, 1));
            var b = (EnemyKilledBatch)sim.DrainEvents().Single();
            Assert.That((b.Count, b.FromStage, b.ToStage), Is.EqualTo((5, 3, 3)));
        }

        [Test]
        public void Snapshot_IsAValueCopy_AndAllocationFree()   // 01 section 2.4
        {
            var sim = TestContent.NewSim();
            sim.Advance(10);
            var s = sim.GetSnapshot();
            Assert.That(s.Tick, Is.EqualTo(10));
            Assert.That(s.Diamonds, Is.EqualTo(sim.State.Wallet.Diamond));
            Assert.That(s.Stage, Is.EqualTo(1));
            Assert.That(s.HeroExpToNext, Is.EqualTo(TestContent.Db.HeroLvlExp(1)));
            Assert.That(s.DamageNumbersThisTick.Length, Is.EqualTo(0));
            Assert.That(AllocatedBy(sim), Is.EqualTo(0));
            Assert.That(s.WithPendingInboxCount(3).PendingInboxCount, Is.EqualTo(3));
        }

        static long AllocatedBy(Simulation sim)
        {
            long best = long.MaxValue;
            for (int attempt = 0; attempt < 3; attempt++)   // the first pass may pay for tiered-JIT bookkeeping
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long sum = 0;
                for (int i = 0; i < 1000; i++) sum += sim.GetSnapshot().Tick;
                long used = GC.GetAllocatedBytesForCurrentThread() - before;
                if (sum < 0) return -1;
                best = Math.Min(best, used);
            }
            return best;
        }

        [Test]
        public void Replay_OfTheCommandLog_ReproducesTheStateHash()
        {
            var log = new ListCommandLog();
            var sim = new Simulation(TestContent.NewState(), TestContent.Db) { CommandLog = log };
            sim.Apply(new ApplyServerTime(T0));
            sim.Advance(12345);
            sim.Apply(new ApplyServerTime(T0 + 90000));
            sim.Apply(new ComputeOfflineCatchup(T0 + 90000));
            sim.Advance(777);
            sim.Apply(new ApplyServerTime(-1));
            sim.Advance(3);

            var replay = new Simulation(TestContent.NewState(), TestContent.Db);
            replay.Replay(log.Records, sim.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(sim.StateHash()));
            Assert.Throws<ReplayException>(() => replay.Replay(log.Records));
        }

        [Test]
        public void ActiveSeconds_CountsTickedTime()
        {
            var sim = TestContent.NewSim();
            sim.Advance(TestContent.Db.Constants.TickHz * 7 + 3);
            Assert.That(sim.State.Stats.ActiveSeconds, Is.EqualTo(7));
        }
    }
}
