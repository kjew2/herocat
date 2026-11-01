using System.Linq;
using HeroCat.Sim.Content;
using HeroCat.Sim.Gacha;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    public class GachaTests
    {
        static ContentDb Db => TestContent.Db;
        static BannerDef Banner => Db.HeroBanner;

        static (BannerState, HeroState, Currencies) Fresh() =>
            (new BannerState(), new HeroState(), new Currencies());

        [Test]
        public void RatesSumToOne() => Assert.That(Banner.Rates.Sum(), Is.EqualTo(1.0).Within(1e-9));

        [TestCase(1, 0.03)]
        [TestCase(50, 0.03)]
        [TestCase(51, 0.0625)]
        [TestCase(55, 0.1925)]
        [TestCase(60, 0.3550)]
        [TestCase(65, 0.5175)]
        [TestCase(70, 0.6800)]
        [TestCase(75, 0.8425)]
        [TestCase(79, 0.9725)]
        [TestCase(80, 1.0)]
        [TestCase(81, 1.0)]
        public void PityRamp(int n, double rate) => Assert.That(Banner.PityRate(n), Is.EqualTo(rate).Within(1e-12));

        [Test]
        public void PityRamp_Monotone()
        {
            for (int n = 1; n < 120; n++) Assert.That(Banner.PityRate(n + 1), Is.GreaterThanOrEqualTo(Banner.PityRate(n)));
        }

        [Test]
        public void HardPity_At80()
        {
            var (st, h, c) = Fresh();
            var rng = new ScriptedRandom(0.999, 0.5, 0.5);
            PullResult last = default;
            for (int i = 1; i <= 80; i++)
            {
                last = GachaAlgorithm.PullOne(Db, Banner, st, h, c, rng, false);
                if (i < 80) Assert.That(last.Tier, Is.LessThan(Rarity.L), $"pull {i}");
            }
            Assert.That(last.Tier, Is.EqualTo(Rarity.L));
            Assert.That(st.PityCounter, Is.EqualTo(0));
        }

        [Test]
        public void TenPull_GuaranteeFiresOnSlot10()
        {
            var (st, h, c) = Fresh();
            var r = GachaAlgorithm.PullBatch(Db, Banner, st, h, c, new ScriptedRandom(0.99, 0.0, 0.5), 10);
            Assert.That(r.Take(9).Select(x => x.Tier), Is.All.EqualTo(Rarity.C));
            Assert.That(r[9].Tier, Is.EqualTo(Rarity.E));
        }

        [Test]
        public void TenPull_DoesNotDowngrade()
        {
            var (st, h, c) = Fresh();
            var rng = new ScriptedRandom(0.99, 0.0, 0.5) { Override = i => i == 2 * 3 + 1 ? 0.95 : double.NaN };   // slot 3's u2
            var r = GachaAlgorithm.PullBatch(Db, Banner, st, h, c, rng, 10);
            Assert.That(r[2].Tier, Is.EqualTo(Rarity.E));
            Assert.That(r[9].Tier, Is.EqualTo(Rarity.C));
        }

        [Test]
        public void DrawCount_IsThreePerPull_AndReproducible()
        {
            var (st1, h1, c1) = Fresh();
            var (st2, h2, c2) = Fresh();
            var a = new RngStream(StreamNames.GachaHero, 7);
            var b = new RngStream(StreamNames.GachaHero, 7);
            for (int i = 0; i < 1000; i++)
            {
                var x = GachaAlgorithm.PullOne(Db, Banner, st1, h1, c1, a, false);
                var y = GachaAlgorithm.PullOne(Db, Banner, st2, h2, c2, b, false);
                Assert.That(x, Is.EqualTo(y));
            }
            Assert.That(a.Counter, Is.EqualTo(3000UL));
        }

        [Test]
        public void ConsolidatedRate_MatchesModel()
        {
            var (st, h, c) = Fresh();
            var rng = new RngStream(StreamNames.GachaHero, 7);
            int n = 1_000_000, lplus = 0;
            for (int i = 0; i < n; i++)
                if (GachaAlgorithm.PullOne(Db, Banner, st, h, c, rng, false).Tier >= Rarity.L) lplus++;
            Assert.That((double)lplus / n, Is.EqualTo(0.036567).Within(0.001));
        }

        [Test]
        public void DupeLevels_AndShards()
        {
            int[] copies = { 1, 2, 3, 6, 10, 160, 161 };
            int[] levels = { 1, 1, 2, 3, 4, 10, 10 };
            for (int i = 0; i < copies.Length; i++) Assert.That(Db.HeroLevel(copies[i]), Is.EqualTo(levels[i]));

            var (st, h, c) = Fresh();
            string id = Banner.Pool[(int)Rarity.C][0];
            h.Copies[id] = 160;
            var r = GachaAlgorithm.PullOne(Db, Banner, st, h, c, new ScriptedRandom(0.99, 0.0, 0.0), false);
            Assert.That(r.Hero, Is.EqualTo(id));
            Assert.That(r.ShardsGained, Is.EqualTo(Db.HeroTiers[(int)Rarity.C].ShardValue));
            Assert.That(c.Shards, Is.EqualTo(1));
        }

        [Test]
        public void Sim_Pull_UsesDailyThenBankedThenGems_AndPull10Guards()
        {
            var sim = TestContent.NewSim();
            sim.Tick();                    // rolls the first day: 3 daily free pulls
            sim.ApplyTutorialGrants();     // 1000 gems, 10 banked
            Assert.That(sim.State.Currencies.FreePullsDaily, Is.EqualTo(3));
            for (int i = 0; i < 3; i++) Assert.That(sim.Pull().PaidWith, Is.EqualTo(PaidWith.FreeDaily));
            Assert.That(sim.Pull().PaidWith, Is.EqualTo(PaidWith.FreeBanked));
            var ten = sim.Pull10();         // 9 banked left -> gems
            Assert.That(ten.PaidWith, Is.EqualTo(PaidWith.Gems));
            Assert.That(ten.Results, Has.Length.EqualTo(10));
            Assert.That(sim.State.Currencies.Gems, Is.EqualTo(50 + 30));   // login + do_pulls quest
            var before = TestContent.Dump(sim.State);
            Assert.That(sim.Pull10().Reason, Is.EqualTo(Reject.NotEnoughGems));
            Assert.That(TestContent.Dump(sim.State), Is.EqualTo(before));
            Assert.That(sim.State.Heroes.Equipped.Count(x => x != null), Is.EqualTo(6));
            Assert.That(sim.Query.HeroMult, Is.GreaterThan(1.0));
            sim.Validate();
        }

        [Test]
        public void EquipHero_Rules()
        {
            var sim = TestContent.NewSim();
            string a = Banner.Pool[0][0], b = Banner.Pool[0][1];
            Assert.That(sim.EquipHero(0, a).Reason, Is.EqualTo(Reject.HeroNotOwned));
            sim.Debug.GrantHeroCopies(a, 1);
            sim.Debug.GrantHeroCopies(b, 1);
            Assert.That(sim.State.Heroes.Equipped[0], Is.EqualTo(a));   // auto-equip on grant
            Assert.That(sim.EquipHero(3, a).Reason, Is.EqualTo(Reject.HeroAlreadyEquipped));
            Assert.That(sim.UnequipHero(0).Ok, Is.True);
            Assert.That(sim.EquipHero(3, a).Ok, Is.True);
            Assert.That(sim.State.Heroes.AutoEquipBest, Is.False);
            Assert.That(sim.Query.HeroMult, Is.EqualTo(1.2).Within(1e-12));
            Assert.That(sim.AutoEquipBestHeroes(), Is.GreaterThan(0));
            sim.Validate();
        }
    }
}
