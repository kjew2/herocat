using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>A scripted uniform source (04 section 3.4: tests script u1..u3).</summary>
    internal sealed class ScriptedSource : IUniformSource
    {
        readonly IReadOnlyList<double> _u;
        public ScriptedSource(IReadOnlyList<double> u) { _u = u; }
        public int Used { get; private set; }
        public double Next() => _u[Used++];
    }

    /// <summary>
    /// specs/04 section 3.4 and 16 criteria 1, 3, 4, 5 against Fixtures/Summon/vectors.json, which tools/gacha_model.py
    /// generates (00 decision 46). These tests only read it (regenerate with the command in its "regenerate" field).
    /// </summary>
    public class SummonTests
    {
        static readonly Lazy<JsonDocument> _vectors = new Lazy<JsonDocument>(() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Fixtures", "Summon", "vectors.json"))));

        static JsonElement V => _vectors.Value.RootElement;
        static ContentDb Db => TestContent.Db;

        static Rarity R(JsonElement e) => (Rarity)Enum.Parse(typeof(Rarity), e.GetString());
        static Banner B(JsonElement e) => (Banner)Enum.Parse(typeof(Banner), e.GetString());
        static double[] Us(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

        static BannerState StateAt(BannerDef def, int pity, int level) =>
            new BannerState { Pity = pity, Exp = level > 1 ? def.Levels[level - 1].ExpRequired - def.ExpPerPull : 0 };

        [Test]
        public void Vectors_AreForThisContent()
        {
            Assert.That(V.GetProperty("format").GetInt32(), Is.EqualTo(1));
            Assert.That(V.GetProperty("contentHash").GetString(), Is.EqualTo(Db.ContentHash),
                        "content changed: regenerate Fixtures/Summon/vectors.json with tools/gacha_model.py --vectors");
        }

        [Test]
        public void V01_PityRate_MatchesTheSpecRamp()
        {
            var def = Db.Banners[Banner.WEAPON];
            var spec = new Dictionary<int, double> { [1] = 0.03, [50] = 0.03, [51] = 0.0625, [55] = 0.1925, [60] = 0.355, [65] = 0.5175, [70] = 0.68, [75] = 0.8425, [79] = 0.9725, [80] = 1.0, [81] = 1.0 };
            foreach (var row in V.GetProperty("pityRate").EnumerateArray())
            {
                int n = row.GetProperty("n").GetInt32();
                Assert.That(def.PityRate(n), Is.EqualTo(row.GetProperty("rate").GetDouble()), $"n={n} (bit-exact vs Python)");
                Assert.That(def.PityRate(n), Is.EqualTo(spec[n]).Within(1e-12), $"n={n} vs 04 vector 1");
            }
        }

        [Test]
        public void V02_to_V12_V16_ScriptedVectors()
        {
            int count = 0;
            foreach (var v in V.GetProperty("scripted").EnumerateArray())
            {
                string name = v.GetProperty("name").GetString();
                var def = Db.Banners[B(v.GetProperty("banner"))];
                var setup = v.GetProperty("setup");
                var st = new BannerState { Pity = setup.GetProperty("pity").GetInt32(), Exp = setup.GetProperty("exp").GetInt64(), TotalPulls = setup.GetProperty("totalPulls").GetInt64() };
                var col = new CollectionState();
                foreach (var c in setup.GetProperty("copies").EnumerateObject()) col.Items[c.Name] = new OwnedItem { Copies = c.Value.GetInt32() };
                var us = Us(v.GetProperty("u"));
                var rng = new ScriptedSource(us);
                PullResult[] got = v.GetProperty("mode").GetString() == "pull10"
                    ? PullAlgorithm.Pull10(Db, def, st, col, rng)
                    : new[] { PullAlgorithm.Pull(Db, def, st, col, rng, v.GetProperty("forceEplus").GetBoolean()) };
                Assert.That(rng.Used, Is.EqualTo(us.Length), name + ": three draws per pull, always consumed");
                var expect = v.GetProperty("expect").EnumerateArray().ToArray();
                Assert.That(got.Length, Is.EqualTo(expect.Length), name);
                for (int i = 0; i < got.Length; i++) AssertPull(got[i], expect[i], $"{name} slot {i + 1}");
                var es = v.GetProperty("expectState");
                Assert.That(st.Pity, Is.EqualTo(es.GetProperty("pity").GetInt32()), name);
                Assert.That(st.Exp, Is.EqualTo(es.GetProperty("exp").GetInt64()), name);
                Assert.That(st.TotalPulls, Is.EqualTo(es.GetProperty("totalPulls").GetInt64()), name);
                count++;
            }
            Assert.That(count, Is.GreaterThanOrEqualTo(15));
        }

        static void AssertPull(PullResult r, JsonElement e, string what)
        {
            Assert.That(r.ItemId, Is.EqualTo(e.GetProperty("itemId").GetString()), what);
            Assert.That(r.Rarity, Is.EqualTo(R(e.GetProperty("rarity"))), what);
            Assert.That(r.IsNew, Is.EqualTo(e.GetProperty("isNew").GetBoolean()), what);
            Assert.That(r.GuaranteeApplied, Is.EqualTo(e.GetProperty("guaranteeApplied").GetBoolean()), what);
            Assert.That(r.CopiesAfter, Is.EqualTo(e.GetProperty("copiesAfter").GetInt32()), what);
            Assert.That(r.LevelBefore, Is.EqualTo(e.GetProperty("levelBefore").GetInt32()), what);
            Assert.That(r.LevelAfter, Is.EqualTo(e.GetProperty("levelAfter").GetInt32()), what);
            Assert.That(r.OverflowRockCandy, Is.EqualTo(e.GetProperty("overflowRockCandy").GetInt64()), what);
            Assert.That(r.PityAfter, Is.EqualTo(e.GetProperty("pityAfter").GetInt32()), what);
            Assert.That(r.BannerLevelAfter, Is.EqualTo(e.GetProperty("bannerLevelAfter").GetInt32()), what);
        }

        [Test]
        public void V02_to_V10_SpecExpectations_HoldOnTheVectors()   // the spec's own words, so a regenerated file cannot drift from 04
        {
            var byName = V.GetProperty("scripted").EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString());
            string Rar(string n, int slot = 0) => byName[n].GetProperty("expect")[slot].GetProperty("rarity").GetString();
            var c0 = Db.Pool(Banner.WEAPON, Rarity.COMMON)[0];
            Assert.That(byName["v02_common_first_item"].GetProperty("expect")[0].GetProperty("itemId").GetString(), Is.EqualTo(c0));
            Assert.That(byName["v02_common_first_item"].GetProperty("expectState").GetProperty("pity").GetInt32(), Is.EqualTo(2));
            Assert.That(Rar("v03_cr_below"), Is.EqualTo("COMMON"));
            Assert.That(Rar("v03_cr_at"), Is.EqualTo("RARE"));
            Assert.That(Rar("v04_re_below"), Is.EqualTo("RARE"));
            Assert.That(Rar("v04_re_at"), Is.EqualTo("EPIC"));
            Assert.That(Rar("v05_pity79_below"), Is.AnyOf("LEGENDARY", "MYTHIC"));
            Assert.That(Rar("v05_pity79_at"), Is.Not.AnyOf("LEGENDARY", "MYTHIC"));
            Assert.That(Rar("v06_hard_mythic"), Is.EqualTo("MYTHIC"));
            Assert.That(Rar("v07_hard_legendary"), Is.EqualTo("LEGENDARY"));
            Assert.That(Rar("v08_guarantee_fires", 9), Is.EqualTo("EPIC"));
            Assert.That(byName["v08_guarantee_fires"].GetProperty("expect")[9].GetProperty("guaranteeApplied").GetBoolean(), Is.True);
            Assert.That(Rar("v09_guarantee_not_needed", 2), Is.EqualTo("EPIC"));
            Assert.That(Rar("v09_guarantee_not_needed", 9), Is.EqualTo("COMMON"));
            Assert.That(Rar("v10_guarantee_lplus_first", 9), Is.AnyOf("LEGENDARY", "MYTHIC"));
            Assert.That(byName["v12_overflow_copy_161"].GetProperty("expect")[0].GetProperty("overflowRockCandy").GetInt64(),
                        Is.EqualTo(Db.Rarities[Rarity.COMMON].DupeOverflowRockCandy));
            Assert.That(byName["v16_level_after_exp"].GetProperty("expect")[0].GetProperty("bannerLevelAfter").GetInt32(), Is.EqualTo(2),
                        "the 50th pull already rolls at level 2 (04 section 3.2)");
        }

        [Test]
        public void V08_Rows_And_Batches()   // 08 section 4: >= 500 rows and 20 batches the server must reproduce too
        {
            var rows = V.GetProperty("rows").EnumerateArray().ToArray();
            Assert.That(rows.Length, Is.GreaterThanOrEqualTo(500));
            foreach (var row in rows)
            {
                var def = Db.Banners[B(row.GetProperty("banner"))];
                var st = StateAt(def, row.GetProperty("pity").GetInt32(), row.GetProperty("level").GetInt32());
                var r = PullAlgorithm.Pull(Db, def, st, new CollectionState(), new ScriptedSource(Us(row.GetProperty("u"))),
                                           row.GetProperty("forceEplus").GetBoolean());
                string what = row.ToString();
                Assert.That(r.Rarity, Is.EqualTo(R(row.GetProperty("rarity"))), what);
                Assert.That(def.Pool[r.Rarity].ToList().IndexOf(r.ItemId), Is.EqualTo(row.GetProperty("poolIndex").GetInt32()), what);
                Assert.That(r.ItemId, Is.EqualTo(row.GetProperty("itemId").GetString()), what);
                Assert.That(r.PityAfter, Is.EqualTo(row.GetProperty("pityAfter").GetInt32()), what);
                Assert.That(r.GuaranteeApplied, Is.EqualTo(row.GetProperty("guaranteeApplied").GetBoolean()), what);
                Assert.That(r.BannerLevelAfter, Is.EqualTo(row.GetProperty("bannerLevelAfter").GetInt32()), what);
            }
            var batches = V.GetProperty("batches").EnumerateArray().ToArray();
            Assert.That(batches.Length, Is.EqualTo(20));
            foreach (var b in batches)
            {
                var def = Db.Banners[B(b.GetProperty("banner"))];
                var st = StateAt(def, b.GetProperty("pity").GetInt32(), b.GetProperty("level").GetInt32());
                var got = PullAlgorithm.Pull10(Db, def, st, new CollectionState(), new ScriptedSource(Us(b.GetProperty("u"))));
                var exp = b.GetProperty("expect").EnumerateArray().ToArray();
                for (int i = 0; i < got.Length; i++)
                {
                    Assert.That(got[i].Rarity, Is.EqualTo(R(exp[i].GetProperty("rarity"))));
                    Assert.That(got[i].ItemId, Is.EqualTo(exp[i].GetProperty("itemId").GetString()));
                    Assert.That(got[i].PityAfter, Is.EqualTo(exp[i].GetProperty("pityAfter").GetInt32()));
                    Assert.That(got[i].GuaranteeApplied, Is.EqualTo(exp[i].GetProperty("guaranteeApplied").GetBoolean()));
                }
            }
        }

        [Test]
        public void V11_DrawCount_IsThreePerPull()
        {
            var d = V.GetProperty("drawCount");
            var def = Db.Banners[Banner.WEAPON];
            var st = new BannerState();
            var rng = new StreamUniformSource((ulong)d.GetProperty("seed").GetInt64(), 0);
            var counts = new Dictionary<Rarity, int>();
            for (int i = 0; i < d.GetProperty("pulls").GetInt32(); i++)
            {
                var r = PullAlgorithm.Pull(Db, def, st, new CollectionState(), rng, false);
                counts.TryGetValue(r.Rarity, out int n);
                counts[r.Rarity] = n + 1;
            }
            Assert.That(rng.Counter, Is.EqualTo(3000).And.EqualTo(d.GetProperty("counterAfter").GetInt64()));
            Assert.That(st.TotalPulls, Is.EqualTo(1000));
            Assert.That(st.Exp, Is.EqualTo(1000).And.EqualTo(d.GetProperty("exp").GetInt64()));
            Assert.That(st.Pity, Is.EqualTo(d.GetProperty("pityAfter").GetInt32()));
            foreach (var c in d.GetProperty("rarityCounts").EnumerateObject())
                Assert.That(counts.TryGetValue((Rarity)Enum.Parse(typeof(Rarity), c.Name), out int n) ? n : 0, Is.EqualTo(c.Value.GetInt32()), c.Name);
        }

        [Test]
        public void V12_CopiesToLevels_AndCriterion5()
        {
            foreach (var row in V.GetProperty("levels").EnumerateArray())
                Assert.That(Db.DupeLevel(row.GetProperty("copies").GetInt32()), Is.EqualTo(row.GetProperty("level").GetInt32()));
            int[] cum = { 1, 3, 6, 10, 16, 25, 40, 64, 100, 160 };
            for (int i = 0; i < cum.Length; i++) Assert.That(Db.DupeLevel(cum[i]), Is.EqualTo(i + 1), $"criterion 5: {cum[i]} copies");
            Assert.That(Db.LevelMult(10), Is.EqualTo(2.8).Within(1e-12));
            Assert.That(Db.LevelMult(10 + 5), Is.EqualTo(3.8).Within(1e-12), "level 10 with 5 limit breaks");
            Assert.That(PullAlgorithm.MaxCopies(Db), Is.EqualTo(160));
        }

        [Test]
        public void V16_BannerLevelThresholds()
        {
            var def = Db.Banners[Banner.WEAPON];
            foreach (var row in V.GetProperty("bannerLevels").EnumerateArray())
                Assert.That(def.LevelFor(row.GetProperty("exp").GetInt64()), Is.EqualTo(row.GetProperty("level").GetInt32()), row.ToString());
            int[] expect = { 1, 1, 2, 9, 10, 10 };
            Assert.That(V.GetProperty("bannerLevels").EnumerateArray().Select(r => r.GetProperty("level").GetInt32()), Is.EqualTo(expect));
        }

        /// <summary>The MC loop gacha_model.py runs: each single pull at a held level; then pulls/10 ten-pulls from pity 0.</summary>
        static (Dictionary<Rarity, int> counts, int fired, int batches) MonteCarlo(BannerDef def, int pulls, ulong seed, int level)
        {
            var st = new BannerState();
            var col = new CollectionState();
            var rng = new StreamUniformSource(seed, 0);
            var counts = Enum.GetValues(typeof(Rarity)).Cast<Rarity>().ToDictionary(r => r, r => 0);
            long threshold = def.Levels[level - 1].ExpRequired;
            for (int i = 0; i < pulls; i++)
            {
                st.Exp = threshold - def.ExpPerPull;
                var r = PullAlgorithm.Pull(Db, def, st, col, rng, false);
                if (i == 0) Assert.That(r.BannerLevelAfter, Is.EqualTo(level));
                counts[r.Rarity]++;
            }
            var rng10 = new StreamUniformSource(seed, 0);
            int batches = pulls / def.GuaranteeBatchSize, fired = 0;
            for (int i = 0; i < batches; i++)
            {
                var b = PullAlgorithm.Pull10(Db, def, new BannerState { Exp = threshold }, col, rng10);
                if (!b.Take(b.Length - 1).Any(x => x.Rarity >= def.GuaranteeMinRarity)) fired++;
            }
            return (counts, fired, batches);
        }

        [Test]
        public void V13_V14_MonteCarlo_ExactCountsMatchPython()
        {
            var def = Db.Banners[Banner.WEAPON];
            foreach (var m in V.GetProperty("monteCarlo").EnumerateArray())
            {
                var (counts, fired, batches) = MonteCarlo(def, m.GetProperty("pulls").GetInt32(), (ulong)m.GetProperty("seed").GetInt64(),
                                                          m.GetProperty("level").GetInt32());
                foreach (var c in m.GetProperty("rarityCounts").EnumerateObject())
                    Assert.That(counts[(Rarity)Enum.Parse(typeof(Rarity), c.Name)], Is.EqualTo(c.Value.GetInt32()), m.GetProperty("name").GetString() + " " + c.Name);
                Assert.That(batches, Is.EqualTo(m.GetProperty("batches").GetInt32()));
                Assert.That(fired, Is.EqualTo(m.GetProperty("guaranteeFired").GetInt32()));
            }
        }

        [TestCase(1, 0.60, 0.28, 0.09)]
        [TestCase(10, 0.42, 0.388, 0.162)]
        public void V13_V14_MonteCarlo_Million_WithinSpecTolerance(int level, double c, double r, double e)   // and criterion 4
        {
            var def = Db.Banners[Banner.WEAPON];
            var (counts, fired, batches) = MonteCarlo(def, 1_000_000, 7, level);
            double total = counts.Values.Sum();
            double lplus = (counts[Rarity.LEGENDARY] + counts[Rarity.MYTHIC]) / total;
            double nonL = counts[Rarity.COMMON] + counts[Rarity.RARE] + counts[Rarity.EPIC];
            double cre = c + r + e;   // the "of non-L+" split is the level's C/R/E renormalised by C + R + E
            Assert.That(lplus, Is.EqualTo(0.03657).Within(0.001), "L+ within 0.1 pp of 3.657%");
            Assert.That(counts[Rarity.MYTHIC] / total, Is.EqualTo(0.00609).Within(0.0003), "M within 0.03 pp of 0.609%");
            Assert.That(counts[Rarity.COMMON] / nonL, Is.EqualTo(c / cre).Within(0.003));
            Assert.That(counts[Rarity.RARE] / nonL, Is.EqualTo(r / cre).Within(0.003));
            Assert.That(counts[Rarity.EPIC] / nonL, Is.EqualTo(e / cre).Within(0.003));
            double eplus = e + 0.03;
            double expectFire = 1;
            for (int i = 0; i < 9; i++) expectFire *= 1 - eplus;
            Assert.That(batches, Is.EqualTo(100_000));
            Assert.That(fired / (double)batches, Is.EqualTo(expectFire).Within(0.005), "criterion 4: guarantee fire rate (1 - P(E+))^9");
            if (level == 1) Assert.That(expectFire, Is.EqualTo(0.316).Within(0.0005));
        }

        [Test]
        public void Criterion3_PityRateMonotone_AndPityInRange()
        {
            foreach (var def in Db.Banners.Values.Where(b => b.Enabled && b.Levels.Count > 0))
            {
                for (int n = 1; n < def.PityHard + 5; n++) Assert.That(def.PityRate(n + 1), Is.GreaterThanOrEqualTo(def.PityRate(n)), $"{def.Id} n={n}");
                Assert.That(def.PityRate(def.PityHard), Is.EqualTo(1.0));
            }
            foreach (var b in new[] { Banner.WEAPON, Banner.SKILL })
            {
                var def = Db.Banners[b];
                var st = new BannerState();
                var col = new CollectionState();
                var rng = new StreamUniformSource(0xC0FFEE, 0);
                for (int i = 0; i < 2000; i++)
                {
                    foreach (var r in PullAlgorithm.PullBatch(Db, def, st, col, rng, i % 3 == 0 ? 10 : 1))
                        Assert.That(r.PityAfter, Is.InRange(0, def.PityHard - 1));
                    Assert.That(st.Pity, Is.InRange(0, def.PityHard - 1));
                }
                Assert.That(rng.Counter, Is.EqualTo(3 * st.TotalPulls));
            }
        }

        [Test]
        public void Criterion4_Pull10Guarantee_Scripted()
        {
            var def = Db.Banners[Banner.WEAPON];
            var low = Enumerable.Repeat(new[] { 0.99, 0.0, 0.0 }, 10).SelectMany(x => x).ToArray();
            var r = PullAlgorithm.Pull10(Db, def, new BannerState(), new CollectionState(), new ScriptedSource(low));
            Assert.That(r.Take(9).All(x => x.Rarity == Rarity.COMMON));
            Assert.That(r[9].Rarity, Is.EqualTo(Rarity.EPIC));
            Assert.That(r[9].GuaranteeApplied, Is.True);
        }

        [Test]
        public void V15_SameSeedAndCounter_GiveIdenticalSequences()
        {
            foreach (var b in new[] { Banner.WEAPON, Banner.SKILL })
            {
                var def = Db.Banners[b];
                var s1 = new BannerState { Pity = 12, Exp = 40, TotalPulls = 40 };
                var s2 = new BannerState { Pity = 12, Exp = 40, TotalPulls = 40 };
                var c1 = new CollectionState();
                var c2 = new CollectionState();
                var r1 = new StreamUniformSource(99, 120);
                var r2 = new StreamUniformSource(99, 120);
                for (int i = 0; i < 1000; i++)
                    Assert.That(PullAlgorithm.Pull(Db, def, s1, c1, r1, false), Is.EqualTo(PullAlgorithm.Pull(Db, def, s2, c2, r2, false)), $"{b} pull {i}");
            }
        }

        [Test]
        public void StreamSource_ReadsButNeverWritesTheSavedStream()
        {
            var s = new RngStreamState { Seed = "00000000000000ff", Counter = 30 };
            var src = StreamUniformSource.From(s);
            double a = src.Next();
            Assert.That(s.Counter, Is.EqualTo(30));
            Assert.That(src.Counter, Is.EqualTo(31));
            Assert.That(a, Is.EqualTo(Rng.RngStreams.NextUnit(new RngStreamState { Seed = s.Seed, Counter = 30 })));
        }
    }
}
