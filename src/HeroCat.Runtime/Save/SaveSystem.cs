using System;
using System.Collections.Generic;
using HeroCat.Runtime.Services;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Save
{
    public enum LoadStatus
    {
        /// <summary>Primary slot loaded.</summary>
        Loaded,

        /// <summary>Primary was absent or bad; the previous save loaded (bad primary was quarantined).</summary>
        LoadedFromBackup,

        /// <summary>No save existed. State is null; create a profile.</summary>
        NoSave,

        /// <summary>Saves existed but none was usable; both were quarantined. State is null; create a profile.</summary>
        Corrupt,
    }

    public sealed class LoadResult
    {
        public LoadStatus Status { get; internal set; }

        /// <summary>Null for NoSave / Corrupt.</summary>
        public GameState State { get; internal set; }

        public SaveMeta Meta { get; internal set; }

        /// <summary>Schema version found on disk before migration (== SaveSchema.Current when nothing migrated).</summary>
        public int FromSchemaVersion { get; internal set; }

        /// <summary>Why rejected slots were rejected (for logs).</summary>
        public List<string> Problems { get; } = new List<string>();

        public bool HasState => State != null;
    }

    /// <summary>
    /// The save pipeline (specs/06 sections 5 and 7) on top of any <see cref="ISaveStore"/>:
    /// Load = read -> Decode (header/len/CRC) -> Migrate -> Materialize -> Validate, primary then backup.
    /// Write = Stamp -> Encode -> store.Write (atomic, rotating).
    /// </summary>
    public static class SaveSystem
    {
        /// <summary>
        /// Load the newest usable save. Throws <see cref="SaveTooNewException"/> (nothing touched, backup not
        /// consulted) if a slot was written by a newer build; every other failure falls through to the next slot.
        /// </summary>
        public static LoadResult Load(ISaveStore store, ContentDb db, long nowServerSec, IAnalytics analytics = null)
        {
            analytics ??= NullAnalytics.Instance;
            var result = new LoadResult();
            bool anyExisted = false, primaryBad = false;
            foreach (var slot in new[] { SaveSlot.Primary, SaveSlot.Backup })
            {
                var bytes = store.Read(slot);
                if (bytes == null) continue;
                anyExisted = true;
                try
                {
                    var (state, meta, from) = DecodeFull(bytes, db, analytics);
                    result.State = state;
                    result.Meta = meta;
                    result.FromSchemaVersion = from;
                    result.Status = slot == SaveSlot.Primary ? LoadStatus.Loaded : LoadStatus.LoadedFromBackup;
                    if (slot == SaveSlot.Backup)
                    {
                        if (primaryBad) store.Quarantine(SaveSlot.Primary, nowServerSec);
                        analytics.Track("save_fallback");
                    }
                    return result;
                }
                catch (Exception e) when (e is SaveCorruptException || e is SaveInvalidException)
                {
                    result.Problems.Add($"{slot}: {e.Message}");
                    if (slot == SaveSlot.Primary) primaryBad = true;
                }
            }

            if (!anyExisted)
            {
                result.Status = LoadStatus.NoSave;
                return result;
            }
            store.Quarantine(SaveSlot.Primary, nowServerSec);
            store.Quarantine(SaveSlot.Backup, nowServerSec);
            analytics.Track("save_corrupt", new Dictionary<string, object> { ["problems"] = string.Join("; ", result.Problems) });
            result.Status = LoadStatus.Corrupt;
            return result;
        }

        /// <summary>Decode + migrate + materialise + validate one file. Throws SaveCorrupt/SaveInvalid/SaveTooNew.</summary>
        public static (GameState state, SaveMeta meta, int fromVersion) DecodeFull(byte[] bytes, ContentDb db, IAnalytics analytics = null)
        {
            var (version, body) = SaveSerializer.Decode(bytes);
            try
            {
                body = SaveMigrations.Migrate(version, body, (from, to) =>
                    (analytics ?? NullAnalytics.Instance).Track("migration_applied", new Dictionary<string, object> { ["from"] = from, ["to"] = to }));
            }
            catch (Exception e) when (!(e is SaveTooNewException) && !(e is SaveInvalidException))
            {
                throw new SaveInvalidException($"migration from v{version} failed: {e.Message}", e);
            }
            var (state, meta) = SaveSerializer.Materialize(body);
            Validate(state, db);
            return (state, meta, version);
        }

        /// <summary>
        /// Reject rules (specs/06 7.2, R3-R5) plus the sim's own invariant check. Constructing a Simulation also
        /// normalises array lengths to the loaded content (a light version of the spec's repair rules).
        /// Throws <see cref="SaveInvalidException"/>.
        /// </summary>
        public static void Validate(GameState s, ContentDb db)
        {
            if (s == null) throw new SaveInvalidException("state is null");
            if (s.ProfileId == Guid.Empty) throw new SaveInvalidException("ProfileId is empty");
            if (s.Clock == null || s.Daily == null || s.Currencies == null || s.Progression == null || s.Stage == null ||
                s.Combat == null || s.Gear == null || s.Heroes == null || s.HeroBanner == null || s.Rng == null ||
                s.Rng.Streams == null || s.Lifetime == null)
                throw new SaveInvalidException("a root section is null");
            foreach (var name in StreamNames.All)
                if (!s.Rng.Streams.TryGetValue(name, out var st) || st == null)
                    throw new SaveInvalidException($"rng stream '{name}' missing");
            try
            {
                new Simulation(db, s).Validate();
            }
            catch (InvariantViolationException e) { throw new SaveInvalidException("invariant: " + e.Message, e); }
            catch (Exception e) when (e is NullReferenceException || e is IndexOutOfRangeException || e is ArgumentException ||
                                      e is KeyNotFoundException)
            {
                throw new SaveInvalidException("state is malformed: " + e.Message, e);
            }
        }

        /// <summary>specs/06 5.1. Every write stamps first; bots that skip files call this directly.</summary>
        public static void Stamp(GameState state, SaveMeta meta, long nowServerSec, string contentVersion)
        {
            meta.LastSavedAtServerSec = nowServerSec;
            meta.SaveSequence += 1;
            if (!string.IsNullOrEmpty(contentVersion)) state.ContentVersion = contentVersion;
        }

        /// <summary>Stamp, encode, write atomically. Throws <see cref="SaveWriteException"/>; the stamp has already happened.</summary>
        public static void Write(ISaveStore store, GameState state, SaveMeta meta, long nowServerSec, string contentVersion)
        {
            Stamp(state, meta, nowServerSec, contentVersion);
            byte[] bytes;
            try { bytes = SaveSerializer.Encode(state, meta); }
            catch (Exception e) { throw new SaveWriteException("encode failed: " + e.Message, e); }
            store.Write(bytes);
        }
    }
}
