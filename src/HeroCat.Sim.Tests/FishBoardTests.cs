using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Runtime.Save;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Fish;
using HeroCat.Sim.Replay;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>The Fish Board (05). Test names cite 05 section 15's acceptance criteria.</summary>
    public class FishBoardTests
    {
        static ContentDb Db => TestContent.Db;

        /// <summary>A fresh anchored sim with its events drained.</summary>
        static Simulation NewSim()
        {
            var sim = TestContent.NewSim();
            sim.DrainEvents();
            return sim;
        }

        /// <summary>Sets the board (padded with empties) and marks every level on it discovered, as a real board would be.</summary>
        static void SetBoard(Simulation sim, params int[] levels)
        {
            var f = sim.State.Fish;
            Array.Clear(f.Cells, 0, f.Cells.Length);
            for (int i = 0; i < levels.Length; i++) f.Cells[i] = levels[i];
            foreach (int n in levels)
                if (n > 0 && f.DiscoveredLevels.Add(n) && n > f.HighestLevel) f.HighestLevel = n;
        }

        static void Discover(Simulation sim, int upTo)
        {
            var f = sim.State.Fish;
            for (int n = 1; n <= upTo; n++) f.DiscoveredLevels.Add(n);
            if (upTo > f.HighestLevel) f.HighestLevel = upTo;
        }

        static long FishSpawnCounter(Simulation sim) => sim.State.Rng[RngStreams.FishSpawn].Counter;

        static void AssertRejected(Simulation sim, Command c, RejectCode code, string reason)
        {
            sim.DrainEvents();
            string before = sim.StateHash();
            var r = sim.Apply(c);
            Assert.That((r.Ok, r.Code, r.Reason), Is.EqualTo((false, code, reason)), c.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(before), c + ": a reject changes nothing");
            Assert.That(sim.DrainEvents(), Is.Empty, c + ": a reject emits nothing");
        }

        // ------------------------------------------------------------------------------------- tank

        [Test]
        public void AC1_TickTank_FillsOneFishPer60Ticks_ThenStops()
        {
            var sim = NewSim();
            var f = sim.State.Fish;
            Assert.That(f.TankCount, Is.EqualTo(0), "new profiles start with an empty tank (issue #246 pending)");
            Assert.That(sim.FishTankRefillTicks, Is.EqualTo(60));
            Assert.That(sim.FishTankCap, Is.EqualTo(10));
            sim.Advance(59);
            Assert.That(f.TankCount, Is.EqualTo(0));
            sim.Advance(1);
            Assert.That((f.TankCount, f.TankRefillTick), Is.EqualTo((1, 0)));
            sim.Advance(539);
            Assert.That(f.TankCount, Is.EqualTo(9));
            sim.Advance(1);
            Assert.That((f.TankCount, f.TankRefillTick), Is.EqualTo((10, 0)));
            sim.Advance(1000);
            Assert.That((f.TankCount, f.TankRefillTick), Is.EqualTo((10, 0)), "progress while full is discarded");
            Assert.That(sim.GetSnapshot().TankFish, Is.EqualTo(10));
            Assert.That(sim.GetSnapshot().TankCap, Is.EqualTo(10));
        }

        [Test]
        public void TickTank_TankFishReady_IsOptIn()
        {
            var sim = NewSim();
            sim.Advance(60);
            Assert.That(sim.DrainEvents().OfType<TankFishReady>(), Is.Empty);
            sim.EmitTankFishReady = true;
            sim.Advance(60);
            Assert.That(sim.DrainEvents().OfType<TankFishReady>().Single().TankCount, Is.EqualTo(2));
        }

        [Test]
        public void AC1_CatchUpTank_ClosedForm()
        {
            var s = TestContent.NewState();
            s.Fish.TankCount = 3; s.Fish.TankRefillTick = 17;
            Assert.That(FishBoard.CatchUpTank(s, Db, 36000), Is.EqualTo((7, 0)));
            s.Fish.TankCount = 0; s.Fish.TankRefillTick = 0;
            Assert.That(FishBoard.CatchUpTank(s, Db, 125), Is.EqualTo((2, 5)));
            s.Fish.TankCount = 10;
            Assert.That(FishBoard.CatchUpTank(s, Db, 125), Is.EqualTo((0, 0)), "full tank");
            s.Fish.TankCount = 2; s.Fish.TankRefillTick = 40;
            Assert.That(FishBoard.CatchUpTank(s, Db, long.MaxValue / 2), Is.EqualTo((8, 0)), "huge gap is bounded by TankCap");

            var c = FishBoard.CatchUpTank(s, Db, 30);
            FishBoard.ApplyTankCatchUp(s, Db, c.added, c.refillTickAfter);
            Assert.That((s.Fish.TankCount, s.Fish.TankRefillTick), Is.EqualTo((3, 10)));
        }

        [Test]
        public void CatchUpTank_MatchesTickTank()
        {
            foreach (int stock in new[] { 0, 3, 9 })
            foreach (int prog in new[] { 0, 17, 59 })
            foreach (int gap in new[] { 0, 1, 42, 60, 125, 599, 3000 })
            {
                var sim = NewSim();
                sim.State.Fish.TankCount = stock; sim.State.Fish.TankRefillTick = prog;
                var closed = FishBoard.CatchUpTank(sim.State, Db, gap);
                sim.Advance(gap);
                Assert.That((stock + closed.added, closed.refillTickAfter),
                            Is.EqualTo((sim.State.Fish.TankCount, sim.State.Fish.TankRefillTick)), $"{stock}/{prog}/{gap}");
            }
        }

        // ------------------------------------------------------------------------------------- release

        [Test]
        public void AC2_ReleaseFish_RejectsWithoutDrawing()
        {
            var sim = NewSim();
            AssertRejected(sim, new ReleaseFish(), RejectCode.TankEmpty, FishRejectReasons.TANK_EMPTY);
            sim.State.Fish.TankCount = 5;
            SetBoard(sim, Enumerable.Repeat(1, 20).ToArray());
            AssertRejected(sim, new ReleaseFish(), RejectCode.BoardFull, FishRejectReasons.BOARD_FULL);
            Assert.That(FishSpawnCounter(sim), Is.EqualTo(0));

            SetBoard(sim);
            Assert.That(sim.Apply(new ReleaseFish()).Ok, Is.True);
            Assert.That(FishSpawnCounter(sim), Is.EqualTo(1), "exactly one draw per successful release");
            Assert.That(sim.State.Stats.FishReleased, Is.EqualTo(1));
            Assert.That(sim.State.Fish.TankCount, Is.EqualTo(4));
            Assert.That(sim.State.Fish.Cells.Count(c => c == 1), Is.EqualTo(1));
        }

        [Test]
        public void AC2_PickEmptyCell_KthEmptyInRowMajorOrder()
        {
            // 7 empty cells at 2, 5, 8, 11, 14, 17, 19
            var cells = Enumerable.Repeat(3, 20).ToArray();
            int[] empty = { 2, 5, 8, 11, 14, 17, 19 };
            foreach (int e in empty) cells[e] = 0;
            Assert.That(FishBoard.PickEmptyCell(cells, 7, 0.0), Is.EqualTo(empty[0]));
            Assert.That(FishBoard.PickEmptyCell(cells, 7, 0.5), Is.EqualTo(empty[3]));
            Assert.That(FishBoard.PickEmptyCell(cells, 7, 1 - 1e-12), Is.EqualTo(empty[6]));
        }

        [Test]
        public void AC2_ReleaseFish_PlacesWhereTheStreamSays()
        {
            var sim = NewSim();
            sim.State.Fish.TankCount = 10;
            for (int i = 0; i < 10; i++)
            {
                double u = RngStreams.PeekUnit(sim.State.Rng[RngStreams.FishSpawn]);
                int expect = FishBoard.PickEmptyCell(sim.State.Fish.Cells, FishBoard.EmptyCells(sim.State), u);
                sim.Apply(new ReleaseFish());
                var e = sim.DrainEvents().OfType<FishReleased>().Single();
                Assert.That(e.Cell, Is.EqualTo(expect));
                Assert.That((e.Level, e.StockAfter), Is.EqualTo((1, 9 - i)));
            }
        }

        [Test]
        public void AC3_FirstRelease_PaysDiscoveryOnce_EventOrder()
        {
            var sim = NewSim();
            sim.State.Fish.TankCount = 2;
            sim.Apply(new ReleaseFish());
            var ev = sim.DrainEvents();
            Assert.That(ev.Select(e => e.GetType()), Is.EqualTo(new[] { typeof(FishReleased), typeof(FishDiscovered), typeof(CurrencyChanged) }));
            Assert.That(ev[1], Is.EqualTo(new FishDiscovered(1, 10) { Tick = ev[1].Tick }));
            var cc = (CurrencyChanged)ev[2];
            Assert.That((cc.Currency, cc.Delta, cc.Reason), Is.EqualTo((Currency.FISH_COIN, 10.0, CurrencyReason.FISH_DISCOVERY)));
            Assert.That(sim.State.Wallet.FishCoin, Is.EqualTo(10));
            Assert.That(sim.State.Fish.DiscoveredLevels, Is.EquivalentTo(new[] { 1 }));
            Assert.That(sim.State.Fish.HighestLevel, Is.EqualTo(1));

            sim.Apply(new ReleaseFish());
            Assert.That(sim.DrainEvents().Select(e => e.GetType()), Is.EqualTo(new[] { typeof(FishReleased) }));
            Assert.That(sim.State.Wallet.FishCoin, Is.EqualTo(10));
            Assert.That(sim.State.Stats.FishCoinEarned, Is.EqualTo(10));
        }

        // ------------------------------------------------------------------------------------- merge

        [Test]
        public void AC4_MergeFish_TwoLevel3_MakeLevel4()
        {
            var sim = NewSim();
            SetBoard(sim, 3, 0, 3, 3, 3);
            Assert.That(sim.Apply(new MergeFish(0, 2)).Ok, Is.True);
            var f = sim.State.Fish;
            Assert.That((f.Cells[0], f.Cells[2]), Is.EqualTo((0, 4)));
            var ev = sim.DrainEvents();
            Assert.That(ev.Select(e => e.GetType()), Is.EqualTo(new[] { typeof(FishMerged), typeof(FishDiscovered), typeof(CurrencyChanged) }));
            var m = (FishMerged)ev[0];
            Assert.That((m.Source, m.Target, m.NewLevel, m.Coins, m.Discovered), Is.EqualTo((0, 2, 4, 80.0, true)));
            Assert.That(m.MissilePct, Is.EqualTo(Db.FishLevels[3].MissilePct));
            Assert.That(((CurrencyChanged)ev[2]).Reason, Is.EqualTo(CurrencyReason.FISH_DISCOVERY));
            Assert.That(sim.State.Combat.MissileQueue.Single(), Is.EqualTo(0.169).Within(0.0005));
            Assert.That(sim.State.Combat.MissileQueue.Single(), Is.EqualTo(Db.FishLevels[3].MissilePct / 100));

            Assert.That(sim.Apply(new MergeFish(3, 4)).Ok, Is.True);
            ev = sim.DrainEvents();
            Assert.That(ev.Select(e => e.GetType()), Is.EqualTo(new[] { typeof(FishMerged), typeof(CurrencyChanged) }));
            var cc = (CurrencyChanged)ev[1];
            Assert.That((cc.Delta, cc.Reason), Is.EqualTo((1.6, CurrencyReason.FISH_MERGE)));
            Assert.That(((FishMerged)ev[0]).Discovered, Is.False);
            Assert.That(sim.State.Combat.MissileQueue.Count, Is.EqualTo(2), "FIFO, one per merge");
            Assert.That(sim.State.Wallet.FishCoin, Is.EqualTo(81.6));
            Assert.That((sim.State.Stats.Merges, sim.State.Fish.TotalMerges), Is.EqualTo((2L, 2L)));
        }

        [Test]
        public void AC4_MergeFish_Rejections()
        {
            var sim = NewSim();
            int cap = sim.FishMaxLvl;
            SetBoard(sim, 3, 4, cap, cap, 0, 5);
            AssertRejected(sim, new MergeFish(0, 1), RejectCode.LevelMismatch, FishRejectReasons.LEVEL_MISMATCH);
            AssertRejected(sim, new MergeFish(2, 3), RejectCode.Capped, FishRejectReasons.FISH_AT_LEVEL_CAP);
            AssertRejected(sim, new MergeFish(4, 5), RejectCode.InvalidTarget, FishRejectReasons.EMPTY_CELL);
            AssertRejected(sim, new MergeFish(5, 4), RejectCode.InvalidTarget, FishRejectReasons.EMPTY_CELL);
            AssertRejected(sim, new MergeFish(0, 0), RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
            AssertRejected(sim, new MergeFish(-1, 0), RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
            AssertRejected(sim, new MergeFish(0, 20), RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
            AssertRejected(sim, new MergeFish(int.MinValue, int.MaxValue), RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
        }

        [Test]
        public void AC5_MergeAll_FixedOrder()
        {
            var sim = NewSim();
            SetBoard(sim, 1, 1, 1, 1, 2, 2);
            Assert.That(sim.Apply(new MergeAll()).Ok, Is.True);
            var merged = sim.DrainEvents().OfType<FishMerged>().Select(m => (m.Source, m.Target, m.NewLevel)).ToArray();
            Assert.That(merged, Is.EqualTo(new[] { (0, 1, 2), (2, 3, 2), (1, 3, 3), (4, 5, 3), (3, 5, 4) }));
            var expect = new int[20]; expect[5] = 4;
            Assert.That(sim.State.Fish.Cells, Is.EqualTo(expect));
            Assert.That(sim.State.Combat.MissileQueue.Count, Is.EqualTo(5));
        }

        [Test]
        public void MergeAll_NothingToMerge_IsAccepted_AndEmitsNothing()
        {
            var sim = NewSim();
            SetBoard(sim, 1, 2, 3);
            string before = sim.StateHash();
            Assert.That(sim.FishMergeablePairs, Is.EqualTo(0));
            Assert.That(sim.Apply(new MergeAll()).Ok, Is.True);
            Assert.That(sim.DrainEvents(), Is.Empty);
            Assert.That(sim.StateHash(), Is.EqualTo(before));
        }

        [Test]
        public void AC12_MergeAll_RandomFullBoardsBelowCap_AlwaysMerge_CapBoardJams()
        {
            var rng = new Random(12);
            int cap = Db.Constants.FishMaxLvl0;
            for (int t = 0; t < 10_000; t++)
            {
                var sim = new Simulation(TestContent.NewState(), Db);
                SetBoard(sim, Enumerable.Range(0, 20).Select(_ => rng.Next(1, cap)).ToArray());
                Assert.That(sim.FishMergeablePairs, Is.GreaterThanOrEqualTo(1));
                sim.Apply(new MergeAll());
                Assert.That(sim.DrainEvents().OfType<FishMerged>().Count(), Is.GreaterThanOrEqualTo(1));
                Assert.That(FishBoard.MergeablePairs(sim.State, Db), Is.EqualTo(0), "MergeAll runs to a fixed point");
            }
            var full = NewSim();
            SetBoard(full, Enumerable.Repeat(cap, 20).ToArray());
            full.Apply(new MergeAll());
            Assert.That(full.DrainEvents(), Is.Empty);
        }

        [Test]
        public void GreedyPlay_HoldsAtMostCellsMinusCapPlusOneCapFish()
        {
            // Making a cap fish needs `cap` cells at its peak (cap-1, ..., 2, 1, 1), so with ReleaseMax + MergeAll a
            // 20-cell board stops at 11 cap-10 fish plus one each of levels 1-9 (the 05 section 11 jam), never 20.
            // This reachable ceiling is the design (issue #247, 05 section 4.3); tools/fish_model.py checks the day it is reached.
            var sim = NewSim();
            int cap = sim.FishMaxLvl, cells = sim.State.Fish.Cells.Length;
            for (int i = 0; i < 2000; i++)
            {
                sim.State.Fish.TankCount = sim.FishTankCap;
                sim.Apply(new ReleaseMax());
                sim.Apply(new MergeAll());
                sim.State.Combat.MissileQueue.Clear();
            }
            Assert.That(sim.State.Fish.Cells.Count(c => c == cap), Is.EqualTo(cells - cap + 1));
            Assert.That(sim.State.Fish.Cells.Where(c => c < cap).OrderBy(c => c), Is.EqualTo(Enumerable.Range(1, cap - 1)));
            Assert.That(sim.FishCanRelease, Is.False);
            Assert.That(sim.FishMergeablePairs, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------------------------- bulk release, discard, move

        [Test]
        public void AC6_ReleaseMax()
        {
            var sim = NewSim();
            sim.State.Fish.TankCount = 10;
            SetBoard(sim, Enumerable.Repeat(2, 13).ToArray());   // 7 empty
            Assert.That(sim.Apply(new ReleaseMax()).Ok, Is.True);
            Assert.That(sim.DrainEvents().OfType<FishReleased>().Count(), Is.EqualTo(7));
            Assert.That((sim.State.Fish.TankCount, FishSpawnCounter(sim)), Is.EqualTo((3, 7L)));

            var b = NewSim();
            b.State.Fish.TankCount = 3;
            b.Apply(new ReleaseMax());
            Assert.That(b.DrainEvents().OfType<FishReleased>().Count(), Is.EqualTo(3));
            Assert.That(b.State.Fish.TankCount, Is.EqualTo(0));

            var log = new ListCommandLog();
            var c = NewSim();
            c.CommandLog = log;
            c.State.Fish.TankCount = 0;
            Assert.That(c.Apply(new ReleaseMax()).Ok, Is.True, "zero releases is not a rejection");
            Assert.That(log.Count, Is.EqualTo(1), "bulk commands are single log entries");
        }

        [Test]
        public void AC7_DiscardFish()
        {
            var sim = NewSim();
            SetBoard(sim, 0, 10);
            Assert.That(sim.Apply(new DiscardFish(1)).Ok, Is.True);
            var ev = sim.DrainEvents();
            Assert.That(ev[0], Is.EqualTo(new FishDiscarded(1, 10, 512) { Tick = ev[0].Tick }));
            var cc = (CurrencyChanged)ev[1];
            Assert.That((cc.Delta, cc.Reason), Is.EqualTo((512.0, CurrencyReason.FISH_MERGE)));
            Assert.That(sim.State.Fish.Cells[1], Is.EqualTo(0));
            Assert.That(sim.State.Stats.FishDiscards, Is.EqualTo(1));
            AssertRejected(sim, new DiscardFish(1), RejectCode.InvalidTarget, FishRejectReasons.EMPTY_CELL);
            AssertRejected(sim, new DiscardFish(20), RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
        }

        [Test]
        public void AC7_MoveFish()
        {
            var sim = NewSim();
            SetBoard(sim, 7, 0, 7);
            double dps = sim.FishBoardDpsPct;
            string streams = string.Join(",", sim.State.Rng.Values.Select(r => r.Counter));
            Assert.That(sim.Apply(new MoveFish(0, 1)).Ok, Is.True);
            Assert.That(sim.DrainEvents().Single(), Is.EqualTo(new FishMoved(0, 1, 7) { Tick = sim.Tick }));
            Assert.That(sim.State.Fish.Cells.Take(3), Is.EqualTo(new[] { 0, 7, 7 }));
            Assert.That(sim.FishBoardDpsPct, Is.EqualTo(dps));
            Assert.That(string.Join(",", sim.State.Rng.Values.Select(r => r.Counter)), Is.EqualTo(streams), "no draw");
            AssertRejected(sim, new MoveFish(1, 2), RejectCode.InvalidTarget, FishRejectReasons.CELL_OCCUPIED);
            AssertRejected(sim, new MoveFish(0, 3), RejectCode.InvalidTarget, FishRejectReasons.EMPTY_CELL);
            AssertRejected(sim, new MoveFish(1, 1), RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
            AssertRejected(sim, new MoveFish(1, -3), RejectCode.BadArgument, FishRejectReasons.BAD_CELL);
        }

        // ------------------------------------------------------------------------------------- passive DPS

        [Test]
        public void AC8_FishBoardDpsPct_SumsTheTable()
        {
            var sim = NewSim();
            SetBoard(sim, 10, 10, 8, 6, 5);
            double table = 0;
            foreach (int n in new[] { 10, 10, 8, 6, 5 }) table += Db.FishLevels[n - 1].BoardDpsPct;
            Assert.That(sim.FishBoardDpsPct, Is.EqualTo(table).Within(1e-9));
            // 05 AC8 at the content's FISH_BOARD_DPS_FACTOR (0.77: 410.5, fishMult 5.105).
            double missile = 0;
            foreach (int n in new[] { 10, 10, 8, 6, 5 }) missile += Db.FishLevels[n - 1].MissilePct;
            double expected = Db.Constants.FishBoardDpsFactor * missile;
            Assert.That(sim.FishBoardDpsPct, Is.EqualTo(expected).Within(1e-9));
            Assert.That(1 + sim.FishBoardDpsPct / 100, Is.EqualTo(1 + expected / 100).Within(1e-12));
            Assert.That(Db.FishLevels[9].BoardDpsPct, Is.EqualTo(Db.Constants.FishBoardDpsFactor * Db.FishLevels[9].MissilePct));
        }

        // ------------------------------------------------------------------------------------- dungeon lock (issue #40)

        [Test]
        public void AC16_DungeonLocksEveryBoardCommand_TankStillRefills()
        {
            var sim = NewSim();
            SetBoard(sim, 1, 1, 2);
            sim.State.Fish.TankCount = 5;
            sim.State.Combat.MissileQueue.Add(0.075);
            double dps = sim.FishBoardDpsPct;
            sim.State.Combat.Mode = CombatMode.Dungeon;
            foreach (var c in new Command[]
            {
                new ReleaseFish(), new ReleaseMax(), new MergeFish(0, 1), new MergeFish(0, 0), new MergeAll(),
                new MoveFish(2, 5), new MoveFish(-1, 99), new DiscardFish(0), new DiscardFish(19),
            })
                AssertRejected(sim, c, RejectCode.Locked, FishRejectReasons.BOARD_LOCKED);
            Assert.That(sim.FishCanRelease, Is.False);
            sim.Advance(60);
            Assert.That(sim.State.Fish.TankCount, Is.EqualTo(6), "the tank keeps refilling");
            Assert.That(sim.FishBoardDpsPct, Is.EqualTo(dps));
            sim.State.Combat.Mode = CombatMode.Stage;
            Assert.That(sim.State.Combat.MissileQueue, Is.EqualTo(new[] { 0.075 }));
            Assert.That(sim.Apply(new MergeFish(0, 1)).Ok, Is.True);
        }

        // ------------------------------------------------------------------------------------- lowered cap (issue #41)

        [Test]
        public void AC17_FishAboveTheCap_StayAndCannotMerge()
        {
            var sim = NewSim();
            Assert.That(sim.FishMaxLvl, Is.EqualTo(10));
            SetBoard(sim, 12, 12, 1, 1);
            Discover(sim, 12);
            double dps = sim.FishBoardDpsPct;
            Assert.That(dps, Is.EqualTo(2 * Db.FishLevels[11].BoardDpsPct + 2 * Db.FishLevels[0].BoardDpsPct));
            AssertRejected(sim, new MergeFish(0, 1), RejectCode.Capped, FishRejectReasons.FISH_AT_LEVEL_CAP);
            sim.Apply(new MergeAll());
            Assert.That(sim.DrainEvents().OfType<FishMerged>().Single().NewLevel, Is.EqualTo(2));
            Assert.That(sim.State.Fish.Cells.Take(2), Is.EqualTo(new[] { 12, 12 }));
            FishBoard.Validate(sim.State, Db);
        }

        // ------------------------------------------------------------------------------------- determinism (05 section 12)

        [Test]
        public void AC10_Fuzz200k_CounterInvariantHolds_ValidateNeverThrows()
        {
            var rng = new Random(103);
            var sim = NewSim();
            FishBoard.Validate(sim.State, Db);
            var counts = new int[7];
            for (int i = 0; i < 200_000; i++)
            {
                int kind = rng.Next(7);
                Command c = kind switch
                {
                    0 => new ReleaseFish(),
                    1 => new ReleaseMax(),
                    2 => new MergeFish(rng.Next(-1, 21), rng.Next(-1, 21)),
                    3 => new MergeAll(),
                    4 => new MoveFish(rng.Next(-1, 21), rng.Next(-1, 21)),
                    5 => new DiscardFish(rng.Next(-1, 21)),
                    _ => null,
                };
                if (c == null) sim.Advance(rng.Next(1, 120));
                else if (sim.Apply(c).Ok) counts[kind]++;
                if (i % 5000 == 0) sim.State.Combat.Mode = sim.State.Combat.Mode == CombatMode.Stage && rng.Next(4) == 0 ? CombatMode.Dungeon : CombatMode.Stage;
                if ((i & 1023) == 0) sim.DrainEvents();
                if (i % 16 == 0) sim.State.Combat.MissileQueue.Clear();   // stands in for combat's drain
                FishBoard.Validate(sim.State, Db);
            }
            Assert.That(FishSpawnCounter(sim), Is.EqualTo(sim.State.Stats.FishReleased));
            Assert.That(sim.State.Fish.TotalMerges, Is.EqualTo(sim.State.Stats.Merges));
            for (int k = 0; k < 6; k++) Assert.That(counts[k], Is.GreaterThan(0), "every board command succeeded at least once: kind " + k);
            Assert.That(sim.State.Fish.HighestLevel, Is.GreaterThanOrEqualTo(5));
        }

        [Test]
        public void StreamIsolation_FishDrawsTouchOnlyFishSpawn()
        {
            var sim = NewSim();
            var before = sim.State.Rng.ToDictionary(kv => kv.Key, kv => kv.Value.Counter);
            sim.State.Fish.TankCount = 10;
            sim.Apply(new ReleaseMax());
            sim.Apply(new MergeAll());
            foreach (var kv in sim.State.Rng)
                Assert.That(kv.Value.Counter, Is.EqualTo(kv.Key == RngStreams.FishSpawn ? before[kv.Key] + 10 : before[kv.Key]), kv.Key);
        }

        [Test]
        public void Replay_OfAFishSession_ReproducesTheStateHash()
        {
            var genesis = TestContent.NewState();
            var sim = new Simulation(StateCopy.DeepClone(genesis), Db);
            var log = new ListCommandLog();
            sim.CommandLog = log;
            var rng = new Random(7);
            sim.Apply(new ApplyServerTime(TestContent.T0));
            for (int i = 0; i < 3000; i++)
            {
                sim.Advance(rng.Next(0, 90));
                switch (rng.Next(6))
                {
                    case 0: sim.Apply(new ReleaseFish()); break;
                    case 1: sim.Apply(new ReleaseMax()); break;
                    case 2: sim.Apply(new MergeFish(rng.Next(20), rng.Next(20))); break;
                    case 3: sim.Apply(new MergeAll()); break;
                    case 4: sim.Apply(new MoveFish(rng.Next(20), rng.Next(20))); break;
                    default: sim.Apply(new DiscardFish(rng.Next(20))); break;
                }
            }
            sim.Advance(33);
            var replay = new Simulation(StateCopy.DeepClone(genesis), Db);
            replay.Replay(log.Records, sim.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(sim.StateHash()));
            Assert.That(sim.State.Stats.FishReleased, Is.GreaterThan(0));
        }

        [Test]
        public void SaveRoundTrip_KeepsTheBoard_AndResimulatesIdentically()
        {
            var sim = NewSim();
            sim.State.Fish.TankCount = 10;
            sim.Apply(new ReleaseMax());
            sim.Apply(new MergeAll());
            sim.Advance(45);
            Assert.That(sim.State.Combat.MissileQueue, Is.Not.Empty);
            var (loaded, _, _) = SaveSystem.DecodeFull(SaveSerializer.Encode(sim.State), Db);
            var f = loaded.Fish;
            Assert.That(f.Cells, Is.EqualTo(sim.State.Fish.Cells));
            Assert.That((f.TankCount, f.TankRefillTick, f.TankReleaseLevel, f.HighestLevel, f.TotalMerges, f.AutoMergeEnabled),
                        Is.EqualTo((sim.State.Fish.TankCount, sim.State.Fish.TankRefillTick, 1, sim.State.Fish.HighestLevel, sim.State.Fish.TotalMerges, false)));
            Assert.That(f.DiscoveredLevels, Is.EquivalentTo(sim.State.Fish.DiscoveredLevels));
            Assert.That(loaded.Combat.MissileQueue, Is.EqualTo(sim.State.Combat.MissileQueue));
            var again = new Simulation(loaded, Db);
            Assert.That(again.StateHash(), Is.EqualTo(sim.StateHash()));
            foreach (var s in new[] { sim, again }) { s.Apply(new ReleaseMax()); s.Advance(600); s.Apply(new ReleaseMax()); s.Apply(new MergeAll()); }
            Assert.That(again.StateHash(), Is.EqualTo(sim.StateHash()));
        }

        [Test]
        public void Validate_ReportsBrokenInvariants()
        {
            var s = TestContent.NewState();
            Assert.That(FishBoard.FirstViolation(s, Db), Is.Null);
            s.Rng[RngStreams.FishSpawn].Counter = 3;
            Assert.That(FishBoard.FirstViolation(s, Db), Does.Contain("fish_spawn.Counter"));
            Assert.Throws<InvalidOperationException>(() => FishBoard.Validate(s, Db));
            s.Rng[RngStreams.FishSpawn].Counter = 0;
            s.Fish.Cells[4] = 2;
            Assert.That(FishBoard.FirstViolation(s, Db), Does.Contain("DiscoveredLevels"));
            s.Fish.Cells[4] = 0;
            s.Fish.TankCount = 11;
            Assert.That(FishBoard.FirstViolation(s, Db), Does.Contain("TankCount"));
        }
    }
}
