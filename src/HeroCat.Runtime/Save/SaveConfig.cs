using System;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Save
{
    /// <summary>
    /// Save engineering constants (02 section 2). Not balance numbers, so they live in code, not ContentDb. The session
    /// constants (AUTOSAVE_INTERVAL_SEC, SAVE_FAIL_WARN_COUNT, ...) are 01's and live in <see cref="RuntimeConfig"/>.
    /// </summary>
    public static class SaveConfig
    {
        public const int SAVE_KEEP_COUNT = 2;
        public const int SAVE_REPAIR_MAX = 20;
        public const int CORRUPT_KEEP_DAYS = 7;
    }

    /// <summary>
    /// The save schema version (02 section 7.1). v1 is the new design's tree from scratch (00 decision 33); saves from the
    /// old prototype are not migrated, they fail Decode (their header lacks <c>v=</c>) and are quarantined.
    ///
    /// Freeze procedure (02 section 7.4): the day a build ships to anyone whose save the team is not willing to delete
    /// (external TestFlight / Play test track, any playtester outside the dev team), set <see cref="V1Frozen"/> to true,
    /// commit <c>src/HeroCat.Runtime.Tests/Fixtures/Saves/v1.json</c> and its <c>v1.sha256</c>, and never set it back.
    /// After the freeze every GameState shape change is a new version with a step in <see cref="SaveMigrations"/>.
    /// </summary>
    public static class SaveSchema
    {
        public const int Current = GameState.CurrentSchemaVersion;   // SAVE_SCHEMA_VERSION
        public const bool V1Frozen = false;
    }

    /// <summary>Header/length/CRC mismatch or unparsable JSON (02 rule R1). The loader falls back to the previous file.</summary>
    public sealed class SaveCorruptException : Exception
    {
        public SaveCorruptException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>Parsed, but unusable (02 rules R2-R5). The loader falls back to the previous file.</summary>
    public sealed class SaveInvalidException : Exception
    {
        public SaveInvalidException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// The save was written by a newer build. Aborts the load: never falls back to the previous file, never overwrites
    /// (02 section 7.1). The client shows SAVE_TOO_NEW.
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
