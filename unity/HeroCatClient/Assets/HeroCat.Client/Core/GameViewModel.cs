using System;
using System.Collections.Generic;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Session;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The one object screens talk to (07 section 3). Pull for numbers: Refresh() copies the sim's per-tick Snapshot into
    /// the sub-view-models. Push for feedback: it subscribes to the SessionRunner's EventBus and turns events into feed
    /// lines and toasts. Commands go through SessionRunner.Issue; a reject arrives as CommandRejected and toasts its
    /// reject.&lt;reason&gt; key (01 section 7). No game rule lives here, and no screen sees GameState.
    /// M0 skeleton: the old prototype's gear, hero party and class screens are gone (00 decision 33); the M0 screens
    /// (SCR_MAIN, SCR_HERO, SCR_WEAPONS, SCR_SKILLS, SCR_SUMMON, ...) are their own issues (TODO(#109), #111-#118).
    /// </summary>
    public sealed class GameViewModel
    {
        readonly SessionRunner _runner;
        readonly Simulation _sim;
        readonly OffsetClock _clock;
        readonly Constants _k;
        readonly List<string> _popups = new List<string>();
        readonly List<IDisposable> _subs = new List<IDisposable>();
        bool _dirty, _saveRequested;

        public GameViewModel(SessionRunner runner, OffsetClock clock, string saveDescription = "")
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _sim = runner.Sim;
            _clock = clock;
            _k = _sim.Db.Constants;
            SaveDescription = saveDescription;
            _subs.Add(runner.Bus.Subscribe<Event>(OnSimEvent));
            _subs.Add(runner.Bus.SubscribeNotice<CommandRejected>(OnRejected));
            _subs.Add(runner.Bus.SubscribeNotice<SaveWriteFailed>(_ => Toasts.Push("Save failed", 3.0)));   // TODO(#118): SCR_ERROR / toast key
            Refresh();
        }

        /// <summary>Unsubscribes from the bus (the session is being torn down).</summary>
        public void Dispose()
        {
            foreach (var s in _subs) s.Dispose();
            _subs.Clear();
        }

        public readonly HudVm Hud = new HudVm();
        public readonly CombatVm Combat = new CombatVm();
        public readonly EventFeed Feed = new EventFeed();
        public readonly ToastQueue Toasts = new ToastQueue();

        public string SaveDescription { get; }
        public string ContentVersion => _sim.Db.Version;
        public string ProfileId => _sim.State.RngRoot.ToString();
        public double TimeScale => _runner.DebugTimeScale;
        public long ClockOffsetSeconds => _clock?.OffsetSeconds ?? 0;
        public bool ResetRequested { get; private set; }

        /// <summary>The oldest popup not yet dismissed, or null.</summary>
        public string CurrentPopup => _popups.Count > 0 ? _popups[0] : null;

        public void DismissPopup()
        {
            if (_popups.Count > 0) _popups.RemoveAt(0);
        }

        public bool ConsumeDirty()
        {
            bool d = _dirty;
            _dirty = false;
            return d;
        }

        public bool ConsumeSaveRequest()
        {
            bool s = _saveRequested;
            _saveRequested = false;
            return s;
        }

        /// <summary>Copies the sim's snapshot into the sub-view-models.</summary>
        public void Refresh()
        {
            var s = _sim.GetSnapshot();
            Hud.Gold = s.Gold;
            Hud.Diamonds = s.Diamonds;
            Hud.FishCoin = s.FishCoin;
            Hud.HeroLevel = s.HeroLevel;
            Combat.Stage = s.Stage;
            Combat.Chapter = s.Chapter;
            Combat.KillsInStage = s.KillsInStage;
            Combat.KillsPerStage = _k.KillsPerStage;
            Combat.IsBossStage = _k.IsBossStage(s.Stage);
            Combat.BossPhase = s.BossPhase;
            Combat.IsWalled = s.BossPhase == BossPhase.Walled;
            Combat.BossTicksLeft = s.BossTicksLeft;
            Combat.EnemyHp = s.EnemyHp;
            Combat.EnemyMaxHp = s.EnemyHpMax;
        }

        void OnSimEvent(Event e)
        {
            _dirty = true;
            switch (e)
            {
                case DayRolledOver _:
                    Toasts.Push("New day", 2.0);   // TODO(#187): localization keys (00 contract 10)
                    break;
                case CurrencyChanged c when c.Reason == CurrencyReason.LOGIN:
                    Feed.Add("Login: +" + TextFmt.Int((long)c.Delta) + " diamonds");
                    break;
            }
        }

        void OnRejected(CommandRejected r)
        {
            _dirty = true;
            Toasts.Push(ErrorClassifier.RejectKey(r.Result), 1.5);   // TODO(#187): localize the key
        }

        /// <summary>Apply a player command now (a button press, never an EventBus handler); a reject toasts its reason key.</summary>
        public ApplyResult Apply(Command c)
        {
            var r = _runner.Issue(c);
            _dirty = true;
            return r;
        }

        // ---------------------------------------------------------------------------------- debug (dev builds)

        /// <summary>Advance the sim by real ticks, as if the app had been open that long.</summary>
        public void DebugRunHours(double hours)
        {
            long ticks = (long)(hours * 3600 * _k.TickHz);
            if (_clock != null) _clock.OffsetSeconds += (long)(hours * 3600);
            _runner.DebugAdvance(ticks);
            Refresh();
        }

        /// <summary>Move the wall clock forward; the next sync sees the gap.</summary>
        public void DebugClockSkip(double hours)
        {
            if (_clock != null) _clock.OffsetSeconds += (long)(hours * 3600);
        }

        public void DebugSetTimeScale(double scale) => _runner.DebugTimeScale = scale;
        public void DebugRequestSave() => _saveRequested = true;
        public void DebugResetProfile() => ResetRequested = true;
    }
}
