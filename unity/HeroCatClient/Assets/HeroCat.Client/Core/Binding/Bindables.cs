using System;
using System.Collections.Generic;

namespace HeroCat.Client.Core
{
    // Change-only setters (07 sections 3 and 16; issue #109). A view-model field is one of these; a screen binds a sink to
    // it once (Bind), and the vm's Refresh writes through it. The sink is written only when the value changed, so a
    // Refresh with nothing new touches no label (07 AC 20) and formats no string (AC 12): text is formatted only when a
    // sink is bound and the raw value moved. The Unity layer's sinks wrap a TMP_Text, an Image fill or a GameObject.

    /// <summary>Receives a label's text. Unity: a TMP_Text adapter.</summary>
    public interface ITextSink { void SetText(string text); }

    /// <summary>Receives a bar fill in [0, 1]. Unity: an Image.fillAmount or an anchor.</summary>
    public interface IFillSink { void SetFill(float fill01); }

    /// <summary>Receives a flag: visible, enabled, a badge dot. Unity: SetActive / interactable + 40% alpha.</summary>
    public interface IFlagSink { void SetFlag(bool on); }

    /// <summary>Receives a struct value (a fish cell's look, a slot). Unity: the cell or slot view.</summary>
    public interface IValueSink<T> { void SetValue(T value); }

    /// <summary>Common counters for tests: how many times the sink was written.</summary>
    public abstract class BindableBase
    {
        /// <summary>Sink writes since construction (07 AC 20 counts these).</summary>
        public int Writes { get; protected set; }

        /// <summary>Bumped on every value change, bound or not.</summary>
        public int Version { get; protected set; }
    }

    /// <summary>A struct-valued field: the general change-only setter.</summary>
    public sealed class Bindable<T> : BindableBase
    {
        static readonly EqualityComparer<T> Eq = EqualityComparer<T>.Default;
        IValueSink<T> _sink;
        bool _has;

        public T Value { get; private set; }

        public void Bind(IValueSink<T> sink)
        {
            _sink = sink;
            if (_sink != null && _has) { _sink.SetValue(Value); Writes++; }
        }

        /// <summary>Returns true when the value changed.</summary>
        public bool Set(T v)
        {
            if (_has && Eq.Equals(Value, v)) return false;
            Value = v;
            _has = true;
            Version++;
            if (_sink != null) { _sink.SetValue(v); Writes++; }
            return true;
        }
    }

    /// <summary>How a <see cref="NumberLabel"/> formats (07 section 7).</summary>
    public enum NumberStyle { Short, PctPoints, PctPlain, Mult, Ratio, Countdown, BossTimer, Seconds, Duration }

    /// <summary>A curve-valued number on the suffix ladder (or another 07 section 7 style), with an optional prefix/suffix.</summary>
    public sealed class NumberLabel : BindableBase
    {
        readonly NumberStyle _style;
        readonly string _prefix, _suffix;
        ITextSink _sink;
        bool _has;

        public NumberLabel(NumberStyle style = NumberStyle.Short, string prefix = null, string suffix = null)
        {
            _style = style;
            _prefix = prefix;
            _suffix = suffix;
        }

        public double Value { get; private set; }

        /// <summary>The formatted text (allocates; for screens and tests, never for Refresh).</summary>
        public string Text => (_prefix ?? "") + Format(_style, Value) + (_suffix ?? "");

        public void Bind(ITextSink sink)
        {
            _sink = sink;
            if (_sink != null && _has) { _sink.SetText(Text); Writes++; }
        }

        public bool Set(double v)
        {
            if (_has && (v == Value || (double.IsNaN(v) && double.IsNaN(Value)))) return false;
            Value = v;
            _has = true;
            Version++;
            if (_sink != null) { _sink.SetText(Text); Writes++; }
            return true;
        }

        public static string Format(NumberStyle style, double v)
        {
            switch (style)
            {
                case NumberStyle.PctPoints: return TextFmt.PctPoints(v);
                case NumberStyle.PctPlain: return TextFmt.PctPlain(v);
                case NumberStyle.Mult: return TextFmt.Mult(v);
                case NumberStyle.Ratio: return TextFmt.Ratio(v);
                case NumberStyle.Countdown: return TextFmt.Countdown(v);
                case NumberStyle.BossTimer: return TextFmt.BossTimer(v);
                case NumberStyle.Seconds: return TextFmt.Seconds(v);
                case NumberStyle.Duration: return TextFmt.Duration((long)Math.Round(v));
                default: return BigNumber.Short(v);
            }
        }
    }

