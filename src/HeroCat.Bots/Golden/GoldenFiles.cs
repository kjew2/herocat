using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HeroCat.Bots.Policies;
using HeroCat.Bots.Report;
using HeroCat.Runtime.Replay;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;

namespace HeroCat.Bots.Golden
{
    /// <summary>
    /// The golden files (10 sections 2 and 3.7): the seed-7 command log, its day hashes and content hash (the cross-platform
    /// replay inputs), and the golden run tables. Regenerate with <c>golden --update</c>, never by hand. TODO(#214, #215):
    /// <c>run-lazy-f2p-seed7-30d.txt</c> and <c>run-whale-seed7-30d.txt</c> once those policies land.
    /// </summary>
    public static class GoldenFiles
    {
        public const string Regenerate = "dotnet run -c Release --project src/HeroCat.Bots -- golden --update --allow-sim-change";
        public const string CommandsFile = "seed7-commands.jsonl";
        public const string HashesFile = "seed7-hashes.txt";
        public const string ContentHashFile = "content-hash.txt";
        public const string RunTableFile = "run-greedy-f2p-seed7-30d.txt";
        public const string SweepTableFile = "sweep-greedy-f2p-seeds1-20-30d.txt";
        public const ulong Seed = 7;
        public const int Days = 30;
        public const string Bot = "greedy-f2p";

        /// <summary>Environment variable CI sets when the PR carries the <c>sim-behaviour-change</c> label (10 section 2).</summary>
        public const string SimChangeEnv = "HEROCAT_SIM_BEHAVIOUR_CHANGE";

        public static readonly string[] FileNames = { CommandsFile, HashesFile, ContentHashFile, RunTableFile, SweepTableFile };

