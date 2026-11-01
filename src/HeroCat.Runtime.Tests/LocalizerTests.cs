using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>09 section 13.4 (issue #129): fallback chain, the ICU subset, culture invariance, loading and swaps.</summary>
    public class LocalizerTests
    {
        static Dictionary<string, string> D(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        static readonly Dictionary<string, string> En = D(
            "ui.hello", "Hello",
            "ui.only_en", "English only",
            "ui.fish", "{n, plural, one {# fish} other {# fishes}}",
            "ui.gold", "{amount, number} gold",
            "ui.tank", "{size, select, big {a big tank} other {a tank}}",
            "ui.exact", "{n, plural, =0 {no fish} one {one fish} other {# fish}}");

        static readonly Dictionary<string, string> Ja = D(
            "ui.hello", "こんにちは",
            "ui.fish", "{n, plural, other {魚#匹}}");

        [Test]
        public void Text_ActiveThenEnThenBracketedKey()
        {
            var l = new Localizer(En, "ja", Ja);
            Assert.That(l.Text("ui.hello"), Is.EqualTo("こんにちは"), "active hit");
            Assert.That(l.Text("ui.only_en"), Is.EqualTo("English only"), "en fallback");
            Assert.That(l.Text("skill.e_03.name"), Is.EqualTo("[skill.e_03.name]"), "total miss");
            Assert.That(l.Has("skill.e_03.name"), Is.False);
        }

        [Test]
        public void Plural_EnUsesOneAndOther_JaUsesOtherOnly()
        {
            var en = new Localizer(En);
            Assert.That(en.Format("ui.fish", ("n", 1)), Is.EqualTo("1 fish"));
            Assert.That(en.Format("ui.fish", ("n", 3)), Is.EqualTo("3 fishes"));
            Assert.That(en.Format("ui.exact", ("n", 0)), Is.EqualTo("no fish"), "=N wins over the category");
            var ja = new Localizer(En, "ja", Ja);
            Assert.That(ja.Format("ui.fish", ("n", 1)), Is.EqualTo("魚1匹"));
            Assert.That(ja.Format("ui.fish", ("n", 5)), Is.EqualTo("魚5匹"));
            Assert.That(Localizer.PluralCategory("ko", 1), Is.EqualTo("other"));
            Assert.That(Localizer.PluralCategory("zh-Hans", 1), Is.EqualTo("other"));
        }

        [Test]
        public void Select_PicksTheBranchOrOther()
        {
            var l = new Localizer(En);
            Assert.That(l.Format("ui.tank", ("size", "big")), Is.EqualTo("a big tank"));
            Assert.That(l.Format("ui.tank", ("size", "small")), Is.EqualTo("a tank"));
        }

        [Test]
        public void Numbers_AreCultureInvariant()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var l = new Localizer(En);
                Assert.That(l.Format("ui.gold", ("amount", 1234.5)), Is.EqualTo("1,234.5 gold"));
                Assert.That(l.Format("ui.fish", ("n", 1500)), Is.EqualTo("1,500 fishes"));
                l.NumberFormatter = v => "#" + v.ToString(CultureInfo.InvariantCulture);
                Assert.That(l.Format("ui.gold", ("amount", 7)), Is.EqualTo("#7 gold"), "the client's formatter plugs in");
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }

        [Test]
        public void Format_MissingArgsAndMalformedMessages_DoNotThrow()
        {
            var l = new Localizer(D("a", "Hi {name}!", "b", "{n, plural, one {x}", "c", "it''s '{literal}'"));
            Assert.That(l.Format("a"), Is.EqualTo("Hi {name}!"));
            Assert.That(l.Format("a", ("name", "Kit")), Is.EqualTo("Hi Kit!"));
            Assert.That(l.Format("b", ("n", 1)), Is.EqualTo("{n, plural, one {x}"));
            Assert.That(l.Format("c"), Is.EqualTo("it's {literal}"));
            Assert.That(l.Format("nope", ("n", 1)), Is.EqualTo("[nope]"));
        }

        [Test]
        public void Load_ReadsTheShippedEnglish_AndFallsBackForALocaleWithNoFile()
        {
            var src = new FileContentSource(TestContent.Dir);
            var en = Localizer.Load(src);
            Assert.That(en.Text("err.content_corrupt.title"), Does.Not.StartWith("["), "an err.* key from 01 AC 14");
            var ja = Localizer.Load(src, "ja");
            Assert.That(ja.Locale, Is.EqualTo("ja"));
            Assert.That(ja.Text("err.content_corrupt.title"), Is.EqualTo(en.Text("err.content_corrupt.title")), "no ja file yet: en");
        }

        [Test]
        public void SetLocaleAndReplace_RaiseLocaleChanged()
        {
            var src = MemoryContentSource.CopyOf(new FileContentSource(TestContent.Dir));
            var l = Localizer.Load(src);
            var seen = new List<string>();
            l.LocaleChanged += seen.Add;
            l.SetLocale(src, "ko");
            l.Replace(src);
            Assert.That(seen, Is.EqualTo(new[] { "ko", "ko" }));
        }

        [Test]
        public void TamperedStrings_AreE900()
        {
            var src = MemoryContentSource.CopyOf(new FileContentSource(TestContent.Dir));
            var bytes = src.Files["localization.en.json"];
            bytes[bytes.Length - 3] ^= 1;
            var e = Assert.Throws<ContentBuildException>(() => Localizer.Load(src));
            Assert.That(e.Errors.Single().Code, Is.EqualTo("E900"));
            Assert.That(e.Errors.Single().Location, Is.EqualTo("localization.en.json"));
        }
    }
}
