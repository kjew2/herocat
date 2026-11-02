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
    /// <summary>03 section 6 and acceptance 13: the Research command, its gates, costs, effects and queries (#158).</summary>
    public class ResearchTests
    {
        static ContentDb Mvp => TestContent.Mvp;

        // 03 section 6, typed in from the spec so a content edit cannot move the test with it:
        // id, effect, effect per level, max level, RES_COST0, RES_COST_G, cost of the last level (3 significant figures).
        static readonly (string id, ResearchEffect effect, double per, int max, double cost0, double g, double last)[] Table =
        {
            ("res_atk", ResearchEffect.RESEARCH_PCT, 0.08, 50, 150, 1.20, 1.14e6),   // 0.08: #249 item 1
            ("res_gold_gain", ResearchEffect.GOLD_MULT, 0.05, 20, 250, 1.40, 1.49e5),
            ("res_mp_pool", ResearchEffect.MP_POOL, 20, 10, 200, 1.50, 7.69e3),
            ("res_mp_regen", ResearchEffect.MP_REGEN, 0.5, 10, 300, 1.50, 1.15e4),
            ("res_tank_cap", ResearchEffect.TANK_CAP, 5, 10, 200, 1.50, 7.69e3),
            ("res_tank_refill", ResearchEffect.TANK_REFILL, 0.4, 10, 300, 1.60, 2.06e4),
            ("res_fish_cap", ResearchEffect.FISH_CAP, 1, 10, 2500, 2.00, 1.28e6),
            ("res_offline_cap", ResearchEffect.OFFLINE_CAP, 2, 6, 500, 1.50, 3.80e3),
        };

        static Simulation Sim(int awakenRank = 1, double fishCoin = 0, ContentDb db = null)
        {
            var sim = new Simulation(TestContent.NewState(), db ?? Mvp);
            sim.Apply(new ApplyServerTime(TestContent.T0));
            sim.State.Hero.AwakenRank = awakenRank;
            sim.State.Wallet.FishCoin = fishCoin;
            sim.Query.MarkDirty();
            sim.DrainEvents();
            return sim;
        }

        static void AssertRejectIsPure(Simulation sim, string nodeId, RejectCode code, string reason)
        {
            string hash = sim.StateHash();
            var r = sim.Apply(new Research(nodeId));
            Assert.That((r.Code, r.Reason), Is.EqualTo((code, reason)), nodeId);
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
            Assert.That(sim.DrainEvents(), Is.Empty);
        }

        static void SetLevel(Simulation sim, string id, int level)
        {
            sim.State.Research.Level[id] = level;
            sim.Query.MarkDirty();
        }

        [Test]
        public void Content_MatchesTheSpecTable_AndTheLastLevelCostWithin1Percent()   // 03 section 6, 09 section 5.18
        {
            Assert.That(Mvp.Research.Keys.OrderBy(k => k, StringComparer.Ordinal),
                        Is.EqualTo(Table.Select(t => t.id).OrderBy(k => k, StringComparer.Ordinal)));
            foreach (var t in Table)
            {
                var n = Mvp.Research[t.id];
                Assert.That((n.Effect, n.EffectPerLevel, n.MaxLevel, n.Cost0, n.CostG, n.UnlockAwakenRank),
                            Is.EqualTo((t.effect, t.per, t.max, t.cost0, t.g, 1)), t.id);
                Assert.That(n.Cost(0), Is.EqualTo(t.cost0), t.id);
                Assert.That(n.Cost(t.max - 1), Is.EqualTo(t.last).Within(1).Percent, t.id + " last level");
                Assert.That(Mvp.ResearchFor(t.effect), Is.SameAs(n));
            }
            Assert.That(Mvp.Research["res_tank_refill"].EffectFloor, Is.EqualTo(Mvp.Tank.RefillFloorS));
        }

        [Test]
        public void AC13_FishCap_Charges2500_Then1280000_ThenMaxed()
        {
            var sim = Sim(fishCoin: 2500);
            Assert.That(sim.Query.NextResearchCost("res_fish_cap"), Is.EqualTo(2500));
            Assert.That(sim.Query.CanResearch("res_fish_cap"), Is.True);
            Assert.That(sim.Apply(new Research("res_fish_cap")).Ok, Is.True);
            Assert.That(sim.State.Wallet.FishCoin, Is.EqualTo(0));
            var ev = sim.DrainEvents();
            Assert.That(ev.Select(e => e.GetType()), Is.EqualTo(new[] { typeof(CurrencyChanged), typeof(ResearchLeveled) }));
            Assert.That(ev[0], Is.EqualTo(new CurrencyChanged(Currency.FISH_COIN, -2500, 0, CurrencyReason.RESEARCH)));
            Assert.That(ev[1], Is.EqualTo(new ResearchLeveled("res_fish_cap", 1, 2500)));
            Assert.That(sim.Query.ResearchLevelOf("res_fish_cap"), Is.EqualTo(1));

            SetLevel(sim, "res_fish_cap", 9);
            sim.State.Wallet.FishCoin = 1_279_999;
            AssertRejectIsPure(sim, "res_fish_cap", RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_FISH_COIN);
            sim.State.Wallet.FishCoin = 1_280_000 + 5;
            Assert.That(sim.Apply(new Research("res_fish_cap")).Ok, Is.True);
            Assert.That(sim.State.Wallet.FishCoin, Is.EqualTo(5), "level 9 -> 10 is 2,500 x 2^9 = 1,280,000 exactly");
            Assert.That(sim.DrainEvents().OfType<ResearchLeveled>().Single(), Is.EqualTo(new ResearchLeveled("res_fish_cap", 10, 1_280_000)));

            sim.State.Wallet.FishCoin = 1e12;
            Assert.That(sim.Query.NextResearchCost("res_fish_cap"), Is.EqualTo(0));
            Assert.That(sim.Query.CanResearch("res_fish_cap"), Is.False);
            AssertRejectIsPure(sim, "res_fish_cap", RejectCode.Capped, CombatRejectReasons.RESEARCH_MAXED);
            Assert.That(CombatRejectReasons.NOT_ENOUGH_FISH_COIN, Is.EqualTo(CollectionRejectReasons.NotEnough(Currency.FISH_COIN)));
        }

        [Test]
        public void AC13_BeforeAwakeningRank1_EveryCallIsLocked()   // 00 decision 11, feature_unlocks RESEARCH
        {
            var sim = Sim(awakenRank: 0, fishCoin: 1e12);
            Assert.That(sim.Query.ResearchUnlocked, Is.False);
            foreach (var t in Table)
            {
                AssertRejectIsPure(sim, t.id, RejectCode.Locked, CombatRejectReasons.RESEARCH_LOCKED);
                Assert.That(sim.Query.CanResearch(t.id), Is.False);
            }
            SetLevel(sim, "res_offline_cap", 6);   // a maxed node in an odd save is still Locked first
            AssertRejectIsPure(sim, "res_offline_cap", RejectCode.Locked, CombatRejectReasons.RESEARCH_LOCKED);

            sim.State.Hero.AwakenRank = 1;
            Assert.That(sim.Apply(new Research("res_atk")).Ok, Is.True);
        }

        [Test]
        public void UnknownNode_IsInvalidTarget_AndM0ContentHasNoNodes()
        {
            var sim = Sim(fishCoin: 1e12);
            AssertRejectIsPure(sim, "rs_mp_pool", RejectCode.InvalidTarget, CombatRejectReasons.UNKNOWN_RESEARCH);
            AssertRejectIsPure(sim, "RES_ATK", RejectCode.InvalidTarget, CombatRejectReasons.UNKNOWN_RESEARCH);
            AssertRejectIsPure(sim, null, RejectCode.InvalidTarget, CombatRejectReasons.UNKNOWN_RESEARCH);
            Assert.That(sim.Query.NextResearchCost("nope"), Is.EqualTo(0));

            var m0 = Sim(awakenRank: 5, fishCoin: 1e12, db: TestContent.Db);
            Assert.That(TestContent.Db.Research, Is.Empty);
            foreach (var t in Table) AssertRejectIsPure(m0, t.id, RejectCode.InvalidTarget, CombatRejectReasons.UNKNOWN_RESEARCH);
            var q = m0.Query;
            Assert.That((q.ResearchPct, q.MpPoolMax, q.ResearchTankCap, q.ResearchTankRefillTicks, q.ResearchFishMaxLvl, q.OfflineCapH),
                        Is.EqualTo((0.0, TestContent.Db.Constants.MpPool0, TestContent.Db.Tank.Cap0, TestContent.Db.Tank.RefillTicks0,
                                    TestContent.Db.Constants.FishMaxLvl0, TestContent.Db.Constants.OfflineCapH)));
        }

        [Test]
        public void EveryNode_EveryLevel_ChargesTheGeometricCost_ThenMaxes()
        {
            var sim = Sim();
            foreach (var t in Table)
            {
                double expected = t.cost0;
                for (int k = 0; k < t.max; k++)
                {
                    double cost = sim.Query.NextResearchCost(t.id);
                    Assert.That(cost, Is.EqualTo(expected).Within(1e-9).Percent, t.id + " level " + k);
                    sim.State.Wallet.FishCoin = cost - 0.5;
                    AssertRejectIsPure(sim, t.id, RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_FISH_COIN);
                    sim.State.Wallet.FishCoin = cost;
                    Assert.That(sim.Apply(new Research(t.id)).Ok, Is.True, t.id + " level " + k);
                    Assert.That(sim.State.Wallet.FishCoin, Is.EqualTo(0));
                    var lv = sim.DrainEvents().OfType<ResearchLeveled>().Single();
                    Assert.That((lv.NodeId, lv.NewLevel, lv.Cost), Is.EqualTo((t.id, k + 1, cost)));
                    expected *= t.g;
                }
                sim.State.Wallet.FishCoin = 1e15;
                AssertRejectIsPure(sim, t.id, RejectCode.Capped, CombatRejectReasons.RESEARCH_MAXED);
                Assert.That(sim.State.Research.Level[t.id], Is.EqualTo(t.max));
            }
        }

        [Test]
        public void TankRefill_NeverBelowTheFloor_TankCapAndFishCapScale()   // 05 section 3, #167 acceptance 1 (values only)
        {
            var sim = Sim();
            for (int k = 0; k <= 10; k++)
            {
                SetLevel(sim, "res_tank_refill", k);
                SetLevel(sim, "res_tank_cap", k);
                SetLevel(sim, "res_fish_cap", k);
                Assert.That(sim.Query.ResearchTankRefillTicks, Is.EqualTo(60 - 4 * k), "level " + k);
                Assert.That(sim.Query.ResearchTankCap, Is.EqualTo(10 + 5 * k));
                Assert.That(sim.Query.ResearchFishMaxLvl, Is.EqualTo(10 + k));
            }
            Assert.That(ResearchRules.TankRefillTicks(Mvp, sim.State), Is.EqualTo(20), "2.0 s floor at TICK_HZ 10");
            SetLevel(sim, "res_tank_refill", 99);   // above max_level (an unrepaired save) reads as max_level
            Assert.That(sim.Query.ResearchTankRefillTicks, Is.EqualTo(20));
            Assert.That(sim.Query.ResearchLevelOf("res_tank_refill"), Is.EqualTo(10));
        }

        [Test]
        public void OfflineCap_Level6_Gives36Hours_AndCatchupUsesIt()   // 00 decision 12, 06 section 7
        {
            var sim = Sim();
            Assert.That(sim.Query.OfflineCapH, Is.EqualTo(24));
            SetLevel(sim, "res_offline_cap", 6);
            Assert.That(sim.Query.OfflineCapH, Is.EqualTo(36));
            Assert.That(Catchup.CapSeconds(Mvp, sim.State), Is.EqualTo(36 * 3600));
        }

        [Test]
        public void MpAndGoldAndAtk_Effects()   // 03 sections 2.4, 6 and 7.2
        {
            var sim = Sim();
            var k = Mvp.Constants;
            Assert.That(sim.Query.MpPoolMax, Is.EqualTo(k.MpPool0));
            Assert.That(sim.Query.MpRegenPerTick, Is.EqualTo(k.MpRegen0 / k.TickHz));
            double gold0 = sim.Query.GoldMult;
            SetLevel(sim, "res_mp_pool", 3);
            SetLevel(sim, "res_mp_regen", 2);
            SetLevel(sim, "res_gold_gain", 4);
            Assert.That(sim.Query.MpPoolMax, Is.EqualTo(k.MpPool0 + 60));
            Assert.That(sim.Query.MpRegenPerTick, Is.EqualTo((k.MpRegen0 + 1.0) / k.TickHz).Within(1e-12).Percent);
            Assert.That(sim.Query.GoldMult, Is.EqualTo(gold0 * 1.2).Within(1e-12).Percent, "one factor 1 + 0.05 k, not per level");
            sim.Advance(1);   // the snapshot is rebuilt after a tick or a command
            Assert.That(sim.GetSnapshot().MpMax, Is.EqualTo(k.MpPool0 + 60));

            sim.State.Training.Level[(int)Stat.GOLD_GAIN] = 10;
            sim.Query.MarkDirty();
            double train = 1.0 + Mvp.Stat(Stat.GOLD_GAIN).PerLvl.Value * 10;
            Assert.That(sim.Query.GoldMult, Is.EqualTo(train * 1.2).Within(1e-12).Percent);
        }

        [Test]
        public void ResAtk_RaisesRatedDps_ThroughTheDirtyCache()   // 03 section 7.3: ResearchLeveled marks DPS dirty
        {
            var sim = Sim(fishCoin: 150);
            double dps = sim.Query.RatedDps;
            Assert.That(sim.Apply(new Research("res_atk")).Ok, Is.True);
            Assert.That(sim.Query.ResearchPct, Is.EqualTo(0.08).Within(1e-12).Percent);
            Assert.That(sim.Query.RatedDps, Is.EqualTo(dps * 1.08).Within(1e-9).Percent);
        }

        [Test]
        public void Queries_DoNotMutate()
        {
            var sim = Sim(fishCoin: 1e6);
            string hash = sim.StateHash();
            foreach (var t in Table)
            {
                _ = sim.Query.NextResearchCost(t.id);
                _ = sim.Query.CanResearch(t.id);
                _ = sim.Query.ResearchLevelOf(t.id);
                _ = sim.Query.ResearchEffectTotal(t.effect);
            }
            _ = sim.Query.MpPoolMax; _ = sim.Query.MpRegenPerTick; _ = sim.Query.ResearchTankCap;
            _ = sim.Query.ResearchTankRefillTicks; _ = sim.Query.ResearchFishMaxLvl; _ = sim.Query.OfflineCapH;
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
        }

        [Test]
        public void Replay_ThroughTheJsonlCodec_ReproducesTheStateHash()   // 01 section 2.2 rule 4, 2.6
        {
            var genesis = TestContent.NewState();
            genesis.Hero.AwakenRank = 1;
            genesis.Wallet.FishCoin = 20_000;
            var sim = new Simulation(StateCopy.DeepClone(genesis), Mvp);
            var log = new ListCommandLog();
            sim.CommandLog = log;
            sim.Apply(new ApplyServerTime(TestContent.T0));
            var rng = new Random(158);
            var ids = Table.Select(t => t.id).Concat(new[] { "res_unknown" }).ToArray();
            int ok = 0, rejected = 0;
            for (int i = 0; i < 80; i++)
            {
                sim.Advance(rng.Next(0, 90));
                if (sim.Apply(new Research(ids[rng.Next(ids.Length)])).Ok) ok++; else rejected++;
                if (rng.Next(3) == 0) sim.Apply(new TrainStatMax(Stat.ATK));
            }
            sim.Advance(17);
            Assert.That(ok, Is.GreaterThan(0));
            Assert.That(rejected, Is.GreaterThan(0));

            var lines = log.Records.Select(CommandCodec.Encode).ToArray();
            Assert.That(lines.Count(l => l.Contains("\"type\":\"Research\"")), Is.EqualTo(80));
            Assert.That(lines.First(l => l.Contains("\"type\":\"Research\"")), Does.Contain("\"nodeId\":\"res_"));
            var decoded = lines.Select(CommandCodec.Decode).ToArray();
            var replay = new Simulation(StateCopy.DeepClone(genesis), Mvp);
            replay.Replay(decoded, sim.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(sim.StateHash()));
            Assert.That(replay.State.Research.Level, Is.EqualTo(sim.State.Research.Level));
            Assert.That(replay.State.Wallet.FishCoin, Is.EqualTo(sim.State.Wallet.FishCoin));
        }
    

        // ------------------------------------------------------------------------------------- Fish Board effects (#167)

        static void SetBoard(Simulation sim, params int[] levels)
        {
            var f = sim.State.Fish;
            Array.Clear(f.Cells, 0, f.Cells.Length);
            for (int i = 0; i < levels.Length; i++) f.Cells[i] = levels[i];
            foreach (int n in levels)
                if (n > 0 && f.DiscoveredLevels.Add(n) && n > f.HighestLevel) f.HighestLevel = n;
        }

        [Test]
        public void Board_TankCapAndRefill_FollowResearch()   // #167 acceptance 1, on the live board
        {
            var sim = Sim();
            for (int k = 0; k <= 10; k++)
            {
                SetLevel(sim, "res_tank_refill", k);
                SetLevel(sim, "res_tank_cap", k);
                Assert.That(sim.FishTankRefillTicks, Is.EqualTo(60 - 4 * k), "level " + k);
                Assert.That(sim.FishTankRefillTicks, Is.GreaterThanOrEqualTo(20));
                Assert.That(sim.FishTankCap, Is.EqualTo(10 + 5 * k));
                Assert.That(Fish.FishBoard.TankRefillSeconds(sim.State, Mvp), Is.EqualTo((60 - 4 * k) / 10.0).Within(1e-12));
            }

            // At RES_TANK_REFILL 10 an empty tank gains one fish every 20 ticks, up to 60.
            sim.State.Fish.TankCount = 0; sim.State.Fish.TankRefillTick = 0;
            sim.Advance(19);
            Assert.That(sim.State.Fish.TankCount, Is.EqualTo(0));
            sim.Advance(1);
            Assert.That((sim.State.Fish.TankCount, sim.State.Fish.TankRefillTick), Is.EqualTo((1, 0)));
            Assert.That(sim.GetSnapshot().TankCap, Is.EqualTo(60));
            Assert.That(Fish.FishBoard.CatchUpTank(sim.State, Mvp, 20 * 100), Is.EqualTo((59, 0)), "catch-up uses the same cap and period");
        }

        [Test]
        public void TankCap_RaisedWhileFull_ResumesRefillOnTheNextTick()   // #167 acceptance 3
        {
            var sim = Sim(fishCoin: 200);
            var f = sim.State.Fish;
            Assert.That(f.TankCount, Is.EqualTo(10));
            sim.Advance(100);
            Assert.That((f.TankCount, f.TankRefillTick), Is.EqualTo((10, 0)), "full at cap 10");
            Assert.That(sim.Apply(new Research("res_tank_cap")).Ok, Is.True);
            Assert.That(sim.FishTankCap, Is.EqualTo(15));
            Assert.That(Fish.FishBoard.FirstViolation(sim.State, Mvp), Is.Null);
            sim.Advance(1);
            Assert.That(f.TankRefillTick, Is.EqualTo(1), "refill resumes on the first tick after the purchase");
            sim.Advance(59);
            Assert.That((f.TankCount, f.TankRefillTick), Is.EqualTo((11, 0)));
        }

        [Test]
        public void TankRefill_ShorterPeriod_ClampsProgress_FishOnTheNextTick()   // decision in #167: same repair as 02 P9b
        {
            var sim = Sim(fishCoin: 300);
            var f = sim.State.Fish;
            f.TankCount = 3; f.TankRefillTick = 58;
            Assert.That(sim.Apply(new Research("res_tank_refill")).Ok, Is.True);
            Assert.That(sim.FishTankRefillTicks, Is.EqualTo(56));
            Assert.That(f.TankRefillTick, Is.EqualTo(55));
            Assert.That(Fish.FishBoard.FirstViolation(sim.State, Mvp), Is.Null);
            sim.Advance(1);
            Assert.That((f.TankCount, f.TankRefillTick), Is.EqualTo((4, 0)));
        }

        [Test]
        public void FishCap1_TwoCapFishMergeToLevel11_DiscoveryOnce_NoRetroactiveDiscovery()   // #167 acceptance 2, 05 section 11
        {
            var sim = Sim(fishCoin: 2500);
            var f = sim.State.Fish;
            for (int n = 1; n <= 10; n++) f.DiscoveredLevels.Add(n);
            SetBoard(sim, 10, 10, 10, 10);
            Assert.That(sim.FishMaxLvl, Is.EqualTo(10));
            Assert.That(sim.Apply(new MergeFish(0, 1)).Code, Is.EqualTo(RejectCode.Capped));
            Assert.That(sim.FishMergeablePairs, Is.EqualTo(0));
            sim.DrainEvents();

            Assert.That(sim.Apply(new Research("res_fish_cap")).Ok, Is.True);
            Assert.That(sim.State.Wallet.FishCoin, Is.EqualTo(0));
            Assert.That(sim.FishMaxLvl, Is.EqualTo(11));
            Assert.That(sim.DrainEvents().OfType<FishDiscovered>(), Is.Empty, "buying the level discovers nothing");
            Assert.That(f.DiscoveredLevels.Contains(11), Is.False);
            Assert.That(sim.FishMergeablePairs, Is.EqualTo(2));

            Assert.That(sim.Apply(new MergeFish(0, 1)).Ok, Is.True);
            var ev = sim.DrainEvents();
            var merged = ev.OfType<FishMerged>().Single();
            Assert.That((merged.NewLevel, merged.Discovered, merged.Coins), Is.EqualTo((11, true, 10_240.0)));
            Assert.That(ev.OfType<FishDiscovered>().Single().Level, Is.EqualTo(11));
            Assert.That(sim.State.Wallet.FishCoin, Is.EqualTo(10_240));

            // MergeAll picks up the other pair of old cap fish; level 11 is now known, so it pays merge coins only.
            Assert.That(sim.Apply(new MergeAll()).Ok, Is.True);
            ev = sim.DrainEvents();
            Assert.That(ev.OfType<FishDiscovered>(), Is.Empty);
            var second = ev.OfType<FishMerged>().Single();
            Assert.That((second.NewLevel, second.Discovered), Is.EqualTo((11, false)));
            Assert.That(sim.State.Wallet.FishCoin, Is.EqualTo(10_240 + Mvp.FishLevels[10].MergeCoins).Within(1e-9));
            Assert.That(f.Cells.Count(c => c == 11), Is.EqualTo(2));
            Assert.That(sim.Apply(new MergeFish(1, 3)).Code, Is.EqualTo(RejectCode.Capped), "11 is the new cap");
        }

        [Test]
        public void FishCapAndTankNodes_LeaveDpsUnchanged()   // #167 acceptance 4
        {
            var sim = Sim(fishCoin: 1e6);
            SetBoard(sim, 10, 10, 9);
            sim.Query.MarkDirty();
            double dps = sim.Query.RatedDps;
            foreach (var id in new[] { "res_fish_cap", "res_tank_cap", "res_tank_refill" })
            {
                Assert.That(sim.Apply(new Research(id)).Ok, Is.True, id);
                Assert.That(sim.Query.RatedDps, Is.EqualTo(dps), id + " leaves DPS unchanged");
            }
            Assert.That(sim.Apply(new Research("res_atk")).Ok, Is.True);
            Assert.That(sim.Query.RatedDps, Is.GreaterThan(dps), "a DPS node does change it");
        }
    }
}
