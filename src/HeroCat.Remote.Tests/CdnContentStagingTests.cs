using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.Content.Dto;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    /// <summary>
    /// The content cache of 09 section 10.3 steps 4-6 and 8 (issue #212): <see cref="CdnContentStaging"/> under the real
    /// <see cref="ContentUpdater"/> and <see cref="ApiClient"/>, with the CDN a <see cref="FakeApiHandler"/> and the cache a
    /// temp directory (specs/10 test row <c>ContentLoader</c>).
    /// </summary>
    public class CdnContentStagingTests
    {
        const string V = "2099.01.01";
        const string Client = "1.0.0";

        static readonly Lazy<string> ShippedDir = new Lazy<string>(() => FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory));
        static readonly Lazy<ContentDb> Running = new Lazy<ContentDb>(() => ContentLoader.LoadFromDirectory(ShippedDir.Value, Tier.M0));
        static readonly Lazy<ManifestDto> RunningManifest = new Lazy<ManifestDto>(() => ContentLoader.LoadDtos(Shipped).Manifest);

        static IContentSource Shipped => new FileContentSource(ShippedDir.Value);

        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "herocat-cdn-" + Guid.NewGuid().ToString("N"), "content");

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(Path.GetDirectoryName(_root), true); }
            catch (DirectoryNotFoundException) { }
        }

        /// <summary>A bundle on the fake CDN: its files by name and the config pointer to it.</summary>
        sealed class Published
        {
            public Dictionary<string, byte[]> Files;
            public ContentPointer Pointer;
        }

        static string Sha(byte[] b)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(b).Select(x => x.ToString("x2")));
        }

        static string Url(string version, string file) => "https://cdn.whiskerhero.example/content/" + version + "/" + file;

        /// <summary>The shipped set re-versioned (and <paramref name="edit"/>ed) as the exporter would publish it.</summary>
        static Published Publish(string version, Action<JObject> edit = null, string touch = null)
        {
            var files = new Dictionary<string, byte[]>(MemoryContentSource.CopyOf(Shipped).Files, StringComparer.Ordinal);
            var m = JObject.Parse(Encoding.UTF8.GetString(files["manifest.json"]));
            m["content_version"] = version;
            m["hot_apply"] = false;
            if (touch != null)
            {
                var entry = (JObject)m["tables"][touch];
                string file = (string)entry["file"];
                files[file] = files[file].Concat(new[] { (byte)'\n' }).ToArray();
                entry["sha256"] = Sha(files[file]);
            }
            edit?.Invoke(m);
            files["manifest.json"] = Encoding.UTF8.GetBytes(m.ToString());
            return new Published
            {
                Files = files,
                Pointer = new ContentPointer(version, Url(version, "manifest.json"), Sha(files["manifest.json"]), 1843200, null),
            };
        }

        static FakeApiHandler Cdn(params Published[] bundles)
        {
            var h = new FakeApiHandler();
            foreach (var b in bundles)
                foreach (var f in b.Files)
                {
                    var bytes = f.Value;
                    h.On("GET", new Uri(Url(b.Pointer.Version, f.Key)).AbsolutePath, _ => new FakeReply(200, bytes: bytes));
                }
            return h;
        }

        static ApiClient Api(FakeApiHandler h) => new ApiClient(new ApiClientOptions
        {
            Handler = h, Delay = (d, ct) => Task.CompletedTask, Jitter = () => 0, DeviceId = "de71ce00-0000-4000-8000-000000000001",
            Credentials = new StaticToken(),
        });

        sealed class StaticToken : IApiCredentials
        {
            public string AccessToken => "eyJ.secret";
            public Task<bool> RefreshAsync(System.Threading.CancellationToken ct) => Task.FromResult(false);
        }

        ContentCache Cache() => new ContentCache(_root);

        ContentUpdater Updater(FakeApiHandler cdn, ContentCache cache, List<ContentError> log, string stagingClient = null) =>
            new ContentUpdater(new CdnContentStaging(Api(cdn), cache, stagingClient), Running.Value, RunningManifest.Value, Client,
                               e => { lock (log) log.Add(e); });

        static ClientConfig Offer(ContentPointer p) => ClientConfig.Defaults("2026.11.02") with { Content = p };

        static void Stage(ContentUpdater u, ContentPointer p) => Assert.That(u.Check(Offer(p)).Wait(30000), Is.True);

        bool AnyTemp() => Directory.Exists(_root) && Directory.GetDirectories(_root).Any(d => d.EndsWith(ContentCache.TempSuffix, StringComparison.Ordinal));

        [Test]
        public void AGoodBundle_IsDownloadedVerifiedAndRenamed_AndTheNextColdBootPicksIt()   // 09 section 10.3 steps 4, 6 and 8
        {
            var b = Publish(V, touch: "skills");
            var cdn = Cdn(b);
            var cache = Cache();
            var log = new List<ContentError>();
            var u = Updater(cdn, cache, log);
            Stage(u, b.Pointer);

            Assert.That(log, Is.Empty);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.PendingColdBoot));
            Assert.That(cache.Versions(), Is.EqualTo(new[] { V }));
            Assert.That(AnyTemp(), Is.False, "<version>.tmp was renamed to <version>");
            Assert.That(Directory.GetFiles(cache.VersionDir(V)).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal),
                        Is.EqualTo(b.Files.Keys.OrderBy(x => x, StringComparer.Ordinal)), "every listed file and the manifest, nothing else");
            Assert.That(cdn.Calls.First().Path, Is.EqualTo("/content/" + V + "/manifest.json"), "the manifest first");
            foreach (var call in cdn.Calls)
            {
                Assert.That(call.Header(ApiHeaders.Authorization), Is.Null, "the CDN never sees the access token");
                Assert.That(call.Header(ApiHeaders.DeviceId), Is.Null, "nor the install id");
            }

            var next = Cache().LoadPreferred(Shipped, Tier.M0, Client);
            Assert.That(next.IsBundled, Is.False);
            Assert.That(next.Db.Version, Is.EqualTo(V));
        }

        [Test]
        public void AManifestThatIsNotTheOneConfigPinned_FailsE900_BeforeAnyTableIsFetched()   // step 4
        {
            var b = Publish(V);
            var pointer = b.Pointer with { Sha256 = new string('0', 64) };
            var cdn = Cdn(b);
            var log = new List<ContentError>();
            var u = Updater(cdn, Cache(), log);
            Stage(u, pointer);

            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Failed));
            Assert.That(log.Select(e => (e.Code, e.Location)), Is.EqualTo(new[] { ("E900", "manifest.json") }));
            Assert.That(cdn.Calls.Count, Is.EqualTo(1), "only the manifest was fetched");
            Assert.That(Cache().Versions(), Is.Empty);
            Assert.That(AnyTemp(), Is.False);
            Assert.That(Cache().LoadPreferred(Shipped, Tier.M0, Client).IsBundled, Is.True, "hash failure keeps the bundle");
        }

        [Test]
        public void ATamperedCdnFile_NeverReachesTheDiskOrContentDbBuild()   // issue #212 acceptance
        {
            var b = Publish(V, touch: "skills");
            b.Files["skills.json"] = b.Files["skills.json"].Concat(new[] { (byte)' ' }).ToArray();   // served bytes != manifest sha256
            var cdn = Cdn(b);
            var cache = Cache();
            var log = new List<ContentError>();
            var u = Updater(cdn, cache, log);
            Stage(u, b.Pointer);

            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Failed));
            Assert.That(log.Select(e => (e.Code, e.Location)), Is.EqualTo(new[] { ("E900", "skills.json") }),
                        "staging refused the file; nothing was built from it");
            Assert.That(cache.Versions(), Is.Empty);
            Assert.That(AnyTemp(), Is.False, "the temp dir is deleted");
            Assert.That(Cache().LoadPreferred(Shipped, Tier.M0, Client).IsBundled, Is.True);
        }

        [Test]
        public void AManifestAboveThisClient_LogsE903_AndKeepsTheRunningContent()   // 09 acceptance 16 (CDN half), 01 AC 11
        {
            var running = Running.Value;
            var b = Publish(V, m => m["min_client_version"] = "9.0.0");   // config's pointer does not say so: only the manifest does
            var log = new List<ContentError>();
            var u = Updater(Cdn(b), Cache(), log);
            Stage(u, b.Pointer);

            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Failed));
            Assert.That(log.Select(e => e.Code), Is.EqualTo(new[] { "E903" }));
            Assert.That(Cache().Versions(), Is.Empty);
            Assert.That(ReferenceEquals(Running.Value, running), Is.True, "the sim's ContentDb is the same instance");
            var boot = Cache().LoadPreferred(Shipped, Tier.M0, Client);
            Assert.That(boot.IsBundled, Is.True);
        }

        [Test]
        public void WithTheClientVersion_StagingRefusesE903_AfterTheManifestAlone()
        {
            var b = Publish(V, m => m["min_client_version"] = "9.0.0");
            var cdn = Cdn(b);
            var log = new List<ContentError>();
            var u = Updater(cdn, Cache(), log, stagingClient: Client);
            Stage(u, b.Pointer);
            Assert.That(log.Select(e => e.Code), Is.EqualTo(new[] { "E903" }));
            Assert.That(cdn.Calls.Count, Is.EqualTo(1), "no table downloaded for a client that cannot load it");
        }

        [Test]
        public void AnUnknownSchemaVersion_FailsE901()   // step 4
        {
            var b = Publish(V, m => m["schema_version"] = 2);
            var log = new List<ContentError>();
            var u = Updater(Cdn(b), Cache(), log);
            Stage(u, b.Pointer);
            Assert.That(log.Select(e => e.Code), Is.EqualTo(new[] { "E901" }));
        }

        [Test]
        public void AManifestNamingAPath_IsRefused_AndNothingIsWrittenOutsideTheCache()
        {
            var b = Publish(V, m => m["tables"]["skills"]["file"] = "../../escape.json");
            var cdn = Cdn(b);
            var log = new List<ContentError>();
            var u = Updater(cdn, Cache(), log);
            Stage(u, b.Pointer);
            Assert.That(log.Select(e => (e.Code, e.Location)), Is.EqualTo(new[] { ("E900", "manifest.json") }));
            Assert.That(cdn.Calls.Count, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(_root), "escape.json")), Is.False);
        }

        [Test]
        public void AMissingCdnFile_FailsTheBundle_AndDeletesTheTempDir()
        {
            var b = Publish(V);
            var cdn = Cdn(b).On("GET", "/content/" + V + "/skills.json", 404, "<html>not found</html>");
            var log = new List<ContentError>();
            var cache = Cache();
            var u = Updater(cdn, cache, log);
            Stage(u, b.Pointer);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Failed));
            Assert.That(log, Has.Count.EqualTo(1));
            Assert.That(AnyTemp(), Is.False);
            Assert.That(cache.Versions(), Is.Empty);
        }

        [Test]
        public void ACdnOutage_AfterTheRetries_IsRetriedAtTheNextConfig_NotReported()   // issue #316
        {
            var b = Publish(V, touch: "skills");
            bool down = true;
            var cdn = Cdn(b);
            var skills = b.Files["skills.json"];
            cdn.On("GET", "/content/" + V + "/skills.json", _ => down ? new FakeReply(502, "<html>bad gateway</html>") : new FakeReply(200, bytes: skills));
            var cache = Cache();
            var log = new List<ContentError>();
            var u = Updater(cdn, cache, log);
            int failed = 0;
            u.Failed += (v, e) => Interlocked.Increment(ref failed);
            Stage(u, b.Pointer);

            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Idle), "not Failed: the bundle was never judged");
            Assert.That(failed, Is.Zero, "no content_update_failed");
            Assert.That(log, Is.Empty);
            Assert.That(AnyTemp(), Is.False, "the partial download is deleted");
            Assert.That(cache.Versions(), Is.Empty);

            down = false;
            Stage(u, b.Pointer);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.PendingColdBoot));
            Assert.That(cache.Versions(), Is.EqualTo(new[] { V }));
            Assert.That(failed, Is.Zero);
        }

        [Test]
        public void OfflineAtBoot_IsRetriedAtTheNextConfig()   // issue #316
        {
            var b = Publish(V);
            var cdn = Cdn(b);
            cdn.Then(Enumerable.Range(0, 50).Select(_ => FakeFault.Drop).ToArray());
            var log = new List<ContentError>();
            var u = Updater(cdn, Cache(), log);
            Stage(u, b.Pointer);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.Idle));
            Assert.That(log, Is.Empty);
            Assert.That(cdn.PendingFaults, Is.GreaterThan(0), "the ApiClient gave up before the network came back");

            while (cdn.PendingFaults > 0) Stage(u, b.Pointer);
            Stage(u, b.Pointer);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.PendingColdBoot));
            Assert.That(log, Is.Empty);
        }

        [Test]
        public void TheCacheKeepsTheTwoNewestVersions()   // step 8, CONTENT_CACHE_KEEP
        {
            var bundles = new[] { Publish("2099.01.01"), Publish("2099.01.02"), Publish("2099.01.03") };
            var cache = Cache();
            var log = new List<ContentError>();
            var u = Updater(Cdn(bundles), cache, log);
            foreach (var b in bundles) Stage(u, b.Pointer);

            Assert.That(log, Is.Empty);
            Assert.That(HeroCat.Runtime.RuntimeConfig.CONTENT_CACHE_KEEP, Is.EqualTo(2));
            Assert.That(cache.Versions(), Is.EqualTo(new[] { "2099.01.03", "2099.01.02" }));
            Assert.That(Cache().LoadPreferred(Shipped, Tier.M0, Client).Db.Version, Is.EqualTo("2099.01.03"));
        }

        [Test]
        public void ACdnRequest_IsAnUnauthenticatedHttpsGetOnly()
        {
            var api = Api(new FakeApiHandler());
            Assert.That(ApiRequest.Cdn(Url(V, "manifest.json")).IsAbsolute, Is.True);
            Assert.ThrowsAsync<ArgumentException>(() =>
                api.SendAsync<byte[]>(new ApiRequest(HttpMethod.Get, Url(V, "manifest.json"))));   // RequiresAuth defaults to true
            Assert.ThrowsAsync<ArgumentException>(() =>
                api.SendAsync<byte[]>(new ApiRequest(HttpMethod.Get, "http://cdn.whiskerhero.example/x.json", RequiresAuth: false)));
        }
    }
}
