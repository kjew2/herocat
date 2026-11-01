using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HeroCat.Sim.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Content
{
    /// <summary>
    /// The client's strings (09 section 13.4, issue #129). Holds <c>localization.en.json</c> plus the active locale's file;
    /// <see cref="Text"/> resolves active, then en, then <c>[key]</c> (ugly, visible, greppable). <see cref="Format"/> runs
    /// the ICU MessageFormat subset the client ships: <c>{arg}</c>, <c>{arg, number}</c>, <c>{arg, plural, ...}</c> with the
    /// CLDR categories of the shipped locales (en: one/other; ja, ko, zh-Hans: other) and <c>=N</c> exact branches, and
    /// <c>{arg, select, ...}</c>. Strings never enter HeroCat.Sim (09 principle 5, 00 contract 10): the Sim and its
    /// ContentDb carry keys only. A locale change or a hot-applied string swap (09 section 10.3 step 7) raises
    /// <see cref="LocaleChanged"/> so 07's TextKeyLabel components re-resolve.
    /// </summary>
    public sealed class Localizer
    {
        public const string FallbackLocale = "en";

        /// <summary>09 section 3.3's Locale enum, in its order.</summary>
        public static readonly IReadOnlyList<string> Locales = new[] { "en", "ja", "ko", "zh-Hans" };

        IReadOnlyDictionary<string, string> _en;
        IReadOnlyDictionary<string, string> _active;

        public Localizer(IReadOnlyDictionary<string, string> en, string locale = FallbackLocale, IReadOnlyDictionary<string, string> active = null)
        {
            _en = en ?? throw new ArgumentNullException(nameof(en));
            Locale = locale ?? FallbackLocale;
            _active = active ?? (Locale == FallbackLocale ? en : EmptyTable);
        }

        static readonly IReadOnlyDictionary<string, string> EmptyTable = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The active locale (Prefs.Locale, 07). A locale with no file falls back to en for every key.</summary>
        public string Locale { get; private set; }

        /// <summary>Raised with the new locale after <see cref="SetLocale"/> or <see cref="Replace"/>.</summary>
        public event Action<string> LocaleChanged;

        /// <summary>How {n, number} and the plural `#` print. Default: invariant culture, grouping, up to three decimals.</summary>
        public Func<double, string> NumberFormatter { get; set; } = DefaultNumber;

        public static string DefaultNumber(double v) => v.ToString("#,0.###", CultureInfo.InvariantCulture);

        /// <summary>Loads en plus <paramref name="locale"/> from a content set's manifest `localization` block (E900 on a bad hash).</summary>
        public static Localizer Load(IContentSource source, string locale = FallbackLocale)
        {
            var files = ReadManifest(source);
            var en = ReadLocale(source, files, FallbackLocale) ?? EmptyTable;
            var active = locale == FallbackLocale ? en : ReadLocale(source, files, locale) ?? EmptyTable;
            return new Localizer(en, locale, active);
        }

        /// <summary>Switches the active locale, reading its file from <paramref name="source"/>, and raises LocaleChanged.</summary>
        public void SetLocale(IContentSource source, string locale)
        {
            var files = ReadManifest(source);
            _active = locale == FallbackLocale ? _en : ReadLocale(source, files, locale) ?? EmptyTable;
            Locale = locale;
            LocaleChanged?.Invoke(Locale);
        }

        /// <summary>Swaps every string from a newer content set, keeping the locale (hot apply, 09 section 10.3 step 7).</summary>
        public void Replace(IContentSource source)
        {
            var files = ReadManifest(source);
            var en = ReadLocale(source, files, FallbackLocale) ?? EmptyTable;
            _active = Locale == FallbackLocale ? en : ReadLocale(source, files, Locale) ?? EmptyTable;
            _en = en;
            LocaleChanged?.Invoke(Locale);
        }

        public bool Has(string key) => key != null && (_active.ContainsKey(key) || _en.ContainsKey(key));

        /// <summary>Active locale, then en, then "[key]".</summary>
        public string Text(string key)
        {
            if (key == null) return "[]";
            if (_active.TryGetValue(key, out var s)) return s;
            if (_en.TryGetValue(key, out s)) return s;
            return "[" + key + "]";
        }

        /// <summary>Text(key) formatted with <paramref name="args"/>. A malformed message prints unformatted rather than throwing.</summary>
        public string Format(string key, IReadOnlyDictionary<string, object> args)
        {
            string pattern = Text(key);
            if (!Has(key)) return pattern;
            return FormatMessage(pattern, Locale, args, NumberFormatter);
        }

        public string Format(string key, params (string Name, object Value)[] args)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var a in args) d[a.Name] = a.Value;
            return Format(key, d);
        }

        // ------------------------------------------------------------------------------------------------ loading

        static Dictionary<string, ManifestEntry> ReadManifest(IContentSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var doc = ParseJson(source.ReadBytes("manifest.json"));
            var files = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
            if (doc["localization"] is JObject loc)
                foreach (var kv in loc)
                    files[kv.Key] = new ManifestEntry((string)kv.Value["file"], (string)kv.Value["sha256"]);
            return files;
        }

        readonly struct ManifestEntry
        {
            public ManifestEntry(string file, string sha) { File = file; Sha256 = sha; }
            public string File { get; }
            public string Sha256 { get; }
        }

        static IReadOnlyDictionary<string, string> ReadLocale(IContentSource source, Dictionary<string, ManifestEntry> files, string locale)
        {
            if (!files.TryGetValue(locale, out var e) || e.File == null || !source.Exists(e.File)) return null;
            var bytes = source.ReadBytes(e.File);
            string actual = ContentLoader.Sha256Hex(bytes);
            if (!string.Equals(actual, e.Sha256, StringComparison.Ordinal))
                throw new ContentBuildException(new[] { new ContentError("E900", e.File, $"{e.File}: sha256 {actual} does not match manifest {e.Sha256}") });
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            if (ParseJson(bytes)["rows"] is JArray rows)
                foreach (var r in rows)
                {
                    var k = (string)r["key"];
                    var t = (string)r["text"];
                    if (k != null && t != null) table[k] = t;
                }
            return table;
        }

        static JObject ParseJson(byte[] data)
        {
            using (var reader = new JsonTextReader(new StreamReader(new MemoryStream(data), new UTF8Encoding(false))))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                return JObject.Load(reader);
            }
        }

        // ------------------------------------------------------------------------------------------- ICU subset

        /// <summary>CLDR plural category of <paramref name="n"/> in <paramref name="locale"/> for the shipped locales (09 section 13.2).</summary>
        public static string PluralCategory(string locale, double n)
        {
            if (locale == "en") return n == 1.0 ? "one" : "other";
            return "other";   // ja, ko, zh-Hans have one category
        }

        /// <summary>Formats an ICU message. Unknown arguments stay as "{name}"; a malformed pattern is returned unchanged.</summary>
        public static string FormatMessage(string pattern, string locale, IReadOnlyDictionary<string, object> args, Func<double, string> number = null)
        {
            if (pattern == null) return "";
            number = number ?? DefaultNumber;
            try
            {
                var p = new Parser(pattern);
                var parts = p.Message(0);
                if (p.Pos != pattern.Length) return pattern;
                var sb = new StringBuilder();
                Render(parts, sb, locale, args, number, null);
                return sb.ToString();
            }
            catch (FormatException)
            {
                return pattern;
            }
        }

        sealed class Part
        {
            public string Literal;          // literal text, or null for an argument
            public bool Hash;               // `#` inside a plural branch
            public string Name, Kind;       // simple | number | plural | select
            public List<KeyValuePair<string, List<Part>>> Branches;
            public double Offset;
        }

        static void Render(List<Part> parts, StringBuilder sb, string locale, IReadOnlyDictionary<string, object> args,
                           Func<double, string> number, double? pluralValue)
        {
            foreach (var p in parts)
            {
                if (p.Literal != null) { sb.Append(p.Literal); continue; }
                if (p.Hash) { sb.Append(pluralValue.HasValue ? number(pluralValue.Value) : "#"); continue; }
                object v = null;
                bool has = args != null && args.TryGetValue(p.Name, out v);
                if (!has) { sb.Append('{').Append(p.Name).Append('}'); continue; }
                switch (p.Kind)
                {
                    case "number":
                        sb.Append(TryNumber(v, out var nv) ? number(nv) : Convert.ToString(v, CultureInfo.InvariantCulture));
                        break;
                    case "plural":
                    {
                        if (!TryNumber(v, out var n)) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break; }
                        var branch = Pick(p.Branches, "=" + n.ToString("R", CultureInfo.InvariantCulture))
                                     ?? Pick(p.Branches, PluralCategory(locale, n - p.Offset)) ?? Pick(p.Branches, "other");
                        if (branch != null) Render(branch, sb, locale, args, number, n - p.Offset);
                        break;
                    }
                    case "select":
                    {
                        var branch = Pick(p.Branches, Convert.ToString(v, CultureInfo.InvariantCulture)) ?? Pick(p.Branches, "other");
                        if (branch != null) Render(branch, sb, locale, args, number, pluralValue);
                        break;
                    }
                    default:
                        sb.Append(TryNumber(v, out var sv) && !(v is string) ? number(sv) : Convert.ToString(v, CultureInfo.InvariantCulture));
                        break;
                }
            }
        }

        static List<Part> Pick(List<KeyValuePair<string, List<Part>>> branches, string selector)
        {
            foreach (var b in branches) if (b.Key == selector) return b.Value;
            return null;
        }

        static bool TryNumber(object v, out double n)
        {
            switch (v)
            {
                case int i: n = i; return true;
                case long l: n = l; return true;
                case double d: n = d; return true;
                case float f: n = f; return true;
                case decimal m: n = (double)m; return true;
                case short s: n = s; return true;
                default: n = 0; return false;
            }
        }

        /// <summary>The same grammar as export_content.icu_parse, so a message the exporter accepts renders here.</summary>
        sealed class Parser
        {
            readonly string _t;
            public int Pos;

            public Parser(string text) { _t = text; }

            public List<Part> Message(int depth, bool inPlural = false)
            {
                var parts = new List<Part>();
                var buf = new StringBuilder();
                void Flush() { if (buf.Length > 0) { parts.Add(new Part { Literal = buf.ToString() }); buf.Clear(); } }
                while (Pos < _t.Length)
                {
                    char ch = _t[Pos];
                    if (ch == '\'')
                    {
                        if (Pos + 1 < _t.Length && _t[Pos + 1] == '\'') { buf.Append('\''); Pos += 2; continue; }
                        if (Pos + 1 < _t.Length && (_t[Pos + 1] == '{' || _t[Pos + 1] == '}' || _t[Pos + 1] == '#'))
                        {
                            int end = _t.IndexOf('\'', Pos + 1);
                            if (end < 0) throw new FormatException("unterminated quote");
                            buf.Append(_t, Pos + 1, end - Pos - 1);
                            Pos = end + 1;
                            continue;
                        }
                        buf.Append(ch); Pos++;
                    }
                    else if (ch == '{') { Flush(); parts.Add(Argument()); }
                    else if (ch == '}')
                    {
                        if (depth == 0) throw new FormatException("unmatched }");
                        break;
                    }
                    else if (ch == '#' && inPlural) { Flush(); parts.Add(new Part { Hash = true }); Pos++; }
                    else { buf.Append(ch); Pos++; }
                }
                Flush();
                return parts;
            }

            string Word()
            {
                while (Pos < _t.Length && char.IsWhiteSpace(_t[Pos])) Pos++;
                int start = Pos;
                while (Pos < _t.Length && _t[Pos] != ',' && _t[Pos] != '{' && _t[Pos] != '}' && !char.IsWhiteSpace(_t[Pos])) Pos++;
                string w = _t.Substring(start, Pos - start);
                while (Pos < _t.Length && char.IsWhiteSpace(_t[Pos])) Pos++;
                return w;
            }

            bool At(char c) => Pos < _t.Length && _t[Pos] == c;

            Part Argument()
            {
                Pos++;   // {
                string name = Word();
                if (name.Length == 0) throw new FormatException("empty argument");
                if (At('}')) { Pos++; return new Part { Name = name, Kind = "simple" }; }
                if (!At(',')) throw new FormatException("expected , or }");
                Pos++;
                string kind = Word();
                if (kind == "number")
                {
                    if (At(',')) { Pos++; Word(); }
                    if (!At('}')) throw new FormatException("unterminated number");
                    Pos++;
                    return new Part { Name = name, Kind = kind };
                }
                if (kind != "plural" && kind != "select") throw new FormatException("unsupported argument type " + kind);
                if (!At(',')) throw new FormatException("expected ,");
                Pos++;
                var part = new Part { Name = name, Kind = kind, Branches = new List<KeyValuePair<string, List<Part>>>() };
                while (true)
                {
                    string sel = Word();
                    if (sel.StartsWith("offset:", StringComparison.Ordinal))
                    {
                        part.Offset = double.Parse(sel.Substring("offset:".Length), NumberStyles.Float, CultureInfo.InvariantCulture);
                        sel = Word();
                    }
                    if (sel.Length == 0) break;
                    if (!At('{')) throw new FormatException("branch without message");
                    Pos++;
                    part.Branches.Add(new KeyValuePair<string, List<Part>>(sel, Message(1, kind == "plural")));
                    if (!At('}')) throw new FormatException("unterminated branch");
                    Pos++;
                }
                if (!At('}')) throw new FormatException("unterminated " + kind);
                Pos++;
                return part;
            }
        }
    }
}
