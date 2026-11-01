using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HeroCat.Sim.Content
{
    /// <summary>
    /// Minimal JSON reader for content files (specs/05 open decision 2: hand-written instead of a package
    /// dependency). Produces Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, long (integer
    /// literals), double, bool and null. Culture-invariant.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            p.Ws();
            object v = p.Value();
            p.Ws();
            if (!p.AtEnd) throw p.Error("trailing characters");
            return v;
        }

        sealed class Parser
        {
            readonly string _s;
            int _i;

            public Parser(string s)
            {
                _s = s ?? throw new ArgumentNullException(nameof(s));
                if (_s.Length > 0 && _s[0] == '﻿') _i = 1;   // tolerate a BOM
            }

            public bool AtEnd => _i >= _s.Length;

            public FormatException Error(string msg) => new FormatException($"JSON parse error at {_i}: {msg}");

            public void Ws()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
            }

            void Expect(char c)
            {
                if (_i >= _s.Length || _s[_i] != c) throw Error($"expected '{c}'");
                _i++;
            }

            public object Value()
            {
                if (AtEnd) throw Error("unexpected end");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return Obj();
                    case '[': return Arr();
                    case '"': return Str();
                    case 't': Lit("true"); return true;
                    case 'f': Lit("false"); return false;
                    case 'n': Lit("null"); return null;
                    default: return Num();
                }
            }

            void Lit(string lit)
            {
                if (string.CompareOrdinal(_s, _i, lit, 0, lit.Length) != 0) throw Error($"expected {lit}");
                _i += lit.Length;
            }

            Dictionary<string, object> Obj()
            {
                Expect('{');
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Ws();
                if (!AtEnd && _s[_i] == '}') { _i++; return d; }
                while (true)
                {
                    Ws();
                    string k = Str();
                    Ws();
                    Expect(':');
                    Ws();
                    d[k] = Value();
                    Ws();
                    if (!AtEnd && _s[_i] == ',') { _i++; continue; }
                    Expect('}');
                    return d;
                }
            }

            List<object> Arr()
            {
                Expect('[');
                var l = new List<object>();
                Ws();
                if (!AtEnd && _s[_i] == ']') { _i++; return l; }
                while (true)
                {
                    Ws();
                    l.Add(Value());
                    Ws();
                    if (!AtEnd && _s[_i] == ',') { _i++; continue; }
                    Expect(']');
                    return l;
                }
            }

            string Str()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw Error("bad escape");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("bad \\u escape");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error($"bad escape \\{e}");
                    }
                }
            }

            object Num()
            {
                int start = _i;
                bool isInt = true;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c >= '0' && c <= '9' || c == '-' || c == '+') { _i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E') { isInt = false; _i++; continue; }
                    break;
                }
                if (start == _i) throw Error($"unexpected character '{_s[_i]}'");
                string tok = _s.Substring(start, _i - start);
                if (isInt && long.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
                if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                throw Error($"bad number '{tok}'");
            }
        }
    }
}
