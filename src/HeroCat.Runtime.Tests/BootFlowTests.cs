using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>BootFlow (01 section 8.1, section 6 degrade rules; issue #70).</summary>
    public class BootFlowTests
    {
        static BootOptions Options(FakeClock clock, ISaveSlotStore store, Func<ContentDb>[] content = null,
                                   Func<GameServices, GameServices> tweak = null, MemoryAnalytics analytics = null)
        {
            return new BootOptions
            {
                Content = content ?? new Func<ContentDb>[] { () => TestContent.Db },
                Services = (db, state) =>
                {
                    var svc = GameServices.Build(BackendMode.Local, new ServicesOptions
                    {
                        Clock = clock, Slots = store, State = state, Content = db, Analytics = analytics ?? new MemoryAnalytics(),
                        InstallId = TestContent.Device,
                    });
                    return tweak != null ? tweak(svc) : svc;
                },
                NewProfileRngRoot = TestContent.Root,
                Runner = new SessionRunnerOptions { Mono = new FakeMonoClock(), TickBudgetMs = 0 },
                CommandLog = new ListCommandLog(),
                Mono = new FakeMonoClock(),
            };
        }

        [Test]
        public void CorruptBundledContent_AndNoCache_IsFatalContentCorrupt()   // issue #70 acceptance 1
        {
            bool servicesBuilt = false;
            var o = Options(new FakeClock(TestContent.T0), new MemorySaveStore());
            var build = o.Services;
            o.Services = (db, s) => { servicesBuilt = true; return build(db, s); };
            o.Content = new Func<ContentDb>[]
            {
                () => throw new InvalidDataException("cached bundle: table hash mismatch"),
                () => throw new InvalidDataException("bundled: table hash mismatch"),
            };
            var r = BootFlow.Run(o);
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Fatal));
            Assert.That(r.Error.Code, Is.EqualTo(ErrorClassifier.CONTENT_CORRUPT));
            Assert.That(r.Error.Class, Is.EqualTo(ErrorClass.Fatal));
            Assert.That(r.Error.LocalizationKey, Is.EqualTo("err.content_corrupt"));
            Assert.That(r.Error.TitleKey, Is.EqualTo("err.content_corrupt.title"));
            Assert.That(r.Error.BodyKey, Is.EqualTo("err.content_corrupt.body"));
            Assert.That(r.Error.SupportId, Is.Not.Empty);
            Assert.That(servicesBuilt, Is.False, "nothing after step 1 runs");
            Assert.That(r.Runner, Is.Null);
        }

        [Test]
        public void ACachedBundleThatFails_FallsBackToTheBundledOne()   // 01 section 8.1 step 1
        {
            var o = Options(new FakeClock(TestContent.T0), new MemorySaveStore());
            o.Content = new Func<ContentDb>[] { () => throw new InvalidDataException("bad cache"), () => TestContent.Db };
            var r = BootFlow.Run(o);
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(r.Content, Is.SameAs(TestContent.Db));
        }

        [Test]
        public void MissingSave_CreatesAProfile_WithExactlyTutorialDiamonds_ReasonTutorial()   // issue #70 acceptance 2
        {
            var store = new MemorySaveStore();
            var analytics = new MemoryAnalytics();
            var r = BootFlow.Run(Options(new FakeClock(TestContent.T0), store, analytics: analytics));
            var s = r.Sim.State;
            var expected = TestContent.Db.ProfileCreatedGrants.Where(g => g.Currency == Currency.DIAMOND).Sum(g => g.Amount);
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(r.IsNewProfile, Is.True);
            Assert.That(r.LoadStatus, Is.EqualTo(LoadStatus.NewProfile));
            Assert.That(s.Stats.ClientDiamondsEarned[CurrencyReason.TUTORIAL], Is.EqualTo(expected));
            Assert.That(s.Tutorial.Step, Is.EqualTo(0));
            Assert.That(r.ShowTutorial, Is.True);
            Assert.That(store.Primary, Is.Not.Null, "boot saves at once (step 5)");
            Assert.That(analytics.Events.Select(e => e.Name), Does.Contain("profile_created").And.Contain("session_start"));
            var grant = analytics.Events.Where(e => e.Name == "currency_changed").ToList();
            Assert.That(grant, Has.Count.EqualTo(1), "the welcome grant's currency_changed row (issue #246 item 4)");
            Assert.That(grant[0].Props["reason"], Is.EqualTo("TUTORIAL"));
            Assert.That(r.SaveResetNotice, Is.Null, "no save existed: nothing to tell the player");
            Assert.That(analytics.Count("save_corrupt_all"), Is.EqualTo(0));
        }

        [Test]
        public void NoReadableSaveCopy_IsANewProfile_WithAOneTimeNotice_NotFatal()   // 02 section 8.3, 00 decision 103 (issue #248)
        {
            var store = new MemorySaveStore { Primary = new byte[] { 1, 2, 3 }, Backup = new byte[] { 4, 5, 6 } };
            var clock = new FakeClock(TestContent.T0);
            var analytics = new MemoryAnalytics();
            var o = Options(clock, store, analytics: analytics);
            var r = BootFlow.Run(o);
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(r.Error, Is.Null);
            Assert.That(r.IsNewProfile, Is.True);
            Assert.That(r.SaveResetNotice, Is.Not.Null);
            Assert.That(r.SaveResetNotice.Code, Is.EqualTo(ErrorClassifier.SAVE_CORRUPT_ALL));
            Assert.That(r.SaveResetNotice.SupportId, Is.EqualTo(new SupportId(o.SessionId, TestContent.T0).Format()));
            Assert.That(r.SaveResetNotice.TitleKey, Is.EqualTo("notice.save_corrupt_all.title"));
            Assert.That(analytics.Count("save_corrupt"), Is.EqualTo(1), "the local quarantine");
            Assert.That(analytics.Count("save_corrupt_all"), Is.EqualTo(1));
            Assert.That(analytics.Events.Single(e => e.Name == "save_corrupt_all").Props["supportId"], Is.EqualTo(r.SaveResetNotice.SupportId));
            Assert.That(store.Quarantined, Has.Count.EqualTo(2), "both copies kept for support");

            r.Runner.OnPause();
            var again = BootFlow.Run(Options(clock, store));
            Assert.That(again.LoadStatus, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(again.SaveResetNotice, Is.Null, "shown once: the next boot loads the new save");
        }

        [Test]
        public void ThreeHourGap_StampsLastTickServerTime_BeforeHandoff_AndRunsNoTicks()   // issue #70 acceptance 3
        {
            var store = new MemorySaveStore();
            var clock = new FakeClock(TestContent.T0);
            var first = BootFlow.Run(Options(clock, store));
            first.Runner.OnPause();
            long tick = first.Sim.Tick;

            clock.Advance(3 * 3600);
            var o = Options(clock, store);
            var r = BootFlow.Run(o);
            var log = ((ListCommandLog)o.CommandLog).Records;
            Assert.That(r.LoadStatus, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(r.Sim.State.Time.LastTickServerTime, Is.EqualTo(clock.NowServerSeconds));
            Assert.That(log.Select(c => c.GetType().Name), Is.EqualTo(new[] { "ApplyServerTime", "ComputeOfflineCatchup" }));
            Assert.That(((ComputeOfflineCatchup)log[1]).NowServerSeconds, Is.EqualTo(clock.NowServerSeconds));
            Assert.That(r.Sim.Tick, Is.EqualTo(tick), "no tick between boot and the first SCR_MAIN frame");
            Assert.That(TestContent.Store(store, clock).Load(clock.NowServerSeconds, TestContent.Db).State.Time.LastTickServerTime,
                        Is.EqualTo(clock.NowServerSeconds), "the stamp is saved before handoff");
            // TODO(#108): with the catch-up compute in place, also assert Offline.Pending covers 3 h (credited >= OFFLINE_MIN_S).
        }

        [Test]
        public void PendingOfflineReward_OpensTheModal_AndKeepsTicksOff()   // 01 section 8.1 step 6, 8.7 step 5
        {
            var store = new MemorySaveStore();
            var clock = new FakeClock(TestContent.T0);
            var first = BootFlow.Run(Options(clock, store));
            first.Sim.State.Offline.Pending = new CatchupResult(3600, 3600, false, 10, 5, 0, 1, 1, TestContent.T0);
            first.Runner.SaveNow(SaveTrigger.Pause);

            var o = Options(clock, store);
            var r = BootFlow.Run(o);
            Assert.That(r.OpenOfflineReward, Is.True);
            Assert.That(r.Runner.TicksEnabled, Is.False);
            long tick = r.Sim.Tick;
            ((FakeMonoClock)o.Runner.Mono).Advance(1.0);
            r.Runner.OnFrame();
            Assert.That(r.Sim.Tick, Is.EqualTo(tick));
        }

        [Test]
        public void TooNewSave_IsFatalSaveTooNew_AndNothingIsWritten()   // 02 section 7.1, 01 section 7
        {
            var body = SaveSerializer.ToJson(TestContent.NewState());
            body["SchemaVersion"] = SaveSchema.Current + 1;
            var store = new MemorySaveStore { Primary = SaveSerializer.EncodeBody(body.ToString()) };
            var r = BootFlow.Run(Options(new FakeClock(TestContent.T0), store));
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Fatal));
            Assert.That(r.Error.Code, Is.EqualTo(ErrorClassifier.SAVE_TOO_NEW));
            Assert.That(store.WriteCount, Is.EqualTo(0));
        }

        [Test]
        public void Maintenance_AndClientTooOld_Block_BeforeTheSaveLoads()   // 01 section 6, 8.12 step 4
        {
            var store = new MemorySaveStore();
            var maint = new ClientConfig("", null, new MaintenanceInfo(true, "maint.default", TestContent.T0 + 600), KillSwitches.AllOn);
            var r = BootFlow.Run(Options(new FakeClock(TestContent.T0), store, tweak: s => s with { Config = new FixedConfig(maint) }));
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Blocked));
            Assert.That(r.Error.Code, Is.EqualTo("MAINTENANCE"));
            Assert.That(r.Error.Class, Is.EqualTo(ErrorClass.Maintenance));
            Assert.That(store.WriteCount, Is.EqualTo(0));

            var gate = new ClientConfig("", "2.0.0", MaintenanceInfo.None, KillSwitches.AllOn);
            var o = Options(new FakeClock(TestContent.T0), store, tweak: s => s with { Config = new FixedConfig(gate) });
            o.ClientVersion = "1.4.0";
            r = BootFlow.Run(o);
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Blocked));
            Assert.That(r.Error.Code, Is.EqualTo("CLIENT_TOO_OLD"));
            o.ClientVersion = "2.0.0";
            Assert.That(BootFlow.Run(o).Outcome, Is.EqualTo(BootOutcome.Ready));
        }

        [Test]
        public void StepTimings_AreReportedInOrder_AndLoggedToAnalytics()   // issue #70: log the step timings
        {
            var analytics = new MemoryAnalytics();
            var steps = new System.Collections.Generic.List<BootStep>();
            var r = BootFlow.Run(Options(new FakeClock(TestContent.T0), new MemorySaveStore(), analytics: analytics), steps.Add);
            var all = new[] { BootStep.Content, BootStep.Config, BootStep.Auth, BootStep.Save, BootStep.CatchUp, BootStep.Handoff };
            Assert.That(steps, Is.EqualTo(all));
            Assert.That(r.Timings.Select(t => t.Step), Is.EqualTo(all));
            Assert.That(analytics.Events.Count(e => e.Name == "boot_step"), Is.EqualTo(all.Length));
        }

        [Test]
        public void LoadedSave_FloorsTheLocalClock_AtLastSavedServerTime()   // 02 section 11.2 rule 4
        {
            var store = new MemorySaveStore();
            var clock = new FakeClock(TestContent.T0 + 1000);
            BootFlow.Run(Options(clock, store)).Runner.OnPause();

            clock.Set(TestContent.T0);   // the device clock went back
            var serverClock = new ServerClock(clock);
            var o = Options(clock, store);
            var build = o.Services;
            o.Services = (db, st) => GameServices.Build(BackendMode.Local, new ServicesOptions
            {
                Clock = serverClock, Slots = store, State = st, Content = db, InstallId = TestContent.Device,
            });
            var r = BootFlow.Run(o);
            Assert.That(serverClock.NowServerSeconds, Is.EqualTo(TestContent.T0 + 1000));
            Assert.That(r.Sim.State.Time.LastTickServerTime, Is.EqualTo(TestContent.T0 + 1000));
        }

        sealed class FixedConfig : IConfigService
        {
            public FixedConfig(ClientConfig c) { Current = c; }
            public ClientConfig Current { get; }
            public Task<ClientConfig> FetchAsync(CancellationToken ct = default) => Task.FromResult(Current);
        }
    }
}
