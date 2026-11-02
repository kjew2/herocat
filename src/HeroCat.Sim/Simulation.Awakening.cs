using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>Awakening, per party slot (03 section 5.3, 00 section 6.5 and decisions 11, 109; issue #157).</summary>
    public sealed partial class Simulation
    {
        /// <summary>
        /// 03 section 5.3's Awaken(slot): BAD_SLOT first, then the three checks in the spec's order, then one ROCK_CANDY debit
        /// with reason AWAKENING, the slot's rank, and one Awakened. Apply marks the DPS cache dirty after the accepted
        /// command (03 section 7.3). An empty slot can be awakened; its rank waits for a companion.
        /// </summary>
        internal ApplyResult HandleAwaken(Awaken cmd)
        {
            var slots = State.Party.Slots;
            if (slots == null || cmd.Slot < 0 || cmd.Slot >= Db.Constants.PartySlots || cmd.Slot >= slots.Length)
                return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            var next = AwakeningRules.Next(Db, State, cmd.Slot);
            if (next == null) return ApplyResult.Reject(RejectCode.Capped, CombatRejectReasons.AWAKEN_MAX_RANK);
            if (State.Progress.HighestCleared < next.StageGate)
                return ApplyResult.Reject(RejectCode.Locked, CombatRejectReasons.AWAKEN_STAGE_NOT_REACHED);
            if (!State.Wallet.CanAfford(Currency.ROCK_CANDY, next.RockCandyCost) ||
                !Ledger.Apply(State, new CurrencyDelta(Currency.ROCK_CANDY, -next.RockCandyCost), CurrencyReason.AWAKENING, this))
                return ApplyResult.Reject(RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_ROCK_CANDY);
            slots[cmd.Slot].AwakenRank = next.Rank;
            MarkDpsDirty();
            Emit(new Awakened(cmd.Slot, next.Rank));
            AchievementsSetMax(QuestGoal.AWAKEN_RANK, AwakeningRules.HighestRank(Db, State));   // highest slot rank (06 section 5.4)
            return ApplyResult.Accepted;
        }
    }

    /// <summary>
    /// The Awakening rules as pure functions over (ContentDb, GameState), shared by the Awaken command, <c>Query</c>, the
    /// bots, the feature gates and the rune slot check. Rows come from the <c>awakening</c> table (09), whose ranks are
    /// exactly 1..N in row order (E060), so rank r is row r - 1; every party slot uses the same rows (00 decision 109). A
    /// tier without the rows (M0) has rank 0 only.
    /// </summary>
    public static class AwakeningRules
    {
        /// <summary>The feature_unlocks id that Awakening rank 1 opens in the seed content (00 decision 11, 09 section 5.34).</summary>
        public const string ResearchFeature = "RESEARCH";

        static int RawRank(GameState s, int slot)
        {
            var slots = s.Party.Slots;
            return slots != null && slot >= 0 && slot < slots.Length && slots[slot] != null ? slots[slot].AwakenRank : 0;
        }

        /// <summary>The slot's AwakenRank clamped to 0..rows (02 rule P3 clamps a loaded save the same way).</summary>
        public static int Rank(ContentDb db, GameState s, int slot)
        {
            int r = RawRank(s, slot);
            int n = db.Awakening.Count;
            return r < 0 ? 0 : r > n ? n : r;
        }

        /// <summary>
        /// The highest clamped rank of any party slot: what feature_unlocks' AWAKEN_RANK, the rune slots and the Awakening
        /// achievements read (00 decision 109).
        /// </summary>
        public static int HighestRank(ContentDb db, GameState s)
        {
            var slots = s.Party.Slots;
            int best = 0;
            if (slots == null) return 0;
            for (int i = 0; i < slots.Length; i++)
            {
                int r = Rank(db, s, i);
                if (r > best) best = r;
            }
            return best;
        }

        /// <summary>The row the slot's next Awaken would buy, or null at the last rank (or with no rows).</summary>
        public static AwakeningDef Next(ContentDb db, GameState s, int slot)
        {
            int r = RawRank(s, slot);
            return r >= 0 && r < db.Awakening.Count ? db.Awakening[r] : null;
        }

        /// <summary>The slot's AWAKEN_MULT ^ rank: the product of the rows' mult for ranks 1..rank (exactly 2.25 at rank 2).</summary>
        public static double Mult(ContentDb db, GameState s, int slot)
        {
            int rank = RawRank(s, slot);
            double m = 1.0;
            var rows = db.Awakening;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Rank <= rank) m *= rows[i].Mult;
            return m;
        }

        /// <summary>Rune slots open now (P1 runes, 03 section 5.3): rune_slots_after of the highest slot rank, 0 at rank 0.</summary>
        public static int RuneSlots(ContentDb db, GameState s)
        {
            int max = db.Constants.RuneSlots;
            int r = HighestRank(db, s);
            int n = r > 0 ? db.Awakening[r - 1].RuneSlotsAfter : 0;
            return n > max ? max : n < 0 ? 0 : n;
        }

        /// <summary>Awaken(slot) would be accepted now: a valid slot, a next rank, its stage gate reached and its Rock Candy held.</summary>
        public static bool CanAwaken(ContentDb db, GameState s, int slot)
        {
            var slots = s.Party.Slots;
            if (slots == null || slot < 0 || slot >= slots.Length || slot >= db.Constants.PartySlots) return false;
            var next = Next(db, s, slot);
            return next != null && s.Progress.HighestCleared >= next.StageGate && s.Wallet.CanAfford(Currency.ROCK_CANDY, next.RockCandyCost);
        }
    }
}
