using System;
using System.Collections.Generic;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using HeroCat.Sim.Time;

namespace HeroCat.Runtime.Session
{
    public sealed class GameSessionOptions
    {
        /// <summary>Periodic autosave while dirty (AUTOSAVE_INTERVAL_SEC), measured in Update dt.</summary>
        public double AutosaveIntervalSec { get; set; } = SaveConfig.AUTOSAVE_INTERVAL_SEC;

        /// <summary>How often Update re-anchors the sim to the clock (rolls days, credits gaps &gt;= OFFLINE_MIN_GAP_S).</summary>
        public double WallClockSyncSec { get; set; } = SaveConfig.WALL_CLOCK_SYNC_SEC;

        /// <summary>A single Update dt above this is treated as the app having been frozen: credited as offline, not ticked.</summary>
        public double InAppGapSec { get; set; } = SaveConfig.IN_APP_GAP_SEC;

        public int MaxTicksPerFrame { get; set; } = SaveConfig.MAX_TICKS_PER_FRAME;

        /// <summary>New profiles only. Null = Guid.NewGuid().</summary>
        public Guid? NewProfileId { get; set; }

        /// <summary>New profiles only. Null = derived from the profile id (GameState.SeedFromProfileId).</summary>
        public ulong? NewProfileSeed { get; set; }

        /// <summary>New profiles only: apply the profile_created grants (gems, banked pulls).</summary>
        public bool ApplyTutorialGrants { get; set; } = true;
    }

    /// <summary>
    /// The one object the Unity client and the bot harness hold (specs/06 section 10's SessionRunner, plus
    /// load-or-create). Pure C#, single-threaded, no UnityEngine.
    ///
    /// Lifecycle:
    /// <code>
    /// var session = GameSession.Open(db, GameServices.Local(Application.persistentDataPath), outbox);
    /// // Update():                      session.Update(Time.unscaledDeltaTime);
    /// // OnApplicationPause(true/false): session.Pause() / session.Resume();
    /// // OnApplicationQuit():           session.Quit();
    /// // commands:                      session.Sim.Pull10(); session.Sim.BuyStatLevel(); ...
    /// </code>
    /// Open loads the newest good save (falling back to the backup, quarantining corrupt files, migrating old
    /// schemas) or creates a profile, credits offline gold since the save's LastSavedAtServerSec, and saves.
    /// Update ticks the sim at TICK_HZ from real dt, re-syncs the wall clock every ~60 s, and autosaves every
    /// 30 s while dirty. Pulls and class advances trigger a save on the next Update. Save failures never throw
    /// out of the session; check <see cref="ConsecutiveSaveFailures"/>.
    ///
    /// Bots: use GameServices.InMemory(fakeClock) and advance the FakeClock in step with Update dt; for an
    /// offline stretch call Pause(), clock.Advance(seconds), Resume().
    /// </summary>
    public sealed class GameSession
    {
        readonly GameServices _services;
        readonly GameSessionOptions _opt;
        readonly ImportantEventSink _sink;
        double _accTicks, _sinceSave, _sinceSync;
        bool _dirty;

        GameSession(ContentDb db, GameServices services, GameSessionOptions opt, ImportantEventSink sink)
        {
            Content = db;
            _services = services;
            _opt = opt;
            _sink = sink;
        }

        public ContentDb Content { get; }
        public Simulation Sim { get; private set; }
        public GameState State => Sim.State;
        public SaveMeta Meta { get; private set; }
        public IClock Clock => _services.Clock;
        public IAnalytics Analytics => _services.Analytics;

        /// <summary>How Open found the save. NoSave / Corrupt mean a new profile was created.</summary>
        public LoadResult LoadResult { get; private set; }

        public bool IsNewProfile { get; private set; }

        /// <summary>The most recent offline credit (Open, Resume, or a frozen-frame gap).</summary>
        public OfflineResult? LastCatchUp { get; private set; }

        /// <summary>Set when a catch-up of at least OFFLINE_REPORT_MIN_SEC happened and the UI has not taken it yet.</summary>
        public OfflineResult? PendingWelcomeBack { get; private set; }

        public int ConsecutiveSaveFailures { get; private set; }
        public bool ShouldWarnSaveFailure => ConsecutiveSaveFailures >= SaveConfig.SAVE_FAIL_WARN_COUNT;
        public string LastSaveError { get; private set; }
        public long SavesWritten { get; private set; }
        public bool IsPaused { get; private set; }

