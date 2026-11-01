namespace HeroCat.Sim.Commands
{
    // Fish Board commands (05 section 4, 00 section 5.1). Handlers are in Simulation.Fish.cs. Every one is rejected
    // Locked / BOARD_LOCKED first while Combat.Mode == Dungeon (issue #40). Cells are row-major indices 0..19.

    /// <summary>One fish from the Magic Tank to a random empty cell (05 section 4.1). One fish_spawn draw on success.</summary>
    public sealed record ReleaseFish : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleReleaseFish();
    }

    /// <summary>ReleaseFish until it would reject (05 section 6.1). Accepted with zero releases too.</summary>
    public sealed record ReleaseMax : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleReleaseMax();
    }

    /// <summary>Merges two equal fish; the new fish lands in <see cref="Target"/> (05 section 4.2).</summary>
    public sealed record MergeFish(int Source, int Target) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleMergeFish(this);
    }

    /// <summary>Every mergeable pair, lowest level first, leftmost pair first, until a pass finds none (05 section 6.1).</summary>
    public sealed record MergeAll : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleMergeAll();
    }

    /// <summary>Cosmetic move to an empty cell (05 section 4.5, 00 decision 22). No draw, no coins, no DPS change.</summary>
    public sealed record MoveFish(int Source, int Target) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleMoveFish(this);
    }

    /// <summary>Releases a fish back to the sea for FISH_DISCARD_COINS_PCT of its discovery value (05 section 4.4).</summary>
    public sealed record DiscardFish(int Cell) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleDiscardFish(this);
    }

    /// <summary>05's closed reject-reason list (section 4.2), localized as <c>reject.&lt;reason&gt;</c>.</summary>
    public static class FishRejectReasons
    {
        public const string TANK_EMPTY = "TANK_EMPTY";
        public const string BOARD_FULL = "BOARD_FULL";
        public const string LEVEL_MISMATCH = "LEVEL_MISMATCH";
        public const string EMPTY_CELL = "EMPTY_CELL";
        public const string CELL_OCCUPIED = "CELL_OCCUPIED";
        public const string FISH_AT_LEVEL_CAP = "FISH_AT_LEVEL_CAP";
        public const string BAD_CELL = "BAD_CELL";
        public const string BOARD_LOCKED = "BOARD_LOCKED";
    }
}
