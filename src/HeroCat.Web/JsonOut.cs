using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HeroCat.Web
{
    /// <summary>Tiny streaming JSON writer. Doubles use the shortest round-trip form ("R"), so JS parses the identical double.</summary>
    internal sealed class JsonOut
    {
        private readonly StringBuilder _sb = new StringBuilder(256);
        private readonly Stack<bool> _has = new Stack<bool>();
        private bool _afterKey;

        public override string ToString() => _sb.ToString();

        private void Sep()
        {
            if (_afterKey) { _afterKey = false; return; }
            if (_has.Count > 0)
            {
                if (_has.Pop()) _sb.Append(',');
                _has.Push(true);
            }
        }

        public JsonOut Obj() { Sep(); _sb.Append('{'); _has.Push(false); return this; }
        public JsonOut EndObj() { _has.Pop(); _sb.Append('}'); return this; }
        public JsonOut Arr() { Sep(); _sb.Append('['); _has.Push(false); return this; }
        public JsonOut EndArr() { _has.Pop(); _sb.Append(']'); return this; }

        public JsonOut Key(string k)
        {
            Sep();
            Quote(k);
            _sb.Append(':');
            _afterKey = true;
            return this;
        }

        public JsonOut Num(double d)
        {
            Sep();
            if (double.IsNaN(d) || double.IsInfinity(d)) _sb.Append("null");
            else _sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonOut Int(long v) { Sep(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonOut Bool(bool b) { Sep(); _sb.Append(b ? "true" : "false"); return this; }
        public JsonOut Null() { Sep(); _sb.Append("null"); return this; }
        public JsonOut Str(string s) { if (s == null) return Null(); Sep(); Quote(s); return this; }
        public JsonOut Raw(string json) { Sep(); _sb.Append(json); return this; }

        public JsonOut P(string k, double v) => Key(k).Num(v);
        public JsonOut P(string k, long v) => Key(k).Int(v);
        public JsonOut P(string k, int v) => Key(k).Int(v);
        public JsonOut P(string k, bool v) => Key(k).Bool(v);
        public JsonOut P(string k, string v) => Key(k).Str(v);

        private void Quote(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        public static string Error(string message) => new JsonOut().Obj().P("error", message).EndObj().ToString();
    }
}
