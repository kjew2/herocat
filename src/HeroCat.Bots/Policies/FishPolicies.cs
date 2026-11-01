using System;
using HeroCat.Sim.Commands;

namespace HeroCat.Bots.Policies
{
    /// <summary>05 section 8.2's three Fish Board players (issue #105).</summary>
    public enum FishPolicy
    {
        /// <summary>ReleaseMax + MergeAll every active minute; discards the lowest fish when jammed below the ceiling.</summary>
        Attentive,
        /// <summary>ReleaseMax + MergeAll once per login (session start), never discards.</summary>
        Lazy,
        /// <summary>Never touches the board.</summary>
        Never,
    }

    /// <summary>
    /// The Fish Board policy steps a bot calls (05 section 8.2, 03 section 8.3 step 5). greedy-f2p plays
    /// <see cref="FishPolicy.Attentive"/> (<see cref="GreedyF2PBot.PlayFishBoard"/>); lazy-f2p (TODO(#214)) plays
    /// <see cref="FishPolicy.Lazy"/>. All through the bulk commands, so a policy is a few logged commands per visit.
    /// </summary>
    public static class FishPolicies
    {
        public static readonly string[] Names = { "attentive", "lazy", "never" };

        public static FishPolicy Parse(string name)
        {
            switch (name)
            {
                case "attentive": return FishPolicy.Attentive;
                case "lazy": return FishPolicy.Lazy;
                case "never": return FishPolicy.Never;
                default: throw new ArgumentException($"unknown fish policy '{name}' (expected {string.Join("|", Names)})");
            }
        }

        public static string Name(FishPolicy p) => Names[(int)p];

        /// <summary>After the session's resume and offline claim (the login).</summary>
        public static void OnSessionStart(BotContext ctx, FishPolicy p)
        {
            if (p == FishPolicy.Attentive) GreedyF2PBot.PlayFishBoard(ctx);
            else if (p == FishPolicy.Lazy) ReleaseAndMergeAll(ctx);
        }

        /// <summary>Once per active minute.</summary>
        public static void OnActiveMinute(BotContext ctx, FishPolicy p)
        {
            if (p == FishPolicy.Attentive) GreedyF2PBot.PlayFishBoard(ctx);
        }

        /// <summary>
        /// The lazy visit (tools/fish_model.py's <c>act(discard=False)</c>): ReleaseMax and MergeAll until neither changes the
        /// board. No discard: a jammed board stays jammed.
        /// </summary>
        public static void ReleaseAndMergeAll(BotContext ctx)
        {
            var sim = ctx.Sim;
            for (int guard = 0; guard < 256; guard++)
            {
                bool acted = false;
                if (sim.FishCanRelease) acted |= ctx.Apply(new ReleaseMax()).Ok;
                if (sim.FishMergeablePairs > 0) acted |= ctx.Apply(new MergeAll()).Ok;
                if (!acted) return;
            }
        }
    }

    /// <summary>
    /// <c>fish-attentive</c>, <c>fish-lazy</c>, <c>fish-never</c>: a player who plays only the Fish Board with one 05 section
    /// 8.2 policy and claims the offline reward, nothing else. The C# side of 05 AC14's cross-check against
    /// tools/fish_model.py (<c>fish-check</c>): the board does not depend on combat, so the two models see the same board.
    /// </summary>
    public sealed class FishOnlyBot : IBotPolicy
    {
        public FishOnlyBot(FishPolicy policy) { Policy = policy; }

        public FishPolicy Policy { get; }
        public string Name => "fish-" + FishPolicies.Name(Policy);

        public void OnSessionStart(BotContext ctx)
        {
            GreedyF2PBot.ClaimOfflineReward(ctx);
            FishPolicies.OnSessionStart(ctx, Policy);
        }

        public void Decide(BotContext ctx) => FishPolicies.OnActiveMinute(ctx, Policy);

        public void OnSessionEnd(BotContext ctx) { }
    }
}