        /// <summary>Returns and clears the welcome-back card data.</summary>
        public OfflineResult? TakeWelcomeBack()
        {
            var r = PendingWelcomeBack;
            PendingWelcomeBack = null;
            return r;
        }

        // ================================================================== open

        /// <summary>
        /// Load or create the profile, apply offline catch-up, save. Throws <see cref="SaveTooNewException"/> if the
        /// save on disk comes from a newer build (nothing is written; show "please update").
        /// </summary>
        /// <param name="events">Where sim events go (e.g. an EventOutbox the client drains per frame). Null = none.</param>
        public static GameSession Open(ContentDb db, GameServices services, IEventSink events = null, GameSessionOptions options = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (services?.Clock == null || services.SaveStore == null) throw new ArgumentException("services needs Clock and SaveStore");
            services.Analytics ??= NullAnalytics.Instance;
            options ??= new GameSessionOptions();

            long now = services.Clock.NowServerSeconds;
            var load = SaveSystem.Load(services.SaveStore, db, now, services.Analytics);
            var session = new GameSession(db, services, options, new ImportantEventSink(events ?? NullEventSink.Instance))
            {
                LoadResult = load,
            };

            if (load.HasState)
            {
                session.Meta = load.Meta;
                session.Sim = new Simulation(db, load.State, session._sink, services.Gacha);
                session.CatchUpFromSaveAnchor(now);
            }
            else
            {
                var id = options.NewProfileId ?? Guid.NewGuid();
                var state = options.NewProfileSeed is ulong seed
                    ? GameState.NewProfile(db, id, seed, now)
                    : GameState.NewProfile(db, id, now);
                session.IsNewProfile = true;
                session.Meta = new SaveMeta { LastSavedAtServerSec = now, DeviceInstallId = Guid.NewGuid() };
                session.Sim = new Simulation(db, state, session._sink, services.Gacha);
                if (options.ApplyTutorialGrants) session.Sim.ApplyTutorialGrants();
                session.Sim.SyncWallClock(now, applyCatchUp: false);   // rolls the first day
                services.Analytics.Track("profile_created", new Dictionary<string, object> { ["profileId"] = id.ToString() });
            }

            session.SaveNow();
            services.Analytics.Track("session_start", new Dictionary<string, object>
            {
                ["stage"] = session.State.Stage.Stage,
                ["loadStatus"] = load.Status.ToString(),
                ["offlineElapsedSec"] = session.LastCatchUp?.ElapsedSec ?? 0,
            });
            return session;
        }

        /// <summary>Load path (specs/06 9.2): credit offline gold since the save's stamp, then re-anchor without crediting again.</summary>
        void CatchUpFromSaveAnchor(long now)
        {
            long elapsed = Math.Max(0, now - Meta.LastSavedAtServerSec);   // clock went backwards => 0
            var r = Sim.ApplyOfflineCatchUp(elapsed);
            Sim.SyncWallClock(now, applyCatchUp: false);
            RecordCatchUp(r);
        }

        void RecordCatchUp(OfflineResult r)
        {
            LastCatchUp = r;
            if (r.ElapsedSec >= SaveConfig.OFFLINE_REPORT_MIN_SEC) PendingWelcomeBack = r;
            if (r.CreditedSec > 0)
                Analytics.Track("offline_catchup", new Dictionary<string, object>
                {
                    ["elapsedSec"] = r.ElapsedSec, ["creditedSec"] = r.CreditedSec, ["capped"] = r.Capped,
                    ["gold"] = r.Gold, ["stage"] = r.Stage,
                });
        }

        // ================================================================== frame loop

