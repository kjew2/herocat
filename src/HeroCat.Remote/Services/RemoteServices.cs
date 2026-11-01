using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Services
{
    /// <summary>Vendor-neutral call names. A vendor transport maps these onto its own routes or RPC ids.</summary>
    public static class RemoteRoutes
    {
        public const string Time = "/time";   // GET  -> {"serverTime": 1793670800} (08 section 2.2)
        public const string Save = "/save";   // PUT  {"bytes": "<base64>"}; 409 when the cloud copy is newer
    }

    /// <summary>
    /// The remote half of the composition root (01 section 1.2), supplied to GameServices.Build through
    /// ServicesOptions.Remote because HeroCat.Runtime cannot reference this assembly. A stub over the vendor-neutral
    /// transport spike: Runtime's ServerClock over GET /time and local-first cloud save; the other seams keep their local
    /// implementations.
    /// TODO(#143): rebuilt on the ApiClient (SummonClient #145, InboxClient, ReceiptClient #147, ConfigClient #144,
    /// DungeonSeedClient #146).
    /// </summary>
    public static class RemoteGameServices
    {
        /// <summary>Pass <see cref="OfflineTransport.Instance"/> until a vendor transport exists.</summary>
        public static GameServices Create(string dataDir, IRemoteTransport transport, RemoteOptions options = null,
                                          IAnalytics analytics = null)
        {
            if (dataDir == null) throw new ArgumentNullException(nameof(dataDir));
            return GameServices.Build(BackendMode.Remote, new ServicesOptions
            {
                DataDir = dataDir,
                Analytics = analytics,
                Remote = o => Wire(o, transport, options),
            });
        }

        /// <summary>
        /// A ServicesOptions.Remote factory over <paramref name="transport"/>: the Local set with the remote clock and the
        /// local-first cloud slots, so the LocalSaveStore and the summon service capture the same clock.
        /// <paramref name="syncNow"/> tries one clock sync before anything reads the time.
        /// </summary>
        public static GameServices Wire(ServicesOptions o, IRemoteTransport transport, RemoteOptions options = null, bool syncNow = false)
        {
            var client = new RemoteClient(transport, options);
            var clock = new ServerClock(new RemoteTimeApi(client), o.Clock);   // device clock until the first observation
            if (syncNow) clock.SyncAsync().GetAwaiter().GetResult();          // false offline; SessionRunner syncs again at boot
            var slots = new CloudSaveStore(o.Slots ?? new FileSaveStore(Path.Combine(o.DataDir, "saves")), client);
            return GameServices.Build(BackendMode.Local, new ServicesOptions
            {
                DataDir = o.DataDir, Content = o.Content, State = o.State, Analytics = o.Analytics, Network = o.Network,
                Clock = clock, Slots = slots, InstallId = o.InstallId, DeviceKey = o.DeviceKey, WrapClock = o.WrapClock,
                DebugReceiptCatalog = o.DebugReceiptCatalog,
            });
        }
    }

    /// <summary>
    /// <c>GET /time</c> over the transport (08 section 2.2: <c>{"serverTime": unix seconds}</c>), the
    /// <see cref="ITimeApi"/> of Runtime's <see cref="ServerClock"/> (02 section 11.1; issue #149). The transport is
    /// synchronous, so the async call runs it on the thread pool and a sync never blocks a frame.
    /// TODO(#143): the ApiClient forwards every response's X-Server-Time to ServerClock.Observe.
    /// </summary>
    public sealed class RemoteTimeApi : ITimeApi
    {
        readonly RemoteClient _client;

        public RemoteTimeApi(RemoteClient client) { _client = client ?? throw new ArgumentNullException(nameof(client)); }

        public Task<long> GetServerTimeAsync(CancellationToken ct) => Task.Run(GetServerTime, ct);

        /// <summary>Fetches server time. Throws ServiceException (ServerError "BadResponse" for an unreadable body).</summary>
        public long GetServerTime()
        {
            string body = _client.Send("GET", RemoteRoutes.Time);
            try { return JObject.Parse(body ?? "")["serverTime"].Value<long>(); }
            catch (Exception e) when (!(e is ServiceException))
            {
                throw new ServiceException(ErrorClass.ServerError, "BadResponse", "time: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Local-first cloud save (specs/08 section 9: a save never fails because the network did). Every operation
    /// goes to the local store; <see cref="Write"/> also queues the bytes, and <see cref="Upload"/> sends the
    /// latest queued copy. Cloud download and the conflict rule (section 5.2) are P1.
    /// </summary>
    public sealed class CloudSaveStore : ISaveSlotStore
    {
        readonly ISaveSlotStore _local;
        readonly RemoteClient _client;

        public CloudSaveStore(ISaveSlotStore local, RemoteClient client)
        {
            _local = local ?? throw new ArgumentNullException(nameof(local));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public ISaveSlotStore Local => _local;

        /// <summary>Newest bytes written locally and not yet accepted by the server; null when in sync.</summary>
        public byte[] PendingUpload { get; private set; }

        public byte[] Read(SaveSlot slot) => _local.Read(slot);

        public void Write(byte[] bytes)
        {
            _local.Write(bytes);
            PendingUpload = bytes;
        }

        public void Quarantine(SaveSlot slot, long nowServerSec) => _local.Quarantine(slot, nowServerSec);

        public void ArchiveConflict(byte[] bytes, long nowServerSec) => _local.ArchiveConflict(bytes, nowServerSec);

        public void DeleteStrayTemp() => _local.DeleteStrayTemp();

        public void Purge(long nowServerSec, long corruptMaxAgeSec, long conflictMaxAgeSec) =>
            _local.Purge(nowServerSec, corruptMaxAgeSec, conflictMaxAgeSec);

        public void DeleteAll()
        {
            _local.DeleteAll();
            PendingUpload = null;
        }

        /// <summary>Uploads the queued copy. Throws ServiceException (Conflict when the cloud copy is newer); the
        /// copy stays queued on failure.</summary>
        public void Upload()
        {
            var bytes = PendingUpload;
            if (bytes == null) return;
            _client.Send("PUT", RemoteRoutes.Save, new JObject { ["bytes"] = Convert.ToBase64String(bytes) }.ToString());
            if (ReferenceEquals(PendingUpload, bytes)) PendingUpload = null;
        }

        public bool TryUpload()
        {
            try { Upload(); return true; }
            catch (ServiceException) { return false; }
        }
    }
}
