using System;
using System.Linq;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>04 section 5.2 and acceptance criteria 5-6: the LimitBreak command, its cost, its effect and its queries (#162).</summary>
    public class LimitBreakTests
    {
        static ContentDb Db => TestContent.Db;

        static Simulation Sim(long rockCandy = 0)
        {
            var sim = TestContent.NewSim();
            sim.State.Wallet.RockCandy = rockCandy;
            sim.Query.MarkDirty();
            sim.DrainEvents();
            return sim;
        }

        static void Own(CollectionState c, string id, int copies = 1, int limitBreaks = 0) =>
            c.Items[id] = new OwnedItem { Copies = copies, LimitBreaks = limitBreaks };

        static long CostPerRank(Rarity r) => Db.Rarities[r].LimitBreakCost;

        static void AssertRejectIsPure(Simulation sim, Command c, RejectCode code, string reason)
        {
            string hash = sim.StateHash();
            var r = sim.Apply(c);
            Assert.That((r.Code, r.Reason), Is.EqualTo((code, reason)), c.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(hash), c.ToString());
            Assert.That(sim.DrainEvents(), Is.Empty, c.ToString());
        }

        [Test]
        public void Content_HasTheSpecRanksAndPerRankCosts()   // 00 section 6.5, 04 section 5.2 table (placeholders, #36)
        {
            Assert.That(Db.Constants.LimitBreakRanks, Is.EqualTo(5));
            Assert.That(Enum.GetValues(typeof(Rarity)).Cast<Rarity>().Select(CostPerRank), Is.EqualTo(new long[] { 10, 20, 40, 80, 150 }));
        }

        [Test]
        public void Rejects_Unknown_Unowned_Capped_AndUnaffordable_ChargingNothing()   // criterion 6
        {
            var sim = Sim(rockCandy: 1_000_000);
            string w = Db.Pool(Banner.WEAPON, Rarity.EPIC)[0];
            AssertRejectIsPure(sim, new LimitBreak(null), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new LimitBreak("no_such_item"), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new LimitBreak(w), RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);
            sim.State.Weapons.Items[w] = new OwnedItem { Copies = 0 };
            AssertRejectIsPure(sim, new LimitBreak(w), RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);

            Own(sim.State.Weapons, w, limitBreaks: Db.Constants.LimitBreakRanks);
            AssertRejectIsPure(sim, new LimitBreak(w), RejectCode.Capped, CollectionRejectReasons.LIMIT_BREAK_CAPPED);

            Own(sim.State.Weapons, w, limitBreaks: 2);
            long cost = CostPerRank(Rarity.EPIC) * 3;
            sim.State.Wallet.RockCandy = cost - 1;
            AssertRejectIsPure(sim, new LimitBreak(w), RejectCode.CannotAfford, CollectionRejectReasons.NotEnough(Currency.ROCK_CANDY));
            Assert.That(sim.State.Wallet.RockCandy, Is.EqualTo(cost - 1));
            Assert.That(CollectionRejectReasons.NotEnough(Currency.ROCK_CANDY), Is.EqualTo("NOT_ENOUGH_ROCK_CANDY"));
        }

        [Test]
        public void ChargesCostTimesRank_WithReasonLimitBreak_AndPublishesItemLimitBroken()   // criterion 6
        {
            foreach (Banner kind in CollectionRules.LimitBreakKinds)
                foreach (Rarity r in Enum.GetValues(typeof(Rarity)))
                {
                    if (Db.Pool(kind, r).Count == 0) continue;   // companions are MVP: none in the M0 tier (CompanionTests covers them)
                    var sim = Sim(rockCandy: 100_000);
                    string id = Db.Pool(kind, r)[0];
                    Own(CollectionRules.For(sim.State, kind), id);
                    long spent = 0;
                    for (int rank = 0; rank < Db.Constants.LimitBreakRanks; rank++)
                    {
                        long cost = CostPerRank(r) * (rank + 1);
                        long before = sim.State.Wallet.RockCandy;
                        Assert.That(sim.Query.NextLimitBreakCost(id), Is.EqualTo(cost));
                        Assert.That(sim.Apply(new LimitBreak(id)).Ok, Is.True, $"{id} rank {rank + 1}");
                        spent += cost;
                        Assert.That(sim.State.Wallet.RockCandy, Is.EqualTo(before - cost));
                        Assert.That(CollectionRules.For(sim.State, kind).Items[id].LimitBreaks, Is.EqualTo(rank + 1));
                        var ev = sim.DrainEvents();
                        Assert.That(ev.OfType<CurrencyChanged>().Single(),
                                    Is.EqualTo(new CurrencyChanged(Currency.ROCK_CANDY, -cost, before - cost, CurrencyReason.LIMIT_BREAK)));
                        Assert.That(ev.OfType<ItemLimitBroken>().Single(), Is.EqualTo(new ItemLimitBroken(id, rank + 1, cost)));
                    }
                    Assert.That(spent, Is.EqualTo(15 * CostPerRank(r)), "ranks 1-5 total = 15 x the per-rank cost (04 5.2 table)");
                    Assert.That(sim.Query.NextLimitBreakCost(id), Is.EqualTo(0), "maxed");
                    Assert.That(sim.Apply(new LimitBreak(id)).Code, Is.EqualTo(RejectCode.Capped));
                }
        }

        [Test]
        public void EachRank_RaisesTheContributionBy20PercentOfTheLevel1Value_WithoutNeedingLevel10()   // criterion 6
        {
            var sim = Sim(rockCandy: 100_000);
            string w = Db.Pool(Banner.WEAPON, Rarity.MYTHIC)[0];
            string k = Db.Pool(Banner.SKILL, Rarity.LEGENDARY)[0];
            Own(sim.State.Weapons, w);   // level 1
            Own(sim.State.Skills, k, copies: Db.DupeLevels[2]);   // level 3
            Assert.That(sim.Apply(new EquipWeapon(w)).Ok, Is.True);
            Assert.That(sim.Apply(new EquipSkill(0, k)).Ok, Is.True);
            double bonus = Db.Constants.DupeLvlBonus;
            Assert.That(bonus, Is.EqualTo(0.20));

            for (int rank = 1; rank <= Db.Constants.LimitBreakRanks; rank++)
            {
                double wBefore = PowerFormula.EquippedWeaponPct(Db, sim.State), pBefore = PowerFormula.WeaponPassivePct(Db, sim.State);
                double dpsBefore = sim.Query.RatedDps;
                double predicted = sim.Query.RatedDpsAfterLimitBreak(w);
                Assert.That(sim.Apply(new LimitBreak(w)).Ok, Is.True);
                Assert.That(PowerFormula.EquippedWeaponPct(Db, sim.State) - wBefore, Is.EqualTo(bonus * Db.Weapons[w].AtkPct).Within(1e-9));
                Assert.That(PowerFormula.WeaponPassivePct(Db, sim.State) - pBefore, Is.EqualTo(bonus * Db.Weapons[w].PassivePct).Within(1e-9));
                Assert.That(sim.Query.RatedDps, Is.GreaterThan(dpsBefore), "MarkDpsDirty");
                Assert.That(sim.Query.RatedDps, Is.EqualTo(predicted), "RatedDpsAfterLimitBreak predicts the command exactly");

                double sBefore = PowerFormula.SkillDpsPct(Db, sim.State);
                Assert.That(sim.Apply(new LimitBreak(k)).Ok, Is.True);
                Assert.That(PowerFormula.SkillDpsPct(Db, sim.State) - sBefore, Is.EqualTo(bonus * Db.Skills[k].DpsPct).Within(1e-9));
                Assert.That(CollectionRules.EffectiveLevel(Db, sim.State.Skills.Items[k]), Is.EqualTo(3 + rank));
            }
        }

        [Test]
        public void LevelMult_AtLevel10WithFiveBreaks_Is3Point8()   // criterion 5
        {
            int max = Db.DupeLevels[Db.DupeLevels.Count - 1];
            var item = new OwnedItem { Copies = max, LimitBreaks = Db.Constants.LimitBreakRanks };
            Assert.That(CollectionRules.EffectiveLevel(Db, item), Is.EqualTo(15));
            Assert.That(CollectionRules.LevelMult(Db, item), Is.EqualTo(3.8).Within(1e-12));
            Assert.That(CollectionRules.LevelMult(Db, new OwnedItem { Copies = max }), Is.EqualTo(2.8).Within(1e-12));
        }

        [Test]
        public void CollectibleVm_ShowsTheNextLimitBreakCost()   // 04 section 11
        {
            var sim = Sim();
            string a = Db.Pool(Banner.WEAPON, Rarity.RARE)[0], b = Db.Pool(Banner.WEAPON, Rarity.RARE)[1];
            Own(sim.State.Weapons, a, limitBreaks: 2);
            Own(sim.State.Weapons, b, limitBreaks: Db.Constants.LimitBreakRanks);
            var items = SummonQuery.Collection(sim, Banner.WEAPON).Items.ToDictionary(x => x.Id);
            Assert.That(items[a].NextLimitBreakCost, Is.EqualTo(CostPerRank(Rarity.RARE) * 3));
            Assert.That(items[a].EffectiveLevel, Is.EqualTo(3));
            Assert.That(items[b].NextLimitBreakCost, Is.EqualTo(0), "maxed");
            Assert.That(items[Db.Pool(Banner.WEAPON, Rarity.MYTHIC)[0]].NextLimitBreakCost, Is.EqualTo(0), "unowned");
        }

        [Test]
        public void Queries_DoNotMutate()
        {
            var sim = Sim(rockCandy: 50);
            string w = sim.State.Loadout.Weapon;
            string hash = sim.StateHash();
            double dps = sim.Query.RatedDps;
            Assert.That(sim.Query.RatedDpsAfterLimitBreak(w), Is.GreaterThan(dps));
            Assert.That(sim.Query.RatedDpsAfterLimitBreak("no_such_item"), Is.EqualTo(dps));
            Assert.That(sim.Query.NextLimitBreakCost("no_such_item"), Is.EqualTo(0));
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
            Assert.That(sim.State.Weapons.Items[w].LimitBreaks, Is.EqualTo(0));
        }

        [Test]
        public void Replay_WithLimitBreaks_ReproducesTheStateHash()   // 01 section 2.2 rule 4
        {
            var genesis = TestContent.NewState();
            genesis.Wallet.RockCandy = 5000;
            var ids = CollectionRules.LimitBreakKinds.SelectMany(k => SummonQuery.PoolIds(Db, k)).ToArray();
            for (int i = 0; i < ids.Length; i += 3)
                Own(CollectionRules.For(genesis, CollectionRules.LimitBreakKinds.First(k => CollectionRules.IsKnown(Db, k, ids[i]))), ids[i]);
            var sim = new Simulation(StateCopy.DeepClone(genesis), Db);
            var log = new ListCommandLog();
            sim.CommandLog = log;
            sim.Apply(new ApplyServerTime(TestContent.T0));
            var rng = new Random(11);
            int ok = 0;
            for (int i = 0; i < 400; i++)
            {
                sim.Advance(rng.Next(0, 60));
                if (sim.Apply(new LimitBreak(ids[rng.Next(ids.Length)])).Ok) ok++;
            }
            sim.Advance(17);
            Assert.That(ok, Is.GreaterThan(5));
            var replay = new Simulation(StateCopy.DeepClone(genesis), Db);
            replay.Replay(log.Records, sim.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(sim.StateHash()));
        }
    }
}
