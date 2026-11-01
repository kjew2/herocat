using System;
using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// The uGUI + TextMeshPro construction kit (00 decision 35, 07 sections 2, 8 and 15.1). M0 builds every screen from
    /// code with placeholder art (coloured Images and TMP labels), so there are no prefabs yet: one builder per screen.
    /// Sizes are reference pixels (1080 x 1920, CanvasScaler match width). Unity-only: not compiled in cloud sessions.
    /// </summary>
    public static class UiKit
    {
        // 07 section 8 type scale (rp).
        public const float Display = 64, Title = 48, Body = 36, Number = 40, Caption = 28, Micro = 24;

        static TMP_FontAsset _font;

        /// <summary>Resources path of the M0 UI font (07 section 15.1): DejaVu Sans has every placeholder glyph.</summary>
        public const string FontResource = "Fonts/DejaVuSans";

        /// <summary>Printable ASCII, the ellipsis TMP truncates with, and every placeholder glyph the screens use.</summary>
        const string Preload =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
            "\u2026\u00B7\u2039\u25CF\u25A0\u25C6\u2605\u265B\u2699\u2709\u2715\u2298";

        /// <summary>
        /// A dynamic TMP font built at runtime from DejaVu Sans, because TMP's LiberationSans lacks the placeholder
        /// glyphs (rarity shapes, crown, gear, envelope, close). Built at runtime so no project asset is modified.
        /// Falls back to TMP's default font, then the built-in font.
        /// </summary>
        public static TMP_FontAsset Font
        {
            get
            {
                if (_font != null) return _font;
                var dejavu = Resources.Load<Font>(FontResource);
                if (dejavu != null) _font = TMP_FontAsset.CreateFontAsset(dejavu);
                if (_font != null)
                {
                    _font.name = "DejaVuSans (runtime)";
                    // Load the glyphs up front: TMP checks for the ellipsis without adding it to a runtime font on demand.
                    if (!_font.TryAddCharacters(Preload, out var missing))
                        Debug.LogWarning("HeroCat: UI font lacks glyphs: " + missing);
                }
                if (_font == null)
                {
                    Debug.LogWarning("HeroCat: " + FontResource + " not found; placeholder glyphs will show as boxes.");
                    try { _font = TMP_Settings.defaultFontAsset; }
                    catch (Exception) { _font = null; }
                }
                if (_font == null)
                {
                    var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (f != null) _font = TMP_FontAsset.CreateFontAsset(f);
                }
                return _font;
            }
        }

        static Sprite _circle;

        /// <summary>A white anti-aliased circle generated once at runtime (no art in M0, 07 section 15.1).</summary>
        public static Sprite Circle
        {
            get
            {
                if (_circle != null) return _circle;
                const int n = 64;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var px = new Color32[n * n];
                float r = n / 2f - 1f;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f;
                        float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                        px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                    }
                tex.SetPixels32(px);
                tex.Apply(false, true);
                _circle = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
                return _circle;
            }
        }

        public static Image CirclePanel(string name, Transform parent, Color color)
        {
            var img = Rect(name, parent).gameObject.AddComponent<Image>();
            img.sprite = Circle;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public static Color C(uint rgb, float a = 1f) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

        public static Color Hue(float degrees, float sat = 0.6f, float val = 0.85f) => Color.HSVToRGB(degrees / 360f, sat, val);

        public static Color Rarity(Rarity r) => C(Palette.RarityColor(r));

        // ------------------------------------------------------------------------------------------ rects

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;   // UI
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        /// <summary>Stretch to the parent with insets (left, top, right, bottom).</summary>
        public static RectTransform Fill(RectTransform rt, float l = 0, float t = 0, float r = 0, float b = 0)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(l, b);
            rt.offsetMax = new Vector2(-r, -t);
            return rt;
        }

        /// <summary>A band of fixed height pinned to the top (y from the top) and stretched across.</summary>
        public static RectTransform TopBand(RectTransform rt, float top, float height)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(0, -top - height);
            rt.offsetMax = new Vector2(0, -top);
            return rt;
        }

        /// <summary>A band of fixed height pinned to the bottom.</summary>
        public static RectTransform BottomBand(RectTransform rt, float bottom, float height)
        {
            rt.anchorMin = new Vector2(0, 0);
            rt.anchorMax = new Vector2(1, 0);
            rt.pivot = new Vector2(0.5f, 0);
            rt.offsetMin = new Vector2(0, bottom);
            rt.offsetMax = new Vector2(0, bottom + height);
            return rt;
        }

        /// <summary>An absolutely placed box: x, y from the parent's top-left, in rp.</summary>
        public static RectTransform Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static Image Panel(string name, Transform parent, uint color, float alpha = 1f, bool raycast = false)
        {
            var img = Rect(name, parent).gameObject.AddComponent<Image>();
            img.color = C(color, alpha);
            img.raycastTarget = raycast;
            return img;
        }

        public static TextMeshProUGUI Text(string name, Transform parent, float size = Body, uint color = Palette.Text,
                                           TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, bool bold = false)
        {
            var t = Rect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) t.font = Font;
            t.fontSize = size;
            t.color = C(color);
            t.alignment = align;
            t.raycastTarget = false;
            t.overflowMode = TextOverflowModes.Ellipsis;   // 07 section 11: truncate with ellipsis, never overlap
            if (bold) t.fontStyle = FontStyles.Bold;
            t.text = "";
            return t;
        }

        /// <summary>A button: an Image, a TMP label child, a CanvasGroup for the 40% disabled alpha.</summary>
        public static Button Button(string name, Transform parent, UnityAction onClick, uint color = 0x37474F, float size = Body)
        {
            var img = Panel(name, parent, color, 1f, raycast: true);
            var b = img.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            img.gameObject.AddComponent<CanvasGroup>();
            var label = Text("Label", img.transform, size, Palette.Text, TextAlignmentOptions.Center);
            Fill(label.rectTransform, 8, 4, 8, 4);
            if (onClick != null) b.onClick.AddListener(onClick);
            return b;
        }

        public static TextMeshProUGUI LabelOf(Button b) => b.GetComponentInChildren<TextMeshProUGUI>(true);

        /// <summary>A horizontal bar: back plate plus a fill child whose anchorMax.x is the fill.</summary>
        public static RectTransform Bar(string name, Transform parent, uint fillColor, out Image fill)
        {
            var back = Panel(name, parent, Palette.BarBack);
            fill = Panel("Fill", back.transform, fillColor);
            var f = fill.rectTransform;
            f.anchorMin = Vector2.zero;
            f.anchorMax = new Vector2(0, 1);
            f.offsetMin = f.offsetMax = Vector2.zero;
            return back.rectTransform;
        }

        // ------------------------------------------------------------------------------------------ sinks

        public static TextSink Sink(this TMP_Text t) => new TextSink(t);
        public static FillSink Sink(this Image fill) => new FillSink(fill.rectTransform);
        public static ActiveSink ActiveOf(GameObject go) => new ActiveSink(go);
        public static InteractableSink EnabledOf(Button b) => new InteractableSink(b);
    }

    /// <summary>TMP adapter for a change-only text field.</summary>
    public sealed class TextSink : ITextSink
    {
        readonly TMP_Text _t;
        public TextSink(TMP_Text t) { _t = t; }
        public void SetText(string text) { if (_t != null) _t.text = text ?? ""; }
    }

    /// <summary>Bar fill through the fill child's anchorMax.x (works with sprite-less placeholder Images).</summary>
    public sealed class FillSink : IFillSink
    {
        readonly RectTransform _fill;
        public FillSink(RectTransform fill) { _fill = fill; }

        public void SetFill(float f)
        {
            if (_fill == null) return;
            _fill.anchorMax = new Vector2(Mathf.Clamp01(f), 1);
            _fill.offsetMax = Vector2.zero;
        }
    }

    public sealed class ActiveSink : IFlagSink
    {
        readonly GameObject _go;
        readonly bool _invert;
        public ActiveSink(GameObject go, bool invert = false) { _go = go; _invert = invert; }
        public void SetFlag(bool on) { if (_go != null) _go.SetActive(on != _invert); }
    }

    /// <summary>Enabled = interactable at full alpha; disabled = 40% alpha (07 section 8: never a colour change alone).</summary>
    public sealed class InteractableSink : IFlagSink
    {
        readonly Button _b;
        readonly CanvasGroup _g;

        public InteractableSink(Button b)
        {
            _b = b;
            _g = b != null ? b.GetComponent<CanvasGroup>() : null;
        }

        public void SetFlag(bool on)
        {
            if (_b != null) _b.interactable = on;
            if (_g != null) _g.alpha = on ? 1f : Palette.DisabledAlpha;
        }
    }

    /// <summary>Tints a graphic between two colours (ok/bad); the caller pairs it with a glyph or text.</summary>
    public sealed class ColorFlagSink : IFlagSink
    {
        readonly Graphic _g;
        readonly Color _on, _off;
        public ColorFlagSink(Graphic g, Color on, Color off) { _g = g; _on = on; _off = off; }
        public void SetFlag(bool on) { if (_g != null) _g.color = on ? _on : _off; }
    }

    /// <summary>A lambda sink, for composite views (a label built from two numbers, a cell look).</summary>
    public sealed class ActionSink<T> : IValueSink<T>
    {
        readonly Action<T> _a;
        public ActionSink(Action<T> a) { _a = a; }
        public void SetValue(T value) => _a?.Invoke(value);
    }

    public sealed class ActionTextSink : ITextSink
    {
        readonly Action<string> _a;
        public ActionTextSink(Action<string> a) { _a = a; }
        public void SetText(string text) => _a?.Invoke(text);
    }

    public sealed class ActionFlagSink : IFlagSink
    {
        readonly Action<bool> _a;
        public ActionFlagSink(Action<bool> a) { _a = a; }
        public void SetFlag(bool on) => _a?.Invoke(on);
    }

    /// <summary>Binds a <see cref="ButtonState"/> to a uGUI button: label, enabled, visible, and the reason label.</summary>
    public static class ButtonBinding
    {
        public static void Bind(ButtonState s, Button b, TMP_Text reason = null)
        {
            s.Label.Bind(UiKit.LabelOf(b).Sink());
            s.Enabled.Bind(UiKit.EnabledOf(b));
            s.Visible.Bind(new ActiveSink(b.gameObject));
            if (reason != null) s.Reason.Bind(reason.Sink());
        }
    }
}
