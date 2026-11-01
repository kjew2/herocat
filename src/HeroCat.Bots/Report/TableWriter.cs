using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using HeroCat.Bots.Measure;

namespace HeroCat.Bots.Report
{
    /// <summary>Table, CSV and JSON output (specs/09 section 7). Invariant culture, '\n', no trailing whitespace.</summary>
    public static class TableWriter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string RowFormat =
            "{0,3} {1,5} {2,8:0.00e+00} {3,7} {4,8:0.00} {5,10:0.00e+00} {6,9:0.00} {7,9:0.00} {8,5} {9,3} {10,9} {11,12:0.00}";

        const string SharesFormat = " {0,6:0.000} {1,6:0.000} {2,6:0.000} {3,6:0.000}";

        static readonly string[] Columns =
            { "day", "stage", "dps", "statLvl", "statMult", "gearMult", "heroMult", "classMult", "pulls", "L+", "firstWall", "longestWallH" };

        static readonly string[] CsvColumns =
            { "day", "stage", "dps", "statLvl", "statMult", "gearMult", "heroMult", "classMult", "pulls", "lPlus", "firstWallStage", "longestWallH" };

        public static string ShortHash(string hash) => string.IsNullOrEmpty(hash) ? "-" : hash.Substring(0, Math.Min(8, hash.Length));

        public static string ContentTag(string version, string hash) => $"{version}/{ShortHash(hash)}";

        public static string Header() =>
            string.Format(Inv, RowFormat.Replace(":0.00e+00", "").Replace(":0.00", ""), Columns.Cast<object>().ToArray());

        public static string FormatRow(DayRow r) =>
            string.Format(Inv, RowFormat, r.Day, r.Stage, r.Dps, r.StatLvl, r.StatMult, r.GearMult, r.HeroMult, r.ClassMult,
                          r.Pulls, r.LPlus, r.FirstWallStage == 0 ? "-" : r.FirstWallStage.ToString(Inv), r.LongestWallHours);

        public static IEnumerable<DayRow> SelectRows(RunResult run, bool allDays) =>
            allDays ? run.Rows : run.Rows.Where(r => Array.IndexOf(HarnessConstants.SummaryDays, r.Day) >= 0 || r.Day == run.Days);

        public static string Table(RunResult run, bool allDays, bool shares = false)
        {
            var sb = new StringBuilder();
            sb.Append("# herocat run v1\n");
            sb.Append(string.Format(Inv, "# bot={0} seed={1} days={2} content={3}\n", run.Bot, run.Seed, run.Days,
                                    ContentTag(run.ContentVersion, run.ContentHash)));
            sb.Append(Header());
            if (shares) sb.Append(string.Format(Inv, " {0,6} {1,6} {2,6} {3,6}", "shStat", "shGear", "shHero", "shCls"));
            sb.Append('\n');
            foreach (var r in SelectRows(run, allDays))
            {
                sb.Append(FormatRow(r));
                if (shares)
                {
                    var s = LayerShares.Of(r, run.BaseAtk);
                    sb.Append(string.Format(Inv, SharesFormat, s.Stat, s.Gear, s.Hero, s.Class));
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static string Csv(RunResult run, bool allDays)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", CsvColumns)).Append('\n');
            foreach (var r in SelectRows(run, allDays))
            {
                sb.Append(string.Format(Inv, "{0},{1},{2:0.00e+00},{3},{4:0.00},{5:0.00e+00},{6:0.00},{7:0.00},{8},{9},{10},{11:0.00}",
                    r.Day, r.Stage, r.Dps, r.StatLvl, r.StatMult, r.GearMult, r.HeroMult, r.ClassMult, r.Pulls, r.LPlus,
                    r.FirstWallStage, r.LongestWallHours));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        /// <summary>System.Text.Json writes doubles in shortest round-trip form.</summary>
        public static string Json(object runOrRuns) => JsonSerializer.Serialize(runOrRuns, JsonOpts).Replace("\r\n", "\n") + "\n";
    }
}
