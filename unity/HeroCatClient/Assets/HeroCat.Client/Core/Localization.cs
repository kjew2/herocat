using System;
using System.Collections.Generic;
using System.Text;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// Resolves localization keys (07 section 12, 00 contract 10). A missing key renders the key itself (dev builds, and
    /// until 09 ships the row) and is reported once through <see cref="Missing"/>.
    /// </summary>
    public interface ILocalizer
    {
        string Text(string key);

        /// <summary><paramref name="key"/>'s text with <c>{0}</c>, <c>{1}</c> ... replaced by <paramref name="args"/>.</summary>
        string Format(string key, params string[] args);
    }

    /// <summary>A key-to-text table (09's <c>localization.&lt;locale&gt;.json</c> rows), loaded by the Unity layer.</summary>
    public sealed class Localizer : ILocalizer
    {
        readonly Dictionary<string, string> _rows;
        readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        public Localizer(IDictionary<string, string> rows = null)
        {
            _rows = rows != null ? new Dictionary<string, string>(rows, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Reads 09's <c>localization.&lt;locale&gt;.json</c> (<c>{"table":"localization","locale":"en","rows":[{"key","text"}]}</c>).
        /// A broken file yields an empty table (every key renders as itself), never an exception at boot.
        /// </summary>
        public static Localizer FromJson(string json)
        {
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(json)) return new Localizer(rows);
            try
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                if (root["rows"] is Newtonsoft.Json.Linq.JArray list)
                    foreach (var row in list)
                    {
                        string k = (string)row["key"], t = (string)row["text"];
                        if (!string.IsNullOrEmpty(k) && t != null) rows[k] = t;
                    }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                rows.Clear();
            }
            return new Localizer(rows);
        }

        /// <summary>Renders every key as itself: the fallback when no table is loaded, and the tests' localizer.</summary>
        public static readonly Localizer Keys = new Localizer();

        /// <summary>Called once per missing key (the Unity layer logs it).</summary>
        public Action<string> Missing { get; set; }

        public int Count => _rows.Count;

        public bool Has(string key) => key != null && _rows.ContainsKey(key);

        public string Text(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (_rows.TryGetValue(key, out var t)) return t;
            if (Missing != null && _reported.Add(key)) Missing(key);
            return key;
        }

        public string Format(string key, params string[] args)
        {
            string t = Text(key);
            if (args == null || args.Length == 0) return t;
            if (ReferenceEquals(t, key) || !_rows.ContainsKey(key ?? ""))
            {
                // Missing row: show the key with its arguments so a dev build still reads.
                var sb = new StringBuilder(key);
                for (int i = 0; i < args.Length; i++) sb.Append(i == 0 ? " " : ", ").Append(args[i]);
                return sb.ToString();
            }
            return Substitute(t, args);
        }

        /// <summary>Replaces <c>{0}</c>..<c>{9}</c>. 09's ICU plural rows are resolved by the row's text, not here.</summary>
        public static string Substitute(string text, string[] args)
        {
            if (text.IndexOf('{') < 0) return text;
            var sb = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{' && i + 2 < text.Length && text[i + 2] == '}' && text[i + 1] >= '0' && text[i + 1] <= '9')
                {
                    int n = text[i + 1] - '0';
                    sb.Append(n < args.Length ? args[n] : "");
                    i += 2;
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
