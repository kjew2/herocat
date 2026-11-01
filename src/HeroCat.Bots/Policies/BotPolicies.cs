using System;

namespace HeroCat.Bots.Policies
{
    /// <summary>
    /// CLI bot ids -> policies (10 section 3.4). TODO(#214, #215): lazy-f2p, light-spender and whale (they need the MVP
    /// daily gift, quests, dungeons and the IAP -> inbox path).
    /// </summary>
    public static class BotPolicies
    {
        public static readonly string[] Names = { "greedy-f2p" };

        public static IBotPolicy Create(string name)
        {
            switch (name)
            {
                case "greedy-f2p": return new GreedyF2PBot();
                default: throw new ArgumentException($"unknown bot '{name}' (expected {string.Join("|", Names)})");
            }
        }
    }
}
