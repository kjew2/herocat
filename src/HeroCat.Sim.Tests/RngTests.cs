using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    public class RngTests
    {
        [TestCase(0x0000000000000000UL, 0UL, 0xE220A8397B1DCDAFUL, 0.88331080821364261)]
        [TestCase(0x0000000000000000UL, 1UL, 0x6E789E6AA1B965F4UL, 0.43152799704850997)]
        [TestCase(0x0000000000000000UL, 2UL, 0x06C45D188009454FUL, 0.026433771592597743)]
        [TestCase(0x0000000000000000UL, 1000UL, 0x2CFA2F23425329E1UL, 0.17569250689332405)]
        [TestCase(0x0000000000000007UL, 0UL, 0x63CBE1E459320DD7UL, 0.38982974839127149)]
        [TestCase(0x0000000000000007UL, 1UL, 0x044C3CD7F43C661CUL, 0.016788294528156111)]
        [TestCase(0x0000000000000007UL, 2UL, 0xE6984080BAB12A02UL, 0.90076068060688341)]
        [TestCase(0x0000000000000007UL, 1000UL, 0xCF1B8D545D1615CAUL, 0.80901416120203629)]
        [TestCase(0x123456789ABCDEF0UL, 0UL, 0x161922C645CE50E8UL, 0.08632104244844041)]
        [TestCase(0x123456789ABCDEF0UL, 1UL, 0xAD760CAFA1697B60UL, 0.67758254325727829)]
        public void Derive_MatchesSpecVectors(ulong seed, ulong k, ulong expected, double unit)
        {
            Assert.That(SplitMix64.Derive(seed, k), Is.EqualTo(expected));
            Assert.That(SplitMix64.ToUnit(SplitMix64.Derive(seed, k)), Is.EqualTo(unit));
        }

        [Test]
        public void PersistedStream_DrawsDeriveSeedCounter_AndAdvancesTheCounter()
        {
            var s = new RngStreamState { Seed = RngStreams.SeedToHex(7), Counter = 0 };
            for (ulong k = 0; k < 100; k++)
            {
                Assert.That(RngStreams.PeekUnit(s), Is.EqualTo(SplitMix64.ToUnit(SplitMix64.Derive(7, k))));
                Assert.That(RngStreams.NextUnit(s), Is.EqualTo(SplitMix64.ToUnit(SplitMix64.Derive(7, k))));
            }
            Assert.That(s.Counter, Is.EqualTo(100));
        }

        [Test]
        public void SeedHex_RoundTrips_AndRejectsBadForms()
        {
            Assert.That(RngStreams.SeedToHex(0x123456789ABCDEF0UL), Is.EqualTo("123456789abcdef0"));
            Assert.That(RngStreams.TryParseSeed("123456789abcdef0", out ulong v), Is.True);
            Assert.That(v, Is.EqualTo(0x123456789ABCDEF0UL));
            Assert.That(RngStreams.TryParseSeed("123", out _), Is.False);
            Assert.That(RngStreams.TryParseSeed("123456789abcdefg", out _), Is.False);
            Assert.That(RngStreams.TryParseSeed(null, out _), Is.False);
        }

        [Test]
        public void EightStreams_InSpecOrder()
        {
            Assert.That(RngStreams.All, Is.EqualTo(new[]
            {
                "combat", "crit", "fish_spawn", "dungeon", "loot", "summon_weapon", "summon_skill", "summon_companion",
            }));
        }
    }
}
