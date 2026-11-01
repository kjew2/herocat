using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The game-loop bridge (specs/07 section 6) as plain C#: the Unity GameRoot forwards Update / pause / quit here.
    /// Fixed-step accumulator at TICK_HZ on the main thread, wall-clock sync every 60 s, autosave every 30 s,
    /// outbox drain and view-model refresh once per frame.
    /// </summary>
    public sealed class SimRunnerCore
    {
        public const int MaxTicksPerFrame = 100;          // 10 s of sim per rendered frame; the rest is dropped (section 6.3)
        public const double IgnoreFrameAboveS = 1.0;      // resume glitches / debugger pauses add nothing
        public const double WallClockSyncS = 60.0;
        public const double AutosaveS = 30.0;

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
            _tickHz = sim.Content.Constants.TickHz;
        }

        public GameViewModel Vm { get; }
        public Simulation Sim => _sim;
        public long TotalTicksRun { get; private set; }

        /// <summary>Open a session: load or create the profile, credit offline time, build the view-model.</summary>
        public static SimRunnerCore Start(ContentDb content, ISaveBridge save, OffsetClock clock)
        {
            // HitLanded stays masked out (section 8.1); EnemySpawned is opt-in and unused by this POC.
            var outbox = new EventOutbox(EventKinds.Default);
            var sim = save.Open(content, outbox, clock);
            sim.SyncWallClock(clock);                     // offline gold before the first frame (section 4 step 8)
            var vm = new GameViewModel(sim, outbox, clock, save.Description);
            vm.DrainEvents();                             // shows the welcome-back popup if one was credited
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
                for (int i = 0; i < ticks; i++) _sim.Tick();
                TotalTicksRun += ticks;
            }

            _sinceSync += dt;
            if (_sinceSync >= WallClockSyncS) { _sinceSync = 0; _sim.SyncWallClock(_clock); }

            int n = Vm.DrainEvents();
            bool dirty = Vm.ConsumeDirty();
            if (ticks > 0 || n > 0 || dirty) Vm.Refresh();

            _sinceSave += dt;
            if (_sinceSave >= AutosaveS || Vm.ConsumeSaveRequest()) { _sinceSave = 0; Save(); }
            return ticks;
        }

        public void OnPause()
        {
            if (_paused) return;
            _paused = true;
            Save();
        }

        public void OnResume()
        {
            _acc = 0;
            _sinceSync = 0;
            _sim.SyncWallClock(_clock);                   // catch-up; events are drained on the next Frame
            _paused = false;
        }

        public void OnQuit() => Save();

        public void Save()
        {
            try { _save.Save(_sim); }
            catch (System.Exception e) { Vm.Toasts.Push("Save failed: " + e.Message, 3.0); }
        }
    }
}
