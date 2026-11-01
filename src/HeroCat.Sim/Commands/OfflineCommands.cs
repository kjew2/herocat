namespace HeroCat.Sim.Commands
{
    // Offline catch-up commands (06 section 7, 01 section 8.7). ComputeOfflineCatchup is the system command in Command.cs;
    // its body and this handler are Simulation.Offline.cs.

    /// <summary>Credits Offline.Pending and clears it (06 section 7.1). Sent by SCR_OFFLINE_REWARD's Claim button.</summary>
    public sealed record ClaimOfflineReward : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleClaimOfflineReward(this);
    }

    /// <summary>Reject reasons of the offline commands (06 section 1.3).</summary>
    public static class OfflineRejectReasons
    {
        public const string NO_PENDING_OFFLINE_REWARD = "NO_PENDING_OFFLINE_REWARD";
    }
}
