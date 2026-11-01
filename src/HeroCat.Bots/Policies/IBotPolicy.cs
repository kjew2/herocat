using HeroCat.Runtime.Services;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Bots.Policies
{
    public sealed class BotContext
    {
        public BotContext(Simulation sim, FakeClock clock)
        {
            Sim = sim;
            Clock = clock;
        }

        public Simulation Sim { get; }
        public GameState State => Sim.State;
        public ContentDb Content => Sim.Db;
        public FakeClock Clock { get; }
        public int Day { get; set; }

        /// <summary>Apply through the one door, like every other caller (00 contract 5).</summary>
        public ApplyResult Apply(Command c) => Sim.Apply(c);
    }

    public interface IBotPolicy
    {
        /// <summary>CLI id and golden-file component.</summary>
        string Name { get; }

        void OnDayStart(BotContext ctx);

        /// <summary>Called once per DecisionIntervalSeconds of active play.</summary>
        void Decide(BotContext ctx);
    }
}
