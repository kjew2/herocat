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
using HeroCat.Runtime.Replay;
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
        public double Double(string k, double def) => _opts.TryGetValue(k, out var v) && v != "" ? double.Parse(v, CultureInfo.InvariantCulture) : def;
    }

    /// <summary>The bot CLI (10 section 3.8). Writes results to stdout (or --out), diagnostics to stderr.</summary>
    public static class Program
    {
        public const string Usage =
            "usage: HeroCat.Bots <verb> [options]   (10 section 3.8)\n" +
            "  run          --bot greedy-f2p --seed 7 --days 30 [--all-days] [--record path] [--format table|csv|json]\n" +
            "               [--diamonds content|flat] [--content dir] [--tier M0|MVP] [--out path]\n" +
            "  sweep        --bot greedy-f2p --seeds 1-20 --days 30 [--parallel N] [--format table|csv|json] [--diamonds content|flat]\n" +
            "  compare      --bots a,b,c --seeds 1-20 --days 30 [--parallel N]\n" +
            "  acceptance   [--table] [--verbose] [--targets path] [--time-budget-s N] [--seeds 1-20] [--days 30] [--tier M0|MVP]\n" +
            "               [--golden-dir path] [--parallel N]\n" +
            "  report       --longest-walls | --layer-shares | --diamonds  [--bot greedy-f2p] --seeds 1-20 --days 30 [--parallel N]\n" +
            "  golden       --check | --update [--allow-sim-change] | --replay   [--golden-dir path]\n" +
            "  save-fixture --check | --update [--out path]\n" +
            "  summon-stream --check | --update [--out path]\n" +
            "  bench        --ticks 1000000 --stage 300 --max-ms 2000\n" +
            "  stage-bound  --check | --update [--seeds 1-20] [--days 60]   TODO(#60): needs the whale bot\n" +
            "common: --content dir (default: the repo's content/), --tier M0|MVP (default M0: the systems the M0 sim has)\n" +
            "bots: " + "greedy-f2p (lazy-f2p, light-spender, whale: TODO(#214, #215))\n";

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
                    case "compare": return CompareCmd(a);
                    case "acceptance": return AcceptanceCmd(a, Console.Out);
                    case "report": return ReportCmd(a);
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
            catch (NotSupportedException e)
            {
                Console.Error.Write("error: " + e.Message + "\n");
                return 1;
            }
        }

        public static string ContentDir(CliArgs a) =>
            a.Str("content", null) ?? FileContentSource.FindContentDirectory(AppContext.BaseDirectory)
            ?? FileContentSource.FindContentDirectory(Directory.GetCurrentDirectory())
            ?? throw new ArgumentException("content/manifest.json not found; pass --content <dir>");

        /// <summary>
        /// --tier M0|MVP (09 section 5.32). The default is M0: the M0 sim has no Awakening, Research, companions, quests or
        /// dungeons yet, so the bots exercise the M0 slice (TODO(#214): default MVP once those land).
        /// </summary>
        public static ContentDb LoadContent(CliArgs a)
        {
            var tier = (Tier)Enum.Parse(typeof(Tier), a.Str("tier", "M0"));
            var db = ContentLoader.LoadFromDirectory(ContentDir(a), tier);
            Console.Error.Write($"# content {db.Version} hash {db.ContentHash} tier {db.BuildTier} sim {typeof(Simulation).Assembly.GetName().Version}\n");
            return db;
        }

        static DiamondMode Diamonds(CliArgs a)
        {
            string d = a.Str("diamonds", "content");
            if (d == "content") return DiamondMode.Content;
            if (d == "flat") return DiamondMode.Flat;
            throw new ArgumentException($"--diamonds {d}: expected content|flat");
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
            string record = a.Str("record", null);
            var log = record != null ? new MemoryJsonlCommandLog() : null;
            var run = new BotRunner(db) { Diamonds = Diamonds(a), CommandLog = log }
                .Run(BotPolicies.Create(a.Str("bot", HarnessConstants.DefaultBot)), (ulong)a.Long("seed", (long)HarnessConstants.DefaultSeed),
                     a.Int("days", HarnessConstants.DefaultDays));
            if (record != null)
            {
                File.WriteAllText(record, log.Text, new UTF8Encoding(false));
                Console.Error.Write($"# recorded {log.Lines.Count} commands to {record}\n");
            }
            string fmt = a.Str("format", "table");
            bool all = a.Has("all-days");
            Emit(a, fmt == "csv" ? TableWriter.Csv(run, all) : fmt == "json" ? TableWriter.Json(run) : TableWriter.Table(run, all));
            return 0;
        }

        static int Parallel(CliArgs a) => a.Int("parallel", Environment.ProcessorCount);

        static int SweepCmd(CliArgs a)
        {
            var db = LoadContent(a);
            var sw = Sweep.Run(db, a.Str("bot", HarnessConstants.DefaultBot), a.Str("seeds", HarnessConstants.DefaultSeeds),
                               a.Int("days", HarnessConstants.DefaultDays), Parallel(a), Diamonds(a));
            string fmt = a.Str("format", "table");
            Emit(a, fmt == "csv" ? Sweep.Csv(sw) : fmt == "json" ? TableWriter.Json(sw.Runs) : Sweep.Table(sw, TableWriter.ContentTag(db.Version, db.ContentHash)));
            return 0;
        }

        static int CompareCmd(CliArgs a)
        {
            var db = LoadContent(a);
            var bots = a.Str("bots", HarnessConstants.DefaultBot).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            string seeds = a.Str("seeds", HarnessConstants.DefaultSeeds);
            int days = a.Int("days", HarnessConstants.DefaultDays);
            var sweeps = bots.Select(b => Sweep.Run(db, b, seeds, days, Parallel(a), Diamonds(a))).ToList();
            Emit(a, Reports.Compare(sweeps, TableWriter.ContentTag(db.Version, db.ContentHash)));
            return 0;
        }

        static int ReportCmd(CliArgs a)
        {
            var db = LoadContent(a);
            var t = AcceptanceTargets.LoadDefault();
            var sw = Sweep.Run(db, a.Str("bot", HarnessConstants.DefaultBot), a.Str("seeds", HarnessConstants.DefaultSeeds),
                               a.Int("days", HarnessConstants.DefaultDays), Parallel(a), Diamonds(a));
            string tag = TableWriter.ContentTag(db.Version, db.ContentHash);
            if (a.Has("longest-walls")) Emit(a, Reports.LongestWalls(sw, tag));
            else if (a.Has("layer-shares")) Emit(a, Reports.LayerSharesReport(sw, tag, t.ShareMax, t.ShareMin));
            else if (a.Has("diamonds")) Emit(a, Reports.Diamonds(sw, tag));
            else throw new ArgumentException("report needs --longest-walls, --layer-shares or --diamonds");
            return 0;
        }

        /// <summary>
        /// AC9: seed 7 twice in-process is byte-identical (run table and final hash), and the committed golden command log
        /// replays to the committed day hashes. Null when both hold. The replay part is skipped (not failed) when the golden
        /// files were made from other content (a scratch --content dir).
        /// </summary>
        public static string DeterminismCheck(ContentDb db, int days, string goldenDirOrNull)
        {
            string Once()
            {
                var r = new BotRunner(db).Run(BotPolicies.Create(GoldenFiles.Bot), GoldenFiles.Seed, days);
                return TableWriter.Table(r, allDays: true) + string.Join("\n", r.DayHashes) + r.FinalHash;
            }
            if (Once() != Once()) return "seed 7 twice in-process differs";
            if (goldenDirOrNull == null || !Directory.Exists(goldenDirOrNull)) return null;
            string hashPath = Path.Combine(goldenDirOrNull, GoldenFiles.ContentHashFile);
            if (File.Exists(hashPath) && GoldenFiles.Read(hashPath).Trim() != db.ContentHash) return null;
            return GoldenFiles.CheckReplay(db, goldenDirOrNull);
        }

        /// <summary>
        /// 10 section 3.6. Every line but AC10's (which carries the measured wall time) is a pure function of the content,
        /// the targets and the options, so two runs in one process print the same lines (acceptance criterion 5).
        /// </summary>
        public static int AcceptanceCmd(CliArgs a, TextWriter output)
        {
            var sw0 = Stopwatch.StartNew();
            var db = LoadContent(a);
            var targets = AcceptanceTargets.Load(a.Str("targets", null));
            string seeds = a.Str("seeds", targets.Seeds ?? HarnessConstants.DefaultSeeds);
            int days = a.Int("days", targets.Days > 0 ? targets.Days : HarnessConstants.DefaultDays);
            string bot = targets.SweepBot ?? HarnessConstants.DefaultBot;
            var sw = Sweep.Run(db, bot, seeds, days, Parallel(a));
            double sweepSecs = sw0.Elapsed.TotalSeconds;
            string goldenDir = a.Str("golden-dir", GoldenFiles.DefaultDir(ContentDir(a)));
            var results = AcceptanceChecker.Check(sw, db, targets, () => DeterminismCheck(db, Math.Min(days, GoldenFiles.Days), goldenDir));
            double budget = a.Double("time-budget-s", targets.TimeBudgetSeconds);
            double secs = sw0.Elapsed.TotalSeconds;
            int ac11 = results.FindIndex(r => r.Id == "AC11");
            results.Insert(ac11 < 0 ? results.Count : ac11, new CheckResult("AC10", secs <= budget ? CheckStatus.Pass : CheckStatus.Fail,
                string.Format(CultureInfo.InvariantCulture, "acceptance wall time {0:0.0} s (sweep {1:0.0} s) {2} {3:0} s budget", secs, sweepSecs,
                              secs <= budget ? "<=" : ">", budget), Array.Empty<string>()));

            string tag = TableWriter.ContentTag(db.Version, db.ContentHash);
            var sb = new StringBuilder();
            sb.Append("# herocat acceptance v3\n");
            sb.Append($"# bot={bot} seeds={seeds} days={days} tier={db.BuildTier} content={tag}\n");
            if (a.Has("table")) sb.Append(AcceptanceChecker.Table(sw, tag, db.ProgressTargets.Select(p => p.Day)));
            foreach (var r in results)
            {
                sb.Append(r.Line).Append('\n');
                if (a.Has("verbose")) foreach (var d in r.Details) sb.Append("     ").Append(d).Append('\n');
            }
            int failures = results.Count(r => r.Status == CheckStatus.Fail);
            int warns = results.Count(r => r.Status == CheckStatus.Warn);
            sb.Append($"RESULT {failures} failure(s), {warns} warning(s)\n");
            output.Write(sb.ToString());
            return failures == 0 ? 0 : 1;
        }

        static int GoldenCmd(CliArgs a)
        {
            var db = LoadContent(a);
            string dir = a.Str("golden-dir", GoldenFiles.DefaultDir(ContentDir(a)));
            if (a.Has("replay"))
            {
                string hashPath = Path.Combine(dir, GoldenFiles.HashesFile), logPath = Path.Combine(dir, GoldenFiles.CommandsFile);
                if (!File.Exists(hashPath) || !File.Exists(logPath)) { Console.Out.Write($"FAIL replay: golden files missing in {dir}\n"); return 1; }
                string expected = GoldenFiles.Read(hashPath);
                string actual = GoldenFiles.Replay(db, JsonlCommandLog.Parse(GoldenFiles.Read(logPath)), GoldenFiles.FinalTick(expected));
                Console.Out.Write(actual);
                bool same = actual == expected;
                Console.Out.Write(same ? "OK   replay matches " + GoldenFiles.HashesFile + "\n" : "FAIL replay differs from " + GoldenFiles.HashesFile + "\n");
                return same ? 0 : 1;
            }
            bool update = a.Has("update");
            if (update)
            {
                string refusal = GoldenFiles.UpdateRefusal(db, dir, a.Has("allow-sim-change"));
                if (refusal != null) { Console.Error.Write(refusal + "\n"); return 3; }
            }
            var files = GoldenFiles.Generate(db);
            int bad = 0;
            foreach (var name in GoldenFiles.FileNames)
            {
                string text = files[name];
                if (update) { GoldenFiles.Write(dir, name, text); Console.Out.Write($"wrote {name}\n"); continue; }
                string diff = GoldenFiles.Compare(dir, name, text);
                if (diff == null) Console.Out.Write($"OK   {name}\n");
                else { Console.Out.Write("FAIL " + diff + "\n"); bad++; }
            }
            if (!update)
            {
                string replay = GoldenFiles.CheckReplay(db, dir);
                if (replay == null) Console.Out.Write("OK   replay of " + GoldenFiles.CommandsFile + "\n");
                else { Console.Out.Write("FAIL " + replay + "\n"); bad++; }
            }
            return bad == 0 ? 0 : 1;
        }

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

        /// <summary>The v1 save fixture: --update rewrites it (refused once V1 is frozen), otherwise checks it byte for byte.</summary>
        static int SaveFixtureCmd(CliArgs a)
        {
            var db = LoadContent(a);
            string path = a.Str("out", SaveFixture.DefaultPath(ContentDir(a)));
#pragma warning disable CS0162 // V1Frozen is a const that flips once; the build runs with -warnaserror (10 section 6.2)
            if (SaveSchema.V1Frozen)
            {
                Console.Out.Write($"SKIP {path}: v1 is frozen; its hash is pinned by MigrationTests M5\n");
                return a.Has("update") ? 1 : 0;
            }
#pragma warning restore CS0162
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
            long maxMs = a.Long("max-ms", 2000);   // 03 criterion 19, 10 section 1.1
            int stage = a.Int("stage", 1);
            var genesis = BotRunner.Genesis(db, 1);
            genesis.Combat.Stage = stage;
            if (stage > 1) genesis.Progress.HighestCleared = stage - 1;
            var sim = new Simulation(genesis, db) { ForceCoalesce = true };
            sim.Apply(new ApplyServerTime(HarnessConstants.Epoch.ToUnixTimeSeconds()));
            var sw = Stopwatch.StartNew();
            for (long done = 0; done < ticks; done += 1000) sim.Advance((int)Math.Min(1000, ticks - done));
            long ms = sw.ElapsedMilliseconds;
            // TODO(#160): equip six skills and four companions (03 criterion 19) once companions land (MVP).
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
