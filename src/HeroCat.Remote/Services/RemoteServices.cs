using System;
using System.IO;
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
        public const string Time = "/time";   // GET  -> {"serverSeconds": 1700000000}
        public const string Save = "/save";   // PUT  {"bytes": "<base64>"}; 409 when the cloud copy is newer
    }

    /// <summary>
    /// The remote half of the composition root (01 section 1.2), supplied to GameServices.Build through
    /// ServicesOptions.Remote because HeroCat.Runtime cannot reference this assembly. A stub over the vendor-neutral
    /// transport spike: server clock and local-first cloud save; the other seams keep their local implementations.
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

        /// <summary>A ServicesOptions.Remote factory over <paramref name="transport"/>.</summary>
        public static GameServices Wire(ServicesOptions o, IRemoteTransport transport, RemoteOptions options = null)
        {
            var client = new RemoteClient(transport, options);
            var local = GameServices.Build(BackendMode.Local, new ServicesOptions
            {
                DataDir = o.DataDir, Content = o.Content, State = o.State, Analytics = o.Analytics, Network = o.Network,
            });
            return local with
            {
                Clock = new ServerClock(client, o.Clock ?? new SystemClock()),
                Saves = new CloudSaveStore(o.Saves ?? new FileSaveStore(Path.Combine(o.DataDir, "saves")), client),
            };
        }
    }

    /// <summary>
    /// specs/08 section 3.1 shape: device time plus an offset learned from the server. Never throws when read, so
    /// the game runs offline; <see cref="IsAuthoritative"/> is true only after a successful <see cref="Sync"/>.
    /// </summary>
    public sealed class ServerClock : IClock
    {
        readonly RemoteClient _client;
        readonly IClock _device;
        long _offsetSeconds;

        public ServerClock(RemoteClient client, IClock device)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        public long NowServerSeconds => _device.NowServerSeconds + _offsetSeconds;
        public bool IsAuthoritative { get; private set; }

        /// <summary>Fetches server time. Throws ServiceException; the previous offset is kept on failure.</summary>
        public void Sync()
        {
            string body = _client.Send("GET", RemoteRoutes.Time);
            long server;
            try { server = JObject.Parse(body ?? "")["serverSeconds"].Value<long>(); }
            catch (Exception e) when (!(e is ServiceException))
            {
                throw new ServiceException(ErrorClass.ServerError, "BadResponse", "time: " + e.Message);
            }
            _offsetSeconds = server - _device.NowServerSeconds;
            IsAuthoritative = true;
        }

        public bool TrySync()
        {
            try { Sync(); return true; }
            catch (ServiceException) { return false; }
        }
    }

    /// <summary>
    /// Local-first cloud save (specs/08 section 9: a save never fails because the network did). Every operation
    /// goes to the local store; <see cref="Write"/> also queues the bytes, and <see cref="Upload"/> sends the
    /// latest queued copy. Cloud download and the conflict rule (section 5.2) are P1.
    /// </summary>
    public sealed class CloudSaveStore : ISaveStore
    {
        readonly ISaveStore _local;
        readonly RemoteClient _client;

        public CloudSaveStore(ISaveStore local, RemoteClient client)
        {
            _local = local ?? throw new ArgumentNullException(nameof(local));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public ISaveStore Local => _local;

        /// <summary>Newest bytes written locally and not yet accepted by the server; null when in sync.</summary>
        public byte[] PendingUpload { get; private set; }

        public byte[] Read(SaveSlot slot) => _local.Read(slot);

        public void Write(byte[] bytes)
        {
            _local.Write(bytes);
            PendingUpload = bytes;
        }

        public void Quarantine(SaveSlot slot, long nowServerSec) => _local.Quarantine(slot, nowServerSec);

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
