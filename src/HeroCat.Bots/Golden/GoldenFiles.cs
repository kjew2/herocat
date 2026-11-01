using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HeroCat.Bots.Policies;
using HeroCat.Bots.Report;
using HeroCat.Sim.Content;

namespace HeroCat.Bots.Golden
{
    /// <summary>Golden files: expected output of fixed commands (specs/09 section 11).</summary>
    public static class GoldenFiles
    {
        public sealed record Entry(string FileName, string Command, Func<ContentDb, string> Generate);

        public static readonly IReadOnlyList<Entry> All = new[]
        {
            new Entry("run-greedy-f2p-seed7-30d.txt", "run --bot greedy-f2p --seed 7 --days 30 --all-days",
                db => TableWriter.Table(new BotRunner(db).Run(BotPolicies.Create("greedy-f2p"), 7, 30), allDays: true)),
            new Entry("run-whale-seed7-30d.txt", "run --bot whale --seed 7 --days 30 --all-days",
                db => TableWriter.Table(new BotRunner(db).Run(BotPolicies.Create("whale"), 7, 30), allDays: true)),
            new Entry("sweep-greedy-f2p-seeds1-20-30d.txt", "sweep --bot greedy-f2p --seeds 1-20 --days 30",
                db => Sweep.Table(Sweep.Run(db, "greedy-f2p", "1-20", 30, Environment.ProcessorCount),
                                  TableWriter.ContentTag(db.Version, db.ContentHash))),
        };

        /// <summary>The repo's golden/ folder: a sibling of the Content folder.</summary>
        public static string DefaultDir(string contentDir) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir).TrimEnd('/', '\\')), "golden");

        /// <summary>Null when <paramref name="actual"/> equals the file; else a report (verdict + first differing lines).</summary>
        public static string Compare(string dir, Entry e, string actual)
        {
            string path = Path.Combine(dir, e.FileName);
            if (!File.Exists(path)) return $"{e.FileName}: missing (run `golden --update`)";
            string expected = File.ReadAllText(path).Replace("\r\n", "\n");
            if (expected == actual) return null;

            var exp = expected.Split('\n');
            var act = actual.Split('\n');
            var sb = new StringBuilder();
            sb.Append($"{e.FileName}: MISMATCH (regenerate: dotnet run -c Release --project src/HeroCat.Bots -- golden --update)\n");
            bool contentChanged = HeaderContent(exp) != HeaderContent(act);
            sb.Append(contentChanged
                ? "  content changed; if the acceptance checks pass and the change is intended, run `golden --update`\n"
                : "  sim or bot behaviour changed with identical content; this is a determinism or logic regression unless a sim change explains it\n");
            int shown = 0;
            for (int i = 0; i < Math.Max(exp.Length, act.Length) && shown < 20; i++)
            {
                string a = i < exp.Length ? exp[i] : null, b = i < act.Length ? act[i] : null;
                if (a == b) continue;
                if (a != null) sb.Append("  -").Append(a).Append('\n');
                if (b != null) sb.Append("  +").Append(b).Append('\n');
                shown++;
            }
            return sb.ToString().TrimEnd('\n');
        }

        static string HeaderContent(string[] lines)
        {
            foreach (var l in lines)
            {
                int i = l.IndexOf("content=", StringComparison.Ordinal);
                if (l.StartsWith("#") && i >= 0) return l.Substring(i);
            }
            return "";
        }

        public static void Write(string dir, Entry e, string actual)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, e.FileName), actual, new UTF8Encoding(false));
        }
    }
}
