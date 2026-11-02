using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Services
{
    /// <summary>08's route paths, relative to the <c>/v1</c> base URL (08 section 1.1).</summary>
    public static class RemoteRoutes
    {
        public const string Time = "/time";   // GET, auth none -> {"serverTime": 1793670800} (08 section 2.2)
        public const string Save = "/save";   // PUT application/octet-stream: the raw save file (02 section 9.2, 08 section 3.1)
    }

    /// <summary>
    /// The remote half of the composition root (01 section 1.2), supplied to GameServices.Build through
    /// ServicesOptions.Remote because HeroCat.Runtime cannot reference this assembly. Builds the process's one
    /// <see cref="ApiClient"/>, Runtime's ServerClock over <c>GET /time</c> (which also observes every response's
    /// X-Server-Time) and the local-first cloud save; the other seams keep their local implementations until their
    /// clients land: SummonClient #145, ConfigClient #144, DungeonSeedClient #146, ReceiptClient #147 (the inbox is RemoteInboxService, #176), AuthClient
    /// (the <see cref="IApiCredentials"/>) #148, cloud-save metadata #152.
    /// </summary>
    public static class RemoteGameServices
    {
        /// <summary>Remote services over <paramref name="api"/>; pass an <see cref="Transport.OfflineHandler"/> until a server exists.</summary>
        public static GameServices Create(string dataDir, ApiClientOptions api, IAnalytics analytics = null)
        {
            if (dataDir == null) throw new ArgumentNullException(nameof(dataDir));
            return GameServices.Build(BackendMode.Remote, new ServicesOptions
            {
                DataDir = dataDir,
                Analytics = analytics,
                Remote = o => Wire(o, api),
            });
        }

        /// <summary>
        /// A ServicesOptions.Remote factory: the Local set with the remote clock and the local-first cloud slots, so the
        /// LocalSaveStore and the summon service capture the same clock. <paramref name="api"/>'s DeviceId defaults to the
        /// install id and its ServerTime is set to the clock. <paramref name="syncNow"/> tries one clock sync (no retries)
        /// before anything reads the time.
        /// </summary>
        public static GameServices Wire(ServicesOptions o, ApiClientOptions api, bool syncNow = false)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (api == null) throw new ArgumentNullException(nameof(api));
            var installId = o.InstallId != Guid.Empty ? o.InstallId
                : o.DataDir != null ? HeroCat.Runtime.Profile.InstallId.LoadOrCreate(o.DataDir) : Guid.NewGuid();
            if (string.IsNullOrEmpty(api.DeviceId)) api.DeviceId = installId.ToString("D");
            var client = new ApiClient(api);
            var clock = new ServerClock(new RemoteTimeApi(client), o.Clock);   // device clock until the first observation
            api.ServerTime = clock;                                             // every X-Server-Time (01 section 5.1)
            if (syncNow) clock.SyncAsync().GetAwaiter().GetResult();          // false offline; SessionRunner syncs again at boot
            var slots = new CloudSaveStore(o.Slots ?? new FileSaveStore(Path.Combine(o.DataDir, "saves")), client);
            var set = GameServices.Build(BackendMode.Local, new ServicesOptions
            {
                DataDir = o.DataDir, Content = o.Content, State = o.State, Analytics = o.Analytics, Network = o.Network,
                Clock = clock, Slots = slots, InstallId = installId, DeviceKey = o.DeviceKey, WrapClock = o.WrapClock,
                DebugReceiptCatalog = o.DebugReceiptCatalog,
            });
            // The server inbox (issue #176). A debug receipt catalog grants into the local inbox, so that build keeps it.
            return o.DebugReceiptCatalog != null ? set : set with { Inbox = new RemoteInboxService(client) };
        }
    }

    /// <summary>
    /// <c>GET /time</c> (08 section 2.2: auth none, <c>{"serverTime": unix seconds}</c>), the <see cref="ITimeApi"/> of
    /// Runtime's <see cref="ServerClock"/> (02 section 11.1). Sent with <see cref="ApiRequest.NoRetry"/>: the clock has its
    /// own CLOCK_RESYNC_SEC cadence, and a boot sync must not wait out a backoff schedule.
    /// </summary>
    public sealed class RemoteTimeApi : ITimeApi
    {
        readonly ApiClient _client;

        public RemoteTimeApi(ApiClient client) { _client = client ?? throw new ArgumentNullException(nameof(client)); }

        /// <summary>Server unix seconds. Throws ServiceException (SRV_UNEXPECTED for an unreadable body).</summary>
        public async Task<long> GetServerTimeAsync(CancellationToken ct)
        {
            var req = ApiRequest.Get(RemoteRoutes.Time, requiresAuth: false) with { NoRetry = true };
            var r = await _client.SendAsync<JToken>(req, ct).ConfigureAwait(false);
            var body = r.EnsureOk();
            if (body is JObject o && o["serverTime"] is JValue v && v.Type == JTokenType.Integer && v.Value<long>() > 0) return v.Value<long>();
            throw new ServiceException(ErrorClass.ServerError, ErrorClassifier.SRV_UNEXPECTED, "time: no serverTime in the body", r.Status);
        }
    }

    /// <summary>
    /// Local-first cloud save (08 section 9: a save never fails because the network did). Every operation goes to the
    /// local store; <see cref="Write"/> also queues the bytes under a fresh request id, and <see cref="UploadAsync"/> sends
    /// the latest queued copy as <c>PUT /save</c>, re-sending the same copy with the same id until it is accepted.
    /// TODO(#152): If-Match, X-Save-Meta and the 02 section 9 response; TODO(#153): download and the conflict rule.
    /// </summary>
    public sealed class CloudSaveStore : ISaveSlotStore
    {
        readonly ISaveSlotStore _local;
        readonly ApiClient _client;
        readonly object _lock = new object();
        Pending _pending;

        sealed class Pending
        {
            public byte[] Bytes;
            public string RequestId;
        }

        public CloudSaveStore(ISaveSlotStore local, ApiClient client)
        {
            _local = local ?? throw new ArgumentNullException(nameof(local));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public ISaveSlotStore Local => _local;

        /// <summary>Newest bytes written locally and not yet accepted by the server; null when in sync.</summary>
        public byte[] PendingUpload
        {
            get { lock (_lock) return _pending?.Bytes; }
        }

        public byte[] Read(SaveSlot slot) => _local.Read(slot);

        public void Write(byte[] bytes)
        {
            _local.Write(bytes);
            lock (_lock) _pending = new Pending { Bytes = bytes, RequestId = ApiRequest.NewRequestId() };
        }

        public void Quarantine(SaveSlot slot, long nowServerSec) => _local.Quarantine(slot, nowServerSec);

        public void ArchiveConflict(byte[] bytes, long nowServerSec) => _local.ArchiveConflict(bytes, nowServerSec);

        public void DeleteStrayTemp() => _local.DeleteStrayTemp();

        public void Purge(long nowServerSec, long corruptMaxAgeSec, long conflictMaxAgeSec) =>
            _local.Purge(nowServerSec, corruptMaxAgeSec, conflictMaxAgeSec);

        public void DeleteAll()
        {
            _local.DeleteAll();
            lock (_lock) _pending = null;
        }

        /// <summary>
        /// Uploads the queued copy on the thread pool (it reads only the encoded bytes, never GameState; 01 section 4).
        /// Throws ServiceException (Conflict for SAVE_CONFLICT); the copy stays queued on failure.
        /// </summary>
        public async Task UploadAsync(CancellationToken ct = default)
        {
            Pending p;
            lock (_lock) p = _pending;
            if (p == null) return;
            var req = ApiRequest.Mutation(HttpMethod.Put, RemoteRoutes.Save, p.Bytes, p.RequestId);
            (await _client.SendAsync<JToken>(req, ct).ConfigureAwait(false)).EnsureOk();
            lock (_lock)
            {
                if (ReferenceEquals(_pending, p)) _pending = null;
            }
        }

        /// <summary><see cref="UploadAsync"/> that reports failure as false instead of throwing.</summary>
        public async Task<bool> TryUploadAsync(CancellationToken ct = default)
        {
            try
            {
                await UploadAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (ServiceException) { return false; }
        }
    }
}
