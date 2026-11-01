using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HeroCat.Sim.Content;
using HeroCat.Sim.Content.Dto;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>09 section 9 (issue #125) and the content rows of #80, #89, #98.</summary>
    public class ContentDbTests
    {
        static ContentDb Db => TestContent.Db;

        static string Snake(string pascal) => Regex.Replace(pascal, "(?<=[a-z0-9])([A-Z])", "_$1").ToUpperInvariant();

        [Test]
        public void Constants_PropertiesAreExactlyRequiredConstants_AndAllSet()   // acceptance 4 (C# half)
        {
            var props = typeof(Constants).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var names = props.Select(p => Snake(p.Name)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.That(names, Is.EqualTo(Constants.RequiredKeys.OrderBy(x => x, StringComparer.Ordinal).ToArray()));
            var dtos = Runtime.Content.ContentLoader.LoadDtos(new Runtime.Content.FileContentSource(TestContent.Dir));
            Assert.That(dtos.Constants.Select(c => c.Key), Is.EquivalentTo(Constants.RequiredKeys), "constants.json == REQUIRED_CONSTANTS");
            foreach (var p in props)
            {
                var v = p.GetValue(Db.Constants);
                Assert.That(v, Is.Not.Null, p.Name);
                if (p.PropertyType.IsValueType && p.Name != "SpawnDelayTicks")   // 0 is legal only where 5.1 allows >= 0 and the seed is 0
                    Assert.That(v, Is.Not.EqualTo(Activator.CreateInstance(p.PropertyType)), p.Name + " is default after build");
            }
        }

        [Test]
        public void SeedValues_MatchSpec00()   // acceptance 5 for the rows this skeleton ships
        {
            var k = Db.Constants;
            Assert.That(k.TickHz, Is.EqualTo(10));
            Assert.That(k.BaseAtk, Is.EqualTo(10.0));
            Assert.That(k.BaseHp, Is.EqualTo(100.0));
            Assert.That(k.BossTimeTicks, Is.EqualTo(300));
            Assert.That(k.BossHpMult, Is.EqualTo(10.0));
            Assert.That(k.OfflineCapH, Is.EqualTo(24.0));
            Assert.That(k.TrainBulkSteps, Is.EqualTo(new[] { 1, 10, 100 }));
            Assert.That(k.LoginDiamonds, Is.EqualTo(50));
            Assert.That(k.FishDiscardCoinsPct, Is.EqualTo(0.10));
            Assert.That(Db.EnemyHp.Base, Is.EqualTo(20.0)); Assert.That(Db.EnemyHp.Growth, Is.EqualTo(1.04));
            Assert.That(Db.GoldPerKill.Base, Is.EqualTo(2.0)); Assert.That(Db.GoldPerKill.Growth, Is.EqualTo(1.03));
            Assert.That(Db.ExpPerKill.Base, Is.EqualTo(5.0)); Assert.That(Db.ExpPerKill.Growth, Is.EqualTo(1.025));
            Assert.That(Db.DupeLevels, Is.EqualTo(new[] { 1, 3, 6, 10, 16, 25, 40, 64, 100, 160 }));
            Assert.That(Db.HeroLevels.MultPerLevel, Is.EqualTo(1.02));
            Assert.That(Db.HeroLevels.AtkPctPerLevel, Is.Null);
            Assert.That(Db.Stat(Stat.CRIT_RATE).Cap, Is.EqualTo(300));
            Assert.That(Db.Stat(Stat.HP).Trainable, Is.False);
            Assert.That(Db.TrainingCurves.Keys, Is.EquivalentTo(Db.Stats.Where(s => s.Trainable).Select(s => s.Id)));
        }

        [Test]
        public void Curves_EvaluateByRepeatedMultiplication()   // #80
        {
            Assert.That(Db.EnemyHp.Eval(1), Is.EqualTo(20 * 1.04));
            double pow = 1.0;
            for (int i = 0; i < 10; i++) pow *= 1.025;
            Assert.That(Db.ExpPerKill.Eval(10), Is.EqualTo(5 * pow), "Base * Growth^x, the power by repeated multiplication");
            double m = 1.0;
            for (int i = 0; i < 50; i++) m *= 1.02;
            Assert.That(BitConverter.DoubleToInt64Bits(Db.HeroMult(50)), Is.EqualTo(BitConverter.DoubleToInt64Bits(m)));
            Assert.That(Db.HeroLvlExp(1), Is.EqualTo(100 * 1.08));
            // 03 section 3 spot checks: mob HP 20.8 at stage 1, 1.01e3 at 100, 4.67e10 at 550, within 1%
            Assert.That(Db.EnemyHp.Eval(100), Is.EqualTo(1.01e3).Within(1).Percent);
            Assert.That(Db.EnemyHp.Eval(550), Is.EqualTo(4.67e10).Within(1).Percent);
        }

        [Test]
        public void CurveDef_PastTheTable_ContinuesTheSameChain()
        {
            var small = new CurveDef("x", 2.0, 1.07, 0, 10);
            var big = new CurveDef("x", 2.0, 1.07, 0, 1000);
            foreach (int x in new[] { 0, 9, 10, 11, 500, 999 })
                Assert.That(BitConverter.DoubleToInt64Bits(small.Eval(x)), Is.EqualTo(BitConverter.DoubleToInt64Bits(big.Eval(x))), x.ToString());
        }

        [Test]
        public void CurveDef_PinnedBits_x64()   // acceptance 9
        {
            var c = new CurveDef("enemy_hp", 20, 1.07, 0, 9011);
            var bits = new[] { 0, 1, 10, 100, 550, 9000 }.Select(s => BitConverter.DoubleToInt64Bits(c.Eval(s))).ToArray();
            Assert.That(bits[0], Is.EqualTo(BitConverter.DoubleToInt64Bits(20.0)));
            Assert.That(bits[1], Is.EqualTo(BitConverter.DoubleToInt64Bits(20 * 1.07)));
            Assert.That(c.Eval(9000), Is.EqualTo(double.PositiveInfinity).Or.GreaterThan(1e250));
            Assert.That(bits, Is.EqualTo(PinnedBits), "x64 pins; ARM64 / IL2CPP via spec 10's golden test");
        }

        // Reproduced independently with Python floats (same repeated multiplication), so the pin is not just this code's output.
        static readonly long[] PinnedBits = { 4626322717216342016, 4626716782183736935, 4630733856134293553, 4670499534903690560, 4868415819958230808, 8582799401449798034 };

        [TestCase(0, 1)] [TestCase(0, 10)] [TestCase(0, 100)]
        [TestCase(50, 1)] [TestCase(50, 10)] [TestCase(50, 100)]
        [TestCase(500, 1)] [TestCase(500, 10)] [TestCase(500, 100)]
        public void TrainCost_EqualsTheSumOfSingleLevels(int n, int m)   // #80
        {
            var cost = Db.TrainingCurves[Stat.ATK].Cost;
            double sum = 0;
            for (int i = 0; i < m; i++) sum += cost.Eval(n + i);
            Assert.That(Db.TrainCost(Stat.ATK, n, m), Is.EqualTo(sum).Within(1e-12 * sum));
        }

        [Test]
        public void MaxAffordable_IsTheLargestCountWithinBudget()
        {
            var cost = Db.TrainingCurves[Stat.ATK].Cost;
            foreach (double budget in new[] { 0.0, 49.9, 50.0, 1e4, 1e9 })
            {
                int m = cost.MaxAffordable(7, budget);
                Assert.That(cost.EvalSum(7, m), Is.LessThanOrEqualTo(budget));
                Assert.That(cost.EvalSum(7, m + 1), Is.GreaterThan(budget));
            }
        }

        [Test]
        public void Banners_RatesPoolsAndTiers()   // #89
        {
            var w = Db.Banners[Banner.WEAPON];
            var r10 = w.RateTable(10);
            Assert.That(r10[Rarity.COMMON], Is.EqualTo(0.42).Within(1e-12));
            Assert.That(r10[Rarity.RARE], Is.EqualTo(0.388).Within(1e-12));
            Assert.That(r10[Rarity.EPIC], Is.EqualTo(0.162).Within(1e-12));
            Assert.That(r10[Rarity.LEGENDARY], Is.EqualTo(0.025));
            Assert.That(r10[Rarity.MYTHIC], Is.EqualTo(0.005));
            foreach (var b in Db.Banners.Values.Where(b => b.Levels.Count > 0))
                foreach (var lv in b.Levels)
                {
                    Assert.That(lv.Rates.Values.Sum(), Is.EqualTo(1.0).Within(1e-9), $"{b.Id} {lv.Level}");
                    Assert.That(lv.Rates[Rarity.LEGENDARY], Is.EqualTo(b.Levels[0].Rates[Rarity.LEGENDARY]).Within(1e-12));
                    Assert.That(lv.Rates[Rarity.MYTHIC], Is.EqualTo(b.Levels[0].Rates[Rarity.MYTHIC]).Within(1e-12));
                }
            Assert.That(w.BaseLplus, Is.EqualTo(0.03).Within(1e-12));
            Assert.That(w.PityRate(80), Is.EqualTo(1.0));
            Assert.That(w.PityRate(51), Is.EqualTo(0.03 + 0.0325).Within(1e-12));
            Assert.That(w.LevelFor(0), Is.EqualTo(1));
            Assert.That(w.LevelFor(300), Is.EqualTo(4));
            Assert.That(w.LevelFor(1_000_000), Is.EqualTo(10));
            Assert.That(Rarities().Select(r => w.Pool[r].Count), Is.EqualTo(new[] { 10, 8, 6, 4, 2 }));
            Assert.That(Rarities().Select(r => Db.Banners[Banner.SKILL].Pool[r].Count), Is.EqualTo(new[] { 14, 11, 8, 5, 2 }));
            Assert.That(w.Pool[Rarity.COMMON], Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
            Assert.That(Db.Banners[Banner.RUNE].Enabled, Is.False);
            Assert.That(Db.Banners[Banner.COMPANION].Enabled, Is.False, "MVP banner in an M0 build");
            Assert.That(TestContent.Mvp.Banners[Banner.COMPANION].Enabled, Is.True);
            var e03 = Db.Skills["skill_e_03"];
            Assert.That(e03.CastType, Is.EqualTo(CastType.INSTANT));
            Assert.That(e03.DamagePctPerCast, Is.EqualTo(e03.DpsPct * e03.CooldownTicks / Db.Constants.AttackIntervalTicks));
            Assert.That(Db.Skills.Values.Count(s => s.CastType == CastType.BUFF), Is.EqualTo(6));
            Assert.That(Db.Weapons, Has.Count.EqualTo(30));
            Assert.That(Db.Skills, Has.Count.EqualTo(40));
        }

        static IEnumerable<Rarity> Rarities() => Enum.GetValues(typeof(Rarity)).Cast<Rarity>();

        [Test]
        public void FishLevelsAndTank()   // #98
        {
            Assert.That(Db.FishLevels, Has.Count.EqualTo(20));
            Assert.That(Db.FishLevels[0].Level, Is.EqualTo(1));
            for (int n = 1; n <= 20; n++)
            {
                var f = Db.FishLevels[n - 1];
                double missile = 5;
                for (int i = 1; i < n; i++) missile *= 1.5;
                Assert.That(f.DiscoveryCoins, Is.EqualTo(10L << (n - 1)));
                Assert.That(f.MissilePct, Is.EqualTo(missile).Within(1e-9));
                Assert.That(f.MergeCoins, Is.EqualTo(f.DiscoveryCoins * 0.02).Within(1e-9));
                Assert.That(f.BoardDpsPct, Is.EqualTo(0.5 * f.MissilePct).Within(1e-9));
            }
            Assert.That(Db.FishLevels[4].MissilePct, Is.EqualTo(25.3125));
            Assert.That(Db.Tank.Cap0, Is.EqualTo(10));
            Assert.That(Db.Tank.RefillS0, Is.EqualTo(6.0));
            Assert.That(Db.Tank.RefillTicks0, Is.EqualTo(60));
            Assert.That(Db.Tank.ReleaseLevel, Is.EqualTo(1));
            Assert.That(Db.Tank.RefillFloorS, Is.EqualTo(2.0));
        }

        [Test]
        public void Chapters_Targets_Unlocks_Awakening()
        {
            Assert.That(Db.Chapters, Has.Count.EqualTo(12));
            Assert.That(Db.ChapterFor(1).Index, Is.EqualTo(1));
            Assert.That(Db.ChapterFor(51).Index, Is.EqualTo(2));
            Assert.That(Db.ChapterFor(9000).Index, Is.EqualTo(12), "clamps to the last chapter");
            Assert.That(Db.ProgressTargets.Select(t => t.Stage), Is.EqualTo(new[] { 240, 330, 430, 480, 550 }));
            var mvp = TestContent.Mvp;
            Assert.That(mvp.FeatureUnlocks.Keys, Is.EquivalentTo(new[] { "RESEARCH", "COMPANIONS", "QUESTS", "DUNGEONS", "SHOP" }));
            var s = TestContent.NewState();
            Assert.That(mvp.IsUnlocked("SHOP", s), Is.True);
            Assert.That(mvp.IsUnlocked("QUESTS", s), Is.False);
            s.Progress.HighestCleared = 10;
            Assert.That(mvp.IsUnlocked("QUESTS", s), Is.True);
            Assert.That(mvp.IsUnlocked("RESEARCH", s), Is.False);
            s.Hero.AwakenRank = 1;
            Assert.That(mvp.IsUnlocked("RESEARCH", s), Is.True);
            Assert.That(Db.IsUnlocked("SHOP", s), Is.False, "MVP rows are filtered out of an M0 build");
            Assert.That(mvp.Awakening.Select(a => a.StageGate), Is.EqualTo(new[] { 50, 150, 300, 500, 800 }));
            Assert.That(mvp.AwakeningTargets.All(t => t.MaxDaysAfterGate == 2), Is.True);
        }

        [Test]
        public void Build_ReportsEveryError_InTheExporterFormat()
        {
            var dtos = Runtime.Content.ContentLoader.LoadDtos(new Runtime.Content.FileContentSource(TestContent.Dir));
            dtos.Constants = dtos.Constants.Where(c => c.Key != "TICK_HZ").Concat(new[]
            {
                new ConstantRowDto { Key = "GEMS_PER_DAY", Type = "int", Value = 1L, Kind = "S" },
            }).Select(c => c.Key == "BASE_ATK" ? new ConstantRowDto { Key = c.Key, Type = "int", Value = c.Value, Kind = c.Kind } : c).ToArray();
            var e = Assert.Throws<ContentBuildException>(() => ContentDb.Build(dtos, Tier.M0));
            Assert.That(e.Errors.Select(x => x.ToString()), Is.EquivalentTo(new[]
            {
                "CONTENT E041 constants[BASE_ATK].type: declared type 'int' does not match required type 'double'",
                "CONTENT E042 constants[GEMS_PER_DAY]: unknown constant 'GEMS_PER_DAY' (add it to REQUIRED_CONSTANTS and ContentDb.Constants first)",
                "CONTENT E040 constants: missing required constant 'TICK_HZ'",
            }));
        }

        [Test]
        public void Build_DropsRowsAboveTheBuildTier_AndKeepsDisabledBannersListed()   // acceptance 13
        {
            var dtos = Runtime.Content.ContentLoader.LoadDtos(new Runtime.Content.FileContentSource(TestContent.Dir));
            dtos.Weapons[dtos.Weapons.Length - 1].Tier = "MVP";
            var db = ContentDb.Build(dtos, Tier.M0);
            Assert.That(db.Weapons, Has.Count.EqualTo(29));
            Assert.That(db.Banners.Keys, Is.EquivalentTo(Enum.GetValues(typeof(Banner)).Cast<Banner>()));
        }

        [Test]
        public void Build_IsFast()   // acceptance 17 (CONTENT_BUILD_MS_MAX = 30 warm)
        {
            var dtos = Runtime.Content.ContentLoader.LoadDtos(new Runtime.Content.FileContentSource(TestContent.Dir));
            ContentDb.Build(dtos, Tier.MVP);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) ContentDb.Build(dtos, Tier.MVP);
            Assert.That(sw.Elapsed.TotalMilliseconds / 5, Is.LessThan(30));
        }
    }
}
