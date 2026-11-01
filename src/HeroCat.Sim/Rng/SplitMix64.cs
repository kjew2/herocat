using System;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Rng
{
    /// <summary>SplitMix64 as a stateless derive (02 section 3.4; same generator as the sibling project).</summary>
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

    /// <summary>
    /// The eight persisted streams of 02 section 3.4 (00 contract 2). Every save holds all of them in every mode
    /// (00 decision 14). Seeds are derived once in Runtime's ProfileFactory from RngRoot; the sim stays hash-free.
    /// </summary>
    public static class RngStreams
    {
        public const string Combat = "combat";                    // reserved: Counter == 0 in MVP
        public const string Crit = "crit";                        // one draw per stage-fight hero hit
        public const string FishSpawn = "fish_spawn";             // one draw per successful release
        public const string Dungeon = "dungeon";                  // M0 dungeon seed derivation
        public const string Loot = "loot";                        // one draw per boss kill
        public const string SummonWeapon = "summon_weapon";       // three draws per pull, LocalSummonService only
        public const string SummonSkill = "summon_skill";
        public const string SummonCompanion = "summon_companion";

        /// <summary>In 02's order. `dungeon_run` is temporary (Combat.DungeonRun) and never in this list.</summary>
        public static readonly string[] All = { Combat, Crit, FishSpawn, Dungeon, Loot, SummonWeapon, SummonSkill, SummonCompanion };

        /// <summary>16 lowercase hex chars, the persisted seed form.</summary>
        public static string SeedToHex(ulong seed) => seed.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Parses a persisted seed. False unless exactly 16 hex chars (02 R4).</summary>
        public static bool TryParseSeed(string hex, out ulong seed)
        {
            seed = 0;
            if (hex == null || hex.Length != 16) return false;
            foreach (char ch in hex)
            {
                int v = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : -1;
                if (v < 0) { seed = 0; return false; }
                seed = (seed << 4) | (uint)v;
            }
            return true;
        }

        /// <summary>The next uniform draw of a persisted stream; advances its counter.</summary>
        public static double NextUnit(RngStreamState s)
        {
            if (!TryParseSeed(s.Seed, out ulong seed)) throw new InvalidOperationException("stream seed is not 16 hex chars");
            ulong k = (ulong)s.Counter;
            s.Counter++;
            return SplitMix64.ToUnit(SplitMix64.Derive(seed, k));
        }

        /// <summary>What the next draw would return, without consuming it (LocalDungeonSeedService peeks, 01 section 8.8).</summary>
        public static double PeekUnit(RngStreamState s)
        {
            if (!TryParseSeed(s.Seed, out ulong seed)) throw new InvalidOperationException("stream seed is not 16 hex chars");
            return SplitMix64.ToUnit(SplitMix64.Derive(seed, (ulong)s.Counter));
        }
    }
}
