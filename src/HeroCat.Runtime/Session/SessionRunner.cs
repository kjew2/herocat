using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Summons;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Replay;

namespace HeroCat.Runtime.Session
{
    /// <summary>A monotonic clock: ticks and ticks per second. The frame loop and the tick budget read it.</summary>
    public interface IMonoClock
    {
        long Ticks { get; }
        long Frequency { get; }
    }

    /// <summary>Stopwatch time. SessionRunner.cs is one of the two Runtime files allowed to read it (02 section 11.2 rule 1).</summary>
    public sealed class StopwatchMonoClock : IMonoClock
    {
        public static readonly StopwatchMonoClock Instance = new StopwatchMonoClock();
        public long Ticks => Stopwatch.GetTimestamp();
        public long Frequency => Stopwatch.Frequency;
    }

    /// <summary>Tests and headless drivers: moves only when told. Optional auto-step per read to exercise the tick budget.</summary>
    public sealed class FakeMonoClock : IMonoClock
    {
        public const long TicksPerSecond = 10_000_000;
        long _ticks;

        public long Ticks
        {
            get
            {
                long t = _ticks;
                _ticks += AutoStepTicks;
                return t;
            }
        }

        public long Frequency => TicksPerSecond;

        /// <summary>Added after every read (0 by default).</summary>
        public long AutoStepTicks { get; set; }

        public void Advance(double seconds) => _ticks += (long)Math.Round(seconds * TicksPerSecond);
    }

    public sealed class SessionRunnerOptions
    {
        public IMonoClock Mono { get; set; } = StopwatchMonoClock.Instance;

        public int MaxTicksPerFrame { get; set; } = RuntimeConfig.MAX_TICKS_PER_FRAME;
        public double InAppGapSec { get; set; } = RuntimeConfig.IN_APP_GAP_SEC;
        public double AutosaveIntervalSec { get; set; } = RuntimeConfig.AUTOSAVE_INTERVAL_SEC;
        public double WallClockSyncSec { get; set; } = RuntimeConfig.WALL_CLOCK_SYNC_SEC;

        /// <summary>TICK_BUDGET_MS; zero or negative disables the budget (01 AC 4 runs with it disabled).</summary>
        public double TickBudgetMs { get; set; } = RuntimeConfig.TICK_BUDGET_MS;

        /// <summary>Background results posted for the main thread; run at the start of each frame.</summary>
        public MainThreadDispatcher Dispatcher { get; set; }
    }

    /// <summary>
    /// The one tick driver for the client, bots and tests (01 section 3; issue #65). Owns the Simulation: every command,
    /// player or system, goes through <see cref="Issue"/> (logged by the sim before it is applied, 2.2 rule 4), events are
    /// drained once per frame and published on the <see cref="EventBus"/>, saves happen on a cadence and on lifecycle
    /// callbacks. Server time enters only as logged ApplyServerTime at boot, resume, gap recovery and every
    /// WALL_CLOCK_SYNC_SEC of foreground time (00 contract 7, issue #5). Main thread only; not thread-safe.
    /// </summary>
    public sealed class SessionRunner
    {
        readonly Simulation _sim;
        readonly GameServices _svc;
        readonly EventBus _bus;
        readonly SessionRunnerOptions _opt;
        readonly IMonoClock _mono;
        readonly double _tickHz;
        Queue<Command> _pending = new Queue<Command>();
        Queue<Command> _running = new Queue<Command>();
        double _accTicks;
        long _lastMono, _lastSaveMono, _lastClockSyncMono;
        bool _dirty, _started, _clockAnomalyReported;
        SummonFlow _summons;
        readonly IClockResync _resync;
        readonly object _syncLock = new object();
        readonly List<(long extrapolated, long synced)> _syncsLanded = new List<(long, long)>();

        public SessionRunner(Simulation sim, GameServices services, EventBus bus, ICommandLog log = null, SessionRunnerOptions options = null)
        {
            _sim = sim ?? throw new ArgumentNullException(nameof(sim));
            _svc = services ?? throw new ArgumentNullException(nameof(services));
            if (services.Clock == null || services.Saves == null) throw new ArgumentException("services needs Clock and Saves", nameof(services));
            // Main-thread only (01 section 4): every runner-owned publish reports clock_anomaly{backwards} (02 section 11.3).
            bus.Subscribe<ClockWentBackwards>(e => ReportClockBackwards(e.DeltaSeconds));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _opt = options ?? new SessionRunnerOptions();
            _mono = _opt.Mono ?? StopwatchMonoClock.Instance;
            _tickHz = sim.Db.Constants.TickHz;
            if (log != null) sim.CommandLog = log;
            long now = _mono.Ticks;
            _lastMono = _lastSaveMono = _lastClockSyncMono = now;
            // 01 section 8.2 step 2: a clock sync may land on any thread; OnFrame handles it on the main thread.
            _resync = services.Clock as IClockResync;
            if (_resync != null) _resync.Resynced += PostClockSync;
        }

