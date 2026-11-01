using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HeroCat.Bots.Report;
using HeroCat.Sim.Content;

namespace HeroCat.Bots.Acceptance
{
    /// <summary>
    /// Harness-side acceptance settings (10 section 3.6). The day/stage targets and tolerance are not here: they are
    /// content (09 progress_targets), read from ContentDb. TODO(#136): the per-tier floor exemptions and the AC table.
    /// </summary>
    public sealed class AcceptanceTargets
    {
        public int[] StageCheckDays { get; set; }

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public static AcceptanceTargets Parse(string json) => JsonSerializer.Deserialize<AcceptanceTargets>(json, Opts);

        public static AcceptanceTargets LoadDefault()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("acceptance-targets.json")
                          ?? throw new InvalidOperationException("embedded acceptance-targets.json missing");
            using var r = new StreamReader(s);
            return Parse(r.ReadToEnd());
        }

        public static AcceptanceTargets Load(string pathOrNull) => pathOrNull == null ? LoadDefault() : Parse(File.ReadAllText(pathOrNull));
    }

    public sealed record CheckResult(string Id, bool Pass, string Message, IReadOnlyList<string> Details);

    /// <summary>
    /// The skeleton's acceptance checks: AC1 (median stage within progress_targets' tolerance on the check days) and the
    /// determinism check. The CLI verb and the tests call this same class. TODO(#136): AC2-AC4, AC9, AC10.
    /// </summary>
    public static class AcceptanceChecker
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <param name="determinism">Returns null when deterministic (and golden matches), else the first difference.</param>
        public static List<CheckResult> Check(SweepResult sw, ContentDb db, AcceptanceTargets t, Func<string> determinism)
        {
            var results = new List<CheckResult>();
            foreach (int day in t.StageCheckDays.Where(d => d <= sw.Days))
            {
                var target = db.ProgressTargets.FirstOrDefault(p => p.Day == day);
                if (target == null)
                {
                    results.Add(new CheckResult("AC1", false, string.Format(Inv, "day {0}: no progress_targets row", day), Array.Empty<string>()));
                    continue;
                }
                var stages = sw.Runs.Select(r => r.Rows[day - 1].Stage).OrderBy(x => x).ToList();
                int med = Sweep.Percentile(stages, 0.5);
                int lo = (int)Math.Ceiling(target.Stage * (1 - target.Tolerance)), hi = (int)Math.Floor(target.Stage * (1 + target.Tolerance));
                bool ok = med >= lo && med <= hi;
                results.Add(new CheckResult("AC1", ok,
                    string.Format(Inv, "day {0}: median stage {1} {2} {3}..{4} (target {5})", day, med, ok ? "in" : "outside", lo, hi, target.Stage),
                    Array.Empty<string>()));
            }
            if (determinism != null)
            {
                string diff = determinism();
                results.Add(new CheckResult("AC6", diff == null, diff == null ? "deterministic: seed 7 twice identical; golden matches" : "determinism or golden check failed: " + diff,
                    Array.Empty<string>()));
            }
            return results;
        }
    }
}
