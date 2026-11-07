using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HeroCat.Client.Core;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Errors;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>00 contract 10: every key the M0 screens build exists in the English table (09's localization.en.json).</summary>
    public class ClientLocalizationTests
    {
        static Localizer English() => Localizer.FromJson(File.ReadAllText(Path.Combine(TestContent.Dir, "localization.en.json")));

        [Test]
        public void EveryUiKey_HasAnEnglishRow()
        {
            var en = English();
            var missing = new List<string>();
            void Need(string key)
            {
                if (!string.IsNullOrEmpty(key) && !en.Has(key)) missing.Add(key);
            }
            foreach (var f in typeof(UiKeys).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(string)) Need((string)f.GetValue(null));
            foreach (CastType t in Enum.GetValues(typeof(CastType))) Need(UiKeys.CastTypeKey(t));
            foreach (Banner b in Enum.GetValues(typeof(Banner))) Need(UiKeys.KindKey(b));
            foreach (ErrorAction a in Enum.GetValues(typeof(ErrorAction))) Need(UiKeys.ErrorActionKey(a));
            foreach (BootStep s in Enum.GetValues(typeof(BootStep))) Need(UiKeys.BootStepKey(s));
            foreach (CollectionSort s in Enum.GetValues(typeof(CollectionSort))) Need(UiKeys.SortKey(s));
            foreach (CollectionFilter f in Enum.GetValues(typeof(CollectionFilter))) Need(UiKeys.FilterKey(f));
            foreach (Rarity r in Enum.GetValues(typeof(Rarity))) Need(UiKeys.RarityName(r));
            foreach (QuestKind k in Enum.GetValues(typeof(QuestKind))) Need(UiKeys.QuestsTabKey(k));
            foreach (Currency c in Enum.GetValues(typeof(Currency))) Need(UiKeys.CurrencyKey(c));   // the Claim all summary toast (issue #183)
            foreach (var q in TestContent.Mvp.QuestsDaily.Concat(TestContent.Mvp.QuestsWeekly).Concat(TestContent.Mvp.Achievements)) Need(q.NameKey);
            // The error and reject keys the client builds itself (SummonVm's predicted rejects), through ErrorClassifier.
            Need(ErrorClassifier.RejectCodeKey(RejectCode.RateLimited));
            Need(ErrorClassifier.ErrTitleKey("NET_OFFLINE"));
            Need(ErrorClassifier.RejectReasonKey(CollectionRejectReasons.BANNER_CLOSED));
            Need(ErrorClassifier.RejectReasonKey(CollectionRejectReasons.NotEnough(Currency.DIAMOND)));
            foreach (var f in typeof(ShopRejectReasons).GetFields(BindingFlags.Public | BindingFlags.Static))   // issue #174
                Need(ErrorClassifier.RejectReasonKey((string)f.GetValue(null)));
            Assert.That(missing, Is.Empty);
        }

        [Test]
        public void Format_SubstitutesPositionalArgs_AndAMissingRowShowsTheKey()
        {
            var en = English();
            Assert.That(Localizer.Substitute("Need {0} more", new[] { "760" }), Is.EqualTo("Need 760 more"));
            Assert.That(new Localizer(new Dictionary<string, string> { ["k"] = "Lv {0} -> {1}" }).Format("k", "2", "3"), Is.EqualTo("Lv 2 -> 3"));
            Assert.That(en.Count, Is.GreaterThan(0), "the shipped English table parses");
            Assert.That(en.Format("scr.no_such_key", "1"), Is.EqualTo("scr.no_such_key 1"));
            Assert.That(Localizer.FromJson("{ not json").Count, Is.EqualTo(0));
        }
    }
}
