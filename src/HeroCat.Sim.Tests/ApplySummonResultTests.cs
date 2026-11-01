using System;
using System.Linq;
using HeroCat.Runtime.Summons;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>04 section 10 and criteria 7, 8 (auto-equip part), 12, 13 (counter part).</summary>
    public class ApplySummonResultTests
    {
        static ContentDb Db => TestContent.Db;

        static LocalSummonService Service(Simulation sim) => new LocalSummonService(() => sim.State, sim.Db, () => TestContent.T0);

        static SummonResultPayload Roll(Simulation sim, Banner b, int count, SummonPayment pay = SummonPayment.Diamonds, string id = null)
        {
            var r = Service(sim).Pull(new SummonRequest(id ?? Guid.NewGuid().ToString("N"), b, count, pay));
            Assert.That(r.IsOk, Is.True, r.ToString());
            return r.Value;
        }

        static void Give(Simulation sim, Currency c, long amount) =>
            Assert.That(Ledger.Apply(sim.State, new CurrencyDelta(c, amount), CurrencyReason.ADMIN, null), Is.True);

        static Simulation Sim()
        {
            var sim = TestContent.NewSim();
            sim.DrainEvents();
            return sim;
        }

        [Test]
        public void Diamonds10Pull_DebitsOnce_WritesItems_PublishesOneItemAcquiredPerPull()   // criterion 12
        {
            var sim = Sim();
            long before = sim.State.Wallet.Diamond;
            var p = Roll(sim, Banner.WEAPON, 10);
            var r = sim.Apply(new ApplySummonResult(p));
            Assert.That(r.Ok, Is.True, r.ToString());
            var def = Db.Banners[Banner.WEAPON];
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(before - 10 * def.PullCostDiamonds));
            var bs = sim.State.Banners[Banner.WEAPON];
            Assert.That(bs.Pity, Is.EqualTo(p.PityAfter));
            Assert.That(bs.Exp, Is.EqualTo(p.BannerExpAfter).And.EqualTo(10));
            Assert.That(bs.TotalPulls, Is.EqualTo(10));
            Assert.That(sim.State.Stats.Pulls, Is.EqualTo(10));
            Assert.That(sim.State.Rng[RngStreamsNames.Weapon].Counter, Is.EqualTo(30));
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<ItemAcquired>().Count(), Is.EqualTo(10));
            var cc = ev.OfType<CurrencyChanged>().Where(e => e.Currency == Currency.DIAMOND).ToList();
            Assert.That(cc.Count, Is.EqualTo(1));
            Assert.That(cc[0].Reason, Is.EqualTo(CurrencyReason.SUMMON));
            foreach (var pull in p.Pulls) Assert.That(sim.State.Weapons.Items[pull.ItemId].Copies, Is.GreaterThanOrEqualTo(pull.CopiesAfter > 0 ? 1 : 0));
            foreach (var g in p.Pulls.GroupBy(x => x.ItemId))
                Assert.That(sim.State.Weapons.Items[g.Key].Copies, Is.EqualTo(g.Last().CopiesAfter));
        }

        [Test]
        public void RepeatedRequestId_ChangesNothing()   // criterion 12
        {
            var sim = Sim();
            var p = Roll(sim, Banner.SKILL, 1);
            Assert.That(sim.Apply(new ApplySummonResult(p)).Ok, Is.True);
            sim.DrainEvents();
            string hash = sim.StateHash();
            var again = sim.Apply(new ApplySummonResult(p));
            Assert.That(again.Ok, Is.False);
            Assert.That(again.Code, Is.EqualTo(RejectCode.StaleRequest));
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
            Assert.That(sim.DrainEvents(), Is.Empty);
        }

        [Test]
        public void StalePayload_RolledFromAnOlderCounter_IsRejected()   // the Local-mode guard that survives a reload
        {
            var sim = Sim();
            var p1 = Roll(sim, Banner.WEAPON, 1);
            Assert.That(sim.Apply(new ApplySummonResult(p1)).Ok, Is.True);
            var reused = p1 with { RequestId = Guid.NewGuid().ToString("N") };
            string hash = sim.StateHash();
            var r = sim.Apply(new ApplySummonResult(reused));
            Assert.That(r.Code, Is.EqualTo(RejectCode.StaleRequest));
            Assert.That(r.Reason, Is.EqualTo(CollectionRejectReasons.SUMMON_STREAM_MISMATCH));
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
        }

        [Test]
        public void Tickets_PayOnePerPull()
        {
            var sim = Sim();
            Give(sim, Currency.TICKET_SUMMON_WEAPON, 10);
            long diamonds = sim.State.Wallet.Diamond;
            var p = Roll(sim, Banner.WEAPON, 10, SummonPayment.Tickets);
            Assert.That(p.Debits.Single().Currency, Is.EqualTo(Currency.TICKET_SUMMON_WEAPON));
            Assert.That(sim.Apply(new ApplySummonResult(p)).Ok, Is.True);
            Assert.That(sim.State.Wallet.SummonTicket[(int)Banner.WEAPON], Is.EqualTo(0));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(diamonds));
        }

        [Test]
        public void Unaffordable_RejectsWithNotEnoughCurrency_AndChangesNothing()
        {
            var sim = Sim();
            var p = Roll(sim, Banner.WEAPON, 1);
            // spend the diamonds the payload assumed
            Assert.That(Ledger.Apply(sim.State, new CurrencyDelta(Currency.DIAMOND, -sim.State.Wallet.Diamond), CurrencyReason.ADMIN, null), Is.True);
            string hash = sim.StateHash();
            var r = sim.Apply(new ApplySummonResult(p));
            Assert.That(r.Code, Is.EqualTo(RejectCode.CannotAfford));
            Assert.That(r.Reason, Is.EqualTo("NOT_ENOUGH_DIAMOND"));
            Assert.That(sim.StateHash(), Is.EqualTo(hash));

            var t = p with { RequestId = Guid.NewGuid().ToString("N"), Debits = new[] { new CurrencyDelta(Currency.TICKET_SUMMON_WEAPON, -1) } };
            var rt = sim.Apply(new ApplySummonResult(t));
            Assert.That(rt.Reason, Is.EqualTo("NOT_ENOUGH_TICKET_SUMMON_WEAPON"), "00 decision 95");
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
        }

        [Test]
        public void MixedPayment_WrongCount_WrongAmount_AreRejected_WithNoChange()
        {
            var sim = Sim();
            Give(sim, Currency.TICKET_SUMMON_WEAPON, 10);
            var p = Roll(sim, Banner.WEAPON, 10);
            string hash = sim.StateHash();
            var cost = 10L * Db.Banners[Banner.WEAPON].PullCostDiamonds;
            var bad = new[]
            {
                p with { Debits = new[] { new CurrencyDelta(Currency.DIAMOND, -cost / 2), new CurrencyDelta(Currency.TICKET_SUMMON_WEAPON, -5) } },
                p with { Debits = new[] { new CurrencyDelta(Currency.DIAMOND, -cost + 100) } },
                p with { Debits = new[] { new CurrencyDelta(Currency.TICKET_SUMMON_SKILL, -10) } },
                p with { Count = 2 },
                p with { Count = 5, Pulls = p.Pulls.Take(5).ToArray() },
                p with { Debits = Array.Empty<CurrencyDelta>() },
                p with { Pulls = p.Pulls.Select((x, i) => i == 0 ? x with { ItemId = "skill_c_01" } : x).ToArray() },
                p with { Pulls = p.Pulls.Select((x, i) => i == 0 ? x with { CopiesAfter = 161 } : x).ToArray() },
                p with { PityAfter = 80 },
                p with { Banner = Banner.RUNE },
            };
            foreach (var b in bad)
            {
                var r = sim.Apply(new ApplySummonResult(b));
                Assert.That(r.Ok, Is.False, b.ToString());
                Assert.That(sim.StateHash(), Is.EqualTo(hash));
            }
            Assert.That(sim.DrainEvents(), Is.Empty);
            Assert.That(sim.Apply(new ApplySummonResult(p)).Ok, Is.True, "the untouched payload still applies");
        }

        [Test]
        public void Copy161_AddsOverflowRockCandy_WithReasonSummon_AndLeavesCopiesAt160()   // criterion 7
        {
            var sim = Sim();
            string id = Db.Pool(Banner.WEAPON, Rarity.COMMON)[0];
            sim.State.Weapons.Items[id].Copies = 160;   // test setup: the tutorial weapon at the cap
            long candy = sim.State.Wallet.RockCandy;
            long overflow = Db.Rarities[Rarity.COMMON].DupeOverflowRockCandy;
            var counter = sim.State.Rng[RngStreamsNames.Weapon].Counter;
            var p = new SummonResultPayload(Guid.NewGuid().ToString("N"), Banner.WEAPON, 1,
                new[] { new SummonPull(id, Rarity.COMMON, false, 10, 10, 160, overflow, false) }, 1, 1, 1,
                new[] { new CurrencyDelta(Currency.DIAMOND, -Db.Banners[Banner.WEAPON].PullCostDiamonds) }, null, counter + 3, TestContent.T0);
            Assert.That(sim.Apply(new ApplySummonResult(p)).Ok, Is.True);
            Assert.That(sim.State.Wallet.RockCandy, Is.EqualTo(candy + overflow));
            Assert.That(sim.State.Weapons.Items[id].Copies, Is.EqualTo(160));
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<CurrencyChanged>().Single(e => e.Currency == Currency.ROCK_CANDY).Reason, Is.EqualTo(CurrencyReason.SUMMON));
            Assert.That(ev.OfType<ItemAcquired>().Single().OverflowRockCandy, Is.EqualTo(overflow));
        }

        [Test]
        public void Overflow_FromTheRealRoll_EndToEnd()
        {
            var sim = Sim();
            foreach (var id in Db.Pool(Banner.WEAPON, Rarity.COMMON))
                sim.State.Weapons.Items[id] = new OwnedItem { Copies = 160 };
            Give(sim, Currency.DIAMOND, 100_000);
            long candy = sim.State.Wallet.RockCandy;
            long expected = 0;
            for (int i = 0; i < 5; i++)
            {
                var p = Roll(sim, Banner.WEAPON, 10);
                expected += p.Pulls.Sum(x => x.OverflowRockCandy);
                Assert.That(sim.Apply(new ApplySummonResult(p)).Ok, Is.True);
            }
            Assert.That(expected, Is.GreaterThan(0));
            Assert.That(sim.State.Wallet.RockCandy, Is.EqualTo(candy + expected));
            Assert.That(Db.Pool(Banner.WEAPON, Rarity.COMMON).All(id => sim.State.Weapons.Items[id].Copies == 160));
        }

        [Test]
        public void BannerLeveledUp_WhenTheLevelRises()   // criterion 12
        {
            var sim = Sim();
            Give(sim, Currency.DIAMOND, 100_000);
            var def = Db.Banners[Banner.WEAPON];
            int pullsToLevel2 = def.Levels[1].ExpRequired / def.ExpPerPull;
            int levelUps = 0;
            for (int i = 0; i < pullsToLevel2 / 10; i++)
            {
                sim.Apply(new ApplySummonResult(Roll(sim, Banner.WEAPON, 10)));
                var ups = sim.DrainEvents().OfType<BannerLeveledUp>().ToList();
                if (i < pullsToLevel2 / 10 - 1) Assert.That(ups, Is.Empty);
                levelUps += ups.Count;
                if (ups.Count > 0) Assert.That((ups[0].Banner, ups[0].Level), Is.EqualTo((Banner.WEAPON, 2)));
            }
            Assert.That(levelUps, Is.EqualTo(1));
            Assert.That(SummonQuery.BannerLevel(sim, Banner.WEAPON), Is.EqualTo(2));
        }

        [Test]
        public void ThousandPulls_CounterIsThreePerPull_AndTheLogReplays()   // criterion 13 (counter), 00 contract 2, 01 2.6
        {
            var genesis = TestContent.NewState();
            var replica = StateCopy.DeepClone(genesis);
            var sim = new Simulation(genesis, Db);
            var log = new ListCommandLog();
            sim.CommandLog = log;
            sim.Apply(new ApplyServerTime(TestContent.T0));
            Give(sim, Currency.DIAMOND, 1_000_000);   // outside Apply: mirrored on the replica below
            Assert.That(Ledger.Apply(replica, new CurrencyDelta(Currency.DIAMOND, 1_000_000), CurrencyReason.ADMIN, null), Is.True);
            long start = sim.State.Rng[RngStreamsNames.Weapon].Counter;
            for (int i = 0; i < 100; i++)
            {
                sim.Advance(7);
                Assert.That(sim.Apply(new ApplySummonResult(Roll(sim, Banner.WEAPON, 10))).Ok, Is.True);
                Assert.That(sim.Apply(new ApplySummonResult(Roll(sim, Banner.SKILL, 1))).Ok, Is.True);
            }
            Assert.That(sim.State.Rng[RngStreamsNames.Weapon].Counter - start, Is.EqualTo(3000));
            Assert.That(sim.State.Rng[RngStreamsNames.Weapon].Counter, Is.EqualTo(3 * sim.State.Banners[Banner.WEAPON].TotalPulls));
            Assert.That(sim.State.Rng[RngStreamsNames.Skill].Counter, Is.EqualTo(3 * sim.State.Banners[Banner.SKILL].TotalPulls));

            var again = new Simulation(replica, Db);
            again.Replay(log.Records);
            Assert.That(again.StateHash(), Is.EqualTo(sim.StateHash()));
        }

        [Test]
        public void RemoteMode_TakesTheServerBalance()
        {
            var sim = Sim();
            var p = Roll(sim, Banner.WEAPON, 1);
            long server = sim.State.Wallet.Diamond - 100 - 40;   // the server ledger saw 40 fewer diamonds
            var remote = p with { StreamCounterAfter = null, BalancesAfter = new[] { new CurrencyDelta(Currency.DIAMOND, server) } };
            long counter = sim.State.Rng[RngStreamsNames.Weapon].Counter;
            Assert.That(sim.Apply(new ApplySummonResult(remote)).Ok, Is.True);
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(server));
            Assert.That(sim.State.Rng[RngStreamsNames.Weapon].Counter, Is.EqualTo(counter), "Remote ignores the local stream (00 decision 14)");
        }

        [Test]
        public void WeaponSummon_AutoEquipsAStrictlyBetterWeapon()   // criterion 8 (auto-equip part)
        {
            var sim = Sim();
            string m = Db.Pool(Banner.WEAPON, Rarity.MYTHIC)[0];
            var counter = sim.State.Rng[RngStreamsNames.Weapon].Counter;
            var p = new SummonResultPayload(Guid.NewGuid().ToString("N"), Banner.WEAPON, 1,
                new[] { new SummonPull(m, Rarity.MYTHIC, true, 0, 1, 1, 0, false) }, 0, 1, 1,
                new[] { new CurrencyDelta(Currency.DIAMOND, -Db.Banners[Banner.WEAPON].PullCostDiamonds) }, null, counter + 3, TestContent.T0);
            string before = sim.State.Loadout.Weapon;
            Assert.That(sim.Apply(new ApplySummonResult(p)).Ok, Is.True);
            Assert.That(sim.State.Loadout.Weapon, Is.EqualTo(m));
            var eq = sim.DrainEvents().OfType<ItemEquipped>().Single();
            Assert.That(eq.Auto, Is.True);
            Assert.That(eq.Banner, Is.EqualTo(Banner.WEAPON));
            Assert.That(eq.Slot, Is.EqualTo(0));
            Assert.That(eq.PreviousItemId, Is.EqualTo(before));
        }

        [Test]
        public void SkillSummon_DoesNotAutoEquip()   // 04 open question 4 default
        {
            var sim = Sim();
            Give(sim, Currency.DIAMOND, 100_000);
            Assert.That(sim.Apply(new ApplySummonResult(Roll(sim, Banner.SKILL, 10))).Ok, Is.True);
            Assert.That(sim.State.Loadout.Skills.All(x => x == null));
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>(), Is.Empty);
            Assert.That(CollectionRules.BetterSkillAvailable(Db, sim.State), Is.True);
        }
    }

    internal static class RngStreamsNames
    {
        public const string Weapon = Rng.RngStreams.SummonWeapon;
        public const string Skill = Rng.RngStreams.SummonSkill;
    }
}