        public Simulation Sim => _sim;
        public EventBus Bus => _bus;
        public GameServices Services => _svc;

        /// <summary>The monotonic clock the frame loop reads (Runtime flows time their latency with it).</summary>
        public IMonoClock Mono => _mono;

        /// <summary>The kill-safe summon flow of 01 section 8.5.1 over this runner (issue #68).</summary>
        public SummonFlow Summons => _summons ??= new SummonFlow(this);

        /// <summary>
        /// A manual tick switch (debug and tests): false runs no ticks and accumulates no time; commands, clock syncs,
        /// events and saves continue. The offline reward needs no toggle: see <see cref="TicksHeldForOfflineReward"/>.
        /// </summary>
        public bool TicksEnabled { get; set; } = true;

        /// <summary>
        /// True while Offline.Pending is set (00 contract 7, decision 53; 01 section 8.7 step 5): the runner holds ticks
        /// exactly as with TicksEnabled false, from the ComputeOfflineCatchup that stored the reward until the
        /// ClaimOfflineReward that SCR_OFFLINE_REWARD sends, so the numbers on the modal are exactly what is credited.
        /// </summary>
        public bool TicksHeldForOfflineReward => _sim.State.Offline.Pending != null;

        /// <summary>True when the next frame may run ticks.</summary>
        public bool TicksRunning => TicksEnabled && !TicksHeldForOfflineReward;

        /// <summary>
        /// Debug builds only (the client's x5/x20 cheat): multiplies frame time for the tick accumulator. The gap rule,
        /// the clock sync and autosave still use real time, and MAX_TICKS_PER_FRAME still caps a frame. Default 1.
        /// </summary>
        public double DebugTimeScale { get; set; } = 1;

        /// <summary>Between OnPause and OnResume. OnFrame does nothing while paused.</summary>
        public bool IsPaused { get; private set; }

        public int ConsecutiveSaveFailures { get; private set; }
        public long SavesWritten { get; private set; }
        public string LastSaveError { get; private set; }
        public long TotalTicks { get; private set; }
        public int TicksLastFrame { get; private set; }

        /// <summary>Commands waiting for the next frame.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Queues a command for the start of the next frame. The only call allowed from an EventBus handler.</summary>
        public void Enqueue(Command c) => _pending.Enqueue(c ?? throw new ArgumentNullException(nameof(c)));

        /// <summary>
        /// Applies a command now (Runtime flows: SummonFlow, inbox claim, boot). Never from an EventBus handler: that would
        /// nest a mutation inside a drain (01 section 2.3, AC 12), so it throws; handlers use <see cref="Enqueue"/>.
        /// A reject is published as <see cref="CommandRejected"/>.
        /// </summary>
        public ApplyResult Issue(Command c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            if (_bus.IsPublishing) throw new InvalidOperationException("Issue called from an EventBus handler: use Enqueue (01 section 2.3)");
            var r = _sim.Apply(c);
            if (r.Ok) _dirty = true;
            else _bus.PublishReject(c, r);
            return r;
        }

        /// <summary>
        /// Boot step 5 (01 section 8.1): ApplyServerTime(now) (rollover first), ComputeOfflineCatchup(now), publish, save.
        /// Call once, after the Simulation exists and before the first <see cref="OnFrame"/>.
        /// </summary>
        public void Start()
        {
            _started = true;
            SyncAndCatchUp();
        }

