using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Runtime.Summons;
using HeroCat.Sim.Content;
using HeroCat.Sim.Replay;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>A config service whose next answer the test sets; <see cref="Hold"/> keeps a fetch in flight.</summary>
    internal sealed class ScriptedConfig : IConfigService
    {
        public ScriptedConfig(ClientConfig initial) { Current = initial; Next = initial; }

        public ClientConfig Next;
        public int Calls;
        public TaskCompletionSource<ClientConfig> Hold;

        public ClientConfig Current { get; private set; }
        public KillSwitches Flags => Current.Flags;

        public Task<ClientConfig> FetchAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Hold != null) return Hold.Task;
            Current = Next;
            return Task.FromResult(Current);
        }

        public static ClientConfig Cfg(bool weapon = true, string minClient = null, MaintenanceInfo maintenance = null,
                                       ContentPointer content = null, ConfigSource source = ConfigSource.Fetched) =>
            new ClientConfig(content?.Version ?? "", minClient, maintenance ?? MaintenanceInfo.None,
                             new KillSwitches(true, true, true, true, new Dictionary<Banner, bool> { [Banner.WEAPON] = weapon }))
            {
                Content = content, Source = source, FetchedAtServerSeconds = TestContent.T0,
            };
    }

    /// <summary>The config cadence, gates and kill switches of 01 sections 8.1 step 2 and 8.12 (issue #144).</summary>
    public class ConfigRefreshTests
    {
        static ConfigRefresher Refresher(ScriptedConfig svc, FakeMonoClock mono, string clientVersion = "1.4.2+318") =>
            new ConfigRefresher(svc, new FakeClock(TestContent.T0), mono, clientVersion, Guid.Empty);

        [Test]
        public void RefreshesEveryConfigRefreshS_AndOnEveryResume()   // 01 section 8.12 step 1
        {
            var mono = new FakeMonoClock();
            var svc = new ScriptedConfig(ScriptedConfig.Cfg());
            var rf = Refresher(svc, mono);
            int landed = 0;
            rf.Landed += _ => landed++;
            rf.Start(svc.Current);

            mono.Advance(RuntimeConfig.CONFIG_REFRESH_S - 1);
            rf.Update();
            Assert.That(svc.Calls, Is.EqualTo(0));
            mono.Advance(1);
            rf.Update();
            Assert.That(svc.Calls, Is.EqualTo(1));
            Assert.That(landed, Is.EqualTo(1));
            Assert.That(rf.SecondsUntilDue, Is.EqualTo(RuntimeConfig.CONFIG_REFRESH_S).Within(1e-6));

            rf.OnResume();
            Assert.That(svc.Calls, Is.EqualTo(2), "every resume");
        }

        [Test]
        public void AFetchInFlight_LandsOnALaterFrame_AndIsNeverDoubled()
        {
            var mono = new FakeMonoClock();
            var svc = new ScriptedConfig(ScriptedConfig.Cfg()) { Hold = new TaskCompletionSource<ClientConfig>() };
            var rf = Refresher(svc, mono);
            ClientConfig got = null;
            rf.Landed += c => got = c;
            rf.RefreshNow();
            rf.RefreshNow();
            rf.OnResume();
            mono.Advance(RuntimeConfig.CONFIG_REFRESH_S * 2);
            rf.Update();
            Assert.That(svc.Calls, Is.EqualTo(1));
            Assert.That(rf.InFlight, Is.True);
            Assert.That(got, Is.Null);

            var answer = ScriptedConfig.Cfg(weapon: false);
            svc.Hold.SetResult(answer);
            svc.Hold = null;
            rf.Update();
            Assert.That(got, Is.SameAs(answer));
            Assert.That(rf.InFlight, Is.False);
        }

        [Test]
        public void Maintenance_PollsEveryMaintPollS_AndRefetchesAtUntil()   // 01 section 7, 08 section 8.4
        {
            var mono = new FakeMonoClock();
            var svc = new ScriptedConfig(ScriptedConfig.Cfg(maintenance: new MaintenanceInfo(true, "maint.db_upgrade", TestContent.T0 + 6000)));
            var rf = Refresher(svc, mono);
            rf.Start(svc.Current);
            Assert.That(rf.SecondsUntilDue, Is.EqualTo(RuntimeConfig.MAINT_POLL_S).Within(1e-6));
            var b = rf.Banner(TestContent.T0);
            Assert.That(b, Is.EqualTo(new MaintenanceBanner(true, "maint.db_upgrade", TestContent.T0 + 6000, 6000)));

            rf.Start(ScriptedConfig.Cfg(maintenance: new MaintenanceInfo(true, "m", TestContent.T0 + 20)));
            Assert.That(rf.SecondsUntilDue, Is.EqualTo(20).Within(1e-6), "at until, sooner than the poll");

            rf.Start(ScriptedConfig.Cfg(maintenance: new MaintenanceInfo(false, "m", TestContent.T0 + 900)));
            Assert.That(rf.SecondsUntilDue, Is.EqualTo(900).Within(1e-6), "an announced window re-fetches at its start");
        }

        [Test]
        public void TheBanner_AnnouncesAWindow_AnHourAhead()   // 08 section 8.4
        {
            long t = TestContent.T0;
            Assert.That(ConfigRefresher.BannerFor(MaintenanceInfo.None, t), Is.Null);
            Assert.That(ConfigRefresher.BannerFor(new MaintenanceInfo(false, "m", t + RuntimeConfig.MAINT_ANNOUNCE_S + 1), t), Is.Null);
            Assert.That(ConfigRefresher.BannerFor(new MaintenanceInfo(false, "m", t + RuntimeConfig.MAINT_ANNOUNCE_S), t),
                        Is.EqualTo(new MaintenanceBanner(false, "m", t + RuntimeConfig.MAINT_ANNOUNCE_S, RuntimeConfig.MAINT_ANNOUNCE_S)));
            Assert.That(ConfigRefresher.BannerFor(new MaintenanceInfo(false, "m", t - 1), t), Is.Null, "over without ever going active");
            Assert.That(ConfigRefresher.BannerFor(new MaintenanceInfo(true, null, 0), t), Is.EqualTo(new MaintenanceBanner(true, null, 0, 0)));
        }

        [Test]
        public void ClientTooOld_MidSession_WaitsForIdle_AndSavesFirst()   // 01 section 8.12 step 3
        {
            var mono = new FakeMonoClock();
            var svc = new ScriptedConfig(ScriptedConfig.Cfg(minClient: "1.4.2"));
            var rf = Refresher(svc, mono);
            rf.Start(svc.Current);
            Assert.That(rf.ClientTooOld, Is.Null, "1.4.2+318 meets 1.4.2: build metadata is ignored");

            svc.Next = ScriptedConfig.Cfg(minClient: "1.5.0");
            rf.OnResume();
            Assert.That(rf.ClientTooOld?.Code, Is.EqualTo("CLIENT_TOO_OLD"));
            int saves = 0;
            Assert.That(rf.TryBlock(false, () => { saves++; return true; }), Is.Null, "not while SCR_MAIN is busy");
            Assert.That(saves, Is.EqualTo(0));
            var e = rf.TryBlock(true, () => { saves++; return true; });
            Assert.That(e.Code, Is.EqualTo("CLIENT_TOO_OLD"));
            Assert.That(saves, Is.EqualTo(1));
        }

        [Test]
        public void Semver_IgnoresBuildMetadata()   // 08 section 1.1
        {
            Assert.That(ContentLoader.CompareSemver("1.4.2+318", "1.4.2"), Is.EqualTo(0));
            Assert.That(ContentLoader.CompareSemver("1.4.2+318", "1.4.3"), Is.EqualTo(-1));
            Assert.That(ContentLoader.CompareSemver("1.10.0", "1.9.9+1"), Is.EqualTo(1));
        }

        // ------------------------------------------------------------------------------------- boot (01 section 8.1)

        static BootOptions Boot(IConfigService config, FakeMonoClock mono, MemorySaveStore store = null)
        {
            var clock = new FakeClock(TestContent.T0);
            store = store ?? new MemorySaveStore();
            return new BootOptions
            {
                Content = new Func<ContentDb>[] { () => TestContent.Db },
                Services = (db, state) => GameServices.Build(BackendMode.Local, new ServicesOptions
                {
                    Clock = clock, Slots = store, State = state, Content = db, Analytics = new MemoryAnalytics(), InstallId = TestContent.Device,
                }) with { Config = config },
                NewProfileRngRoot = TestContent.Root,
                Runner = new SessionRunnerOptions { Mono = mono, TickBudgetMs = 0 },
                CommandLog = new ListCommandLog(),
                Mono = new FakeMonoClock(),
            };
        }

        [Test]
        public void AKillSwitch_DisablesSummons_WithinOneRefresh_AndTheContentDbNeverChanges()   // 01 AC 11
        {
            var mono = new FakeMonoClock();
            const string newer = "2099.01.01";
            var svc = new ScriptedConfig(ScriptedConfig.Cfg(content: Bundles.Pointer(newer)));
            var staging = new MemoryContentStaging();
            staging.Cdn[newer] = Bundles.Make(newer);
            var o = Boot(svc, mono);
            o.ContentUpdates = db => new ContentUpdater(staging, db, Bundles.RunningManifest, null);
            var r = BootFlow.Run(o);
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Ready));
            var db0 = r.Sim.Db;
            Assert.That(r.Runner.Config, Is.SameAs(r.ConfigRefresher));
            Assert.That(r.ContentUpdates.Check(r.Config).Wait(10000), Is.True);
            Assert.That(r.ContentUpdates.UpdateReady, Is.True, "staged for the next cold boot");
            Assert.That(r.Sim.Db, Is.SameAs(db0));

            // The flow works before the switch.
            Assert.That(r.Runner.Summons.SummonAsync(Banner.WEAPON, 1, SummonPayment.Diamonds).Result.IsOk, Is.True);

            // The server flips WEAPON off and announces an even newer bundle; one refresh later the summon is refused.
            const string newest = "2099.02.01";
            staging.Cdn[newest] = Bundles.Make(newest);
            svc.Next = ScriptedConfig.Cfg(weapon: false, content: Bundles.Pointer(newest));
            int calls = svc.Calls;
            mono.Advance(RuntimeConfig.CONFIG_REFRESH_S);
            r.Runner.OnFrame();
            Assert.That(svc.Calls, Is.EqualTo(calls + 1));
            Assert.That(r.Runner.Services.Config.Flags.IsSummonEnabled(Banner.WEAPON), Is.False);
            var refused = r.Runner.Summons.SummonAsync(Banner.WEAPON, 1, SummonPayment.Diamonds).Result;
            Assert.That(refused.Error, Is.EqualTo(SummonError.Disabled));
            Assert.That(r.Runner.Summons.Pending, Is.Null, "no intent saved, nothing charged");

            Assert.That(r.ContentUpdates.Check(svc.Current).Wait(10000), Is.True);
            Assert.That(r.ContentUpdates.Version, Is.EqualTo(newest));
            Assert.That(r.Sim.Db, Is.SameAs(db0), "the sim's ContentDb is the same object after a refresh announcing a newer bundle");
            Assert.That(r.Runner.Sim.Db, Is.SameAs(db0));
        }

        [Test]
        public void ACachedMaintenanceFlag_DoesNotBlockBoot_ALiveOneDoes()   // 01 section 8.1 step 2, 08 section 8.4
        {
            var maint = new MaintenanceInfo(true, "maint.db_upgrade", TestContent.T0 + 600);
            var cached = new ScriptedConfig(ScriptedConfig.Cfg(maintenance: maint, source: ConfigSource.Cached));
            Assert.That(BootFlow.Run(Boot(cached, new FakeMonoClock())).Outcome, Is.EqualTo(BootOutcome.Ready));
            var live = new ScriptedConfig(ScriptedConfig.Cfg(maintenance: maint));
            Assert.That(BootFlow.Run(Boot(live, new FakeMonoClock())).Outcome, Is.EqualTo(BootOutcome.Blocked));
        }

        [Test]
        public void AfterAuth_ConfigIsFetchedOnceMore_InTheBackground()   // 01 section 8.1 step 2 (rollout bucket, issue #31)
        {
            var svc = new ScriptedConfig(ScriptedConfig.Cfg());
            var o = Boot(svc, new FakeMonoClock());
            Assert.That(BootFlow.Run(o).Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(svc.Calls, Is.EqualTo(1), "a Local account has no JWT, so config is fetched once");

            svc = new ScriptedConfig(ScriptedConfig.Cfg());
            o = Boot(svc, new FakeMonoClock());
            var services = o.Services;
            o.Services = (db, state) => services(db, state) with { Account = new SignedInAccount() };
            Assert.That(BootFlow.Run(o).Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(svc.Calls, Is.EqualTo(2));
        }

        [Test]
        public void AConfigServiceThatNeverAnswers_HoldsBootAtMostTheFetchTimeout()   // issue #144 acceptance
        {
            var svc = new ScriptedConfig(ClientConfig.Defaults(TestContent.Db.Version)) { Hold = new TaskCompletionSource<ClientConfig>() };
            var sw = Stopwatch.StartNew();
            var r = BootFlow.Run(Boot(svc, new FakeMonoClock()));
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(RuntimeConfig.CONFIG_FETCH_TIMEOUT_S + 2)));
            Assert.That(r.Config.Source, Is.EqualTo(ConfigSource.Defaults));
        }
    
        /// <summary>An account that signs in at once, so boot step 3 yields a JWT.</summary>
        sealed class SignedInAccount : HeroCat.Runtime.Account.IAccountService
        {
            static readonly Guid Player = new Guid("00000000-0000-0000-0000-0000000000a1");
            public Guid PlayerId => Player;
            public bool HasSession => true;
            public Task<HeroCat.Runtime.Account.SignInResult> SignInAsync(CancellationToken ct = default) =>
                Task.FromResult(new HeroCat.Runtime.Account.SignInResult(HeroCat.Runtime.Account.SignInStatus.SignedIn, Player));
            public Task<bool> LogoutAsync(CancellationToken ct = default) => Task.FromResult(true);
            public Task<long> DeleteAccountAsync(CancellationToken ct = default) => Task.FromResult(0L);
            public event Action<HeroCat.Runtime.Account.SignInResult> SessionLost { add { } remove { } }
        }
    }
}
