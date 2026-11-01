using System;
using System.IO;
using System.Linq;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Events;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>02 section 11 clock seam (issue #78): the floored local clock and acceptance 15; plus the install id (#73).</summary>
    public class ClockSeamTests : TempDirTest
    {
        static long Day => TestContent.Db.Constants.DaySeconds;

        [Test]
        public void SystemClock_IsFloored_NeverAuthoritative_FloorOnlyRises()
        {
            long device = TestContent.T0;
            var c = new SystemClock { DeviceSeconds = () => device };
            Assert.That(c.NowServerSeconds, Is.EqualTo(TestContent.T0));
            c.SetFloor(TestContent.T0 + 365 * Day);   // device clock a year behind the last save
            Assert.That(c.NowServerSeconds, Is.EqualTo(TestContent.T0 + 365 * Day));
            c.SetFloor(TestContent.T0);
            Assert.That(c.Floor, Is.EqualTo(TestContent.T0 + 365 * Day), "never lowered");
            device = TestContent.T0 + 400 * Day;
            Assert.That(c.NowServerSeconds, Is.EqualTo(device), "device time once it passes the floor");
            Assert.That(c.IsAuthoritative, Is.False);
        }

        [Test]
        public void Session_BeforeAnyObservation_NowIsAtLeastTheLastSave()   // #78 acceptance
        {
            var slots = new MemorySaveStore();
            var saved = TestContent.NewState();
            saved.Time.LastSavedServerTime = TestContent.T0 + 365 * Day;
            slots.Primary = SaveSerializer.Encode(saved);
            var clock = new SystemClock { DeviceSeconds = () => TestContent.T0 };
            var session = TestSession.Open(store: slots, clockOverride: clock);
            Assert.That(clock.NowServerSeconds, Is.GreaterThanOrEqualTo(saved.Time.LastSavedServerTime));
            Assert.That(session.Sim.State.Time.LastSavedServerTime, Is.GreaterThanOrEqualTo(saved.Time.LastSavedServerTime));
        }

        [Test]
        public void Acceptance15_DayKeyBehind_ResetsNothing_EmitsClockAnomalyOncePerSession()
        {
            var clock = new FakeClock(TestContent.T0 + Day / 2);
            var analytics = new MemoryAnalytics();
            var session = TestSession.Open(clock, analytics: analytics);
            var state = session.Sim.State;
            int dayKey = state.Time.DayKey;
            long diamonds = state.Wallet.Diamond;
            var rolled = new System.Collections.Generic.List<DayRolledOver>();
            session.Bus.Subscribe<DayRolledOver>(rolled.Add);

            session.Runner.OnPause();
            clock.Set(TestContent.T0 - Day);   // dayKey(now) < DayKey
            session.Runner.OnResume();
            Assert.That(state.Time.DayKey, Is.EqualTo(dayKey), "no reset");
            Assert.That(state.Wallet.Diamond, Is.EqualTo(diamonds), "no second login grant");
            Assert.That(rolled, Is.Empty);
            Assert.That(analytics.Count("clock_anomaly"), Is.EqualTo(1));
            Assert.That(analytics.Events.Single(e => e.Name == "clock_anomaly").Props["kind"], Is.EqualTo("backwards"));

            session.Runner.OnPause();
            clock.Set(TestContent.T0 - 2 * Day);
            session.Runner.OnResume();
            Assert.That(analytics.Count("clock_anomaly"), Is.EqualTo(1), "once per session");
        }

        [Test]
        public void Acceptance15_NowBehindLastTick_CreditsZero_AnchorKept()
        {
            var clock = new FakeClock(TestContent.T0);
            var analytics = new MemoryAnalytics();
            var session = TestSession.Open(clock, analytics: analytics);
            var state = session.Sim.State;
            clock.Advance(1000);
            session.Runner.DebugAdvance(1000L * TestContent.Db.Constants.TickHz);   // the sim stamps the anchor as it ticks
            session.Runner.OnPause();                         // the window up to T0+1000 was ticked
            long anchor = state.Time.LastTickServerTime;
            Assert.That(anchor, Is.EqualTo(TestContent.T0 + 1000));
            double gold = state.Wallet.Gold;

            clock.Set(TestContent.T0 + 400);
            session.Runner.OnResume();
            Assert.That(state.Time.LastTickServerTime, Is.EqualTo(anchor), "the anchor never moves back");
            Assert.That(state.Offline.Pending, Is.Null, "nothing credited");
            Assert.That(state.Wallet.Gold, Is.EqualTo(gold));
            Assert.That(analytics.Count("clock_anomaly"), Is.EqualTo(1));
        }

        [Test]
        public void ResumeBehindByAtMostTheTolerance_IsJitter_NoClockAnomaly()   // 01 section 3.1, issue #246 item 5
        {
            var clock = new FakeClock(TestContent.T0);
            var analytics = new MemoryAnalytics();
            var session = TestSession.Open(clock, analytics: analytics);
            int tol = TestContent.Db.Constants.ClockBackwardsToleranceS;
            clock.Advance(1000);
            session.Runner.DebugAdvance(1000L * TestContent.Db.Constants.TickHz);   // ticked up to T0+1000
            session.Runner.OnPause();
            clock.Set(TestContent.T0 + 1000 - tol);
            session.Runner.OnResume();
            Assert.That(analytics.Count("clock_anomaly"), Is.EqualTo(0), "within CLOCK_BACKWARDS_TOLERANCE_S");

            session.Runner.OnPause();
            clock.Set(TestContent.T0 + 1000 - tol - 1);
            session.Runner.OnResume();
            Assert.That(analytics.Count("clock_anomaly"), Is.EqualTo(1), "past it");
        }

        // ------------------------------------------------------------------------------------------- install id

        [Test]
        public void InstallId_IsCreatedOnce_AndReused()
        {
            var a = InstallId.LoadOrCreate(Dir);
            Assert.That(a, Is.Not.EqualTo(Guid.Empty));
            Assert.That(File.ReadAllText(Path.Combine(Dir, InstallId.FileName)), Is.EqualTo(a.ToString("D")));
            Assert.That(InstallId.LoadOrCreate(Dir), Is.EqualTo(a));
        }

        [Test]
        public void InstallId_MissingOrUnreadable_IsANewDevice()
        {
            var a = InstallId.LoadOrCreate(Dir);
            File.Delete(Path.Combine(Dir, InstallId.FileName));
            var b = InstallId.LoadOrCreate(Dir);
            Assert.That(b, Is.Not.EqualTo(a), "a restored install is a new device (02 section 8.4)");
            File.WriteAllText(Path.Combine(Dir, InstallId.FileName), "not a guid");
            var c = InstallId.LoadOrCreate(Dir);
            Assert.That(c, Is.Not.EqualTo(b));
            Assert.That(InstallId.LoadOrCreate(Dir), Is.EqualTo(c));
        }

        [Test]
        public void LocalServices_StampTheInstallIdIntoDeviceId()
        {
            var services = GameServices.Local(Dir, analytics: new MemoryAnalytics());
            Assert.That(services.InstallId, Is.EqualTo(InstallId.LoadOrCreate(Dir)));
            var boot = HeroCat.Runtime.Boot.BootFlow.Run(new HeroCat.Runtime.Boot.BootOptions
            {
                Content = new Func<HeroCat.Sim.Content.ContentDb>[] { () => TestContent.Db },
                Services = (db, state) => GameServices.Local(Dir, state, db, new MemoryAnalytics()),
                NewProfileRngRoot = TestContent.Root,
            });
            Assert.That(boot.Services.InstallId, Is.EqualTo(services.InstallId));
            Assert.That(boot.Sim.State.DeviceId, Is.EqualTo(services.InstallId));
        }
    }
}
