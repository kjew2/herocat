using System;
using System.Collections.Generic;
using HeroCat.Runtime.Services;

namespace HeroCat.Client.Core
{
    /// <summary>Wall clock plus a debug offset (07 section 17.3). The offset is never persisted.</summary>
    public sealed class OffsetClock : IClock, IClockFloor, IClockResync
    {
        readonly IClock _inner;

        public OffsetClock(IClock inner) { _inner = inner; }

        public long OffsetSeconds { get; set; }
        public long NowServerSeconds => _inner.NowServerSeconds + OffsetSeconds;
        public bool IsAuthoritative => _inner.IsAuthoritative;

        /// <summary>Forwards boot's floor to the wrapped clock (02 section 11.2 rule 4).</summary>
        public void SetFloor(long lastSavedServerTime) => (_inner as IClockFloor)?.SetFloor(lastSavedServerTime);

        /// <summary>Forwards the wrapped clock's syncs (01 section 8.2 step 2), shifted by the debug offset.</summary>
        public event Action<long, long> Resynced
        {
            add { if (_inner is IClockResync r) r.Resynced += Shifted(value); }
            remove { if (_inner is IClockResync r) r.Resynced -= Shifted(value); }
        }

        readonly Dictionary<Action<long, long>, Action<long, long>> _shifted =
            new Dictionary<Action<long, long>, Action<long, long>>();

        Action<long, long> Shifted(Action<long, long> h)
        {
            if (h == null) return null;
            if (!_shifted.TryGetValue(h, out var s)) _shifted[h] = s = (a, b) => h(a + OffsetSeconds, b + OffsetSeconds);
            return s;
        }
    }
}
