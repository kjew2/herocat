using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>
    /// 07 section 15.1: every character the client can show is in the M0 UI font (DejaVu Sans), so TMP never draws a
    /// box. Scans the client's code (literal characters and \u / \U escapes outside comments) and the English
    /// localization, then looks each character up in the font's cmap.
    /// </summary>
    public class UiFontCoverageTests
    {
        static string ClientDir => Path.Combine(TestContent.RepoRoot, "unity", "HeroCatClient", "Assets", "HeroCat.Client");
        static string FontPath => Path.Combine(TestContent.RepoRoot, "unity", "HeroCatClient", "Assets", "Resources", "Fonts", "DejaVuSans.ttf");

        static readonly Regex Escape = new Regex(@"\\u([0-9A-Fa-f]{4})|\\U([0-9A-Fa-f]{8})", RegexOptions.Compiled);

        [Test]
        public void EveryCharacterTheClientShows_IsInTheUiFont()
        {
            var cmap = ReadCmap(File.ReadAllBytes(FontPath));
            Assert.That(cmap.Count, Is.GreaterThan(1000), "cmap parse");

            var used = new Dictionary<int, string>();
            foreach (var dir in new[] { "Screens", "Core" })
                foreach (var f in Directory.GetFiles(Path.Combine(ClientDir, dir), "*.cs", SearchOption.AllDirectories))
                {
                    int n = 0;
                    foreach (var line in File.ReadLines(f))
                    {
                        n++;
                        string code = StripComment(line);
                        string where = Path.GetFileName(f) + ":" + n;
                        foreach (Match m in Escape.Matches(code))
                            Add(used, int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, NumberStyles.HexNumber), where);
                        AddText(used, code, where);
                    }
                }
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContent.RepoRoot, "content", "localization.en.json"))))
                Walk(doc.RootElement, used);

            var missing = new List<string>();
            foreach (var kv in used)
                if (!cmap.Contains(kv.Key)) missing.Add("U+" + kv.Key.ToString("X4") + " " + char.ConvertFromUtf32(kv.Key) + " at " + kv.Value);
            Assert.That(missing, Is.Empty, "characters the UI font lacks (pick a glyph DejaVu Sans has)");
        }

        static string StripComment(string line)
        {
            // Good enough for this code base: no "//" inside the string literals that hold glyphs.
            int i = line.IndexOf("//", System.StringComparison.Ordinal);
            return i < 0 ? line : line.Substring(0, i);
        }

        static void AddText(Dictionary<int, string> used, string s, string where)
        {
            for (int i = 0; i < s.Length; i++)
            {
                int cp = char.ConvertToUtf32(s, i);
                if (cp > 0xFFFF) i++;
                if (cp > 0x7E) Add(used, cp, where);
            }
        }

        static void Add(Dictionary<int, string> used, int cp, string where)
        {
            if (cp == 0xFEFF) return;   // byte order mark
            if (!used.ContainsKey(cp)) used[cp] = where;
        }

        static void Walk(JsonElement e, Dictionary<int, string> used)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value, used); break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Walk(x, used); break;
                case JsonValueKind.String: AddText(used, e.GetString() ?? "", "localization.en.json"); break;
            }
        }

        /// <summary>The code points mapped by the font's Unicode cmap subtables (formats 4 and 12).</summary>
        static HashSet<int> ReadCmap(byte[] b)
        {
            int U16(int o) => (b[o] << 8) | b[o + 1];
            long U32(int o) => ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
            var set = new HashSet<int>();
            int numTables = U16(4), cmap = -1;
            for (int t = 0; t < numTables; t++)
            {
                int rec = 12 + 16 * t;
                if (Encoding.ASCII.GetString(b, rec, 4) == "cmap") cmap = (int)U32(rec + 8);
            }
            Assert.That(cmap, Is.GreaterThanOrEqualTo(0), "no cmap table");
            int subs = U16(cmap + 2);
            for (int s = 0; s < subs; s++)
            {
                int platform = U16(cmap + 4 + 8 * s), encoding = U16(cmap + 6 + 8 * s);
                if (!(platform == 0 || (platform == 3 && (encoding == 1 || encoding == 10)))) continue;
                int st = cmap + (int)U32(cmap + 8 + 8 * s);
                int format = U16(st);
                if (format == 4)
                {
                    int segX2 = U16(st + 6);
                    int ends = st + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                    for (int i = 0; i < segX2 / 2; i++)
                    {
                        int end = U16(ends + 2 * i), start = U16(starts + 2 * i);
                        int delta = (short)U16(deltas + 2 * i), rangeOff = U16(ranges + 2 * i);
                        for (int c = start; c <= end && c != 0xFFFF; c++)
                        {
                            int glyph = rangeOff == 0 ? (c + delta) & 0xFFFF
                                : U16(ranges + 2 * i + rangeOff + 2 * (c - start));
                            if (rangeOff != 0 && glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                            if (glyph != 0) set.Add(c);
                        }
                    }
                }
                else if (format == 12)
                {
                    long groups = U32(st + 12);
                    for (long g = 0; g < groups; g++)
                    {
                        int o = st + 16 + (int)(12 * g);
                        long start = U32(o), end = U32(o + 4), glyph = U32(o + 8);
                        for (long c = start; c <= end; c++) if (glyph + (c - start) != 0) set.Add((int)c);
                    }
                }
            }
            return set;
        }
    }
}
