using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.Content.Dto;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>Content bundles derived from the shipped content/ for the update tests.</summary>
    internal static class Bundles
    {
        static readonly Lazy<ManifestDto> _running = new Lazy<ManifestDto>(() => ContentLoader.LoadDtos(Shipped).Manifest);

        public static IContentSource Shipped => new FileContentSource(TestContent.Dir);

        public static ManifestDto RunningManifest => _running.Value;

        public static ContentPointer Pointer(string version, string minClient = null) =>
            new ContentPointer(version, "https://cdn.example/content/" + version + "/manifest.json", "sha", 1, minClient);

        /// <summary>
        /// The shipped set re-versioned as <paramref name="version"/>. <paramref name="touch"/> names tables (or
        /// "localization") whose bytes change (a trailing newline: same rows, new sha256); <paramref name="corrupt"/> leaves
        /// their manifest sha256 stale, as a tampered CDN file would.
        /// </summary>
        public static MemoryContentSource Make(string version, bool hotApply = false, bool corrupt = false, string minClient = null,
                                               params string[] touch)
        {
            var src = MemoryContentSource.CopyOf(Shipped);
            var m = JObject.Parse(Encoding.UTF8.GetString(src.Files["manifest.json"]));
            m["content_version"] = version;
            m["hot_apply"] = hotApply;
            if (minClient != null) m["min_client_version"] = minClient;
            foreach (var t in touch)
            {
                var entry = t == "localization" ? (JObject)m["localization"]["en"] : (JObject)m["tables"][t];
                string file = (string)entry["file"];
                var bytes = src.Files[file].Concat(new[] { (byte)'\n' }).ToArray();
                src.Files[file] = bytes;
                if (!corrupt) entry["sha256"] = Sha(bytes);
            }
            src.Files["manifest.json"] = Encoding.UTF8.GetBytes(m.ToString());
            return src;
        }

        static string Sha(byte[] b)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(b).Select(x => x.ToString("x2")));
        }
    }

    /// <summary>The content update flow of 01 section 8.12 and 09 section 10.3 (issue #144) over the staging seam (#212).</summary>
    public class ContentUpdaterTests
    {
        const string V = "2099.01.01";

        static ClientConfig Offer(string version, string minClient = null) => ScriptedConfig.Cfg(content: Bundles.Pointer(version, minClient));

        static ContentUpdater Updater(MemoryContentStaging staging, List<ContentError> log = null, string clientVersion = "1.0.0") =>
            new ContentUpdater(staging, TestContent.Db, Bundles.RunningManifest, clientVersion, log == null ? null : (Action<ContentError>)(e => { lock (log) log.Add(e); }));

        static ContentUpdater Run(MemoryContentSource bundle, out MemoryContentStaging staging, List<ContentError> log = null)
        {
            staging = new MemoryContentStaging();
            staging.Cdn[V] = bundle;
            var u = Updater(staging, log);
            Assert.That(u.Check(Offer(V)).Wait(10000), Is.True);
            return u;
        }

        [Test]
        public void AGoodBundle_IsMarkedPending_AndTheNextColdBootPicksIt()   // 01 section 8.12 step 2, 09 section 10.3 steps 6 and 8
        {
            var u = Run(Bundles.Make(V, touch: "skills"), out var staging);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.PendingColdBoot));
            Assert.That(u.UpdateReady, Is.True, "SCR_SETTINGS: Update ready");
            Assert.That(u.Version, Is.EqualTo(V));
            Assert.That(staging.Cached.Count, Is.EqualTo(1));

            var next = ContentSelection.LoadPreferred(Bundles.Shipped, staging.Cached, Tier.M0, "1.0.0");
            Assert.That(next.IsBundled, Is.False);
            Assert.That(next.Db.Version, Is.EqualTo(V));
        }

        [Test]
        public void ABadPerTableHash_IsDiscarded_AndTheNextBootUsesTheBundle()   // issue #144 acceptance
        {
            var log = new List<ContentError>();
            var u = Run(Bundles.Make(V, corrupt: true, touch: "skills"), out var staging, log);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Failed));
            Assert.That(u.UpdateReady, Is.False);
            Assert.That(staging.Discarded, Is.EqualTo(new[] { V }));
            Assert.That(staging.Cached, Is.Empty);
            Assert.That(log.Select(e => e.Code), Has.Member("E900"));
            Assert.That(u.LastErrors.Select(e => e.Location), Has.Member("skills.json"));
            Assert.That(ContentSelection.LoadPreferred(Bundles.Shipped, staging.Cached, Tier.M0, "1.0.0").IsBundled, Is.True);

            Assert.That(u.Check(Offer(V)).IsCompleted, Is.True);
            Assert.That(staging.Downloads, Is.EqualTo(1), "a broken version is not re-downloaded every refresh");
        }

        [Test]
        public void Failed_IsRaisedOncePerBadBundle_WithItsFirstError()   // 09 section 10.3 step 5, issue #298
        {
            var staging = new MemoryContentStaging();
            staging.Cdn[V] = Bundles.Make(V, corrupt: true, touch: "skills");
            var u = Updater(staging);
            var seen = new List<(string, ContentError)>();
            u.Failed += (v, e) => { lock (seen) seen.Add((v, e)); };
            u.Failed += (v, e) => throw new InvalidOperationException("a broken reporter");
            Assert.That(u.Check(Offer(V)).Wait(10000), Is.True, "a throwing handler does not fault the staging task");
            Assert.That(u.Check(Offer(V)).IsCompleted, Is.True);
            Assert.That(seen, Has.Count.EqualTo(1));
            Assert.That(seen[0].Item1, Is.EqualTo(V));
            Assert.That((seen[0].Item2.Code, seen[0].Item2.Location), Is.EqualTo((u.LastErrors[0].Code, u.LastErrors[0].Location)));
        }

        [Test]
        public void ADownloadFailure_LeavesTheRunningContent()
        {
            var staging = new MemoryContentStaging();   // the fake CDN has nothing
            var u = Updater(staging);
            Assert.That(u.Check(Offer(V)).Wait(10000), Is.True);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Failed));
            Assert.That(staging.Discarded, Is.EqualTo(new[] { V }));
        }

        [Test]
        public void AHotApplyBundle_OfAllowlistTablesOnly_SwapsAtASafePoint()   // 00 contract 3, 01 section 8.12 step 2
        {
            var u = Run(Bundles.Make(V, hotApply: true, touch: "localization"), out var staging);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.HotReady));
            Assert.That(u.TryApplyHot(safePoint: false), Is.Null, "not with a modal open or in a boss");
            HotContentSet raised = null;
            u.HotApplied += s => raised = s;
            var set = u.TryApplyHot(safePoint: true);
            Assert.That(set, Is.Not.Null);
            Assert.That(raised, Is.SameAs(set));
            Assert.That(set.Version, Is.EqualTo(V));
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.HotApplied));
            Assert.That(u.TryApplyHot(true), Is.Null, "once");
            Assert.That(staging.Cached.Count, Is.EqualTo(1), "and the next cold boot loads the whole bundle");

            var loc = Localizer.Load(Bundles.Shipped);
            loc.Replace(set.Source);   // the Localizer reads the swapped strings outside the sim's ContentDb
            Assert.That(loc.Has("err.summon_disabled.title"), Is.True);
        }

        [Test]
        public void AHotApplyBundle_TouchingAnyOtherTable_IsColdBootOnly()   // issue #144 acceptance
        {
            var u = Run(Bundles.Make(V, hotApply: true, touch: new[] { "localization", "skills" }), out _);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.PendingColdBoot));
            Assert.That(u.TryApplyHot(true), Is.Null);
        }

        [Test]
        public void WithoutTheManifestBool_AnAllowlistOnlyDiff_IsStillColdBootOnly()   // the manifest bool is required (issue #54)
        {
            var u = Run(Bundles.Make(V, hotApply: false, touch: "localization"), out _);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.PendingColdBoot));
        }

        [Test]
        public void TheHotDiff_ComparesAgainstTheRunningManifest()
        {
            var running = Bundles.RunningManifest;
            ManifestDto With(Action<ManifestDto> edit)
            {
                var m = ContentLoader.LoadDtos(Bundles.Make(V, hotApply: true)).Manifest;
                edit(m);
                return m;
            }
            Assert.That(ContentUpdater.IsHotApplicable(running, With(_ => { })), Is.True, "identical tables");
            Assert.That(ContentUpdater.IsHotApplicable(running, With(m => m.Tables["shop_products"] = new ManifestEntryDto { File = "shop_products.json", Sha256 = "x" })),
                        Is.True, "an added allowlist table");
            Assert.That(ContentUpdater.IsHotApplicable(running, With(m => m.Tables.Remove("skills"))), Is.False, "a removed sim table");
            Assert.That(ContentUpdater.IsHotApplicable(running, With(m => m.SchemaVersion = 2)), Is.False);
            Assert.That(ContentUpdater.IsHotApplicable(null, With(_ => { })), Is.False, "nothing to diff against");
            Assert.That(ContentUpdater.HotApplyTables, Is.EquivalentTo(new[] { "localization", "shop_products", "shop_packages", "inbox_templates" }));
        }

        [Test]
        public void NothingNewer_OrTooNewForThisClient_DownloadsNothing()   // 09 section 10.3 steps 2 and 3
        {
            var staging = new MemoryContentStaging();
            var u = Updater(staging);
            Assert.That(u.Check(Offer(TestContent.Db.Version)).IsCompleted, Is.True);
            Assert.That(u.Check(Offer("2020.01.01")).IsCompleted, Is.True);
            Assert.That(u.Check(ScriptedConfig.Cfg()).IsCompleted, Is.True, "no content block");
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Idle));

            Assert.That(u.Check(Offer(V, minClient: "9.0.0")).IsCompleted, Is.True);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.NeedsClientUpdate));
            Assert.That(staging.Downloads, Is.EqualTo(0));
        }

        [Test]
        public void AManifestAboveThisClient_FailsE903_AndKeepsTheRunningContent()   // 09 acceptance 16
        {
            var log = new List<ContentError>();
            var staging = new MemoryContentStaging();
            staging.Cdn[V] = Bundles.Make(V, minClient: "9.0.0");
            var u = Updater(staging, log);
            Assert.That(u.Check(Offer(V)).Wait(10000), Is.True, "config did not say, the manifest does");
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Failed));
            Assert.That(log.Select(e => e.Code), Is.EqualTo(new[] { "E903" }));
            Assert.That(staging.Cached, Is.Empty);
        }

        [Test]
        public void ABundleWhoseManifestIsAnotherVersion_IsRejected()
        {
            var staging = new MemoryContentStaging();
            staging.Cdn[V] = Bundles.Make("2099.03.03");
            var u = Updater(staging);
            Assert.That(u.Check(Offer(V)).Wait(10000), Is.True);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Failed));
        }
    }
}
