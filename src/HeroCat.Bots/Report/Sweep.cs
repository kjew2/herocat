using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HeroCat.Bots.Policies;
using HeroCat.Sim.Content;

namespace HeroCat.Bots.Report
{
    public sealed class SweepResult
    {
        public string Bot { get; set; }
        public string SeedSpec { get; set; }
        public int Days { get; set; }
        public DiamondMode Diamonds { get; set; }
        public RunResult[] Runs { get; set; }   // in seed-list order
    }

    /// <summary>Multi-seed runs and nearest-rank percentiles (10 section 3.7). Output is independent of the parallelism.</summary>
    public static class Sweep
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static T Percentile<T>(IReadOnlyList<T> sortedAscending, double p)
        {
            if (sortedAscending.Count == 0) throw new ArgumentException("empty");
            return sortedAscending[Math.Max(0, (int)Math.Ceiling(p * sortedAscending.Count) - 1)];
        }

        public static T Median<T>(IEnumerable<T> xs) => Percentile(xs.OrderBy(x => x).ToList(), 0.5);

        /// <summary>"1-20", "3,5,9", "7", or a mix ("1-3,9"). Seeds are positive; ranges must be ascending.</summary>
        public static ulong[] ParseSeeds(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) throw new FormatException("empty seed list");
            var list = new List<ulong>();
            foreach (var part in spec.Split(','))
            {
                var p = part.Trim();
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    ulong a = ParseOne(p.Substring(0, dash)), b = ParseOne(p.Substring(dash + 1));
                    if (b < a) throw new FormatException($"descending seed range '{p}'");
                    for (ulong s = a; s <= b; s++) list.Add(s);
                }
                else list.Add(ParseOne(p));
            }
            return list.ToArray();
        }

        static ulong ParseOne(string s)
        {
            if (!ulong.TryParse(s.Trim(), NumberStyles.None, Inv, out ulong v) || v == 0)
                throw new FormatException($"bad seed '{s}' (seeds are positive integers)");
            return v;
        }

        public static SweepResult Run(ContentDb content, string bot, string seedSpec, int days, int parallel,
                                      DiamondMode diamonds = DiamondMode.Content)
        {
            var seeds = ParseSeeds(seedSpec);
            BotPolicies.Create(bot);   // validate the name before spinning up workers
            var runs = new RunResult[seeds.Length];
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel) };
            Parallel.For(0, seeds.Length, opts, i =>
            {
                runs[i] = new BotRunner(content) { Diamonds = diamonds }.Run(BotPolicies.Create(bot), seeds[i], days);
            });
            return new SweepResult { Bot = bot, SeedSpec = seedSpec, Days = days, Diamonds = diamonds, Runs = runs };
        }

        public sealed record DayAggregate(int Day, int StageP10, int StageMed, int StageP90, int PartyLevelMed, double DpsMed,
                                          long DiamondsEarnedMed, int PullsMed, int FishLevelMed, double LongestWallHMed);

        public static IEnumerable<DayAggregate> Aggregate(SweepResult sw)
        {
            for (int d = 0; d < sw.Days; d++)
            {
                var rows = sw.Runs.Select(r => r.Rows[d]).ToList();
                var stage = rows.Select(r => r.Stage).OrderBy(x => x).ToList();
                yield return new DayAggregate(d + 1, Percentile(stage, 0.1), Percentile(stage, 0.5), Percentile(stage, 0.9),
                                              Median(rows.Select(r => r.PartyLevel)), Median(rows.Select(r => r.Dps)),
                                              Median(rows.Select(r => r.DiamondsEarned)), Median(rows.Select(r => r.Pulls)),
                                              Median(rows.Select(r => r.FishMaxLevel)), Median(rows.Select(r => r.LongestWallHours)));
            }
        }

        const string Fmt = "{0,3} {1,8} {2,8} {3,8} {4,8} {5,10:0.00e+00} {6,8} {7,6} {8,7} {9,10:0.0}";

        public static string Table(SweepResult sw, string contentTag)
        {
            var sb = new StringBuilder();
            sb.Append("# herocat sweep v3\n");
            sb.Append(string.Format(Inv, "# bot={0} seeds={1} days={2} diamonds={3} content={4}\n", sw.Bot, sw.SeedSpec, sw.Days,
                                    sw.Diamonds.ToString().ToLowerInvariant(), contentTag));
            sb.Append(string.Format(Inv, Fmt.Replace(":0.00e+00", "").Replace(":0.0", ""), "day", "stageP10", "stageMed", "stageP90", "partyLMed",
                                    "dpsMed", "diaEarn", "pulls", "fishLvl", "longWallH")).Append('\n');
            foreach (var a in Aggregate(sw))
                sb.Append(string.Format(Inv, Fmt, a.Day, a.StageP10, a.StageMed, a.StageP90, a.PartyLevelMed, a.DpsMed, a.DiamondsEarnedMed,
                                        a.PullsMed, a.FishLevelMed, a.LongestWallHMed)).Append('\n');
            return sb.ToString();
        }

        public static string Csv(SweepResult sw)
        {
            var sb = new StringBuilder("day,stageP10,stageMed,stageP90,heroLevelMed,dpsMed,diamondsEarnedMed,pullsMed,fishLevelMed,longestWallHMed\n");
            foreach (var a in Aggregate(sw))
                sb.Append(string.Format(Inv, "{0},{1},{2},{3},{4},{5:R},{6},{7},{8},{9:R}\n", a.Day, a.StageP10, a.StageMed, a.StageP90,
                                        a.PartyLevelMed, a.DpsMed, a.DiamondsEarnedMed, a.PullsMed, a.FishLevelMed, a.LongestWallHMed));
            return sb.ToString();
        }
    }
}
