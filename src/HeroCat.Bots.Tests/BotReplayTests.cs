using System.Linq;
using HeroCat.Bots.Golden;
using HeroCat.Bots.Policies;
using HeroCat.Runtime.Replay;
using NUnit.Framework;

namespace HeroCat.Bots.Tests
{
    /// <summary>
    /// 10 section 2: a bot run's JSONL command log replays on its genesis (BotRunner.Genesis, the harness epoch) to the same
    /// StateHash after each day's first catch-up and at the end. The runner drives the sim through SessionRunner; the replay
    /// uses only Simulation.Apply and Advance, so this also proves that nothing in the session path mutates state outside
    /// Apply (00 contract 5).
    /// </summary>
    public class BotReplayTests
    {
        [TestCase(7UL, 3)]
        [TestCase(11UL, 2)]
        public void BotRun_ReplaysToSameStateHash_EveryDay(ulong seed, int days)
        {
            var log = new MemoryJsonlCommandLog();
            var run = new BotRunner(Content.Db) { CommandLog = log }.Run(new GreedyF2PBot(), seed, days);
            Assert.That(log.Lines.Count, Is.GreaterThan(4 * days), "system commands plus the policy's");

            string expected = GoldenFiles.HashesText(run.DayHashes, run.FinalTick, run.FinalHash);
            string actual = seed == GoldenFiles.Seed
                ? GoldenFiles.Replay(Content.Db, JsonlCommandLog.Parse(log.Text), run.FinalTick)
                : ReplayOther(seed, log, run.FinalTick);
            Assert.That(actual, Is.EqualTo(expected));
        }

        static string ReplayOther(ulong seed, MemoryJsonlCommandLog log, long finalTick)
        {
            var sim = BotRunner.NewSimulation(Content.Db, seed);
            var cmds = JsonlCommandLog.Parse(log.Text);
            var days = new System.Collections.Generic.List<DayHash>();
            int catchups = 0;
            foreach (var c in cmds)
            {
                sim.AdvanceTicks(c.IssuedAtTick - sim.Tick);
                sim.Apply(c);
                if (c is HeroCat.Sim.Commands.ComputeOfflineCatchup && ++catchups % 2 == 1) days.Add(new DayHash(days.Count + 1, sim.Tick, sim.StateHash()));
            }
            sim.AdvanceTicks(finalTick - sim.Tick);
            return GoldenFiles.HashesText(days, sim.Tick, sim.StateHash());
        }

        [Test]
        public void Replay_DetectsATamperedLog()
        {
            var log = new MemoryJsonlCommandLog();
            var run = new BotRunner(Content.Db) { CommandLog = log }.Run(new GreedyF2PBot(), GoldenFiles.Seed, 1);
            var cmds = JsonlCommandLog.Parse(log.Text).Where(c => !(c is HeroCat.Sim.Commands.TrainStat)).ToList();
            string actual = GoldenFiles.Replay(Content.Db, cmds, run.FinalTick);
            Assert.That(actual, Is.Not.EqualTo(GoldenFiles.HashesText(run.DayHashes, run.FinalTick, run.FinalHash)));
        }
    }
}
