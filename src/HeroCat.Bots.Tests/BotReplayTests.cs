using System.Collections.Generic;
using System.Linq;
using HeroCat.Bots.Policies;
using HeroCat.Sim;
using HeroCat.Sim.Replay;
using NUnit.Framework;

namespace HeroCat.Bots.Tests
{
    /// <summary>10 section 2: a bot run's command log replays on its genesis to the same StateHash, day by day.</summary>
    public class BotReplayTests
    {
        [TestCase(7UL, 3)]
        [TestCase(11UL, 2)]
        public void BotRun_ReplaysToSameStateHash_EveryDay(ulong seed, int days)
        {
            var log = new ListCommandLog();
            var dayEnds = new List<(int count, long tick, string hash)>();
            var runner = new BotRunner(Content.Db)
            {
                CommandLog = log,
                AfterDay = (d, ctx) => dayEnds.Add((log.Count, ctx.Sim.Tick, ctx.Sim.StateHash())),
            };
            runner.Run(new IdleBot(), seed, days);
            Assert.That(log.Count, Is.EqualTo(1 + 3 * days), "the start sync plus two system commands and the claim per night");

            var sim = new Simulation(BotRunner.Genesis(Content.Db, seed), Content.Db);
            var records = log.Records.ToArray();
            int from = 0;
            foreach (var (count, tick, hash) in dayEnds)
            {
                sim.Replay(records.Skip(from).Take(count - from).ToList(), tick);
                Assert.That(sim.StateHash(), Is.EqualTo(hash));
                from = count;
            }
        }
    }
}
