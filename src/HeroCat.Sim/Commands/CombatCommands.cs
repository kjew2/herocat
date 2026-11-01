using HeroCat.Sim.Content;

namespace HeroCat.Sim.Commands
{
    /// <summary>03's closed reject-reason list for the combat and progression commands (issue #11).</summary>
    public static class CombatRejectReasons
    {
        public const string STAT_NOT_TRAINABLE = "STAT_NOT_TRAINABLE";
        public const string STAT_AT_CAP = "STAT_AT_CAP";
        public const string NOT_ENOUGH_GOLD = "NOT_ENOUGH_GOLD";
        public const string BOSS_ALREADY_PENDING = "BOSS_ALREADY_PENDING";
        public const string NO_BOSS_TO_ATTEMPT = "NO_BOSS_TO_ATTEMPT";

        /// <summary>BadArgument: a Stat value outside the enum.</summary>
        public const string INVALID_STAT = "INVALID_STAT";

        /// <summary>BadArgument: TrainStat count &lt; 1, or TapZap count &lt; 0.</summary>
        public const string INVALID_COUNT = "INVALID_COUNT";
    }

    /// <summary>
    /// Buys <paramref name="Count"/> Training levels of <paramref name="Stat"/>, all or nothing, at the closed-form bulk cost
    /// (03 section 5.1, 01 section 2.1). Rejects: BadArgument (bad stat or count &lt; 1), Locked/STAT_NOT_TRAINABLE,
    /// Capped/STAT_AT_CAP (level + count past TRAIN_CAP), CannotAfford/NOT_ENOUGH_GOLD.
    /// </summary>
    public sealed record TrainStat(Stat Stat, int Count) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleTrainStat(this);
    }

    /// <summary>Buys the most affordable levels of <paramref name="Stat"/>, clamped at TRAIN_CAP (01 section 8 TrainStat flow).</summary>
    public sealed record TrainStatMax(Stat Stat) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleTrainStatMax(this);
    }

    /// <summary>Retries the frontier boss (03 section 2.7): the current mob despawns with no reward; the next spawn is the boss.</summary>
    public sealed record AttemptBoss : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleAttemptBoss();
    }

    /// <summary>
    /// Taps since the last frame (03 section 2.5). At most TAP_ZAP_MAX_PER_SEC are accepted per one-second window; the
    /// rest are ignored, not rejected (<see cref="Simulation.LastTapZapUsed"/>, <see cref="Simulation.LastTapZapIgnored"/>).
    /// </summary>
    public sealed record TapZap(int Count) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleTapZap(this);
    }
}
