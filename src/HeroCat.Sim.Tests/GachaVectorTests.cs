using System.Linq;
using HeroCat.Sim.Content;
using HeroCat.Sim.Gacha;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>Replays Fixtures/Gacha/vectors.json against LocalGachaService (specs/08 section 4.3 rule 4).</summary>
    public class GachaVectorTests
    {
        static GachaVectorFile Vectors => GachaVectorFile.LoadFromRepo(TestContext.CurrentContext.TestDirectory);

        [Test]
        public void FileShape()
        {
            var f = Vectors;
            Assert.That(f.ContentHash, Is.EqualTo(TestContent.Db.ContentHash), "content changed: " + GachaVectorFile.Regenerate);
            Assert.That(f.Vectors.Length, Is.EqualTo(20));
            foreach (var v in f.Vectors)
            {
                Assert.That(v.Batches.Sum(b => b.Length), Is.EqualTo(f.PullsPerSeed));
                Assert.That(v.Batches.Select(b => b.Length).Distinct().OrderBy(n => n), Is.EqualTo(new[] { 1, 10 }));
            }
        }

        [Test]
        public void LocalGachaService_MatchesVectors()
        {
            var f = Vectors;
            var db = TestContent.Db;
            var banner = db.Banners[f.BannerId];
            IGachaService gacha = new LocalGachaService();
            foreach (var v in f.Vectors)
            {
                var rng = new RngStream(StreamNames.GachaHero, v.StreamSeed);
                var st = new BannerState();
                var heroes = new HeroState();
                var cur = new Currencies();
                int pulls = 0, pity = 0;
                for (int b = 0; b < v.Batches.Length; b++)
                {
                    var expected = v.Batches[b];
                    var got = gacha.Pull(db, banner, st, heroes, cur, rng, expected.Length);
                    for (int i = 0; i < got.Length; i++)
                    {
                        pity = got[i].Tier >= Rarity.L ? 0 : pity + 1;
                        string at = $"profileSeed {v.ProfileSeed}, pull {pulls + i + 1}";
                        Assert.That(got[i].Tier, Is.EqualTo(expected[i].Tier), at);
                        Assert.That(got[i].Hero, Is.EqualTo(expected[i].HeroId), at);
                        Assert.That(pity, Is.EqualTo(expected[i].PityAfter), at);
                    }
                    pulls += got.Length;
                    Assert.That(st.PityCounter, Is.EqualTo(pity), $"profileSeed {v.ProfileSeed}, batch {b}");
                    Assert.That(st.TotalPulls, Is.EqualTo(pulls));
                    Assert.That(rng.Counter, Is.EqualTo((ulong)(3 * pulls)));
                }
            }
        }
    }
}
