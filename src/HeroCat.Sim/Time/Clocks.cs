using System;

namespace HeroCat.Sim.Time
{
    /// <summary>
    /// The only clock the economy trusts (specs/06 section 11, specs/08 section 3). The sim never reads a
    /// clock itself: callers read an IClock and pass seconds into Simulation.SyncWallClock.
    /// </summary>
    public interface IClock
    {
        /// <summary>Unix seconds, UTC, anchored to server time.</summary>
        long NowServerSeconds { get; }

        /// <summary>True only when anchored to a server sync.</summary>
        bool IsAuthoritative { get; }
    }

    /// <summary>Tests and bot harness. Starts wherever you put it and only moves when told.</summary>
    public sealed class FakeClock : IClock
    {
        public FakeClock(long startServerSeconds = 0) { NowServerSeconds = startServerSeconds; }

        public FakeClock(DateTimeOffset startUtc) : this(startUtc.ToUnixTimeSeconds()) { }

        public long NowServerSeconds { get; private set; }
        public bool IsAuthoritative => true;
        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeSeconds(NowServerSeconds);

        public void Set(long serverSeconds) => NowServerSeconds = serverSeconds;
        public void Set(DateTimeOffset utc) => NowServerSeconds = utc.ToUnixTimeSeconds();
        public void Advance(long seconds) => NowServerSeconds += seconds;
        public void Advance(TimeSpan by) => NowServerSeconds += (long)by.TotalSeconds;
    }

    /// <summary>
    /// Prototype client clock: the device clock plus a debug offset. Not authoritative. This file is the only
    /// place in HeroCat.Sim that reads the system time.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public long OffsetSeconds { get; set; }
        public long NowServerSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + OffsetSeconds;
        public bool IsAuthoritative => false;
    }
}
