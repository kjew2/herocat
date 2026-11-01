using System;
using System.Diagnostics;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// The device clock plus a debug offset; not authoritative. One of the two places in HeroCat.Runtime that read system
    /// time (02 section 11.2 rule 1 allows ServerClock.cs and SessionRunner.cs). TODO(#78): the purity grep.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public long OffsetSeconds { get; set; }
        public long NowServerSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + OffsetSeconds;
        public bool IsAuthoritative => false;
    }

    /// <summary>
    /// 02 section 11.1's ServerClock, the Local-mode half (issue #66): without a time API it serves the device clock
    /// floored at the last save (<see cref="SetFloor"/>, 02 section 11.2 rule 4), so time never reads below
    /// LastSavedServerTime, and <see cref="IsAuthoritative"/> stays false. <see cref="Observe"/> is the hook the ApiClient
    /// calls with every X-Server-Time; after one observation time runs on the monotonic clock from that offset.
    /// TODO(#149): SyncAsync over a time API, CLOCK_JUMP_WARN_SEC and the clock_anomaly event.
    /// </summary>
    public sealed class ServerClock : IClock
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
