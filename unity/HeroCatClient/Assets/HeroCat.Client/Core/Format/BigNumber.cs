using System;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The suffix-ladder formatter (07 section 7, 00 contract 9 and decision 54; issue #112). Rules:
    /// <list type="number">
    /// <item>Round to three significant digits first, then choose the suffix: a rounded value below 1000 prints as an
    /// integer with no suffix (<c>999.4 -> 999</c>), and <c>999.5 -> 1.00K</c>. Negative values print the magnitude with a
    /// leading <c>-</c>. NaN and infinity print <c>?</c> (a bug upstream, never a crash here).</item>
    /// <item><c>k = floor(log1000 v)</c> by repeated division (no <c>Math.Log</c>); the mantissa keeps three significant
    /// digits (<c>1.23</c>, <c>12.3</c>, <c>123</c>); a carry to 1000 bumps <c>k</c>.</item>
    /// <item>Suffixes K, M, B, T, then two lowercase letters: aa = 1e15, ab = 1e18, ... az, ba, ...; double.MaxValue is
    /// <c>180dt</c>.</item>
    /// <item>Culture-invariant <c>.</c>, no grouping, and no allocation beyond the returned string.</item>
    /// </list>
    /// 07 section 7 names this <c>HeroCat.Sim.Format.BigNumber</c>; the sim has no formatter, so the client's Core holds the
    /// single implementation (bots and the server format nothing for players).
    /// </summary>
    public static class BigNumber
    {
        const string Unknown = "?";
        const int MaxExactGroup = 7;   // 1000^7 = 1e21 is exact in a double
        static readonly char[] Named = { '\0', 'K', 'M', 'B', 'T' };

        /// <summary>The ladder suffix for group <paramref name="k"/> (0 = none, 1 = K, ... 5 = aa).</summary>
        public static string Suffix(int k)
        {
            if (k <= 0) return "";
            if (k < Named.Length) return Named[k].ToString();
            Span<char> s = stackalloc char[2];
            WriteSuffix(s, k);
            return new string(s);
        }

        /// <summary>Writes the suffix of group k (k &gt;= 1) into <paramref name="dst"/>; returns the chars written.</summary>
        static int WriteSuffix(Span<char> dst, int k)
        {
            if (k < Named.Length) { dst[0] = Named[k]; return 1; }
            int n = k - Named.Length;   // 0 -> "aa"
            dst[0] = (char)('a' + (n / 26) % 26);
            dst[1] = (char)('a' + n % 26);
            return 2;
        }

        public static string Short(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return Unknown;
            Span<char> buf = stackalloc char[24];
            int len = 0;
            if (v < 0)
            {
                buf[len++] = '-';
                v = -v;
            }

            if (v < 999.5)
            {
                long n = (long)Math.Round(v, MidpointRounding.AwayFromZero);
                len += WriteInt(buf.Slice(len), n);
                if (len == 2 && buf[0] == '-' && buf[1] == '0') len = 0;   // -0.2 -> "0"
                if (len == 0) buf[len++] = '0';
                return new string(buf.Slice(0, len));
            }

            int k = 0;
            double scaled = v;
            while (scaled >= 1000.0)
            {
                scaled /= 1000.0;
                k++;
            }
            int decimals = scaled >= 100.0 ? 0 : scaled >= 10.0 ? 1 : 2;
            // Three significant digits. Divide the input once by 1000^k / 10^d (exact while it fits a double's 2^53) so a
            // decimal midpoint like 9995 rounds up; past 1e21 the scaled mantissa is as good as it gets.
            double units0;
            if (k <= MaxExactGroup)
            {
                double div = 1.0;
                for (int i = 0; i < k; i++) div *= 1000.0;
                units0 = v / (div / Pow10(decimals));
            }
            else units0 = scaled * Pow10(decimals);
            long units = (long)Math.Round(units0, MidpointRounding.AwayFromZero);
            if (units >= 1000)
            {
                // The carry: 999.5 -> 1.00K, 99.95K -> 100K, 9.995M -> 10.0M.
                units = 100;
                if (decimals == 0) { k++; decimals = 2; }
                else decimals--;
            }
            len += WriteFixed(buf.Slice(len), units, decimals);
            if (k > 0) len += WriteSuffix(buf.Slice(len), k);
            return new string(buf.Slice(0, len));
        }

        static double Pow10(int d) => d == 0 ? 1.0 : d == 1 ? 10.0 : 100.0;

        /// <summary>Non-negative integer, invariant digits.</summary>
        static int WriteInt(Span<char> dst, long n)
        {
            if (n == 0) { dst[0] = '0'; return 1; }
            int count = 0;
            for (long t = n; t > 0; t /= 10) count++;
            for (int i = count - 1; i >= 0; i--)
            {
                dst[i] = (char)('0' + (int)(n % 10));
                n /= 10;
            }
            return count;
        }

        /// <summary><paramref name="units"/> (100..999) with a decimal point <paramref name="decimals"/> digits from the right.</summary>
        static int WriteFixed(Span<char> dst, long units, int decimals)
        {
            Span<char> digits = stackalloc char[20];
            int n = WriteInt(digits, units);
            int len = 0;
            int intDigits = n - decimals;
            for (int i = 0; i < n; i++)
            {
                if (i == intDigits) dst[len++] = '.';
                dst[len++] = digits[i];
            }
            return len;
        }
    }
}
