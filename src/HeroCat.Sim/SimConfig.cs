namespace HeroCat.Sim
{
    /// <summary>
    /// Engineering constants of the sim (01, 02 "Constants introduced"). Not balance numbers, so they are code, not
    /// ContentDb rows (09 principle 2 covers balance literals only).
    /// </summary>
    public static class SimConfig
    {
        /// <summary>01 section 2.3: initial event buffer capacity.</summary>
        public const int EVENT_BUFFER_MAX = 4096;   // literal-ok: engineering constant (01), not balance

        /// <summary>01 section 2.3: past this many buffered events in one Advance, kill events coalesce into EnemyKilledBatch.</summary>
        public const int EVENT_COALESCE_AT = 1024;   // literal-ok: engineering constant (01), not balance

        /// <summary>01 section 2.4: damage numbers carried by one snapshot.</summary>
        public const int DMG_NUMBERS_PER_TICK_MAX = 16;   // literal-ok: engineering constant (01), not balance

        /// <summary>02 section 2: Dungeons.RecentRuns length (issue #52).</summary>
        public const int DUNGEON_RECENT_RUNS_MAX = 30;   // literal-ok: engineering constant (02), not balance
    }
}
