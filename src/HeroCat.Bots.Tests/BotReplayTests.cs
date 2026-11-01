using System;
using System.Collections.Generic;
using System.Diagnostics;
using HeroCat.Bots.Policies;
using HeroCat.Sim;
using NUnit.Framework;

namespace HeroCat.Bots.Tests
{
    /// <summary>specs/10 section 4.4: a bot run's command log replays on its genesis to the same StateHash.</summary>
    public class BotReplayTests
    {
        [TestCase("greedy-f2p", 7UL, 3)]
        [TestCase("whale", 11UL, 2)]
        public void BotRun_ReplaysToSameStateHash_EveryDay(string bot, ulong seed, int days)
        {
            var log = new ListCommandLog();
            var dayEnds = new List<(int count, long tick, string hash)>();
            var runner = new BotRunner(Content.Db)
            {
                CommandLog = log,
                AfterDay = (d, ctx) => dayEnds.Add((log.Count, ctx.State.Tick, ctx.Sim.StateHash())),
            };
            runner.Run(BotPolicies.Create(bot), seed, days);
            Assert.That(log.Count, Is.GreaterThan(100));

            var sim = new Simulation(Content.Db, BotRunner.Genesis(Content.Db, seed));
            var records = log.ToArray();
            int from = 0;
            foreach (var (count, tick, hash) in dayEnds)
            {
                sim.Replay(new ReadOnlySpan<CommandRecord>(records, from, count - from), null, tick);
                Assert.That(sim.StateHash(), Is.EqualTo(hash));
                from = count;
            }
        }

        [Test, Explicit("timing; run by hand")]
        public void NullLog_NoMeasurableSlowdown()
        {
            new BotRunner(Content.Db).Run(new GreedyF2PBot(), 7, 3);   // warm up
            var sw = Stopwatch.StartNew();
            new BotRunner(Content.Db).Run(new GreedyF2PBot(), 7, 30);
            long nullMs = sw.ElapsedMilliseconds;
            sw.Restart();
            new BotRunner(Content.Db) { CommandLog = new ListCommandLog() }.Run(new GreedyF2PBot(), 7, 30);
            TestContext.Progress.WriteLine($"null log {nullMs} ms, list log {sw.ElapsedMilliseconds} ms");
        }
    }
}
