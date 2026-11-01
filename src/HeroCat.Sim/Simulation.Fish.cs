using System.Collections.Generic;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Fish;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>
    /// The Fish Board (05): the Magic Tank tick, the six board commands, discovery and Fish Coins, and the missile
    /// producer side (Combat.MissileQueue; 03's ApplyQueuedFishMissiles drains it). Pure rules are in
    /// <see cref="FishBoard"/>.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Publish the opt-in TankFishReady event on each tank refill (05 section 3). Off by default; not state.</summary>
        public bool EmitTankFishReady { get; set; }

        /// <summary>
        /// Called at most once per board command that changes FISH_BOARD_DPS_PCT (05 section 12 rule 5). The combat track
        /// implements it as MarkDpsDirty(); unimplemented it compiles away.
        /// </summary>
        partial void OnFishBoardDpsChanged();

        // ------------------------------------------------------------------------------------- queries (05)

        /// <summary>Query.TankCap.</summary>
        public int FishTankCap => FishBoard.TankCap(State, Db);

        /// <summary>Query.TankRefillTicks.</summary>
        public int FishTankRefillTicks => FishBoard.TankRefillTicks(State, Db);

        /// <summary>Query.FishMaxLvl: the highest level a merge can create.</summary>
        public int FishMaxLvl => FishBoard.FishMaxLvl(State, Db);

        /// <summary>Query.FishBoardDpsPct: FISH_BOARD_DPS_PCT in percentage points (05 section 5.2).</summary>
        public double FishBoardDpsPct => FishBoard.BoardDpsPct(State, Db);

        /// <summary>Query.FishMergeablePairs: 0 means "Nothing to merge" (05 section 6).</summary>
        public int FishMergeablePairs => FishBoard.MergeablePairs(State, Db);

        /// <summary>False means "Tank empty" or "Board full"; also false while the board is locked.</summary>
        public bool FishCanRelease => !FishBoard.IsLocked(State) && FishBoard.CanRelease(State);

        // ------------------------------------------------------------------------------------- tank (05 section 3)

        /// <summary>Runs every tick, before combat, in every Combat.Mode (the tank refills during dungeon runs too).</summary>
        void TickTank()
        {
            var f = State.Fish;
            if (f.TankCount >= FishBoard.TankCap(State, Db)) { f.TankRefillTick = 0; return; }
            f.TankRefillTick += 1;
            int refill = FishBoard.TankRefillTicks(State, Db);
            if (f.TankRefillTick >= refill)
            {
                f.TankRefillTick -= refill;
                f.TankCount += 1;
                if (EmitTankFishReady) Emit(new TankFishReady(f.TankCount));
            }
        }

        // ------------------------------------------------------------------------------------- commands (05 section 4)

        static ApplyResult BoardLocked() => ApplyResult.Reject(RejectCode.Locked, FishRejectReasons.BOARD_LOCKED);

        bool IsCell(int i) => i >= 0 && i < State.Fish.Cells.Length;

        internal ApplyResult HandleReleaseFish()
        {
            if (FishBoard.IsLocked(State)) return BoardLocked();
            var r = TryRelease();
            if (r.Ok) OnFishBoardDpsChanged();
            return r;
        }

        internal ApplyResult HandleReleaseMax()
        {
            if (FishBoard.IsLocked(State)) return BoardLocked();
            int n = 0;
            while (TryRelease().Ok) n++;
            if (n > 0) OnFishBoardDpsChanged();
            return ApplyResult.Accepted;   // zero releases is not a rejection; the client disables the button (05 section 6.1)
        }

        ApplyResult TryRelease()
        {
            var f = State.Fish;
            if (f.TankCount <= 0) return ApplyResult.Reject(RejectCode.TankEmpty, FishRejectReasons.TANK_EMPTY);
            var cells = f.Cells;
            int empties = FishBoard.EmptyCells(State);
            if (empties == 0) return ApplyResult.Reject(RejectCode.BoardFull, FishRejectReasons.BOARD_FULL);

            double u = RngStreams.NextUnit(State.Rng[RngStreams.FishSpawn]);   // exactly one draw per successful release
            int cell = FishBoard.PickEmptyCell(cells, empties, u);
            int lvl = FishBoard.ReleaseLevel(State, Db);
            cells[cell] = lvl;
            f.TankCount -= 1;
            State.Stats.FishReleased += 1;
            Emit(new FishReleased(cell, lvl, f.TankCount));
            if (Discover(lvl))
            {
                double coins = FishBoard.Level(Db, lvl).DiscoveryCoins;
                Emit(new FishDiscovered(lvl, coins));
                Ledger.Apply(State, new CurrencyDelta(Content.Currency.FISH_COIN, coins), CurrencyReason.FISH_DISCOVERY, this);
            }
            QuestsProgress(Content.QuestGoal.RELEASE_FISH, 1);
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleMergeFish(MergeFish c)
        {
            if (FishBoard.IsLocked(State)) return BoardLocked();
            if (c.Source == c.Target || !IsCell(c.Source) || !IsCell(c.Target))
                return ApplyResult.Reject(RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
            var cells = State.Fish.Cells;
            int a = cells[c.Source], b = cells[c.Target];
            if (a == 0 || b == 0) return ApplyResult.Reject(RejectCode.InvalidTarget, FishRejectReasons.EMPTY_CELL);
            if (a < 0 || b < 0) return ApplyResult.Reject(RejectCode.BadArgument, FishRejectReasons.BAD_CELL);   // unrepaired save; never throw
            if (a != b) return ApplyResult.Reject(RejectCode.LevelMismatch, FishRejectReasons.LEVEL_MISMATCH);
            if (a >= FishBoard.FishMaxLvl(State, Db)) return ApplyResult.Reject(RejectCode.Capped, FishRejectReasons.FISH_AT_LEVEL_CAP);
            MergeCore(c.Source, c.Target);
            OnFishBoardDpsChanged();
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleMergeAll()
        {
            if (FishBoard.IsLocked(State)) return BoardLocked();
            var cells = State.Fish.Cells;
            int cap = FishBoard.FishMaxLvl(State, Db);
            var at = new List<int>(cells.Length);
            int merges = 0;
            bool found;
            do
            {
                found = false;
                for (int level = 1; level < cap; level++)
                {
                    at.Clear();
                    for (int i = 0; i < cells.Length; i++) if (cells[i] == level) at.Add(i);
                    for (int p = 0; p + 1 < at.Count; p += 2)
                    {
                        MergeCore(at[p], at[p + 1]);   // leftmost pair first, result in the second cell
                        merges++;
                        found = true;
                    }
                }
            } while (found);
            if (merges > 0) OnFishBoardDpsChanged();
            return ApplyResult.Accepted;   // zero merges is not a rejection ("Nothing to merge")
        }

        /// <summary>The validated merge: source empties, target becomes level n, coins, missile, events (05 section 4.2).</summary>
        void MergeCore(int source, int target)
        {
            var f = State.Fish;
            int n = f.Cells[target] + 1;
            f.Cells[target] = n;
            f.Cells[source] = 0;
            f.TotalMerges += 1;
            State.Stats.Merges += 1;
            var row = FishBoard.Level(Db, n);
            bool discovered = Discover(n);
            double coins = discovered ? row.DiscoveryCoins : row.MergeCoins;
            State.Combat.MissileQueue.Add(row.MissilePct / Units.Percent);   // 03 section 2.5 drains it on the next Fighting tick
            Emit(new FishMerged(source, target, n, coins, row.MissilePct, discovered));
            if (discovered) Emit(new FishDiscovered(n, row.DiscoveryCoins));
            Ledger.Apply(State, new CurrencyDelta(Content.Currency.FISH_COIN, coins),
                         discovered ? CurrencyReason.FISH_DISCOVERY : CurrencyReason.FISH_MERGE, this);
            QuestsProgress(Content.QuestGoal.MERGE_FISH, 1);   // d_merge, w_merges and the LIFETIME_MERGES ladder (06 section 5.4)
        }

        internal ApplyResult HandleMoveFish(MoveFish c)
        {
            if (FishBoard.IsLocked(State)) return BoardLocked();
            if (c.Source == c.Target || !IsCell(c.Source) || !IsCell(c.Target))
                return ApplyResult.Reject(RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
            var cells = State.Fish.Cells;
            if (cells[c.Source] == 0) return ApplyResult.Reject(RejectCode.InvalidTarget, FishRejectReasons.EMPTY_CELL);
            if (cells[c.Target] != 0) return ApplyResult.Reject(RejectCode.InvalidTarget, FishRejectReasons.CELL_OCCUPIED);
            cells[c.Target] = cells[c.Source];
            cells[c.Source] = 0;
            Emit(new FishMoved(c.Source, c.Target, cells[c.Target]));   // no draw, no coins, no stat, DPS unchanged (00 decision 22)
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleDiscardFish(DiscardFish c)
        {
            if (FishBoard.IsLocked(State)) return BoardLocked();
            if (!IsCell(c.Cell)) return ApplyResult.Reject(RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
            var cells = State.Fish.Cells;
            int n = cells[c.Cell];
            if (n == 0) return ApplyResult.Reject(RejectCode.InvalidTarget, FishRejectReasons.EMPTY_CELL);
            if (n < 0 || n > FishBoard.FishMaxLvlHard(Db)) return ApplyResult.Reject(RejectCode.BadArgument, FishRejectReasons.BAD_CELL);   // unrepaired save; never throw
            cells[c.Cell] = 0;
            State.Stats.FishDiscards += 1;
            double coins = Db.Constants.FishDiscardCoinsPct * FishBoard.Level(Db, n).DiscoveryCoins;
            Emit(new FishDiscarded(c.Cell, n, coins));
            Ledger.Apply(State, new CurrencyDelta(Content.Currency.FISH_COIN, coins), CurrencyReason.FISH_MERGE, this);   // salvage, not discovery
            OnFishBoardDpsChanged();
            return ApplyResult.Accepted;
        }

        /// <summary>True only the first time level <paramref name="n"/> is created (05 section 4.2).</summary>
        bool Discover(int n)
        {
            var f = State.Fish;
            if (!f.DiscoveredLevels.Add(n)) return false;
            if (n > f.HighestLevel)
            {
                f.HighestLevel = n;
                AchievementsSetMax(Content.QuestGoal.FISH_LEVEL, n);   // the Fish level ladder (06 section 5.4)
            }
            return true;
        }
    }
}
