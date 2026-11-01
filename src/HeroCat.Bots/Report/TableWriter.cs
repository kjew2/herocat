using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using HeroCat.Bots.Measure;
using HeroCat.Sim.Content;

namespace HeroCat.Bots.Report
{
    /// <summary>Table, CSV and JSON output of a run (10 sections 3.5, 3.7). Invariant culture, '\n', no trailing whitespace.</summary>
    public static class TableWriter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string RowFormat =
            "{0,3} {1,5} {2,7} {3,5} {4,10:0.00e+00} {5,10:0.00e+00} {6,7} {7,8} {8,5} {9,5} {10,9} {11,7} {12,10:0.00e+00} {13,5} {14,9:0.0} {15,8}";

        static readonly string[] Columns =
            { "day", "stage", "highest", "heroL", "dps", "gold", "diaEarn", "diaSpent", "pulls", "lplus", "rarity", "fishLvl", "fishCoin",
              "wall1", "longWallH", "hash" };

        public static string ShortHash(string hash) => string.IsNullOrEmpty(hash) ? "-" : hash.Substring(0, Math.Min(8, hash.Length));

        public static string ContentTag(string version, string hash) => $"{version}/{ShortHash(hash)}";

        public static string RarityName(Rarity? r) => r.HasValue ? r.Value.ToString() : "-";

        public static string Header() =>
            string.Format(Inv, RowFormat.Replace(":0.00e+00", "").Replace(":0.0", ""), Columns.Cast<object>().ToArray());

        public static string FormatRow(DayRow r) =>
            string.Format(Inv, RowFormat, r.Day, r.Stage, r.HighestCleared, r.HeroLevel, r.Dps, r.Gold, r.DiamondsEarned, r.DiamondsSpent,
                          r.Pulls, r.LPlus, RarityName(r.HighestRarity), r.FishMaxLevel, r.FishCoins, r.FirstWallStage, r.LongestWallHours,
                          ShortHash(r.StateHash));

        public static IEnumerable<DayRow> SelectRows(RunResult run, bool allDays) =>
            allDays ? run.Rows : run.Rows.Where(r => Array.IndexOf(HarnessConstants.SummaryDays, r.Day) >= 0 || r.Day == run.Days);

        public static string RunHeader(RunResult run) =>
            string.Format(Inv, "# bot={0} seed={1} days={2} diamonds={3} content={4}\n", run.Bot, run.Seed, run.Days,
                          run.Diamonds.ToString().ToLowerInvariant(), ContentTag(run.ContentVersion, run.ContentHash));

        public static string Table(RunResult run, bool allDays)
        {
            var sb = new StringBuilder();
            sb.Append("# herocat run v3\n");
            sb.Append(RunHeader(run));
            sb.Append(Header()).Append('\n');
            foreach (var r in SelectRows(run, allDays)) sb.Append(FormatRow(r)).Append('\n');
            sb.Append("# layer shares (ln factor / ln(DPS / BASE_ATK))\n");
            sb.Append(SharesHeader()).Append('\n');
            foreach (var r in SelectRows(run, allDays)) sb.Append(SharesRow(r.Day, r.Shares)).Append('\n');
            return sb.ToString();
        }

        public static string SharesHeader()
        {
            var sb = new StringBuilder("day");
            foreach (var n in LayerShares.Names) sb.Append(' ').Append(n.PadLeft(Math.Max(6, n.Length)));
            return sb.ToString();
        }

        public static string SharesRow(int day, LayerShares s, Func<string, double, string> mark = null)
        {
            var sb = new StringBuilder(string.Format(Inv, "{0,3}", day));
            foreach (var (n, v) in s.All())
            {
                string cell = string.Format(Inv, "{0:0.0}%", 100 * v) + (mark != null ? mark(n, v) : "");
                sb.Append(' ').Append(cell.PadLeft(Math.Max(6, n.Length)));
            }
            return sb.ToString();
        }

        public static string Csv(RunResult run, bool allDays)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", Columns)).Append(',').Append(string.Join(",", LayerShares.Names.Select(n => "share" + n))).Append('\n');
            foreach (var r in SelectRows(run, allDays))
            {
                sb.Append(string.Format(Inv, "{0},{1},{2},{3},{4:R},{5:R},{6},{7},{8},{9},{10},{11},{12:R},{13},{14:R},{15}", r.Day, r.Stage,
                                        r.HighestCleared, r.HeroLevel, r.Dps, r.Gold, r.DiamondsEarned, r.DiamondsSpent, r.Pulls, r.LPlus,
                                        RarityName(r.HighestRarity), r.FishMaxLevel, r.FishCoins, r.FirstWallStage, r.LongestWallHours, r.StateHash));
                foreach (var (_, v) in r.Shares.All()) sb.Append(',').Append(v.ToString("R", Inv));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };

        /// <summary>A run's day rows and walls as JSON (the wall list and spend log are included; dictionaries are not).</summary>
        public static string Json(RunResult run) =>
            JsonSerializer.Serialize(new
            {
                run.Bot, run.Seed, run.Days, Diamonds = run.Diamonds.ToString(), run.ContentVersion, run.ContentHash, run.Rows,
                Walls = run.Walls, run.Spend, run.FinalTick, run.FinalHash,
            }, JsonOpts).Replace("\r\n", "\n") + "\n";

        public static string Json(IEnumerable<RunResult> runs) => "[\n" + string.Join(",\n", runs.Select(r => Json(r).TrimEnd('\n'))) + "\n]\n";
    }
}
