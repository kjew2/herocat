namespace HeroCat.Runtime
{
    /// <summary>
    /// The engineering constants of 01 ("Constants introduced"). Code constants, not balance numbers, so they are not
    /// ContentDb rows (09 principle 2 covers balance literals only). 10's perf and fault tests are their check.
    /// </summary>
    public static class RuntimeConfig
    {
        // ---------------------------------------------------------------- SessionRunner (01 section 3)

        /// <summary>At most 10 s of sim per frame; a 25 s hitch is caught up over 3 frames (3.2).</summary>
        public const int MAX_TICKS_PER_FRAME = 100;

        /// <summary>A frame gap above this is an offline interval: catch-up, not ticks (3.2).</summary>
        public const int IN_APP_GAP_SEC = 30;

        /// <summary>Periodic save while dirty (3.2).</summary>
        public const int AUTOSAVE_INTERVAL_SEC = 30;

        /// <summary>Foreground cadence of the logged ApplyServerTime re-anchor (3.1, 8.11; 00 contract 7).</summary>
        public const int WALL_CLOCK_SYNC_SEC = 60;

        /// <summary>Soft per-frame wall-time budget of the tick loop; the remainder stays in the accumulator (3.2).</summary>
        public const int TICK_BUDGET_MS = 8;

        /// <summary>Consecutive save failures that raise SaveWriteFailed (3.1).</summary>
        public const int SAVE_FAIL_WARN_COUNT = 3;

        // ---------------------------------------------------------------- sim buffers (mirrored in HeroCat.Sim.SimConfig)

        public const int EVENT_BUFFER_MAX = HeroCat.Sim.SimConfig.EVENT_BUFFER_MAX;
        public const int EVENT_COALESCE_AT = HeroCat.Sim.SimConfig.EVENT_COALESCE_AT;
        public const int DMG_NUMBERS_PER_TICK_MAX = HeroCat.Sim.SimConfig.DMG_NUMBERS_PER_TICK_MAX;

        // ---------------------------------------------------------------- command log (2.6)

        /// <summary>saves/cmdlog.jsonl rotates at this size, dropping whole lines from the head.</summary>
        public const int CMDLOG_MAX_BYTES = 1024 * 1024;

        // ---------------------------------------------------------------- network (5.2)

        public const int HTTP_TIMEOUT_S = 10;
        public const int HTTP_RETRY_BASE_MS = 1000;
        public const int HTTP_RETRY_CAP_MS = 8000;
        public const int HTTP_RETRY_MAX = 4;

        // ---------------------------------------------------------------- boot, config, cloud (8.1, 8.10, 8.12)

        public const int CONFIG_FETCH_TIMEOUT_S = 3;
        public const int CONFIG_CACHE_MAX_AGE_S = 86400;
        public const int CONFIG_REFRESH_S = 1800;
        public const int CLOUD_FETCH_TIMEOUT_S = 5;
        public const int CLOUD_UPLOAD_MIN_SEC = 60;
        public const int CLOCK_JUMP_WARN_SEC = 300;

        // ---------------------------------------------------------------- flows (7, 8.5, 8.8, 8.9, 8.13)

        public const int SUMMON_PREROLL_MS = 800;
        public const int SUMMON_SLOW_MS = 3000;
        public const int DUNGEON_SEED_TTL_S = 600;
        public const int RECEIPT_RETRY_S = 60;
        public const int MAINT_POLL_S = 60;
        public const int LOGOUT_UPLOAD_TIMEOUT_S = 10;
        public const int ARCHIVE_KEEP_DAYS = 30;

        /// <summary>
        /// 08 section 2.1 (issue #273), mirrored: the server takes a same-device retry of a refresh token rotated out at
        /// most this long ago as a lost response, not reuse. The AuthClient starts refresh retries only within
        /// <c>REFRESH_REUSE_GRACE_S - HTTP_TIMEOUT_S</c> of the first send, so every retry lands inside the grace.
        /// </summary>
        public const int REFRESH_REUSE_GRACE_S = 30;

        /// <summary>
        /// 8.11: the week epoch offset, <c>4 * DAY_SECONDS</c>. DAY_SECONDS is a content constant, so this is a day count;
        /// HeroCat.Sim.DayKeys.FirstMondayDayKey is the one used by the sim (06 owns the week start).
        /// </summary>
        public const int WEEK_EPOCH_OFFSET_DAYS = HeroCat.Sim.DayKeys.FirstMondayDayKey;

        // ---------------------------------------------------------------- analytics (02 section 14, 06 section 13)

        /// <summary>02 section 2: events per upload batch.</summary>
        public const int ANALYTICS_BATCH_MAX = 100;

        /// <summary>02 section 2: offline queue cap; past it the oldest events are dropped.</summary>
        public const int ANALYTICS_QUEUE_MAX = 10000;

        /// <summary>06 section 13: KILL and BOSS <c>currency_changed</c> deltas are summed per currency over this window.</summary>
        public const int CURRENCY_AGG_SEC = 60;
    }
}
