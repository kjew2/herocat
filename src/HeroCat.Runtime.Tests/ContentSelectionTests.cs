using System.Collections.Generic;
using System.Linq;
using System.Text;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>09 section 10.3 steps 2-8 and acceptance 16 (bundled half; issue #127).</summary>
    public class ContentSelectionTests
    {
        static MemoryContentSource Bundle() => MemoryContentSource.CopyOf(new FileContentSource(TestContent.Dir));

        static string Version(IContentSource s) => ContentSelection.VersionOf(s);

        /// <summary>A copy of the bundle whose manifest says <paramref name="version"/> (and optionally a higher client floor).</summary>
        static MemoryContentSource Cached(string version, string minClient = null)
        {
            var src = Bundle();
            string m = Encoding.UTF8.GetString(src.Files["manifest.json"]);
            m = m.Replace($"\"content_version\": \"{Version(src)}\"", $"\"content_version\": \"{version}\"");
            if (minClient != null) m = m.Replace("\"min_client_version\": \"0.1.0\"", $"\"min_client_version\": \"{minClient}\"");
            src.Files["manifest.json"] = Encoding.UTF8.GetBytes(m);
            return src;
        }

        [Test]
        public void ColdBoot_PrefersTheNewestCachedSetThatPassesEveryGate()
        {
            var bundled = Bundle();
            var older = Cached("2020.01.01");
            var newer = Cached("2099.01.01");
            var newest = Cached("2099.02.01");
            var choice = ContentSelection.LoadPreferred(bundled, new IContentSource[] { older, newest, newer }, Tier.M0, "0.1.0");
            Assert.That(choice.IsBundled, Is.False);
            Assert.That(choice.Db.Version, Is.EqualTo("2099.02.01"));
            Assert.That(choice.Rejected, Is.Empty);
        }

        [Test]
        public void ACachedSetForANewerClient_IsE903_AndTheBundleIsKept()   // acceptance 16
        {
            var logged = new List<ContentError>();
            var choice = ContentSelection.LoadPreferred(Bundle(), new IContentSource[] { Cached("2099.01.01", "9.0.0") }, Tier.M0, "0.1.0", logged.Add);
            Assert.That(choice.IsBundled, Is.True);
            Assert.That(choice.Db.Version, Is.EqualTo(Version(Bundle())));
            Assert.That(logged.Select(e => e.Code), Is.EqualTo(new[] { "E903" }));
            Assert.That(choice.Rejected.Single().Message, Is.EqualTo("content requires client >= 9.0.0, this client is 0.1.0"));
        }

        [Test]
        public void ATamperedCachedSet_IsE900_AndFallsBackToTheNextCandidate()
        {
            var bad = Cached("2099.02.01");
            bad.Files["skills.json"][40] ^= 1;
            var choice = ContentSelection.LoadPreferred(Bundle(), new IContentSource[] { bad, Cached("2099.01.01") }, Tier.M0, "0.1.0");
            Assert.That(choice.Db.Version, Is.EqualTo("2099.01.01"));
            Assert.That(choice.Rejected.Select(e => e.Code), Is.EqualTo(new[] { "E900" }));
        }

        [Test]
        public void TryNewer_KeepsTheSameInstance_UnlessTheCandidateIsNewerAndValid()   // acceptance 16, 01 acceptance 11
        {
            var current = ContentLoader.Load(Bundle(), Tier.M0, "0.1.0");
            var logged = new List<ContentError>();
            Assert.That(ContentSelection.TryNewer(Cached("2099.01.01", "9.0.0"), current, Tier.M0, "0.1.0", logged.Add), Is.SameAs(current));
            Assert.That(logged.Single().Code, Is.EqualTo("E903"));
            Assert.That(ContentSelection.TryNewer(Cached("2020.01.01"), current, Tier.M0, "0.1.0"), Is.SameAs(current), "not newer");
            var next = ContentSelection.TryNewer(Cached("2099.01.01"), current, Tier.M0, "0.1.0");
            Assert.That(next, Is.Not.SameAs(current));
            Assert.That(next.Version, Is.EqualTo("2099.01.01"));
        }
    }
}
