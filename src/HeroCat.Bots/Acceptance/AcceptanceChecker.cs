using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HeroCat.Bots.Measure;
using HeroCat.Bots.Report;

namespace HeroCat.Bots.Acceptance
{
    /// <summary>specs/02 section 9 / specs/00 section 2.6, the single copy in code.</summary>
    public sealed class AcceptanceTargets
    {
        public Dictionary<string, int> StageTargets { get; set; }
        public double StageTolerance { get; set; }
        public int[] StageCheckDays { get; set; }
        public int[] ShareCheckDays { get; set; }
        public double ShareMin { get; set; }
        public double ShareMax { get; set; }
        public int FirstWallMinStage { get; set; }
        public int FirstWallMaxStage { get; set; }
        public int MaxWallHoursThroughDay { get; set; }
        public double MaxWallHours { get; set; }
        public double StatLevelSlope { get; set; }
        public double StatLevelIntercept { get; set; }

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

    /// <summary>AC1-AC6 of specs/09 section 9. The CLI verb and the NUnit tests call this same class.</summary>
    public static class AcceptanceChecker
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <param name="determinism">Returns null when deterministic (and golden matches), else the first differing line / reason.</param>
        public static List<CheckResult> Check(SweepResult sw, AcceptanceTargets t, Func<string> determinism)
        {
            var results = new List<CheckResult>();
            var runs = sw.Runs;

            // AC1: median stage within tolerance on check days
            foreach (int day in t.StageCheckDays.Where(d => d <= sw.Days))
            {
                int target = t.StageTargets[day.ToString(Inv)];
                var stages = runs.Select(r => r.Rows[day - 1].Stage).OrderBy(x => x).ToList();
                int med = Sweep.Percentile(stages, 0.5);
                int lo = (int)Math.Ceiling(target * (1 - t.StageTolerance)), hi = (int)Math.Floor(target * (1 + t.StageTolerance));
                bool ok = med >= lo && med <= hi;
                results.Add(new CheckResult("AC1", ok,
                    string.Format(Inv, "day {0}: median stage {1} {2} {3}..{4} (target {5})", day, med, ok ? "in" : "outside", lo, hi, target),
                    Array.Empty<string>()));
            }

            // AC2: layer shares per seed on check days
            {
                var fails = new List<string>();
                var details = new List<string>();
                int[] days = t.ShareCheckDays.Where(d => d <= sw.Days).ToArray();
                foreach (var r in runs)
                    foreach (int day in days)
                    {
                        var s = LayerShares.Of(r.Rows[day - 1], r.BaseAtk);
                        details.Add(string.Format(Inv, "seed {0} day {1}: stat {2:0.000} gear {3:0.000} hero {4:0.000} class {5:0.000}",
                                                  r.Seed, day, s.Stat, s.Gear, s.Hero, s.Class));
                        foreach (var (layer, share) in s.Layers)
                            if (!(share >= t.ShareMin && share <= t.ShareMax))
                                fails.Add(string.Format(Inv, "seed {0} day {1} {2} share {3:0.000}", r.Seed, day, layer, share));
                    }
                results.Add(new CheckResult("AC2", fails.Count == 0,
                    fails.Count == 0
                        ? string.Format(Inv, "layer shares in {0:0.00}..{1:0.00} on days {2} for {3} seeds", t.ShareMin, t.ShareMax, string.Join(",", days), runs.Length)
                        : string.Format(Inv, "layer shares outside {0:0.00}..{1:0.00}: {2} case(s), first: {3}", t.ShareMin, t.ShareMax, fails.Count, fails[0]),
                    details));
            }

            // AC3: first wall stage in range for every seed
            {
                var bad = runs.Where(r => !InRange(r.Rows[r.Days - 1].FirstWallStage, t.FirstWallMinStage, t.FirstWallMaxStage)).ToList();
                var details = runs.Select(r => string.Format(Inv, "seed {0}: first wall {1}", r.Seed, r.Rows[r.Days - 1].FirstWallStage)).ToList();
                results.Add(new CheckResult("AC3", bad.Count == 0,
                    bad.Count == 0
                        ? string.Format(Inv, "first wall in {0}..{1} for {2} seeds", t.FirstWallMinStage, t.FirstWallMaxStage, runs.Length)
                        : string.Format(Inv, "first wall in {0}..{1}: {2} seed(s) outside, first: seed {3} walled first at stage {4}",
                              t.FirstWallMinStage, t.FirstWallMaxStage, bad.Count, bad[0].Seed, bad[0].Rows[bad[0].Days - 1].FirstWallStage),
                    details));
            }

            // AC4: walls opened by day N last <= maxWallHours wall-clock
            {
                var fails = new List<string>();
                var details = new List<string>();
                foreach (var r in runs)
                {
                    long endMs = r.DayEndUtcMs[r.Days - 1];
                    double longest = 0; int longestStage = 0;
                    foreach (var w in r.Walls.Where(w => w.OpenDay <= t.MaxWallHoursThroughDay))
                    {
                        double h = w.WallClockHours(endMs);
                        if (h > longest) { longest = h; longestStage = w.Stage; }
                        if (h > t.MaxWallHours) fails.Add(string.Format(Inv, "seed {0} stage {1} {2:0.00} h", r.Seed, w.Stage, h));
                    }
                    details.Add(string.Format(Inv, "seed {0}: longest wall by day {1}: stage {2} {3:0.00} h", r.Seed, t.MaxWallHoursThroughDay, longestStage, longest));
                }
                results.Add(new CheckResult("AC4", fails.Count == 0,
                    fails.Count == 0
                        ? string.Format(Inv, "no wall opened by day {0} exceeds {1:0.00} h", t.MaxWallHoursThroughDay, t.MaxWallHours)
                        : string.Format(Inv, "{0} wall(s) opened by day {1} exceed {2:0.00} h, first: {3}", fails.Count, t.MaxWallHoursThroughDay, t.MaxWallHours, fails[0]),
                    details));
            }

            // AC5: stat level bound
            {
                var fails = new List<string>();
                foreach (var r in runs)
                    foreach (var row in r.Rows)
                    {
                        double bound = t.StatLevelSlope * row.Stage + t.StatLevelIntercept;
                        if (row.StatLvl > bound)
                            fails.Add(string.Format(Inv, "seed {0} day {1} statLvl {2} > {3:0.0}", r.Seed, row.Day, row.StatLvl, bound));
                    }
                results.Add(new CheckResult("AC5", fails.Count == 0,
                    fails.Count == 0
                        ? string.Format(Inv, "statLvl <= {0}*stage+{1} on every day for {2} seeds", t.StatLevelSlope, t.StatLevelIntercept, runs.Length)
                        : string.Format(Inv, "statLvl bound broken in {0} row(s), first: {1}", fails.Count, fails[0]),
                    Array.Empty<string>()));
            }

            // AC6: determinism (+ golden when available)
            if (determinism != null)
            {
                string diff = determinism();
                results.Add(new CheckResult("AC6", diff == null, diff == null ? "deterministic: seed 7 twice identical; golden matches" : "not deterministic: " + diff,
                    Array.Empty<string>()));
            }

            return results;
        }

        static bool InRange(int v, int lo, int hi) => v >= lo && v <= hi;
    }
}
