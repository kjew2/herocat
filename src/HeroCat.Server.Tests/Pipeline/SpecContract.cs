using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HeroCat.Server.Pipeline;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace HeroCat.Server.Tests.Pipeline;

/// <summary>One endpoint line of specs/08 (<c>**`POST /auth/guest`** -- auth `none` -- idempotent ... -- 5/min, 20/day</c>) and its example JSON.</summary>
sealed record SpecEndpoint(string Method, string Path, string Auth, IReadOnlyList<RateLimitRule> Limits,
                           IReadOnlyDictionary<string, HashSet<string>> BodyKeys, int Line, bool Idempotent = false)
{
    public string Id => Method + " " + Path;
}

/// <summary>
/// Reads the endpoint contract out of specs/08: each endpoint line's method, path, auth policy and rate limits, and the
/// top-level keys of the request and of each 2xx response in the JSON block under it. The examples are illustrative
/// JSON (comments, ellipses), so only key names are compared, which is what the OpenAPI schemas must agree on.
/// </summary>
static partial class SpecContract
{
    [GeneratedRegex(@"^\*\*`(GET|POST|PUT|PATCH|DELETE) (/[^`?\s]*)(?:\?[^`]*)?`\*\* -- (.*)$")]
    private static partial Regex EndpointLine();

    [GeneratedRegex(@"`(none|jwt|jwt\+device|admin)`")]
    private static partial Regex AuthName();

    [GeneratedRegex(@"(\d+)\s*/\s*(min|hour|day)\b|(\d+) per (\d+) days")]
    private static partial Regex Limit();

    public static string SpecText => File.ReadAllText(RepoPaths.Of("specs/08-backend-and-live-ops.md"));

    public static List<SpecEndpoint> Endpoints() => Parse(SpecText);

