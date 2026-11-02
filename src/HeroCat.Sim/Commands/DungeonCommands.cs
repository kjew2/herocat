using HeroCat.Sim.Content;

namespace HeroCat.Sim.Commands
{
    // Dungeon commands (06 sections 6.1-6.2, 01 sections 2.1 and 8.8; issues #172, #173). Handlers are in
    // Simulation.Dungeons.cs. Both run EnsureDay first, then the DUNGEONS feature gate, then their own checks; a reject changes
    // nothing and emits nothing.

    /// <summary>
    /// Starts a ticked run of <paramref name="Floor"/> (06 section 6.2): validates, sets Combat.Mode = Dungeon and reseeds the
    /// temporary `dungeon_run` stream with <paramref name="Seed"/>. No ticket is spent here: the clear debits it (00 decision
    /// 16). <paramref name="Seed"/> and <paramref name="GrantId"/> come from IDungeonSeedService (00 contract 11): the server's
    /// in MVP, or with <paramref name="LocalSeed"/> = true the peeked next `dungeon` draw (Rng.LocalDungeonSeed), which this
    /// command then consumes.
    /// </summary>
    public sealed record StartDungeon(Dungeon Dungeon, int Floor, ulong Seed, string GrantId, bool LocalSeed) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleStartDungeon(this);
    }

    /// <summary>Grants RewardOf(dungeon, bestFloor) at once for one ticket, with no fight and no seed (06 section 6.2).</summary>
    public sealed record SweepDungeon(Dungeon Dungeon) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleSweepDungeon(this);
    }

    /// <summary>06 section 1.3's dungeon reject reasons, localized as <c>reject.&lt;reason_lower&gt;</c>.</summary>
    public static class DungeonRejectReasons
    {
        /// <summary>(Locked) before the feature_unlocks row DUNGEONS (stage 30), or a build without it (M0).</summary>
        public const string DUNGEON_LOCKED = "DUNGEON_LOCKED";

        /// <summary>(Locked) StartDungeon on a floor below 1, above bestFloor + 1, or past the dungeon's last floor.</summary>
        public const string DUNGEON_FLOOR_NOT_UNLOCKED = "DUNGEON_FLOOR_NOT_UNLOCKED";

        /// <summary>(NotAvailable) StartDungeon or SweepDungeon while a run is active.</summary>
        public const string DUNGEON_RUN_IN_PROGRESS = "DUNGEON_RUN_IN_PROGRESS";

        /// <summary>(NotAvailable) StartDungeon while a stage boss is being fought (issue #42); a sweep is allowed.</summary>
        public const string BOSS_ATTEMPT_IN_PROGRESS = "BOSS_ATTEMPT_IN_PROGRESS";

        /// <summary>(NotAvailable) SweepDungeon while bestFloor = 0.</summary>
        public const string NOTHING_TO_SWEEP = "NOTHING_TO_SWEEP";

        /// <summary>(BadArgument) a Dungeon value this build has no row for (outside the enum).</summary>
        public const string INVALID_DUNGEON = "INVALID_DUNGEON";
    }
}