        /// <summary>One rendered frame (01 section 3.1). Returns the ticks run.</summary>
        public int OnFrame()
        {
            TicksLastFrame = 0;
            if (IsPaused) return 0;
            if (!_started) Start();
            _opt.Dispatcher?.RunPending();
            HandleLandedClockSyncs();
            // 02 section 11.1: CLOCK_RESYNC_SEC of foreground time without an observation fires a /time sync (issue #149).
            if (_resync != null && _resync.ResyncDue) _ = _resync.SyncAsync();

            long mono = _mono.Ticks;
            double dt = (mono - _lastMono) / (double)_mono.Frequency;
            _lastMono = mono;

            if (dt > _opt.InAppGapSec)   // frozen without a pause callback (debugger, OS freeze): catch-up, not ticks
            {
                _accTicks = 0;
                SyncAndCatchUp();
                return 0;
            }

            // Commands queued before this frame; anything a handler enqueues during this frame waits for the next one.
            var batch = _pending;
            _pending = _running;
            _running = batch;
            while (batch.Count > 0) Issue(batch.Dequeue());

            int n = 0;
            if (TicksRunning)
            {
                double scale = DebugTimeScale > 0 ? DebugTimeScale : 1;
                _accTicks = Math.Min(_accTicks + dt * scale * _tickHz, _opt.InAppGapSec * scale * _tickHz);
                long budget = _opt.TickBudgetMs > 0 ? (long)(_opt.TickBudgetMs / 1000.0 * _mono.Frequency) : long.MaxValue;
                long start = _opt.TickBudgetMs > 0 ? _mono.Ticks : 0;
                while (_accTicks >= 1.0 - 1e-9 && n < _opt.MaxTicksPerFrame)
                {
                    _sim.Advance(1);
                    _accTicks -= 1.0;
                    n++;
                    if (_opt.TickBudgetMs > 0 && _mono.Ticks - start >= budget) break;   // the remainder stays in the accumulator
                }
                if (_accTicks < 0) _accTicks = 0;
            }
            else
            {
                _accTicks = 0;
            }
            if (n > 0) _dirty = true;
            TicksLastFrame = n;
            TotalTicks += n;

            if ((mono - _lastClockSyncMono) / (double)_mono.Frequency >= _opt.WallClockSyncSec)
            {
                Issue(new ApplyServerTime(_svc.Clock.NowServerSeconds));   // re-anchor the virtual server clock (issue #5)
                _lastClockSyncMono = mono;
            }
            _bus.Publish(_sim.DrainEvents());

            if (_dirty && (mono - _lastSaveMono) / (double)_mono.Frequency >= _opt.AutosaveIntervalSec) SaveNow(SaveTrigger.Periodic);
            return n;
        }

        /// <summary>
        /// Debug builds ("Run 1 h") and headless drivers: runs <paramref name="ticks"/> ticks now, as if the app had been
        /// open that long, and publishes the events. Not from an EventBus handler.
        /// </summary>
        public void DebugAdvance(long ticks)
        {
            if (_bus.IsPublishing) throw new InvalidOperationException("DebugAdvance called from an EventBus handler");
            if (ticks <= 0) return;
            _sim.AdvanceTicks(ticks);
            TotalTicks += ticks;
            _dirty = true;
            _bus.Publish(_sim.DrainEvents());
        }

        /// <summary>App to background (01 section 3.3): a synchronous save inside the callback, then the analytics flush.</summary>
        public void OnPause()
        {
            if (IsPaused) return;
            IsPaused = true;
            _bus.Publish(_sim.DrainEvents());
            SaveNow(SaveTrigger.Pause);
            _svc.Analytics?.Flush();
        }

        /// <summary>App back in the foreground (01 section 8.2): re-anchor, catch-up compute, save.</summary>
        public void OnResume()
        {
            IsPaused = false;
            _accTicks = 0;
            _summons?.OnResume();
            SyncAndCatchUp();
        }

        public void OnQuit()
        {
            _bus.Publish(_sim.DrainEvents());
            SaveNow(SaveTrigger.Quit);
            _svc.Analytics?.Flush();
        }

        /// <summary>Application.lowMemory (01 section 3.3): save as on pause; the client then unloads hidden screens.</summary>
        public void OnLowMemory() => SaveNow(SaveTrigger.Pause);

        /// <summary>
        /// Writes now. Never throws for a write failure: it counts it (SaveWriteFailed at SAVE_FAIL_WARN_COUNT) and the
        /// state stays in memory for the next trigger. Returns true when written.
        /// </summary>
        public bool SaveNow(SaveTrigger trigger)
        {
            _lastSaveMono = _mono.Ticks;
            try
            {
                _svc.Saves.Save(_sim.State, trigger);   // 02 section 8.2: the store stamps, encodes and rotates
                _dirty = false;
                ConsecutiveSaveFailures = 0;
                LastSaveError = null;
                SavesWritten++;
                return true;
            }
            catch (SaveWriteException e)
            {
                ConsecutiveSaveFailures++;   // the store tracks save_write_failed{consecutive} (02 section 14.3)
                LastSaveError = e.Message;
                if (ConsecutiveSaveFailures == RuntimeConfig.SAVE_FAIL_WARN_COUNT) _bus.PublishSystem(new SaveWriteFailed(ConsecutiveSaveFailures));
                return false;
            }
        }

