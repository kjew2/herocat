using System;

namespace HeroCat.Bots.Policies
{
    /// <summary>CLI bot ids -> policies.</summary>
    public static class BotPolicies
    {
        public static readonly string[] Names = { "greedy-f2p", "whale" };

        public static IBotPolicy Create(string name)
        {
            switch (name)
            {
                case "greedy-f2p": return new GreedyF2PBot();
                case "whale": return new WhaleBot();
                default: throw new ArgumentException($"unknown bot '{name}' (expected {string.Join("|", Names)})");
            }
        }
    }
}
