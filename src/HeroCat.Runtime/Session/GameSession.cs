using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Summons;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Session
{
    public sealed class GameSessionOptions
    {
        public double AutosaveIntervalSec { get; set; } = SaveConfig.AUTOSAVE_INTERVAL_SEC;

        /// <summary>WALL_CLOCK_SYNC_SEC: how often Update issues ApplyServerTime (01 section 3.1).</summary>
        public double WallClockSyncSec { get; set; } = SaveConfig.WALL_CLOCK_SYNC_SEC;

        /// <summary>IN_APP_GAP_SEC: a single Update dt above this is a freeze, recovered with ApplyServerTime + ComputeOfflineCatchup.</summary>
        public double InAppGapSec { get; set; } = SaveConfig.IN_APP_GAP_SEC;

        public int MaxTicksPerFrame { get; set; } = SaveConfig.MAX_TICKS_PER_FRAME;

        /// <summary>New profiles only. Null = a fresh random RngRoot (02 section 3.4).</summary>
        public Guid? NewProfileRngRoot { get; set; }

        /// <summary>This install's id (stamped into DeviceId on every write). Null = a fresh Guid per session.</summary>
        public Guid? InstallId { get; set; }
    }

    /// <summary>
    /// Load-or-create plus the frame loop, the M0 skeleton of 01 section 3's SessionRunner. Pure C#, single-threaded.
    /// Runtime's only door into the sim is Apply and Advance (00 contract 5): server time enters as the logged
    /// ApplyServerTime, offline catch-up as ComputeOfflineCatchup. Events are drained once per Update into
    /// <see cref="PendingEvents"/> for the view-models.
    /// TODO(#65): SessionRunner, EventBus and the command queue replace this class; TODO(#70): the BootFlow steps.
    /// </summary>
    public sealed class GameSession
    {
        readonly GameServices _services;
        readonly GameSessionOptions _opt;
        readonly Guid _installId;
        readonly List<Event> _pending = new List<Event>();
        double _accTicks, _sinceSave, _sinceSync;
        ISummonService _summon;
        bool _dirty;

        GameSession(ContentDb db, GameServices services, GameSessionOptions opt)
        {
            Content = db;
            _services = services;
            _opt = opt;
            _installId = opt.InstallId ?? Guid.NewGuid();
        }

        public ContentDb Content { get; }
        public Simulation Sim { get; private set; }
        public GameState State => Sim.State;
        public IClock Clock => _services.Clock;
        public IAnalytics Analytics => _services.Analytics;
        public LoadResult LoadResult { get; private set; }
        public bool IsNewProfile { get; private set; }
        public int ConsecutiveSaveFailures { get; private set; }
        public bool ShouldWarnSaveFailure => ConsecutiveSaveFailures >= SaveConfig.SAVE_FAIL_WARN_COUNT;
        public string LastSaveError { get; private set; }
        public long SavesWritten { get; private set; }
        public bool IsPaused { get; private set; }

        /// <summary>Events drained since the last <see cref="TakeEvents"/>.</summary>
        public IReadOnlyList<Event> PendingEvents => _pending;

        public List<Event> TakeEvents()
        {
            var r = new List<Event>(_pending);
            _pending.Clear();
            return r;
        }

        /// <summary>
        /// Load or create the profile, anchor the clock, run the catch-up compute, save. Throws
        /// <see cref="SaveTooNewException"/> if the save on disk comes from a newer build (nothing is written).
        /// </summary>
        public static GameSession Open(ContentDb db, GameServices services, GameSessionOptions options = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (services?.Clock == null || services.SaveStore == null) throw new ArgumentException("services needs Clock and SaveStore");
            services.Analytics ??= NullAnalytics.Instance;
            options ??= new GameSessionOptions();

            long now = services.Clock.NowServerSeconds;
            var load = SaveSystem.Load(services.SaveStore, db, now, services.Analytics);
            var session = new GameSession(db, services, options) { LoadResult = load };

            GameState state;
            if (load.HasState) state = load.State;
            else
            {
                state = ProfileFactory.Create(db, Guid.Empty, now, session._installId, options.NewProfileRngRoot);
                session.IsNewProfile = true;
                services.Analytics.Track("profile_created", new Dictionary<string, object> { ["rngRoot"] = state.RngRoot.ToString() });
            }
            session.Sim = new Simulation(state, db);
            session.Sync(catchUp: true);
            session.SaveNow();
            services.Analytics.Track("session_start", new Dictionary<string, object>
            {
                ["stage"] = session.State.Combat.Stage,
                ["loadStatus"] = load.Status.ToString(),
            });
            return session;
        }

        /// <summary>
        /// Advance by <paramref name="realDeltaSec"/> of real time: ticks at TICK_HZ (at most MaxTicksPerFrame per call,
        /// the rest carries), ApplyServerTime every WallClockSyncSec, autosave while dirty. A dt above InAppGapSec is a
        /// freeze: no ticks, recovered like a resume. Returns the number of ticks run.
        /// </summary>
        public int Update(double realDeltaSec)
        {
            if (IsPaused || !(realDeltaSec > 0)) return 0;
            if (realDeltaSec > _opt.InAppGapSec)
            {
                _accTicks = 0;
                _sinceSync = 0;
                Sync(catchUp: true);
                SaveNow();
                return 0;
            }

            int hz = Content.Constants.TickHz;
            _accTicks = Math.Min(_accTicks + realDeltaSec * hz, _opt.InAppGapSec * hz);
            int n = 0;
            while (_accTicks >= 1.0 - 1e-9 && n < _opt.MaxTicksPerFrame)
            {
                _accTicks -= 1.0;
                n++;
            }
            if (n > 0)
            {
                Sim.Advance(n);
                _dirty = true;
            }

            _sinceSync += realDeltaSec;
            if (_sinceSync >= _opt.WallClockSyncSec)
            {
                _sinceSync = 0;
                Sim.Apply(new ApplyServerTime(Clock.NowServerSeconds));
            }
            Drain();

            _sinceSave += realDeltaSec;
            if (_dirty && _sinceSave >= _opt.AutosaveIntervalSec) SaveNow();
            return n;
        }

        /// <summary>Applies a player command and marks the state dirty. Rejects change nothing.</summary>
        public ApplyResult Apply(Command c)
        {
            var r = Sim.Apply(c);
            if (r.Ok) _dirty = true;
            Drain();
            return r;
        }

        /// <summary>The summon service in use: services.Summon, or a LocalSummonService over this session's state (M0).</summary>
        public ISummonService SummonService => _summon ??= _services.Summon ?? new LocalSummonService(() => State, Content, () => Clock.NowServerSeconds);

        /// <summary>
        /// The Runtime summon flow of 01 section 8.5.1, minimal: ask the service, then apply the payload through the logged
        /// ApplySummonResult (the only summon mutator), drain events, save. TODO(#65): the SessionRunner's SummonFlow with
        /// the persisted Pending.Summon intent, the summon analytics event (04 section 13) and the Remote pre-roll.
        /// </summary>
        public async Task<Result<SummonResultPayload, SummonError>> SummonAsync(Banner banner, int count, SummonPayment payment,
                                                                               CancellationToken ct = default)
        {
            var result = await SummonService.PullAsync(SummonRequest.New(banner, count, payment), ct).ConfigureAwait(false);
            if (!result.IsOk) return result;
            var applied = Apply(new ApplySummonResult(result.Value));
            if (!applied.Ok) throw new InvalidOperationException("ApplySummonResult rejected a service payload: " + applied);
            SaveNow();
            return result;
        }

        /// <summary>App to background: save immediately, stop ticking.</summary>
        public void Pause()
        {
            if (IsPaused) return;
            IsPaused = true;
            SaveNow(ticking: true);
            Analytics.Flush();
        }

        /// <summary>App back in the foreground: re-anchor the clock and run the catch-up compute, save.</summary>
        public void Resume()
        {
            IsPaused = false;
            _accTicks = 0;
            _sinceSync = 0;
            Sync(catchUp: true);
            SaveNow();
        }

        public void Quit() => Pause();

        /// <summary>Write now. Never throws; returns false and counts the failure (state stays in memory, next save retries).</summary>
        public bool SaveNow(bool ticking = false)
        {
            _sinceSave = 0;
            try
            {
                SaveSystem.Write(_services.SaveStore, State, Clock.NowServerSeconds, Content, _installId, ticking);
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
                if (ConsecutiveSaveFailures == SaveConfig.SAVE_FAIL_WARN_COUNT)
                    Analytics.Track("save_write_failed", new Dictionary<string, object> { ["consecutive"] = ConsecutiveSaveFailures });
                return false;
            }
        }

        void Sync(bool catchUp)
        {
            long now = Clock.NowServerSeconds;
            Sim.Apply(new ApplyServerTime(now));
            if (catchUp) Sim.Apply(new ComputeOfflineCatchup(now));
            Drain();
        }

        void Drain()
        {
            var e = Sim.DrainEvents();
            if (e.Count > 0) _pending.AddRange(e);
        }
    }
}
