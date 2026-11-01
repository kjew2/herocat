using System;
using HeroCat.Sim.Events;
using HeroCat.Sim.Time;

namespace HeroCat.Sim
{
    /// <summary>
    /// Wall clock, offline catch-up and daily rollover (specs/03 section 11, specs/04 section 7.1).
    /// Tick() never reads a clock. The sim keeps a virtual server time:
    ///   NowServerMs = Clock.LastSyncServerMs + (Tick - Clock.TickAtLastSync) * (1000 / TICK_HZ)
    /// and the caller anchors it to real (server) time with SyncWallClock. Day rollover fires lazily on the
    /// tick that crosses a UTC midnight of virtual time, and on SyncWallClock.
    /// </summary>
    public sealed partial class Simulation
    {
        internal long NowServerMs => State.Clock.LastSyncServerMs + (State.Tick - State.Clock.TickAtLastSync) * _msPerTick;

        public OfflineResult SyncWallClock(IClock clock, bool applyCatchUp = true) => SyncWallClock(clock.NowServerSeconds, applyCatchUp);

        public OfflineResult SyncWallClock(DateTimeOffset nowUtc, bool applyCatchUp = true) =>
            SyncWallClock(nowUtc.ToUnixTimeSeconds(), applyCatchUp);

        /// <summary>
        /// Anchor the sim to server time. Any gap between virtual time and <paramref name="nowServerSec"/> of at least
        /// OFFLINE_MIN_GAP_S is credited as offline gold (closed form, no ticks, no RNG) when applyCatchUp is true.
        /// A clock that went backwards by more than 1 s publishes ClockWentBackwards and credits nothing. Then rolls
        /// the day if needed and re-anchors. Call on session open, app resume and every ~60 s while open.
        /// </summary>
        public OfflineResult SyncWallClock(long nowServerSec, bool applyCatchUp = true)
        {
            Log(CommandKind.SyncWallClock, nowServerSec, applyCatchUp ? 1 : 0);
            long nowMs = nowServerSec * 1000L;
            long gapMs = nowMs - NowServerMs;
            var result = new OfflineResult(0, 0, false, 0, State.Stage.Stage, 0);
            if (gapMs >= _k.OfflineMinGapS * 1000L)
            {
                if (applyCatchUp) result = ApplyOfflineCatchUpCore(gapMs / 1000);
            }
            else if (gapMs < -1000)
            {
                if (Wants(EventKinds.ClockWentBackwards)) _sink.Publish(new ClockWentBackwards(State.Tick, -gapMs));
            }
            State.Clock.LastSyncServerMs = nowMs;
            State.Clock.TickAtLastSync = State.Tick;
            if (State.Progression.ChestTimerStart > nowServerSec) State.Progression.ChestTimerStart = nowServerSec;
            EnsureDayCore(nowServerSec);
            RecomputeNextDayTick();
            return result;
        }

        /// <summary>
        /// Closed-form offline gold for <paramref name="elapsedSec"/> seconds away (specs/06 section 9.1):
        /// min(elapsed, OFFLINE_CAP_H) * OFFLINE_RATE * Gold(stage) / KillTimeSeconds(stage). Mutates gold only.
        /// SyncWallClock calls this; call it directly only if you manage your own anchor (and then re-anchor
        /// with SyncWallClock(now, applyCatchUp: false) so the same window is not credited twice).
        /// </summary>
        public OfflineResult ApplyOfflineCatchUp(long elapsedSec)
        {
            Log(CommandKind.OfflineCatchUp, elapsedSec);
            return ApplyOfflineCatchUpCore(elapsedSec);
        }

        OfflineResult ApplyOfflineCatchUpCore(long elapsedSec)
        {
            int stage = State.Stage.Stage;
            double k = Query.KillTimeSeconds(stage);
            if (elapsedSec <= 0) return new OfflineResult(Math.Max(0, elapsedSec), 0, false, 0, stage, k);
            long cap = (long)(_k.OfflineCapH * 3600);
            long credited = Math.Min(elapsedSec, cap);
            double gold = credited * _k.OfflineRate * Content.GoldPerKill(stage) / k;
            State.Currencies.Gold += gold;
            State.Lifetime.OfflineGoldEarned += gold;
            if (Wants(EventKinds.OfflineGoldCredited))
                _sink.Publish(new OfflineGoldCredited(State.Tick, elapsedSec, credited, elapsedSec > cap, gold));
            return new OfflineResult(elapsedSec, credited, elapsedSec > cap, gold, stage, k);
        }

        /// <summary>Roll the day forward if <paramref name="nowServerSec"/> is in a later day. Never re-grants (key &lt;= DayKey is a no-op).</summary>
        public void EnsureDay(long nowServerSec)
        {
            Log(CommandKind.EnsureDay, nowServerSec);
            EnsureDayCore(nowServerSec);
        }

        void EnsureDayCore(long nowServerSec)
        {
            long key = FloorDiv(nowServerSec, _k.DaySeconds);
            if (key <= State.Daily.DayKey) return;
            RollDay(key);
            RecomputeNextDayTick();
        }

        void RollDayFromTicks()
        {
            long key = FloorDiv(NowServerMs, _k.DaySeconds * 1000L);
            if (key > State.Daily.DayKey) RollDay(key);
            RecomputeNextDayTick();
        }

        void RollDay(long key)
        {
            var d = State.Daily;
            d.DayKey = key;
            Array.Clear(d.Progress, 0, d.Progress.Length);
            Array.Clear(d.Rewarded, 0, d.Rewarded.Length);
            State.Currencies.FreePullsDaily = _banner.FreePullsPerDay;
            if (Wants(EventKinds.DayRolledOver)) _sink.Publish(new DayRolledOver(State.Tick, key));
            QuestProgress(HeroCat.Sim.Content.QuestType.Login, 1);
        }

        void RecomputeNextDayTick()
        {
            long boundaryMs = (State.Daily.DayKey + 1) * _k.DaySeconds * 1000L;
            long delta = boundaryMs - State.Clock.LastSyncServerMs;
            long ticks = delta <= 0 ? 0 : (delta + _msPerTick - 1) / _msPerTick;
            _nextDayTick = State.Clock.TickAtLastSync + ticks;
        }

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
