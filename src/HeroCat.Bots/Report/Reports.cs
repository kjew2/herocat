using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HeroCat.Bots.Measure;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

namespace HeroCat.Bots.Report
{
    /// <summary>The report verbs and <c>compare --bots</c> (10 section 3.7).</summary>
    public static class Reports
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly int[] LayerShareDays = { 1, 3, 7, 14, 30 };

        static string F(string fmt, params object[] args) => string.Format(Inv, fmt, args);

        static string Head(string what, SweepResult sw, string contentTag) =>
            F("# herocat report {0}   bot={1} seeds={2} days={3} content={4}\n", what, sw.Bot, sw.SeedSpec, sw.Days, contentTag);

        /// <summary>Per seed, the five longest walls by active minutes, with the layer that unblocked each.</summary>
        public static string LongestWalls(SweepResult sw, string contentTag)
        {
            var sb = new StringBuilder(Head("--longest-walls", sw, contentTag));
            const string fmt = "{0,5} {1,6} {2,6} {3,7} {4,8:0.0} {5,10:0.0} {6,5} {7}";
            sb.Append(F(fmt.Replace(":0.0", ""), "seed", "stage", "opened", "closed", "wallH", "activeMin", "fails", "unblockedBy")).Append('\n');
            foreach (var r in sw.Runs)
            {
                var top = r.Walls.Select(w => (w, min: w.ActiveMinutes(r.FinalTick, r.TickHz)))
                                 .OrderByDescending(x => x.min).ThenBy(x => x.w.OpenTick).Take(5);
                foreach (var (w, min) in top)
                    sb.Append(F(fmt, r.Seed, w.Stage, w.OpenedDay, w.IsOpen ? "open" : w.ClosedDay.ToString(Inv),
                                w.WallClockHours(r.FinalServerSec), min, w.Fails, w.IsOpen ? "-" : w.UnblockedBy)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The median over seeds of each layer share on <paramref name="days"/>; '^' marks a share over the ceiling, 'v' one under the floor.</summary>
        public static string LayerSharesReport(SweepResult sw, string contentTag, double shareMax, double shareMin)
        {
            var sb = new StringBuilder(Head("--layer-shares", sw, contentTag));
            sb.Append(F("# median over seeds; ^ above {0:0}% (ceiling), v below {1:0}% (floor, non-zero layers)\n", 100 * shareMax, 100 * shareMin));
            sb.Append(TableWriter.SharesHeader()).Append('\n');
            foreach (int day in LayerShareDays.Where(d => d <= sw.Days))
                sb.Append(TableWriter.SharesRow(day, MedianShares(sw, day), (n, v) =>
                    n == "Base" ? " " : v > shareMax ? "^" : Math.Abs(v) > 1e-12 && v < shareMin ? "v" : " ")).Append('\n');
            return sb.ToString();
        }

        /// <summary>The per-layer median share over seeds on <paramref name="day"/> (the same per-seed numbers AC2 reads).</summary>
        public static LayerShares MedianShares(SweepResult sw, int day)
        {
            double M(string n) => Sweep.Median(sw.Runs.Select(r => r.Rows[day - 1].Shares.Get(n)));
            return new LayerShares(M("Training"), M("HeroLevel"), M("Weapons"), M("Skills"), M("Companions"), M("FishBoard"), M("Research"),
                                   M("Awakening"), M("Crit"), M("Speed"), M("Base"));
        }

        /// <summary>Diamonds earned by reason per day, median over seeds; the per-reason totals of one run sum to its DiamondsEarned.</summary>
        public static string Diamonds(SweepResult sw, string contentTag)
        {
            var reasons = sw.Runs.SelectMany(r => r.DiamondsByReason.Keys.Select(k => k.reason)).Distinct().OrderBy(x => x).ToList();
            var sb = new StringBuilder(Head("--diamonds", sw, contentTag));
            sb.Append("# diamonds earned per day by reason, median over seeds\n");
            sb.Append("day");
            foreach (var re in reasons) sb.Append(' ').Append(re.ToString().PadLeft(Math.Max(8, re.ToString().Length)));
            sb.Append(' ').Append("total".PadLeft(8)).Append('\n');
            for (int day = 1; day <= sw.Days; day++)
            {
                sb.Append(F("{0,3}", day));
                foreach (var re in reasons)
                    sb.Append(' ').Append(Sweep.Median(sw.Runs.Select(r => Get(r, day, re))).ToString(Inv).PadLeft(Math.Max(8, re.ToString().Length)));
                sb.Append(' ').Append(Sweep.Median(sw.Runs.Select(r => DayTotal(r, day))).ToString(Inv).PadLeft(8)).Append('\n');
            }
            return sb.ToString();
        }

        static long Get(RunResult r, int day, CurrencyReason re) => r.DiamondsByReason.TryGetValue((day, re), out long v) ? v : 0;

        /// <summary>Diamonds earned on <paramref name="day"/> over every reason.</summary>
        public static long DayTotal(RunResult r, int day) => r.DiamondsByReason.Where(kv => kv.Key.day == day).Sum(kv => kv.Value);

        /// <summary><c>compare --bots a,b,c</c>: median stage per day per bot, and each bot's gap to the first.</summary>
        public static string Compare(IReadOnlyList<SweepResult> sweeps, string contentTag)
        {
            if (sweeps.Count == 0) throw new ArgumentException("no bots");
            var sb = new StringBuilder();
            sb.Append(F("# herocat compare   bots={0} seeds={1} days={2} content={3}\n", string.Join(",", sweeps.Select(s => s.Bot)), sweeps[0].SeedSpec,
                        sweeps[0].Days, contentTag));
            sb.Append("day");
            foreach (var s in sweeps) sb.Append(' ').Append(s.Bot.PadLeft(Math.Max(10, s.Bot.Length)));
            foreach (var s in sweeps.Skip(1)) sb.Append(' ').Append(("gap:" + s.Bot).PadLeft(Math.Max(10, s.Bot.Length + 4)));
            sb.Append('\n');
            for (int day = 1; day <= sweeps[0].Days; day++)
            {
                var med = sweeps.Select(s => Sweep.Median(s.Runs.Select(r => r.Rows[day - 1].Stage))).ToList();
                sb.Append(F("{0,3}", day));
                for (int i = 0; i < sweeps.Count; i++) sb.Append(' ').Append(med[i].ToString(Inv).PadLeft(Math.Max(10, sweeps[i].Bot.Length)));
                for (int i = 1; i < sweeps.Count; i++)
                    sb.Append(' ').Append((med[i] - med[0]).ToString("+0;-0;0", Inv).PadLeft(Math.Max(10, sweeps[i].Bot.Length + 4)));
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
