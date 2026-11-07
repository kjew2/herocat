using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using Newtonsoft.Json;

namespace HeroCat.Runtime.Save
{
    /// <summary>Why a save is written (02 section 8.1). Cloud upload policy keys off it (9.2).</summary>
    public enum SaveTrigger { Periodic, Pause, Quit, Resume, Summon, Inbox, Dungeon, Debug, Conflict }

    /// <summary>02 section 8.1.</summary>
    public enum LoadStatus
    {
        /// <summary>slot0.sav loaded.</summary>
        Loaded,

        /// <summary>slot0.sav was absent or bad; slot0.prev.sav loaded (a bad slot0.sav was quarantined).</summary>
        LoadedFromPrev,

        /// <summary>No usable save: State is a fresh ProfileFactory profile (any bad files were quarantined).</summary>
        NewProfile,

        /// <summary>Cloud only (9.3): State and Other are the two candidates. LocalSaveStore never returns it.</summary>
        Conflict,

        /// <summary>A file was written by a newer build. State is null, nothing was touched, writes are refused (7.1).</summary>
        TooNew,
    }

    /// <summary>
    /// Save metadata (02 section 9.1). A local load fills the file's own fields; the conflict rule (9.3,
    /// <see cref="ConflictRule"/>) also reads <see cref="ClockAuthoritative"/>, <see cref="ProgressScore"/> and
    /// <see cref="Summary"/>, and the modal (9.4) <see cref="DeviceModel"/>. TODO(#152): clientVersion, hmac, and filling
    /// ClockAuthoritative, ProgressScore and Summary for the cloud client (a local load leaves them at their defaults).
    /// </summary>
    public sealed class SaveMeta
    {
        public Guid PlayerId { get; set; }
        public Guid DeviceId { get; set; }
        public long SaveSeq { get; set; }
        public long LastSavedServerTime { get; set; }
        public int SchemaVersion { get; set; }
        public string ContentVersion { get; set; } = "";
        public int SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
        public bool HmacValid { get; set; }

        /// <summary>The stamp came from an authoritative clock (02 section 11). A non-authoritative stamp never beats an authoritative one (9.3).</summary>
        public bool ClockAuthoritative { get; set; }

        /// <summary>02 section 9.1's progressScore (computed by the cloud client, #152).</summary>
        public double ProgressScore { get; set; }

        /// <summary>02 section 9.1's <c>summary</c> block. Never null.</summary>
        public SaveSummary Summary { get; set; } = new SaveSummary();

        /// <summary>Remote candidates only: the server version's etag (the resolution's <c>If-Match</c>, 9.4).</summary>
        public string Etag { get; set; }

        /// <summary>Remote candidates only: the uploading device's model name from <c>devices</c> (08), for the modal's card.</summary>
        public string DeviceModel { get; set; }

        public static SaveMeta Of(GameState s, SaveFile f, int sizeBytes) => new SaveMeta
        {
            PlayerId = s.PlayerId, DeviceId = s.DeviceId, SaveSeq = s.SaveSeq, LastSavedServerTime = s.Time.LastSavedServerTime,
            SchemaVersion = f.SchemaVersion, ContentVersion = s.ContentVersion, SizeBytes = sizeBytes, Sha256 = f.Sha256,
            HmacValid = f.HmacValid,
        };
    }

    /// <summary>02 section 8.1's record (a class: C# 9 records are fine, but the extra diagnostics read better as properties).</summary>
    public sealed class LoadResult
    {
        public LoadResult(LoadStatus status, GameState state, GameState other, IReadOnlyList<string> repairs, SaveMeta localMeta,
                          SaveMeta remoteMeta)
        {
            Status = status; State = state; Other = other; Repairs = repairs ?? Array.Empty<string>(); LocalMeta = localMeta;
            RemoteMeta = remoteMeta;
        }

        public LoadStatus Status { get; }

        /// <summary>The state to run. Null only for TooNew.</summary>
        public GameState State { get; }

        /// <summary>The other conflict candidate (Conflict only).</summary>
        public GameState Other { get; }

        /// <summary>Repair rule ids SaveValidator applied to State ("P1", "P9b", ...).</summary>
        public IReadOnlyList<string> Repairs { get; }

        public SaveMeta LocalMeta { get; }
        public SaveMeta RemoteMeta { get; }

        /// <summary>Schema version found on disk before migration (0 for NewProfile / TooNew).</summary>
        public int FromSchemaVersion { get; internal set; }

        /// <summary>Why rejected files were rejected (logs, the debug panel).</summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>The TooNew version found, else 0.</summary>
        public int TooNewVersion { get; internal set; }

