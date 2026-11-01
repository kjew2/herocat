using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Runtime.Save;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    internal static class Combat
    {
        public static ContentDb Db => TestContent.Db;
        public static Constants K => TestContent.Db.Constants;

        public static Simulation NewSim(bool hits = true)
        {
            var sim = TestContent.NewSim();
            sim.EmitHitLanded = hits;
            sim.DrainEvents();
            return sim;
        }

        public static void SetLevel(Simulation sim, Stat stat, int level)
        {
            sim.State.Training.Level[(int)stat] = level;
            sim.Query.MarkDirty();
        }

        public static void Give(Simulation sim, Currency c, double amount) =>
            Assert.That(Ledger.Apply(sim.State, new CurrencyDelta(c, amount), CurrencyReason.ADMIN, null), Is.True);

        public static void Own(CollectionState col, string id, int copies, int limitBreaks = 0) =>
            col.Items[id] = new OwnedItem { Copies = copies, LimitBreaks = limitBreaks };

        /// <summary>Ticks one at a time until an event of type T is emitted; returns it (its Tick is the emission tick).</summary>
        public static T AdvanceUntil<T>(Simulation sim, int maxTicks = 100_000, Func<T, bool> where = null) where T : Event
        {
            for (int i = 0; i < maxTicks; i++)
            {
                sim.Advance(1);
                foreach (var e in sim.DrainEvents())
                    if (e is T t && (where == null || where(t))) return t;
            }
            Assert.Fail($"no {typeof(T).Name} within {maxTicks} ticks");
            return null;
        }

        /// <summary>Puts the fight on <paramref name="stage"/> with a fresh spawn pending.</summary>
        public static void GoTo(Simulation sim, int stage, int kills = 0, BossPhase boss = BossPhase.None)
        {
            var c = sim.State.Combat;
            c.Stage = stage;
            c.KillsInStage = kills;
            c.Boss = boss;
            c.Fight = null;
            sim.DrainEvents();
        }

        public static FightState Fight(Simulation sim) => sim.State.Combat.Fight;
    }

    /// <summary>03 section 2: the stage loop (acceptance 1-5, 7, 8, 15; issues #81, #82).</summary>
    public class CombatTests
    {
        static Constants K => Combat.K;

        [Test]
        public void AC1_FirstSpawnAndFirstHit_Timing()
        {
            var sim = Combat.NewSim();
            var spawn = Combat.AdvanceUntil<EnemySpawned>(sim);
            Assert.That(spawn.Tick, Is.EqualTo(K.SpawnDelayTicks));
            Assert.That((spawn.Stage, spawn.IsBoss, spawn.MaxHp), Is.EqualTo((1, false, Combat.Db.EnemyHp.Eval(1))));
            var hit = Combat.AdvanceUntil<HitLanded>(sim);
            Assert.That(hit.Tick, Is.EqualTo(K.SpawnDelayTicks + K.AttackIntervalTicks));
        }

        [Test]
        public void AC1_AtkSpd100_HitsEveryFiveTicks_For1000Hits()
        {
            var sim = Combat.NewSim();
            Combat.SetLevel(sim, Stat.ATK_SPD, 100);
            Assert.That(sim.Query.AttackGainUnits, Is.EqualTo(2 * K.AttackProgressUnit));
            Combat.AdvanceUntil<EnemySpawned>(sim);
            Combat.Fight(sim).EnemyHp = Combat.Fight(sim).EnemyMaxHp = 1e300;   // unkillable
            var ticks = new List<long>();
            while (ticks.Count < 1001)
            {
                sim.Advance(1);
                ticks.AddRange(sim.DrainEvents().OfType<HitLanded>().Select(h => h.Tick));
            }
            var gaps = ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToList();
            Assert.That(gaps.Count, Is.EqualTo(1000));
            Assert.That(gaps.All(g => g == 5), Is.True);
        }

        [Test]
        public void AC2_TenKillsOnStage1_AdvanceOnce()
        {
            var sim = Combat.NewSim(hits: false);
            var ev = new List<Event>();
            while (sim.State.Combat.Stage == 1) { sim.Advance(1); ev.AddRange(sim.DrainEvents()); }
            Assert.That(ev.OfType<EnemyKilled>().Count(), Is.EqualTo(K.KillsPerStage));
            Assert.That(ev.OfType<StageCleared>().Single(), Is.EqualTo(new StageCleared(1, 2, false) { Tick = ev.OfType<StageCleared>().Single().Tick }));
            Assert.That(sim.State.Progress.HighestCleared, Is.EqualTo(1));
            Assert.That(sim.State.Combat.KillsInStage, Is.EqualTo(0));
        }

        [Test]
        public void AC2_TheTenthEnemyOfStage10_IsTheBoss()
        {
            var sim = Combat.NewSim(hits: false);
            Combat.GoTo(sim, 10, kills: K.KillsPerStage - 2);
            Combat.AdvanceUntil<EnemyKilled>(sim);
            Assert.That(sim.State.Combat.Boss, Is.EqualTo(BossPhase.Pending));
            var boss = Combat.AdvanceUntil<EnemySpawned>(sim);
            Assert.That(boss.IsBoss, Is.True);
            Assert.That(boss.MaxHp, Is.EqualTo(K.BossHpMult * Combat.Db.EnemyHp.Eval(10)));
            Assert.That(Combat.Fight(sim).BossTicksLeft, Is.EqualTo(K.BossTimeTicks));
        }

        /// <summary>A stage-10 boss whose HP needs exactly <paramref name="hits"/> - 0.5 non-crit hero hits.</summary>
        static (Simulation sim, EnemySpawned spawn) BossNeeding(double hits)
        {
            var sim = Combat.NewSim(hits: false);
            Combat.GoTo(sim, 10, kills: K.KillsPerStage - 1, boss: BossPhase.Pending);
            var spawn = Combat.AdvanceUntil<EnemySpawned>(sim);
            Assert.That(spawn.IsBoss, Is.True);
            var f = Combat.Fight(sim);
            f.EnemyHp = f.EnemyMaxHp = hits * sim.Query.HeroHit;   // CRIT_RATE 0: no crits
            return (sim, spawn);
        }

        [Test]
        public void AC3_ABossNeeding30Hits_IsClearedOnTheFinalTick()
        {
            double needed = (double)K.BossTimeTicks / K.AttackIntervalTicks;   // 30 * speedFactor at speed 1
            Assert.That(needed, Is.EqualTo(30));
            var (sim, spawn) = BossNeeding(needed - 0.5);
            var kill = Combat.AdvanceUntil<EnemyKilled>(sim, K.BossTimeTicks + 1);
            Assert.That(kill.IsBoss, Is.True);
            Assert.That(kill.Tick, Is.EqualTo(spawn.Tick + K.BossTimeTicks), "the 30th hit lands on the timer's final tick and counts");
            Assert.That(sim.State.Combat.Stage, Is.EqualTo(11));
            Assert.That(sim.State.Stats.BossFails, Is.EqualTo(0));
        }

        [Test]
        public void AC3_ABossNeedingOneMoreHit_FailsAtTheTimer()
        {
            var (sim, spawn) = BossNeeding(30.5);
            double hit = sim.Query.HeroHit;
            var fail = Combat.AdvanceUntil<BossFailed>(sim, K.BossTimeTicks + 1);
            Assert.That(fail.Tick, Is.EqualTo(spawn.Tick + K.BossTimeTicks));
            Assert.That((fail.Stage, fail.Attempt), Is.EqualTo((10, 1)));
            Assert.That(fail.RemainingFraction, Is.EqualTo(0.5 * hit / (30.5 * hit)).Within(1e-9));
            var c = sim.State.Combat;
            Assert.That((c.Boss, c.BossFailsOnStage, sim.State.Stats.FirstWallStage), Is.EqualTo((BossPhase.Walled, 1, 10)));
            Assert.That(Combat.Fight(sim).Phase, Is.EqualTo(FightPhase.Spawning));
        }

        [Test]
        public void AC4_WalledFarming_PaysAndHoldsKills_AttemptBossDespawnsAndRejects()
        {
            var sim = Combat.NewSim(hits: false);
            Combat.GoTo(sim, 10, kills: K.KillsPerStage - 1, boss: BossPhase.Walled);
            Combat.SetLevel(sim, Stat.GOLD_GAIN, 5);
            Combat.SetLevel(sim, Stat.EXP_GAIN, 3);
            for (int i = 0; i < 3; i++)
            {
                var k = Combat.AdvanceUntil<EnemyKilled>(sim);
                Assert.That(k.IsBoss, Is.False);
                Assert.That(k.Gold, Is.EqualTo(Combat.Db.GoldPerKill.Eval(10) * sim.Query.GoldMult));
                Assert.That(k.Exp, Is.EqualTo(Combat.Db.ExpPerKill.Eval(10) * sim.Query.ExpMult));
                Assert.That(sim.Query.GoldMult, Is.EqualTo(1.1).Within(1e-12));
                Assert.That(sim.State.Combat.KillsInStage, Is.EqualTo(K.KillsPerStage - 1));
            }

            Combat.AdvanceUntil<EnemySpawned>(sim);
            double gold = sim.State.Wallet.Gold, exp = sim.State.Hero.Exp;
            long kills = sim.State.Stats.Kills;
            Assert.That(sim.Apply(new AttemptBoss()).Ok, Is.True);
            Assert.That((sim.State.Wallet.Gold, sim.State.Hero.Exp, sim.State.Stats.Kills), Is.EqualTo((gold, exp, kills)), "despawn pays nothing");
            Assert.That(Combat.Fight(sim).Phase, Is.EqualTo(FightPhase.Spawning));
            var r = sim.Apply(new AttemptBoss());
            Assert.That((r.Code, r.Reason), Is.EqualTo((RejectCode.NotAvailable, CombatRejectReasons.BOSS_ALREADY_PENDING)));
            Assert.That(Combat.AdvanceUntil<EnemySpawned>(sim).IsBoss, Is.True);

            var normal = Combat.NewSim();
            Combat.GoTo(normal, 7);
            var r2 = normal.Apply(new AttemptBoss());
            Assert.That((r2.Code, r2.Reason), Is.EqualTo((RejectCode.NotAvailable, CombatRejectReasons.NO_BOSS_TO_ATTEMPT)));
        }

        [Test]
        public void BossKill_PaysBossGold_FirstClearOnce_AndOneLootDraw()
        {
            var sim = Combat.NewSim(hits: false);
            Combat.GoTo(sim, 10, kills: K.KillsPerStage - 1, boss: BossPhase.Pending);
            Combat.AdvanceUntil<EnemySpawned>(sim);
            Combat.Fight(sim).EnemyHp = 1;
            var ev = new List<Event>();
            while (sim.State.Combat.Stage == 10) { sim.Advance(1); ev.AddRange(sim.DrainEvents()); }
            var kill = ev.OfType<EnemyKilled>().Single();
            Assert.That(kill.Gold, Is.EqualTo(Combat.Db.GoldPerKill.Eval(10) * K.BossGoldMult));
            Assert.That(ev.OfType<CurrencyChanged>().Where(c => c.Currency == Currency.GOLD).Single().Reason, Is.EqualTo(CurrencyReason.BOSS));
            Assert.That(ev.OfType<StageFirstCleared>().Single().Diamonds, Is.EqualTo(K.FirstClearDiamonds));
            Assert.That(ev.OfType<CurrencyChanged>().Single(c => c.Currency == Currency.DIAMOND).Reason, Is.EqualTo(CurrencyReason.FIRST_CLEAR));
            Assert.That(ev.OfType<BossCleared>().Single().Stage, Is.EqualTo(10));
            Assert.That(ev.OfType<StageCleared>().Single(), Is.EqualTo(new StageCleared(10, 11, true) { Tick = kill.Tick }));
            Assert.That(sim.State.Progress.HighestFirstClearBoss, Is.EqualTo(10));
            Assert.That(sim.State.Rng[RngStreams.Loot].Counter, Is.EqualTo(1));

            // a second clear of the same boss stage (replayed by hand) pays no first-clear diamonds
            Combat.GoTo(sim, 10, kills: K.KillsPerStage - 1, boss: BossPhase.Pending);
            Combat.AdvanceUntil<EnemySpawned>(sim);
            Combat.Fight(sim).EnemyHp = 1;
            ev.Clear();
            while (sim.State.Combat.Stage == 10) { sim.Advance(1); ev.AddRange(sim.DrainEvents()); }
            Assert.That(ev.OfType<StageFirstCleared>(), Is.Empty);
            Assert.That(sim.State.Rng[RngStreams.Loot].Counter, Is.EqualTo(sim.State.Stats.BossKills));
        }

        /// <summary>Kills the current enemy on its first fighting tick by queueing a huge missile.</summary>
        static void KillNext(Simulation sim, List<Event> into)
        {
            long kills = sim.State.Stats.Kills;
            while (sim.State.Stats.Kills == kills)
            {
                sim.State.Combat.MissileQueue.Add(1e300);
                sim.Advance(1);
                into.AddRange(sim.DrainEvents());
            }
        }

        [Test]
        public void ChapterEntered_OnTheFirstStageOfAChapter()
        {
            var sim = Combat.NewSim(hits: false);
            Combat.GoTo(sim, K.ChapterWidth, kills: K.KillsPerStage - 1, boss: BossPhase.Pending);
            var ev = new List<Event>();
            KillNext(sim, ev);
            Assert.That(ev.OfType<ChapterEntered>().Single().Chapter, Is.EqualTo(2));
            for (int i = 0; i < K.KillsPerStage; i++) KillNext(sim, ev);
            Assert.That(sim.State.Combat.Stage, Is.EqualTo(K.ChapterWidth + 2));
            Assert.That(ev.OfType<ChapterEntered>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void StageCap_IsReachedOnce_AndTheStageLoops()   // issue #81's decision
        {
            var sim = Combat.NewSim(hits: false);
            Combat.GoTo(sim, K.StageCap, kills: 0);
            var ev = new List<Event>();
            for (int i = 0; i < 3 * K.KillsPerStage; i++) KillNext(sim, ev);
            Assert.That(ev.OfType<StageCapReached>().Count(), Is.EqualTo(1));
            Assert.That(ev.OfType<StageCleared>(), Is.Empty);
            Assert.That(sim.State.Combat.Stage, Is.EqualTo(K.StageCap));
            Assert.That(sim.State.Progress.StageCapReached, Is.GreaterThan(0));
            Assert.That(sim.State.Progress.HighestCleared, Is.EqualTo(K.StageCap));
        }

        [Test]
        public void AC7_TapZap_CapsPerSecond_AndEachTapIsHalfAHit()
        {
            var sim = Combat.NewSim(hits: false);
            Combat.AdvanceUntil<EnemySpawned>(sim);
            var f = Combat.Fight(sim);
            f.EnemyHp = f.EnemyMaxHp = 1e9;
            Assert.That(sim.Apply(new TapZap(20)).Ok, Is.True);
            Assert.That((sim.LastTapZapUsed, sim.LastTapZapIgnored), Is.EqualTo((K.TapZapMaxPerSec, 20 - K.TapZapMaxPerSec)));
            Assert.That(sim.Apply(new TapZap(3)).Ok, Is.True);
            Assert.That((sim.LastTapZapUsed, sim.LastTapZapIgnored), Is.EqualTo((0, 3)), "same one-second window");
            long crit = sim.State.Rng[RngStreams.Crit].Counter;
            double before = f.EnemyHp;
            sim.Advance(1);
            var tz = sim.DrainEvents().OfType<TapZapped>().Single();
            Assert.That(tz.Count, Is.EqualTo(K.TapZapMaxPerSec));
            Assert.That(tz.Damage, Is.EqualTo(sim.Query.HeroHit * K.TapZapDmgPct * K.TapZapMaxPerSec));
            Assert.That(before - f.EnemyHp, Is.EqualTo(tz.Damage).Within(1e-6), "no hero hit on this tick");
            Assert.That(sim.State.Rng[RngStreams.Crit].Counter, Is.EqualTo(crit), "taps draw nothing");

            sim.Advance(K.TickHz);
            sim.Apply(new TapZap(20));
            Assert.That(sim.LastTapZapUsed, Is.EqualTo(K.TapZapMaxPerSec), "the next second accepts the cap again");
            Assert.That(sim.State.Stats.Taps, Is.EqualTo(2 * K.TapZapMaxPerSec));
            var bad = sim.Apply(new TapZap(-1));
            Assert.That((bad.Code, bad.Reason), Is.EqualTo((RejectCode.BadArgument, CombatRejectReasons.INVALID_COUNT)));
        }

        [Test]
        public void AC8_Missiles_QueuedWhileSpawning_ApplyInOrder_NoCrit_SurviveASave_SurplusDiscarded()
        {
            var sim = Combat.NewSim(hits: false);
            Assert.That(Combat.Fight(sim), Is.Null);
            sim.State.Combat.MissileQueue.AddRange(new[] { 0.05, 0.075 });

            // a save round trip while queued: the queue persists, the fight does not
            var loaded = SaveSerializer.Materialize(SaveSerializer.ToJson(sim.State));
            Assert.That(loaded.Combat.MissileQueue, Is.EqualTo(new[] { 0.05, 0.075 }));
            var sim2 = new Simulation(loaded, TestContent.Db);

            foreach (var s in new[] { sim, sim2 })
            {
                var spawn = Combat.AdvanceUntil<EnemySpawned>(s);
                Assert.That(s.State.Combat.MissileQueue.Count, Is.EqualTo(2), "not drained while spawning");
                long crit = s.State.Rng[RngStreams.Crit].Counter;
                s.Advance(1);
                var ev = s.DrainEvents();
                var hits = ev.OfType<FishMissileHit>().ToList();
                Assert.That(hits.Select(h => h.Fraction), Is.EqualTo(new[] { 0.05, 0.075 }));
                Assert.That(hits.All(h => h.Tick == spawn.Tick + 1), Is.True, "first Fighting tick");
                Assert.That(hits[0].Damage, Is.EqualTo(s.Query.HeroHit * 0.05));
                Assert.That(s.State.Combat.MissileQueue, Is.Empty);
                Assert.That(s.State.Rng[RngStreams.Crit].Counter, Is.EqualTo(crit));
            }

            // a missile far bigger than the enemy kills it; the next enemy spawns at full HP
            sim.State.Combat.MissileQueue.Add(1e6);
            var kill = Combat.AdvanceUntil<EnemyKilled>(sim, 20);
            var next = Combat.AdvanceUntil<EnemySpawned>(sim, 20);
            sim.Advance(1);
            Assert.That(Combat.Fight(sim).EnemyHp, Is.EqualTo(next.MaxHp), "surplus is discarded");
            Assert.That(kill, Is.Not.Null);
        }

        [Test]
        public void Missiles_AreNotDrained_InDungeonMode()   // 03 section 2.5, issue #40
        {
            var sim = Combat.NewSim(hits: false);
            Combat.AdvanceUntil<EnemySpawned>(sim);
            sim.State.Combat.Mode = CombatMode.Dungeon;
            sim.State.Combat.MissileQueue.Add(0.05);
            sim.Advance(50);
            Assert.That(sim.State.Combat.MissileQueue.Count, Is.EqualTo(1));
            sim.State.Combat.Mode = CombatMode.Stage;
            sim.Advance(1);
            Assert.That(sim.State.Combat.MissileQueue, Is.Empty);
        }

        static Simulation Scripted(Guid root)
        {
            var state = Runtime.Profile.ProfileFactory.Create(TestContent.Db, Guid.Empty, TestContent.T0,
                                                               new Guid("de71ce00-0000-4000-8000-000000000001"), root);
            var sim = new Simulation(state, TestContent.Db) { ForceCoalesce = true };
            sim.Apply(new ApplyServerTime(TestContent.T0));
            Combat.Own(state.Skills, "skill_e_02", 3);
            Combat.Own(state.Skills, "skill_c_09", 1);
            Combat.Own(state.Skills, "skill_c_13", 1);
            state.Loadout.Skills[0] = "skill_e_02";
            state.Loadout.Skills[1] = "skill_c_09";
            state.Loadout.Skills[2] = "skill_c_13";
            sim.Query.MarkDirty();
            return sim;
        }

        /// <summary>A scripted 1,000,000-tick run: training, taps, missiles and boss retries; returns an event digest.</summary>
        static string Script(Simulation sim, int ticks)
        {
            var rng = new Random(7);
            var digest = new System.Text.StringBuilder();
            for (int done = 0; done < ticks; done += 1000)
            {
                sim.Advance(1000);
                var stat = new[] { Stat.ATK, Stat.CRIT_RATE, Stat.CRIT_DMG, Stat.ATK_SPD, Stat.GOLD_GAIN, Stat.EXP_GAIN }[rng.Next(6)];
                sim.Apply(new TrainStatMax(stat));
                sim.Apply(new TapZap(rng.Next(0, 10)));
                if (rng.Next(4) == 0) sim.State.Combat.MissileQueue.Add(0.05 * rng.Next(1, 5));
                if (sim.State.Combat.Boss == BossPhase.Walled) sim.Apply(new AttemptBoss());
                foreach (var e in sim.DrainEvents()) digest.Append(e.GetType().Name).Append(e.Tick).Append(';');
                if (digest.Length > 1_000_000) { string h = digest.ToString().GetHashCode().ToString(); digest.Clear().Append(h); }
            }
            return digest.ToString();
        }

        [Test]
        public void AC5_AC15_MillionTicks_InvariantsHold_AndTwoRunsAreIdentical()
        {
            var a = Scripted(TestContent.Root);
            var b = Scripted(TestContent.Root);
            string ea = Script(a, 1_000_000), eb = Script(b, 1_000_000);
            Assert.That(eb, Is.EqualTo(ea));
            Assert.That(b.StateHash(), Is.EqualTo(a.StateHash()));

            var s = a.State;
            Assert.That(s.Combat.Stage, Is.GreaterThan(20), "the scripted player progresses");
            Assert.That(s.Stats.HeroHits, Is.GreaterThan(0));
            Assert.That(s.Rng[RngStreams.Crit].Counter, Is.EqualTo(s.Stats.HeroHits), "AC5: skills, missiles and taps draw no crit");
            Assert.That(s.Rng[RngStreams.Loot].Counter, Is.EqualTo(s.Stats.BossKills));
            Assert.That(s.Rng[RngStreams.Combat].Counter, Is.EqualTo(0));
        }

        [Test]
        public void AC15_ADeepCloneMidBoss_ContinuesIdentically()
        {
            var sim = Scripted(TestContent.Root);
            Combat.GoTo(sim, 10, kills: K.KillsPerStage - 1, boss: BossPhase.Pending);
            Combat.AdvanceUntil<EnemySpawned>(sim);
            sim.Advance(37);
            sim.DrainEvents();
            Assert.That(Combat.Fight(sim).EnemyIsBoss, Is.True);
            var clone = new Simulation(StateCopy.DeepClone(sim.State), TestContent.Db) { EmitHitLanded = true, ForceCoalesce = true };
            sim.EmitHitLanded = true;
            Assert.That(clone.StateHash(), Is.EqualTo(sim.StateHash()));
            for (int i = 0; i < 50; i++)
            {
                sim.Advance(997);
                clone.Advance(997);
                Assert.That(string.Join("|", clone.DrainEvents()), Is.EqualTo(string.Join("|", sim.DrainEvents())));
            }
            Assert.That(clone.StateHash(), Is.EqualTo(sim.StateHash()));
        }

        [Test]
        public void Replay_OfACombatCommandLog_ReproducesTheStateHash()
        {
            var log = new Replay.ListCommandLog();
            var sim = new Simulation(TestContent.NewState(), TestContent.Db) { CommandLog = log };
            sim.Apply(new ApplyServerTime(TestContent.T0));
            for (int i = 0; i < 200; i++)
            {
                sim.Advance(600);
                sim.Apply(new TrainStatMax(i % 2 == 0 ? Stat.ATK : Stat.ATK_SPD));
                sim.Apply(new TapZap(i % 7));
                sim.Apply(new AttemptBoss());
            }
            var replay = new Simulation(TestContent.NewState(), TestContent.Db);
            replay.Replay(log.Records, sim.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(sim.StateHash()));
        }

        [Test]
        public void Snapshot_CarriesRatedDps_AndTheLastTicksDamageNumbers()
        {
            var sim = Combat.NewSim(hits: false);
            var spawn = Combat.AdvanceUntil<EnemySpawned>(sim);
            Combat.Fight(sim).EnemyHp = 1e9;
            sim.Advance(K.AttackIntervalTicks);   // the first hit lands on this Advance's last tick
            var snap = sim.GetSnapshot();
            Assert.That(snap.HeroDps, Is.EqualTo(sim.Query.RatedDps));
            Assert.That(snap.HeroDps, Is.GreaterThan(0));
            Assert.That(snap.DamageNumbersThisTick.Length, Is.EqualTo(1));
            Assert.That(snap.DamageNumbersThisTick[0].Amount, Is.EqualTo(sim.Query.HeroHit));
            sim.Advance(1);
            Assert.That(sim.GetSnapshot().DamageNumbersThisTick.Length, Is.EqualTo(0));
            Assert.That(spawn.Stage, Is.EqualTo(1));
        }
    }
}
