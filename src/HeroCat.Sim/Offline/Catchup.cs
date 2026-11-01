using HeroCat.Sim.Content;
using HeroCat.Sim.Fish;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>
    /// Offline Catch-up's pure arithmetic (06 section 7, 00 contract 7, decision 53). A rate times a duration and nothing
    /// else: no stage, kills, drops, Rock Candy, quest progress, RNG draw or boss attempt. Only Simulation's
    /// ComputeOfflineCatchup and ClaimOfflineReward handlers (Simulation.Offline.cs) change state with it.
    /// </summary>
    public static class Catchup
    {
        /// <summary>Seconds per hour: a unit conversion for OFFLINE_CAP_H, not a balance number.</summary>
        const int SecondsPerHour = 3600;   // literal-ok: unit conversion (hours to seconds)

        /// <summary>The effective cap in seconds: (OFFLINE_CAP_H + RES_OFFLINE_CAP hours) * 3600 (00 decision 12).</summary>
        public static long CapSeconds(ContentDb db, GameState s) => (long)(PowerFormula.OfflineCapH(db, s) * SecondsPerHour);

        /// <summary>
        /// <c>credited * OFFLINE_RATE * perKill / killTimeSec</c>: offline gold or EXP for <paramref name="creditedSec"/>
        /// (06 section 7.1). 0 when the kill time is not a positive finite number.
        /// </summary>
        public static double Amount(long creditedSec, double offlineRate, double perKill, double killTimeSec)
        {
            if (!(killTimeSec > 0) || double.IsInfinity(killTimeSec)) return 0.0;
            return creditedSec * offlineRate * perKill / killTimeSec;
        }

        /// <summary>
        /// What ComputeOfflineCatchup stores for a gap of <paramref name="gapSec"/> seconds ending at
        /// <paramref name="serverNow"/> on top of <paramref name="prev"/> (the pending record, or null), at the rated power
        /// <paramref name="p"/> of <paramref name="s"/>. Pure. The caller handles gaps below OFFLINE_MIN_S and backwards
        /// clocks. A second gap sums into the record and the cap applies to the summed CreditedSec; Stage and KillTimeSec
        /// are the latest gap's, for display (issue #21).
        /// </summary>
        public static CatchupResult Next(ContentDb db, GameState s, in PowerBreakdown p, CatchupResult prev, long gapSec, long serverNow)
        {
            var k = db.Constants;
            long prevCredited = prev != null ? prev.CreditedSec : 0;
            long room = CapSeconds(db, s) - prevCredited;
            long credited = gapSec < room ? gapSec : room;
            if (credited < 0) credited = 0;

            int stage = s.Combat.Stage;   // the stage being farmed, walled or not (06 section 7.1)
            double killTime = PowerFormula.KillTimeSeconds(db, p, db.EnemyHp.Eval(stage));
            double gold = Amount(credited, k.OfflineRate, db.GoldPerKill.Eval(stage) * p.GoldMult, killTime);   // includes GOLD_GAIN
            double exp = Amount(credited, k.OfflineRate, db.ExpPerKill.Eval(stage) * p.ExpMult, killTime);      // includes EXP_GAIN
            int tankFill = FishBoard.CatchUpTank(s, db, credited * k.TickHz).added;   // read-only here; applied at claim
            if (double.IsInfinity(killTime)) killTime = 0;   // no kill rate: nothing earned; keep the record finite for the save

            long elapsedSum = gapSec + (prev != null ? prev.ElapsedSec : 0);
            long creditedSum = credited + prevCredited;
            return new CatchupResult(elapsedSum, creditedSum, creditedSum < elapsedSum,
                                     (prev != null ? prev.Gold : 0) + gold, (prev != null ? prev.Exp : 0) + exp,
                                     (prev != null ? prev.TankFill : 0) + tankFill, stage, killTime, serverNow);
        }
    }
}
