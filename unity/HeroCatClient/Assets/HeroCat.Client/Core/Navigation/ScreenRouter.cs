using System;
using System.Collections.Generic;

namespace HeroCat.Client.Core
{
    /// <summary>07 section 1: Full replaces the content area, Modal is a card over a scrim, Overlay draws over SCR_MAIN.</summary>
    public enum ScreenType { Full, Modal, Overlay }

    /// <summary>00 section 5.3's screen ids, with each screen's type, tier and dismiss rules.</summary>
    public static class ScreenIds
    {
        public const string Boot = "SCR_BOOT", Title = "SCR_TITLE", Main = "SCR_MAIN", Hero = "SCR_HERO", Weapons = "SCR_WEAPONS",
                            Skills = "SCR_SKILLS", Companions = "SCR_COMPANIONS", Runes = "SCR_RUNES", Research = "SCR_RESEARCH",
                            Summon = "SCR_SUMMON", SummonResult = "SCR_SUMMON_RESULT", Dungeons = "SCR_DUNGEONS", DungeonRun = "SCR_DUNGEON_RUN",
                            StageSelect = "SCR_STAGE_SELECT", Quests = "SCR_QUESTS", Shop = "SCR_SHOP", Inbox = "SCR_INBOX",
                            OfflineReward = "SCR_OFFLINE_REWARD", ItemDetail = "SCR_ITEM_DETAIL", Settings = "SCR_SETTINGS",
                            Profile = "SCR_PROFILE", Guild = "SCR_GUILD", Ranking = "SCR_RANKING", Events = "SCR_EVENTS",
                            Tutorial = "SCR_TUTORIAL", Confirm = "SCR_CONFIRM", Error = "SCR_ERROR";

        /// <summary>The screens this build implements (M0, 07 tiers). Others route to a locked toast.</summary>
        public static readonly IReadOnlyList<string> M0 = new[]
        {
            Boot, Main, Hero, Weapons, Skills, Summon, SummonResult, OfflineReward, ItemDetail, Confirm, Error,
        };

        public static bool IsM0(string id)
        {
            for (int i = 0; i < M0.Count; i++) if (M0[i] == id) return true;
            return false;
        }

        public static ScreenType TypeOf(string id)
        {
            switch (id)
            {
                case SummonResult:
                case Tutorial:
                    return ScreenType.Overlay;
                case StageSelect:
                case Inbox:
                case OfflineReward:
                case ItemDetail:
                case Profile:
                case Confirm:
                case Error:
                    return ScreenType.Modal;
                default:
                    return ScreenType.Full;
            }
        }

        /// <summary>Mandatory modals are the only non-dismissible ones (07 section 10 rule 4): SCR_OFFLINE_REWARD.</summary>
        public static bool IsMandatory(string id) => id == OfflineReward;

        /// <summary>Back / scrim dismisses: every modal but the mandatory one; the tutorial overlay is not dismissible.</summary>
        public static bool IsDismissible(string id) => !IsMandatory(id) && id != Tutorial;
    }

    /// <summary>Where a modal card sits (07 section 10 rule 4, Open question 2's default: shift down during a boss).</summary>
    public enum ModalPlacement { Center, BoardRegion }

    /// <summary>A stack entry: <c>ScreenEntry { ScreenId, Type, Instance, Args }</c> (07 section 16).</summary>
    public sealed class ScreenEntry
    {
        public ScreenEntry(string id, object args)
        {
            Id = id;
            Type = ScreenIds.TypeOf(id);
            Args = args;
        }

        public string Id { get; }
        public ScreenType Type { get; }
        public object Args { get; }

        /// <summary>The Unity view instance; the router never looks at it.</summary>
        public object Instance { get; set; }

        public ModalPlacement Placement { get; internal set; }

        public override string ToString() => Id;
    }

    /// <summary>The Unity layer: shows and hides views, releases evicted Full screens (Addressables, 07 section 16).</summary>
    public interface IScreenHost
    {
        void Show(ScreenEntry e);
        void Hide(ScreenEntry e);
        void Evict(string screenId);
    }

    /// <summary>What a back press did (07 section 1 rule 5).</summary>
    public enum BackResult { Ignored, PoppedOverlay, PoppedModal, PoppedFull, SkippedReveal, ShowedExitConfirm, Quit }

