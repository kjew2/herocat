using System;
using System.IO;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Save;
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
    /// The P1 remote composition root (specs/08 section 1). GameServices lives in HeroCat.Runtime, which cannot
    /// reference this assembly, so the specs' <c>Services.Remote(...)</c> is this static factory.
    /// </summary>
    public static class RemoteGameServices
    {
        /// <summary>
        /// Server clock (falls back to the device clock until synced), local-first cloud save under {dataDir}/saves.
        /// TODO(#145): the server summon path (SummonClient). Pass <see cref="OfflineTransport.Instance"/> until a vendor transport exists.
        /// </summary>
        public static GameServices Create(string dataDir, IRemoteTransport transport, RemoteOptions options = null,
                                          IAnalytics analytics = null)
        {
            if (dataDir == null) throw new ArgumentNullException(nameof(dataDir));
            var client = new RemoteClient(transport, options);
            return new GameServices
            {
                Clock = new ServerClock(client, new SystemClock()),
                SaveStore = new CloudSaveStore(new FileSaveStore(Path.Combine(dataDir, "saves")), client),
                Analytics = analytics ?? NullAnalytics.Instance,
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
                throw new ServiceException(ServiceErrorKind.Internal, "BadResponse", "time: " + e.Message);
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
