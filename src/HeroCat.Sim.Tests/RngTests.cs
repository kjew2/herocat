using HeroCat.Sim.Rng;
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

        [TestCase("combat", 0xCA0BF97CCE717B21UL, 0xD638BA460341F349UL)]
        [TestCase("gear_drop", 0xC1A5A0EE94A0A3BAUL, 0xFB1D7F3436BC41A4UL)]
        [TestCase("gear_rarity", 0x6AB86776709CD868UL, 0x9A1426458CA81741UL)]
        [TestCase("enhance", 0xDC465426329C1EE9UL, 0x84C074F86395F937UL)]
        [TestCase("gacha_hero", 0x8C00A1F67FD04D96UL, 0x9BB72FE2750126ACUL)]
        public void Fnv_And_StreamSeed_MatchSpecVectors(string name, ulong fnv, ulong streamSeed)
        {
            Assert.That(Fnv1a64.Hash(name), Is.EqualTo(fnv));
            Assert.That(StreamNames.StreamSeed(7, name), Is.EqualTo(streamSeed));
        }

        [Test]
        public void Stream_CounterAndPeek()
        {
            var s = new RngStream("x", 7);
            for (ulong j = 0; j < 100; j++)
                Assert.That(s.NextDouble(), Is.EqualTo(s.PeekDouble(j)));
            Assert.That(s.Counter, Is.EqualTo(100UL));
        }

        [Test]
        public void NextIndex_CoversRange()
        {
            var s = new RngStream("x", 7);
            foreach (int n in new[] { 3, 6, 20 })
            {
                var seen = new bool[n];
                for (int i = 0; i < 100000; i++)
                {
                    int v = s.NextIndex(n);
                    Assert.That(v, Is.InRange(0, n - 1));
                    seen[v] = true;
                }
                Assert.That(seen, Is.All.True);
            }
        }
    }
}
