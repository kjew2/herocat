using System;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// The Local-mode clock (02 section 11.2 rule 4; issue #78): device time plus a debug offset, floored at
    /// <c>Time.LastSavedServerTime</c> of the loaded save so time never reads below the last save, and never authoritative.
    /// This file and SessionRunner.cs are the only files in HeroCat.Runtime allowed to read system time (02 acceptance 18,
    /// enforced by tools/check_clock_apis.py).
    /// TODO(#149): the monotonic ServerClock with Observe/resync and clock_anomaly{resync}. It keeps this floor and replaces
    /// both this class and HeroCat.Remote's ServerClock (its api == null case is this Local mode).
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
}