    public static List<SpecEndpoint> Parse(string spec)
    {
        var lines = spec.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var list = new List<SpecEndpoint>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = EndpointLine().Match(lines[i]);
            if (!m.Success) continue;
            var segments = m.Groups[3].Value.Split(" -- ");
            var auth = AuthName().Match(segments[0]);
            if (!auth.Success) throw new FormatException($"specs/08 line {i + 1}: no auth policy in the first segment");
            var limits = new List<RateLimitRule>();
            bool idempotent = segments.Skip(1).Any(seg => seg.TrimStart().StartsWith("idempotent", StringComparison.Ordinal));
            foreach (string seg in segments.Skip(1))
            {
                string head = seg.Split(". ")[0];
                foreach (Match l in Limit().Matches(head))
                {
                    if (l.Groups[1].Success)
                    {
                        int window = l.Groups[2].Value switch { "min" => RateLimitAttribute.Minute, "hour" => RateLimitAttribute.Hour, _ => RateLimitAttribute.Day };
                        limits.Add(new RateLimitRule(Int(l.Groups[1].Value), window));
                    }
                    else limits.Add(new RateLimitRule(Int(l.Groups[3].Value), Int(l.Groups[4].Value) * RateLimitAttribute.Day));
                }
            }
            list.Add(new SpecEndpoint(m.Groups[1].Value, m.Groups[2].Value, auth.Groups[1].Value, limits, BlockKeys(lines, i + 1), i + 1, idempotent));
        }
        return list;
    }

    static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// The JSON block below an endpoint line (before the next endpoint line or heading), split at its <c>// label</c>
    /// comment lines: <c>request</c> (also an unlabelled leading segment) or a status. Keys of several objects under one
    /// label (alternative request shapes, two 200 examples) are united.
    /// </summary>
    static Dictionary<string, HashSet<string>> BlockKeys(string[] lines, int from)
    {
        var keys = new Dictionary<string, HashSet<string>>();
        int i = from;
        for (; i < lines.Length; i++)
        {
            if (EndpointLine().IsMatch(lines[i]) || lines[i].StartsWith('#')) return keys;
            if (lines[i].StartsWith("```json", StringComparison.Ordinal)) break;
        }
        string label = "request";
        var text = new StringBuilder();
        void Flush()
        {
            var found = TopLevelKeys(text.ToString());
            if (found.Count > 0)
            {
                if (!keys.TryGetValue(label, out var set)) keys[label] = set = new HashSet<string>();
                set.UnionWith(found);
            }
            text.Clear();
        }
        for (i++; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("//", StringComparison.Ordinal))
            {
                Flush();
                string l = t[2..].Trim();
                var status = Regex.Match(l, @"^\d{3}");
                label = status.Success ? status.Value : l.Split(' ')[0];
                continue;
            }
            text.AppendLine(lines[i]);
        }
        Flush();
        return keys;
    }

    /// <summary>The keys at depth 1 of every top-level object in an illustrative JSON text.</summary>
    public static HashSet<string> TopLevelKeys(string json)
    {
        var keys = new HashSet<string>();
        int depth = 0;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') depth--;
            else if (c == '"')
            {
                int end = json.IndexOf('"', i + 1);
                if (end < 0) break;
                string s = json[(i + 1)..end];
                int j = end + 1;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                if (depth == 1 && j < json.Length && json[j] == ':') keys.Add(s);
                i = end;
            }
        }
        return keys;
    }

    /// <summary>The spec's error body example (section 1.1), as keys.</summary>
    public static HashSet<string> ErrorBodyKeys()
    {
        string spec = SpecText;
        int at = spec.IndexOf("Error body, every 4xx/5xx:", StringComparison.Ordinal);
        int open = spec.IndexOf("```json", at, StringComparison.Ordinal);
        int close = spec.IndexOf("```", open + 7, StringComparison.Ordinal);
        return TopLevelKeys(spec[(open + 7)..close]);
    }

    /// <summary>Normalises a route for matching: <c>/v1</c> dropped, parameter names erased, lowercase.</summary>
    public static string Normalise(string path)
    {
        string p = path.StartsWith(RouteRules.ApiBase + "/", StringComparison.OrdinalIgnoreCase) ? path[RouteRules.ApiBase.Length..] : path;
        return Regex.Replace(p.TrimEnd('/'), @"\{[^}]*\}", "{}").ToLowerInvariant();
    }

    /// <summary>Every way the generated document disagrees with the spec for its <c>/v1</c> client operations.</summary>
    public static List<string> Compare(OpenApiDocument doc, IReadOnlyList<SpecEndpoint> spec, Func<string, bool>? include = null)
    {
        var bySpec = spec.GroupBy(e => e.Method + " " + Normalise(e.Path)).ToDictionary(g => g.Key, g => g.First());
        var problems = new List<string>();
        foreach (var (path, item) in doc.Paths)
        {
            if (!RouteRules.IsClientRoute(path)) continue;
            foreach (var (type, op) in item.Operations)
            {
                string id = type.ToString().ToUpperInvariant() + " " + Normalise(path);
                if (include != null && !include(id)) continue;
                if (!bySpec.TryGetValue(id, out var want))
                {
                    problems.Add($"{id}: not an endpoint in specs/08");
                    continue;
                }
                string auth = op.Extensions.TryGetValue(OpenApi.AuthExtension, out var a) && a is OpenApiString s ? s.Value : "?";
                if (auth != want.Auth) problems.Add($"{id}: auth {auth}, specs/08 line {want.Line} says {want.Auth}");

                var limits = op.Extensions.TryGetValue(OpenApi.RateLimitsExtension, out var l) && l is OpenApiArray arr
                    ? arr.OfType<OpenApiString>().Select(x => x.Value).OrderBy(x => x).ToList()
                    : new List<string>();
                var wantLimits = want.Limits.Select(r => r.ToString()).OrderBy(x => x).ToList();
                if (!limits.SequenceEqual(wantLimits))
                    problems.Add($"{id}: rate limits [{string.Join(", ", limits)}], specs/08 line {want.Line} says [{string.Join(", ", wantLimits)}]");

                bool idem = op.Extensions.TryGetValue(OpenApi.IdempotentExtension, out var ie) && ie is OpenApiBoolean b && b.Value;
                if (idem != want.Idempotent)
                    problems.Add($"{id}: idempotent {idem.ToString().ToLowerInvariant()}, specs/08 line {want.Line} says {want.Idempotent.ToString().ToLowerInvariant()}");

                foreach (var (label, wantKeys) in want.BodyKeys)
                {
                    OpenApiSchema? schema;
                    if (label == "request") schema = op.RequestBody?.Content.TryGetValue("application/json", out var mt) == true ? mt.Schema : null;
                    else if (label.Length == 3 && label[0] == '2') schema = op.Responses.TryGetValue(label, out var r) && r.Content.TryGetValue("application/json", out var rmt) ? rmt.Schema : null;
                    else continue;
                    if (schema == null)
                    {
                        problems.Add($"{id}: no {label} JSON schema, specs/08 line {want.Line} shows one");
                        continue;
                    }
                    var have = Properties(doc, schema);
                    if (!have.SetEquals(wantKeys))
                        problems.Add($"{id} {label}: properties [{string.Join(", ", have.OrderBy(x => x))}], specs/08 line {want.Line} shows [{string.Join(", ", wantKeys.OrderBy(x => x))}]");
                }
            }
        }
        return problems;
    }

    public static HashSet<string> Properties(OpenApiDocument doc, OpenApiSchema schema)
    {
        var s = Resolve(doc, schema);
        var names = new HashSet<string>(s.Properties.Keys);
        foreach (var part in s.AllOf) names.UnionWith(Properties(doc, part));
        return names;
    }

    static OpenApiSchema Resolve(OpenApiDocument doc, OpenApiSchema schema) =>
        schema.Reference != null && doc.Components.Schemas.TryGetValue(schema.Reference.Id, out var target) ? target : schema;
}