    /// <summary>An integer count (<see cref="TextFmt.IntText"/>: full with grouping below 1e6).</summary>
    public sealed class IntLabel : BindableBase
    {
        readonly string _prefix, _suffix;
        ITextSink _sink;
        bool _has;

        public IntLabel(string prefix = null, string suffix = null)
        {
            _prefix = prefix;
            _suffix = suffix;
        }

        public long Value { get; private set; }

        public string Text => (_prefix ?? "") + TextFmt.IntText(Value) + (_suffix ?? "");

        public void Bind(ITextSink sink)
        {
            _sink = sink;
            if (_sink != null && _has) { _sink.SetText(Text); Writes++; }
        }

        public bool Set(long v)
        {
            if (_has && v == Value) return false;
            Value = v;
            _has = true;
            Version++;
            if (_sink != null) { _sink.SetText(Text); Writes++; }
            return true;
        }
    }

    /// <summary>
    /// One argument of a <see cref="TextKeyLabel"/>: a string, an integer count or a formatted number. Numbers are kept raw
    /// and formatted only when the label writes its sink, so a Refresh with no bound view formats nothing (07 AC 12).
    /// </summary>
    public readonly struct LabelArg : IEquatable<LabelArg>
    {
        readonly string _s;
        readonly double _d;
        readonly byte _kind;   // 0 none, 1 string, 2 int count, 3 number
        readonly NumberStyle _style;

        LabelArg(string s, double d, byte kind, NumberStyle style)
        {
            _s = s;
            _d = d;
            _kind = kind;
            _style = style;
        }

        public static LabelArg Str(string s) => new LabelArg(s, 0, 1, NumberStyle.Short);
        public static LabelArg Int(long n) => new LabelArg(null, n, 2, NumberStyle.Short);
        public static LabelArg Num(double v, NumberStyle style = NumberStyle.Short) => new LabelArg(null, v, 3, style);

        public static implicit operator LabelArg(string s) => Str(s);
        public static implicit operator LabelArg(int n) => Int(n);
        public static implicit operator LabelArg(long n) => Int(n);

        public bool IsNone => _kind == 0;

        public override string ToString() => _kind switch
        {
            1 => _s ?? "",
            2 => TextFmt.IntText((long)_d),
            3 => NumberLabel.Format(_style, _d),
            _ => "",
        };

        public bool Equals(LabelArg o) =>
            _kind == o._kind && _style == o._style && string.Equals(_s, o._s, StringComparison.Ordinal) &&
            (_d == o._d || (double.IsNaN(_d) && double.IsNaN(o._d)));

        public override bool Equals(object obj) => obj is LabelArg o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(_kind, _s, _d);
    }

    /// <summary>
    /// A localized label: a key plus up to three arguments. The text is resolved when the key or an argument changes and a
    /// sink is bound, or when <see cref="Relocalize"/> runs after a locale or hot content change (07 section 16).
    /// </summary>
    public sealed class TextKeyLabel : BindableBase
    {
        ILocalizer _loc;
        ITextSink _sink;
        bool _has;
        LabelArg _a0, _a1, _a2;
        int _argc;

        public TextKeyLabel(ILocalizer loc = null) { _loc = loc; }

        public string Key { get; private set; }

        public bool IsBound => _sink != null;

        public ILocalizer Localizer
        {
            get => _loc ?? Core.Localizer.Keys;
            set => _loc = value;
        }

        public LabelArg Arg(int i) => i == 0 ? _a0 : i == 1 ? _a1 : _a2;

        public string Text
        {
            get
            {
                if (Key == null) return "";
                if (Key.Length == 0)   // UiKeys.Raw: the arguments are the text (an already-formatted number)
                    return _argc == 0 ? "" : _argc == 1 ? _a0.ToString() : _argc == 2 ? _a0.ToString() + _a1.ToString() : _a0.ToString() + _a1.ToString() + _a2.ToString();
                switch (_argc)
                {
                    case 0: return Localizer.Text(Key);
                    case 1: return Localizer.Format(Key, _a0.ToString());
                    case 2: return Localizer.Format(Key, _a0.ToString(), _a1.ToString());
                    default: return Localizer.Format(Key, _a0.ToString(), _a1.ToString(), _a2.ToString());
                }
            }
        }

