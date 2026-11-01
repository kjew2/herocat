using System;
using System.Globalization;
using HeroCat.Sim.Content;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// Every number a screen shows goes through here (07 section 7; issue #112). Magnitudes use <see cref="BigNumber.Short"/>;
    /// integer counts use <see cref="IntText"/>. No screen formats a game number any other way. The suffix-ladder letters
    /// are not localised (07 section 12); grouping follows the culture passed in (invariant by default).
    /// </summary>
    public static class TextFmt
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The culture whose grouping separator <see cref="IntText"/> uses. Set from ClientPrefs.Locale.</summary>
        public static CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

        public static string Num(double v) => BigNumber.Short(v);

        /// <summary>
        /// Counts (diamonds, tickets, copies, pity): in full with grouping below 1e6 (<c>1,240</c>, <c>999,999</c>), the ladder
        /// from 1e6 (00 contract 9).
        /// </summary>
        public static string IntText(long n)
        {
            long a = n < 0 ? -n : n;
            if (a < 1_000_000) return n.ToString("#,0", Culture);
            return BigNumber.Short(n);
        }

        /// <summary>A signed percentage from percentage points: <c>+48%</c>, <c>+5.0%</c> below 10%, <c>+1.64K%</c> from 1000%.</summary>
        public static string PctPoints(double points)
        {
            if (double.IsNaN(points) || double.IsInfinity(points)) return "?";
            string sign = points < 0 ? "-" : "+";
            double a = Math.Abs(points);
            if (a >= 999.5) return sign + BigNumber.Short(a) + "%";
            if (a < 9.95) return sign + a.ToString("0.0", Inv) + "%";
            return sign + Math.Round(a, MidpointRounding.AwayFromZero).ToString("0", Inv) + "%";
        }

        /// <summary>A fraction as a signed percentage (0.05 -&gt; <c>+5.0%</c>).</summary>
        public static string PctSigned(double fraction) => PctPoints(fraction * 100.0);

        /// <summary>An unsigned percentage from percentage points (crit rate <c>12.5%</c>), same ladder rules.</summary>
        public static string PctPlain(double points)
        {
            string s = PctPoints(points);
            return s == "?" ? s : s.Substring(1);
        }

        /// <summary>Chances: integer percent, floored, so 99.6% never reads as 100% (07 section 7).</summary>
        public static string Chance(double p)
        {
            if (double.IsNaN(p) || double.IsInfinity(p)) return "?";
            return Math.Floor(p * 100.0 + 1e-9).ToString("0", Inv) + "%";
        }

        /// <summary>Multipliers: <c>x2.25</c>; the ladder from 1000 (<c>x1.50K</c>); never scientific notation.</summary>
        public static string Mult(double m)
        {
            if (double.IsNaN(m) || double.IsInfinity(m)) return "x?";
            if (Math.Abs(m) >= 999.995) return "x" + BigNumber.Short(m);
            return "x" + m.ToString("0.00", Inv);
        }

        /// <summary>A ratio like the boss button's "need ~2.3x": one decimal, ladder from 1000.</summary>
        public static string Ratio(double r)
        {
            if (double.IsNaN(r) || double.IsInfinity(r)) return "?";
            if (r >= 999.95) return BigNumber.Short(r);
            return r.ToString("0.0", Inv);
        }

        /// <summary><c>Duration(s)</c>: the two largest units, <c>12h 04m</c>, <c>45m 10s</c>, <c>12s</c>.</summary>
        public static string Duration(long s)
        {
            if (s < 0) s = 0;
            long h = s / 3600, m = (s % 3600) / 60, sec = s % 60;
            if (h > 0) return h.ToString(Inv) + "h " + m.ToString("00", Inv) + "m";
            if (m > 0) return m.ToString(Inv) + "m " + sec.ToString("00", Inv) + "s";
            return sec.ToString(Inv) + "s";
        }

        /// <summary><c>Countdown(s)</c>: <c>6h 12m</c> from an hour, <c>12:34</c> below, <c>0:04</c> below a minute.</summary>
        public static string Countdown(long s)
        {
            if (s < 0) s = 0;
            if (s >= 3600) return (s / 3600).ToString(Inv) + "h " + ((s % 3600) / 60).ToString("00", Inv) + "m";
            return (s / 60).ToString(Inv) + ":" + (s % 60).ToString("00", Inv);
        }

        /// <summary>Countdown from fractional seconds, rounded up so "0:00" only shows at zero.</summary>
        public static string Countdown(double s) => Countdown((long)Math.Ceiling(s < 0 ? 0 : s));

        /// <summary>The boss timer: <c>17.3 s</c>.</summary>
        public static string BossTimer(double seconds) => (seconds < 0 ? 0 : seconds).ToString("0.0", Inv) + " s";

        /// <summary>Seconds with one decimal (skill cooldowns): <c>8.0s</c>.</summary>
        public static string Seconds(double s) => (s < 0 ? 0 : s).ToString("0.0", Inv) + "s";

        /// <summary>The rarity's localization key (<c>rarity.epic.name</c>, 09 rows).</summary>
        public static string RarityKey(Rarity r) => "rarity." + r.ToString().ToLowerInvariant() + ".name";

        /// <summary>The one-letter code (C/R/E/L/M, 00 section 5.1); not a localized word.</summary>
        public static string RarityCode(Rarity r) => r switch
        {
            Rarity.COMMON => "C",
            Rarity.RARE => "R",
            Rarity.EPIC => "E",
            Rarity.LEGENDARY => "L",
            Rarity.MYTHIC => "M",
            _ => "?",
        };
    }

    /// <summary>The rarity shape glyph (07 section 8) so colour is never the only signal (section 11).</summary>
    public enum RarityShape { Circle, Square, Diamond, Star, Crown }

    /// <summary>Palette tokens (07 section 8) as RGB hex, shared by screens. No UnityEngine here.</summary>
    public static class Palette
    {
        public const uint Background = 0x121212, Panel = 0x1E1E1E, PanelRaised = 0x262626, Text = 0xEEEEEE, Muted = 0x9E9E9E,
                          Accent = 0x4DD0E1, Gold = 0xFFD54F, Diamond = 0x4DD0E1, FishCoin = 0x26C6DA, RockCandy = 0xF06292,
                          Ok = 0x66BB6A, Bad = 0xEF5350, Scrim = 0x000000, BarBack = 0x424242;

        /// <summary>Scrim opacity (60%) and the disabled opacity (40%, never a colour change alone).</summary>
        public const float ScrimAlpha = 0.6f, DisabledAlpha = 0.4f, UnownedAlpha = 0.3f;

        public static uint RarityColor(Rarity r) => r switch
        {
            Rarity.COMMON => 0x9E9E9E,
            Rarity.RARE => 0x42A5F5,
            Rarity.EPIC => 0xAB47BC,
            Rarity.LEGENDARY => 0xFFA726,
            Rarity.MYTHIC => 0xEF5350,
            _ => 0x9E9E9E,
        };

        public static RarityShape Shape(Rarity r) => r switch
        {
            Rarity.COMMON => RarityShape.Circle,
            Rarity.RARE => RarityShape.Square,
            Rarity.EPIC => RarityShape.Diamond,
            Rarity.LEGENDARY => RarityShape.Star,
            _ => RarityShape.Crown,
        };

        /// <summary>A text glyph for the shape, for placeholder art (07 section 15.1): ● ■ ◆ ★ ♛.</summary>
        public static string ShapeGlyph(Rarity r) => Shape(r) switch
        {
            RarityShape.Circle => "●",
            RarityShape.Square => "■",
            RarityShape.Diamond => "◆",
            RarityShape.Star => "★",
            _ => "♛",
        };

        /// <summary>Black text on C/R/L, white on E/M (section 8).</summary>
        public static bool DarkTextOn(Rarity r) => r == Rarity.COMMON || r == Rarity.RARE || r == Rarity.LEGENDARY;

        /// <summary>Fish placeholder hue: 30 degrees per level (07 section 15.1), in [0, 360).</summary>
        public static float FishHue(int level) => ((level - 1) * 30 % 360 + 360) % 360;

        /// <summary>Enemy placeholder hue: <c>(chapter * 23) mod 360</c> (07 section 15.1).</summary>
        public static float EnemyHue(int chapter) => (chapter * 23 % 360 + 360) % 360;
    }
}
