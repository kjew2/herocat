using System;
using System.IO;
using HeroCat.Remote;
using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    public class RemoteServicesTests
    {
        [Test]
        public void ServerClock_uses_device_time_until_synced_then_runs_on_the_monotonic_clock()   // issue #149
        {
            var device = new FakeClock(1000);
            var mono = new FakeMonoClock();
            var t = new FakeTransport().On("GET", RemoteRoutes.Time, 200, "{\"serverTime\":5000}");
            var clock = new ServerClock(new RemoteTimeApi(new RemoteClient(t)), device, mono);

            Assert.That(clock.NowServerSeconds, Is.EqualTo(1000));
            Assert.That(clock.IsAuthoritative, Is.False);

            Assert.That(clock.SyncAsync().GetAwaiter().GetResult(), Is.True);
            Assert.That(clock.IsAuthoritative, Is.True);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(5000));
            device.Advance(86400);   // a device clock change after the sync has no effect
            mono.Advance(60);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(5060));
        }

        [Test]
        public void ServerClock_offline_stays_on_device_time()
        {
            var api = new RemoteTimeApi(new RemoteClient(OfflineTransport.Instance));
            var clock = new ServerClock(api, new FakeClock(1000), new FakeMonoClock());
            Assert.That(clock.SyncAsync().GetAwaiter().GetResult(), Is.False);
            Assert.That(Assert.Throws<ServiceException>(() => api.GetServerTime()).Kind, Is.EqualTo(ErrorClass.Offline));
            Assert.That(clock.NowServerSeconds, Is.EqualTo(1000));
            Assert.That(clock.IsAuthoritative, Is.False);
        }

        [Test]
        public void TimeApi_bad_body_is_Internal()
        {
            foreach (var body in new[] { "not json", "{\"serverSeconds\":5000}" })
            {
                var t = new FakeTransport().On("GET", RemoteRoutes.Time, 200, body);
                var e = Assert.Throws<ServiceException>(() => new RemoteTimeApi(new RemoteClient(t)).GetServerTime(), body);
                Assert.That(e.Kind, Is.EqualTo(ErrorClass.ServerError));
                Assert.That(e.Code, Is.EqualTo("BadResponse"));
            }
        }

        [Test]
        public void CloudSave_writes_locally_even_when_offline_and_keeps_upload_queued()
        {
            var local = new MemorySaveStore();
            var t = new FakeTransport { FailWith = TransportFailure.Unreachable };
            var store = new CloudSaveStore(local, new RemoteClient(t));
            var bytes = new byte[] { 1, 2, 3 };

            store.Write(bytes);
            Assert.That(store.Read(SaveSlot.Primary), Is.EqualTo(bytes));
            Assert.That(store.TryUpload(), Is.False);
            Assert.That(store.PendingUpload, Is.EqualTo(bytes));

            t.FailWith = null;
            t.On("PUT", RemoteRoutes.Save, 204);
            Assert.That(store.TryUpload(), Is.True);
            Assert.That(store.PendingUpload, Is.Null);
            var sent = JObject.Parse(t.Requests[t.Requests.Count - 1].Body)["bytes"].Value<string>();
            Assert.That(Convert.FromBase64String(sent), Is.EqualTo(bytes));
        }

        [Test]
        public void CloudSave_conflict_surfaces_as_Conflict()
        {
            var t = new FakeTransport().On("PUT", RemoteRoutes.Save, 409, "{\"code\":\"CloudNewer\"}");
            var store = new CloudSaveStore(new MemorySaveStore(), new RemoteClient(t));
            store.Write(new byte[] { 9 });
            var e = Assert.Throws<ServiceException>(store.Upload);
            Assert.That(e.Kind, Is.EqualTo(ErrorClass.Conflict));
            Assert.That(e.Code, Is.EqualTo("CloudNewer"));
            Assert.That(store.PendingUpload, Is.Not.Null);
        }

        [Test]
        public void Remote_factory_wires_stubs_and_runs_offline()
        {
            var dir = Path.Combine(Path.GetTempPath(), "herocat-remote-" + Guid.NewGuid().ToString("N"));
            try
            {
                var s = RemoteGameServices.Create(dir, OfflineTransport.Instance);
                Assert.That(s.Clock, Is.TypeOf<ServerClock>());
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
    }
}
