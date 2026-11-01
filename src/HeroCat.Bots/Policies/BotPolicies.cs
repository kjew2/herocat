using System;

namespace HeroCat.Bots.Policies
{
    /// <summary>
    /// The M0 skeleton's only policy: let time pass. Advance runs, nothing is spent. TODO(#135): greedy-f2p for the M0
    /// systems; TODO(#214), #215: lazy-f2p, light-spender and whale.
    /// </summary>
    public sealed class IdleBot : IBotPolicy
    {
        public string Name => "idle";
        public void OnDayStart(BotContext ctx) { }
        public void Decide(BotContext ctx) { }
    }

    /// <summary>CLI bot ids -> policies.</summary>
    public static class BotPolicies
    {
        public static readonly string[] Names = { "idle" };

        public static IBotPolicy Create(string name)
        {
            switch (name)
            {
                case "idle": return new IdleBot();
                default: throw new ArgumentException($"unknown bot '{name}' (expected {string.Join("|", Names)})");
            }
        }
    }
}
