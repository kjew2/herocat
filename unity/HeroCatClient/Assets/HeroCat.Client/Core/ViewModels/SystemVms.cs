using System;
using System.Collections.Generic;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Errors;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// SCR_BOOT (07 section 4.1; issue #118): the weighted progress bar (content 40, config 10, auth 10, save 20, catch-up
    /// 10, warm-up 10), the step key <c>boot.step.*</c> and the version caption. It reacts to BootFlow's step callback, not
    /// the sim. Back is ignored. Config and auth are MVP steps that Local mode completes at once.
    /// </summary>
    public sealed class BootVm
    {
        static readonly int[] Weights = { 40, 10, 10, 20, 10, 10 };   // BootStep order: Content, Config, Auth, Save, CatchUp, Handoff

        public BootVm(ILocalizer loc = null)
        {
            StepKey = new TextKeyLabel(loc);
            Caption = new TextKeyLabel(loc);
            StepKey.Set(UiKeys.BootStepContent);
        }

        public readonly BarFill Progress = new BarFill();
        public readonly TextKeyLabel StepKey;
        public readonly TextKeyLabel Caption;

        public double Progress01 { get; private set; }
        public BootStep? Current { get; private set; }

        /// <summary>BootFlow's progress callback: <paramref name="step"/> is starting, so every earlier step is done.</summary>
        public void OnStep(BootStep step)
        {
            Current = step;
            int done = 0;
            for (int i = 0; i < (int)step && i < Weights.Length; i++) done += Weights[i];
            Progress01 = done / 100.0;
            Progress.Set(Progress01);
            StepKey.Set(UiKeys.BootStepKey(step));
        }

        public void OnDone()
        {
            Progress01 = 1.0;
            Progress.Set(1.0);
        }

        public void SetVersions(string clientVersion, string contentVersion, string buildHash) =>
            Caption.Set(UiKeys.BootCaption, clientVersion ?? "-", contentVersion ?? "-", buildHash ?? "-");
    }

    /// <summary>The actions 01 section 7's per-code table offers (07 section 4.27).</summary>
    public enum ErrorAction { Retry, UpdateApp, ResetProfile, RestoreCloud, ContactSupport, Dismiss }

    /// <summary>
    /// SCR_ERROR (07 section 4.27; issue #118): 01's taxonomy rendered. Blocking variant above everything for Fatal,
    /// Block (maintenance, CLIENT_TOO_OLD), boot failures, modal server rejections and exhausted retries; the Offline class
    /// is the non-blocking card in the action-strip position. Never two at once: a new error replaces the visible one and
    /// the replaced code is logged (<see cref="Replaced"/>). Keys are <c>err.&lt;code_lower&gt;.title/.body</c> (issue #248).
    /// </summary>
    public sealed class ErrorVm
    {
        readonly ScreenRouter _router;
        readonly List<ErrorAction> _actions = new List<ErrorAction>();
        readonly List<string> _replaced = new List<string>();

        public ErrorVm(ILocalizer loc, ScreenRouter router)
        {
            _router = router;
            Title = new TextKeyLabel(loc);
            Body = new TextKeyLabel(loc);
            CodeLine = new TextKeyLabel(loc);
        }

        public UserError Error { get; private set; }
        public string Code => Error?.Code;
        public bool Blocking { get; private set; }
        public IReadOnlyList<ErrorAction> Actions => _actions;
        public int ActionsVersion { get; private set; }

        public readonly TextKeyLabel Title, Body;

        /// <summary>The code in small text for support, with the support id (07 section 10 rule 7).</summary>
        public readonly TextKeyLabel CodeLine;

        public readonly Flag Visible = new Flag();

        /// <summary>The non-blocking Offline card (action-strip position).</summary>
        public readonly Flag CardVisible = new Flag();

        /// <summary>Codes replaced by a newer error, oldest first (07 section 4.27 "the replaced code is logged").</summary>
        public IReadOnlyList<string> Replaced => _replaced;

        /// <summary>Also called with each replaced code (the Unity layer logs it).</summary>
        public Action<string> Log { get; set; }

        /// <summary>Action handlers (Retry re-runs boot, Reset deletes the profile, ...). Dismiss is handled here.</summary>
        public Action<ErrorAction> OnAction { get; set; }

        /// <summary>Shows <paramref name="e"/>; <paramref name="bootFailure"/> forces the blocking variant with Retry.</summary>
        public void Show(UserError e, bool bootFailure = false)
        {
            if (e == null) return;
            if (Error != null && (Visible.Value || CardVisible.Value))
            {
                _replaced.Add(Error.Code);
                Log?.Invoke(Error.Code);
            }
            Error = e;
            Title.Set(e.TitleKey);   // Runtime's ErrorClassifier owns the key casing (issue #248)
            Body.Set(e.BodyKey);
            CodeLine.Set(UiKeys.ErrCodeLine, e.Code ?? "", e.SupportId ?? "");
            _actions.Clear();
            BuildActions(e, bootFailure);
            ActionsVersion++;

            bool card = e.Presentation == Presentation.Card && !bootFailure;
            Blocking = !card;
            CardVisible.Set(card);
            Visible.Set(!card);
            if (card) _router?.CloseBlockingError();
            else _router?.ShowBlockingError(new ScreenEntry(ScreenIds.Error, e.Code));
        }

        void BuildActions(UserError e, bool boot)
        {
            if (e.Code == "CLIENT_TOO_OLD") { _actions.Add(ErrorAction.UpdateApp); return; }   // only Update (07 section 4.1)
            switch (e.Code)
            {
                case ErrorClassifier.SAVE_CORRUPT:
                    _actions.Add(ErrorAction.Retry);
                    _actions.Add(ErrorAction.ResetProfile);   // two-step confirm; [Restore from cloud] is MVP
                    return;
            }
            switch (e.Class)
            {
                case ErrorClass.Fatal:
                    if (boot && e.Code == ErrorClassifier.CONTENT_CORRUPT) _actions.Add(ErrorAction.Retry);
                    _actions.Add(ErrorAction.ContactSupport);
                    return;
                case ErrorClass.Maintenance:
                    _actions.Add(ErrorAction.Retry);
                    return;
                case ErrorClass.Offline:
                    if (boot) _actions.Add(ErrorAction.Retry);
                    return;
                case ErrorClass.Timeout:
                case ErrorClass.ServerError:
                    _actions.Add(ErrorAction.Retry);
                    _actions.Add(ErrorAction.Dismiss);   // [Later]
                    return;
                default:
                    if (boot) _actions.Add(ErrorAction.Retry);
                    _actions.Add(ErrorAction.Dismiss);
                    return;
            }
        }

        public void Act(ErrorAction a)
        {
            if (a == ErrorAction.Dismiss)
            {
                Hide();
                return;
            }
            OnAction?.Invoke(a);
        }

        /// <summary>The Offline card auto-hides on reconnect; a modal hides on Dismiss.</summary>
        public void Hide()
        {
            Visible.Set(false);
            CardVisible.Set(false);
            _router?.CloseBlockingError();
            Error = null;
        }
    }

    /// <summary>
    /// What SCR_CONFIRM shows (07 section 4.26): title and body keys with args, an optional cost line with the balance
    /// after, confirm / cancel keys, the destructive style and an optional typed confirmation.
    /// </summary>
    public sealed class ConfirmRequest
    {
        public string TitleKey, BodyKey, ConfirmKey = UiKeys.ConfirmOk, CancelKey = UiKeys.ConfirmCancel;
        public string[] BodyArgs;
        public Currency? CostCurrency;
        public double CostAmount, BalanceAfter;
        public bool Destructive;

        /// <summary>When set, Confirm is enabled only once the player typed exactly this ("DELETE").</summary>
        public string TypedConfirmation;

        public Action OnConfirm, OnCancel;

        /// <summary>The router's exit confirm (07 section 1 rule 5): back again within 2 s quits.</summary>
        public bool IsExit;
    }

    /// <summary>SCR_CONFIRM's vm (07 section 4.26; issue #118). Cancel, the scrim and back send nothing.</summary>
    public sealed class ConfirmVm
    {
        readonly ScreenRouter _router;
        string _typed = "";

        public ConfirmVm(ILocalizer loc, ScreenRouter router)
        {
            _router = router;
            Title = new TextKeyLabel(loc);
            Body = new TextKeyLabel(loc);
            CostLine = new TextKeyLabel(loc);
            Confirm = new ButtonState(loc);
            Cancel = new ButtonState(loc);
        }

        public ConfirmRequest Request { get; private set; }
        public readonly TextKeyLabel Title, Body, CostLine;
        public readonly Flag CostVisible = new Flag();
        public readonly Flag Destructive = new Flag();
        public readonly Flag TypedVisible = new Flag();
        public readonly ButtonState Confirm, Cancel;

        /// <summary>Requests the modal (queued behind a visible one by the router).</summary>
        public void Ask(ConfirmRequest r)
        {
            if (r == null) return;
            _router?.ShowModal(ScreenIds.Confirm, r);
        }

        /// <summary>The view calls this when the router shows a SCR_CONFIRM entry.</summary>
        public void Bind(ConfirmRequest r)
        {
            Request = r;
            _typed = "";
            if (r == null) return;
            Title.Set(r.TitleKey);
            var a = r.BodyArgs;
            if (a == null || a.Length == 0) Body.Set(r.BodyKey);
            else if (a.Length == 1) Body.Set(r.BodyKey, a[0]);
            else if (a.Length == 2) Body.Set(r.BodyKey, a[0], a[1]);
            else Body.Set(r.BodyKey, a[0], a[1], a[2]);
            CostVisible.Set(r.CostCurrency.HasValue);
            if (r.CostCurrency.HasValue)
                CostLine.Set(UiKeys.ConfirmCost, UiKeys.CurrencyKey(r.CostCurrency.Value), Amount(r.CostCurrency.Value, r.CostAmount),
                             Amount(r.CostCurrency.Value, r.BalanceAfter));
            Destructive.Set(r.Destructive);
            TypedVisible.Set(!string.IsNullOrEmpty(r.TypedConfirmation));
            Confirm.Label.Set(r.ConfirmKey);
            Cancel.Label.Set(r.CancelKey);
            Cancel.Enabled.Set(true);
            Cancel.Visible.Set(true);
            Confirm.Visible.Set(true);
            Confirm.Enabled.Set(string.IsNullOrEmpty(r.TypedConfirmation));
        }

        static LabelArg Amount(Currency c, double v) =>
            HeroCat.Sim.State.Wallet.IsCounted(c) ? LabelArg.Int((long)v) : LabelArg.Num(v);

        public void OnTyped(string text)
        {
            _typed = text ?? "";
            if (Request != null && !string.IsNullOrEmpty(Request.TypedConfirmation))
                Confirm.Enabled.Set(string.Equals(_typed, Request.TypedConfirmation, StringComparison.Ordinal));
        }

        public void OnConfirm()
        {
            var r = Request;
            if (r == null || !Confirm.Enabled.Value) return;
            Request = null;
            _router?.CloseModal(ScreenIds.Confirm);
            r.OnConfirm?.Invoke();
        }

        /// <summary>Cancel, scrim or back: sends nothing.</summary>
        public void OnCancel()
        {
            var r = Request;
            Request = null;
            _router?.CloseModal(ScreenIds.Confirm);
            r?.OnCancel?.Invoke();
        }

        /// <summary>The router dismissed the modal (scrim or back).</summary>
        public void OnDismissed()
        {
            var r = Request;
            Request = null;
            r?.OnCancel?.Invoke();
        }
    }

    /// <summary>
    /// SCR_OFFLINE_REWARD (07 section 4.18, 06 section 7.3, 01 section 8.7; issue #118): the <c>Offline.Pending</c> record
    /// plus formatted strings. Ticks stay off while it is open, so the numbers shown are exactly what is credited: the
    /// SessionRunner holds them while <c>Offline.Pending</c> is set (decision 53), and Claim sends
    /// <c>ClaimOfflineReward</c>, which credits the reward and clears it. Claim is mandatory: no scrim, no back. If nothing
    /// is pending when it would show, it pops silently.
    /// </summary>
    public sealed class OfflineRewardVm
    {
        public const string AnchorClaim = "offline.claim";

        readonly VmContext _c;

        public OfflineRewardVm(VmContext c)
        {
            _c = c;
            Away = c.Label();
            Rewards = c.Label();
            TankLine = c.Label();
            StageLine = c.Label();
            Claim = c.Button();
            Claim.Label.Set(UiKeys.OfflineClaim);
        }

        /// <summary>The command [Claim] sends (06 section 7.3).</summary>
        public Func<Command> ClaimCommand { get; set; } = () => new ClaimOfflineReward();

        public CatchupResult Pending { get; private set; }

        public readonly TextKeyLabel Away, Rewards, TankLine, StageLine;
        public readonly NumberLabel Gold = new NumberLabel(NumberStyle.Short, "+");
        public readonly NumberLabel Exp = new NumberLabel(NumberStyle.Short, "+");
        public readonly Flag Capped = new Flag();
        public readonly Flag TankVisible = new Flag();

        /// <summary>P1 Claim x2: hidden.</summary>
        public readonly Flag ClaimDoubleVisible = new Flag();

        public readonly ButtonState Claim;

        /// <summary>
        /// Boot and resume: when <c>Offline.Pending</c> is set (the runner is then holding ticks), request the modal (the
        /// router holds it back during a boss). Returns true when the modal was requested.
        /// </summary>
        public bool CheckPending(Simulation sim)
        {
            var p = sim.State.Offline.Pending;
            if (p == null) return false;
            if (_c.Router != null && (_c.Router.IsOpen(ScreenIds.OfflineReward) || _c.Router.IsQueued(ScreenIds.OfflineReward)))
            {
                Fill(p);
                return true;
            }
            Fill(p);
            _c.Router?.ShowModal(ScreenIds.OfflineReward, p);
            return true;
        }

        /// <summary>Called when the router shows the modal: a double-show race with nothing pending pops it silently.</summary>
        public void OnShown(Simulation sim)
        {
            var p = sim.State.Offline.Pending;
            if (p == null)
            {
                _c.Router?.CloseModal(ScreenIds.OfflineReward);
                return;
            }
            Fill(p);
        }

        void Fill(CatchupResult p)
        {
            Pending = p;
            var k = _c.K;
            Away.Set(UiKeys.OfflineAway, LabelArg.Num(p.ElapsedSec, NumberStyle.Duration));
            double capH = k.OfflineCapH;   // + 2 h per RES_OFFLINE_CAP level (MVP Research, issue #21)
            Capped.Set(p.Capped);
            if (p.Capped) Rewards.Set(UiKeys.OfflineRewardsCapped, LabelArg.Num(p.CreditedSec, NumberStyle.Duration), LabelArg.Num(capH * 3600, NumberStyle.Duration));
            else Rewards.Set(UiKeys.OfflineRewards, LabelArg.Num(p.CreditedSec, NumberStyle.Duration));
            Gold.Set(p.Gold);
            Exp.Set(p.Exp);
            TankVisible.Set(p.TankFill > 0);
            TankLine.Set(UiKeys.OfflineTank, p.TankFill);
            double rate = p.CreditedSec > 0 ? p.Gold / p.CreditedSec : 0;
            StageLine.Set(UiKeys.OfflineStage, p.Stage, LabelArg.Num(rate));
            ClaimDoubleVisible.Set(false);
            Claim.Visible.Set(true);
            Claim.Enabled.Set(true);
        }

        /// <summary>[Claim]: ClaimOfflineReward (which releases the runner's tick hold), the gold burst into the HUD chip, pop
        /// (06 section 7.3). A rejected claim keeps the modal open.</summary>
        public void OnClaim()
        {
            var make = ClaimCommand;
            if (make != null)
            {
                var r = _c.Commands != null ? _c.Commands.Send(make(), AnchorClaim) : ApplyResult.Accepted;
                if (!r.Ok) return;
            }
            _c.Cues?.Push(CueKind.GoldBurst, value: Pending?.Gold ?? 0);
            _c.Cues?.Feel(Haptic.Light, Sounds.Coins);
            Pending = null;
            _c.Router?.CloseModal(ScreenIds.OfflineReward);
        }
    }
}
