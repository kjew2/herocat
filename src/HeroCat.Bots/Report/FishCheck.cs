using System;
using System.Globalization;
using System.IO;
using System.Text;
using HeroCat.Bots.Measure;
using HeroCat.Bots.Policies;
using HeroCat.Sim.Content;
using Newtonsoft.Json.Linq;

namespace HeroCat.Bots.Report
{
    /// <summary>
    /// 05 section 15 AC14 (issue #105): the C# attentive Fish Board policy and tools/fish_model.py agree on day-7
    /// <c>fishMult</c> within <see cref="DefaultTolerance"/>. Runs <see cref="FishOnlyBot"/> for each policy through the
    /// normal harness (two 60-minute sessions a day, offline catch-up between them), prints the per-day fish table and
    /// compares with the model's FISHLINE values from its JSON output (<c>fish_model.py --out DIR</c> writes
    /// <c>DIR/fish_attentive.json</c>). The lazy comparison is printed, not gated (AC14 names the attentive policy).
    /// </summary>
    public static class FishCheck
    {
        public const double DefaultTolerance = 0.05;
        public const int Day = 7;

        public sealed class Outcome
        {
            public double Attentive, Lazy, Never;
            public double? ModelAttentive, ModelLazy;
            public string Report;
            public bool Ok;
        }

        public static FishDayStats[] Days(ContentDb db, FishPolicy policy, ulong seed, int days)
        {
            var run = new BotRunner(db).Run(new FishOnlyBot(policy), seed, days);
            var rows = new FishDayStats[run.Rows.Length];
            for (int i = 0; i < rows.Length; i++) rows[i] = run.Rows[i].Fish;
            return rows;
        }

        public static Outcome Run(ContentDb db, string modelJsonPath, ulong seed, double tolerance)
        {
            var inv = CultureInfo.InvariantCulture;
            var o = new Outcome();
            var sb = new StringBuilder();
            sb.Append("# fish-check: C# FishOnlyBot vs tools/fish_model.py (05 AC14), seed ").Append(seed).Append('\n');
            foreach (FishPolicy p in Enum.GetValues(typeof(FishPolicy)))
            {
                var rows = Days(db, p, seed, Day);
                sb.Append("policy ").Append(FishPolicies.Name(p)).Append('\n');
                sb.Append("day  released  merges  discards  board_dps%  missile%  fishMult  coins(disc/merge/discard)  highest\n");
                for (int i = 0; i < rows.Length; i++)
                {
                    var r = rows[i];
                    sb.Append(string.Format(inv, "{0,3}  {1,8}  {2,6}  {3,8}  {4,10:0.0}  {5,8:0.00}  x{6,7:0.00}  {7,9:N0} / {8,8:N1} / {9,6:N0}  {10,7}\n",
                                            i + 1, r.Released, r.Merges, r.Discards, r.BoardDpsPct, r.MissileDpsPct, r.FishMult,
                                            r.CoinsDiscovery, r.CoinsMerge, r.CoinsDiscard, r.HighestLevel));
                }
                double m = rows[Day - 1].FishMult;
                if (p == FishPolicy.Attentive) o.Attentive = m;
                else if (p == FishPolicy.Lazy) o.Lazy = m;
                else o.Never = m;
            }

            if (modelJsonPath == null || !File.Exists(modelJsonPath))
            {
                sb.Append($"FAIL model output {modelJsonPath ?? "(none)"} not found: run python3 tools/fish_model.py --quiet --out DIR first\n");
                o.Report = sb.ToString();
                return o;
            }
            var line = (JObject)JObject.Parse(File.ReadAllText(modelJsonPath))["fishline"];
            o.ModelAttentive = (double)line["attentive_day7"];
            o.ModelLazy = (double)line["lazy_day7"];
            double attDiff = o.Attentive / o.ModelAttentive.Value - 1, lazyDiff = o.Lazy / o.ModelLazy.Value - 1;
            o.Ok = Math.Abs(attDiff) <= tolerance;
            sb.Append(string.Format(inv, "{0}  attentive day-{1} fishMult: C# x{2:0.00} vs model x{3:0.00} ({4:+0.0%;-0.0%}, tolerance {5:0%})\n",
                                    o.Ok ? "PASS" : "FAIL", Day, o.Attentive, o.ModelAttentive, attDiff, tolerance));
            sb.Append(string.Format(inv, "info  lazy day-{0} fishMult: C# x{1:0.00} vs model x{2:0.00} ({3:+0.0%;-0.0%}); never x{4:0.00}\n",
                                    Day, o.Lazy, o.ModelLazy, lazyDiff, o.Never));
            o.Report = sb.ToString();
            return o;
        }
    }
}
