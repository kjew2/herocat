using System;
using System.Globalization;

namespace HeroCat.Sim.Format
{
    /// <summary>
    /// Short display form (specs/03 section 4): 3 significant digits plus a suffix.
    /// K, M, B, T, then aa, ab, ... az, ba, ... for each further factor of 1000.
    /// </summary>
    public static class BigNumber
    {
        static readonly string[] Named = { "", "K", "M", "B", "T" };

        public static string Suffix(int group)
        {
            if (group < Named.Length) return Named[group];
            int k = group - Named.Length;               // 0 -> "aa"
            char first = (char)('a' + (k / 26) % 26);
            char second = (char)('a' + k % 26);
            return new string(new[] { first, second });
        }

        public static string Short(double v)
        {
            if (double.IsNaN(v)) return "NaN";
            if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
            if (v < 0) return "-" + Short(-v);
            if (v < 1000) return Math.Floor(v).ToString("0", CultureInfo.InvariantCulture);

            int group = 0;
            double scaled = v;
            while (scaled >= 1000) { scaled /= 1000; group++; }

            int decimals = scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2;
            double rounded = Math.Round(scaled, decimals, MidpointRounding.AwayFromZero);
            if (rounded >= 1000)
            {
                group++;
                rounded = Math.Round(rounded / 1000, 2, MidpointRounding.AwayFromZero);
                decimals = 2;
            }
            return rounded.ToString("F" + decimals, CultureInfo.InvariantCulture) + Suffix(group);
        }
    }
}
