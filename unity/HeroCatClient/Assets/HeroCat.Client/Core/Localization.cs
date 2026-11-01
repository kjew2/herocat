using System;
using System.Collections.Generic;
using System.Text;
using HeroCat.Sim.Commands;

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

    /// <summary>
    /// Key builders (07 sections 3 and 12). Issue #248 decided the casing: wire codes and reject reasons stay UPPER_SNAKE
    /// in code and on the wire, and the client lowercases them when it builds the key, so every key matches 09's lowercase
    /// key regex: errors are <c>err.&lt;code_lower&gt;.title</c> / <c>.body</c>, rejects <c>reject.&lt;reason_lower&gt;</c>.
    /// </summary>
    public static class LocKeys
    {
        public static string ErrTitle(string code) => "err." + Lower(code) + ".title";

        public static string ErrBody(string code) => "err." + Lower(code) + ".body";

        /// <summary>
        /// <c>reject.&lt;reason_lower&gt;</c>; a reject without a reason uses its <see cref="RejectCode"/> in snake case
        /// (<c>CannotAfford</c> -&gt; <c>reject.cannot_afford</c>).
        /// </summary>
        public static string Reject(ApplyResult r) =>
            "reject." + (string.IsNullOrEmpty(r.Reason) ? Snake(r.Code.ToString()) : Lower(r.Reason));

        public static string Reject(string reason) => "reject." + Lower(reason);

        static string Lower(string s) => string.IsNullOrEmpty(s) ? "unknown" : s.ToLowerInvariant();

        /// <summary>PascalCase to lower snake case.</summary>
        public static string Snake(string pascal)
        {
            var sb = new StringBuilder(pascal.Length + 4);
            for (int i = 0; i < pascal.Length; i++)
            {
                char c = pascal[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
