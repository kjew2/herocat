using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// A Full screen (07 section 2): pushed over the combat viewport, action strip and Fish Board, a 96 rp title row with
    /// the back chevron, scrolling content below. Unity-only: not compiled in cloud sessions.
    /// </summary>
    public abstract class FullScreenView : ScreenView
    {
        protected RectTransform Content;
        protected abstract string TitleKey { get; }

        protected override void Build()
        {
            UiKit.Fill(UiKit.Panel("Bg", Root, Palette.Background, 1f, raycast: true).rectTransform);
            var title = UiKit.TopBand(UiKit.Rect("TitleRow", Root), 0, UiConstants.TitleRow);
            UiKit.Fill(UiKit.Panel("Bg", title, Palette.Panel).rectTransform);
            var back = UiKit.Button("Back", title, () => Vm.Router.PopFull(), Palette.Panel, UiKit.Title);
            UiKit.Place(back.GetComponent<RectTransform>(), 0, 0, 112, UiConstants.TitleRow);
            UiKit.LabelOf(back).text = "‹";   // back chevron glyph
            var t = UiKit.Text("Title", title, UiKit.Title, Palette.Text, TextAlignmentOptions.MidlineLeft, bold: true);
            UiKit.Place(t.rectTransform, 128, 0, 900, UiConstants.TitleRow);
            t.text = Vm.Localizer.Text(TitleKey);

            var scroll = UiKit.Rect("Scroll", Root);
            UiKit.Fill(scroll, 0, UiConstants.TitleRow, 0, 0);
            var sr = scroll.gameObject.AddComponent<ScrollRect>();
            scroll.gameObject.AddComponent<RectMask2D>();
            sr.horizontal = false;
            Content = UiKit.Rect("Content", scroll);
            Content.anchorMin = new Vector2(0, 1);
            Content.anchorMax = new Vector2(1, 1);
            Content.pivot = new Vector2(0.5f, 1);
            Content.sizeDelta = new Vector2(0, 2000);
            sr.content = Content;
            sr.viewport = scroll;
            BuildContent();
        }

        protected abstract void BuildContent();

        protected void SetContentHeight(float h) => Content.sizeDelta = new Vector2(0, h);

        protected TextMeshProUGUI Line(string name, float y, float size = UiKit.Body, uint color = Palette.Text, float x = 24, float w = 1032, float h = 56)
        {
            var t = UiKit.Text(name, Content, size, color);
            UiKit.Place(t.rectTransform, x, y, w, h);
            return t;
        }

        protected Button Btn(string name, float x, float y, float w, float h, UnityEngine.Events.UnityAction tap, uint color = 0x37474F, float size = UiKit.Body)
        {
            var b = UiKit.Button(name, Content, tap, color, size);
            UiKit.Place(b.GetComponent<RectTransform>(), x, y, w, h);
            return b;
        }
    }

    /// <summary>Places a thin marker at a fraction of its parent bar (the soft-pity tick on the pity bar).</summary>
    public sealed class FillMarkerSink : IFillSink
    {
        readonly RectTransform _rt;
        public FillMarkerSink(RectTransform rt) { _rt = rt; }

        public void SetFill(float f)
        {
            _rt.anchorMin = new Vector2(f, 0);
            _rt.anchorMax = new Vector2(f, 1);
            _rt.sizeDelta = new Vector2(6, 0);
            _rt.anchoredPosition = Vector2.zero;
        }
    }
}
