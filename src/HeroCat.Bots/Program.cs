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
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Save;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;

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
            "usage: HeroCat.Bots <run|sweep|acceptance|golden|save-fixture|summon-stream|bench> [options]\n" +
            "  run          --bot idle --seed N --days N --all-days --format table|csv|json --out path --content dir --tier M0|MVP\n" +
            "  sweep        --bot idle --seeds 1-20 --days N --format table|csv|json --out path --parallel N --content dir\n" +
            "  acceptance   --seeds 1-20 --days N --time-budget-s 180 --targets path --verbose --content dir\n" +
            "  golden       --check|--update --golden-dir path --content dir\n" +
            "  save-fixture --check|--update --out path --content dir\n" +
            "  summon-stream --check|--update --out path --content dir\n" +
            "  bench        --ticks N --max-ms N\n";

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
                    case "acceptance": return AcceptanceCmd(a);
                    case "golden": return GoldenCmd(a);
                    case "save-fixture": return SaveFixtureCmd(a);
                    case "summon-stream": return SummonStreamCmd(a);
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

        /// <summary>The bots build at M0 by default: the M0 slice is what they exercise (TODO(#136): --tier per 10 section 3.8).</summary>
        static ContentDb LoadContent(CliArgs a)
        {
            var tier = (Tier)Enum.Parse(typeof(Tier), a.Str("tier", "M0"));
            var db = ContentLoader.LoadFromDirectory(ContentDir(a), tier);
            Console.Error.Write($"# content {db.Version} hash {db.ContentHash} tier {db.BuildTier} sim {typeof(Simulation).Assembly.GetName().Version}\n");
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
            var run = new BotRunner(db).Run(BotPolicies.Create(a.Str("bot", HarnessConstants.DefaultBot)),
                                            (ulong)a.Long("seed", (long)HarnessConstants.DefaultSeed), a.Int("days", HarnessConstants.DefaultDays));
            string fmt = a.Str("format", "table");
            bool all = a.Has("all-days");
            Emit(a, fmt == "csv" ? TableWriter.Csv(run, all) : fmt == "json" ? TableWriter.Json(run) : TableWriter.Table(run, all));
            return 0;
        }

        static int SweepCmd(CliArgs a)
        {
            var db = LoadContent(a);
            var sw = Sweep.Run(db, a.Str("bot", HarnessConstants.DefaultBot), a.Str("seeds", HarnessConstants.DefaultSeeds),
                               a.Int("days", HarnessConstants.DefaultDays), a.Int("parallel", Environment.ProcessorCount));
            string fmt = a.Str("format", "table");
            Emit(a, fmt == "csv" ? Sweep.Csv(sw) : fmt == "json" ? TableWriter.Json(sw.Runs) : Sweep.Table(sw, TableWriter.ContentTag(db.Version, db.ContentHash)));
            return 0;
        }

        /// <summary>Null if seed 7 twice is identical and (when the golden dir exists) the run golden matches.</summary>
        public static string DeterminismCheck(ContentDb db, string goldenDirOrNull)
        {
            var e = GoldenFiles.All[0];
            string x = e.Generate(db), y = e.Generate(db);
            if (x != y) return "seed 7 twice differs";
            if (goldenDirOrNull != null && Directory.Exists(goldenDirOrNull)) return GoldenFiles.Compare(goldenDirOrNull, e, x);
            return null;
        }

        static int AcceptanceCmd(CliArgs a)
        {
            var sw0 = Stopwatch.StartNew();
            var db = LoadContent(a);
            var targets = AcceptanceTargets.Load(a.Str("targets", null));
            string seeds = a.Str("seeds", HarnessConstants.DefaultSeeds);
            int days = a.Int("days", HarnessConstants.DefaultDays);
            string bot = HarnessConstants.DefaultBot;   // TODO(#135): greedy-f2p
            var sw = Sweep.Run(db, bot, seeds, days, Environment.ProcessorCount);
            string goldenDir = a.Str("golden-dir", GoldenFiles.DefaultDir(ContentDir(a)));
            var results = AcceptanceChecker.Check(sw, db, targets, () => DeterminismCheck(db, goldenDir));
            double secs = sw0.Elapsed.TotalSeconds;
            int budget = a.Int("time-budget-s", 180);

            var sb = new StringBuilder();
            sb.Append("# herocat acceptance v2\n");
            sb.Append($"# bot={bot} seeds={seeds} days={days} content={TableWriter.ContentTag(db.Version, db.ContentHash)}\n");
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

        /// <summary>The v1 save fixture: --update rewrites it (refused once V1 is frozen), otherwise checks it byte for byte.</summary>
        /// <summary>Fixtures/Summon/stream.json, the C# stream-sequence golden that gacha_model.py reproduces (issue #14).</summary>
        static int SummonStreamCmd(CliArgs a)
        {
            var db = LoadContent(a);
            string path = a.Str("out", SummonStream.DefaultPath(ContentDir(a)));
            string text = SummonStream.Generate(db);
            if (a.Has("update"))
            {
                SummonStream.Write(path, text);
                Console.Out.Write($"wrote {path}\n");
                return 0;
            }
            bool same = File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == text;
            Console.Out.Write(same ? $"OK   {path}\n" : $"FAIL {path} differs (regenerate: {SummonStream.Regenerate})\n");
            return same ? 0 : 1;
        }

        static int SaveFixtureCmd(CliArgs a)
        {
            var db = LoadContent(a);
            string path = a.Str("out", SaveFixture.DefaultPath(ContentDir(a)));
            if (SaveSchema.V1Frozen)
            {
                Console.Out.Write($"SKIP {path}: v1 is frozen; its hash is pinned by MigrationTests M5\n");
                return a.Has("update") ? 1 : 0;
            }
            string text = SaveFixture.Generate(db);
            if (a.Has("update"))
            {
                SaveFixture.Write(path, text);
                Console.Out.Write($"wrote {path}\n");
                return 0;
            }
            bool same = File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == text;
            Console.Out.Write(same ? $"OK   {path}\n" : $"FAIL {path} differs (regenerate: {SaveFixture.Regenerate})\n");
            return same ? 0 : 1;
        }

        static int Bench(CliArgs a)
        {
            var db = LoadContent(a);
            long ticks = a.Long("ticks", 1_000_000);
            long maxMs = a.Long("max-ms", 1000);
            var sim = new Simulation(BotRunner.Genesis(db, 1), db) { ForceCoalesce = true };
            sim.Apply(new ApplyServerTime(HarnessConstants.Epoch.ToUnixTimeSeconds()));
            var sw = Stopwatch.StartNew();
            for (long done = 0; done < ticks; done += 1000) sim.Advance((int)Math.Min(1000, ticks - done));
            long ms = sw.ElapsedMilliseconds;
            // TODO(#160): bench a full stage loop with skills and companions once combat lands (#81).
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
