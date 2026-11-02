using System;
using System.Collections.Generic;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Session;
using HeroCat.Runtime.Summons;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;

namespace HeroCat.Client.Core
{
    /// <summary>What the client hands the view-model layer (all optional).</summary>
    public sealed class GameVmOptions
    {
        public ILocalizer Localizer { get; set; }
        public IClientPrefs Prefs { get; set; }

        /// <summary>Monotonic seconds; default: the runner's mono clock is not visible here, so a Stopwatch.</summary>
        public Func<double> Now { get; set; }

        /// <summary>Overrides the Runtime summon flow (tests).</summary>
        public SummonFlow Summon { get; set; }

        public string SaveDescription { get; set; } = "";
    }

    /// <summary>
    /// The one object screens talk to (07 sections 3 and 16; issue #109). It owns one vm per screen (<see cref="Main"/>,
    /// <see cref="Hero"/>, <see cref="Weapons"/>, ...), the <see cref="Router"/>, <see cref="Toasts"/> and <see cref="Cues"/>.
    /// Pull for numbers: <see cref="Refresh"/> copies the per-tick Snapshot (and read-only sim queries) into the vms through
    /// change-only setters, only for what is visible. Push for feedback: it subscribes to the SessionRunner's EventBus and
    /// routes events to the vms. Commands go out through <see cref="Send"/>; a reject becomes a shake plus a toast keyed
    /// <c>reject.&lt;reason_lower&gt;</c> under the pressed button in the same frame. It is the only client type besides
    /// SessionRunner holding a Simulation; no screen sees GameState.
    /// </summary>
    public sealed class GameViewModel : ICommandSender
    {
        readonly SessionRunner _runner;
        readonly Simulation _sim;
        readonly OffsetClock _clock;
        readonly Constants _k;
        readonly List<IDisposable> _subs = new List<IDisposable>();
        readonly Func<double> _now;
        bool _dirty, _saveRequested, _inSend, _detailDirty = true;

        public GameViewModel(SessionRunner runner, OffsetClock clock, GameVmOptions options = null)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _sim = runner.Sim;
            _clock = clock;
            _k = _sim.Db.Constants;
            options = options ?? new GameVmOptions();
            SaveDescription = options.SaveDescription ?? "";
            Localizer = options.Localizer ?? Core.Localizer.Keys;
            Prefs = options.Prefs ?? new MemoryClientPrefs();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _now = options.Now ?? (() => sw.Elapsed.TotalSeconds);

            Router = new ScreenRouter();
            Context = new VmContext(_sim.Db, Localizer, this, Toasts, Cues, Prefs, Router, _now)
            {
                Flags = () => _runner.Services.Config?.Flags,
            };
            Main = new MainVm(Context);
            Hero = new HeroVm(Context);
            Weapons = new WeaponsVm(Context);
            Skills = new SkillsVm(Context);
            Companions = new CompanionsVm(Context);
            ItemDetail = new ItemDetailVm(Context);
            SummonResult = new SummonResultVm(Context);
            Summon = new SummonVm(Context, options.Summon ?? ((b, n, p) => _runner.SummonAsync(b, n, p)), SummonResult);
            SummonResult.Summon = Summon;
            OfflineReward = new OfflineRewardVm(Context);
            Confirm = new ConfirmVm(Localizer, Router);
            Error = new ErrorVm(Localizer, Router);

            Router.EnemyIsBoss = () => Main.Combat.IsBoss;
            Router.RevealRunning = () => SummonResult.Running;
            Router.SkipReveal = SummonResult.OnSkip;
            Router.ExitConfirmArgs = () => new ConfirmRequest { TitleKey = UiKeys.ExitTitle, BodyKey = UiKeys.ExitBody, IsExit = true };
            Router.ModalDismissed = e => { if (e.Id == ScreenIds.Confirm) Confirm.OnDismissed(); };
            Router.Shown = OnScreenShown;
            Router.Hidden = OnScreenHidden;

            _sim.CombatEvents = CombatEventMask.All;   // damage numbers and cast VFX (07 section 4.3): HitLanded, SkillCast and CompanionAbilityUsed are opt-in, never in bots
            _subs.Add(runner.Bus.Subscribe<Event>(OnSimEvent));
            _subs.Add(runner.Bus.SubscribeNotice<CommandRejected>(OnRejected));
            _subs.Add(runner.Bus.SubscribeNotice<SaveWriteFailed>(_ => Toasts.Push(Localizer.Text(UiKeys.ToastSaveFailed), ToastStyle.Error, 3.0)));
            Refresh();
            OfflineReward.CheckPending(_sim);
        }

