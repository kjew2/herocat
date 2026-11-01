using System;
using System.Diagnostics;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// The Local-mode clock (02 section 11.2 rule 4; issue #78): device time plus a debug offset, floored at
    /// <c>Time.LastSavedServerTime</c> of the loaded save so time never reads below the last save, and never authoritative.
    /// This file and SessionRunner.cs are the only files in HeroCat.Runtime allowed to read system time (02 acceptance 18,
    /// enforced by tools/check_clock_apis.py).
    /// <see cref="ServerClock"/> below wraps it (the composition root's Local clock). TODO(#149): the ServerClock's time API
    /// resync and clock_anomaly{resync}; it then also replaces HeroCat.Remote's ServerClock.
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
    /// 02 section 11.1's ServerClock, the Local-mode half (issue #66): without a time API it serves the device clock
    /// floored at the last save (<see cref="SetFloor"/>, 02 section 11.2 rule 4), so time never reads below
    /// LastSavedServerTime, and <see cref="IsAuthoritative"/> stays false. <see cref="Observe"/> is the hook the ApiClient
    /// calls with every X-Server-Time; after one observation time runs on the monotonic clock from that offset.
    /// TODO(#149): SyncAsync over a time API, CLOCK_JUMP_WARN_SEC and the clock_anomaly event.
    /// </summary>
    public sealed class ServerClock : IClock, IClockFloor
    {
        readonly IClock _device;
        long? _offsetSec;   // serverNow - monotonicNow at the last observation
        long _floorSec;

        /// <param name="device">The device clock; default <see cref="SystemClock"/>. Tests pass a FakeClock.</param>
        public ServerClock(IClock device = null) { _device = device ?? new SystemClock(); }

        public bool IsAuthoritative => _offsetSec.HasValue;

        public long NowServerSeconds => Math.Max(_floorSec, _offsetSec is long o ? MonoSeconds() + o : _device.NowServerSeconds);

        /// <summary>Boot step 4: the loaded save's LastSavedServerTime.</summary>
        public void SetFloor(long lastSavedServerTime)
        {
            if (lastSavedServerTime > _floorSec) _floorSec = lastSavedServerTime;
        }

        /// <summary>A server time observation (X-Server-Time plus half the round trip).</summary>
        public void Observe(long serverTimeSec, long rttMs)
        {
            long estimate = serverTimeSec + rttMs / 2000;
            _offsetSec = estimate - MonoSeconds();
        }

        static long MonoSeconds() => Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    }
}
