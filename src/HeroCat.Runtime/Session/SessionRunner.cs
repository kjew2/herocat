using System;
using System.Collections.Generic;
using System.Diagnostics;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
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

        /// <summary>This install's id, stamped into DeviceId on every save. TODO(#73): the persisted install_id file.</summary>
        public Guid InstallId { get; set; }

        /// <summary>HMAC device key for the save envelope (02 section 10), if any.</summary>
        public byte[] DeviceKey { get; set; }

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
        bool _dirty, _started;

        public SessionRunner(Simulation sim, GameServices services, EventBus bus, ICommandLog log = null, SessionRunnerOptions options = null)
        {
            _sim = sim ?? throw new ArgumentNullException(nameof(sim));
            _svc = services ?? throw new ArgumentNullException(nameof(services));
            if (services.Clock == null || services.Saves == null) throw new ArgumentException("services needs Clock and Saves", nameof(services));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _opt = options ?? new SessionRunnerOptions();
            _mono = _opt.Mono ?? StopwatchMonoClock.Instance;
            _tickHz = sim.Db.Constants.TickHz;
            if (log != null) sim.CommandLog = log;
            long now = _mono.Ticks;
            _lastMono = _lastSaveMono = _lastClockSyncMono = now;
        }

        public Simulation Sim => _sim;
        public EventBus Bus => _bus;
        public GameServices Services => _svc;

        /// <summary>
        /// False while SCR_OFFLINE_REWARD is open (01 sections 8.1 step 6, 8.7 step 5): no ticks run and no time
        /// accumulates; commands, clock syncs, events and saves continue.
        /// </summary>
        public bool TicksEnabled { get; set; } = true;

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
            if (TicksEnabled)
            {
                _accTicks = Math.Min(_accTicks + dt * _tickHz, _opt.InAppGapSec * _tickHz);
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
                WriteSave(trigger);
                _dirty = false;
                ConsecutiveSaveFailures = 0;
                LastSaveError = null;
                SavesWritten++;
                return true;
            }
            catch (SaveWriteException e)
            {
                ConsecutiveSaveFailures++;
                LastSaveError = e.Message;
                if (ConsecutiveSaveFailures == RuntimeConfig.SAVE_FAIL_WARN_COUNT)
                {
                    _svc.Analytics?.Track("save_write_failed", new Dictionary<string, object> { ["consecutive"] = ConsecutiveSaveFailures, ["trigger"] = trigger.ToString() });
                    _bus.PublishSystem(new SaveWriteFailed(ConsecutiveSaveFailures));
                }
                return false;
            }
        }

        void WriteSave(SaveTrigger trigger)
        {
            // Ticking saves stamp LastTickServerTime = now: that window was ticked, not idle (02 section 8.2).
            bool ticking = TicksEnabled && (trigger == SaveTrigger.Periodic || trigger == SaveTrigger.Pause || trigger == SaveTrigger.Quit);
            // TODO(#77): 02 section 8.1's ISaveStore.Save(state, trigger) replaces this call into SaveSystem.
            SaveSystem.Write(_svc.Saves, _sim.State, _svc.Clock.NowServerSeconds, _sim.Db, _opt.InstallId, ticking, _opt.DeviceKey);
        }

        void SyncAndCatchUp()
        {
            // TODO(#149): fire ServerClock.SyncAsync here (not awaited; the result lands on a later frame), and on a jump
            // over CLOCK_JUMP_WARN_SEC credit the second catch-up window (01 section 8.2 step 2).
            long now = _svc.Clock.NowServerSeconds;
            Issue(new ApplyServerTime(now));          // re-anchor; day/week rollover first
            Issue(new ComputeOfflineCatchup(now));    // stores or sums into Offline.Pending and stamps LastTickServerTime (8.7)
            _bus.Publish(_sim.DrainEvents());
            SaveNow(SaveTrigger.Resume);
            _lastMono = _mono.Ticks;
            _lastClockSyncMono = _lastMono;
        }
    }
}
