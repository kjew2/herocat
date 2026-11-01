using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>10 section 1.1's Determinism group and 00 contract 1 on the compiled sim (issue #139).</summary>
    public class DeterminismGuardTests
    {
        static string SimAssembly => typeof(Simulation).Assembly.Location;

        [Test]
        public void Sim_Il_UsesNoFloatClockRandomReflectionOrTranscendentals()   // 00 contract 1
        {
            var hits = IlGuard.Scan(SimAssembly, _ => true, assemblyWideTypeRefs: true);
            Assert.That(hits, Is.Empty, string.Join("\n", hits));
        }

        [Test]
        public void IlGuard_CatchesEachBannedPattern()   // the guard itself: a deliberate violation must fail it
        {
            var hits = IlGuard.Scan(typeof(IlGuardViolations).Assembly.Location,
                                    t => t == typeof(IlGuardViolations).FullName, assemblyWideTypeRefs: false);
            string all = string.Join("\n", hits);
            foreach (var expect in new[] { "System.Math::Pow", "System.Math::Exp", "System.Math::Log", "System.DateTime::get_UtcNow",
                                           "System.DateTimeOffset::get_Now", "banned type System.Random", "System.Type::GetMethod",
                                           "float opcode", "float parameter", "banned type System.MathF" })
                Assert.That(all, Does.Contain(expect));
            Assert.That(hits.Any(h => h.Contains("::Clean:")), Is.False, all);
            Assert.That(hits.Any(h => h.Contains("::TypeName:")), Is.False, "MemberInfo.Name is allowed: " + all);
        }

        [Test]
        public void AdvanceTwice_FromTheSameState_GivesTheSameHash()
        {
            var a = Combat.NewSim(hits: false);
            var b = new Simulation(StateCopy.DeepClone(a.State), Combat.Db);
            a.Advance(200_000);
            b.Advance(200_000);
            Assert.That(b.StateHash(), Is.EqualTo(a.StateHash()));
        }

        [Test]
        public void AdvanceWithACommandLog_ReplaysToTheSameHash()
        {
            var genesis = TestContent.NewState();
            var sim = new Simulation(StateCopy.DeepClone(genesis), Combat.Db);
            var log = new ListCommandLog();
            sim.CommandLog = log;
            sim.Apply(new ApplyServerTime(TestContent.T0));
            var rng = new Random(7);
            for (int i = 0; i < 300; i++)
            {
                sim.Advance(rng.Next(1, 400));
                switch (rng.Next(4))
                {
                    case 0: sim.Apply(new TrainStatMax((Stat)rng.Next(6))); break;
                    case 1: sim.Apply(new TapZap(rng.Next(0, 12))); break;
                    case 2: sim.Apply(new AttemptBoss()); break;
                    default: sim.Apply(new TrainStat(Stat.ATK, 1)); break;
                }
            }
            var replay = new Simulation(genesis, Combat.Db);
            replay.Replay(log.Records, sim.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(sim.StateHash()));
        }

        [Test]
        public void FourSeedsInParallel_EqualSequential()
        {
            static string Run(ulong seed)
            {
                var sim = new Simulation(ProfileSeeds.Reseed(seed), Combat.Db);
                sim.Advance(100_000);
                return sim.StateHash();
            }
            var seeds = new ulong[] { 1, 2, 3, 4 };
            var sequential = seeds.Select(Run).ToArray();
            var parallel = new string[seeds.Length];
            Parallel.For(0, seeds.Length, i => parallel[i] = Run(seeds[i]));
            Assert.That(parallel, Is.EqualTo(sequential));
            Assert.That(sequential.Distinct().Count(), Is.EqualTo(seeds.Length), "seeds differ");
        }
    }

    /// <summary>Test-only seed variation: a different RNG root per seed, the rest of the profile unchanged.</summary>
    internal static class ProfileSeeds
    {
        public static GameState Reseed(ulong seed) =>
            HeroCat.Runtime.Profile.ProfileFactory.Create(Combat.Db, Guid.Empty, TestContent.T0,
                new Guid("de71ce00-0000-4000-8000-000000000001"), new Guid($"7e57a7e0-0000-4000-8000-{seed:x12}"));
    }

    /// <summary>
    /// 10 section 1.1's Perf group (issue #139, 03 criterion 19). Separate category so a perf flake reads as one; check.sh
    /// runs it as its own step in full mode. Budgets apply to Release builds only.
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

        /// <summary>A stage-300 profile with Training sized so enemies die (TODO(#160): six skills and four companions, MVP).</summary>
        static Simulation Stage300()
        {
            var sim = Combat.NewSim(hits: false);
            var s = sim.State;
            s.Combat.Stage = 300;
            s.Progress.HighestCleared = 299;
            Combat.SetLevel(sim, Stat.ATK, 30_000);
            Combat.SetLevel(sim, Stat.ATK_SPD, 100);
            sim.ForceCoalesce = true;
            return sim;
        }

        [Test]
        public void MillionTicks_FromStage300_UnderTwoSeconds()   // 03 criterion 19
        {
            RequireRelease();
            var sim = Stage300();
            sim.Advance(10_000);   // warm up the JIT
            sim.DrainEvents();
            long kills = sim.State.Stats.Kills;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) { sim.Advance(1000); sim.DrainEvents(); }
            sw.Stop();
            TestContext.Out.WriteLine($"1M ticks in {sw.ElapsedMilliseconds} ms, {sim.State.Stats.Kills - kills} kills");
            Assert.That(sim.State.Stats.Kills - kills, Is.GreaterThan(1000), "the fixture must kill");
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000));
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
        public void Kills_AllocateUnder128BytesEach()   // 03 criterion 19
        {
            RequireRelease();
            var sim = Stage300();
            sim.Advance(10_000);
            sim.DrainEvents();
            long kills = sim.State.Stats.Kills;
            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Advance(100_000);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            long k = sim.State.Stats.Kills - kills;
            TestContext.Out.WriteLine($"{bytes} B over {k} kills");
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

    /// <summary>Deliberate violations for <see cref="DeterminismGuardTests.IlGuard_CatchesEachBannedPattern"/>. Never called.</summary>
    internal static class IlGuardViolations
    {
        public static double Pow(double x) => Math.Pow(x, 2) + Math.Exp(x) + Math.Log(x);
        public static long Clock() => DateTime.UtcNow.Ticks + DateTimeOffset.Now.Ticks;
        public static int Rand() => new Random(1).Next();
        public static object Reflect() => typeof(string).GetMethod("Trim", Type.EmptyTypes);
        public static double Floats(double x) { float f = (float)x; f *= 1.5f; return f; }
        public static double Param(float f) => f;
        public static double Mf(double x) => MathF.Sqrt(2);
        public static double Clean(double x) => Math.Sqrt(x) * 2.0;
        public static string TypeName() => typeof(Stat).Name;
    }
}