        /// <summary>Unsubscribes from the bus (the session is being torn down).</summary>
        public void Dispose()
        {
            foreach (var s in _subs) s.Dispose();
            _subs.Clear();
        }

        public ILocalizer Localizer { get; }
        public IClientPrefs Prefs { get; }
        public VmContext Context { get; }
        public ScreenRouter Router { get; }
        public readonly ToastQueue Toasts = new ToastQueue();
        public readonly CueQueue Cues = new CueQueue();

        public MainVm Main { get; }
        public HudVm Hud => Main.Hud;
        public CombatVm Combat => Main.Combat;
        public FishBoardVm Board => Main.Board;
        public HeroVm Hero { get; }
        public WeaponsVm Weapons { get; }
        public SkillsVm Skills { get; }
        public CompanionsVm Companions { get; }
        public ItemDetailVm ItemDetail { get; }
        public SummonVm Summon { get; }
        public SummonResultVm SummonResult { get; }
        public OfflineRewardVm OfflineReward { get; }
        public ConfirmVm Confirm { get; }
        public ErrorVm Error { get; }

        public string SaveDescription { get; }
        public string ContentVersion => _sim.Db.Version;
        public string ProfileId => _sim.State.RngRoot.ToString();
        public double TimeScale => _runner.DebugTimeScale;
        public long ClockOffsetSeconds => _clock?.OffsetSeconds ?? 0;
        public bool ResetRequested { get; private set; }
        /// <summary>False while ticks are off or held for the offline reward (SessionRunner.TicksRunning).</summary>
        public bool TicksRunning => _runner.TicksRunning;

        /// <summary>Monotonic seconds, as the vms see them.</summary>
        public double Now => _now();

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

        // ------------------------------------------------------------------------------------------ refresh

        /// <summary>
        /// Copies the snapshot into the visible vms (07 section 16: on frames where a tick ran, an event arrived or a command
        /// marked dirty). Allocation-free on SCR_MAIN when no label is bound or nothing changed (AC 12).
        /// </summary>
        public void Refresh()
        {
            var s = _sim.GetSnapshot();
            double now = _now();
            int newCount = Prefs.NewCount(Banner.WEAPON) + Prefs.NewCount(Banner.SKILL);
            Main.Refresh(_sim, s, now, newCount);
            string top = Router.TopFull.Id;
            if (top == ScreenIds.Hero) Hero.Refresh(_sim, s);
            else if (top == ScreenIds.Weapons) Weapons.Refresh(_sim);
            else if (top == ScreenIds.Skills) Skills.Refresh(_sim);
            else if (top == ScreenIds.Companions) Companions.Refresh(_sim);
            else if (top == ScreenIds.Summon) Summon.Refresh(_sim);
            var modal = Router.Modal;
            if (modal != null && modal.Id == ScreenIds.ItemDetail && (_detailDirty || ItemDetail.Dirty))
            {
                _detailDirty = false;
                ItemDetail.Refresh(_sim);
            }
        }

        /// <summary>
        /// Every rendered frame, after the session's ticks: animation clocks, the long-press timer, the frame's taps, a landed
        /// summon request, the modal queue. Allocation-free when idle.
        /// </summary>
        public void Frame()
        {
            double now = _now();
            Main.Combat.FlushTaps();
            Main.Combat.Update(now);
            Main.Board.Update(now);
            Summon.Poll();
            if (Router.IsOpen(ScreenIds.SummonResult)) SummonResult.Update(now, _sim);
            Router.Update();
        }

        void OnScreenShown(ScreenEntry e)
        {
            _dirty = true;
            switch (e.Id)
            {
                case ScreenIds.Confirm: Confirm.Bind(e.Args as ConfirmRequest); break;
                case ScreenIds.OfflineReward: OfflineReward.OnShown(_sim); break;
                case ScreenIds.ItemDetail:
                    _detailDirty = true;
                    ItemDetail.Refresh(_sim);
                    _detailDirty = false;
                    break;
                case ScreenIds.Weapons: Weapons.Invalidate(); break;
                case ScreenIds.Skills: Skills.Invalidate(); break;
                case ScreenIds.Companions: Companions.Invalidate(); break;
            }
            if (e.Type == ScreenType.Full) Main.Board.Cancel();
        }

