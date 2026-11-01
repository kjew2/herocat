using HeroCat.Runtime.Services;

namespace HeroCat.Client.Core
{
    /// <summary>Wall clock plus a debug offset (07 section 17.3). The offset is never persisted.</summary>
    public sealed class OffsetClock : IClock, IClockFloor
    {
        readonly IClock _inner;

        public OffsetClock(IClock inner) { _inner = inner; }

        public long OffsetSeconds { get; set; }
        public long NowServerSeconds => _inner.NowServerSeconds + OffsetSeconds;
        public bool IsAuthoritative => _inner.IsAuthoritative;

        /// <summary>Forwards boot's floor to the wrapped clock (02 section 11.2 rule 4).</summary>
        public void SetFloor(long lastSavedServerTime) => (_inner as IClockFloor)?.SetFloor(lastSavedServerTime);
    }
}
