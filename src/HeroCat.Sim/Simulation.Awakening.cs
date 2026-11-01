using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>Awakening (03 section 5.3, 00 section 6.5 and decision 11; issue #157).</summary>
    public sealed partial class Simulation
    {
        /// <summary>
        /// 03 section 5.3's Awaken(): the three checks in the spec's order, then one ROCK_CANDY debit with reason AWAKENING,
        /// the rank, and one Awakened. Apply marks the DPS cache dirty after the accepted command (03 section 7.3).
        /// </summary>
        internal ApplyResult HandleAwaken()
        {
            var next = AwakeningRules.Next(Db, State);
            if (next == null) return ApplyResult.Reject(RejectCode.Capped, CombatRejectReasons.AWAKEN_MAX_RANK);
            if (State.Progress.HighestCleared < next.StageGate)
                return ApplyResult.Reject(RejectCode.Locked, CombatRejectReasons.AWAKEN_STAGE_NOT_REACHED);
            if (!State.Wallet.CanAfford(Currency.ROCK_CANDY, next.RockCandyCost) ||
                !Ledger.Apply(State, new CurrencyDelta(Currency.ROCK_CANDY, -next.RockCandyCost), CurrencyReason.AWAKENING, this))
                return ApplyResult.Reject(RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_ROCK_CANDY);
            State.Hero.AwakenRank = next.Rank;
            MarkDpsDirty();
            Emit(new Awakened(next.Rank));
            AchievementsSetMax(QuestGoal.AWAKEN_RANK, next.Rank);   // the Awakening ladder (06 section 5.4)
            return ApplyResult.Accepted;
        }
    }

    /// <summary>
    /// The Awakening rules as pure functions over (ContentDb, GameState), shared by the Awaken command, <c>Query</c>, the
    /// bots and the companion and rune slot checks (04 section 8.2). Rows come from the <c>awakening</c> table (09), whose
    /// ranks are exactly 1..N in row order (E060), so rank r is row r - 1. A tier without the rows (M0) has rank 0 only.
    /// </summary>
    public static class AwakeningRules
    {
        /// <summary>The feature_unlocks id that Awakening rank 1 opens in the seed content (00 decision 11, 09 section 5.34).</summary>
        public const string ResearchFeature = "RESEARCH";

        /// <summary>Hero.AwakenRank clamped to 0..rows (02 rule P3 clamps a loaded save the same way).</summary>
        public static int Rank(ContentDb db, GameState s)
        {
            int r = s.Hero.AwakenRank;
            int n = db.Awakening.Count;
            return r < 0 ? 0 : r > n ? n : r;
        }

        /// <summary>The row the next Awaken would buy, or null at the last rank (or with no rows).</summary>
        public static AwakeningDef Next(ContentDb db, GameState s)
        {
            int r = s.Hero.AwakenRank;
            return r >= 0 && r < db.Awakening.Count ? db.Awakening[r] : null;
        }

        /// <summary>The row of the current rank, or null at rank 0.</summary>
        public static AwakeningDef Current(ContentDb db, GameState s)
        {
            int r = Rank(db, s);
            return r > 0 ? db.Awakening[r - 1] : null;
        }

        /// <summary>
        /// Companion slots open now: the current row's companion_slots_after; at rank 0 slot 1 only, which is open from the
        /// start (03 section 5.3, 04 section 8.2). Never above COMPANION_SLOTS.
        /// </summary>
        public static int CompanionSlots(ContentDb db, GameState s)
        {
            int max = db.Constants.CompanionSlots;
            var cur = Current(db, s);
            int n = cur != null ? cur.CompanionSlotsAfter : 1;
            return n > max ? max : n < 0 ? 0 : n;
        }

        /// <summary>Rune slots open now (P1 runes, 03 section 5.3): the current row's rune_slots_after, 0 at rank 0.</summary>
        public static int RuneSlots(ContentDb db, GameState s)
        {
            int max = db.Constants.RuneSlots;
            var cur = Current(db, s);
            int n = cur != null ? cur.RuneSlotsAfter : 0;
            return n > max ? max : n < 0 ? 0 : n;
        }

        /// <summary>Awaken would be accepted now: a next rank exists, its stage gate is reached and its Rock Candy is held.</summary>
        public static bool CanAwaken(ContentDb db, GameState s)
        {
            var next = Next(db, s);
            return next != null && s.Progress.HighestCleared >= next.StageGate && s.Wallet.CanAfford(Currency.ROCK_CANDY, next.RockCandyCost);
        }
    }
}