        /// <summary>
        /// Advance by <paramref name="realDeltaSec"/> of real (unscaled) time. Ticks at TICK_HZ, at most
        /// MaxTicksPerFrame per call (the rest carries over). A dt above InAppGapSec is a freeze: no ticks, the gap
        /// is credited via the offline formula. Returns the number of ticks run.
        /// </summary>
        public int Update(double realDeltaSec)
        {
            if (IsPaused || !(realDeltaSec > 0)) return 0;
            if (realDeltaSec > _opt.InAppGapSec)
            {
                _accTicks = 0;
                _sinceSync = 0;
                RecordCatchUp(Sim.SyncWallClock(Clock));   // virtual time stopped at the freeze; the gap is the freeze
                SaveNow();
                return 0;
            }

            int hz = Content.Constants.TickHz;
            _accTicks = Math.Min(_accTicks + realDeltaSec * hz, _opt.InAppGapSec * hz);
            int n = 0;
            while (_accTicks >= 1.0 - 1e-9 && n < _opt.MaxTicksPerFrame)
            {
                Sim.Tick();
                _accTicks -= 1.0;
                n++;
            }
            if (n > 0) _dirty = true;

            _sinceSync += realDeltaSec;
            if (_sinceSync >= _opt.WallClockSyncSec)
            {
                _sinceSync = 0;
                var r = Sim.SyncWallClock(Clock);
                if (r.CreditedSec > 0) RecordCatchUp(r);
            }

            _sinceSave += realDeltaSec;
            if (_sink.SaveRequested || (_dirty && _sinceSave >= _opt.AutosaveIntervalSec)) SaveNow();
            return n;
        }

        /// <summary>App going to background (OnApplicationPause(true)): save immediately, stop ticking.</summary>
        public void Pause()
        {
            if (IsPaused) return;
            IsPaused = true;
            SaveNow();
            Analytics.Flush();
        }

        /// <summary>
        /// App back in the foreground: roll the day, credit the time away (from the sim's virtual time, which stopped
        /// at the pause), save. Safe to call without a prior Pause.
        /// </summary>
        public void Resume()
        {
            IsPaused = false;
            _accTicks = 0;
            _sinceSync = 0;
            RecordCatchUp(Sim.SyncWallClock(Clock));
            SaveNow();
        }

        /// <summary>OnApplicationQuit: same as Pause.</summary>
        public void Quit() => Pause();

        /// <summary>Mark state changed outside a tick (a command) so the next autosave window writes it.</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>Write now. Never throws; returns false and counts the failure (state stays in memory, next save retries).</summary>
        public bool SaveNow()
        {
            _sinceSave = 0;
            _sink.SaveRequested = false;
            try
            {
                SaveSystem.Write(_services.SaveStore, State, Meta, Clock.NowServerSeconds, Content.Version);
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

        /// <summary>
        /// Forwards events to the caller's sink (respecting its mask) and flags a save after pulls and class
        /// advances (specs/06 5.3 "immediate" triggers).
        /// </summary>
        sealed class ImportantEventSink : IEventSink
        {
            const EventKinds SaveTriggers = EventKinds.HeroPulled | EventKinds.ClassAdvanced;
            readonly IEventSink _inner;

            public ImportantEventSink(IEventSink inner) { _inner = inner; }

            public bool SaveRequested;
            public EventKinds Mask => _inner.Mask | SaveTriggers;

            public void Publish(SimEvent e)
            {
                if (e is HeroPulled || e is ClassAdvanced) SaveRequested = true;
                if ((_inner.Mask & KindOf(e)) != 0) _inner.Publish(e);
            }

            static EventKinds KindOf(SimEvent e)
            {
                switch (e)
                {
                    case EnemySpawned _: return EventKinds.EnemySpawned;
                    case HitLanded _: return EventKinds.HitLanded;
                    case EnemyKilled _: return EventKinds.EnemyKilled;
                    case StageAdvanced _: return EventKinds.StageAdvanced;
                    case BossFailed _: return EventKinds.BossFailed;
                    case BossCleared _: return EventKinds.BossCleared;
                    case GearDropped _: return EventKinds.GearDropped;
                    case StatLeveled _: return EventKinds.StatLeveled;
                    case EnhanceAttempted _: return EventKinds.EnhanceAttempted;
                    case HeroPulled _: return EventKinds.HeroPulled;
                    case HeroEquipped _: return EventKinds.HeroEquipped;
                    case ClassAdvanced _: return EventKinds.ClassAdvanced;
                    case OfflineGoldCredited _: return EventKinds.OfflineGoldCredited;
                    case DayRolledOver _: return EventKinds.DayRolledOver;
                    case ClockWentBackwards _: return EventKinds.ClockWentBackwards;
                    case StageCapReached _: return EventKinds.StageCapReached;
                    case QuestCompleted _: return EventKinds.QuestCompleted;
                    case FirstClear _: return EventKinds.FirstClear;
                    case IdleChestClaimed _: return EventKinds.IdleChestClaimed;
                    default: return EventKinds.All;
                }
            }
        }
    }
}
