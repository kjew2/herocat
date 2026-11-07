using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The on-device content cache of 09 section 10.3 steps 6 and 8 (issue #212); the CDN half is in HeroCat.Remote.Tests.</summary>
    public class ContentCacheTests
    {
        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "herocat-cache-" + Guid.NewGuid().ToString("N"), "content");

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(Path.GetDirectoryName(_root), true); }
            catch (DirectoryNotFoundException) { }
        }

        /// <summary>Stages <paramref name="src"/> as <paramref name="version"/> and installs it, as CdnContentStaging does.</summary>
        static void Put(ContentCache cache, string version, MemoryContentSource src, bool install = true)
        {
            string tmp = cache.BeginStaging(version);
            foreach (var f in src.Files) File.WriteAllBytes(Path.Combine(tmp, f.Key), f.Value);
            if (install) cache.Install(version);
        }

        [Test]
        public void ColdBoot_SkipsAVersionThisClientCannotLoad_AndNeverPrunesTheOneInUse()   // step 8
        {
            var cache = new ContentCache(_root);
            Put(cache, "2099.01.01", Bundles.Make("2099.01.01"));
            Put(cache, "2099.01.02", Bundles.Make("2099.01.02", corrupt: true, touch: "skills"));

            var logged = new List<ContentError>();
            var booted = new ContentCache(_root);
            var choice = booted.LoadPreferred(Bundles.Shipped, Tier.M0, "1.0.0", logged.Add);
            Assert.That(choice.IsBundled, Is.False);
            Assert.That(choice.Db.Version, Is.EqualTo("2099.01.01"), "a corrupt newer copy is skipped");
            Assert.That(booted.InUse, Is.EqualTo("2099.01.01"));
            Assert.That(logged.Select(e => e.Code), Is.EqualTo(new[] { "E900" }));

            Put(booted, "2099.01.03", Bundles.Make("2099.01.03", minClient: "9.0.0"));   // installed while running 2099.01.01
            Assert.That(booted.Versions(), Is.EqualTo(new[] { "2099.01.03", "2099.01.02", "2099.01.01" }), "two newest plus the one in use");

            logged.Clear();
            booted = new ContentCache(_root);
            Assert.That(booted.LoadPreferred(Bundles.Shipped, Tier.M0, "1.0.0", logged.Add).Db.Version, Is.EqualTo("2099.01.01"),
                        "the newest set this client loads cleanly");
            Assert.That(logged.Select(e => e.Code), Is.EqualTo(new[] { "E903", "E900" }));
            Assert.That(booted.Versions(), Is.EqualTo(new[] { "2099.01.03", "2099.01.02", "2099.01.01" }));

            Put(booted, "2099.01.04", Bundles.Make("2099.01.04"));
            Assert.That(booted.Versions(), Is.EqualTo(new[] { "2099.01.04", "2099.01.03", "2099.01.01" }));

            var next = new ContentCache(_root);
            Assert.That(next.LoadPreferred(Bundles.Shipped, Tier.M0, "1.0.0").Db.Version, Is.EqualTo("2099.01.04"));
            Assert.That(next.Versions(), Is.EqualTo(new[] { "2099.01.04", "2099.01.03" }), "CONTENT_CACHE_KEEP once 2099.01.01 is no longer in use");
        }

        [Test]
        public void AnInterruptedDownload_IsNeverBooted_AndColdBootDeletesIt()   // step 6: the rename is the ready mark
        {
            var cache = new ContentCache(_root);
            Put(cache, "2099.01.01", Bundles.Make("2099.01.01"), install: false);
            Assert.That(cache.Versions(), Is.Empty);
            var choice = cache.LoadPreferred(Bundles.Shipped, Tier.M0, "1.0.0");
            Assert.That(choice.IsBundled, Is.True);
            Assert.That(cache.InUse, Is.Null);
            Assert.That(Directory.Exists(cache.TempDir("2099.01.01")), Is.False);
        }

        [Test]
        public void Install_ReplacesAnEarlierCopy_AndDiscardKeepsTheInstalledOne()
        {
            var cache = new ContentCache(_root);
            var broken = Bundles.Make("2099.01.01", corrupt: true, touch: "skills");
            Put(cache, "2099.01.01", broken);
            Put(cache, "2099.01.01", Bundles.Make("2099.01.01"));
            Assert.That(cache.LoadPreferred(Bundles.Shipped, Tier.M0, "1.0.0").IsBundled, Is.False, "the good copy replaced the broken one");

            Put(cache, "2099.01.01", broken, install: false);
            cache.Discard("2099.01.01");
            Assert.That(Directory.Exists(cache.TempDir("2099.01.01")), Is.False);
            Assert.That(cache.Versions(), Is.EqualTo(new[] { "2099.01.01" }));
            Assert.Throws<DirectoryNotFoundException>(() => cache.Install("2099.01.02"));
        }

        [Test]
        public void AVersionIsAPathComponent_SoOnlyAContentVersionIsAccepted()
        {
            var cache = new ContentCache(_root);
            Assert.Throws<ArgumentException>(() => cache.BeginStaging("../escape"));
            Assert.Throws<ArgumentException>(() => cache.VersionDir("2099.1.1"));
            Directory.CreateDirectory(Path.Combine(_root, "not-a-version"));
            Assert.That(cache.Versions(), Is.Empty, "foreign directories are ignored");
        }
    }
}
