using System.Collections.Generic;
using System.Linq;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>
    /// SelectStage: side-stage farming (03 section 2.7 and acceptance 22, 00 decision 67, 01 section 2.1; issues #35, #159).
    /// </summary>
    public class StageSelectTests
    {
        static Constants K => Combat.K;

        /// <summary>A player on frontier <paramref name="stage"/> with every lower stage completed.</summary>
        static Simulation AtFrontier(int stage, int kills = 0, BossPhase boss = BossPhase.None, bool hits = false)
        {
            var sim = Combat.NewSim(hits);
            Combat.GoTo(sim, stage, kills, boss);
            sim.State.Progress.HighestCleared = stage - 1;
            sim.State.Progress.HighestFirstClearBoss = (stage - 1) / K.BossEvery * K.BossEvery;
            return sim;
        }

        static void AssertRejected(Simulation sim, Command cmd, RejectCode code, string reason)
        {
            string before = sim.StateHash();
            var r = sim.Apply(cmd);
            Assert.That((r.Ok, r.Code, r.Reason), Is.EqualTo((false, code, reason)), cmd.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(before), "a reject mutates nothing");
            Assert.That(sim.DrainEvents(), Is.Empty, "a reject emits nothing");
        }

        /// <summary>Kills the current enemy on its first fighting tick by queueing a huge missile.</summary>
        static EnemyKilled KillNext(Simulation sim, List<Event> into = null)
        {
            long kills = sim.State.Stats.Kills;
            while (sim.State.Stats.Kills == kills)
            {
                sim.State.Combat.MissileQueue.Add(1e300);
                sim.Advance(1);
                var ev = sim.DrainEvents();
                into?.AddRange(ev);
                var k = ev.OfType<EnemyKilled>().FirstOrDefault();
                if (k != null) return k;
            }
            Assert.Fail("no EnemyKilled");
            return null;
        }

        [Test]
        public void Rejects_OutOfRange_AsBadArgument_AndUnclearedStages_AsLocked()
        {
            var sim = AtFrontier(57, kills: 4);
            AssertRejected(sim, new SelectStage(58), RejectCode.Locked, CombatRejectReasons.STAGE_NOT_CLEARED);
            AssertRejected(sim, new SelectStage(0), RejectCode.BadArgument, CombatRejectReasons.STAGE_OUT_OF_RANGE);
            AssertRejected(sim, new SelectStage(-3), RejectCode.BadArgument, CombatRejectReasons.STAGE_OUT_OF_RANGE);
            AssertRejected(sim, new SelectStage(int.MinValue), RejectCode.BadArgument, CombatRejectReasons.STAGE_OUT_OF_RANGE);
            AssertRejected(sim, new SelectStage(K.StageCap + 1), RejectCode.BadArgument, CombatRejectReasons.STAGE_OUT_OF_RANGE);
            AssertRejected(sim, new SelectStage(int.MaxValue), RejectCode.BadArgument, CombatRejectReasons.STAGE_OUT_OF_RANGE);

            sim.State.Combat.Mode = CombatMode.Dungeon;   // the stage fields are frozen during a run (02 section 3.3)
            AssertRejected(sim, new SelectStage(10), RejectCode.NotAvailable, CombatRejectReasons.DUNGEON_RUN_IN_PROGRESS);
            AssertRejected(sim, new SelectStage(null), RejectCode.NotAvailable, CombatRejectReasons.DUNGEON_RUN_IN_PROGRESS);
        }

        [Test]
        public void AtTheCap_TheCapStageIsTheFrontier_AndCanBeSelectedBack()
        {
            var sim = AtFrontier(K.StageCap);
            sim.State.Progress.HighestCleared = K.StageCap;   // the cap boss is cleared: the stage loops (issue #81)
            Assert.That(sim.Apply(new SelectStage(K.StageCap - 5)).Ok, Is.True);
            Assert.That(sim.Apply(new SelectStage(K.StageCap)).Ok, Is.True);
            Assert.That(sim.State.Combat.Selected, Is.Null, "selecting the frontier stage equals Return");
        }

        [Test]
        public void AC22_FarmingASideStage_PaysItsRewards_AndReturnResumesTheFrontier()
        {
            var sim = AtFrontier(57, kills: 4);
            Combat.SetLevel(sim, Stat.GOLD_GAIN, 5);
            Combat.SetLevel(sim, Stat.EXP_GAIN, 3);
            Combat.AdvanceUntil<EnemySpawned>(sim);
            double gold = sim.State.Wallet.Gold, exp = sim.State.Hero.Exp;
            long kills = sim.State.Stats.Kills;

            var r = sim.Apply(new SelectStage(56));
            Assert.That(r.Ok, Is.True);
            Assert.That(sim.DrainEvents().Single(), Is.EqualTo(new StageChanged(56) { Tick = sim.Tick }));
            var c = sim.State.Combat;
            Assert.That(c.Selected, Is.EqualTo(56));
            Assert.That(Combat.Fight(sim).Phase, Is.EqualTo(FightPhase.Spawning), "the frontier mob despawns");
            Assert.That((sim.State.Wallet.Gold, sim.State.Hero.Exp, sim.State.Stats.Kills), Is.EqualTo((gold, exp, kills)), "despawn pays nothing");

            var spawn = Combat.AdvanceUntil<EnemySpawned>(sim);
            Assert.That((spawn.Stage, spawn.IsBoss, spawn.MaxHp), Is.EqualTo((56, false, sim.Query.EnemyHp(56))));

            // more than a stage's worth of kills: each pays stage 56, none advances anything or spawns a boss
            for (int i = 0; i < K.KillsPerStage + 3; i++)
            {
                var ev = new List<Event>();
                var k = KillNext(sim, ev);
                Assert.That((k.Stage, k.IsBoss), Is.EqualTo((56, false)));
                Assert.That(k.Gold, Is.EqualTo(Combat.Db.GoldPerKill.Eval(56) * sim.Query.GoldMult));
                Assert.That(k.Exp, Is.EqualTo(Combat.Db.ExpPerKill.Eval(56) * sim.Query.ExpMult));
                Assert.That(ev.OfType<StageCleared>(), Is.Empty);
                Assert.That((c.Stage, c.KillsInStage, c.Boss, c.BossFailsOnStage, sim.State.Progress.HighestCleared),
                            Is.EqualTo((57, 4, BossPhase.None, 0, 56)));
                Assert.That(Combat.AdvanceUntil<EnemySpawned>(sim).IsBoss, Is.False);
            }
            Assert.That(sim.State.Stats.Kills, Is.EqualTo(kills + K.KillsPerStage + 3), "side-stage kills count for Stats.Kills");
            Assert.That(sim.State.Progress.HighestCleared, Is.EqualTo(56), "farming never lowers the Awakening gate (03 section 5.3)");

            Assert.That(sim.Apply(new SelectStage(56)).Ok, Is.True);
            Assert.That(sim.DrainEvents(), Is.Empty, "re-selecting the fought stage is a no-op");
            Assert.That(Combat.Fight(sim).Phase, Is.EqualTo(FightPhase.Fighting), "and does not despawn");

            Assert.That(sim.Apply(new SelectStage(null)).Ok, Is.True);
            Assert.That(sim.DrainEvents().Single(), Is.EqualTo(new StageChanged(57) { Tick = sim.Tick }));
            Assert.That(c.Selected, Is.Null);
            Assert.That(Combat.AdvanceUntil<EnemySpawned>(sim).Stage, Is.EqualTo(57));
            var back = KillNext(sim);
            Assert.That(back.Gold, Is.EqualTo(Combat.Db.GoldPerKill.Eval(57) * sim.Query.GoldMult));
            Assert.That(c.KillsInStage, Is.EqualTo(5), "the frontier resumes at 4 kills");

            Assert.That(sim.Apply(new SelectStage(null)).Ok, Is.True);
            Assert.That(sim.DrainEvents(), Is.Empty, "returning while on the frontier is a no-op");
        }

        [Test]
        public void SelectingTheFrontierStage_EqualsReturn()
        {
            var sim = AtFrontier(30, kills: 2);
            Assert.That(sim.Apply(new SelectStage(12)).Ok, Is.True);
            sim.DrainEvents();
            Assert.That(sim.Apply(new SelectStage(30)).Ok, Is.True);
            Assert.That(sim.State.Combat.Selected, Is.Null);
            Assert.That(sim.DrainEvents().Single(), Is.EqualTo(new StageChanged(30) { Tick = sim.Tick }));
        }

        [Test]
        public void AC22_AWalledFrontier_SurvivesSelectAndReturn_AndAttemptBossReturnsToTheFrontierBoss()
        {
            var sim = AtFrontier(60, kills: K.KillsPerStage - 1, boss: BossPhase.Walled);
            sim.State.Combat.BossFailsOnStage = 2;
            var c = sim.State.Combat;

            Assert.That(sim.Apply(new SelectStage(55)).Ok, Is.True);
            KillNext(sim);
            Assert.That((c.Stage, c.KillsInStage, c.Boss, c.BossFailsOnStage), Is.EqualTo((60, K.KillsPerStage - 1, BossPhase.Walled, 2)));
            Assert.That(sim.Apply(new SelectStage(null)).Ok, Is.True);
            Assert.That((c.Stage, c.KillsInStage, c.Boss, c.BossFailsOnStage), Is.EqualTo((60, K.KillsPerStage - 1, BossPhase.Walled, 2)));
            Assert.That(Combat.AdvanceUntil<EnemySpawned>(sim).IsBoss, Is.False, "still walled: farming mobs");

            Assert.That(sim.Apply(new SelectStage(41)).Ok, Is.True);
            Combat.AdvanceUntil<EnemySpawned>(sim);
            sim.DrainEvents();
            Assert.That(sim.Apply(new AttemptBoss()).Ok, Is.True);
            Assert.That(c.Selected, Is.Null);
            Assert.That(sim.DrainEvents().Single(), Is.EqualTo(new StageChanged(60) { Tick = sim.Tick }));
            var boss = Combat.AdvanceUntil<EnemySpawned>(sim);
            Assert.That((boss.Stage, boss.IsBoss, boss.MaxHp), Is.EqualTo((60, true, K.BossHpMult * sim.Query.EnemyHp(60))));
        }

        [Test]
        public void APendingFrontierBoss_NeverSpawnsOnASideStage_AndWaitsForTheReturn()
        {
            var sim = AtFrontier(20, kills: K.KillsPerStage - 1, boss: BossPhase.Pending);
            Assert.That(sim.Apply(new SelectStage(7)).Ok, Is.True);
            for (int i = 0; i < 3; i++)
            {
                var s = Combat.AdvanceUntil<EnemySpawned>(sim);
                Assert.That((s.Stage, s.IsBoss), Is.EqualTo((7, false)));
                var k = KillNext(sim);
                Assert.That(k.IsBoss, Is.False);
            }
            Assert.That(sim.State.Combat.Boss, Is.EqualTo(BossPhase.Pending));
            Assert.That(sim.State.Progress.HighestFirstClearBoss, Is.EqualTo(10), "no first clear from a side stage");
            Assert.That(sim.Apply(new SelectStage(null)).Ok, Is.True);
            var boss = Combat.AdvanceUntil<EnemySpawned>(sim);
            Assert.That((boss.Stage, boss.IsBoss), Is.EqualTo((20, true)));
        }

        [Test]
        public void CoalescedSideStageKills_BatchUnderTheFoughtStage()
        {
            var sim = AtFrontier(57, kills: 4);
            sim.ForceCoalesce = true;
            Assert.That(sim.Apply(new SelectStage(33)).Ok, Is.True);
            sim.DrainEvents();
            var ev = new List<Event>();
            long kills = sim.State.Stats.Kills;
            while (sim.State.Stats.Kills < kills + 3)
            {
                sim.State.Combat.MissileQueue.Add(1e300);
                sim.Advance(1);
                ev.AddRange(sim.DrainEvents());
            }
            var batches = ev.OfType<EnemyKilledBatch>().ToList();
            Assert.That(batches.Sum(b => b.Count), Is.EqualTo(3));
            Assert.That(batches.All(b => b.FromStage == 33 && b.ToStage == 33), Is.True);
        }

        /// <summary>A scripted select / farm / return run issued through Apply only.</summary>
        static string Script(Simulation sim)
        {
            var digest = new System.Text.StringBuilder();
            int[] targets = { 3, 9, 0, 14, 0, 5, 9, 0 };   // 0 = return to the frontier
            for (int round = 0; round < 40; round++)
            {
                sim.Advance(1500);
                sim.Apply(new TrainStatMax(round % 2 == 0 ? Stat.ATK : Stat.ATK_SPD));
                int t = targets[round % targets.Length];
                int highest = sim.State.Progress.HighestCleared;
                sim.Apply(new SelectStage(t == 0 ? (int?)null : (t <= highest ? t : highest > 0 ? highest : (int?)null)));
                sim.Apply(new SelectStage(highest + 2));   // rejected: never cleared
                if (sim.State.Combat.Boss == BossPhase.Walled && round % 3 == 0) sim.Apply(new AttemptBoss());
                foreach (var e in sim.DrainEvents()) digest.Append(e).Append(';');
            }
            return digest.ToString();
        }

        [Test]
        public void SelectFarmReturn_IsDeterministic_SurvivesDeepClone_AndReplays()
        {
            var log = new Replay.ListCommandLog();
            var a = new Simulation(TestContent.NewState(), TestContent.Db) { CommandLog = log };
            var b = new Simulation(TestContent.NewState(), TestContent.Db);
            a.Apply(new ApplyServerTime(TestContent.T0));
            b.Apply(new ApplyServerTime(TestContent.T0));
            string ea = Script(a), eb = Script(b);
            Assert.That(eb, Is.EqualTo(ea));
            Assert.That(b.StateHash(), Is.EqualTo(a.StateHash()));
            Assert.That(ea, Does.Contain("StageChanged"), "the script selects and returns");
            Assert.That(a.State.Progress.HighestCleared, Is.GreaterThan(9), "the script progresses past its side stages");

            var replay = new Simulation(TestContent.NewState(), TestContent.Db);
            replay.Replay(log.Records, a.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(a.StateHash()));

            // a DeepClone while farming a side stage continues identically
            int side = a.State.Progress.HighestCleared - 1;
            Assert.That(a.Apply(new SelectStage(side)).Ok, Is.True);
            a.Advance(333);
            a.DrainEvents();
            Assert.That(a.State.Combat.Selected, Is.EqualTo(side));
            var clone = new Simulation(StateCopy.DeepClone(a.State), TestContent.Db);
            Assert.That(clone.StateHash(), Is.EqualTo(a.StateHash()));
            for (int i = 0; i < 10; i++)
            {
                a.Advance(997);
                clone.Advance(997);
                Assert.That(string.Join("|", clone.DrainEvents()), Is.EqualTo(string.Join("|", a.DrainEvents())));
            }
            Assert.That(a.Apply(new SelectStage(null)).Ok, Is.True);
            Assert.That(clone.Apply(new SelectStage(null)).Ok, Is.True);
            clone.Advance(5000);
            a.Advance(5000);
            Assert.That(clone.StateHash(), Is.EqualTo(a.StateHash()));
        }
    }
}
