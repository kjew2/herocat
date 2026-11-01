using HeroCat.Sim.State;

namespace HeroCat.Sim.Events
{
    // ---------------------------------------------------------------------------------------- offline catch-up (06 section 7)

    /// <summary>
    /// ComputeOfflineCatchup stored or summed a gap of at least OFFLINE_MIN_S into Offline.Pending (06 section 7.1).
    /// <see cref="Pending"/> is the whole pending record after this gap (the sum when a reward was already pending).
    /// </summary>
    public sealed record OfflineRewardComputed(CatchupResult Pending) : Event;

    /// <summary>ClaimOfflineReward credited <see cref="Reward"/> and cleared Offline.Pending (06 section 7.1).</summary>
    public sealed record OfflineRewardClaimed(CatchupResult Reward) : Event;
}
