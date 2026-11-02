using System;
using System.Diagnostics;
using System.Linq;
using HeroCat.Bots.Measure;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>The perf fixture as the Perf group uses it (03 criterion 19; issue #160).</summary>
    internal static class Stage300
    {
        /// <summary>The full-loadout stage-300 sim, anchored at T0, kills coalesced as the harness runs them.</summary>
        public static Simulation New(CombatEventMask events = CombatEventMask.Default)
        {
            var sim = PerfFixture.Create(TestContent.NewState(), TestContent.Mvp);
            sim.Apply(new ApplyServerTime(TestContent.T0));
            sim.ForceCoalesce = true;
            sim.CombatEvents = events;
            sim.DrainEvents();
            return sim;
        }
    }

    /// <summary>The fixture really is 03 criterion 19's loadout: every skill and companion slot casts, and enemies die.</summary>
    public class PerfFixtureTests
    {
        [Test]
        public void FullLoadout_EverySlotCasts_AndEnemiesDie()
        {
            var sim = Stage300.New(CombatEventMask.All);
            Assert.That(sim.State.Loadout.Skills.Count(id => id != null), Is.EqualTo(sim.Db.Constants.SkillSlots));
            Assert.That(sim.State.Loadout.Companions.Count(id => id != null), Is.EqualTo(sim.Db.Constants.CompanionSlots));
            Assert.That(sim.State.Combat.Stage, Is.EqualTo(PerfFixture.Stage));
            var skills = new bool[sim.State.Loadout.Skills.Length];
            var companions = new bool[sim.State.Loadout.Companions.Length];
            long kills = sim.State.Stats.Kills;
            for (int i = 0; i < 20; i++)
            {
                sim.Advance(1000);
                foreach (var e in sim.DrainEvents())
                {
                    if (e is SkillCast sc) skills[sc.Slot] = true;
                    if (e is CompanionAbilityUsed cu) companions[cu.Slot] = true;
                }
            }
            Assert.That(skills, Is.All.True, "every skill slot casts");
            Assert.That(companions, Is.All.True, "every companion slot casts");
            Assert.That(sim.State.Stats.Kills - kills, Is.GreaterThan(100), "the fixture must kill");
        }

        [Test]
        public void DefaultMask_LeavesOutTheLoopsOwnPerTickEvents()   // 03 criterion 19: casts and hits are opt-in
        {
            var sim = Stage300.New();
            sim.Advance(5000);
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<EnemySpawned>(), Is.Not.Empty);
            Assert.That(ev.Any(e => e is SkillCast || e is CompanionAbilityUsed || e is HitLanded), Is.False);
        }

        [Test]
        public void EventMask_ChangesNoState()
        {
            var all = Stage300.New(CombatEventMask.All);
            var none = Stage300.New(CombatEventMask.None);
            all.Advance(20_000);
            none.Advance(20_000);
            Assert.That(none.StateHash(), Is.EqualTo(all.StateHash()));
            Assert.That(none.DrainEvents().Any(e => e is SkillCast || e is CompanionAbilityUsed || e is EnemySpawned || e is HitLanded), Is.False);
        }
    }

    /// <summary>
    /// 10 section 1.1's Perf group (issues #139, #160; 03 criterion 19). Separate category so a perf flake reads as one;
    /// check.sh runs it as its own step in full mode. Budgets apply to Release builds only.
    /// </summary>
    [Category("Perf")]
    public class PerfTests
    {
        static void RequireRelease()
        {
#if DEBUG
            Assert.Ignore("perf budgets apply to Release builds");
#endif
        }

        /// <summary>03 criterion 19: the null sink is no per-tick event masked in, kills coalesced, the buffer drained.</summary>
        [Test]
        public void MillionTicks_FullLoadoutStage300_NullSink_UnderTwoSeconds()
        {
            RequireRelease();
            var sim = Stage300.New(CombatEventMask.None);
            sim.Advance(10_000);   // warm up the JIT
            sim.DrainEvents();
            long kills = sim.State.Stats.Kills;
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;   // printed beside wall time: a loaded runner shows as wall >> CPU
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) { sim.Advance(1000); sim.DrainEvents(); }
            sw.Stop();
            double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
            TestContext.Out.WriteLine($"1M ticks in {sw.ElapsedMilliseconds} ms (process CPU {cpu:0} ms), {sim.State.Stats.Kills - kills} kills, stage {sim.State.Combat.Stage}");
            Assert.That(sim.State.Stats.Kills - kills, Is.GreaterThan(1000), "the fixture must kill");
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000));
        }

        /// <summary>03 section 1 goal 4: with nothing masked in, a fighting tick (casts, DOTs, buffs, MP) allocates nothing.</summary>
        [Test]
        public void FullLoadout_NoEventsMaskedIn_AllocatesNothingPerTick()
        {
            RequireRelease();
            var sim = Stage300.New(CombatEventMask.None);
            Combat.AdvanceUntil<EnemyKilledBatch>(sim);   // past the first spawn, the JIT warm
            sim.State.Combat.Fight.EnemyHp = sim.State.Combat.Fight.EnemyMaxHp = 1e300;   // unkillable
            sim.State.Combat.Fight.Phase = FightPhase.Fighting;
            sim.Advance(2000);
            sim.DrainEvents();
            long kills = sim.State.Stats.Kills;
            long hits = sim.State.Stats.HeroHits;
            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Advance(10_000);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assume.That(sim.State.Stats.Kills, Is.EqualTo(kills), "no kill in the window");
            Assert.That(sim.State.Stats.HeroHits, Is.GreaterThan(hits), "the hero fights in the window");
            Assert.That(bytes, Is.EqualTo(0));
        }

        [Test]
        public void SteadyStateFighting_AllocatesNothingPerTick()   // 10 AC13 (CI part)
        {
            RequireRelease();
            var sim = Combat.NewSim(hits: false);
            sim.State.Combat.Stage = 300;   // a fresh hero against stage 300: hits for a long time, no kill
            sim.Advance(2000);
            sim.DrainEvents();
            long kills = sim.State.Stats.Kills;
            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Advance(10_000);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assume.That(sim.State.Stats.Kills, Is.EqualTo(kills), "no kill in the window");
            Assert.That(bytes, Is.EqualTo(0));
        }

        [Test]
        public void Kills_FullLoadout_DefaultMask_AllocateUnder128BytesEach()   // 03 criterion 19
        {
            RequireRelease();
            var sim = Stage300.New();
            sim.Advance(10_000);
            sim.DrainEvents();
            long kills = sim.State.Stats.Kills;
            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Advance(100_000);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            long k = sim.State.Stats.Kills - kills;
            TestContext.Out.WriteLine($"{bytes} B over {k} kills ({(double)bytes / Math.Max(1, k):0.0} B/kill), {sim.BufferedEvents.Count} events buffered");
            Assert.That(k, Is.GreaterThan(100));
            Assert.That((double)bytes / k, Is.LessThan(128));
        }

        [Test]
        public void ContentDbBuild_Under30Ms()   // 10 section 1.1
        {
            RequireRelease();
            var dtos = ContentLoader.LoadDtos(new FileContentSource(TestContent.Dir));
            ContentDb.Build(dtos, Tier.M0);   // warm up
            double best = double.MaxValue;   // best of five, so a scheduler hiccup on a shared runner is not a failure
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                ContentDb.Build(dtos, Tier.M0);
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds < best) best = sw.Elapsed.TotalMilliseconds;
            }
            TestContext.Out.WriteLine($"ContentDb.Build best {best:0.0} ms");
            Assert.That(best, Is.LessThan(30));
        }
    }
}
