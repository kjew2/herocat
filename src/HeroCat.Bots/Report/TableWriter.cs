using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using HeroCat.Bots.Measure;

namespace HeroCat.Bots.Report
{
    /// <summary>Table, CSV and JSON output (10 section 3.7). Invariant culture, '\n', no trailing whitespace.</summary>
    public static class TableWriter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string RowFormat = "{0,3} {1,5} {2,7} {3,5} {4,10:0.00e+00} {5,8} {6,10:0.00e+00} {7,6} {8,8}";

        static readonly string[] Columns = { "day", "stage", "highest", "heroL", "gold", "diamonds", "fishCoin", "pulls", "hash" };

        public static string ShortHash(string hash) => string.IsNullOrEmpty(hash) ? "-" : hash.Substring(0, Math.Min(8, hash.Length));

        public static string ContentTag(string version, string hash) => $"{version}/{ShortHash(hash)}";

        public static string Header() =>
            string.Format(Inv, RowFormat.Replace(":0.00e+00", ""), Columns.Cast<object>().ToArray());

        public static string FormatRow(DayRow r) =>
            string.Format(Inv, RowFormat, r.Day, r.Stage, r.HighestCleared, r.HeroLevel, r.Gold, r.Diamonds, r.FishCoin, r.Pulls,
                          ShortHash(r.StateHash));

        public static IEnumerable<DayRow> SelectRows(RunResult run, bool allDays) =>
            allDays ? run.Rows : run.Rows.Where(r => Array.IndexOf(HarnessConstants.SummaryDays, r.Day) >= 0 || r.Day == run.Days);

        public static string Table(RunResult run, bool allDays)
        {
            var sb = new StringBuilder();
            sb.Append("# herocat run v2\n");
            sb.Append(string.Format(Inv, "# bot={0} seed={1} days={2} content={3}\n", run.Bot, run.Seed, run.Days,
                                    ContentTag(run.ContentVersion, run.ContentHash)));
            sb.Append(Header()).Append('\n');
            foreach (var r in SelectRows(run, allDays)) sb.Append(FormatRow(r)).Append('\n');
            return sb.ToString();
        }

        public static string Csv(RunResult run, bool allDays)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", Columns)).Append('\n');
            foreach (var r in SelectRows(run, allDays))
                sb.Append(string.Format(Inv, "{0},{1},{2},{3},{4:R},{5},{6:R},{7},{8}\n", r.Day, r.Stage, r.HighestCleared, r.HeroLevel,
                                        r.Gold, r.Diamonds, r.FishCoin, r.Pulls, r.StateHash));
            return sb.ToString();
        }

        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        public static string Json(object runOrRuns) => JsonSerializer.Serialize(runOrRuns, JsonOpts).Replace("\r\n", "\n") + "\n";
    }
}
