using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    /// <summary>The ConfigClient (issue #144) against 08 section 6.1's body, on <see cref="FakeApiHandler"/>.</summary>
    public class RemoteConfigTests
    {
        /// <summary>08 section 6.1's example body, verbatim apart from the elided URLs and hash.</summary>
        const string Body = @"{
  ""configVersion"": 318,
  ""serverTime"": 1793670900,
  ""minClientVersion"": ""1.4.0"",
  ""latestClientVersion"": ""1.4.2"",
  ""storeUrls"": { ""ios"": ""https://apps.apple.com/x"", ""android"": ""https://play.google.com/x"" },
  ""content"": {
    ""version"": ""2026.11.10"",
    ""url"": ""https://cdn.whiskerhero.example/content/2026.11.10/manifest.json"",
    ""sha256"": ""ab12"",
    ""sizeBytes"": 1843200,
    ""minClientVersion"": ""1.4.1"",
    ""rolloutPct"": 25,
    ""inBucket"": true,
    ""hotApplyTables"": [""localization"", ""shop_products"", ""shop_packages"", ""inbox_templates""]
  },
  ""minContentVersionByClient"": { ""1.4.0"": ""2026.10.20"", ""1.4.1"": ""2026.11.03"", ""1.4.2"": ""2026.11.03"" },
  ""killSwitches"": { ""shopEnabled"": true, ""summonEnabled"": { ""WEAPON"": true, ""SKILL"": true, ""COMPANION"": true, ""RUNE"": false }, ""dungeonsEnabled"": true, ""cloudSaveEnabled"": true, ""adsEnabled"": false, ""guildEnabled"": false, ""leaderboardEnabled"": false },
  ""maintenance"": { ""active"": false, ""messageKey"": null, ""until"": null },
  ""banners"": [ { ""banner"": ""WEAPON"", ""openFrom"": 0, ""openUntil"": null } ],
  ""events"": [],
  ""limits"": { ""summonPerMinute"": 20, ""saveUploadsPerDay"": 400 },
  ""features"": { ""stageSelect"": true, ""tutorialV2"": false }
}";

        const long T = 1793670900;

        static string WithWeapon(bool on) => Body.Replace(@"""WEAPON"": true, ""SKILL""", @"""WEAPON"": " + (on ? "true" : "false") + @", ""SKILL""");

        static ApiClient Client(FakeApiHandler h) => new ApiClient(new ApiClientOptions { Handler = h, Delay = (d, ct) => Task.CompletedTask, Jitter = () => 0 });

        static RemoteConfigService Service(FakeApiHandler h, IConfigCache cache, Func<long> now, TimeSpan? timeout = null) =>
            new RemoteConfigService(Client(h), cache, now, () => "2026.11.02", timeout);

        [Test]
        public async Task TheSection61Body_ReadsIntoClientConfig()
        {
            var h = new FakeApiHandler().On("GET", RemoteConfigService.Route, 200, Body);
            var svc = Service(h, new MemoryConfigCache(), () => T);
            var c = await svc.FetchAsync();

            Assert.That(c.Source, Is.EqualTo(ConfigSource.Fetched));
            Assert.That(c.ConfigVersion, Is.EqualTo(318));
            Assert.That(c.FetchedAtServerSeconds, Is.EqualTo(T), "the body's serverTime");
            Assert.That(c.MinClientVersion, Is.EqualTo("1.4.0"));
            Assert.That(c.LatestClientVersion, Is.EqualTo("1.4.2"));
            Assert.That(c.StoreUrls["ios"], Is.EqualTo("https://apps.apple.com/x"));
            Assert.That(c.ContentVersion, Is.EqualTo("2026.11.10"));
            Assert.That(c.Content, Is.EqualTo(new ContentPointer("2026.11.10", "https://cdn.whiskerhero.example/content/2026.11.10/manifest.json",
                                                                  "ab12", 1843200, "1.4.1") { RolloutPct = 25, InBucket = true }));
            Assert.That(c.Maintenance.Active, Is.False);
            Assert.That(c.Flags.ShopEnabled && c.Flags.DungeonsEnabled && c.Flags.CloudSaveEnabled, Is.True);
            Assert.That(c.Flags.AdsEnabled, Is.False);
            Assert.That(c.Flags.IsSummonEnabled(Banner.WEAPON), Is.True);
            Assert.That(c.Flags.IsSummonEnabled(Banner.RUNE), Is.False);
            Assert.That(svc.Current, Is.SameAs(c));
            Assert.That(svc.Flags, Is.SameAs(c.Flags));

            var call = h.Calls.Single();
            Assert.That(call.ToString(), Is.EqualTo("GET /config"));
            Assert.That(call.Header(ApiHeaders.ClientVersion), Is.Not.Null, "the 5.1 header set");
        }

        [Test]
        public async Task WithoutAJwt_InBucketIsOmitted_AndReadsAsUnknown()   // 08 section 3.4, issue #31: the client never hashes
        {
            var h = new FakeApiHandler().On("GET", RemoteConfigService.Route, 200, Body.Replace(@"""inBucket"": true,", ""));
            var c = await Service(h, null, () => T).FetchAsync();
            Assert.That(c.Content.InBucket, Is.Null);
            Assert.That(c.Content.RolloutPct, Is.EqualTo(25));
        }

        [Test]
        public async Task MaintenanceAndMissingSwitches_ReadLeniently()
        {
            const string body = @"{ ""serverTime"": 1793670900, ""killSwitches"": { ""summonEnabled"": { ""WEAPON"": false, ""NOT_A_BANNER"": false } },
                                     ""maintenance"": { ""active"": true, ""messageKey"": ""maint.db_upgrade"", ""until"": 1793680000 } }";
            var h = new FakeApiHandler().On("GET", RemoteConfigService.Route, 200, body);
            var c = await Service(h, null, () => T).FetchAsync();
            Assert.That(c.Maintenance, Is.EqualTo(new MaintenanceInfo(true, "maint.db_upgrade", 1793680000)));
            Assert.That(c.Flags.ShopEnabled && c.Flags.AdsEnabled && c.Flags.IsSummonEnabled(Banner.SKILL), Is.True, "a missing switch is on");
            Assert.That(c.Flags.IsSummonEnabled(Banner.WEAPON), Is.False);
            Assert.That(c.ContentVersion, Is.EqualTo("2026.11.02"), "no content block: the running version");
            Assert.That(c.Content, Is.Null);
        }

        [Test]
        public async Task AKillSwitch_AppliesOnTheNextFetch_WithoutARestart()   // 01 AC 11 (config half)
        {
            bool weapon = true;
            var h = new FakeApiHandler().On("GET", RemoteConfigService.Route, _ => FakeReply.Json(200, WithWeapon(weapon)));
            var svc = Service(h, new MemoryConfigCache(), () => T);
            await svc.FetchAsync();
            Assert.That(svc.Flags.IsSummonEnabled(Banner.WEAPON), Is.True);
            weapon = false;
            await svc.FetchAsync();
            Assert.That(svc.Flags.IsSummonEnabled(Banner.WEAPON), Is.False);
            Assert.That(svc.Flags.IsSummonEnabled(Banner.SKILL), Is.True);
        }

        [Test]
        public async Task OfflineAtBoot_UsesAFreshCache_ElseTheDefaults()   // 01 section 8.1 step 2
        {
            var cache = new MemoryConfigCache();
            long now = T;
            var online = new FakeApiHandler().On("GET", RemoteConfigService.Route, 200, WithWeapon(false));
            await Service(online, cache, () => now).FetchAsync();
            Assert.That(cache.Writes, Is.EqualTo(1));
            Assert.That(cache.Stored.FetchedAtServerSeconds, Is.EqualTo(T));

            // A later launch, offline, within CONFIG_CACHE_MAX_AGE_S: the cached answer, kill switch and all.
            now = T + RuntimeConfig.CONFIG_CACHE_MAX_AGE_S - 1;
            var offline = new FakeApiHandler().Then(FakeFault.Drop);
            var svc = Service(offline, cache, () => now);
            Assert.That(svc.Current.Source, Is.EqualTo(ConfigSource.Cached), "before any fetch");
            var c = await svc.FetchAsync();
            Assert.That(c.Source, Is.EqualTo(ConfigSource.Cached));
            Assert.That(c.Flags.IsSummonEnabled(Banner.WEAPON), Is.False);
            Assert.That(c.MinClientVersion, Is.EqualTo("1.4.0"));
            Assert.That(offline.Calls.Count, Is.EqualTo(1), "one attempt, never retried");

            // Past the max age: the defaults, everything enabled, no gate.
            now = T + RuntimeConfig.CONFIG_CACHE_MAX_AGE_S;
            c = await Service(new FakeApiHandler().Then(FakeFault.Drop), cache, () => now).FetchAsync();
            Assert.That(c.Source, Is.EqualTo(ConfigSource.Defaults));
            Assert.That(c.MinClientVersion, Is.Null);
            Assert.That(c.Maintenance.Active, Is.False);
            Assert.That(c.Flags.IsSummonEnabled(Banner.WEAPON) && c.Flags.ShopEnabled, Is.True);
            Assert.That(c.ContentVersion, Is.EqualTo("2026.11.02"));

            // No cache at all: the defaults too.
            c = await Service(new FakeApiHandler().Then(FakeFault.Drop), new MemoryConfigCache(), () => T).FetchAsync();
            Assert.That(c.Source, Is.EqualTo(ConfigSource.Defaults));
        }

        [Test]
        public async Task AFailedRefresh_KeepsTheLastAnswer_WhileFresh()
        {
            long now = T;
            var h = new FakeApiHandler().On("GET", RemoteConfigService.Route, 200, WithWeapon(false));
            var svc = Service(h, null, () => now);
            var first = await svc.FetchAsync();
            h.Then(FakeFault.Status(500, "INTERNAL"), FakeFault.With(FakeReply.Json(200, "[1, 2]")));
            now += 1800;
            Assert.That(await svc.FetchAsync(), Is.SameAs(first), "a 5xx keeps it");
            Assert.That(await svc.FetchAsync(), Is.SameAs(first), "an unreadable body keeps it");
            Assert.That(h.Calls.Count, Is.EqualTo(3), "the 500 is not retried: the refresh cadence is the retry");
            now = T + RuntimeConfig.CONFIG_CACHE_MAX_AGE_S;
            h.Then(FakeFault.Drop);
            Assert.That((await svc.FetchAsync()).Source, Is.EqualTo(ConfigSource.Defaults), "too old to trust");
        }

        [Test]
        public async Task AHangingServer_NeverHoldsTheFetchPastItsBudget()   // boot is never blocked longer than 3 s by config
        {
            var h = new FakeApiHandler().Then(FakeFault.Hang);
            var svc = Service(h, null, () => T, TimeSpan.FromMilliseconds(200));
            var sw = Stopwatch.StartNew();
            var c = await svc.FetchAsync();
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            Assert.That(c.Source, Is.EqualTo(ConfigSource.Defaults));
            Assert.That(h.Calls.Count, Is.EqualTo(1));
            Assert.That(RuntimeConfig.CONFIG_FETCH_TIMEOUT_S, Is.EqualTo(3), "the production budget (01 Constants)");
        }

        [Test]
        public async Task TheFileCache_SurvivesARestart_AndAGarbledFileReadsAsNone()
        {
            string dir = Path.Combine(Path.GetTempPath(), "herocat-config-" + Guid.NewGuid().ToString("N"));
            try
            {
                var h = new FakeApiHandler().On("GET", RemoteConfigService.Route, 200, WithWeapon(false));
                await Service(h, new FileConfigCache(dir), () => T).FetchAsync();
                await Service(h, new FileConfigCache(dir), () => T).FetchAsync();   // the second write replaces the first

                var restarted = Service(new FakeApiHandler().Then(FakeFault.Drop), new FileConfigCache(dir), () => T + 60);
                Assert.That(restarted.Current.Source, Is.EqualTo(ConfigSource.Cached));
                Assert.That(restarted.Flags.IsSummonEnabled(Banner.WEAPON), Is.False);
                Assert.That(restarted.Current.ConfigVersion, Is.EqualTo(318));

                var cache = new FileConfigCache(dir);
                File.WriteAllText(cache.FilePath, "{ not json");
                Assert.That(cache.Read(), Is.Null);
                Assert.That(Service(new FakeApiHandler(), cache, () => T).Current.Source, Is.EqualTo(ConfigSource.Defaults));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void RemoteWiring_UsesTheConfigClient()
        {
            string dir = Path.Combine(Path.GetTempPath(), "herocat-wire-" + Guid.NewGuid().ToString("N"));
            try
            {
                var svc = RemoteGameServices.Create(dir, new ApiClientOptions { Handler = new OfflineHandler() });
                Assert.That(svc.Config, Is.TypeOf<RemoteConfigService>());
                var c = svc.Config.FetchAsync().GetAwaiter().GetResult();
                Assert.That(c.Source, Is.EqualTo(ConfigSource.Defaults), "offline with no cache");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
