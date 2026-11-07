using System.Text.Json;
using System.Text.RegularExpressions;
using HeroCat.Server.Observability;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Observability;

/// <summary>
/// <c>server/ops/alarms.json</c> is the specs/10 section 10 alarm table as code: every row is there with its severity
/// and <c>RB-*</c> runbook, every runbook exists in specs/10 section 9 and in 08 section 9.5's list, and every
/// Prometheus expression names only section 9.4 metrics.
/// </summary>
[TestFixture]
public sealed class AlarmCatalogTests
{
    sealed record TableRow(string Alarm, string Severity, string? Runbook);

    static readonly string[] Sources = { "prometheus", "logs", "sql", "analytics", "job", "cloudwatch", "crashlytics", "budget", "rollout" };
    static readonly string[] Severities = { "P1", "P2", "notify" };

    static JsonElement Catalog() => JsonDocument.Parse(File.ReadAllText(RepoPaths.Of("server/ops/alarms.json"))).RootElement.Clone();

    static string Section(string specFile, string heading, string next)
    {
        string spec = File.ReadAllText(RepoPaths.Of(specFile));
        int start = spec.IndexOf(heading, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), heading);
        int end = spec.IndexOf(next, start + heading.Length, StringComparison.Ordinal);
        return spec[start..(end < 0 ? spec.Length : end)];
    }

    static List<TableRow> AlarmTable()
    {
        string section = Section("specs/10-testing-release-and-ops.md", "## 10. Dashboards and alarms", "\n## ");
        int header = section.IndexOf("| Alarm | Condition | Severity | Runbook |", StringComparison.Ordinal);
        Assert.That(header, Is.GreaterThan(0));
        return section[header..].Split('\n').Skip(2).TakeWhile(l => l.StartsWith('|'))
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries))
            .Select(c => new TableRow(c[1], c[3], Regex.Match(c[4], "`(RB-[A-Z0-9-]+)`") is { Success: true } m ? m.Groups[1].Value : null))
            .ToList();
    }

    static IEnumerable<JsonElement> Alarms() => Catalog().GetProperty("alarms").EnumerateArray();

    [Test]
    public void Every_alarm_of_the_spec_table_is_defined_with_its_severity_and_runbook()
    {
        var table = AlarmTable();
        Assert.That(table, Has.Count.GreaterThanOrEqualTo(28), "parsed the specs/10 section 10 table");
        var byName = Alarms().ToDictionary(a => a.GetProperty("alarm").GetString()!);
        foreach (var row in table)
        {
            Assert.That(byName.ContainsKey(row.Alarm), $"alarm '{row.Alarm}' (specs/10 section 10) is not in server/ops/alarms.json");
            var a = byName[row.Alarm];
            Assert.That(a.GetProperty("severity").GetString(), Is.EqualTo(row.Severity), row.Alarm + " severity");
            if (row.Runbook != null)
                Assert.That(a.GetProperty("runbook").GetString(), Is.EqualTo(row.Runbook), row.Alarm + " runbook");
            else
            {
                Assert.That(a.GetProperty("runbook").ValueKind, Is.EqualTo(JsonValueKind.Null), row.Alarm + " has no runbook of its own");
                Assert.That(a.GetProperty("runbookFrom").GetString(), Is.Not.Empty);
            }
        }
        Assert.That(byName.Keys, Is.EquivalentTo(table.Select(r => r.Alarm)), "no alarm outside the spec table");
        Assert.That(Alarms().Select(a => a.GetProperty("id").GetString()), Is.Unique);
    }

    [Test]
    public void Every_runbook_exists_in_specs_10_and_in_08_section_9_5()
    {
        string runbooks = Section("specs/10-testing-release-and-ops.md", "## 9. Live-ops runbooks", "\n## 10.");
        var defined = Regex.Matches(runbooks, @"^(?:###[^\n]*\(`(RB-[A-Z0-9-]+)`\)|\| `(RB-[A-Z0-9-]+)` \|)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToHashSet();
        string listed = Section("specs/08-backend-and-live-ops.md", "### 9.5 Alarm list", "### 9.6");
        foreach (string rb in Alarms().Select(a => a.GetProperty("runbook")).Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).Distinct())
        {
            Assert.That(defined, Does.Contain(rb), rb + " has no runbook in specs/10 section 9");
            Assert.That(listed, Does.Contain("`" + rb + "`"), rb + " is missing from specs/08 section 9.5's runbook list");
        }
    }

    [Test]
    public void Every_condition_has_a_known_source_and_severity_matching_its_alarm()
    {
        foreach (var a in Alarms())
        {
            string name = a.GetProperty("alarm").GetString()!;
            var conds = a.GetProperty("conditions").EnumerateArray().ToList();
            Assert.That(conds, Is.Not.Empty, name);
            var severities = new HashSet<string>();
            foreach (var c in conds)
            {
                Assert.That(Sources, Does.Contain(c.GetProperty("source").GetString()), name + " source");
                Assert.That(Severities, Does.Contain(c.GetProperty("severity").GetString()), name + " condition severity");
                Assert.That(c.GetProperty("expr").GetString(), Is.Not.Empty, name + " expr");
                severities.Add(c.GetProperty("severity").GetString()!);
            }
            var declared = a.GetProperty("severity").GetString()!.Split('/', StringSplitOptions.TrimEntries)
                .Select(s => s == "-" ? "notify" : s).ToHashSet();
            Assert.That(severities, Is.EquivalentTo(declared), name + ": the conditions' severities are the table's");
        }
    }

    static readonly HashSet<string> PromQlWords = new(StringComparer.Ordinal)
    {
        "sum", "min", "max", "avg", "count", "rate", "increase", "changes", "histogram_quantile", "quantile_over_time",
        "by", "without", "on", "ignoring", "and", "or", "unless", "le",
    };

    /// <summary>The metric names a PromQL expression reads: its identifiers, less functions, keywords, labels and strings.</summary>
    static IEnumerable<string> MetricsIn(string expr)
    {
        var labels = MetricCatalog.All.SelectMany(m => m.Labels).ToHashSet();
        string noStrings = Regex.Replace(expr, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
        return Regex.Matches(noStrings, @"(?<![\w.])[a-zA-Z_][a-zA-Z0-9_]*")
            .Select(m => m.Value)
            .Where(w => !PromQlWords.Contains(w) && !labels.Contains(w))
            .Distinct();
    }

    [Test]
    public void Prometheus_conditions_read_only_section_9_4_metrics()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in MetricCatalog.All)
        {
            known.Add(m.Name);
            if (m.Kind == MetricKind.Histogram) known.UnionWith(new[] { m.Name + "_bucket", m.Name + "_sum", m.Name + "_count" });
        }
        var exprs = Alarms().SelectMany(a => a.GetProperty("conditions").EnumerateArray())
            .Where(c => c.GetProperty("source").GetString() == "prometheus")
            .Select(c => c.GetProperty("expr").GetString()!)
            .Concat(Catalog().GetProperty("recordingRules").EnumerateArray().Select(r => r.GetProperty("expr").GetString()!))
            .ToList();
        Assert.That(exprs, Has.Count.GreaterThan(15));
        foreach (string e in exprs)
        {
            var metrics = MetricsIn(e).ToList();
            Assert.That(metrics, Is.Not.Empty, e);
            foreach (string m in metrics) Assert.That(known, Does.Contain(m), $"'{m}' in '{e}' is not a specs/08 section 9.4 metric");
        }
    }

    [Test]
    public void Every_derived_metric_has_its_recording_rule()
    {
        var rules = Catalog().GetProperty("recordingRules").EnumerateArray().Select(r => r.GetProperty("record").GetString()).ToList();
        foreach (var m in MetricCatalog.All.Where(m => m.Source == MetricSource.Derived))
            Assert.That(rules, Does.Contain(m.Name), m.Name + " is derived but has no recording rule");
    }

    [Test]
    public void The_metrics_the_alarm_table_names_are_read_by_an_alarm()
    {
        string all = File.ReadAllText(RepoPaths.Of("server/ops/alarms.json"));
        foreach (string m in new[]
                 {
                     HttpMetrics.RequestsTotal, HttpMetrics.DurationSeconds, IapMetrics.ValidateTotal, IapMetrics.WebhookTotal,
                     SaveMetrics.FlagsTotal, ModerationMetrics.ActionsTotal, DbMetrics.PoolInUse, WorkerJobMetrics.FailuresTotal,
                     "db_partition_horizon_days", ConfigMetrics.ActiveVersion,
                 })
            Assert.That(all, Does.Contain(m), m);
    }
}
