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

        /// <summary>BadArgument: SelectStage with a stage &lt; 1 or &gt; STAGE_CAP.</summary>
        public const string STAGE_OUT_OF_RANGE = "STAGE_OUT_OF_RANGE";

        /// <summary>Locked: SelectStage above both Progress.HighestCleared and the frontier.</summary>
        public const string STAGE_NOT_CLEARED = "STAGE_NOT_CLEARED";

        /// <summary>NotAvailable: SelectStage while Combat.Mode == Dungeon (the stage fields are frozen during a run, 02 section 3.3).</summary>
        public const string DUNGEON_RUN_IN_PROGRESS = "DUNGEON_RUN_IN_PROGRESS";

        /// <summary>Capped: Awaken at the last awakening row's rank (03 section 5.3; every rank in a tier without the rows).</summary>
        public const string AWAKEN_MAX_RANK = "AWAKEN_MAX_RANK";

        /// <summary>Locked: Awaken while Progress.HighestCleared is below the next rank's stage gate (AWAKEN_STAGES).</summary>
        public const string AWAKEN_STAGE_NOT_REACHED = "AWAKEN_STAGE_NOT_REACHED";

        /// <summary>CannotAfford: Awaken without the next rank's AWAKEN_COST_ROCK_CANDY (the same key as 04's limit breaks).</summary>
        public const string NOT_ENOUGH_ROCK_CANDY = "NOT_ENOUGH_ROCK_CANDY";
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

    /// <summary>
    /// Farms a cleared side stage, or returns to the frontier with null (03 section 2.7, 00 decision 67; issue #35).
    /// Combat.Stage stays the frontier and keeps its KillsInStage, Boss phase and BossFailsOnStage; kills on the selected
    /// stage pay its Gold and Exp and never advance anything. Selecting the frontier stage equals null. The current enemy
    /// despawns with no reward. Rejects: BadArgument/STAGE_OUT_OF_RANGE (stage &lt; 1 or &gt; STAGE_CAP),
    /// Locked/STAGE_NOT_CLEARED (stage &gt; max(HighestCleared, Stage)), NotAvailable/DUNGEON_RUN_IN_PROGRESS.
    /// Emits StageChanged(fought stage); selecting what is already fought is accepted with no event.
    /// </summary>
    public sealed record SelectStage(int? Stage) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleSelectStage(this);
    }

    /// <summary>
    /// Buys the next Awakening rank (03 section 5.3, 00 decision 11; issue #157). Rejects, in this order:
    /// Capped/AWAKEN_MAX_RANK (no next row), Locked/AWAKEN_STAGE_NOT_REACHED (Progress.HighestCleared below the next
    /// rank's stage_gate), CannotAfford/NOT_ENOUGH_ROCK_CANDY. Charges rock_candy_cost with reason AWAKENING, raises
    /// Hero.AwakenRank by one and publishes Awakened(rank).
    /// </summary>
    public sealed record Awaken : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleAwaken();
    }
}