    /// <summary>
    /// The navigation stack, modal queue and Android back mapping (07 sections 1, 10 rule 4 and 16; issue #110), engine
    /// free. Rules:
    /// <list type="number">
    /// <item>SCR_MAIN is the stack root and is never popped; Full screens push over it.</item>
    /// <item>Tab taps <see cref="Replace"/> the top Full screen, so the depth stays 2.</item>
    /// <item>At most one modal is visible; further requests queue and show in order (AC 3).</item>
    /// <item>While the enemy is a boss a player-invoked modal shifts to the Fish Board region (Open question 2's default);
    /// SCR_OFFLINE_REWARD is never shown mid-boss and waits in the queue (AC 4).</item>
    /// <item>Back pops the top overlay or modal when dismissible, else the top Full screen; on a bare SCR_MAIN it shows the
    /// exit confirm, and a second press within <see cref="UiConstants.BackExitWindowSec"/> quits. It never pops
    /// SCR_OFFLINE_REWARD and skips a running summon reveal instead of closing it (AC 5).</item>
    /// <item>A blocking SCR_ERROR sits above everything and swallows back.</item>
    /// </list>
    /// </summary>
    public sealed class ScreenRouter
    {
        readonly List<ScreenEntry> _stack = new List<ScreenEntry>();
        readonly List<ScreenEntry> _overlays = new List<ScreenEntry>();
        readonly Queue<ScreenEntry> _modalQueue = new Queue<ScreenEntry>();
        readonly List<string> _lru = new List<string>();
        IScreenHost _host;
        double _exitArmedAt = double.NegativeInfinity;
        ScreenEntry _exitConfirm;

        public ScreenRouter(IScreenHost host = null)
        {
            _host = host;
            var root = new ScreenEntry(ScreenIds.Main, null);
            _stack.Add(root);
        }

        /// <summary>Attach the view layer later (the Unity root builds it after the vm).</summary>
        public void SetHost(IScreenHost host)
        {
            _host = host;
            if (_host == null) return;
            foreach (var e in _stack) _host.Show(e);
            if (Modal != null) _host.Show(Modal);
            foreach (var o in _overlays) _host.Show(o);
            if (BlockingError != null) _host.Show(BlockingError);
        }

        /// <summary>True while the fought enemy is a boss (MainVm); null means never.</summary>
        public Func<bool> EnemyIsBoss { get; set; }

        /// <summary>True while a summon reveal is running; back skips it.</summary>
        public Func<bool> RevealRunning { get; set; }

        /// <summary>Called by back during a reveal (07 section 6 rule 4).</summary>
        public Action SkipReveal { get; set; }

        /// <summary>Builds the exit confirm's args (the GameViewModel supplies a ConfirmRequest).</summary>
        public Func<object> ExitConfirmArgs { get; set; }

        /// <summary>Called whenever a modal is closed by the router (back, scrim): the confirm's Cancel.</summary>
        public Action<ScreenEntry> ModalDismissed { get; set; }

        /// <summary>Called after a screen is shown (the vm layer binds modal args, refreshes the newly visible vm).</summary>
        public Action<ScreenEntry> Shown { get; set; }

        /// <summary>Called after a screen is hidden (a collection screen clears its NEW badges on leave).</summary>
        public Action<ScreenEntry> Hidden { get; set; }

        /// <summary>Bumped on every change; views re-sync when it moves.</summary>
        public int Version { get; private set; }

        public IReadOnlyList<ScreenEntry> Stack => _stack;
        public int Depth => _stack.Count;
        public ScreenEntry TopFull => _stack[_stack.Count - 1];
        public ScreenEntry Modal { get; private set; }
        public int QueuedModals => _modalQueue.Count;
        public IReadOnlyList<ScreenEntry> Overlays => _overlays;
        public ScreenEntry BlockingError { get; private set; }

        /// <summary>The Full screens cached by the view, most recent last (07 section 13 LRU of three).</summary>
        public IReadOnlyList<string> CachedFull => _lru;

        /// <summary>Combat viewport and Fish Board are hidden under Full screens except SCR_DUNGEON_RUN (rule 1).</summary>
        public bool CombatVisible => TopFull.Id == ScreenIds.Main || TopFull.Id == ScreenIds.DungeonRun;

        public bool BoardVisible => TopFull.Id == ScreenIds.Main;

        public bool IsOpen(string id)
        {
            if (TopFull.Id == id) return true;
            if (Modal != null && Modal.Id == id) return true;
            foreach (var o in _overlays) if (o.Id == id) return true;
            return false;
        }

        public bool IsQueued(string id)
        {
            foreach (var e in _modalQueue) if (e.Id == id) return true;
            return false;
        }

