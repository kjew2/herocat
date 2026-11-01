using System;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// The device clock plus a debug offset; not authoritative. The only place in HeroCat.Runtime that reads system time
    /// (02 AC 18 allows ServerClock.cs and SessionRunner.cs). TODO(#149): the monotonic ServerClock floored at the last
    /// save, with Observe/resync and clock_anomaly; TODO(#78): the purity grep.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public long OffsetSeconds { get; set; }
        public long NowServerSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + OffsetSeconds;
        public bool IsAuthoritative => false;
    }
}
