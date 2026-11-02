using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>SessionRunner (01 section 3; issue #65) and the specs/10 section 1 SessionRunner rows.</summary>
    public class SessionRunnerTests
    {
        static int Hz => TestContent.Db.Constants.TickHz;

        [Test]
        public void NewProfile_IsCreated_Granted_LoggedIn_AndSaved()
        {
            var store = new MemorySaveStore();
            var s = TestSession.Open(store: store);
            var k = TestContent.Db.Constants;
            Assert.That(s.Boot.IsNewProfile, Is.True);
            Assert.That(store.WriteCount, Is.EqualTo(1), "boot saves once after the catch-up");
            Assert.That(s.Sim.State.Wallet.Diamond, Is.EqualTo(1500 + k.LoginDiamonds));
            Assert.That(s.Sim.State.Time.DayKey, Is.EqualTo(TestContent.T0 / k.DaySeconds));
            Assert.That(s.Log.Records.Select(c => c.GetType().Name), Is.EqualTo(new[] { "ApplyServerTime", "ComputeOfflineCatchup" }));
        }

        [Test]
        public void Reopen_LoadsTheSave_AndDoesNotReGrant()
        {
            var store = new MemorySaveStore();
            var clock = new FakeClock(TestContent.T0);
            var a = TestSession.Open(clock, store);
            a.Play(10);
            a.Runner.OnPause();
            long diamonds = a.Sim.State.Wallet.Diamond;
            long tick = a.Sim.Tick;
            clock.Advance(3600);
            var b = TestSession.Open(clock, store);
            Assert.That(b.Boot.IsNewProfile, Is.False);
            Assert.That(b.Boot.LoadStatus, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(b.Sim.State.Wallet.Diamond, Is.EqualTo(diamonds), "same day: no second login grant");
            Assert.That(b.Sim.Tick, Is.EqualTo(tick), "no tick runs during boot (01 section 8.1 step 6)");
        }

        [Test]
        public void Frames_TickAtTickHz_WithThePerFrameCap()
        {
            var s = TestSession.Open();
            Assert.That(s.Frame(1.0), Is.EqualTo(Hz));
            Assert.That(s.Frame(20.0), Is.EqualTo(RuntimeConfig.MAX_TICKS_PER_FRAME));
            Assert.That(s.Frame(0.05), Is.EqualTo(RuntimeConfig.MAX_TICKS_PER_FRAME), "the rest of the 20 s carries");
        }

        [Test]
        public void Gap25s_IsTickedOverAtMostThreeFrames()   // 01 AC 4, first half (TICK_BUDGET_MS disabled)
        {
            var s = TestSession.Open();
            long t0 = s.Sim.Tick;
            int frames = 0;
            s.Frame(25.0);
            frames++;
            while (s.Sim.Tick - t0 < 25 * Hz && frames < 10)
            {
                s.Frame(0);
                frames++;
            }
            Assert.That(s.Sim.Tick - t0, Is.EqualTo(25 * Hz));
            Assert.That(frames, Is.LessThanOrEqualTo(3));
            Assert.That(s.Log.Records.OfType<ComputeOfflineCatchup>().Count(), Is.EqualTo(1), "only boot's");
        }

        [Test]
        public void Gap31s_RunsNoTicks_AndOneLoggedCatchupCovering31s()   // 01 AC 4, second half
        {
            var s = TestSession.Open();
            s.Frame(1.0);
            long t0 = s.Sim.Tick;
            long lastTick = s.Sim.State.Time.LastTickServerTime;
            int logged = s.Log.Count;
            Assert.That(s.Frame(31.0), Is.EqualTo(0));
            Assert.That(s.Sim.Tick, Is.EqualTo(t0));
            var after = s.Log.Records.Skip(logged).ToList();
            var catchup = after.OfType<ComputeOfflineCatchup>().Single();
            Assert.That(after.Select(c => c.GetType().Name), Is.EqualTo(new[] { "ApplyServerTime", "ComputeOfflineCatchup" }));
            Assert.That(catchup.NowServerSeconds - lastTick, Is.EqualTo(31), "the 31 s gap; the ticked 1 s frame moved the anchor");
            Assert.That(s.Frame(0.1), Is.EqualTo(1), "ticking resumes from a clean accumulator");
        }

        [Test]
        public void TickBudget_StopsTheLoop_AndKeepsTheRemainder()   // 01 section 3.2
        {
            var s = TestSession.Open(configure: o => o.TickBudgetMs = RuntimeConfig.TICK_BUDGET_MS);
            s.Mono.AutoStepTicks = FakeMonoClock.TicksPerSecond / 1000;   // every read of the clock costs 1 ms
            long t0 = s.Sim.Tick;
            int n = s.Frame(10.0);
            Assert.That(n, Is.GreaterThan(0).And.LessThan(RuntimeConfig.MAX_TICKS_PER_FRAME));
            s.Mono.AutoStepTicks = 0;
            int m = s.Frame(0);
            Assert.That(s.Sim.Tick - t0, Is.EqualTo(n + m));
            Assert.That(n + m, Is.GreaterThan(n), "the remainder stayed in the accumulator");
        }

        [Test]
        public void TicksDisabled_NoTicks_ButAutosaveCommandsAndEventsContinue()   // 01 section 8.7 step 5
        {
            var store = new MemorySaveStore();
            var s = TestSession.Open(store: store);
            var events = new List<Event>();
            s.Bus.Subscribe<Event>(events.Add);
            s.Runner.TicksEnabled = false;
            long t0 = s.Sim.Tick;
            int writes = store.WriteCount;

            s.Runner.Enqueue(new ApplyServerTime(s.Clock.NowServerSeconds + 86400));   // a command that emits events
            s.Play(RuntimeConfig.AUTOSAVE_INTERVAL_SEC + 1);
            Assert.That(s.Sim.Tick, Is.EqualTo(t0), "no ticks");
            Assert.That(events.OfType<DayRolledOver>().Count(), Is.EqualTo(1), "events still published");
            Assert.That(store.WriteCount, Is.GreaterThan(writes), "autosave still runs (the command made the state dirty)");

            s.Runner.TicksEnabled = true;
            Assert.That(s.Frame(1.0), Is.EqualTo(Hz), "time spent with ticks off is not ticked afterwards");
        }

        [Test]
        public void Autosave_EveryIntervalWhileDirty_AndNotWhenClean()   // 10 section 1 "autosave cadence"
        {
            var store = new MemorySaveStore();
            var s = TestSession.Open(store: store);
            int w0 = store.WriteCount;
            s.Play(RuntimeConfig.AUTOSAVE_INTERVAL_SEC - 1);
            Assert.That(store.WriteCount, Is.EqualTo(w0));
            s.Play(2);
            Assert.That(store.WriteCount, Is.EqualTo(w0 + 1));
            s.Play(RuntimeConfig.AUTOSAVE_INTERVAL_SEC);
            Assert.That(store.WriteCount, Is.EqualTo(w0 + 2));

            s.Runner.TicksEnabled = false;   // nothing changes: clean, so no save
            s.Play(RuntimeConfig.AUTOSAVE_INTERVAL_SEC + 1);
            int clean = store.WriteCount;
            s.Play(RuntimeConfig.AUTOSAVE_INTERVAL_SEC * 2 + 1);
            Assert.That(store.WriteCount - clean, Is.LessThanOrEqualTo(2), "only the clock-sync re-anchors dirty it");
        }

        [Test]
        public void ApplyServerTime_IsLoggedEveryWallClockSync_OfForegroundTime()   // 00 contract 7, issue #5
        {
            var s = TestSession.Open();
            int before = s.Log.Records.OfType<ApplyServerTime>().Count();
            s.Play(RuntimeConfig.WALL_CLOCK_SYNC_SEC * 3);
            var syncs = s.Log.Records.OfType<ApplyServerTime>().Skip(before).ToList();
            Assert.That(syncs.Count, Is.EqualTo(3));
            Assert.That(s.Sim.DrainEvents().OfType<ClockWentBackwards>(), Is.Empty, "the virtual clock kept pace");
            Assert.That(s.Sim.VirtualNowServerSeconds, Is.EqualTo(s.Clock.NowServerSeconds));
        }

        [Test]
        public void PauseResume_SavesOnPause_FreezesFrames_AndCatchesUpOnResume()   // 01 section 3.3, 8.2
        {
            var store = new MemorySaveStore();
            var s = TestSession.Open(store: store);
            s.Play(5);
            int w = store.WriteCount;
            s.Runner.OnPause();
            Assert.That(store.WriteCount, Is.EqualTo(w + 1), "the pause save is synchronous");
            long tick = s.Sim.Tick;
            s.Mono.Advance(3 * 86400);
            s.Clock.Advance(3 * 86400);
            Assert.That(s.Runner.OnFrame(), Is.EqualTo(0), "no frames while paused");
            long d0 = s.Sim.State.Wallet.Diamond;
            s.Runner.OnResume();
            Assert.That(s.Sim.Tick, Is.EqualTo(tick), "a resume never ticks the gap");
            Assert.That(s.Sim.State.Wallet.Diamond, Is.EqualTo(d0 + TestContent.Db.Constants.LoginDiamonds), "lazy single reset");
            Assert.That(s.Sim.State.Time.LastTickServerTime, Is.EqualTo(s.Clock.NowServerSeconds), "ComputeOfflineCatchup stamped it");
            Assert.That(store.WriteCount, Is.EqualTo(w + 2), "resume saves");
            Assert.That(s.Sim.State.Offline.Pending, Is.Not.Null, "the 3-day gap is a pending reward (06 section 7.1)");
            Assert.That(s.Frame(1.0), Is.EqualTo(0), "ticks are held while the reward is pending (decision 53)");
            s.Runner.Enqueue(new ClaimOfflineReward());
            s.Frame(1.0);
            Assert.That(s.Sim.State.Offline.Pending, Is.Null);
            Assert.That(s.Frame(1.0), Is.EqualTo(Hz));
        }

        [Test]
        public void QuitAndLowMemory_Save()   // 01 section 3.3
        {
            var store = new MemorySaveStore();
            var s = TestSession.Open(store: store);
            int w = store.WriteCount;
            s.Runner.OnLowMemory();
            s.Runner.OnQuit();
            Assert.That(store.WriteCount, Is.EqualTo(w + 2));
        }

        [Test]
        public void SaveFailures_RaiseSaveWriteFailed_AtTheWarnCount()   // 01 section 3.1
        {
            var store = new MemorySaveStore();
            var s = TestSession.Open(store: store);
            var notices = new List<SaveWriteFailed>();
            s.Bus.SubscribeNotice<SaveWriteFailed>(notices.Add);
            store.FailWrites = true;
            for (int i = 0; i < RuntimeConfig.SAVE_FAIL_WARN_COUNT + 1; i++) Assert.That(s.Runner.SaveNow(SaveTrigger.Periodic), Is.False);
            Assert.That(notices.Select(n => n.ConsecutiveFailures), Is.EqualTo(new[] { RuntimeConfig.SAVE_FAIL_WARN_COUNT }));
            Assert.That(s.Analytics.Count("save_write_failed"), Is.EqualTo(RuntimeConfig.SAVE_FAIL_WARN_COUNT + 1), "the store tracks each failure (02 section 14.3)");
            store.FailWrites = false;
            Assert.That(s.Runner.SaveNow(SaveTrigger.Periodic), Is.True);
            Assert.That(s.Runner.ConsecutiveSaveFailures, Is.EqualTo(0));
        }

        [Test]
        public void HandlerEnqueue_NeverNestsApply_AndLandsNextFrame()   // 01 AC 12
        {
            var s = TestSession.Open();
            var applied = new List<long>();
            int frame = 0;
            s.Bus.Subscribe<DayRolledOver>(e =>
            {
                // A handler may only queue. Issue from inside a drain is refused (the re-entrancy guard of AC 12).
                Assert.Throws<InvalidOperationException>(() => s.Runner.Issue(new ApplyServerTime(1)));
                s.Runner.Enqueue(new ComputeOfflineCatchup(s.Clock.NowServerSeconds));
                applied.Add(frame);
            });
            int before = s.Log.Records.OfType<ComputeOfflineCatchup>().Count();
            s.Runner.Enqueue(new ApplyServerTime(s.Clock.NowServerSeconds + 86400));
            frame = 1;
            s.Frame(0.1);
            Assert.That(applied, Is.EqualTo(new[] { 1L }));
            Assert.That(s.Log.Records.OfType<ComputeOfflineCatchup>().Count(), Is.EqualTo(before), "not in the same frame");
            Assert.That(s.Runner.PendingCount, Is.EqualTo(1));
            frame = 2;
            s.Frame(0.1);
            Assert.That(s.Log.Records.OfType<ComputeOfflineCatchup>().Count(), Is.EqualTo(before + 1), "applied on the next frame");
        }

        [Test]
        public void Rejects_ArePublished_AsCommandRejected()
        {
            var s = TestSession.Open();
            var rejects = new List<CommandRejected>();
            s.Bus.SubscribeNotice<CommandRejected>(rejects.Add);
            s.Runner.Enqueue(new ApplyServerTime(-1));
            s.Frame(0.1);
            Assert.That(rejects.Single().Result.Code, Is.EqualTo(RejectCode.BadArgument));
            Assert.That(rejects.Single().Command, Is.TypeOf<ApplyServerTime>());
        }

        [Test]
        public void EventBus_DispatchesInSubscriptionOrder_ByType_AndUnsubscribes()   // 01 section 2.3
        {
            var bus = new EventBus();
            var seen = new List<string>();
            var a = bus.Subscribe<Event>(e => seen.Add("any:" + e.GetType().Name));
            bus.Subscribe<DayRolledOver>(e => seen.Add("day:" + e.DayKey));
            bus.Subscribe<CurrencyChanged>(e => seen.Add("cc:" + e.Reason));
            bus.Publish(new Event[] { new DayRolledOver(3), new CurrencyChanged(Currency.DIAMOND, 50, 50, CurrencyReason.LOGIN) });
            Assert.That(seen, Is.EqualTo(new[] { "any:DayRolledOver", "day:3", "any:CurrencyChanged", "cc:LOGIN" }));
            seen.Clear();
            a.Dispose();
            bus.Publish(new Event[] { new DayRolledOver(4) });
            Assert.That(seen, Is.EqualTo(new[] { "day:4" }));
        }

        [Test]
        public void EventBus_IsMainThreadOnly_AndTheDispatcherDeliversOnTheMainThread()   // 01 section 4
        {
            var bus = new EventBus();
            Exception fromWorker = null;
            var t = new Thread(() =>
            {
                try { bus.Publish(new Event[] { new DayRolledOver(1) }); }
                catch (Exception e) { fromWorker = e; }
            });
            t.Start();
            t.Join();
            Assert.That(fromWorker, Is.TypeOf<InvalidOperationException>());

            var dispatcher = new MainThreadDispatcher();
            var s = TestSession.Open(configure: o => o.Dispatcher = dispatcher);
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int ranOn = -1;
            var worker = new Thread(() => dispatcher.Post(() =>
            {
                ranOn = Thread.CurrentThread.ManagedThreadId;
                s.Runner.Enqueue(new ComputeOfflineCatchup(s.Clock.NowServerSeconds));   // results become commands
            }));
            worker.Start();
            worker.Join();
            Assert.That(ranOn, Is.EqualTo(-1), "nothing runs until the next frame");
            s.Frame(0.1);
            Assert.That(ranOn, Is.EqualTo(mainThread));
        }

        [Test]
        public void OnPauseSave_Of40KbState_IsUnder50ms()   // 01 AC 5 (.NET half; the device number is 10's perf test)
        {
            string dir = Path.Combine(Path.GetTempPath(), "herocat-tests", Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileSaveStore(dir);
                var s = TestSession.Open(store: store);
                Pad(s.Sim.State, 40 * 1024);
                s.Runner.SaveNow(SaveTrigger.Periodic);   // warm up JIT and the file
                Assert.That(new FileInfo(store.PrimaryPath).Length, Is.GreaterThanOrEqualTo(40 * 1024));
                double best = double.MaxValue;
                for (int i = 0; i < 5; i++)
                {
                    s.Runner.OnResume();
                    var sw = Stopwatch.StartNew();
                    s.Runner.OnPause();
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }
                Assert.That(best, Is.LessThan(50), "OnPause save of a 40 KB state");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        sealed class CountingAnalytics : IAnalytics, IAnalyticsUpload
        {
            public int Flushes, Uploads;
            public void Track(string name, IReadOnlyDictionary<string, object> props = null) { }
            public void Flush() => Flushes++;
            public void Upload() => Uploads++;
        }

        [Test]
        public void TheAnalyticsQueue_UploadsEveryAnalyticsUploadSec_OfForegroundTime_WithoutAFlush()
        {
            var a = new CountingAnalytics();
            var s = TestSession.Open(sink: a);
            Assert.That(RuntimeConfig.ANALYTICS_UPLOAD_SEC, Is.EqualTo(60));
            s.Play(59);
            Assert.That(a.Uploads, Is.Zero);
            s.Play(2);
            Assert.That((a.Uploads, a.Flushes), Is.EqualTo((1, 0)), "the interval uploads; only pause and quit raise the flush hook");
            s.Play(60);
            Assert.That(a.Uploads, Is.EqualTo(2));

            s.Runner.OnPause();
            Assert.That(a.Flushes, Is.EqualTo(1));
            s.Mono.Advance(3600);   // background time does not count
            s.Clock.Advance(3600);
            s.Runner.OnResume();
            s.Play(59);
            Assert.That(a.Uploads, Is.EqualTo(2));
            s.Play(2);
            Assert.That(a.Uploads, Is.EqualTo(3));
            s.Runner.OnQuit();
            Assert.That(a.Flushes, Is.EqualTo(2));
        }

        [Test]
        public void AZeroAnalyticsUploadSec_DisablesTheIntervalUpload()
        {
            var a = new CountingAnalytics();
            var s = TestSession.Open(sink: a, configure: o => o.AnalyticsUploadSec = 0);
            s.Play(600);
            Assert.That(a.Uploads, Is.Zero);
        }

        /// <summary>Grows the save to about <paramref name="bytes"/> with achievement progress rows (realistic map entries).</summary>
        static void Pad(GameState s, int bytes)
        {
            for (int i = 0; SaveSerializer.Encode(s).Length < bytes; i++)
                for (int j = 0; j < 50; j++)
                    s.Quests.Achievement["pad_achievement_" + i + "_" + j] = new QuestProgress { Progress = i * 50 + j };
        }
    }
}
