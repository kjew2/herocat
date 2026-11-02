using System;
using System.Linq;
using HeroCat.Runtime.Replay;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>03 section 5.3 and acceptance 12: Awaken(slot), its gates, cost, per-slot effects and queries (#157, 00 decision 109).</summary>
    public class AwakeningTests
    {
        static ContentDb Mvp => TestContent.Mvp;

        // 03 section 5.3 / 00 section 6.5, typed in from the spec so a content edit cannot move the test with it.
        static readonly int[] Gates = { 50, 150, 300, 500, 800 };
        static readonly long[] Costs = { 100, 300, 800, 2000, 5000 };
        static readonly int[] RuneSlotsAfter = { 0, 0, 0, 3, 6 };

        static Simulation Sim(int highestCleared = 0, long rockCandy = 0, ContentDb db = null)
        {
            var sim = new Simulation(TestContent.NewState(), db ?? Mvp);
            sim.Apply(new ApplyServerTime(TestContent.T0));
            sim.State.Progress.HighestCleared = highestCleared;
            sim.State.Wallet.RockCandy = rockCandy;
            sim.Query.MarkDirty();
            sim.DrainEvents();
            return sim;
        }

        static void AssertRejectIsPure(Simulation sim, RejectCode code, string reason, int slot = 0)
        {
            string hash = sim.StateHash();
            var r = sim.Apply(new Awaken(slot));
            Assert.That((r.Code, r.Reason), Is.EqualTo((code, reason)));
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
            Assert.That(sim.DrainEvents(), Is.Empty);
        }

        [Test]
        public void Content_MatchesTheSpecTable()   // 03 section 5.3, 00 section 6.5
        {
            Assert.That(Mvp.Awakening.Select(a => a.Rank), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
            Assert.That(Mvp.Awakening.Select(a => a.StageGate), Is.EqualTo(Gates));
            Assert.That(Mvp.Awakening.Select(a => a.RockCandyCost), Is.EqualTo(Costs));
            Assert.That(Mvp.Awakening.Select(a => a.Mult), Is.All.EqualTo(1.5));
            Assert.That(Mvp.Awakening.Select(a => a.RuneSlotsAfter), Is.EqualTo(RuneSlotsAfter));
        }

        [Test]
        public void AC12_StageGate_ThenRockCandy_ThenChargesExactly100_AndMultipliesOnlyThatSlot()
        {
            var sim = Sim(highestCleared: 49, rockCandy: 1000);
            Assert.That(sim.Query.NextAwakenStageGate(2), Is.EqualTo(50));
            Assert.That(sim.Query.CanAwaken(2), Is.False);
            AssertRejectIsPure(sim, RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT, slot: -1);
            AssertRejectIsPure(sim, RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT, slot: Mvp.Constants.PartySlots);
            AssertRejectIsPure(sim, RejectCode.Locked, CombatRejectReasons.AWAKEN_STAGE_NOT_REACHED);

            sim.State.Progress.HighestCleared = 50;
            sim.State.Wallet.RockCandy = 99;
            AssertRejectIsPure(sim, RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_ROCK_CANDY);
            Assert.That(CombatRejectReasons.NOT_ENOUGH_ROCK_CANDY, Is.EqualTo(CollectionRejectReasons.NotEnough(Currency.ROCK_CANDY)),
                        "one localization key for both Rock Candy sinks");

            sim.State.Wallet.RockCandy = 100;
            Assert.That(sim.Query.CanAwaken(2), Is.True);
            Assert.That(sim.Query.ResearchUnlocked, Is.False);
            var before = sim.Query.Power;
            double dps = sim.Query.RatedDps, slot2 = before.SlotDps(2) * before.PartyBuff;

            Assert.That(sim.Apply(new Awaken(2)).Ok, Is.True);
            Assert.That(sim.State.Wallet.RockCandy, Is.EqualTo(0));
            Assert.That(sim.State.Party.Slots.Select(x => x.AwakenRank), Is.EqualTo(new[] { 0, 0, 1, 0, 0 }), "the slot's rank only");
            var ev = sim.DrainEvents();
            Assert.That(ev.Select(e => e.GetType()), Is.EqualTo(new[] { typeof(CurrencyChanged), typeof(Awakened), typeof(QuestCompleted) }));
            Assert.That(((QuestCompleted)ev[2]).QuestId, Is.EqualTo("ach_awakening_01"), "the Awakening ladder (06 section 5.4, issue #171)");
            Assert.That(ev[0], Is.EqualTo(new CurrencyChanged(Currency.ROCK_CANDY, -100, 0, CurrencyReason.AWAKENING)));
            Assert.That(ev[1], Is.EqualTo(new Awakened(2, 1)));
            Assert.That(sim.Query.ResearchUnlocked, Is.True, "Research opens when any slot reaches rank 1 (00 decisions 11, 109)");
            Assert.That(sim.Query.AwakenMult(2), Is.EqualTo(1.5));
            Assert.That(sim.Query.AwakenMult(0), Is.EqualTo(1.0));
            Assert.That(sim.Query.HighestAwakenRank, Is.EqualTo(1));
            Assert.That(sim.Query.RatedDps, Is.EqualTo(dps + 0.5 * slot2).Within(1e-12).Percent,
                        "x1.5 on slot 2's damage only; DPS cache marked dirty");
            Assert.That(sim.GetSnapshot().Slot(2).AwakenRank, Is.EqualTo(1));
            Assert.That(sim.GetSnapshot().Slot(0).AwakenRank, Is.EqualTo(0));
            Assert.That(sim.GetSnapshot().RockCandy, Is.EqualTo(0));
        }

        [Test]
        public void EveryRank_InOrder_ThenMaxed()
        {
            var sim = Sim(highestCleared: 0, rockCandy: 0);
            for (int r = 0; r < Gates.Length; r++)
            {
                Assert.That(sim.Query.AwakenRank(0), Is.EqualTo(r));
                Assert.That(sim.Query.NextAwakenStageGate(0), Is.EqualTo(Gates[r]));
                Assert.That(sim.Query.NextAwakenCost(0), Is.EqualTo(Costs[r]));

                sim.State.Progress.HighestCleared = Gates[r] - 1;
                sim.State.Wallet.RockCandy = Costs[r] + 7;
                AssertRejectIsPure(sim, RejectCode.Locked, CombatRejectReasons.AWAKEN_STAGE_NOT_REACHED);
                sim.State.Progress.HighestCleared = Gates[r];
                sim.State.Wallet.RockCandy = Costs[r] - 1;
                AssertRejectIsPure(sim, RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_ROCK_CANDY);
                sim.State.Wallet.RockCandy = Costs[r] + 7;

                Assert.That(sim.Apply(new Awaken(0)).Ok, Is.True, "rank " + (r + 1));
                Assert.That(sim.State.Wallet.RockCandy, Is.EqualTo(7));
                Assert.That(sim.DrainEvents().OfType<Awakened>().Single(), Is.EqualTo(new Awakened(0, r + 1)));
                Assert.That(sim.Query.RuneSlotsUnlocked, Is.EqualTo(RuneSlotsAfter[r]), "rune slots read the highest slot rank");
                Assert.That(sim.Query.ResearchUnlocked, Is.True);
                if (r + 1 == 2) Assert.That(sim.Query.AwakenMult(0), Is.EqualTo(2.25), "1.5^2 is exact in binary");
            }
            Assert.That(sim.Query.AwakenMult(0), Is.EqualTo(Math.Pow(1.5, 5)).Within(1e-12).Percent);
            Assert.That(sim.Query.NextAwakenStageGate(0), Is.Null);
            Assert.That(sim.Query.NextAwakenCost(0), Is.EqualTo(0));
            Assert.That(sim.Query.CanAwaken(0), Is.False);
            Assert.That(sim.Query.NextAwakenStageGate(1), Is.EqualTo(Gates[0]), "every other slot still starts at rank 0");

            sim.State.Progress.HighestCleared = 100_000;
            sim.State.Wallet.RockCandy = 1_000_000;
            AssertRejectIsPure(sim, RejectCode.Capped, CombatRejectReasons.AWAKEN_MAX_RANK);
        }

        [Test]
        public void MaxRank_IsCheckedBeforeTheGate_AndTheGateBeforeTheCost()   // 03 section 5.3 order
        {
            var sim = Sim(highestCleared: 0, rockCandy: 0);
            AssertRejectIsPure(sim, RejectCode.Locked, CombatRejectReasons.AWAKEN_STAGE_NOT_REACHED);
            sim.State.Party.Slots[0].AwakenRank = Gates.Length;
            AssertRejectIsPure(sim, RejectCode.Capped, CombatRejectReasons.AWAKEN_MAX_RANK);
            sim.State.Party.Slots[0].AwakenRank = Gates.Length + 3;   // out of range (02 rule P3 clamps on load): still maxed
            AssertRejectIsPure(sim, RejectCode.Capped, CombatRejectReasons.AWAKEN_MAX_RANK);
            Assert.That(sim.Query.AwakenRank(0), Is.EqualTo(Gates.Length));
        }

        [Test]
        public void TheGateReadsHighestCleared_NotTheFoughtStage()   // 03 section 5.3: farming a side stage never lowers it
        {
            var sim = Sim(highestCleared: 60, rockCandy: 100);
            sim.State.Combat.Stage = 61;
            Assert.That(sim.Apply(new SelectStage(10)).Ok, Is.True);
            Assert.That(sim.Apply(new Awaken(4)).Ok, Is.True);
            Assert.That(sim.State.Combat.Selected, Is.EqualTo(10), "Awaken leaves the stage alone");
        }

        [Test]
        public void M0Content_HasNoRanks()   // Awakening is MVP; the M0 build has no awakening rows
        {
            var sim = Sim(highestCleared: 1000, rockCandy: 1_000_000, db: TestContent.Db);
            Assert.That(TestContent.Db.Awakening, Is.Empty);
            AssertRejectIsPure(sim, RejectCode.Capped, CombatRejectReasons.AWAKEN_MAX_RANK);
            Assert.That(sim.Query.NextAwakenStageGate(0), Is.Null);
            Assert.That(sim.Query.RuneSlotsUnlocked, Is.EqualTo(0));
            Assert.That(sim.Query.AwakenMult(0), Is.EqualTo(1.0));
            Assert.That(sim.Query.ResearchUnlocked, Is.False, "no feature_unlocks rows in M0");
        }

        [Test]
        public void Queries_DoNotMutate()
        {
            var sim = Sim(highestCleared: 200, rockCandy: 500);
            string hash = sim.StateHash();
            for (int i = 0; i < 5; i++)
            {
                _ = sim.Query.CanAwaken(i);
                _ = sim.Query.NextAwakenCost(i);
                _ = sim.Query.NextAwakenStageGate(i);
            }
            _ = sim.Query.HighestAwakenRank;
            _ = sim.Query.RuneSlotsUnlocked;
            _ = sim.Query.ResearchUnlocked;
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
        }

        [Test]
        public void Replay_ThroughTheJsonlCodec_ReproducesTheStateHash()   // 01 section 2.2 rule 4, 2.6
        {
            var genesis = TestContent.NewState();
            genesis.Progress.HighestCleared = 900;
            genesis.Wallet.RockCandy = 3000;
            var sim = new Simulation(StateCopy.DeepClone(genesis), Mvp);
            var log = new ListCommandLog();
            sim.CommandLog = log;
            sim.Apply(new ApplyServerTime(TestContent.T0));
            var rng = new Random(157);
            int ok = 0, rejected = 0;
            for (int i = 0; i < 60; i++)
            {
                sim.Advance(rng.Next(0, 90));
                if (sim.Apply(new Awaken(rng.Next(0, 2))).Ok) ok++; else rejected++;
                if (rng.Next(3) == 0) sim.Apply(new TrainStatMax(Stat.ATK));
            }
            sim.Advance(23);
            Assert.That(ok, Is.InRange(3, 6), "3000 candy over two slots: ranks of 100, 300 and 800 each, never a 2,000 rank 4");
            Assert.That(rejected, Is.GreaterThan(0));

            var lines = log.Records.Select(CommandCodec.Encode).ToArray();
            Assert.That(lines.Count(l => l.Contains("\"type\":\"Awaken\"")), Is.EqualTo(60));
            var decoded = lines.Select(CommandCodec.Decode).ToArray();
            var replay = new Simulation(StateCopy.DeepClone(genesis), Mvp);
            replay.Replay(decoded, sim.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(sim.StateHash()));
            Assert.That(replay.State.Party.Slots[0].AwakenRank + replay.State.Party.Slots[1].AwakenRank, Is.EqualTo(ok));
        }
    }
}
