using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The game-loop bridge (07 section 17.3) as plain C#: the Unity GameRoot forwards Update / pause / quit here.
    /// Fixed-step accumulator at TICK_HZ on the main thread; ApplyServerTime every WALL_CLOCK_SYNC_SEC; resume runs
    /// ApplyServerTime and ComputeOfflineCatchup; autosave every 30 s; event drain and view-model refresh once per frame.
    /// TODO(#65): Runtime's SessionRunner replaces this.
    /// </summary>
    public sealed class SimRunnerCore
    {
        public const int MaxTicksPerFrame = 100;          // MAX_TICKS_PER_FRAME
        public const double IgnoreFrameAboveS = 1.0;      // resume glitches / debugger pauses add nothing
        public const double WallClockSyncS = 60.0;        // WALL_CLOCK_SYNC_SEC
        public const double AutosaveS = 30.0;             // AUTOSAVE_INTERVAL_SEC

        readonly Simulation _sim;
        readonly OffsetClock _clock;
        readonly ISaveBridge _save;
        readonly int _tickHz;
        double _acc, _sinceSync, _sinceSave;
        bool _paused;

        public SimRunnerCore(Simulation sim, OffsetClock clock, ISaveBridge save, GameViewModel vm)
        {
            _sim = sim;
            _clock = clock;
            _save = save;
            Vm = vm;
            _tickHz = sim.Db.Constants.TickHz;
        }

        public GameViewModel Vm { get; }
        public Simulation Sim => _sim;
        public long TotalTicksRun { get; private set; }

        /// <summary>Open a session: load or create the profile, sync the clock, run the catch-up, build the view-model.</summary>
        public static SimRunnerCore Start(ContentDb content, ISaveBridge save, OffsetClock clock)
        {
            var sim = save.Open(content, clock);
            long now = clock.NowServerSeconds;
            sim.Apply(new ApplyServerTime(now));
            sim.Apply(new ComputeOfflineCatchup(now));
            var vm = new GameViewModel(sim, clock, save.Description);
            vm.DrainEvents();
            vm.Refresh();
            return new SimRunnerCore(sim, clock, save, vm);
        }

        /// <summary>Call once per rendered frame with the unscaled frame time in seconds. Returns ticks run.</summary>
        public int Frame(double dt)
        {
            if (_paused) return 0;
            if (dt < 0) dt = 0;
            if (dt <= IgnoreFrameAboveS) _acc += dt * Vm.TimeScale;
            double cap = MaxTicksPerFrame / (double)_tickHz;
            if (_acc > cap) _acc = cap;
            int ticks = (int)(_acc * _tickHz);
            if (ticks > 0)
            {
                _acc -= ticks / (double)_tickHz;
                _sim.Advance(ticks);
                TotalTicksRun += ticks;
            }

            _sinceSync += dt;
            if (_sinceSync >= WallClockSyncS)
            {
                _sinceSync = 0;
                _sim.Apply(new ApplyServerTime(_clock.NowServerSeconds));
            }

            int n = Vm.DrainEvents();
            bool dirty = Vm.ConsumeDirty();
            if (ticks > 0 || n > 0 || dirty) Vm.Refresh();

            _sinceSave += dt;
            if (_sinceSave >= AutosaveS || Vm.ConsumeSaveRequest()) { _sinceSave = 0; Save(ticking: true); }
            return ticks;
        }

        public void OnPause()
        {
            if (_paused) return;
            _paused = true;
            Save(ticking: true);
        }

        public void OnResume()
        {
            _acc = 0;
            _sinceSync = 0;
            long now = _clock.NowServerSeconds;
            _sim.Apply(new ApplyServerTime(now));
            _sim.Apply(new ComputeOfflineCatchup(now));
            _paused = false;
        }

        public void OnQuit() => Save(ticking: true);

        public void Save(bool ticking)
        {
            try { _save.Save(_sim, ticking); }
            catch (System.Exception e) { Vm.Toasts.Push("Save failed: " + e.Message, 3.0); }
        }
    }
}
