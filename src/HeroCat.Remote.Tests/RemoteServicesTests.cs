using System;
using System.IO;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    public class RemoteServicesTests
    {
        static ApiClientOptions Options(System.Net.Http.HttpMessageHandler handler, FakeMonoClock mono = null) => new ApiClientOptions
        {
            BaseUrl = "https://api.test.whiskerhero.example/v1",
            Handler = handler,
            ClientVersion = "1.0.0",
            Mono = mono,
            Delay = (d, ct) => Task.CompletedTask,
        };

        [Test]
        public void ServerClock_UsesDeviceTimeUntilSynced_ThenRunsOnTheMonotonicClock()
        {
            var device = new FakeClock(1000);
            var mono = new FakeMonoClock();
            var server = new FakeApiHandler { ServerTime = () => 5000 }.On("GET", RemoteRoutes.Time, 200, new { serverTime = 5000 });
            var clock = new ServerClock(new RemoteTimeApi(new ApiClient(Options(server, mono))), device, mono);

            Assert.That(clock.NowServerSeconds, Is.EqualTo(1000));
            Assert.That(clock.IsAuthoritative, Is.False);

            Assert.That(clock.SyncAsync().GetAwaiter().GetResult(), Is.True);
            Assert.That(clock.IsAuthoritative, Is.True);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(5000));
            device.Advance(86400);   // a device clock change after the sync has no effect
            mono.Advance(60);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(5060));
            Assert.That(server.Calls[0].Header("Authorization"), Is.Null, "GET /time is auth none");
        }

        [Test]
        public void ServerClock_Offline_StaysOnDeviceTime_AndTheTimeCallIsNotRetried()
        {
            var api = new RemoteTimeApi(new ApiClient(Options(new OfflineHandler())));
            var clock = new ServerClock(api, new FakeClock(1000), new FakeMonoClock());
            Assert.That(clock.SyncAsync().GetAwaiter().GetResult(), Is.False);
            var e = Assert.ThrowsAsync<ServiceException>(() => api.GetServerTimeAsync(default));
            Assert.That(e.Kind, Is.EqualTo(ErrorClass.Offline));
            Assert.That(e.Code, Is.EqualTo(ErrorClassifier.NET_OFFLINE));
            Assert.That(clock.NowServerSeconds, Is.EqualTo(1000));
            Assert.That(clock.IsAuthoritative, Is.False);

            var server = new FakeApiHandler().Then(FakeFault.Drop);
            Assert.ThrowsAsync<ServiceException>(() => new RemoteTimeApi(new ApiClient(Options(server))).GetServerTimeAsync(default));
            Assert.That(server.Calls, Has.Count.EqualTo(1), "NoRetry: the clock has its own cadence");
        }

        [Test]
        public void TimeApi_BadBody_IsServerError()
        {
            foreach (var body in new[] { "\"not an object\"", "{\"serverSeconds\":5000}" })
            {
                var server = new FakeApiHandler().On("GET", RemoteRoutes.Time, _ => new FakeReply(200, body));
                var e = Assert.ThrowsAsync<ServiceException>(() => new RemoteTimeApi(new ApiClient(Options(server))).GetServerTimeAsync(default), body);
                Assert.That(e.Kind, Is.EqualTo(ErrorClass.ServerError));
                Assert.That(e.Code, Is.EqualTo(ErrorClassifier.SRV_UNEXPECTED));
            }
        }

        [Test]
        public void CloudSave_WritesLocallyEvenWhenOffline_AndKeepsTheUploadQueuedUnderOneRequestId()
        {
            var local = new MemorySaveStore();
            var server = new FakeApiHandler();
            server.Then(FakeFault.Drop, FakeFault.Drop, FakeFault.Drop, FakeFault.Drop, FakeFault.Drop);   // the first upload's 5 attempts
            var store = new CloudSaveStore(local, new ApiClient(Options(server)));
            var bytes = new byte[] { 1, 2, 3 };

            store.Write(bytes);
            Assert.That(store.Read(SaveSlot.Primary), Is.EqualTo(bytes));
            Assert.That(store.TryUploadAsync().GetAwaiter().GetResult(), Is.False);
            Assert.That(store.PendingUpload, Is.EqualTo(bytes));

            server.On("PUT", RemoteRoutes.Save, 200, new { etag = "e1" });
            Assert.That(store.TryUploadAsync().GetAwaiter().GetResult(), Is.True);
            Assert.That(store.PendingUpload, Is.Null);
            var calls = server.Calls;
            var last = calls[calls.Count - 1];
            Assert.That(last.Body, Is.EqualTo(bytes));
            Assert.That(last.ContentType, Is.EqualTo("application/octet-stream"));
            Assert.That(last.Header("X-Request-Id"), Is.EqualTo(calls[0].Header("X-Request-Id")), "the same blob keeps its request id");

            store.Write(new byte[] { 4 });
            store.TryUploadAsync().GetAwaiter().GetResult();
            calls = server.Calls;
            Assert.That(calls[calls.Count - 1].Header("X-Request-Id"), Is.Not.EqualTo(last.Header("X-Request-Id")), "a new blob, a new id");
        }

        [Test]
        public void CloudSave_ConflictSurfacesAsConflict()
        {
            var server = new FakeApiHandler().On("PUT", RemoteRoutes.Save, _ => FakeReply.Error(409, "SAVE_CONFLICT"));
            var store = new CloudSaveStore(new MemorySaveStore(), new ApiClient(Options(server)));
            store.Write(new byte[] { 9 });
            var e = Assert.ThrowsAsync<ServiceException>(() => store.UploadAsync());
            Assert.That(e.Kind, Is.EqualTo(ErrorClass.Conflict));
            Assert.That(e.Code, Is.EqualTo("SAVE_CONFLICT"));
            Assert.That(store.PendingUpload, Is.Not.Null);
        }

        [Test]
        public void RemoteFactory_WiresTheApiClient_AndRunsOffline()
        {
            var dir = Path.Combine(Path.GetTempPath(), "herocat-remote-" + Guid.NewGuid().ToString("N"));
            try
            {
                var api = Options(new OfflineHandler());
                var s = RemoteGameServices.Create(dir, api);
                Assert.That(s.Clock, Is.TypeOf<ServerClock>());
                Assert.That(api.ServerTime, Is.SameAs(s.Clock), "every X-Server-Time reaches the clock");
                Assert.That(api.DeviceId, Is.EqualTo(s.InstallId.ToString("D")), "X-Device-Id is the install id");
                Assert.That(s.Saves, Is.TypeOf<LocalSaveStore>());
                Assert.That(s.Slots, Is.TypeOf<CloudSaveStore>());
                Assert.That(s.Analytics, Is.SameAs(NullAnalytics.Instance));
                Assert.That(s.Mode, Is.EqualTo(BackendMode.Remote));
                Assert.That(s.Inbox, Is.Not.Null);
                Assert.That(s.Dungeons, Is.Not.Null);

                Assert.That(s.Clock.IsAuthoritative, Is.False);
                s.Slots.Write(new byte[] { 7 });
                Assert.That(File.Exists(Path.Combine(dir, "saves", "slot0.sav")), Is.True);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void RemoteWire_SyncNow_AnchorsTheClockBeforeAnythingReadsIt()
        {
            var server = new FakeApiHandler { ServerTime = () => 7000 }.On("GET", RemoteRoutes.Time, 200, new { serverTime = 7000 });
            var s = RemoteGameServices.Wire(new ServicesOptions { Slots = new MemorySaveStore(), InstallId = Guid.NewGuid() },
                                            Options(server), syncNow: true);
            Assert.That(s.Clock.IsAuthoritative, Is.True);
            Assert.That(s.Clock.NowServerSeconds, Is.GreaterThanOrEqualTo(7000));
        }
    }
}