        /// <summary>
        /// NewProfile only: save copies existed but none was readable, so they were quarantined (02 section 8.3). In M0 there
        /// is no cloud copy, so boot treats this as "no readable copy anywhere": the save_corrupt_all event and the one-time
        /// notice (00 decision 103). The MVP cloud wrapper clears it when it restores a cloud copy instead.
        /// </summary>
        public bool ReplacedUnreadableSave { get; internal set; }

        public bool HasState => State != null;
        public bool IsNewProfile => Status == LoadStatus.NewProfile;
    }

    /// <summary>02 section 8.1. Local implementation: <see cref="LocalSaveStore"/>; the MVP cloud client wraps it (9).</summary>
    public interface ISaveStore
    {
        Task<LoadResult> LoadAsync(long nowServerSec, ContentDb db, CancellationToken ct);

        /// <summary>Synchronous; must complete inside OnApplicationPause. Throws <see cref="SaveWriteException"/>.</summary>
        void Save(GameState state, SaveTrigger trigger);

        /// <summary>Local: archives <paramref name="rejected"/> as slot0.conflict-&lt;unix&gt;.sav and saves <paramref name="chosen"/>.</summary>
        Task ResolveConflictAsync(GameState chosen, GameState rejected, CancellationToken ct);
    }

    /// <summary>
    /// SaveStamp (02 section 8.2), applied by every write before encoding: <c>LastSavedServerTime = now</c>,
    /// <c>SaveSeq += 1</c>, <c>ContentVersion</c>/<c>ContentHash</c> from the content, <c>DeviceId = installId</c>. It never
    /// writes <c>Time.LastTickServerTime</c>: the sim owns it inside Apply and every tick (00 contract 5), so the command log
    /// replays it, and everything the stamp writes is bookkeeping that StateHash leaves out.
    /// </summary>
    public static class SaveStamp
    {
        public static void Apply(GameState state, long nowServerSec, ContentDb db, Guid installId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            state.Time.LastSavedServerTime = nowServerSec;
            state.SaveSeq += 1;
            state.ContentVersion = db.Version;
            state.ContentHash = db.ContentHash;
            if (installId != Guid.Empty) state.DeviceId = installId;
        }
    }

    /// <summary>
    /// The local save store of 02 section 8 on top of an <see cref="ISaveSlotStore"/> (files on device, memory in tests
    /// and bots). Load is 8.3's recovery order; Save is 8.2's stamp and rotating write. Analytics: <c>save_fallback</c>,
    /// <c>save_reject{file, reason}</c>, <c>save_corrupt</c>, <c>save_repaired{rule}</c>, <c>save_write_failed{consecutive}</c>,
    /// <c>migration_applied{from, to}</c> (14.3). Single-threaded, like the session that owns it.
    /// </summary>
    public sealed class LocalSaveStore : ISaveStore
    {
        readonly IClock _clock;
        readonly IAnalytics _analytics;
        readonly byte[] _deviceKey;
        bool _tooNew;

        /// <param name="slots">Where the bytes live.</param>
        /// <param name="clock">Read by Save's stamp; a <see cref="IClockFloor"/> clock is floored at the loaded save's LastSavedServerTime.</param>
        /// <param name="installId">This install's id (02 section 8.4), stamped into DeviceId and used by repair P20.</param>
        public LocalSaveStore(ISaveSlotStore slots, IClock clock, Guid installId, IAnalytics analytics = null, byte[] deviceKey = null)
        {
            Slots = slots ?? throw new ArgumentNullException(nameof(slots));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            InstallId = installId;
            _analytics = analytics ?? NullAnalytics.Instance;
            _deviceKey = deviceKey;
        }

        public ISaveSlotStore Slots { get; }
        public Guid InstallId { get; }

        /// <summary>The content of the last LoadAsync; Save stamps its version and hash.</summary>
        public ContentDb Content { get; private set; }

        /// <summary>Remote mode skips the summon counter invariant (P15).</summary>
        public bool RemoteMode { get; set; }

        /// <summary>Builds the NewProfile state. Default: a local guest via <see cref="ProfileFactory.Create"/>.</summary>
        public Func<ContentDb, long, GameState> NewProfileFactory { get; set; }

        public int ConsecutiveWriteFailures { get; private set; }

        public Task<LoadResult> LoadAsync(long nowServerSec, ContentDb db, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Load(nowServerSec, db));
        }

