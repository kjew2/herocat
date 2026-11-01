using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HeroCat.Bots.Acceptance;
using HeroCat.Bots.Golden;
using HeroCat.Bots.Policies;
using HeroCat.Bots.Report;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Bots
{
    /// <summary>Hand-rolled "--key value" / "--flag" parser.</summary>
    public sealed class CliArgs
    {
        readonly Dictionary<string, string> _opts = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Verb { get; }

        public CliArgs(string[] args)
        {
            int i = 0;
            Verb = args.Length > 0 && !args[0].StartsWith("--") ? args[i++] : "run";
            for (; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--")) throw new ArgumentException($"unexpected argument '{a}'");
                string key = a.Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) _opts[key] = args[++i];
                else _opts[key] = "";
            }
        }

        public bool Has(string k) => _opts.ContainsKey(k);
        public string Str(string k, string def) => _opts.TryGetValue(k, out var v) && v != "" ? v : def;
        public int Int(string k, int def) => _opts.TryGetValue(k, out var v) && v != "" ? int.Parse(v, CultureInfo.InvariantCulture) : def;
        public long Long(string k, long def) => _opts.TryGetValue(k, out var v) && v != "" ? long.Parse(v, CultureInfo.InvariantCulture) : def;
    }

    public static class Program
    {
        const string Usage =
            "usage: HeroCat.Bots <run|sweep|compare|acceptance|golden|gacha-vectors|bench> [options]\n" +
            "  run        --bot greedy-f2p|whale --seed N --days N --all-days --shares --format table|csv|json --out path --content dir\n" +
            "  sweep      --bot B --seeds 1-20 --days N --format table|csv|json --out path --parallel N --content dir\n" +
            "  compare    --seeds 1-20 --days N --content dir\n" +
            "  acceptance --seeds 1-20 --days N --time-budget-s 180 --targets path --verbose --content dir\n" +
            "  golden     --check|--update --golden-dir path --content dir\n" +
            "  gacha-vectors --check|--update --out path --content dir\n" +
            "  bench      --ticks N --stage S --max-ms N\n";

        public static int Main(string[] argv)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var a = new CliArgs(argv);
                switch (a.Verb)
                {
                    case "run": return Run(a);
                    case "sweep": return SweepCmd(a);
                    case "compare": return Compare(a);
                    case "acceptance": return AcceptanceCmd(a);
                    case "golden": return GoldenCmd(a);
                    case "gacha-vectors": return GachaVectorsCmd(a);
                    case "bench": return Bench(a);
                    case "help": case "--help": Console.Out.Write(Usage); return 0;
                    default: Console.Error.Write($"unknown verb '{a.Verb}'\n" + Usage); return 2;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException)
            {
                Console.Error.Write(e.Message + "\n" + Usage);
                return 2;
            }
        }

        public static string ContentDir(CliArgs a) =>
            a.Str("content", null) ?? FileContentSource.FindContentDirectory(AppContext.BaseDirectory)
            ?? FileContentSource.FindContentDirectory(Directory.GetCurrentDirectory())
            ?? throw new ArgumentException("content/manifest.json not found; pass --content <dir>");

        static ContentDb LoadContent(CliArgs a)
        {
            var db = ContentDb.LoadFromDirectory(ContentDir(a));
            Console.Error.Write($"# content {db.Version} hash {db.ContentHash} sim {typeof(Simulation).Assembly.GetName().Version}\n");
            return db;
        }

        static void Emit(CliArgs a, string text)
        {
            string path = a.Str("out", null);
            if (path == null) { Console.Out.Write(text); return; }
            File.WriteAllText(path, text, new UTF8Encoding(false));
            Console.Error.Write($"# wrote {path}\n");
        }

        static int Run(CliArgs a)
        {
            var db = LoadContent(a);
            var runner = new BotRunner(db);
            if (a.Has("walls"))
                runner.AfterDay = (day, ctx) =>
                {
                    var l = ctx.State.Lifetime;
                    Console.Error.Write(string.Format(CultureInfo.InvariantCulture,
                        "# day {0,2}: kills {1} drops {2} bossKills {3} bossFails {4} enh {5}/{6} gold {7:0.00e+00} offline {8:0.00e+00} gems {9}\n",
                        day, l.Kills, l.GearDrops, l.BossKills, l.BossFails, l.EnhanceSuccesses, l.EnhanceAttempts, l.GoldEarned,
                        l.OfflineGoldEarned, l.GemsEarned));
                };
            var run = runner.Run(BotPolicies.Create(a.Str("bot", "greedy-f2p")),
                                            (ulong)a.Long("seed", (long)HarnessConstants.DefaultSeed), a.Int("days", HarnessConstants.DefaultDays));
            string fmt = a.Str("format", "table");
            bool all = a.Has("all-days");
            Emit(a, fmt == "csv" ? TableWriter.Csv(run, all) : fmt == "json" ? TableWriter.Json(run) : TableWriter.Table(run, all, a.Has("shares")));
            if (a.Has("walls"))
            {
                long end = run.DayEndUtcMs[run.Days - 1];
                foreach (var w in run.Walls)
                    Console.Error.Write(string.Format(CultureInfo.InvariantCulture, "# wall stage {0,4} day {1,2} {2,8:0.00} h wall-clock {3,6:0.00} h active{4}\n",
                        w.Stage, w.OpenDay, w.WallClockHours(end), w.ActiveHours(w.IsOpen ? w.OpenTick : w.CloseTick, db.Constants.TickHz),
                        w.IsOpen ? " (open)" : ""));
            }
            return 0;
        }

        static int SweepCmd(CliArgs a)
        {
            var db = LoadContent(a);
            var sw = Sweep.Run(db, a.Str("bot", "greedy-f2p"), a.Str("seeds", HarnessConstants.DefaultSeeds),
                               a.Int("days", HarnessConstants.DefaultDays), a.Int("parallel", Environment.ProcessorCount));
            string fmt = a.Str("format", "table");
            Emit(a, fmt == "csv" ? Sweep.Csv(sw) : fmt == "json" ? TableWriter.Json(sw.Runs) : Sweep.Table(sw, TableWriter.ContentTag(db.Version, db.ContentHash)));
            return 0;
        }

        static int Compare(CliArgs a)
        {
            var db = LoadContent(a);
            string seeds = a.Str("seeds", HarnessConstants.DefaultSeeds);
            int days = a.Int("days", HarnessConstants.DefaultDays);
            var f2p = Sweep.Aggregate(Sweep.Run(db, "greedy-f2p", seeds, days, Environment.ProcessorCount)).ToList();
            var whale = Sweep.Aggregate(Sweep.Run(db, "whale", seeds, days, Environment.ProcessorCount)).ToList();
            var sb = new StringBuilder();
            sb.Append("# herocat compare v1\n");
            sb.Append($"# seeds={seeds} days={days} content={TableWriter.ContentTag(db.Version, db.ContentHash)}\n");
            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,3} {1,8} {2,8} {3,5} {4,9} {5,9}\n", "day", "f2pMed", "whaleMed", "gap", "f2pHero", "whaleHero"));
            for (int i = 0; i < days; i++)
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,3} {1,8} {2,8} {3,5} {4,9:0.00} {5,9:0.00}\n", i + 1, f2p[i].StageMed,
                                        whale[i].StageMed, whale[i].StageMed - f2p[i].StageMed, f2p[i].HeroMultMed, whale[i].HeroMultMed));
            Console.Out.Write(sb.ToString());
            return 0;
        }

        /// <summary>Null if seed 7 twice is identical and (when the golden dir exists) the run golden matches.</summary>
        public static string DeterminismCheck(ContentDb db, string goldenDirOrNull)
        {
            var e = GoldenFiles.All[0];
            string a = e.Generate(db), b = e.Generate(db);
            if (a != b)
            {
                var la = a.Split('\n'); var lb = b.Split('\n');
                for (int i = 0; i < Math.Min(la.Length, lb.Length); i++)
                    if (la[i] != lb[i]) return $"line {i + 1}: '{la[i]}' vs '{lb[i]}'";
                return "lengths differ";
            }
            if (goldenDirOrNull != null && Directory.Exists(goldenDirOrNull))
                return GoldenFiles.Compare(goldenDirOrNull, e, a);
            return null;
        }

        static int AcceptanceCmd(CliArgs a)
        {
            var sw0 = Stopwatch.StartNew();
            var db = LoadContent(a);
            var targets = AcceptanceTargets.Load(a.Str("targets", null));
            string seeds = a.Str("seeds", HarnessConstants.DefaultSeeds);
            int days = a.Int("days", HarnessConstants.DefaultDays);
            var sw = Sweep.Run(db, "greedy-f2p", seeds, days, Environment.ProcessorCount);
            string goldenDir = a.Str("golden-dir", GoldenFiles.DefaultDir(ContentDir(a)));
            var results = AcceptanceChecker.Check(sw, targets, () => DeterminismCheck(db, goldenDir));
            double secs = sw0.Elapsed.TotalSeconds;
            int budget = a.Int("time-budget-s", 180);

            var sb = new StringBuilder();
            sb.Append("# herocat acceptance v1\n");
            sb.Append($"# bot=greedy-f2p seeds={seeds} days={days} content={TableWriter.ContentTag(db.Version, db.ContentHash)}\n");
            foreach (var r in results)
            {
                sb.Append(r.Pass ? "PASS " : "FAIL ").Append(r.Id).Append(' ').Append(r.Message).Append('\n');
                if (a.Has("verbose")) foreach (var d in r.Details) sb.Append("     ").Append(d).Append('\n');
            }
            int failures = results.Count(r => !r.Pass);
            if (secs > budget) { sb.Append(string.Format(CultureInfo.InvariantCulture, "FAIL time budget: {0:0.0} s > {1} s\n", secs, budget)); failures++; }
            sb.Append(string.Format(CultureInfo.InvariantCulture, "RESULT {0} failure(s) in {1:0.0} s\n", failures, secs));
            Console.Out.Write(sb.ToString());
            return failures == 0 ? 0 : 1;
        }

        static int GoldenCmd(CliArgs a)
        {
            var db = LoadContent(a);
            string dir = a.Str("golden-dir", GoldenFiles.DefaultDir(ContentDir(a)));
            bool update = a.Has("update");
            int bad = 0;
            foreach (var e in GoldenFiles.All)
            {
                string text = e.Generate(db);
                if (update) { GoldenFiles.Write(dir, e, text); Console.Out.Write($"wrote {e.FileName}\n"); continue; }
                string diff = GoldenFiles.Compare(dir, e, text);
                if (diff == null) Console.Out.Write($"OK   {e.FileName}\n");
                else { Console.Out.Write("FAIL " + diff + "\n"); bad++; }
            }
            return bad == 0 ? 0 : 1;
        }

        /// <summary>Fixtures/Gacha/vectors.json: --update rewrites it, otherwise checks it byte for byte.</summary>
        static int GachaVectorsCmd(CliArgs a)
        {
            var db = LoadContent(a);
            string path = a.Str("out", GachaVectors.DefaultPath(ContentDir(a)));
            string text = GachaVectors.Generate(db);
            if (a.Has("update"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text, new UTF8Encoding(false));
                Console.Out.Write($"wrote {path}\n");
                return 0;
            }
            bool same = File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == text;
            Console.Out.Write(same ? $"OK   {path}\n" : $"FAIL {path} differs (regenerate: {GachaVectors.Regenerate})\n");
            return same ? 0 : 1;
        }

        static int Bench(CliArgs a)
        {
            var db = LoadContent(a);
            long ticks = a.Long("ticks", 1_000_000);
            int stage = a.Int("stage", 300);
            long maxMs = a.Long("max-ms", 1000);
            var state = GameState.NewProfile(db, BotRunner.ProfileIdFor(1), 1, HarnessConstants.Epoch.ToUnixTimeSeconds());
            var sim = new Simulation(db, state, NullEventSink.Instance);
            sim.Debug.SetStage(stage);
            var sw = Stopwatch.StartNew();
            sim.Advance(ticks);
            long ms = sw.ElapsedMilliseconds;
#if DEBUG
            Console.Out.Write($"bench: {ticks} ticks in {ms} ms ({ticks * 1000.0 / Math.Max(1, ms):0} ticks/s); Debug build, no budget verdict\n");
            return 0;
#else
            bool ok = ms <= maxMs;
            Console.Out.Write($"bench: {ticks} ticks in {ms} ms ({ticks * 1000.0 / Math.Max(1, ms):0} ticks/s) {(ok ? "PASS" : "FAIL")} (max {maxMs} ms)\n");
            return ok ? 0 : 1;
#endif
        }
    }
}
