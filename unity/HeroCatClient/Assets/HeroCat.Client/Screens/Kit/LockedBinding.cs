using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// The view half of the shared Locked presentation (07 section 3, section 10 rule 8; Core's <see cref="LockedVm"/>):
    /// while locked the element stays visible and tappable, greyed to the disabled alpha with a lock glyph in its top-right
    /// corner; the tap goes to <c>LockedVm.Tap()</c>, which toasts the unlock condition. Any screen can call it.
    /// </summary>
    public static class LockedBinding
    {
        /// <summary>The placeholder lock glyph: the M0 font has no emoji (07 section 15.1).</summary>
        public const string Glyph = "⊘";

        /// <summary>
        /// Adds the glyph to <paramref name="target"/> and binds <c>lockVm.Locked</c> to it and to the greying. The glyph
        /// sits at (x, y) inside the target, 48 x 40 rp. <paramref name="condition"/>, when given, shows the condition text.
        /// </summary>
        public static TextMeshProUGUI Bind(LockedVm lockVm, RectTransform target, float x, float y, TMP_Text condition = null)
        {
            var glyph = UiKit.Text("Lock", target, UiKit.Caption, Palette.Muted, TextAlignmentOptions.Center);
            UiKit.Place(glyph.rectTransform, x, y, 48, 40);
            glyph.text = Glyph;
            var group = target.GetComponent<CanvasGroup>();   // no ?? on a UnityEngine.Object: its null is overloaded
            if (group == null) group = target.gameObject.AddComponent<CanvasGroup>();
            lockVm.Locked.Bind(new ActionFlagSink(on =>
            {
                glyph.gameObject.SetActive(on);
                if (group != null) group.alpha = on ? Palette.DisabledAlpha : 1f;
                if (condition != null) condition.gameObject.SetActive(on);
            }));
            if (condition != null) lockVm.Condition.Bind(condition.Sink());
            return glyph;
        }
    }
}
