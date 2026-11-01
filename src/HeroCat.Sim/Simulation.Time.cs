using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;

namespace HeroCat.Sim
{
    /// <summary>
    /// The virtual server clock and the day rollover (01 section 3.1, 06 section 4; issue #5). The sim never reads a
    /// clock: Runtime issues the logged ApplyServerTime, and between those each tick advances virtual time by 1 / TICK_HZ:
    /// <c>now = AnchorServerTime + (Tick - AnchorTick) / TICK_HZ</c>. The day rolls on the tick that crosses midnight.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>False until the first ApplyServerTime (or ProfileFactory) sets an anchor.</summary>
        bool IsAnchored => State.Time.AnchorServerTime > 0;

        /// <summary>The sim's virtual server time, unix seconds.</summary>
        public long VirtualNowServerSeconds
        {
            get
            {
                var t = State.Time;
                return t.AnchorServerTime + (t.Tick - t.AnchorTick) / _tickHz;
            }
        }

        internal ApplyResult HandleApplyServerTime(ApplyServerTime c)
        {
            if (c.ServerSeconds < 0) return ApplyResult.Reject(RejectCode.BadArgument, SimRejectReasons.BAD_SERVER_TIME);
            var t = State.Time;
            if (IsAnchored)
            {
                long virtualNow = VirtualNowServerSeconds;
                if (c.ServerSeconds < virtualNow) Emit(new ClockWentBackwards(virtualNow - c.ServerSeconds));
            }
            t.AnchorServerTime = c.ServerSeconds;
            t.AnchorTick = t.Tick;
            EnsureDay(c.ServerSeconds);
            RecomputeNextDayTick();
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleComputeOfflineCatchup(ComputeOfflineCatchup c)
        {
            if (c.NowServerSeconds < 0) return ApplyResult.Reject(RejectCode.BadArgument, SimRejectReasons.BAD_SERVER_TIME);
            // TODO(#108): Catchup.Compute, Offline.Pending (stored or summed), OfflineRewardComputed and the tank fill.
            // The skeleton only moves the catch-up anchor forward (never backwards, 06 section 4.2 rule 3).
            var t = State.Time;
            if (c.NowServerSeconds > t.LastTickServerTime) t.LastTickServerTime = c.NowServerSeconds;
            return ApplyResult.Accepted;
        }

        /// <summary>
        /// 06 section 4.2. Lazy and single: a player away three days gets one rollover. A backwards clock never re-grants
        /// (<c>day &lt;= DayKey</c> is a no-op). New profiles start at DayKey = WeekKey = LoginDayKey = -1.
        /// TODO(#107): daily quest reset, dungeon ticket top-up, shop resets and the LOGIN / LOGIN_DAYS quest progress.
        /// </summary>
        void EnsureDay(long now)
        {
            var t = State.Time;
            var k = Db.Constants;
            int day = (int)FloorDiv(now, k.DaySeconds);
            if (day <= t.DayKey) return;
            t.DayKey = day;

            int week = (int)FloorDiv(day - 4, 7);   // Monday 00:00 UTC (00 decision 21)
            if (week > t.WeekKey)
            {
                t.WeekKey = week;
                Emit(new WeekRolledOver(week));
            }
            Emit(new DayRolledOver(day));

            if (t.LoginDayKey < day)   // the only payment of LOGIN_DIAMONDS (issue #20)
            {
                Ledger.Apply(State, new CurrencyDelta(Currency.DIAMOND, k.LoginDiamonds), CurrencyReason.LOGIN, this);
                t.LoginDayKey = day;
            }
        }

        void RecomputeNextDayTick()
        {
            var t = State.Time;
            long boundary = ((long)t.DayKey + 1) * Db.Constants.DaySeconds;
            long delta = boundary - t.AnchorServerTime;
            _nextDayTick = delta <= 0 ? t.Tick : t.AnchorTick + delta * _tickHz;
        }

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
