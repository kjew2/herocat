using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeroCat.Bots.Measure;
using HeroCat.Bots.Report;
using HeroCat.Sim.Content;

namespace HeroCat.Bots.Acceptance
{
    public sealed class LazyFraction
    {
        public double Day7 { get; set; }
        public double Day30 { get; set; }
    }

    /// <summary>
    /// Harness-side acceptance thresholds that are not in 00 (10 section 3.6), embedded from acceptance-targets.json. The
    /// day/stage targets and tolerance are not here: they are content (09 progress_targets) read from ContentDb, as are the
    /// Awakening cadence targets (awakening_targets).
    /// </summary>
    public sealed class AcceptanceTargets
    {
        public string SweepBot { get; set; }
        public string Seeds { get; set; }
        public int Days { get; set; }
        public int[] StageCheckDays { get; set; }
        public int[] ShareMaxCheckDays { get; set; }
        public int[] ShareMinCheckDays { get; set; }
        public double ShareMax { get; set; }
        public double ShareMin { get; set; }
        public string[] ShareFloorExcluded { get; set; }
        public Dictionary<string, string[]> ShareFloorExempt { get; set; }
        public int FirstWallMinStage { get; set; }
        public int FirstWallMaxStage { get; set; }
        public double MaxWallActiveMinutesThroughDay14 { get; set; }
        public double? LateWallMaxActiveMinutes { get; set; }
        public LazyFraction LazyFraction { get; set; }
        public int[] WhaleGapStagesDay7 { get; set; }
        public double DiamondsPerDayTolerance { get; set; }
        public double TimeBudgetSeconds { get; set; }

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.Strict,
        };

        public static AcceptanceTargets Parse(string json) => JsonSerializer.Deserialize<AcceptanceTargets>(json, Opts);