        // ------------------------------------------------------------------------------------------ open

        /// <summary>Opens <paramref name="id"/> by its type: Full pushes, Modal shows or queues, Overlay draws on top.</summary>
        public ScreenEntry Push(string id, object args = null)
        {
            switch (ScreenIds.TypeOf(id))
            {
                case ScreenType.Modal: return ShowModal(id, args);
                case ScreenType.Overlay: return ShowOverlay(id, args);
            }
            if (id == ScreenIds.Main) { PopTo(ScreenIds.Main); return TopFull; }
            var e = new ScreenEntry(id, args);
            _stack.Add(e);
            Touch(id);
            ShowE(e);
            Changed();
            return e;
        }

        /// <summary>Tab bar and collection chips: replace the top Full screen (depth stays 2; rule 2).</summary>
        public ScreenEntry Replace(string id, object args = null)
        {
            if (ScreenIds.TypeOf(id) != ScreenType.Full) return Push(id, args);
            if (id == ScreenIds.Main) { PopTo(ScreenIds.Main); return TopFull; }
            if (_stack.Count > 1)
            {
                var top = _stack[_stack.Count - 1];
                if (top.Id == id && Equals(top.Args, args)) return top;
                _stack.RemoveAt(_stack.Count - 1);
                HideE(top);
            }
            return Push(id, args);
        }

        /// <summary>Shows a modal, or queues it behind the visible one (rule 3, AC 3).</summary>
        public ScreenEntry ShowModal(string id, object args = null)
        {
            var e = new ScreenEntry(id, args);
            if (id == ScreenIds.Error)
            {
                ShowBlockingError(e);
                return e;
            }
            if (Modal != null || (ScreenIds.IsMandatory(id) && Boss()))
            {
                if (ScreenIds.IsMandatory(id)) EnqueueFront(e);   // the mandatory claim goes first once it may show
                else _modalQueue.Enqueue(e);
                Changed();
                return e;
            }
            Present(e);
            return e;
        }

        /// <summary>The overlays (SCR_SUMMON_RESULT, SCR_TUTORIAL) draw above modals, never hiding the HUD (rule 4).</summary>
        public ScreenEntry ShowOverlay(string id, object args = null)
        {
            for (int i = 0; i < _overlays.Count; i++)
                if (_overlays[i].Id == id)
                {
                    var old = _overlays[i];
                    HideE(old);
                    _overlays.RemoveAt(i);
                    break;
                }
            var e = new ScreenEntry(id, args);
            _overlays.Add(e);
            ShowE(e);
            Changed();
            return e;
        }

        /// <summary>
        /// A blocking SCR_ERROR above everything. Never two at once: a new one replaces the visible one (ErrorVm logs the
        /// replaced code).
        /// </summary>
        public void ShowBlockingError(ScreenEntry e)
        {
            if (BlockingError != null) HideE(BlockingError);
            BlockingError = e;
            ShowE(e);
            Changed();
        }

        public void CloseBlockingError()
        {
            if (BlockingError == null) return;
            HideE(BlockingError);
            BlockingError = null;
            Changed();
        }

        // ------------------------------------------------------------------------------------------ close

        /// <summary>Closes the visible modal (its own buttons), then shows the next queued one.</summary>
        public void CloseModal()
        {
            if (Modal == null) return;
            var m = Modal;
            Modal = null;
            if (m == _exitConfirm) { _exitConfirm = null; _exitArmedAt = double.NegativeInfinity; }
            HideE(m);
            ShowNextModal();
            Changed();
        }

        /// <summary>Closes a modal by id when it is the visible one, or drops it from the queue.</summary>
        public void CloseModal(string id)
        {
            if (Modal != null && Modal.Id == id) { CloseModal(); return; }
            if (_modalQueue.Count == 0) return;
            int n = _modalQueue.Count;
            for (int i = 0; i < n; i++)
            {
                var e = _modalQueue.Dequeue();
                if (e.Id != id) _modalQueue.Enqueue(e);
            }
            Changed();
        }

        public void CloseOverlay(string id)
        {
            for (int i = 0; i < _overlays.Count; i++)
            {
                if (_overlays[i].Id != id) continue;
                var e = _overlays[i];
                _overlays.RemoveAt(i);
                HideE(e);
                Changed();
                return;
            }
        }

