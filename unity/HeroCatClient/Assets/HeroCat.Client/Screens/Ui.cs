using System;
using HeroCat.Client.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// Tiny UI Toolkit construction kit: every element is styled inline from C# so the project needs no UXML/USS
    /// assets and still looks the same with or without a theme. Setters are change-only (specs/07 section 7.3).
    /// </summary>
    public static class Ui
    {
        public const int Body = 30, Small = 24, Big = 40;
        public const uint ButtonBg = 0x37474F, ButtonBorder = 0x78909C;

        public static Color C(uint rgb, float a = 1f) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

        public static void Pad(VisualElement e, float v) => Pad(e, v, v);

        public static void Pad(VisualElement e, float vertical, float horizontal)
        {
            e.style.paddingTop = vertical; e.style.paddingBottom = vertical;
            e.style.paddingLeft = horizontal; e.style.paddingRight = horizontal;
        }

        public static void Margin(VisualElement e, float v)
        {
            e.style.marginTop = v; e.style.marginBottom = v; e.style.marginLeft = v; e.style.marginRight = v;
        }

        public static void Border(VisualElement e, float width, uint rgb)
        {
            e.style.borderTopWidth = width; e.style.borderBottomWidth = width;
            e.style.borderLeftWidth = width; e.style.borderRightWidth = width;
            Color c = C(rgb);
            e.style.borderTopColor = c; e.style.borderBottomColor = c;
            e.style.borderLeftColor = c; e.style.borderRightColor = c;
        }

        public static void Radius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        public static VisualElement Row()
        {
            var e = new VisualElement();
            e.style.flexDirection = FlexDirection.Row;
            e.style.flexWrap = Wrap.Wrap;
            e.style.alignItems = Align.Center;
            return e;
        }

        public static VisualElement Col()
        {
            var e = new VisualElement();
            e.style.flexDirection = FlexDirection.Column;
            return e;
        }

        public static VisualElement Panel()
        {
            var e = Col();
            e.style.backgroundColor = C(Palette.Panel);
            Pad(e, 12);
            e.style.marginBottom = 10;
            Radius(e, 8);
            return e;
        }

        public static Label Text(string text, int size = Body, uint rgb = Palette.Text)
        {
            var l = new Label(text);
            l.style.fontSize = size;
            l.style.color = C(rgb);
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.marginTop = 2; l.style.marginBottom = 2;
            return l;
        }

        public static Label Heading(string text)
        {
            var l = Text(text, Big);
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            return l;
        }

        public static Button Btn(string text, Action onClick, int size = Body)
        {
            var b = new Button(onClick) { text = text };
            b.style.fontSize = size;
            b.style.color = C(Palette.Text);
            b.style.backgroundColor = C(ButtonBg);
            Border(b, 2, ButtonBorder);
            Radius(b, 8);
            Pad(b, 10, 18);
            Margin(b, 5);
            b.style.minHeight = 72;
            b.style.whiteSpace = WhiteSpace.Normal;
            b.style.unityTextAlign = TextAnchor.MiddleCenter;
            return b;
        }

        /// <summary>A flat square with a centered label: hero glyphs, enemies, party slots.</summary>
        public static Label Box(string text, float size, uint bg, int fontSize = Small)
        {
            var l = new Label(text);
            l.style.width = size; l.style.height = size;
            l.style.backgroundColor = C(bg);
            l.style.color = C(0x000000);
            l.style.fontSize = fontSize;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.whiteSpace = WhiteSpace.Normal;
            Radius(l, 6);
            Margin(l, 4);
            return l;
        }

        /// <summary>Horizontal bar: a background track, a fill child and a centered text overlay.</summary>
        public sealed class Bar
        {
            public readonly VisualElement Root, Fill;
            public readonly Label Caption;
            float _lastPct = -1f;

            public Bar(float height, uint fill, uint back = Palette.BarBack)
            {
                Root = new VisualElement();
                Root.style.height = height;
                Root.style.backgroundColor = C(back);
                Root.style.marginTop = 4; Root.style.marginBottom = 4;
                Radius(Root, 4);
                Root.style.overflow = Overflow.Hidden;
                Fill = new VisualElement();
                Fill.style.position = Position.Absolute;
                Fill.style.left = 0; Fill.style.top = 0; Fill.style.bottom = 0;
                Fill.style.width = Length.Percent(0);
                Fill.style.backgroundColor = C(fill);
                Root.Add(Fill);
                Caption = Text("", Small);
                Caption.style.position = Position.Absolute;
                Caption.style.left = 0; Caption.style.right = 0; Caption.style.top = 0; Caption.style.bottom = 0;
                Caption.style.unityTextAlign = TextAnchor.MiddleCenter;
                Caption.style.marginTop = 0; Caption.style.marginBottom = 0;
                Root.Add(Caption);
            }

            public void Set(double frac, string caption)
            {
                float pct = (float)(Math.Max(0, Math.Min(1, double.IsNaN(frac) ? 0 : frac)) * 100.0);
                if (Math.Abs(pct - _lastPct) >= 0.5f) { _lastPct = pct; Fill.style.width = Length.Percent(pct); }
                Ui.Set(Caption, caption);
            }
        }

        // ---- change-only setters

        public static void Set(TextElement e, string text)
        {
            if (e.text != text) e.text = text;
        }

        public static void Show(VisualElement e, bool on)
        {
            var want = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (e.style.display.value != want) e.style.display = want;   // unset reads as Flex, the default
        }

        public static void Enable(VisualElement e, bool on)
        {
            if (e.enabledSelf == on) return;
            e.SetEnabled(on);
            e.style.opacity = on ? 1f : 0.4f;
        }

        public static void Bg(VisualElement e, uint rgb)
        {
            var c = C(rgb);
            if (e.style.backgroundColor.value != c) e.style.backgroundColor = c;
        }

        public static void Bg(VisualElement e, Color c)
        {
            if (e.style.backgroundColor.value != c) e.style.backgroundColor = c;
        }
    }
}
