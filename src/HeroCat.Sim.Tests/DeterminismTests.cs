using System.Collections.Generic;
using System.Linq;
using HeroCat.Sim.Events;
using HeroCat.Sim.Time;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    public class DeterminismTests
    {
        static (string state, List<string> events) Run(ulong seed)
        {
            var log = new List<string>();
            var sim = TestContent.NewSim(seed, new CallbackEventSink(e => log.Add(e.ToString())));
            sim.ApplyTutorialGrants();
            var clock = new FakeClock(TestContent.T0);
            for (int d = 0; d < 2; d++) GreedyBot.PlayDay(sim, clock, activeMinutes: 60);
            sim.Validate();
            return (TestContent.Dump(sim.State), log);
        }

        [Test]
        public void SameSeed_SameStateAndEvents()
        {
            var a = Run(7);
            var b = Run(7);
            Assert.That(a.state, Is.EqualTo(b.state));
            Assert.That(a.events, Is.EqualTo(b.events));
            Assert.That(Run(8).state, Is.Not.EqualTo(a.state));
        }

        [Test]
        public void DeepClone_MidFight_ContinuesIdentically()
        {
            var sim = TestContent.NewSim();
            sim.ApplyTutorialGrants();
            sim.Advance(12_345);
            GreedyBot.Spend(sim);
            sim.Advance(17);   // mid-fight
            var clone = new Simulation(TestContent.Db, sim.State.DeepClone());
            Assert.That(TestContent.Dump(clone.State), Is.EqualTo(TestContent.Dump(sim.State)));
            for (int i = 0; i < 20; i++)
            {
                sim.Advance(6000); GreedyBot.Spend(sim);
                clone.Advance(6000); GreedyBot.Spend(clone);
            }
            Assert.That(TestContent.Dump(clone.State), Is.EqualTo(TestContent.Dump(sim.State)));
        }
    }
}