        /// <summary>Pops the top-most thing: an overlay, else the modal, else the top Full screen (never SCR_MAIN).</summary>
        public void Pop()
        {
            if (_overlays.Count > 0) { CloseOverlay(_overlays[_overlays.Count - 1].Id); return; }
            if (Modal != null) { CloseModal(); return; }
            PopFull();
        }

        public void PopFull()
        {
            if (_stack.Count <= 1) return;
            var top = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            HideE(top);
            Changed();
        }

        /// <summary>Pops Full screens until <paramref name="id"/> is on top (SCR_MAIN at the latest).</summary>
        public void PopTo(string id)
        {
            while (_stack.Count > 1 && TopFull.Id != id) PopFull();
        }

        // ------------------------------------------------------------------------------------------ back (rule 5)

        public BackResult Back(double nowSec)
        {
            if (BlockingError != null) return BackResult.Ignored;

            if (_overlays.Count > 0)
            {
                var o = _overlays[_overlays.Count - 1];
                if (o.Id == ScreenIds.SummonResult && RevealRunning != null && RevealRunning())
                {
                    SkipReveal?.Invoke();
                    return BackResult.SkippedReveal;
                }
                if (!ScreenIds.IsDismissible(o.Id)) return BackResult.Ignored;
                CloseOverlay(o.Id);
                return BackResult.PoppedOverlay;
            }

            if (Modal != null)
            {
                if (Modal == _exitConfirm && nowSec - _exitArmedAt <= UiConstants.BackExitWindowSec) return BackResult.Quit;
                if (!ScreenIds.IsDismissible(Modal.Id)) return BackResult.Ignored;
                var m = Modal;
                CloseModal();
                ModalDismissed?.Invoke(m);
                return BackResult.PoppedModal;
            }

            if (_stack.Count > 1)
            {
                PopFull();
                return BackResult.PoppedFull;
            }

            if (nowSec - _exitArmedAt <= UiConstants.BackExitWindowSec) return BackResult.Quit;
            _exitArmedAt = nowSec;
            _exitConfirm = new ScreenEntry(ScreenIds.Confirm, ExitConfirmArgs?.Invoke());
            Present(_exitConfirm);
            return BackResult.ShowedExitConfirm;
        }

        /// <summary>A scrim tap: dismisses the visible modal when it is dismissible (equals Cancel).</summary>
        public bool ScrimTap()
        {
            if (Modal == null || !ScreenIds.IsDismissible(Modal.Id)) return false;
            var m = Modal;
            CloseModal();
            ModalDismissed?.Invoke(m);
            return true;
        }

        /// <summary>Per frame: a mandatory modal held back by a boss shows once the boss fight ends.</summary>
        public void Update()
        {
            if (Modal == null && _modalQueue.Count > 0) ShowNextModal();
        }

        // ------------------------------------------------------------------------------------------ internals

        bool Boss() => EnemyIsBoss != null && EnemyIsBoss();

        void Present(ScreenEntry e)
        {
            e.Placement = Boss() && !ScreenIds.IsMandatory(e.Id) ? ModalPlacement.BoardRegion : ModalPlacement.Center;
            Modal = e;
            ShowE(e);
            Changed();
        }

        void ShowNextModal()
        {
            if (_modalQueue.Count == 0) return;
            var next = _modalQueue.Peek();
            if (ScreenIds.IsMandatory(next.Id) && Boss()) return;   // never shown mid-boss
            _modalQueue.Dequeue();
            Present(next);
        }

        void EnqueueFront(ScreenEntry e)
        {
            var rest = _modalQueue.ToArray();
            _modalQueue.Clear();
            _modalQueue.Enqueue(e);
            foreach (var r in rest) _modalQueue.Enqueue(r);
        }

        void Touch(string id)
        {
            _lru.Remove(id);
            _lru.Add(id);
            while (_lru.Count > UiConstants.ScreenCache)
            {
                string victim = null;
                for (int i = 0; i < _lru.Count; i++)
                {
                    if (OnStack(_lru[i])) continue;
                    victim = _lru[i];
                    break;
                }
                if (victim == null) break;
                _lru.Remove(victim);
                _host?.Evict(victim);
            }
        }

        bool OnStack(string id)
        {
            foreach (var e in _stack) if (e.Id == id) return true;
            return false;
        }

        void Changed() => Version++;

        void ShowE(ScreenEntry e)
        {
            _host?.Show(e);
            Shown?.Invoke(e);
        }

        void HideE(ScreenEntry e)
        {
            _host?.Hide(e);
            Hidden?.Invoke(e);
        }
    }
}