        /// <summary>The repo's golden/ folder: a sibling of the content folder.</summary>
        public static string DefaultDir(string contentDir) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir).TrimEnd('/', '\\')), "golden");

        /// <summary>Every golden file's expected text for this content: one seed-7 run feeds the log, the hashes and the run table.</summary>
        public static IReadOnlyDictionary<string, string> Generate(ContentDb db)
        {
            var log = new MemoryJsonlCommandLog();
            var run = new BotRunner(db) { CommandLog = log }.Run(BotPolicies.Create(Bot), Seed, Days);
            var sweep = Sweep.Run(db, Bot, "1-20", Days, Environment.ProcessorCount);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CommandsFile] = log.Text,
                [HashesFile] = HashesText(run.DayHashes, run.FinalTick, run.FinalHash),
                [ContentHashFile] = db.ContentHash + "\n",
                [RunTableFile] = TableWriter.Table(run, allDays: true),
                [SweepTableFile] = Sweep.Table(sweep, TableWriter.ContentTag(db.Version, db.ContentHash)),
            };
        }

        /// <summary><c>day,tick,stateHash</c> after each day's first catch-up, then <c>final,tick,stateHash</c>.</summary>
        public static string HashesText(IEnumerable<DayHash> days, long finalTick, string finalHash)
        {
            var sb = new StringBuilder("day,tick,stateHash\n");
            foreach (var d in days) sb.Append(d.Day.ToString(CultureInfo.InvariantCulture)).Append(',').Append(d.Tick.ToString(CultureInfo.InvariantCulture))
                                      .Append(',').Append(d.StateHash).Append('\n');
            sb.Append("final,").Append(finalTick.ToString(CultureInfo.InvariantCulture)).Append(',').Append(finalHash).Append('\n');
            return sb.ToString();
        }

        /// <summary>The final tick recorded in a hashes file (the replay advances to it after the last command).</summary>
        public static long FinalTick(string hashesText)
        {
            foreach (var line in hashesText.Split('\n'))
                if (line.StartsWith("final,", StringComparison.Ordinal)) return long.Parse(line.Split(',')[1], CultureInfo.InvariantCulture);
            throw new FormatException("hashes file has no final line");
        }

        /// <summary>
        /// 10 section 2's replay: build the seed-7 genesis at the harness epoch, replay the command log by tick with Advance
        /// between commands, and hash the state after each day's first ComputeOfflineCatchup (odd-numbered ones: each
        /// session issues one) and at <paramref name="finalTick"/>. Returns the hashes file text.
        /// </summary>
        public static string Replay(ContentDb db, IReadOnlyList<Command> log, long finalTick)
        {
            var sim = BotRunner.NewSimulation(db, Seed);
            var days = new List<DayHash>();
            int catchups = 0;
            foreach (var c in log)
            {
                if (c.IssuedAtTick < sim.Tick) throw new FormatException($"command at tick {c.IssuedAtTick} is before tick {sim.Tick}");
                sim.AdvanceTicks(c.IssuedAtTick - sim.Tick);
                sim.Apply(c);
                sim.DrainEvents();
                if (c is ComputeOfflineCatchup && ++catchups % HarnessConstants.SessionsPerDay == 1)
                    days.Add(new DayHash(days.Count + 1, sim.Tick, sim.StateHash()));
            }
            if (finalTick < sim.Tick) throw new FormatException($"final tick {finalTick} is before the last command's tick {sim.Tick}");
            sim.AdvanceTicks(finalTick - sim.Tick);
            sim.DrainEvents();
            return HashesText(days, sim.Tick, sim.StateHash());
        }

        /// <summary>Replays the committed log and compares with the committed hashes. Null when they match, else a report.</summary>
        public static string CheckReplay(ContentDb db, string dir)
        {
            string logPath = Path.Combine(dir, CommandsFile), hashPath = Path.Combine(dir, HashesFile);
            if (!File.Exists(logPath) || !File.Exists(hashPath)) return $"{CommandsFile} / {HashesFile}: missing (run `{Regenerate}`)";
            string expected = Read(hashPath);
            string actual = Replay(db, JsonlCommandLog.Parse(Read(logPath)), FinalTick(expected));
            return Diff(HashesFile + " (replay)", expected, actual);
        }

        public static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

        /// <summary>Null when <paramref name="actual"/> equals the file; else a report (verdict + first differing lines).</summary>
        public static string Compare(string dir, string fileName, string actual)
        {
            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path)) return $"{fileName}: missing (run `{Regenerate}`)";
            return Diff(fileName, Read(path), actual);
        }

        static string Diff(string name, string expected, string actual)
        {
            if (expected == actual) return null;
            var exp = expected.Split('\n');
            var act = actual.Split('\n');
            var sb = new StringBuilder();
            sb.Append($"{name}: MISMATCH (regenerate: {Regenerate})\n");
            int shown = 0;
            for (int i = 0; i < Math.Max(exp.Length, act.Length) && shown < 10; i++)
            {
                string a = i < exp.Length ? exp[i] : null, b = i < act.Length ? act[i] : null;
                if (a == b) continue;
                if (a != null) sb.Append("  -").Append(Clip(a)).Append('\n');
                if (b != null) sb.Append("  +").Append(Clip(b)).Append('\n');
                shown++;
            }
            return sb.ToString().TrimEnd('\n');
        }

        static string Clip(string s) => s.Length > 200 ? s.Substring(0, 200) + "..." : s;

        /// <summary>
        /// 10 section 2: <c>golden --update</c> is refused unless the content hash changed (against the committed
        /// content-hash.txt) or the change is declared a sim behaviour change (the <c>sim-behaviour-change</c> PR label in CI,
        /// <c>--allow-sim-change</c> locally). Null when allowed, else the refusal message.
        /// </summary>
        public static string UpdateRefusal(ContentDb db, string dir, bool allowSimChange)
        {
            if (allowSimChange) return null;
            if (Environment.GetEnvironmentVariable(SimChangeEnv) == "1") return null;
            string path = Path.Combine(dir, ContentHashFile);
            if (!File.Exists(path) || Read(path).Trim() != db.ContentHash) return null;
            return $"golden --update refused: the content hash ({db.ContentHash}) did not change. A sim behaviour change needs the " +
                   $"`sim-behaviour-change` PR label ({SimChangeEnv}=1 in CI) or --allow-sim-change locally (10 section 2).";
        }

        public static void Write(string dir, string fileName, string text)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), text, new UTF8Encoding(false));
        }
    }
}
