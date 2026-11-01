using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HeroCat.Bots;
using HeroCat.Bots.Policies;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Bots.Tests
{
    internal static class Content
    {
        static readonly Lazy<string> _dir = new Lazy<string>(() =>
            FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory)
            ?? throw new InvalidOperationException("content/manifest.json not found above the test directory"));

        static readonly Lazy<ContentDb> _db = new Lazy<ContentDb>(() => ContentLoader.LoadFromDirectory(_dir.Value, Tier.M0));

        public static string Dir => _dir.Value;
        public static ContentDb Db => _db.Value;
    }

    /// <summary>
    /// A scratch copy of content/ with some tables edited (10 section 3.6 criteria: a content change moves the targets with
    /// no code change). The edited files are rehashed into the manifest and content_hash is replaced, so golden-file checks
    /// see other content.
    /// </summary>
    internal static class ScratchContent
    {
        public static string Make(params (string table, Action<JArray> edit)[] edits)
        {
            string dir = Path.Combine(Path.GetTempPath(), "herocat-bots-scratch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(Content.Dir)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            var manifestPath = Path.Combine(dir, "manifest.json");
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            foreach (var (table, edit) in edits)
            {
                var entry = (JObject)manifest["tables"][table];
                string file = Path.Combine(dir, (string)entry["file"]);
                var doc = JObject.Parse(File.ReadAllText(file));
                edit((JArray)doc["rows"]);
                var bytes = new UTF8Encoding(false).GetBytes(doc.ToString(Formatting.Indented) + "\n");
                File.WriteAllBytes(file, bytes);
                using (var sha = SHA256.Create())
                    entry["sha256"] = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
            }
            manifest["content_hash"] = "scratch" + Guid.NewGuid().ToString("N");
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented) + "\n");
            return dir;
        }

        public static ContentDb Load(string dir, Tier tier = Tier.M0) => ContentLoader.LoadFromDirectory(dir, tier);
    }

    /// <summary>Counts calls; spends nothing. Records the day key at each session's start and end.</summary>
    sealed class CountingPolicy : IBotPolicy
    {
        public int Decisions, SessionStarts, SessionEnds;
        public readonly System.Collections.Generic.List<(int day, int session, int keyAtStart, int keyAtEnd)> DayKeys =
            new System.Collections.Generic.List<(int, int, int, int)>();
        int _keyAtStart;

        public string Name => "counting";

        public void OnSessionStart(BotContext ctx)
        {
            SessionStarts++;
            _keyAtStart = ctx.State.Time.DayKey;
        }

        public void Decide(BotContext ctx) => Decisions++;

        public void OnSessionEnd(BotContext ctx)
        {
            SessionEnds++;
            DayKeys.Add((ctx.Day, ctx.Session, _keyAtStart, ctx.State.Time.DayKey));
        }
    }
}
