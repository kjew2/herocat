using System;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Session;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// The server time source behind <see cref="ServerClock.SyncAsync"/> (02 section 11.1): <c>GET /time</c> (08 section 2.2).
    /// Null in Local mode. Implementations throw on any failure; ServerClock turns that into a failed sync.
    /// </summary>
    public interface ITimeApi
    {
        /// <summary>Server unix seconds, UTC.</summary>
        Task<long> GetServerTimeAsync(CancellationToken ct);
    }

    /// <summary>
    /// Device time plus a debug offset, floored at <c>Time.LastSavedServerTime</c> of the loaded save so time never reads
    /// below the last save, and never authoritative (02 section 11.2 rule 4; issue #78). <see cref="ServerClock"/> reads it
    /// as its device clock until the first server observation. This file and SessionRunner.cs are the only files in
    /// HeroCat.Runtime allowed to read system time (02 acceptance 18, enforced by tools/check_clock_apis.py).
    /// </summary>
    public sealed class SystemClock : IClock, IClockFloor
    {
        long _floor;

        public long OffsetSeconds { get; set; }

        /// <summary>The raw device reading (unix seconds), replaceable in tests to simulate a device clock set back.</summary>
        public Func<long> DeviceSeconds { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public long NowServerSeconds => Math.Max(_floor, DeviceSeconds() + OffsetSeconds);
        public bool IsAuthoritative => false;

        public long Floor => _floor;

        /// <summary>Raises the floor, never lowers it: a second load in the same process cannot move time backwards.</summary>
        public void SetFloor(long lastSavedServerTime)
        {
            if (lastSavedServerTime > _floor) _floor = lastSavedServerTime;
        }
    }

    /// <summary>
    /// 02 section 11.1's ServerClock (issues #66, #149). Before any observation it serves the device clock floored at the
    /// last save (<see cref="SetFloor"/>, 11.2 rule 4) and <see cref="IsAuthoritative"/> is false. <see cref="Observe"/>
    /// (the ApiClient's hook for every X-Server-Time, 01 section 5.1, and the end of <see cref="SyncAsync"/>) anchors it:
    /// from then on time runs on the monotonic clock from that offset, so a device clock change has no effect (11.2 rule 3),
    /// and it never reads below the floor. Each observation raises <see cref="Resynced"/>; SessionRunner turns a move over
    /// CLOCK_JUMP_WARN_SEC into clock_anomaly{resync} and the second catch-up window (01 section 8.2 step 2). SessionRunner
    /// also fires <see cref="SyncAsync"/> at boot and resume and whenever <see cref="ResyncDue"/> (CLOCK_RESYNC_SEC without
    /// an observation). With no <see cref="ITimeApi"/> (Local mode) it never syncs. Thread-safe.
    /// </summary>
    public sealed class ServerClock : IClock, IClockFloor, IClockResync, IServerTimeObserver
    {
        static readonly Task<bool> NoSync = Task.FromResult(false);

        readonly ITimeApi _api;
        readonly IClock _device;
        readonly IMonoClock _mono;
        readonly object _lock = new object();
        long? _offsetSec;      // serverNow - monotonic seconds at the last observation
        long _floorSec;        // Time.LastSavedServerTime of the loaded save; time never reads below it
        long _lastSyncTicks;   // monotonic ticks of the last observation or sync attempt (the CLOCK_RESYNC_SEC cadence)
        Task<bool> _inFlight;

        /// <param name="api">The time API; null in Local mode (no syncs, never authoritative unless observed).</param>
        /// <param name="device">The device clock served before the first observation; default <see cref="SystemClock"/>.</param>
        /// <param name="mono">The monotonic source; default Stopwatch. Tests pass a FakeMonoClock so they never sleep.</param>
        public ServerClock(ITimeApi api = null, IClock device = null, IMonoClock mono = null)
        {
            _api = api;
            _device = device ?? new SystemClock();
            _mono = mono ?? StopwatchMonoClock.Instance;
            _lastSyncTicks = _mono.Ticks;
        }

        public bool IsAuthoritative
        {
            get { lock (_lock) return _offsetSec.HasValue; }
        }

        public long NowServerSeconds
        {
            get
            {
                lock (_lock) return Math.Max(_floorSec, _offsetSec is long o ? MonoSeconds() + o : _device.NowServerSeconds);
            }
        }

        /// <summary>Boot step 4: the loaded save's LastSavedServerTime. Only rises.</summary>
        public void SetFloor(long lastSavedServerTime)
        {
            lock (_lock)
            {
                if (lastSavedServerTime > _floorSec) _floorSec = lastSavedServerTime;
            }
        }

        /// <summary>
        /// A server time observation: <paramref name="serverTimeSec"/> plus half the round trip becomes the new offset from
        /// the monotonic clock, and the cadence restarts (any API call counts as a sync). Raises <see cref="Resynced"/>.
        /// </summary>
        public void Observe(long serverTimeSec, long rttMs)
        {
            long extrapolated, synced;
            lock (_lock)
            {
                extrapolated = NowServerSeconds;
                _offsetSec = serverTimeSec + Math.Max(0, rttMs) / 2000 - MonoSeconds();
                _lastSyncTicks = _mono.Ticks;
                synced = NowServerSeconds;
            }
            Resynced?.Invoke(extrapolated, synced);
        }

        /// <summary>After every observation: (what the clock read just before, what it reads now). Any thread.</summary>
        public event Action<long, long> Resynced;

        /// <summary>
        /// True when a time API exists, no sync is in flight and CLOCK_RESYNC_SEC of monotonic time passed since the last
        /// observation or sync attempt (a failed attempt waits a full interval too, so an offline device does not retry
        /// every frame).
        /// </summary>
        public bool ResyncDue
        {
            get
            {
                if (_api == null) return false;
                lock (_lock) return _inFlight == null && _mono.Ticks - _lastSyncTicks >= SaveConfig.CLOCK_RESYNC_SEC * _mono.Frequency;
            }
        }

        /// <summary>
        /// <c>GET /time</c>, then <see cref="Observe"/> with the measured round trip. True when an observation landed; false
        /// in Local mode, on any failure (the previous offset is kept) or on cancellation. Never throws, so callers fire and
        /// forget. A call while one is in flight joins it. The result may land on any thread.
        /// </summary>
        public Task<bool> SyncAsync(CancellationToken ct = default)
        {
            if (_api == null) return NoSync;
            lock (_lock)
            {
                if (_inFlight != null) return _inFlight;
                _lastSyncTicks = _mono.Ticks;
                var task = RunSync(ct);
                // A sync that completed inline has already cleared _inFlight; one still running clears it when done (it
                // needs this lock to do so, so it cannot finish between this check and the assignment).
                if (!task.IsCompleted) _inFlight = task;
                return task;
            }
        }

        async Task<bool> RunSync(CancellationToken ct)
        {
            try
            {
                long t0 = _mono.Ticks;
                long server = await _api.GetServerTimeAsync(ct).ConfigureAwait(false);
                long rttMs = (long)((_mono.Ticks - t0) * 1000.0 / _mono.Frequency);
                Observe(server, rttMs);
                return true;
            }
            catch (Exception)
            {
                return false;   // offline, timeout, bad response or cancelled: the clock keeps its offset
            }
            finally
            {
                lock (_lock) _inFlight = null;
            }
        }

        long MonoSeconds() => _mono.Ticks / _mono.Frequency;
    }

    /// <summary>
    /// Where the ApiClient reports the <c>X-Server-Time</c> of every response, success or error, with the measured round
    /// trip (01 section 5.1). <see cref="ServerClock"/> implements it; HeroCat.Remote's ApiClient holds it, so Remote code
    /// needs no reference to the concrete clock (a debug wrapper such as the client's OffsetClock stays outside). Any thread.
    /// </summary>
    public interface IServerTimeObserver
    {
        /// <summary>A server time observation: unix seconds as stamped by the server, and the request's round trip in ms.</summary>
        void Observe(long serverTimeSec, long rttMs);
    }

    /// <summary>
    /// A clock whose server syncs can move it (01 section 8.2 step 2). <see cref="Resynced"/> fires after each sync with the
    /// time the clock extrapolated just before it and the synced time, possibly off the main thread; SessionRunner
    /// subscribes and handles a jump over CLOCK_JUMP_WARN_SEC on its next frame (issue #65). SessionRunner fires
    /// <see cref="SyncAsync"/> (not awaited) at boot and resume and whenever <see cref="ResyncDue"/> (issue #149).
    /// </summary>
    public interface IClockResync
    {
        event Action<long, long> Resynced;

        /// <summary>CLOCK_RESYNC_SEC passed without a sync; polled once per foreground frame.</summary>
        bool ResyncDue { get; }

        /// <summary>One sync with the server; true when it landed. Never throws.</summary>
        Task<bool> SyncAsync(CancellationToken ct = default);
    }
}
