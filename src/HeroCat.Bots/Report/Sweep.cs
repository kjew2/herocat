using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HeroCat.Bots.Measure;
using HeroCat.Bots.Policies;
using HeroCat.Sim.Content;

namespace HeroCat.Bots.Report
{
    public sealed class SweepResult
    {
        public string Bot { get; set; }
        public string SeedSpec { get; set; }
        public int Days { get; set; }
        public RunResult[] Runs { get; set; }   // in seed-list order
    }

    /// <summary>Multi-seed runs and nearest-rank percentiles (specs/09 section 8).</summary>
    public static class Sweep
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static T Percentile<T>(IReadOnlyList<T> sortedAscending, double p)
        {
            if (sortedAscending.Count == 0) throw new ArgumentException("empty");
            return sortedAscending[Math.Max(0, (int)Math.Ceiling(p * sortedAscending.Count) - 1)];
        }

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

        public static SweepResult Run(ContentDb content, string bot, string seedSpec, int days, int parallel)
        {
            var seeds = ParseSeeds(seedSpec);
            var runs = new RunResult[seeds.Length];
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel) };
            Parallel.For(0, seeds.Length, opts, i =>
            {
                runs[i] = new BotRunner(content).Run(BotPolicies.Create(bot), seeds[i], days);
            });
            return new SweepResult { Bot = bot, SeedSpec = seedSpec, Days = days, Runs = runs };
        }

        const string Fmt =
            "{0,3} {1,8} {2,8} {3,8} {4,8:0.00e+00} {5,10} {6,11:0.00e+00} {7,11:0.00} {8,12:0.00} {9,8} {10,5} {11,12} {12,12} {13,15:0.00}";

        static readonly object[] Cols =
        {
            "day", "stageP10", "stageMed", "stageP90", "dpsMed", "statLvlMed", "gearMultMed", "heroMultMed", "classMultMed",
            "pullsMed", "L+Med", "firstWallMin", "firstWallMax", "longestWallHMax",
        };

        public static string Table(SweepResult sw, string contentTag)
        {
            var sb = new StringBuilder();
            sb.Append("# herocat sweep v1\n");
            sb.Append(string.Format(Inv, "# bot={0} seeds={1} days={2} content={3}\n", sw.Bot, sw.SeedSpec, sw.Days, contentTag));
            sb.Append(string.Format(Inv, Fmt.Replace(":0.00e+00", "").Replace(":0.00", ""), Cols)).Append('\n');
            foreach (var a in Aggregate(sw))
            {
                sb.Append(string.Format(Inv, Fmt, a.Day, a.StageP10, a.StageMed, a.StageP90, a.DpsMed, a.StatLvlMed, a.GearMultMed,
                    a.HeroMultMed, a.ClassMultMed, a.PullsMed, a.LPlusMed,
                    a.FirstWallMin == 0 ? "-" : a.FirstWallMin.ToString(Inv), a.FirstWallMax == 0 ? "-" : a.FirstWallMax.ToString(Inv),
                    a.LongestWallHMax)).Append('\n');
            }
            return sb.ToString();
        }

        public sealed record DayAggregate(int Day, int StageP10, int StageMed, int StageP90, double DpsMed, int StatLvlMed,
            double GearMultMed, double HeroMultMed, double ClassMultMed, long PullsMed, int LPlusMed, int FirstWallMin,
            int FirstWallMax, double LongestWallHMax);

        public static IEnumerable<DayAggregate> Aggregate(SweepResult sw)
        {
            for (int d = 0; d < sw.Days; d++)
            {
                var rows = sw.Runs.Select(r => r.Rows[d]).ToList();
                List<T> Sorted<T>(Func<DayRow, T> f) => rows.Select(f).OrderBy(x => x).ToList();
                var stage = Sorted(r => r.Stage);
                var walls = rows.Where(r => r.FirstWallStage > 0).Select(r => r.FirstWallStage).ToList();
                yield return new DayAggregate(
                    d + 1, Percentile(stage, 0.1), Percentile(stage, 0.5), Percentile(stage, 0.9),
                    Percentile(Sorted(r => r.Dps), 0.5), Percentile(Sorted(r => r.StatLvl), 0.5),
                    Percentile(Sorted(r => r.GearMult), 0.5), Percentile(Sorted(r => r.HeroMult), 0.5),
                    Percentile(Sorted(r => r.ClassMult), 0.5), Percentile(Sorted(r => r.Pulls), 0.5),
                    Percentile(Sorted(r => r.LPlus), 0.5),
                    walls.Count == 0 ? 0 : walls.Min(), walls.Count == 0 ? 0 : walls.Max(),
                    rows.Max(r => r.LongestWallHours));
            }
        }

        public static string Csv(SweepResult sw)
        {
            var sb = new StringBuilder();
            sb.Append("day,stageP10,stageMed,stageP90,dpsMed,statLvlMed,gearMultMed,heroMultMed,classMultMed,pullsMed,lPlusMed,firstWallMin,firstWallMax,longestWallHMax\n");
            foreach (var a in Aggregate(sw))
                sb.Append(string.Format(Inv, "{0},{1},{2},{3},{4:0.00e+00},{5},{6:0.00e+00},{7:0.00},{8:0.00},{9},{10},{11},{12},{13:0.00}\n",
                    a.Day, a.StageP10, a.StageMed, a.StageP90, a.DpsMed, a.StatLvlMed, a.GearMultMed, a.HeroMultMed, a.ClassMultMed,
                    a.PullsMed, a.LPlusMed, a.FirstWallMin, a.FirstWallMax, a.LongestWallHMax));
            return sb.ToString();
        }
    }
}
