using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>
    /// 02 section 11.1 ServerClock (issue #149): the monotonic offset, the floor, Observe, SyncAsync over a time API, the
    /// CLOCK_RESYNC_SEC cadence and clock_anomaly{resync}; acceptance 14. Every clock is fake, so nothing sleeps.
    /// </summary>
    public class ServerClockTests
    {
        static long T0 => TestContent.T0;
        static long Day => TestContent.Db.Constants.DaySeconds;

        /// <summary>A time API whose server clock is a function of the fake monotonic clock.</summary>
        sealed class FakeTimeApi : ITimeApi
        {
            public Func<long> Server;
            public FakeMonoClock Mono;
            public double LatencySec;
            public Exception Fail;
            public TaskCompletionSource<long> Hold;
            public int Calls;

            public Task<long> GetServerTimeAsync(CancellationToken ct)
            {
                Calls++;
                if (Fail != null) throw Fail;
                if (Hold != null) return Hold.Task;
                Mono?.Advance(LatencySec / 2);   // the server stamps its time halfway through the round trip
                long server = Server();
                Mono?.Advance(LatencySec / 2);
                return Task.FromResult(server);
            }
        }

        static long MonoSec(FakeMonoClock mono) => mono.Ticks / FakeMonoClock.TicksPerSecond;

        // ------------------------------------------------------------------------------------------- acceptance 14

        [Test]
        public void Acceptance14_Observe_MakesItAuthoritative_AndADeviceClockChangeMovesNowByTheMonotonicElapsedOnly()
        {
            var device = new FakeClock(T0);
            var mono = new FakeMonoClock();
            var clock = new ServerClock(device: device, mono: mono);
            Assert.That(clock.IsAuthoritative, Is.False);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0), "the device clock before any observation");

            clock.Observe(T0 + 100, 0);
            Assert.That(clock.IsAuthoritative, Is.True);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 100));

            device.Advance(Day);   // +1 day on the device between observations
            mono.Advance(30);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 130), "only the monotonic 30 s");
            device.Set(T0 - Day);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 130), "a device clock set back has no effect either");

            clock.Observe(T0 + 130, 0);
            mono.Advance(5);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 135));
        }

        [Test]
        public void Acceptance14_BeforeAnyObservation_NowIsAtLeastTheFloor_AndTheFloorOnlyRises()
        {
            var device = new FakeClock(T0);
            var mono = new FakeMonoClock();
            var clock = new ServerClock(device: device, mono: mono);
            clock.SetFloor(T0 + 1000);   // the device clock is behind the last save
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 1000));
            clock.SetFloor(T0);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 1000), "never lowered");
            device.Set(T0 + 2000);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 2000), "device time once it passes the floor");
            Assert.That(clock.IsAuthoritative, Is.False);

            clock.Observe(T0 + 500, 0);   // a server behind the last save (a save stamped by a device clock ahead)
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 1000), "time never reads below the floor");
            mono.Advance(600);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 1100));
        }

        [Test]
        public void Observe_AddsHalfTheRoundTrip_AndIgnoresANegativeOne()
        {
            var clock = new ServerClock(device: new FakeClock(T0), mono: new FakeMonoClock());
            clock.Observe(T0, 4000);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 2));
            clock.Observe(T0, -4000);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0));
        }

        [Test]
        public void Observe_RaisesResynced_WithTheExtrapolatedAndTheSyncedTime()
        {
            var mono = new FakeMonoClock();
            var clock = new ServerClock(device: new FakeClock(T0), mono: mono);
            var seen = new List<(long, long)>();
            clock.Resynced += (e, s) => seen.Add((e, s));
            clock.Observe(T0 + 10, 0);
            mono.Advance(60);
            clock.Observe(T0 + 10 + 60 + 400, 0);
            Assert.That(seen, Is.EqualTo(new[] { (T0, T0 + 10), (T0 + 70, T0 + 470) }));
        }

        // ------------------------------------------------------------------------------------------- SyncAsync

        [Test]
        public void LocalMode_NeverSyncs_AndIsNeverDue()
        {
            var mono = new FakeMonoClock();
            var clock = new ServerClock(device: new FakeClock(T0), mono: mono);
            Assert.That(clock.SyncAsync().GetAwaiter().GetResult(), Is.False);
            mono.Advance(10 * SaveConfig.CLOCK_RESYNC_SEC);
            Assert.That(clock.ResyncDue, Is.False);
            Assert.That(clock.IsAuthoritative, Is.False);
        }

        [Test]
        public void SyncAsync_ObservesTheServerTime_CorrectedByTheMeasuredRoundTrip()
        {
            var mono = new FakeMonoClock();
            mono.Advance(1000);
            var api = new FakeTimeApi { Mono = mono, LatencySec = 4, Server = () => T0 + 50 + MonoSec(mono) };
            var clock = new ServerClock(api, new FakeClock(T0 - Day), mono);
            Assert.That(clock.SyncAsync().GetAwaiter().GetResult(), Is.True);
            Assert.That(api.Calls, Is.EqualTo(1));
            Assert.That(clock.IsAuthoritative, Is.True);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 50 + MonoSec(mono)), "the server's now, not the stamp's");
        }

        [Test]
        public void SyncAsync_Failure_KeepsTheOffset_AndNeverThrows()
        {
            var mono = new FakeMonoClock();
            var api = new FakeTimeApi { Mono = mono, Server = () => T0 + 50 + MonoSec(mono) };
            var clock = new ServerClock(api, new FakeClock(T0), mono);
            Assert.That(clock.SyncAsync().GetAwaiter().GetResult(), Is.True);

            api.Fail = new InvalidOperationException("thrown before any task");
            mono.Advance(10);
            Assert.That(clock.SyncAsync().GetAwaiter().GetResult(), Is.False);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 60), "the old offset");

            api.Fail = null;
            api.Hold = new TaskCompletionSource<long>();
            var faulted = clock.SyncAsync();
            api.Hold.SetException(new TimeoutException());
            Assert.That(faulted.GetAwaiter().GetResult(), Is.False);

            api.Hold = new TaskCompletionSource<long>();
            var cancelled = clock.SyncAsync();
            api.Hold.SetCanceled();
            Assert.That(cancelled.GetAwaiter().GetResult(), Is.False);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 60));
            Assert.That(clock.IsAuthoritative, Is.True);
        }

        [Test]
        public void SyncAsync_WhileOneIsInFlight_JoinsIt()
        {
            var mono = new FakeMonoClock();
            var api = new FakeTimeApi { Hold = new TaskCompletionSource<long>() };
            var clock = new ServerClock(api, new FakeClock(T0), mono);
            var a = clock.SyncAsync();
            var b = clock.SyncAsync();
            Assert.That(b, Is.SameAs(a));
            Assert.That(api.Calls, Is.EqualTo(1));
            mono.Advance(2 * SaveConfig.CLOCK_RESYNC_SEC);
            Assert.That(clock.ResyncDue, Is.False, "not due while a sync is in flight");

            api.Hold.SetResult(T0 + 10);
            Assert.That(a.GetAwaiter().GetResult(), Is.True);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(T0 + 10 + SaveConfig.CLOCK_RESYNC_SEC), "plus half the long round trip");
            api.Hold = new TaskCompletionSource<long>();
            Assert.That(clock.SyncAsync(), Is.Not.SameAs(a), "a new sync once the first landed");
            Assert.That(api.Calls, Is.EqualTo(2));
        }

        [Test]
        public void ResyncDue_AfterClockResyncSecWithoutAnObservation_AnyObservationOrAttemptRestartsIt()
        {
            var mono = new FakeMonoClock();
            var api = new FakeTimeApi { Mono = mono, Server = () => T0 + MonoSec(mono) };
            var clock = new ServerClock(api, new FakeClock(T0), mono);
            Assert.That(clock.ResyncDue, Is.False);
            mono.Advance(SaveConfig.CLOCK_RESYNC_SEC - 1);
            Assert.That(clock.ResyncDue, Is.False);
            clock.Observe(T0 + SaveConfig.CLOCK_RESYNC_SEC - 1, 0);   // other API traffic carried X-Server-Time
            mono.Advance(SaveConfig.CLOCK_RESYNC_SEC - 1);
            Assert.That(clock.ResyncDue, Is.False);
            mono.Advance(1);
            Assert.That(clock.ResyncDue, Is.True);

            api.Fail = new InvalidOperationException("offline");
            Assert.That(clock.SyncAsync().GetAwaiter().GetResult(), Is.False);
            Assert.That(clock.ResyncDue, Is.False, "a failed attempt waits a full interval, not a frame");
            mono.Advance(SaveConfig.CLOCK_RESYNC_SEC);
            Assert.That(clock.ResyncDue, Is.True);
        }

        // ------------------------------------------------------------------------------------------- in a session

        [Test]
        public void Session_SyncsAtBootAndResume_AndEveryClockResyncSecWithoutTraffic()
        {
            var mono = new FakeMonoClock();
            var device = new FakeClock(T0);
            var api = new FakeTimeApi { Mono = mono, Server = () => T0 + MonoSec(mono) };
            var clock = new ServerClock(api, device, mono);
            var t = TestSession.Open(device, clockOverride: clock, mono: mono);
            Assert.That(api.Calls, Is.EqualTo(1), "fired at boot");
            Assert.That(clock.IsAuthoritative, Is.True);

            t.Play(SaveConfig.CLOCK_RESYNC_SEC - 10, 5);
            Assert.That(api.Calls, Is.EqualTo(1));
            t.Play(15, 5);
            Assert.That(api.Calls, Is.EqualTo(2), "CLOCK_RESYNC_SEC of foreground time without traffic");

            t.Runner.OnPause();
            mono.Advance(3600);
            t.Runner.OnResume();
            Assert.That(api.Calls, Is.EqualTo(3), "fired at resume");
            Assert.That(t.Analytics.Count("clock_anomaly"), Is.EqualTo(0), "the server agreed with the extrapolation");
        }

        [Test]
        public void Session_AResyncMovingTheOffsetPastClockJumpWarnSec_EmitsExactlyOneClockAnomaly()
        {
            var mono = new FakeMonoClock();
            var device = new FakeClock(T0);
            long skew = 0;
            var api = new FakeTimeApi { Mono = mono, Server = () => T0 + MonoSec(mono) + skew };
            var clock = new ServerClock(api, device, mono);
            var t = TestSession.Open(device, clockOverride: clock, mono: mono);
            t.Play(SaveConfig.CLOCK_RESYNC_SEC, 5);
            Assert.That(api.Calls, Is.EqualTo(2));
            Assert.That(t.Analytics.Count("clock_anomaly"), Is.EqualTo(0));

            skew = 3600;   // the server is an hour ahead of the extrapolation
            t.Play(SaveConfig.CLOCK_RESYNC_SEC, 5);   // the resync fires on the last frame
            Assert.That(api.Calls, Is.EqualTo(3));
            Assert.That(t.Analytics.Count("clock_anomaly"), Is.EqualTo(0), "its result is handled on the next frame");
            t.Frame(5);
            var anomaly = t.Analytics.Events.Single(e => e.Name == "clock_anomaly");
            Assert.That(anomaly.Props["kind"], Is.EqualTo("resync"));
            Assert.That(anomaly.Props["seconds"], Is.EqualTo(3600L));
            Assert.That(t.Sim.State.Offline.Pending, Is.Not.Null, "the second catch-up window");
            Assert.That(t.Sim.State.Offline.Pending.ElapsedSec, Is.EqualTo(3600).Within(5));

            t.Play(SaveConfig.CLOCK_RESYNC_SEC, 5);   // the next resync agrees
            skew += RuntimeConfig.CLOCK_JUMP_WARN_SEC;   // and one after it moves by exactly the threshold
            t.Play(SaveConfig.CLOCK_RESYNC_SEC, 5);
            Assert.That(api.Calls, Is.EqualTo(5));
            Assert.That(t.Analytics.Count("clock_anomaly"), Is.EqualTo(1), "exactly one");
        }
    }
}
