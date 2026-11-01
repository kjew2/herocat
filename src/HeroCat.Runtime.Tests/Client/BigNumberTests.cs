using System;
using System.Globalization;
using HeroCat.Client.Core;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>07 section 7 and AC 7, 00 contract 9 and decision 54 (issue #112).</summary>
    public class BigNumberTests
    {
        // 07 section 7's vector table, then the edges of rule 1-3.
        [TestCase(999.4, "999")]
        [TestCase(999.5, "1.00K")]
        [TestCase(12345.0, "12.3K")]
        [TestCase(1.7e5, "170K")]
        [TestCase(4.56e7, "45.6M")]
        [TestCase(2.9e18, "2.90ab")]
        [TestCase(0.0, "0")]
        [TestCase(2.0, "2")]
        [TestCase(1234.0, "1.23K")]
        [TestCase(1.23e17, "123aa")]
        [TestCase(1e12, "1.00T")]
        [TestCase(999999.0, "1.00M")]
        [TestCase(99950.0, "100K")]
        [TestCase(9995.0, "10.0K")]
        [TestCase(1e15, "1.00aa")]
        [TestCase(1e90, "1.00az")]
        [TestCase(1e93, "1.00ba")]
        [TestCase(-12345.0, "-12.3K")]
        [TestCase(-0.2, "0")]
        public void Short_Vectors(double v, string expected) => Assert.That(BigNumber.Short(v), Is.EqualTo(expected));

        [Test]
        public void Short_DoubleMax_Is180dt() => Assert.That(BigNumber.Short(double.MaxValue), Is.EqualTo("180dt"));

        [Test]
        public void Short_NaNAndInfinity_AreQuestionMarks()
        {
            Assert.That(BigNumber.Short(double.NaN), Is.EqualTo("?"));
            Assert.That(BigNumber.Short(double.PositiveInfinity), Is.EqualTo("?"));
            Assert.That(BigNumber.Short(double.NegativeInfinity), Is.EqualTo("?"));
        }

        [Test]
        public void Short_SuffixBoundaries_KThroughAb()
        {
            string[] suffix = { "K", "M", "B", "T", "aa", "ab" };
            double p = 1000;
            for (int i = 0; i < suffix.Length; i++, p *= 1000)
            {
                Assert.That(BigNumber.Short(p), Is.EqualTo("1.00" + suffix[i]), "at 1e" + (3 * (i + 1)));
                Assert.That(BigNumber.Short(p * 0.9994), Is.EqualTo(i == 0 ? "999" : "999" + suffix[i - 1]), "just below 1e" + (3 * (i + 1)));
                Assert.That(BigNumber.Short(p * 0.9995), Is.EqualTo("1.00" + suffix[i]), "the carry at 1e" + (3 * (i + 1)));
            }
        }

        static double Parse(string s)
        {
            if (s == "?") return double.NaN;
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-')) i++;
            double m = double.Parse(s.Substring(0, i), CultureInfo.InvariantCulture);
            string suf = s.Substring(i);
            int k = suf switch { "" => 0, "K" => 1, "M" => 2, "B" => 3, "T" => 4, _ => 5 + (suf[0] - 'a') * 26 + (suf[1] - 'a') };
            for (int j = 0; j < k; j++) m *= 1000;
            return m;
        }

        [Test]
        public void Short_IsMonotonic_AndWithinHalfAUnitOfTheThirdDigit()
        {
            double prev = -1;
            for (double v = 100; v < 1e30; v *= 1.0137)
            {
                double shown = Parse(BigNumber.Short(v));
                Assert.That(shown, Is.GreaterThanOrEqualTo(prev), "monotonic at " + v);
                Assert.That(Math.Abs(shown - v) / v, Is.LessThanOrEqualTo(0.0051), "3 significant digits at " + v);
                prev = shown;
            }
        }

        [Test]
        public void Short_AllocatesOnlyTheReturnedString()
        {
            BigNumber.Short(12345);   // warm up
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) BigNumber.Short(12345.0 + i);
            long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
            Assert.That(perCall, Is.LessThanOrEqualTo(40), "one 5-char string is 32 bytes");
        }

        [Test]
        public void IntText_GroupsBelowAMillion_LadderFromAMillion()
        {
            Assert.That(TextFmt.IntText(1240), Is.EqualTo("1,240"));
            Assert.That(TextFmt.IntText(999_999), Is.EqualTo("999,999"));
            Assert.That(TextFmt.IntText(1_000_000), Is.EqualTo("1.00M"));
            Assert.That(TextFmt.IntText(0), Is.EqualTo("0"));
        }

        [Test]
        public void Times_Countdown_Duration_BossTimer()
        {
            Assert.That(TextFmt.Countdown(4L), Is.EqualTo("0:04"));
            Assert.That(TextFmt.Countdown(754L), Is.EqualTo("12:34"));
            Assert.That(TextFmt.Countdown(22320L), Is.EqualTo("6h 12m"));
            Assert.That(TextFmt.Duration(43440), Is.EqualTo("12h 04m"));
            Assert.That(TextFmt.Duration(2710), Is.EqualTo("45m 10s"));
            Assert.That(TextFmt.Duration(12), Is.EqualTo("12s"));
            Assert.That(TextFmt.BossTimer(17.3), Is.EqualTo("17.3 s"));
        }

        [Test]
        public void Percent_Chance_Multiplier()
        {
            Assert.That(TextFmt.PctPoints(48), Is.EqualTo("+48%"));
            Assert.That(TextFmt.PctPoints(5), Is.EqualTo("+5.0%"));
            Assert.That(TextFmt.PctPoints(1640), Is.EqualTo("+1.64K%"));
            Assert.That(TextFmt.Chance(0.996), Is.EqualTo("99%"), "floored so it never reads as certain");
            Assert.That(TextFmt.Mult(2.25), Is.EqualTo("x2.25"));
            Assert.That(TextFmt.Mult(1500), Is.EqualTo("x1.50K"));
            Assert.That(TextFmt.Mult(double.NaN), Is.EqualTo("x?"));
        }
    }
}