        public void Bind(ITextSink sink)
        {
            _sink = sink;
            if (_sink != null && _has) { _sink.SetText(Text); Writes++; }
        }

        public bool Set(string key) => SetN(key, 0, default, default, default);

        public bool Set(string key, LabelArg a0) => SetN(key, 1, a0, default, default);

        public bool Set(string key, LabelArg a0, LabelArg a1) => SetN(key, 2, a0, a1, default);

        public bool Set(string key, LabelArg a0, LabelArg a1, LabelArg a2) => SetN(key, 3, a0, a1, a2);

        bool SetN(string key, int argc, LabelArg a0, LabelArg a1, LabelArg a2)
        {
            if (_has && argc == _argc && string.Equals(key, Key, StringComparison.Ordinal) && a0.Equals(_a0) && a1.Equals(_a1) && a2.Equals(_a2))
                return false;
            Key = key;
            _argc = argc;
            _a0 = a0; _a1 = a1; _a2 = a2;
            _has = true;
            Version++;
            if (_sink != null) { _sink.SetText(Text); Writes++; }
            return true;
        }

        /// <summary>Re-resolves the bound text (locale change, hot-applied localization; 07 section 16).</summary>
        public void Relocalize()
        {
            if (_sink != null && _has) { _sink.SetText(Text); Writes++; }
        }
    }

    /// <summary>A bar fill in [0, 1], quantized to 1/1000 so a tiny change does not rebuild the band.</summary>
    public sealed class BarFill : BindableBase
    {
        IFillSink _sink;
        int _q = -1;

        public float Value { get; private set; }

        public void Bind(IFillSink sink)
        {
            _sink = sink;
            if (_sink != null && _q >= 0) { _sink.SetFill(Value); Writes++; }
        }

        /// <summary><paramref name="num"/> / <paramref name="den"/>, clamped; 0 when den is not positive.</summary>
        public bool Set(double num, double den) => Set(den > 0 && !double.IsNaN(num) ? num / den : 0.0);

        public bool Set(double fill)
        {
            if (double.IsNaN(fill)) fill = 0;
            if (fill < 0) fill = 0;
            if (fill > 1) fill = 1;
            int q = (int)Math.Round(fill * 1000.0);
            if (q == _q) return false;
            _q = q;
            Value = q / 1000f;
            Version++;
            if (_sink != null) { _sink.SetFill(Value); Writes++; }
            return true;
        }
    }

    /// <summary>A flag: a visible element, an enabled button, a red dot.</summary>
    public sealed class Flag : BindableBase
    {
        IFlagSink _sink;
        bool _has;

        public bool Value { get; private set; }

        public void Bind(IFlagSink sink)
        {
            _sink = sink;
            if (_sink != null && _has) { _sink.SetFlag(Value); Writes++; }
        }

        public bool Set(bool v)
        {
            if (_has && v == Value) return false;
            Value = v;
            _has = true;
            Version++;
            if (_sink != null) { _sink.SetFlag(v); Writes++; }
            return true;
        }
    }

    /// <summary>A red-dot badge with an optional count (07 section 10 rule 2: a free thing is waiting).</summary>
    public sealed class BadgeDot : BindableBase
    {
        IFlagSink _dot;
        ITextSink _count;
        int _n = -1;

        public int Count => _n < 0 ? 0 : _n;
        public bool Visible => _n > 0;

        public void Bind(IFlagSink dot, ITextSink count = null)
        {
            _dot = dot;
            _count = count;
            if (_n >= 0) Write();
        }

        public bool Set(int n)
        {
            if (n < 0) n = 0;
            if (n == _n) return false;
            _n = n;
            Version++;
            Write();
            return true;
        }

        void Write()
        {
            if (_dot != null) { _dot.SetFlag(_n > 0); Writes++; }
            if (_count != null) { _count.SetText(_n > 0 ? TextFmt.IntText(_n) : ""); Writes++; }
        }
    }

    /// <summary>A button: enabled flag plus the reason label shown when disabled (07 section 10 rule 9).</summary>
    public sealed class ButtonState
    {
        public readonly Flag Enabled = new Flag();
        public readonly Flag Visible = new Flag();
        public readonly TextKeyLabel Label;
        public readonly TextKeyLabel Reason;

        public ButtonState(ILocalizer loc)
        {
            Label = new TextKeyLabel(loc);
            Reason = new TextKeyLabel(loc);
        }

        public int Writes => Enabled.Writes + Visible.Writes + Label.Writes + Reason.Writes;
    }
}
