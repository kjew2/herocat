using System;
using HeroCat.Sim.Content;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Fish
{
    /// <summary>
    /// The Fish Board's pure rules (05): Magic Tank parameters, the offline tank closed form, board queries and the
    /// state invariants. No events, no mutation except where a name says so. The commands live in Simulation.Fish.cs.
    /// </summary>
    public static class FishBoard
    {
        // ---------------------------------------------------------------------------------------- tank (05 section 3)

        /// <summary>
        /// TankCap = TANK_CAP0 + RES_TANK_CAP_PER_LVL * research[RES_TANK_CAP].
        /// TODO(#167): switch to ResearchRules.TankCap (the Research term, issue #158); until then this is TANK_CAP0.
        /// </summary>
        public static int TankCap(GameState s, ContentDb db) => db.Tank.Cap0;

        /// <summary>
        /// TankRefillTicks = round(max(TANK_REFILL_FLOOR_S, TANK_REFILL_S0 - RES_TANK_REFILL_PER_LVL * research) * TICK_HZ).
        /// TODO(#167): switch to ResearchRules.TankRefillTicks (issue #158); until then the Research-0 whole-tick value.
        /// </summary>
        public static int TankRefillTicks(GameState s, ContentDb db) => db.Tank.RefillTicks0;

        /// <summary>TankRefillS in seconds, for display and the tools.</summary>
        public static double TankRefillSeconds(GameState s, ContentDb db) => (double)TankRefillTicks(s, db) / db.Constants.TickHz;

        /// <summary>The level a tank fish is released at for Fish.TankReleaseLevel (05 section 3). 1 in MVP content.</summary>
        public static int ReleaseLevel(GameState s, ContentDb db)
        {
            // P1 tank upgrades: content will map TankReleaseLevel to a fish level; until then the base row's value.
            int lvl = db.Tank.ReleaseLevel;
            if (lvl < 1) lvl = 1;
            if (lvl > db.FishLevels.Count) lvl = db.FishLevels.Count;
            return lvl;
        }

        /// <summary>
        /// 05 section 3 CatchUpTank: the closed form for <paramref name="elapsedTicks"/> of refill. Pure: returns how many
        /// fish the gap adds (bounded by TankCap) and the refill progress after it. 06's Catchup.Compute calls it.
        /// </summary>
        public static (int added, int refillTickAfter) CatchUpTank(GameState s, ContentDb db, long elapsedTicks)
        {
            var f = s.Fish;
            int cap = TankCap(s, db);
            if (f.TankCount >= cap || elapsedTicks <= 0) return (0, f.TankCount >= cap ? 0 : f.TankRefillTick);
            int refill = TankRefillTicks(s, db);
            long total = f.TankRefillTick + elapsedTicks;
            long gained = total / refill;
            int room = cap - f.TankCount;
            int added = gained >= room ? room : (int)gained;
            int after = f.TankCount + added >= cap ? 0 : (int)(total % refill);
            return (added, after);
        }

        /// <summary>Applies a CatchUpTank result (05 section 7: TankCount = min(TankCap, TankCount + added), refill per section 3).</summary>
        public static void ApplyTankCatchUp(GameState s, ContentDb db, int added, int refillTickAfter)
        {
            var f = s.Fish;
            int cap = TankCap(s, db);
            long n = (long)f.TankCount + (added < 0 ? 0 : added);
            f.TankCount = n >= cap ? cap : (int)n;
            f.TankRefillTick = f.TankCount >= cap ? 0 : refillTickAfter;
        }

        // ---------------------------------------------------------------------------------------- board

        /// <summary>FishMaxLvl = FISH_MAX_LVL0 + research[RES_FISH_CAP], never above the table (FISH_MAX_LVL_HARD). TODO(#167): switch to ResearchRules.FishMaxLvl.</summary>
        public static int FishMaxLvl(GameState s, ContentDb db)
        {
            int cap = db.Constants.FishMaxLvl0;
            return cap > db.FishLevels.Count ? db.FishLevels.Count : cap;
        }

        /// <summary>FISH_MAX_LVL_HARD: the fish_levels row count, the only clamp a saved cell gets (issue #41).</summary>
        public static int FishMaxLvlHard(ContentDb db) => db.FishLevels.Count;

        /// <summary>The fish_levels row for level <paramref name="n"/> (1-based).</summary>
        public static FishLevelDef Level(ContentDb db, int n) => db.FishLevels[n - 1];

        /// <summary>
        /// 05 section 5.2: FISH_BOARD_DPS_PCT, the sum of the table's BoardDpsPct over occupied cells in index order (the
        /// floating-point order is fixed, rule 5). Percentage points.
        /// </summary>
        public static double BoardDpsPct(GameState s, ContentDb db)
        {
            var cells = s.Fish.Cells;
            int hard = db.FishLevels.Count;
            double sum = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                int n = cells[i];
                if (n >= 1 && n <= hard) sum += db.FishLevels[n - 1].BoardDpsPct;
            }
            return sum;
        }

        /// <summary>Count of empty cells.</summary>
        public static int EmptyCells(GameState s)
        {
            int n = 0;
            foreach (int c in s.Fish.Cells) if (c == 0) n++;
            return n;
        }

        /// <summary>
        /// 05 section 4.1: the k-th empty cell in row-major order, k = (int)(u * empties), for a draw u in [0, 1).
        /// <paramref name="empties"/> must be the board's empty count and at least 1.
        /// </summary>
        public static int PickEmptyCell(int[] cells, int empties, double u)
        {
            int k = (int)(u * empties);
            if (k >= empties) k = empties - 1;   // u < 1 always; guard only
            if (k < 0) k = 0;
            for (int i = 0; i < cells.Length; i++)   // row-major scan (rule 2)
            {
                if (cells[i] != 0) continue;
                if (k == 0) return i;
                k--;
            }
            return -1;
        }

        /// <summary>Count of the pairs MergeAll's first pass would merge (levels below FishMaxLvl), for the "Nothing to merge" state.</summary>
        public static int MergeablePairs(GameState s, ContentDb db)
        {
            int cap = FishMaxLvl(s, db);
            var counts = new int[cap];
            foreach (int c in s.Fish.Cells) if (c >= 1 && c < cap) counts[c]++;
            int pairs = 0;
            for (int l = 1; l < cap; l++) pairs += counts[l] / 2;
            return pairs;
        }

        /// <summary>True when the tank has a fish and the board has room (ReleaseFish would not reject, outside a dungeon).</summary>
        public static bool CanRelease(GameState s) => s.Fish.TankCount > 0 && EmptyCells(s) > 0;

        /// <summary>The board is hidden and frozen during a dungeon run (issue #40).</summary>
        public static bool IsLocked(GameState s) => s.Combat.Mode == CombatMode.Dungeon;

        // ---------------------------------------------------------------------------------------- invariants (05 section 12)

        /// <summary>
        /// The Fish Board invariants (05 section 12 rule 1, 02 P9 / P9b / P15 in their checked form). Throws
        /// <see cref="InvalidOperationException"/> naming the first one that fails; the sim never breaks them, so a throw is
        /// a sim bug or an unrepaired save.
        /// </summary>
        public static void Validate(GameState s, ContentDb db)
        {
            string e = FirstViolation(s, db);
            if (e != null) throw new InvalidOperationException("Fish Board invariant: " + e);
        }

        /// <summary>Null when every invariant holds, else a description of the first failure.</summary>
        public static string FirstViolation(GameState s, ContentDb db)
        {
            var f = s.Fish;
            var k = db.Constants;
            if (f.Cells == null || f.Cells.Length != k.FishBoardW * k.FishBoardH) return "Cells length != FISH_BOARD_W * FISH_BOARD_H";
            int hard = db.FishLevels.Count;
            int highest = 0;
            for (int i = 0; i < f.Cells.Length; i++)
            {
                int n = f.Cells[i];
                if (n < 0 || n > hard) return $"Cells[{i}] = {n} outside 0..{hard}";
                if (n > 0 && !f.DiscoveredLevels.Contains(n)) return $"Cells[{i}] level {n} not in DiscoveredLevels";
            }
            foreach (int d in f.DiscoveredLevels)
            {
                if (d < 1 || d > hard) return $"DiscoveredLevels holds {d}";
                if (d > highest) highest = d;
            }
            if (f.HighestLevel != highest) return $"HighestLevel {f.HighestLevel} != max(DiscoveredLevels) {highest}";
            int cap = TankCap(s, db);
            if (f.TankCount < 0 || f.TankCount > cap) return $"TankCount {f.TankCount} outside 0..{cap}";
            int refill = TankRefillTicks(s, db);
            if (f.TankRefillTick < 0 || f.TankRefillTick >= refill) return $"TankRefillTick {f.TankRefillTick} outside 0..{refill - 1}";
            if (f.TankCount >= cap && f.TankRefillTick != 0) return "TankRefillTick advanced while the tank is full";
            if (f.TankReleaseLevel < 1) return "TankReleaseLevel < 1";
            if (f.TotalMerges < 0 || f.TotalMerges > s.Stats.Merges) return $"TotalMerges {f.TotalMerges} outside 0..Stats.Merges";
            if (!s.Rng.TryGetValue(RngStreams.FishSpawn, out var st) || st == null) return "fish_spawn stream missing";
            if (st.Counter != s.Stats.FishReleased) return $"fish_spawn.Counter {st.Counter} != Stats.FishReleased {s.Stats.FishReleased}";
            foreach (double m in s.Combat.MissileQueue)
                if (!(m > 0) || double.IsInfinity(m)) return $"MissileQueue entry {m} not positive and finite";
            return null;
        }
    }
}
