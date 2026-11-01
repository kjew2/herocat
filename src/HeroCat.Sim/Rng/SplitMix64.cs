using System.Text;

namespace HeroCat.Sim.Rng
{
    /// <summary>SplitMix64 as a stateless derive (specs/03 section 5.2).</summary>
    public static class SplitMix64
    {
        public const ulong Gamma = 0x9E3779B97F4A7C15UL;

        public static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>Output k (0-based) of the reference SplitMix64 generator seeded with <paramref name="seed"/>.</summary>
        public static ulong Derive(ulong seed, ulong k) => Mix(unchecked(seed + (k + 1) * Gamma));

        /// <summary>Uniform in [0, 1) with 53 bits of precision.</summary>
        public static double ToUnit(ulong x) => (x >> 11) * (1.0 / (1UL << 53));
    }

    public static class Fnv1a64
    {
        public static ulong Hash(string s)
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                h ^= b;
                h = unchecked(h * 0x100000001B3UL);
            }
            return h;
        }
    }

    public static class StreamNames
    {
        public const string Combat = "combat", GearDrop = "gear_drop", GearRarity = "gear_rarity",
                            Enhance = "enhance", GachaHero = "gacha_hero";

        public static readonly string[] All = { Combat, GearDrop, GearRarity, Enhance, GachaHero };

        /// <summary>Per-stream seed derived from the profile seed.</summary>
        public static ulong StreamSeed(ulong profileSeed, string name) =>
            SplitMix64.Mix(unchecked(profileSeed + Fnv1a64.Hash(name)));
    }

    /// <summary>Anything that hands out uniform doubles in [0,1). Lets tests script gacha/enhance outcomes.</summary>
    public interface IRandomSource
    {
        double NextDouble();
    }

    /// <summary>Persisted in GameState.Rng. Seed and Counter are the entire state.</summary>
    public sealed class RngStream : IRandomSource
    {
        public RngStream() { }

        public RngStream(string name, ulong seed, ulong counter = 0)
        {
            Name = name;
            Seed = seed;
            Counter = counter;
        }

        public string Name { get; set; }
        public ulong Seed { get; set; }
        public ulong Counter { get; set; }

        public double NextDouble() => SplitMix64.ToUnit(SplitMix64.Derive(Seed, Counter++));

        /// <summary>0..n-1, same arithmetic as specs/01 section 8.</summary>
        public int NextIndex(int n) => (int)(NextDouble() * n);

        /// <summary>Audit/tests only: the value draw <paramref name="counter"/> would return.</summary>
        public double PeekDouble(ulong counter) => SplitMix64.ToUnit(SplitMix64.Derive(Seed, counter));

        public RngStream Clone() => new RngStream(Name, Seed, Counter);
    }
}
