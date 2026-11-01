using System;

namespace HeroCat.Runtime.Save
{
    /// <summary>Engineering constants from specs/06 section 2. Not balance numbers, so they live in code, not ContentDb.</summary>
    public static class SaveConfig
    {
        public const double AUTOSAVE_INTERVAL_SEC = 30;
        public const int SAVE_FAIL_WARN_COUNT = 3;
        public const long OFFLINE_REPORT_MIN_SEC = 60;
        public const double IN_APP_GAP_SEC = 30;
        public const int MAX_TICKS_PER_FRAME = 100;
        public const double WALL_CLOCK_SYNC_SEC = 60;
    }

    /// <summary>The save schema version (SAVE_SCHEMA_VERSION). Bump + add a step in SaveMigrations for every shape change after freeze.</summary>
    public static class SaveSchema
    {
        public const int Current = 1;

        /// <summary>Oldest version SaveMigrations can lift to Current. v0 is the pre-envelope dev format (see SaveMigrations).</summary>
        public const int MinSupported = 0;
    }

    /// <summary>Header/length/CRC mismatch or unparsable JSON (specs/06 rule R1). The loader falls back to the backup.</summary>
    public sealed class SaveCorruptException : Exception
    {
        public SaveCorruptException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>Parsed, but the content is unusable (missing sections, empty profile id, sim invariants broken).</summary>
    public sealed class SaveInvalidException : Exception
    {
        public SaveInvalidException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// The save was written by a newer build. Aborts the load: never falls back to the backup, never overwrites
    /// (specs/06 section 7.1). The client should show "please update".
    /// </summary>
    public sealed class SaveTooNewException : Exception
    {
        public SaveTooNewException(int found, int supported)
            : base($"Save schema v{found} is newer than this build supports (v{supported}).")
        {
            Found = found;
            Supported = supported;
        }

        public int Found { get; }
        public int Supported { get; }
    }

    /// <summary>Any failure while writing. The caller keeps its in-memory state and retries later.</summary>
    public sealed class SaveWriteException : Exception
    {
        public SaveWriteException(string message, Exception inner = null) : base(message, inner) { }
    }
}
