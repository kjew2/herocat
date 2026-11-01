using System;
using System.Collections.Generic;
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
    /// the sub-view-models. Push for feedback: DrainEvents() turns events into feed lines and toasts. Commands go through
    /// Simulation.Apply and toast the reject reason. No game rule lives here, and no screen sees GameState.
    /// M0 skeleton: the old prototype's gear, hero party and class screens are gone (00 decision 33); the M0 screens
    /// (SCR_MAIN, SCR_HERO, SCR_WEAPONS, SCR_SKILLS, SCR_SUMMON, ...) are their own issues (TODO(#109), #111-#118).
    /// </summary>
    public sealed class GameViewModel
    {
        readonly Simulation _sim;
        readonly OffsetClock _clock;
        readonly Constants _k;
        readonly List<string> _popups = new List<string>();
        bool _dirty, _saveRequested;

        public GameViewModel(Simulation sim, OffsetClock clock, string saveDescription = "")
        {
            _sim = sim ?? throw new ArgumentNullException(nameof(sim));
            _clock = clock;
            _k = sim.Db.Constants;
            SaveDescription = saveDescription;
            Refresh();
        }

        public readonly HudVm Hud = new HudVm();
        public readonly CombatVm Combat = new CombatVm();
        public readonly EventFeed Feed = new EventFeed();
        public readonly ToastQueue Toasts = new ToastQueue();

        public string SaveDescription { get; }
        public string ContentVersion => _sim.Db.Version;
        public string ProfileId => _sim.State.RngRoot.ToString();
        public double TimeScale { get; private set; } = 1;
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

        /// <summary>Drains the sim's events into feed lines and toasts. Returns how many were handled.</summary>
        public int DrainEvents()
        {
            var events = _sim.DrainEvents();
            foreach (var e in events) OnSimEvent(e);
            if (events.Count > 0) _dirty = true;
            return events.Count;
        }

        void OnSimEvent(Event e)
        {
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

        /// <summary>Apply a command; a reject toasts its reason key.</summary>
        public ApplyResult Apply(Command c)
        {
            var r = _sim.Apply(c);
            if (!r.Ok) Toasts.Push("reject." + (r.Reason ?? r.Code.ToString()), 1.5);
            _dirty = true;
            return r;
        }

        // ---------------------------------------------------------------------------------- debug (dev builds)

        /// <summary>Advance the sim by real ticks, as if the app had been open that long.</summary>
        public void DebugRunHours(double hours)
        {
            long ticks = (long)(hours * 3600 * _k.TickHz);
            _sim.AdvanceTicks(ticks);
            if (_clock != null) _clock.OffsetSeconds += (long)(hours * 3600);
            DrainEvents();
            Refresh();
        }

        /// <summary>Move the wall clock forward; the next sync sees the gap.</summary>
        public void DebugClockSkip(double hours)
        {
            if (_clock != null) _clock.OffsetSeconds += (long)(hours * 3600);
        }

        public void DebugSetTimeScale(double scale) => TimeScale = scale;
        public void DebugRequestSave() => _saveRequested = true;
        public void DebugResetProfile() => ResetRequested = true;
    }
}
