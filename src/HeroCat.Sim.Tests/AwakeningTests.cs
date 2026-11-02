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
    /// <summary>03 section 5.3 and acceptance 12: the Awaken command, its gates, cost, effects and queries (#157).</summary>
    public class AwakeningTests
    {
        static ContentDb Mvp => TestContent.Mvp;

        // 03 section 5.3 / 00 section 6.5, typed in from the spec so a content edit cannot move the test with it.
        static readonly int[] Gates = { 50, 150, 300, 500, 800 };
        static readonly long[] Costs = { 100, 300, 800, 2000, 5000 };
        static readonly int[] CompanionSlotsAfter = { 2, 3, 4, 4, 4 };
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

        static void AssertRejectIsPure(Simulation sim, RejectCode code, string reason)
        {
            string hash = sim.StateHash();
            var r = sim.Apply(new Awaken());
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
            Assert.That(Mvp.Awakening.Select(a => a.CompanionSlotsAfter), Is.EqualTo(CompanionSlotsAfter));
            Assert.That(Mvp.Awakening.Select(a => a.RuneSlotsAfter), Is.EqualTo(RuneSlotsAfter));
        }

        [Test]
        public void AC12_StageGate_ThenRockCandy_ThenChargesExactly100_AndOpensCompanionSlot2()
        {
            var sim = Sim(highestCleared: 49, rockCandy: 1000);
            Assert.That(sim.Query.NextAwakenStageGate, Is.EqualTo(50));
            Assert.That(sim.Query.CanAwaken, Is.False);
            AssertRejectIsPure(sim, RejectCode.Locked, CombatRejectReasons.AWAKEN_STAGE_NOT_REACHED);

            sim.State.Progress.HighestCleared = 50;
            sim.State.Wallet.RockCandy = 99;
            AssertRejectIsPure(sim, RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_ROCK_CANDY);
            Assert.That(CombatRejectReasons.NOT_ENOUGH_ROCK_CANDY, Is.EqualTo(CollectionRejectReasons.NotEnough(Currency.ROCK_CANDY)),
                        "one localization key for both Rock Candy sinks");

            sim.State.Wallet.RockCandy = 100;
            Assert.That(sim.Query.CanAwaken, Is.True);
            Assert.That(sim.Query.CompanionSlotsUnlocked, Is.EqualTo(1), "slot 1 is open from the start");
            Assert.That(sim.Query.ResearchUnlocked, Is.False);
            double dps = sim.Query.RatedDps;

            Assert.That(sim.Apply(new Awaken()).Ok, Is.True);
            Assert.That(sim.State.Wallet.RockCandy, Is.EqualTo(0));
            Assert.That(sim.State.Hero.AwakenRank, Is.EqualTo(1));
            var ev = sim.DrainEvents();
            Assert.That(ev.Select(e => e.GetType()), Is.EqualTo(new[] { typeof(CurrencyChanged), typeof(Awakened), typeof(QuestCompleted) }));
            Assert.That(((QuestCompleted)ev[2]).QuestId, Is.EqualTo("ach_awakening_01"), "the Awakening ladder (06 section 5.4, issue #171)");
            Assert.That(ev[0], Is.EqualTo(new CurrencyChanged(Currency.ROCK_CANDY, -100, 0, CurrencyReason.AWAKENING)));
            Assert.That(ev[1], Is.EqualTo(new Awakened(1)));
            Assert.That(sim.Query.CompanionSlotsUnlocked, Is.EqualTo(2));
            Assert.That(sim.Query.ResearchUnlocked, Is.True, "Research opens at rank 1 (00 decision 11)");
            Assert.That(sim.Query.AwakenMult, Is.EqualTo(1.5));
            Assert.That(sim.Query.RatedDps, Is.EqualTo(dps * 1.5).Within(1e-12).Percent, "DPS cache marked dirty");
            Assert.That(sim.GetSnapshot().AwakenRank, Is.EqualTo(1));
            Assert.That(sim.GetSnapshot().RockCandy, Is.EqualTo(0));
        }

        [Test]
        public void EveryRank_InOrder_ThenMaxed()
        {
            var sim = Sim(highestCleared: 0, rockCandy: 0);
            for (int r = 0; r < Gates.Length; r++)
            {
                Assert.That(sim.Query.AwakenRank, Is.EqualTo(r));
                Assert.That(sim.Query.NextAwakenStageGate, Is.EqualTo(Gates[r]));
                Assert.That(sim.Query.NextAwakenCost, Is.EqualTo(Costs[r]));

                sim.State.Progress.HighestCleared = Gates[r] - 1;
                sim.State.Wallet.RockCandy = Costs[r] + 7;
                AssertRejectIsPure(sim, RejectCode.Locked, CombatRejectReasons.AWAKEN_STAGE_NOT_REACHED);
                sim.State.Progress.HighestCleared = Gates[r];
                sim.State.Wallet.RockCandy = Costs[r] - 1;
                AssertRejectIsPure(sim, RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_ROCK_CANDY);
                sim.State.Wallet.RockCandy = Costs[r] + 7;

                Assert.That(sim.Apply(new Awaken()).Ok, Is.True, "rank " + (r + 1));
                Assert.That(sim.State.Wallet.RockCandy, Is.EqualTo(7));
                Assert.That(sim.DrainEvents().OfType<Awakened>().Single().Rank, Is.EqualTo(r + 1));
                Assert.That(sim.Query.CompanionSlotsUnlocked, Is.EqualTo(CompanionSlotsAfter[r]));
                Assert.That(sim.Query.RuneSlotsUnlocked, Is.EqualTo(RuneSlotsAfter[r]));
                Assert.That(sim.Query.ResearchUnlocked, Is.True);
                if (r + 1 == 2) Assert.That(sim.Query.AwakenMult, Is.EqualTo(2.25), "1.5^2 is exact in binary");
            }
            Assert.That(sim.Query.AwakenMult, Is.EqualTo(Math.Pow(1.5, 5)).Within(1e-12).Percent);
            Assert.That(sim.Query.NextAwakenStageGate, Is.Null);
            Assert.That(sim.Query.NextAwakenCost, Is.EqualTo(0));
            Assert.That(sim.Query.CanAwaken, Is.False);

            sim.State.Progress.HighestCleared = 100_000;
            sim.State.Wallet.RockCandy = 1_000_000;
            AssertRejectIsPure(sim, RejectCode.Capped, CombatRejectReasons.AWAKEN_MAX_RANK);
        }

        [Test]
        public void MaxRank_IsCheckedBeforeTheGate_AndTheGateBeforeTheCost()   // 03 section 5.3 order
        {
            var sim = Sim(highestCleared: 0, rockCandy: 0);
            AssertRejectIsPure(sim, RejectCode.Locked, CombatRejectReasons.AWAKEN_STAGE_NOT_REACHED);
            sim.State.Hero.AwakenRank = Gates.Length;
            AssertRejectIsPure(sim, RejectCode.Capped, CombatRejectReasons.AWAKEN_MAX_RANK);
            sim.State.Hero.AwakenRank = Gates.Length + 3;   // out of range (02 rule P3 clamps on load): still maxed
            AssertRejectIsPure(sim, RejectCode.Capped, CombatRejectReasons.AWAKEN_MAX_RANK);
            Assert.That(sim.Query.AwakenRank, Is.EqualTo(Gates.Length));
            Assert.That(sim.Query.CompanionSlotsUnlocked, Is.EqualTo(4));
        }

        [Test]
        public void TheGateReadsHighestCleared_NotTheFoughtStage()   // 03 section 5.3: farming a side stage never lowers it
        {
            var sim = Sim(highestCleared: 60, rockCandy: 100);
            sim.State.Combat.Stage = 61;
            Assert.That(sim.Apply(new SelectStage(10)).Ok, Is.True);
            Assert.That(sim.Apply(new Awaken()).Ok, Is.True);
            Assert.That(sim.State.Combat.Selected, Is.EqualTo(10), "Awaken leaves the stage alone");
        }

        [Test]
        public void M0Content_HasTheRanks_ButNoResearch()   // Awakening is M0 (decision #249 item 9b); Research stays MVP
        {
            var sim = Sim(highestCleared: 1000, rockCandy: 1_000_000, db: TestContent.Db);
            Assert.That(TestContent.Db.Awakening.Select(a => a.Rank), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
            Assert.That(sim.Query.NextAwakenStageGate, Is.EqualTo(50));
            Assert.That(sim.Apply(new Awaken()).Ok, Is.True);
            Assert.That(sim.Query.AwakenMult, Is.EqualTo(1.5));
            Assert.That(sim.Query.ResearchUnlocked, Is.False, "no RESEARCH feature_unlocks row in M0");
        }

        [Test]
        public void Queries_DoNotMutate()
        {
            var sim = Sim(highestCleared: 200, rockCandy: 500);
            string hash = sim.StateHash();
            _ = sim.Query.CanAwaken;
            _ = sim.Query.NextAwakenCost;
            _ = sim.Query.NextAwakenStageGate;
            _ = sim.Query.CompanionSlotsUnlocked;
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
                if (sim.Apply(new Awaken()).Ok) ok++; else rejected++;
                if (rng.Next(3) == 0) sim.Apply(new TrainStatMax(Stat.ATK));
            }
            sim.Advance(23);
            Assert.That(ok, Is.EqualTo(3), "3000 candy buys ranks 1-3 (1,200), not rank 4 (2,000 more)");
            Assert.That(rejected, Is.GreaterThan(0));

            var lines = log.Records.Select(CommandCodec.Encode).ToArray();
            Assert.That(lines.Count(l => l.Contains("\"type\":\"Awaken\"")), Is.EqualTo(60));
            var decoded = lines.Select(CommandCodec.Decode).ToArray();
            var replay = new Simulation(StateCopy.DeepClone(genesis), Mvp);
            replay.Replay(decoded, sim.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(sim.StateHash()));
            Assert.That(replay.State.Hero.AwakenRank, Is.EqualTo(3));
        }
    }
}
