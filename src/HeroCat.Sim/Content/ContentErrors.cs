using System;
using System.Collections.Generic;

namespace HeroCat.Sim.Content
{
    /// <summary>One validation finding, printed in the exporter's format (09 section 8.1).</summary>
    public readonly struct ContentError
    {
        public ContentError(string code, string location, string message)
        {
            Code = code;
            Location = location;
            Message = message;
        }

        public string Code { get; }
        public string Location { get; }
        public string Message { get; }

        public override string ToString() => "CONTENT " + Code + " " + Location + ": " + Message;
    }

    /// <summary>ContentDb.Build or the loader rejected the bundle. Carries every error, not the first (09 section 8.1).</summary>
    public sealed class ContentBuildException : Exception
    {
        public ContentBuildException(IReadOnlyList<ContentError> errors)
            : base(Format(errors))
        {
            Errors = errors;
        }

        public IReadOnlyList<ContentError> Errors { get; }

        static string Format(IReadOnlyList<ContentError> errors)
        {
            var lines = new string[errors.Count];
            for (int i = 0; i < errors.Count; i++) lines[i] = errors[i].ToString();
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// content_version ordering (09 section 6.3): YYYY.MM.DD[.N], numeric per component, a missing N reads as 0.
    /// The format carries no major/minor meaning.
    /// </summary>
    public static class ContentVersion
    {
        /// <summary>True for YYYY.MM.DD or YYYY.MM.DD.N (N &gt;= 1), the exporter's E094 rule.</summary>
        public static bool IsValid(string v) => TryParse(v, out _);

        /// <summary>Negative, zero or positive like string.CompareOrdinal. Throws FormatException on a malformed version.</summary>
        public static int Compare(string a, string b)
        {
            if (!TryParse(a, out var pa)) throw new FormatException("bad content_version '" + a + "'");
            if (!TryParse(b, out var pb)) throw new FormatException("bad content_version '" + b + "'");
            // literal-ok-begin: content_version format YYYY.MM.DD[.N] (00 contract 3), not balance
            for (int i = 0; i < 4; i++)
                if (pa[i] != pb[i]) return pa[i] < pb[i] ? -1 : 1;
            return 0;
        }

        static bool TryParse(string v, out int[] parts)
        {
            parts = new int[4];
            if (string.IsNullOrEmpty(v)) return false;
            var bits = v.Split('.');
            if (bits.Length != 3 && bits.Length != 4) return false;
            int[] widths = { 4, 2, 2, 0 };
            for (int i = 0; i < bits.Length; i++)
            {
                string s = bits[i];
                if (s.Length == 0 || (widths[i] > 0 && s.Length != widths[i]) || s.Length > 9) return false;
                int n = 0;
                foreach (char ch in s)
                {
                    if (ch < '0' || ch > '9') return false;
                    n = n * 10 + (ch - '0');
                }
                if (i == 3 && (n < 1 || s[0] == '0')) return false;
                parts[i] = n;
            }
            // literal-ok-end
            return true;
        }
    }
}
