using System;
using System.Collections.Generic;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Save
{
    public enum LoadStatus
    {
        /// <summary>slot0.sav loaded.</summary>
        Loaded,

        /// <summary>slot0.sav was absent or bad; slot0.prev.sav loaded (the bad one was quarantined).</summary>
        LoadedFromPrev,

        /// <summary>No save existed. State is null; create a profile.</summary>
        NoSave,

        /// <summary>Saves existed but none was usable; both were quarantined (an old-design dev save lands here). State is null.</summary>
        Corrupt,
    }

    public sealed class LoadResult
    {
        public LoadStatus Status { get; internal set; }

        /// <summary>Null for NoSave / Corrupt.</summary>
        public GameState State { get; internal set; }

        /// <summary>Schema version found on disk before migration.</summary>
        public int FromSchemaVersion { get; internal set; }

        public bool HmacValid { get; internal set; }

        /// <summary>Why rejected files were rejected (for logs).</summary>
        public List<string> Problems { get; } = new List<string>();

        public bool HasState => State != null;
    }

    /// <summary>
    /// The save pipeline on top of any <see cref="ISaveStore"/> (02 sections 6-8): Load = read, Decode (R1), Migrate,
    /// Materialize, Validate (R2-R5), slot0.sav then slot0.prev.sav; Write = Stamp, Encode, atomic rotating write.
    /// TODO(#75): the repair rules P1-P21 and the content-dependent validator; TODO(#77): 02 section 8's
    /// LocalSaveStore interface, recovery order and quarantine retention.
    /// </summary>
    public static class SaveSystem
    {
        /// <summary>
        /// Load the newest usable save. Throws <see cref="SaveTooNewException"/> (nothing touched, prev not consulted) if a
        /// file was written by a newer build; every other failure falls through to the next file.
        /// </summary>
        public static LoadResult Load(ISaveStore store, ContentDb db, long nowServerSec, IAnalytics analytics = null, byte[] deviceKey = null)
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
                    var (state, from, hmacValid) = DecodeFull(bytes, db, analytics, deviceKey);
                    result.State = state;
                    result.FromSchemaVersion = from;
                    result.HmacValid = hmacValid;
                    result.Status = slot == SaveSlot.Primary ? LoadStatus.Loaded : LoadStatus.LoadedFromPrev;
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
                    analytics.Track("save_reject", new Dictionary<string, object> { ["file"] = slot.ToString(), ["reason"] = e.Message });
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

        /// <summary>Decode + migrate + materialize + validate one file. Throws SaveCorrupt / SaveInvalid / SaveTooNew.</summary>
        public static (GameState state, int fromVersion, bool hmacValid) DecodeFull(byte[] bytes, ContentDb db, IAnalytics analytics = null,
                                                                                   byte[] deviceKey = null)
        {
            var file = SaveSerializer.Decode(bytes, deviceKey);
            if (file.SchemaVersion > SaveSchema.Current) throw new SaveTooNewException(file.SchemaVersion, SaveSchema.Current);
            Newtonsoft.Json.Linq.JObject body;
            try
            {
                body = SaveMigrations.Migrate(file.SchemaVersion, file.Body, new AnalyticsMigrationLog(analytics ?? NullAnalytics.Instance));
            }
            catch (Exception e) when (!(e is SaveTooNewException) && !(e is SaveInvalidException))
            {
                throw new SaveInvalidException($"migration from v{file.SchemaVersion} failed: {e.Message}", e);
            }
            var state = SaveSerializer.Materialize(body);
            Validate(state, db);
            return (state, file.SchemaVersion, file.HmacValid);
        }

        /// <summary>The structural reject rules R2-R5 (02 section 6.4). Throws <see cref="SaveInvalidException"/>.</summary>
        public static void Validate(GameState s, ContentDb db)
        {
            if (s == null) throw new SaveInvalidException("state is null");
            if (s.SchemaVersion < 1) throw new SaveInvalidException("R2: SchemaVersion missing or < 1");
            if (s.Time == null || s.Hero == null || s.Training == null || s.Wallet == null || s.Weapons == null || s.Skills == null ||
                s.Companions == null || s.Runes == null || s.Loadout == null || s.Fish == null || s.Banners == null || s.Combat == null ||
                s.Progress == null || s.Quests == null || s.Dungeons == null || s.Shop == null || s.Research == null || s.Inbox == null ||
                s.Offline == null || s.Pending == null || s.Tutorial == null || s.Rng == null || s.Stats == null)
                throw new SaveInvalidException("R3: a root section is null");
            if (s.RngRoot == Guid.Empty) throw new SaveInvalidException("R4: RngRoot is empty");
            foreach (var name in RngStreams.All)
            {
                if (!s.Rng.TryGetValue(name, out var st) || st == null) throw new SaveInvalidException($"R4: rng stream '{name}' missing");
                if (!RngStreams.TryParseSeed(st.Seed, out _)) throw new SaveInvalidException($"R4: rng stream '{name}' seed is not 16 hex chars");
                if (st.Counter < 0) throw new SaveInvalidException($"R4: rng stream '{name}' counter is negative");
            }
            var k = db.Constants;
            if (s.Training.Level == null || s.Training.Level.Length != 8) throw new SaveInvalidException("R5: Training.Level length != 8");
            if (s.Fish.Cells == null || s.Fish.Cells.Length != k.FishBoardW * k.FishBoardH)
                throw new SaveInvalidException("R5: Fish.Cells length != FISH_BOARD_W * FISH_BOARD_H");
            if (s.Wallet.DungeonTicket == null || s.Wallet.DungeonTicket.Length != 3) throw new SaveInvalidException("R5: Wallet.DungeonTicket length != 3");
            if (s.Wallet.SummonTicket == null || s.Wallet.SummonTicket.Length != 4) throw new SaveInvalidException("R5: Wallet.SummonTicket length != 4");
        }

        /// <summary>
        /// SaveStamp (02 section 8.2): LastSavedServerTime = now; SaveSeq += 1; ContentVersion/ContentHash = db's;
        /// DeviceId = this install. When <paramref name="ticking"/>, LastTickServerTime = now too (the window was ticked).
        /// </summary>
        public static void Stamp(GameState state, long nowServerSec, ContentDb db, Guid installId, bool ticking)
        {
            state.Time.LastSavedServerTime = nowServerSec;
            state.SaveSeq += 1;
            state.ContentVersion = db.Version;
            state.ContentHash = db.ContentHash;
            if (installId != Guid.Empty) state.DeviceId = installId;
            if (ticking && nowServerSec > state.Time.LastTickServerTime) state.Time.LastTickServerTime = nowServerSec;
        }

        /// <summary>Stamp, encode, write atomically. Throws <see cref="SaveWriteException"/>; the stamp has already happened.</summary>
        public static void Write(ISaveStore store, GameState state, long nowServerSec, ContentDb db, Guid installId, bool ticking,
                                 byte[] deviceKey = null)
        {
            Stamp(state, nowServerSec, db, installId, ticking);
            byte[] bytes;
            try { bytes = SaveSerializer.Encode(state, deviceKey); }
            catch (Exception e) { throw new SaveWriteException("encode failed: " + e.Message, e); }
            store.Write(bytes);
        }

        sealed class AnalyticsMigrationLog : IMigrationLog
        {
            readonly IAnalytics _a;
            public AnalyticsMigrationLog(IAnalytics a) { _a = a; }

            public void Applied(int from, int to) =>
                _a.Track("migration_applied", new Dictionary<string, object> { ["from"] = from, ["to"] = to });
        }
    }
}