        /// <summary>The synchronous body of <see cref="LoadAsync"/> (local IO only).</summary>
        public LoadResult Load(long nowServerSec, ContentDb db)
        {
            Content = db ?? throw new ArgumentNullException(nameof(db));
            long day = db.Constants.DaySeconds;
            Slots.DeleteStrayTemp();
            Slots.Purge(nowServerSec, SaveConfig.CORRUPT_KEEP_DAYS * day, SaveConfig.CONFLICT_KEEP_DAYS * day);

            var problems = new List<string>();
            bool anyExisted = false;
            bool primaryBad = false;
            foreach (var slot in new[] { SaveSlot.Primary, SaveSlot.Backup })
            {
                var bytes = Slots.Read(slot);
                if (bytes == null) continue;
                anyExisted = true;
                string file = slot == SaveSlot.Primary ? "slot0.sav" : "slot0.prev.sav";
                try
                {
                    var file0 = SaveSerializer.Decode(bytes, _deviceKey);
                    if (file0.SchemaVersion > SaveSchema.Current)
                    {
                        // Never falls through to prev, renames nothing, and refuses every later write (02 section 7.1).
                        _tooNew = true;
                        var tooNew = new LoadResult(LoadStatus.TooNew, null, null, null, null, null) { TooNewVersion = file0.SchemaVersion };
                        tooNew.Problems.AddRange(problems);
                        return tooNew;
                    }
                    var state = SaveSystem.MigrateAndMaterialize(file0, _analytics);
                    var report = SaveValidator.RunWithReport(state, nowServerSec, db,
                        new SaveValidatorOptions { InstallId = InstallId, RemoteMode = RemoteMode });
                    foreach (var rule in report.Repairs)
                        _analytics.Track(new AnalyticsEvents.SaveRepaired(rule));
                    if (slot == SaveSlot.Backup)
                    {
                        if (primaryBad) Slots.Quarantine(SaveSlot.Primary, nowServerSec);
                        _analytics.Track(new AnalyticsEvents.SaveFallback());
                    }
                    (_clock as IClockFloor)?.SetFloor(state.Time.LastSavedServerTime);   // 02 section 11.2 rule 4
                    var result = new LoadResult(slot == SaveSlot.Primary ? LoadStatus.Loaded : LoadStatus.LoadedFromPrev, state, null,
                                                report.Repairs, SaveMeta.Of(state, file0, bytes.Length), null)
                    {
                        FromSchemaVersion = file0.SchemaVersion,
                    };
                    result.Problems.AddRange(problems);
                    result.Problems.AddRange(report.Log);
                    return result;
                }
                catch (Exception e) when (e is SaveCorruptException || e is SaveInvalidException || e is JsonException)
                {
                    problems.Add($"{file}: {e.Message}");
                    _analytics.Track(new AnalyticsEvents.SaveReject(file, e.Message));
                    if (slot == SaveSlot.Primary) primaryBad = true;
                }
            }

            if (anyExisted)
            {
                Slots.Quarantine(SaveSlot.Primary, nowServerSec);
                Slots.Quarantine(SaveSlot.Backup, nowServerSec);
                _analytics.Track(new AnalyticsEvents.SaveCorrupt(string.Join("; ", problems)));
            }
            var fresh = (NewProfileFactory ?? DefaultNewProfile)(db, nowServerSec);
            var created = new LoadResult(LoadStatus.NewProfile, fresh, null, null, null, null) { ReplacedUnreadableSave = anyExisted };
            created.Problems.AddRange(problems);
            return created;
        }

        GameState DefaultNewProfile(ContentDb db, long now) => ProfileFactory.Create(db, Guid.Empty, now, InstallId, null, _analytics);

        public void Save(GameState state, SaveTrigger trigger)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (_tooNew) throw new SaveWriteException("the save on disk is newer than this build; writes are refused (02 section 7.1)");
            if (Content == null) throw new InvalidOperationException("LoadAsync must run before Save (it supplies the content)");
            try
            {
                Write(state, trigger);
                ConsecutiveWriteFailures = 0;
            }
            catch (SaveWriteException)
            {
                ConsecutiveWriteFailures++;
                _analytics.Track(new AnalyticsEvents.SaveWriteFailed(ConsecutiveWriteFailures));
                throw;
            }
        }

        void Write(GameState state, SaveTrigger trigger)
        {
            SaveStamp.Apply(state, _clock.NowServerSeconds, Content, InstallId);
            byte[] bytes;
            try { bytes = SaveSerializer.Encode(state, _deviceKey); }
            catch (Exception e) { throw new SaveWriteException("encode failed: " + e.Message, e); }
            Slots.Write(bytes);
        }

        public Task ResolveConflictAsync(GameState chosen, GameState rejected, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (rejected != null) Slots.ArchiveConflict(SaveSerializer.Encode(rejected, _deviceKey), _clock.NowServerSeconds);
            if (chosen != null) Save(chosen, SaveTrigger.Conflict);
            return Task.CompletedTask;
        }
    }
}
