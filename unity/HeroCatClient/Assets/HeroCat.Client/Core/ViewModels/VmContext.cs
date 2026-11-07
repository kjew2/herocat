using System;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// <c>Commands.Send(cmd)</c> (07 sections 3 and 16): the one door from a screen to the sim. A reject renders as 01's
    /// <c>Reject</c> presentation: a shake of the pressed button (<paramref name="anchorId"/>) and a toast under it keyed
    /// <c>reject.&lt;reason_lower&gt;</c>, in the same frame (07 section 10 rule 10).
    /// </summary>
    public interface ICommandSender
    {
        ApplyResult Send(Command command, string anchorId = null);
    }

    /// <summary>What every screen vm shares. Owned by <see cref="GameViewModel"/>; holds no Simulation reference.</summary>
    public sealed class VmContext
    {
        public VmContext(ContentDb db, ILocalizer loc, ICommandSender commands, ToastQueue toasts, CueQueue cues, IClientPrefs prefs,
                         ScreenRouter router, Func<double> now)
        {
            Db = db ?? throw new ArgumentNullException(nameof(db));
            Loc = loc ?? Localizer.Keys;
            Commands = commands;
            Toasts = toasts;
            Cues = cues;
            Prefs = prefs ?? new MemoryClientPrefs();
            Router = router;
            Now = now ?? (() => 0.0);
        }

        public ContentDb Db { get; }
        public ILocalizer Loc { get; }
        public ICommandSender Commands { get; }
        public ToastQueue Toasts { get; }
        public CueQueue Cues { get; }
        public IClientPrefs Prefs { get; }
        public ScreenRouter Router { get; }

        /// <summary>Monotonic seconds (the runner's mono clock), for toasts, throttles and animations.</summary>
        public Func<double> Now { get; }

        /// <summary>
        /// The kill switches in force (IConfigService.Flags; 01 section 8.12 step 1), read on every refresh so a switch
        /// applies within one config refresh. Null: everything on.
        /// </summary>
        public Func<KillSwitches> Flags { get; set; }

        /// <summary>The kill switches now (everything on without a source).</summary>
        public KillSwitches Switches => Flags?.Invoke() ?? KillSwitches.AllOn;

        /// <summary>
        /// The hot-applied shop and inbox tables (00 contract 3, 01 section 8.12 step 2; issue #299): null until a hot bundle
        /// was swapped in this session. The Shop and Inbox view-models read their rows from here when set, from
        /// <see cref="Db"/> otherwise. TODO(#299): ShopVm and InboxVm do not exist yet (#184); they subscribe to
        /// <see cref="HotContentApplied"/> and re-read their rows when they land.
        /// </summary>
        public HotContentSet HotContent { get; private set; }

        /// <summary>Raised on the main thread after a hot apply, with the swapped tables.</summary>
        public event Action<HotContentSet> HotContentApplied;

        /// <summary>The GameViewModel calls this from <c>ContentUpdater.HotApplied</c>.</summary>
        public void PublishHotContent(HotContentSet set)
        {
            if (set == null) return;
            HotContent = set;
            HotContentApplied?.Invoke(set);
        }

        public Constants K => Db.Constants;

        /// <summary>
        /// Slot mode (07 section 3, 00 decision 109): the party slot (0-based) a collection screen equips into when pushed
        /// from a SCR_PARTY slot card; null when opened from the tab bar (Equip then asks for the slot).
        /// </summary>
        public int? TargetSlot { get; set; }

        public TextKeyLabel Label() => new TextKeyLabel(Loc);

        public ButtonState Button() => new ButtonState(Loc);

        /// <summary>A localized info toast.</summary>
        public void Toast(string key, ToastStyle style = ToastStyle.Info, double sec = UiConstants.ToastSec) => Toasts?.Push(Loc.Text(key), style, sec);

        public void Toast(string key, string arg, ToastStyle style = ToastStyle.Info, double sec = UiConstants.ToastSec) =>
            Toasts?.Push(Loc.Format(key, arg), style, sec);

        /// <summary>A target this build does not have yet (MVP screens): a lock toast naming the condition (issue #111).</summary>
        public void LockedToast(string feature)
        {
            string key = null;
            if (feature != null && Db.FeatureUnlocks != null && Db.FeatureUnlocks.TryGetValue(feature, out var f)) key = f.LockedTextKey;
            Toast(string.IsNullOrEmpty(key) ? UiKeys.ComingSoon : key, ToastStyle.Reject);
        }
    }
}
