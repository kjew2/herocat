using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    public class ContentDbTests
    {
        [Test]
        public void ShippedContent_Loads_AndMatchesSpec00()
        {
            var db = TestContent.Db;
            Assert.That(db.Version, Is.EqualTo("1.0"));
            Assert.That(db.ContentHash, Has.Length.EqualTo(64));
            var b = db.HeroBanner;
            Assert.That(Enumerable.Range(0, 5).Select(i => b.Pool[i].Count), Is.EqualTo(new[] { 20, 15, 10, 6, 3 }));
            Assert.That(db.ClassAdvances.Select(c => c.Stage), Is.EqualTo(new[] { 50, 150, 300, 500 }));
            Assert.That(db.HeroLevelThresholds, Is.EqualTo(new[] { 1, 3, 6, 10, 16, 25, 40, 64, 100, 160 }));
            Assert.That(db.GearSlotCount, Is.EqualTo(6));
            Assert.That(db.GearRarity[(int)Rarity.M].Mult, Is.EqualTo(3.0));
            Assert.That(db.Constants.TickHz, Is.EqualTo(10));
            Assert.That(db.Curves[CurveId.EnemyHp].Growth, Is.EqualTo(1.07));
            Assert.That(db.BossRuleFor(550).Id, Is.EqualTo("default"));
            Assert.That(b.BaseLplus, Is.EqualTo(0.03).Within(1e-15));
            Assert.That(b.Pool[0], Is.Ordered.Using((IComparer<string>)System.StringComparer.Ordinal));
            Assert.That(db.Text("hero.c_01.name"), Is.EqualTo("Pell the Farmhand"));
            Assert.That(db.Text("no.such.key"), Is.EqualTo("no.such.key"));
        }

        [Test]
        public void Curves_AgreeWithPow()
        {
            var db = TestContent.Db;
            for (int s = 0; s <= 1000; s += 37)
            {
                Assert.That(db.EnemyHp(s), Is.EqualTo(20 * System.Math.Pow(1.07, s)).Within(1e-9).Percent);
                Assert.That(db.GearSlotPct(s) + 1, Is.EqualTo(System.Math.Pow(1.0074, s)).Within(1e-9).Percent);
            }
            Assert.That(double.IsInfinity(db.EnemyHp(2500)), Is.False);
        }

        [Test]
        public void HashMismatch_IsRejected()
        {
            string dir = FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory);
            var files = Directory.GetFiles(dir, "*.json").ToDictionary(Path.GetFileName, File.ReadAllText);
            files["heroes.json"] = files["heroes.json"].Replace("\"base_pct\": 10.0", "\"base_pct\": 11.0");
            var ex = Assert.Throws<ContentLoadException>(() => ContentDb.Load(new DictionaryContentSource(files)));
            Assert.That(ex.Errors[0].Code, Is.EqualTo("E900"));
        }

        [Test]
        public void CultureInvariant()
        {
            var prev = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var db = ContentDb.LoadFromDirectory(FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory));
                Assert.That(db.Curves[CurveId.EnemyHp].Growth, Is.EqualTo(1.07));
            }
            finally { CultureInfo.CurrentCulture = prev; }
        }

        [Test]
        public void IsCompatibleWith()
        {
            var db = TestContent.Db;   // 1.0
            Assert.That(db.IsCompatibleWith("1.7"), Is.True);
            Assert.That(db.IsCompatibleWith("2.0"), Is.False);
            Assert.That(db.IsCompatibleWith(""), Is.False);
            Assert.That(db.IsCompatibleWith("a.b"), Is.False);
        }
    }
}
