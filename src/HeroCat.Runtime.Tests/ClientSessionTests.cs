using System;
using System.Linq;
using HeroCat.Client.Core;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The client's engine-free session host over BootFlow + SessionRunner (issue #65: SimRunnerCore is gone).</summary>
    public class ClientSessionTests
    {
        static ClientSession Start(FakeClock clock, MemorySaveStore store, FakeMonoClock mono, Func<ContentDb> content = null) =>
            ClientSession.Start(new[] { content ?? (() => TestContent.Db) },
                                (db, state) => GameServices.Build(BackendMode.Local, new ServicesOptions { Clock = clock, Saves = store, State = state, Content = db }),
                                runner: new SessionRunnerOptions { Mono = mono, TickBudgetMs = 0 });

        [Test]
        public void Boots_Ticks_RefreshesTheVm_AndDebugControlsGoThroughTheRunner()
        {
            var clock = new FakeClock(TestContent.T0);
            var store = new MemorySaveStore();
            var mono = new FakeMonoClock();
            var cs = Start(clock, store, mono);
            Assert.That(cs.IsReady, Is.True);
            int hz = TestContent.Db.Constants.TickHz;

            mono.Advance(1.0);
            Assert.That(cs.Frame(), Is.EqualTo(hz));

            cs.Vm.DebugSetTimeScale(5);
            mono.Advance(1.0);
            Assert.That(cs.Frame(), Is.EqualTo(5 * hz));
            cs.Vm.DebugSetTimeScale(1);

            long before = cs.Runner.Sim.Tick;
            cs.Vm.DebugRunHours(1);
            Assert.That(cs.Runner.Sim.Tick - before, Is.EqualTo(3600L * hz));
            Assert.That(cs.Clock.OffsetSeconds, Is.EqualTo(3600));

            int writes = store.WriteCount;
            cs.Vm.DebugRequestSave();
            cs.Frame();
            Assert.That(store.WriteCount, Is.EqualTo(writes + 1));
        }

        [Test]
        public void Rejects_ToastTheirKey_ViaTheBus()
        {
            var cs = Start(new FakeClock(TestContent.T0), new MemorySaveStore(), new FakeMonoClock());
            var r = cs.Vm.Apply(new ApplyServerTime(-1));
            Assert.That(r.Ok, Is.False);
            Assert.That(cs.Vm.Toasts.Current(0), Does.Contain(ErrorClassifier.RejectKey(r)));
        }

        [Test]
        public void BadContent_IsNotReady_AndCarriesTheFatalError()
        {
            var cs = Start(new FakeClock(TestContent.T0), new MemorySaveStore(), new FakeMonoClock(), () => throw new InvalidOperationException("broken"));
            Assert.That(cs.IsReady, Is.False);
            Assert.That(cs.Boot.Error.Code, Is.EqualTo(ErrorClassifier.CONTENT_CORRUPT));
            Assert.That(cs.Frame(), Is.EqualTo(0));
            cs.OnPause();
        }

        [Test]
        public void DeleteProfile_ThenStart_CreatesANewProfile()
        {
            var clock = new FakeClock(TestContent.T0);
            var store = new MemorySaveStore();
            var a = Start(clock, store, new FakeMonoClock());
            a.OnPause();
            a.DeleteProfile();
            Assert.That(store.Primary, Is.Null);
            var b = Start(clock, store, new FakeMonoClock());
            Assert.That(b.Boot.IsNewProfile, Is.True);
        }
    }
}