        void OnScreenHidden(ScreenEntry e)
        {
            _dirty = true;
            switch (e.Id)
            {
                case ScreenIds.Weapons: Weapons.OnLeave(); break;
                case ScreenIds.Skills: Skills.OnLeave(); break;
                case ScreenIds.Companions: Companions.OnLeave(); break;
            }
        }

        // ------------------------------------------------------------------------------------------ events

        void OnSimEvent(Event e)
        {
            _dirty = true;
            double now = _now();
            Main.OnEvent(e, now);
            Hero.OnEvent(e);
            Weapons.OnEvent(e);
            Skills.OnEvent(e);
            Companions.OnEvent(e);
            Summon.OnEvent(e);
            switch (e)
            {
                case ItemAcquired _:
                case ItemEquipped _:
                case ItemLimitBroken _:
                case CurrencyChanged cc when cc.Currency == Currency.ROCK_CANDY:   // the Limit break button's affordability
                    _detailDirty = true;
                    break;
                case DayRolledOver _:
                    Toasts.Push(Localizer.Text(UiKeys.ToastNewDay), ToastStyle.Info);
                    break;
                case CurrencyChanged c when c.Reason == CurrencyReason.LOGIN && c.Delta > 0:
                    Toasts.Push(Localizer.Format(UiKeys.ToastLogin, TextFmt.IntText((long)c.Delta)), ToastStyle.Reward);
                    break;
            }
        }

        void OnRejected(CommandRejected r)
        {
            _dirty = true;
            if (_inSend) return;   // Send renders its own reject with the button anchor
            RejectFeedback(r.Result, null);
        }

        // ------------------------------------------------------------------------------------------ commands

        /// <summary>
        /// <c>Commands.Send(cmd)</c>: applies a player command now (a button press, never an EventBus handler). A reject shakes
        /// <paramref name="anchorId"/> and toasts <c>reject.&lt;reason_lower&gt;</c> under it in the same frame.
        /// </summary>
        public ApplyResult Send(Command command, string anchorId = null)
        {
            _inSend = true;
            ApplyResult r;
            try
            {
                r = _runner.Issue(command);
            }
            finally
            {
                _inSend = false;
            }
            _dirty = true;
            if (!r.Ok) RejectFeedback(r, anchorId);
            return r;
        }

        /// <summary>Alias of <see cref="Send"/> without an anchor.</summary>
        public ApplyResult Apply(Command c) => Send(c);

        void RejectFeedback(ApplyResult r, string anchorId)
        {
            Toasts.Push(Localizer.Text(ErrorClassifier.RejectKey(r)), ToastStyle.Reject, UiConstants.ToastSec, anchorId);
            if (anchorId != null) Cues.Push(CueKind.Shake, id: anchorId);
            Cues.Feel(Haptic.Light, Sounds.UiDeny);
            Main.Board.OnRejected(r);
        }

        // ------------------------------------------------------------------------------------------ input and lifecycle

        /// <summary>Android back (07 section 1 rule 5). The caller quits the app on <see cref="BackResult.Quit"/>.</summary>
        public BackResult Back() => Router.Back(_now());

        /// <summary>The app went to the background: a drag is cancelled, pending taps dropped (07 section 5).</summary>
        public void OnPause()
        {
            Main.Board.Cancel();
            Main.Combat.Taps.Cancel();
        }

        /// <summary>Back in the foreground, after the runner's resume catch-up: SCR_OFFLINE_REWARD if a reward is pending.</summary>
        public void OnResume()
        {
            OfflineReward.CheckPending(_sim);
            _dirty = true;
        }

        // ------------------------------------------------------------------------------------------ debug (dev builds)

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

        /// <summary>
        /// Credits <paramref name="amount"/> of <paramref name="currency"/> through the normal admin-grant command, so the
        /// grant is logged, replayable and saved like any other (06 rule 9.4). Each call is a new grant id.
        /// </summary>
        public ApplyResult DebugGrant(Currency currency, long amount) =>
            Send(new ApplyInboxGrant(new InboxGrantPayload("debug-" + Guid.NewGuid(),
                new[] { new GrantLine(Simulation.GrantKindCurrency, currency.ToString(), amount) }, HeroCat.Sim.Economy.CurrencyReason.ADMIN)));
        public void DebugRequestSave() => _saveRequested = true;
        public void DebugResetProfile() => ResetRequested = true;
    }
}