        public static AcceptanceTargets LoadDefault()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("acceptance-targets.json")
                          ?? throw new InvalidOperationException("embedded acceptance-targets.json missing");
            using var r = new StreamReader(s);
            return Parse(r.ReadToEnd());
        }

        public static AcceptanceTargets Load(string pathOrNull) => pathOrNull == null ? LoadDefault() : Parse(File.ReadAllText(pathOrNull));

        /// <summary>The per-tier floor exemptions (WARN instead of FAIL) for <paramref name="tier"/>.</summary>
        public IReadOnlyCollection<string> ExemptFor(Tier tier) =>
            ShareFloorExempt != null && ShareFloorExempt.TryGetValue(tier.ToString(), out var l) && l != null ? l : Array.Empty<string>();
    }

    public enum CheckStatus { Pass, Fail, Warn, Skip }

    public sealed record CheckResult(string Id, CheckStatus Status, string Message, IReadOnlyList<string> Details)
    {
        public bool Pass => Status != CheckStatus.Fail;

        public string Line => Status.ToString().ToUpperInvariant() + " " + Id + " " + Message;
    }

    /// <summary>
    /// The acceptance gate of 10 section 3.6 for the checks the M0 bots can run: AC1 (day-stage targets), AC2 (layer-share
    /// ceiling and floor), AC3 (first wall), AC4 (wall length in active minutes), AC9 (determinism, golden replay), AC10
    /// (time budget, added by the caller), AC11 (Awakening cadence; SKIP when the tier has no awakening rows). AC5-AC8 need
    /// the MVP bots (lazy, whale, quests) and are reported SKIP. The CLI verb and the tests call this same class.
    /// </summary>
    public static class AcceptanceChecker
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly IReadOnlyList<string> None = Array.Empty<string>();

        static string F(string fmt, params object[] args) => string.Format(Inv, fmt, args);

        static string Pct(double v) => F("{0:0.0}%", 100 * v);

        /// <param name="determinism">Returns null when deterministic and the golden replay matches, else the first difference. Null: skipped.</param>
        public static List<CheckResult> Check(SweepResult sw, ContentDb db, AcceptanceTargets t, Func<string> determinism)
        {
            var results = new List<CheckResult>();
            Ac1(sw, db, t, results);
            Ac2(sw, db, t, results);
            Ac3(sw, t, results);
            Ac4(sw, t, results);
            foreach (var id in new[] { "AC5", "AC6", "AC7", "AC8" })
                results.Add(new CheckResult(id, CheckStatus.Skip, "needs the MVP bots and economy (TODO(#214, #215))", None));
            if (determinism != null)
            {
                string diff = determinism();
                results.Add(new CheckResult("AC9", diff == null ? CheckStatus.Pass : CheckStatus.Fail,
                    diff == null ? "seed 7 twice in-process is byte-identical; the golden replay matches" : "determinism: " + diff, None));
            }
            Ac11(sw, db, results);
            return results;
        }

        /// <summary>AC1: for each check day that exists, the median stage within progress_targets' tolerance.</summary>
        static void Ac1(SweepResult sw, ContentDb db, AcceptanceTargets t, List<CheckResult> results)
        {
            foreach (int day in t.StageCheckDays.Where(d => d <= sw.Days))
            {
                var target = db.ProgressTargets.FirstOrDefault(p => p.Day == day);
                if (target == null)
                {
                    results.Add(new CheckResult("AC1", CheckStatus.Fail, F("day {0}: no progress_targets row", day), None));
                    continue;
                }
                var stages = sw.Runs.Select(r => r.Rows[day - 1].Stage).OrderBy(x => x).ToList();
                int med = Sweep.Percentile(stages, 0.5);
                int lo = (int)Math.Ceiling(target.Stage * (1 - target.Tolerance) - 1e-9), hi = (int)Math.Floor(target.Stage * (1 + target.Tolerance) + 1e-9);
                bool ok = med >= lo && med <= hi;
                results.Add(new CheckResult("AC1", ok ? CheckStatus.Pass : CheckStatus.Fail,
                    F("day {0}: median stage {1} {2} {3}..{4} (target {5})", day, med, ok ? "in" : "outside", lo, hi, target.Stage),
                    sw.Runs.Select(r => F("seed {0}: stage {1}", r.Seed, r.Rows[day - 1].Stage)).ToList()));
            }
        }

        static bool NonZero(double share) => Math.Abs(share) > 1e-12;

        /// <summary>
        /// AC2 (issue #12): every layer share &lt;= shareMax on the ceiling days and every non-zero, non-excluded layer share
        /// &gt;= shareMin on the floor days, per seed. Floor misses of the tier's exempt layers are WARN. Base is not a layer.
        /// </summary>
        static void Ac2(SweepResult sw, ContentDb db, AcceptanceTargets t, List<CheckResult> results)
        {
            var layers = LayerShares.Names.Where(n => n != "Base").ToArray();
            foreach (int day in t.ShareMaxCheckDays.Where(d => d <= sw.Days))
            {
                string worstLayer = null;
                ulong worstSeed = 0;
                double worst = double.NegativeInfinity;
                var details = new List<string>();
                foreach (var r in sw.Runs)
                    foreach (var n in layers)
                    {
                        double v = r.Rows[day - 1].Shares.Get(n);
                        if (v > worst) { worst = v; worstLayer = n; worstSeed = r.Seed; }
                        if (v > t.ShareMax) details.Add(F("seed {0}: {1} {2}", r.Seed, n, Pct(v)));
                    }
                bool ok = details.Count == 0;
                results.Add(new CheckResult("AC2", ok ? CheckStatus.Pass : CheckStatus.Fail,
                    F("day {0} ceiling: max share {1} ({2}, seed {3}) {4} {5}{6}", day, Pct(worst), worstLayer, worstSeed, ok ? "<=" : ">",
                      Pct(t.ShareMax), ok ? "" : F(", {0} seed-layer(s) over", details.Count)), details));
            }

            var excluded = new HashSet<string>(t.ShareFloorExcluded ?? Array.Empty<string>(), StringComparer.Ordinal) { "Base" };
            var exempt = new HashSet<string>(t.ExemptFor(db.BuildTier), StringComparer.Ordinal);
            foreach (int day in t.ShareMinCheckDays.Where(d => d <= sw.Days))
            {
                foreach (var n in layers)
                {
                    if (excluded.Contains(n)) continue;
                    var below = new List<string>();
                    int active = 0;
                    double min = double.PositiveInfinity;
                    foreach (var r in sw.Runs)
                    {
                        double v = r.Rows[day - 1].Shares.Get(n);
                        if (!NonZero(v)) continue;   // a layer whose multiplier is 1 (not unlocked) is not floor-checked
                        active++;
                        if (v < min) min = v;
                        if (v < t.ShareMin) below.Add(F("seed {0}: {1}", r.Seed, Pct(v)));
                    }
                    if (active == 0)
                    {
                        if (exempt.Contains(n))
                            results.Add(new CheckResult("AC2", CheckStatus.Warn, F("day {0} floor {1}: multiplier 1 on every seed (exempt at {2})", day, n, db.BuildTier), None));
                        continue;
                    }
                    if (below.Count == 0)
                        results.Add(new CheckResult("AC2", CheckStatus.Pass, F("day {0} floor {1}: min share {2} >= {3}", day, n, Pct(min), Pct(t.ShareMin)), None));
                    else
                        results.Add(new CheckResult("AC2", exempt.Contains(n) ? CheckStatus.Warn : CheckStatus.Fail,
                            F("day {0} floor {1}: min share {2} < {3} on {4} of {5} seed(s){6}", day, n, Pct(min), Pct(t.ShareMin), below.Count, active,
                              exempt.Contains(n) ? F(" (exempt at {0})", db.BuildTier) : ""), below));
                }
            }
        }

        /// <summary>AC3: each seed's first wall (at the last day, 30 in a full sweep) within [firstWallMinStage, firstWallMaxStage].</summary>
        static void Ac3(SweepResult sw, AcceptanceTargets t, List<CheckResult> results)
        {
            var walls = sw.Runs.Select(r => (r.Seed, stage: r.Rows[sw.Days - 1].FirstWallStage)).ToList();
            var bad = walls.Where(w => w.stage < t.FirstWallMinStage || w.stage > t.FirstWallMaxStage)
                           .Select(w => F("seed {0}: first wall {1}", w.Seed, w.stage == 0 ? "never" : w.stage.ToString(Inv))).ToList();
            int lo = walls.Min(w => w.stage), hi = walls.Max(w => w.stage);
            results.Add(new CheckResult("AC3", bad.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
                F("day {0}: first wall stage {1}..{2} over seeds {3} {4}..{5}{6}", sw.Days, lo, hi, bad.Count == 0 ? "in" : "outside",
                  t.FirstWallMinStage, t.FirstWallMaxStage, bad.Count == 0 ? "" : F(" ({0} seed(s))", bad.Count)), bad));
        }

        /// <summary>AC4: walls opened by day 14 last at most maxWallActiveMinutesThroughDay14 active minutes; days 15-30 against lateWallMaxActiveMinutes (WARN while null).</summary>
        static void Ac4(SweepResult sw, AcceptanceTargets t, List<CheckResult> results)
        {
            var early = new List<(ulong seed, Wall w, double min)>();
            var late = new List<(ulong seed, Wall w, double min)>();
            foreach (var r in sw.Runs)
                foreach (var w in r.Walls)
                {
                    double m = w.ActiveMinutes(r.FinalTick, r.TickHz);
                    if (w.OpenedDay <= 14) early.Add((r.Seed, w, m));
                    else if (w.OpenedDay <= 30) late.Add((r.Seed, w, m));
                }

            string Desc((ulong seed, Wall w, double min) x) =>
                F("seed {0}: stage {1} opened day {2}, {3:0} active min{4}", x.seed, x.w.Stage, x.w.OpenedDay, x.min, x.w.IsOpen ? " (still open)" : "");

            var badEarly = early.Where(x => x.min > t.MaxWallActiveMinutesThroughDay14).OrderByDescending(x => x.min).ToList();
            double maxEarly = early.Count > 0 ? early.Max(x => x.min) : 0;
            results.Add(new CheckResult("AC4", badEarly.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
                F("days 1-14: longest wall {0:0} active min {1} {2:0} ({3} wall(s), {4} over)", maxEarly, badEarly.Count == 0 ? "<=" : ">",
                  t.MaxWallActiveMinutesThroughDay14, early.Count, badEarly.Count), badEarly.Select(Desc).ToList()));

            if (sw.Days < 15) return;
            double maxLate = late.Count > 0 ? late.Max(x => x.min) : 0;
            if (!t.LateWallMaxActiveMinutes.HasValue)
            {
                results.Add(new CheckResult("AC4", CheckStatus.Warn,
                    F("days 15-{0}: longest wall {1:0} active min; lateWallMaxActiveMinutes is null until power_curve_model.py sets it (issue #36)", Math.Min(30, sw.Days), maxLate),
                    late.OrderByDescending(x => x.min).Take(5).Select(Desc).ToList()));
                return;
            }
            var badLate = late.Where(x => x.min > t.LateWallMaxActiveMinutes.Value).OrderByDescending(x => x.min).ToList();
            results.Add(new CheckResult("AC4", badLate.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
                F("days 15-{0}: longest wall {1:0} active min {2} {3:0}", Math.Min(30, sw.Days), maxLate, badLate.Count == 0 ? "<=" : ">",
                  t.LateWallMaxActiveMinutes.Value), badLate.Select(Desc).ToList()));
        }

        /// <summary>
        /// AC11 (issue #43): every Awakening rank whose gate (highest cleared stage &gt;= its stage gate) is reached by the last
        /// day is bought (AwakenRank) within awakening_targets.max_days_after_gate days. SKIP when the tier has no awakening.
        /// </summary>
        static void Ac11(SweepResult sw, ContentDb db, List<CheckResult> results)
        {
            if (db.Awakening.Count == 0 || db.AwakeningTargets.Count == 0)
            {
                results.Add(new CheckResult("AC11", CheckStatus.Skip, F("no awakening rows at tier {0} (Awakening is MVP)", db.BuildTier), None));
                return;
            }
            var bad = new List<string>();
            int checkedRanks = 0;
            foreach (var r in sw.Runs)
                foreach (var a in db.Awakening)
                {
                    var target = db.AwakeningTargets.FirstOrDefault(x => x.Rank == a.Rank);
                    if (target == null) continue;
                    var gate = r.Rows.FirstOrDefault(row => row.HighestCleared >= a.StageGate);
                    if (gate == null) continue;
                    checkedRanks++;
                    var bought = r.Rows.FirstOrDefault(row => row.AwakenRank >= a.Rank);
                    int late = (bought?.Day ?? int.MaxValue) - gate.Day;
                    if (late > target.MaxDaysAfterGate)
                        bad.Add(F("seed {0}: rank {1} gate day {2}, bought {3}", r.Seed, a.Rank, gate.Day, bought == null ? "never" : "day " + bought.Day));
                }
            results.Add(new CheckResult("AC11", bad.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
                F("{0} rank gate(s) reached, {1} bought later than max_days_after_gate", checkedRanks, bad.Count), bad));
        }

        /// <summary>
        /// The acceptance table (10 section 3.6): per check day the medians over seeds of stage, gold earned, diamonds earned
        /// and spent, pulls, highest rarity, fish level and longest wall (wall-clock hours).
        /// </summary>
        public static string Table(SweepResult sw, string contentTag, IEnumerable<int> days)
        {
            var sb = new StringBuilder();
            sb.Append(F("# herocat acceptance table   bot={0} seeds={1} days={2} content={3}\n", sw.Bot, sw.SeedSpec, sw.Days, contentTag));
            const string fmt = "{0,3} {1,9} {2,14:0.0e+00} {3,13} {4,12} {5,6} {6,14} {7,11} {8,13:0.0}";
            sb.Append(F(fmt.Replace(":0.0e+00", "").Replace(":0.0", ""), "day", "stageMed", "goldEarnedMed", "diaEarnedMed", "diaSpentMed", "pulls",
                        "highestRarity", "fishLvlMed", "longestWallH")).Append('\n');
            foreach (int day in days.Where(d => d >= 1 && d <= sw.Days))
            {
                var rows = sw.Runs.Select(r => r.Rows[day - 1]).ToList();
                var rarity = Sweep.Median(rows.Select(r => r.HighestRarity.HasValue ? (int)r.HighestRarity.Value : -1));
                sb.Append(F(fmt, day, Sweep.Median(rows.Select(r => r.Stage)), Sweep.Median(rows.Select(r => r.GoldEarned)),
                            Sweep.Median(rows.Select(r => r.DiamondsEarned)), Sweep.Median(rows.Select(r => r.DiamondsSpent)),
                            Sweep.Median(rows.Select(r => r.Pulls)), rarity < 0 ? "-" : ((Rarity)rarity).ToString(),
                            Sweep.Median(rows.Select(r => r.FishMaxLevel)), Sweep.Median(rows.Select(r => r.LongestWallHours)))).Append('\n');
            }
            return sb.ToString();
        }
    }
}
