using System;
using HeroCat.Bots.Measure;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using HeroCat.Sim.Time;

namespace HeroCat.Bots.Policies
{
    public sealed class BotContext
    {
        public BotContext(Simulation sim, FakeClock clock, BotEventSink stats)
        {
            Sim = sim;
            Clock = clock;
            Stats = stats;
        }

        public Simulation Sim { get; }
        public GameState State => Sim.State;
        public ContentDb Content => Sim.Content;
        public FakeClock Clock { get; }
        public BotEventSink Stats { get; }
        public int Day { get; set; }
    }

    public interface IBotPolicy
    {
        /// <summary>CLI id and golden-file component: "greedy-f2p", "whale".</summary>
        string Name { get; }

        void OnDayStart(BotContext ctx);

        /// <summary>Called once per DecisionIntervalSeconds of active play.</summary>
        void Decide(BotContext ctx);
    }
}
