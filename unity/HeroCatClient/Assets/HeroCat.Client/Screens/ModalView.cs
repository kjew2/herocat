using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>A modal card (07 section 2): 960 rp wide, centred (or over the board during a boss), scrim behind.</summary>
    public abstract class ModalView : ScreenView
    {
        protected RectTransform Card;

        protected override void Build()
        {
            Card = UiKit.Panel("Card", Root, Palette.PanelRaised, 1f, raycast: true).rectTransform;
            UiKit.Fill(Card);
            BuildCard();
        }

        protected abstract void BuildCard();

        protected TextMeshProUGUI Line(string name, float y, float size = UiKit.Body, uint color = Palette.Text, float h = 56,
                                       TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var t = UiKit.Text(name, Card, size, color, align);
            UiKit.Place(t.rectTransform, 32, y, UiConstants.ModalWidth - 64, h);
            return t;
        }

        protected Button Btn(string name, float x, float y, float w, UnityEngine.Events.UnityAction tap, uint color = 0x37474F)
        {
            var b = UiKit.Button(name, Card, tap, color, UiKit.Body);
            UiKit.Place(b.GetComponent<RectTransform>(), x, y, w, 128);
            return b;
        }
    }

    /// <summary>The error card's contents, shared by the in-game modal and the boot screen.</summary>
    public static class ErrorViews
    {
        public static void Build(RectTransform card, ErrorVm e, ILocalizer loc, out RectTransform actions)
        {
            var title = UiKit.Text("Title", card, UiKit.Title, Palette.Bad, TextAlignmentOptions.Center, bold: true);
            UiKit.Place(title.rectTransform, 32, 32, UiConstants.ModalWidth - 64, 72);
            e.Title.Bind(title.Sink());
            var body = UiKit.Text("Body", card, UiKit.Body, Palette.Text, TextAlignmentOptions.Center);
            UiKit.Place(body.rectTransform, 32, 120, UiConstants.ModalWidth - 64, 240);
            e.Body.Bind(body.Sink());
            var code = UiKit.Text("Code", card, UiKit.Micro, Palette.Muted, TextAlignmentOptions.Center);
            UiKit.Place(code.rectTransform, 32, 372, UiConstants.ModalWidth - 64, 40);
            e.CodeLine.Bind(code.Sink());   // the code in small text for support (07 section 10 rule 7)
            actions = UiKit.Rect("Actions", card);
            UiKit.Place(actions, 32, 440, UiConstants.ModalWidth - 64, 280);
        }

        public static void Actions(RectTransform actions, ErrorVm e, ILocalizer loc)
        {
            for (int i = actions.childCount - 1; i >= 0; i--) Object.Destroy(actions.GetChild(i).gameObject);
            var list = e.Actions;
            float w = list.Count > 0 ? (UiConstants.ModalWidth - 64 - (list.Count - 1) * 16) / list.Count : 0;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                var b = UiKit.Button("Action" + a, actions, () => e.Act(a), a == ErrorAction.Dismiss ? Palette.Panel : 0x1565C0, UiKit.Body);
                UiKit.Place(b.GetComponent<RectTransform>(), i * (w + 16), 0, w, 128);
                UiKit.LabelOf(b).text = loc.Text(UiKeys.ErrorActionKey(a));
            }
        }
    }
}
