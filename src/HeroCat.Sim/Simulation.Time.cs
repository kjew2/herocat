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
                // Only a gap past CLOCK_BACKWARDS_TOLERANCE_S is a backwards clock; less is resync jitter (issue #246 item 5).
                long behind = virtualNow - c.ServerSeconds;
                if (behind > Db.Constants.ClockBackwardsToleranceS) Emit(new ClockWentBackwards(behind));
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
            return HandleComputeOfflineCatchupCore(c);   // Simulation.Offline.cs (06 section 7.1, issue #108)
        }

        /// <summary>
        /// 06 section 4.2 (issue #107). Lazy, forward-only and single: a player away three days gets one rollover, and a
        /// backwards clock never re-grants (<c>day &lt;= DayKey</c> is a no-op, rule 3). New profiles start at
        /// DayKey = WeekKey = LoginDayKey = -1, so the first call grants the first login (rule 4). Runs from every
        /// ApplyServerTime, on the tick that crosses a day key, and (through <see cref="EnsureDayNow"/>) at the top of every
        /// quest, dungeon, shop and inbox command. The day keys are 00 contract 7's (<see cref="DayKeys"/>).
        /// </summary>
        void EnsureDay(long now)
        {
            var t = State.Time;
            var k = Db.Constants;
            int day = DayKeys.DayKey(now + RolloverOffsetSeconds, k.DaySeconds);
            if (day <= t.DayKey) return;
            int skipped = t.DayKey < 0 ? 0 : day - t.DayKey - 1;   // a new profile (DayKey -1) skipped nothing
            long sinceLastTick = System.Math.Max(0, now - t.LastTickServerTime);
            t.DayKey = day;

            // Daily resets (06 section 4.2 table). Each is a hook the owning MVP issue implements in its own file; until then
            // they compile to nothing (C# partial methods without an implementation are removed).
            ResetDailyQuests(day);
            TopUpDungeonTickets(day);
            ResetShopDaily(day);

            int week = DayKeys.WeekKey(day);   // Monday 00:00 UTC (00 decision 21)
            if (week > t.WeekKey)
            {
                t.WeekKey = week;
                ResetWeeklyQuests(week);
                Emit(new WeekRolledOver(week));
            }
            Emit(new DayRolledOver(day, skipped, sinceLastTick));

            if (t.LoginDayKey < day)   // the only payment of LOGIN_DIAMONDS (issue #20)
            {
                Ledger.Apply(State, new CurrencyDelta(Currency.DIAMOND, k.LoginDiamonds), CurrencyReason.LOGIN, this);
                t.LoginDayKey = day;
            }
            ProgressLoginQuests(day);   // d_login (DAILY LOGIN) and LOGIN_DAYS (WEEKLY); they count, they never pay (issue #20)
        }

        /// <summary>
        /// EnsureDay at the sim's virtual now: called first by every quest, dungeon, shop and inbox command handler (06
        /// section 4.2), so a command issued on a new day never sees yesterday's counters. No-op before the first anchor.
        /// </summary>
        internal void EnsureDayNow()
        {
            if (IsAnchored) EnsureDay(VirtualNowServerSeconds);
        }

        /// <summary>
        /// 06 section 4.2 rule 5 / 00 decision 8: added to now before the day division. 0 in MVP (00:00 UTC for everyone);
        /// per-region rollover later supplies it from content or config.
        /// </summary>
        static long RolloverOffsetSeconds => 0;

        // Day-rollover hooks (06 section 4.2), implemented by the MVP issues that own the state they reset:
        // daily/weekly quests (06 section 5), dungeon tickets (06 section 6.2), the daily gift and exchange counts (06 section 8).
        partial void ResetDailyQuests(int dayKey);
        partial void TopUpDungeonTickets(int dayKey);
        partial void ResetShopDaily(int dayKey);
        partial void ResetWeeklyQuests(int weekKey);
        partial void ProgressLoginQuests(int dayKey);

        void RecomputeNextDayTick()
        {
            var t = State.Time;
            long boundary = ((long)t.DayKey + 1) * Db.Constants.DaySeconds - RolloverOffsetSeconds;
            long delta = boundary - t.AnchorServerTime;
            _nextDayTick = delta <= 0 ? t.Tick : t.AnchorTick + delta * _tickHz;
        }

    }
}
