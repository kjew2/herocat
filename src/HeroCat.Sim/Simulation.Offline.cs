using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Fish;

namespace HeroCat.Sim
{
    /// <summary>
    /// Two-phase Offline Catch-up (06 section 7.1, 01 section 8.7, 00 contract 7, decision 53). ComputeOfflineCatchup stores
    /// or sums the gap into Offline.Pending and stamps Time.LastTickServerTime at once, so the window is consumed at compute
    /// time; ClaimOfflineReward credits it. Runtime's SessionRunner holds ticks while a reward is pending, so the numbers
    /// shown are exactly what is credited.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>
        /// Every tick moves Time.LastTickServerTime up to the virtual server time (never back), so time the session ticked is
        /// never counted as offline, and the anchor is a pure function of the command log (00 contract 5): a replay from
        /// genesis reproduces it without the save's stamp. No-op before the first ApplyServerTime.
        /// </summary>
        void StampLastTick()
        {
            if (!IsAnchored) return;
            long now = VirtualNowServerSeconds;
            var t = State.Time;
            if (now > t.LastTickServerTime) t.LastTickServerTime = now;
        }

        /// <summary>Catchup.Compute (06 section 7.1). Runs only inside Apply(ComputeOfflineCatchup).</summary>
        internal ApplyResult HandleComputeOfflineCatchupCore(ComputeOfflineCatchup c)
        {
            long now = c.NowServerSeconds;
            var t = State.Time;
            long gap = now - t.LastTickServerTime;
            if (gap < 0)   // grants nothing; LastTickServerTime unchanged (06 AC 11)
            {
                // Only a gap past CLOCK_BACKWARDS_TOLERANCE_S is a backwards clock; less is resync jitter (issue #246 item 5).
                if (-gap > Db.Constants.ClockBackwardsToleranceS) Emit(new ClockWentBackwards(-gap));
                return ApplyResult.Accepted;
            }
            if (gap < Db.Constants.OfflineMinS)
            {
                // No record and no modal, but the tank still gets the gap's refill, applied directly (05 section 7, issue #21).
                var (added, refillAfter) = FishBoard.CatchUpTank(State, Db, gap * Db.Constants.TickHz);
                FishBoard.ApplyTankCatchUp(State, Db, added, refillAfter);
                t.LastTickServerTime = now;
                return ApplyResult.Accepted;
            }

            var pending = Catchup.Next(Db, State, Query.Power, State.Offline.Pending, gap, now);
            State.Offline.Pending = pending;
            t.LastTickServerTime = now;   // moved now, so a crash before the claim cannot double-credit
            Emit(new OfflineRewardComputed(pending));
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleClaimOfflineReward(ClaimOfflineReward c)
        {
            var r = State.Offline.Pending;
            if (r == null) return ApplyResult.Reject(RejectCode.NotAvailable, OfflineRejectReasons.NO_PENDING_OFFLINE_REWARD);
            Ledger.Apply(State, new CurrencyDelta(Currency.GOLD, r.Gold), CurrencyReason.OFFLINE, this);
            Ledger.Apply(State, new CurrencyDelta(Currency.PARTY_EXP, r.Exp), CurrencyReason.OFFLINE, this);   // runs CheckPartyLevel
            // TankCount = min(TankCap, TankCount + TankFill) (05 section 7). The record holds no refill progress, so the live
            // TankRefillTick is kept (0 once the tank is full); ticks were held since compute, so it is the compute-time value.
            FishBoard.ApplyTankCatchUp(State, Db, r.TankFill, State.Fish.TankRefillTick);
            State.Offline.LifetimeOfflineSecCredited += r.CreditedSec;
            State.Offline.Pending = null;
            Emit(new OfflineRewardClaimed(r));
            return ApplyResult.Accepted;
        }
    }
}
