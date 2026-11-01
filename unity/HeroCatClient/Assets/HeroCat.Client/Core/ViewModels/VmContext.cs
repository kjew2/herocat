using System;
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

        public Constants K => Db.Constants;

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
