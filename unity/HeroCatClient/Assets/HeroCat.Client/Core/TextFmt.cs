using System;
using System.Globalization;
using HeroCat.Sim.Content;
using HeroCat.Sim.Format;

namespace HeroCat.Client.Core
{
    /// <summary>Presentation-only text helpers (specs/07 section 9.2). Magnitudes always go through BigNumber.Short.</summary>
    public static class TextFmt
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Num(double v) => BigNumber.Short(v);

        public static string Int(long n) => n.ToString(Inv);

        /// <summary>0.05 -> "+5.0%", 8.18 -> "+818%", 16.4 -> "+1.64K%".</summary>
        public static string PctSigned(double fraction)
        {
            double pct = fraction * 100.0;
            string sign = pct < 0 ? "-" : "+";
            double a = Math.Abs(pct);
            if (a >= 1000) return sign + BigNumber.Short(a) + "%";
            if (a < 10) return sign + a.ToString("0.0", Inv) + "%";
            return sign + Math.Round(a).ToString("0", Inv) + "%";
        }

        /// <summary>Hero percentage points (40 = +40%).</summary>
        public static string PctPoints(double points) => PctSigned(points / 100.0);

        /// <summary>Integer percent, floored so 99.6% never reads as certain.</summary>
        public static string Chance(double p) => Math.Floor(p * 100.0 + 1e-9).ToString("0", Inv) + "%";

        public static string Seconds(double s) => s.ToString("0.0", Inv) + " s";

        public static string Duration(long s)
        {
            if (s < 0) s = 0;
            long h = s / 3600, m = (s % 3600) / 60, sec = s % 60;
            if (h > 0) return h.ToString(Inv) + "h " + m.ToString("00", Inv) + "m";
            if (m > 0) return m.ToString(Inv) + "m " + sec.ToString("00", Inv) + "s";
            return sec.ToString(Inv) + "s";
        }

        public static string Mult(double m) => m < 1000 ? "x" + m.ToString("0.##", Inv) : "x" + BigNumber.Short(m);

        public static string Ratio(double r) => r.ToString("0.0", Inv);

        public static string Tier(Rarity r) => r switch
        {
            Rarity.C => "Common",
            Rarity.R => "Rare",
            Rarity.E => "Epic",
            Rarity.L => "Legendary",
            Rarity.M => "Mythic",
            _ => r.ToString(),
        };
    }

    /// <summary>Palette (specs/07 section 10.1) as RGB hex, shared by screens. No UnityEngine here.</summary>
    public static class Palette
    {
        public const uint Background = 0x121212, Panel = 0x1E1E1E, Text = 0xEEEEEE, Muted = 0x9E9E9E;
        public const uint Gold = 0xFFD54F, Gems = 0x4DD0E1, Ok = 0x66BB6A, Bad = 0xEF5350, BarBack = 0x424242;

        public static uint RarityColor(Rarity r) => r switch
        {
            Rarity.C => 0x9E9E9E,
            Rarity.R => 0x42A5F5,
            Rarity.E => 0xAB47BC,
            Rarity.L => 0xFFA726,
            Rarity.M => 0xEF5350,
            _ => 0x9E9E9E,
        };

        static readonly uint[] ClassColors = { 0xBDBDBD, 0x81C784, 0x64B5F6, 0xBA68C8, 0xFFB74D };

        public static uint ClassColor(int advances) => ClassColors[Math.Max(0, Math.Min(advances, ClassColors.Length - 1))];

        /// <summary>White text on E/M, black on C/R/L (section 8.2).</summary>
        public static bool DarkTextOn(Rarity r) => r == Rarity.C || r == Rarity.R || r == Rarity.L;
    }
}