        /// <summary>
        /// The Runtime summon flow of 01 section 8.5: <see cref="Summons"/>' SummonAsync (persisted intent, the logged
        /// ApplySummonResult, publish, save). Main thread; the local service completes synchronously.
        /// </summary>
        public Task<Result<SummonResultPayload, SummonError>> SummonAsync(Banner banner, int count, SummonPayment payment,
                                                                         CancellationToken ct = default)
        {
            if (_svc.Summon == null) throw new InvalidOperationException("no summon service in this GameServices");
            return Summons.SummonAsync(banner, count, payment, ct);
        }

        /// <summary>Publishes the events the sim has buffered (Runtime flows that Issue outside a frame).</summary>
        public void PublishDrained()
        {
            if (_bus.IsPublishing) throw new InvalidOperationException("PublishDrained called from an EventBus handler");
            _bus.Publish(_sim.DrainEvents());
        }

        /// <summary>
        /// 01 section 8.2 step 2 (issue #65): a clock sync landed and reports <paramref name="syncedServerSeconds"/> where the
        /// clock had extrapolated <paramref name="extrapolatedServerSeconds"/>. Past CLOCK_JUMP_WARN_SEC it reports
        /// clock_anomaly{resync}; when real time was longer it credits the difference as a second catch-up window
        /// (ApplyServerTime then ComputeOfflineCatchup at the corrected now; it sums into Offline.Pending, whose effective
        /// cap applies to the sum, 8.7). When shorter nothing is taken back. Main thread, not from an EventBus handler; an
        /// <see cref="IClockResync"/> clock's syncs arrive here through the next <see cref="OnFrame"/>.
        /// </summary>
        public void OnClockSynced(long extrapolatedServerSeconds, long syncedServerSeconds)
        {
            if (_bus.IsPublishing) throw new InvalidOperationException("OnClockSynced called from an EventBus handler");
            long jump = syncedServerSeconds - extrapolatedServerSeconds;
            if (Math.Abs(jump) <= RuntimeConfig.CLOCK_JUMP_WARN_SEC) return;
            _svc.Analytics?.Track("clock_anomaly", new Dictionary<string, object>
            {
                ["kind"] = "resync", ["seconds"] = jump, ["negative"] = jump < 0,
            });
            if (jump < 0) return;   // the discrepancy is bounded by the cap and logged; the next re-anchor reports it backwards
            _accTicks = 0;
            Issue(new ApplyServerTime(syncedServerSeconds));
            Issue(new ComputeOfflineCatchup(syncedServerSeconds));   // the second window: synced - LastTickServerTime
            _bus.Publish(_sim.DrainEvents());
            SaveNow(SaveTrigger.Resume);
            _lastClockSyncMono = _mono.Ticks;
        }

        void PostClockSync(long extrapolated, long synced)
        {
            lock (_syncLock) _syncsLanded.Add((extrapolated, synced));
        }

        void HandleLandedClockSyncs()
        {
            (long extrapolated, long synced)[] landed;
            lock (_syncLock)
            {
                if (_syncsLanded.Count == 0) return;
                landed = _syncsLanded.ToArray();
                _syncsLanded.Clear();
            }
            foreach (var (e, s) in landed) OnClockSynced(e, s);
        }

        /// <summary>clock_anomaly{backwards}, at most once per session (02 section 11.3).</summary>
        void ReportClockBackwards(long seconds)
        {
            if (_clockAnomalyReported) return;
            _clockAnomalyReported = true;
            _svc.Analytics?.Track("clock_anomaly", new Dictionary<string, object> { ["kind"] = "backwards", ["seconds"] = seconds });
        }

        void SyncAndCatchUp()
        {
            long now = _svc.Clock.NowServerSeconds;
            // 02 section 11.3: a resume behind the catch-up anchor credits nothing (the sim never moves the anchor back).
            long behind = _sim.State.Time.LastTickServerTime - now;
            if (behind > _sim.Db.Constants.ClockBackwardsToleranceS) ReportClockBackwards(behind);   // past the jitter tolerance (issue #246)
            Issue(new ApplyServerTime(now));          // re-anchor; day/week rollover first
            Issue(new ComputeOfflineCatchup(now));    // stores or sums into Offline.Pending and stamps LastTickServerTime (8.7)
            _bus.Publish(_sim.DrainEvents());
            SaveNow(SaveTrigger.Resume);
            _lastMono = _mono.Ticks;
            _lastClockSyncMono = _lastMono;
            // 01 section 8.2 step 1: the catch-up above used the extrapolated time; the sync is fired, not awaited (issue
            // #149). Its result lands through IClockResync.Resynced on a later frame, where OnClockSynced credits a jump
            // over CLOCK_JUMP_WARN_SEC as the second catch-up window (step 2). It never throws.
            if (_resync != null) _ = _resync.SyncAsync();
        }
    }
}
