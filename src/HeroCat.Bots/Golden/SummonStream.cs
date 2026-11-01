using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HeroCat.Sim.Content;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;

namespace HeroCat.Bots.Golden
{
    /// <summary>
    /// Fixtures/Summon/stream.json (04 section 3.4, 08 section 4, issue #14): the C#-generated stream-sequence golden.
    /// Raw SplitMix64 draws for three seeds, and 1,000 pulls per M0 banner through a summon stream from counter 0 on a
    /// fresh banner and collection, in blocks of one 10-pull then ten singles. tools/gacha_model.py --check must
    /// reproduce it bit for bit; `summon-stream --check` (check.sh) keeps it in step with the C# algorithm.
    /// </summary>
    public static class SummonStream
    {
        public const int Format = 1, Draws = 1000, Pulls = 1000;
        public const string Regenerate = "dotnet run -c Release --project src/HeroCat.Bots -- summon-stream --update";
        public static readonly ulong[] Seeds = { 1UL, 7UL, 0xDEADBEEFCAFEF00DUL };
        public const ulong PullSeed = 7UL;

        /// <summary>The repo's Fixtures/Summon folder: Fixtures is a sibling of the content folder.</summary>
        public static string DefaultPath(string contentDir) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir).TrimEnd('/', '\\')), "Fixtures", "Summon", "stream.json");

        public static List<int> Batches(int batchSize)
        {
            var b = new List<int>();
            int n = 0;
            while (n < Pulls)
            {
                b.Add(batchSize); n += batchSize;
                for (int k = 0; k < 10 && n < Pulls; k++) { b.Add(1); n++; }
            }
            return b;
        }

        public static string Generate(ContentDb db)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"format\": {Format},\n");
            sb.Append("  \"description\": \"04 section 3.4 stream-sequence golden. draws: SplitMix64.Derive(seed, k) for k = 0..999 ")
              .Append("(raw as 16 hex chars, unit = ToUnit(raw)). pulls: PullAlgorithm on a fresh BannerState and collection with a ")
              .Append("StreamUniformSource(seed, 0); each batch is Pull10 (size 10) or Pull; each result is [rarity, itemId, pityAfter, ")
              .Append("guaranteeApplied].\",\n");
            sb.Append($"  \"regenerate\": {Q(Regenerate)},\n");
            sb.Append($"  \"contentVersion\": {Q(db.Version)},\n");
            sb.Append("  \"draws\": [\n");
            for (int i = 0; i < Seeds.Length; i++)
            {
                ulong seed = Seeds[i];
                var raw = new StringBuilder();
                var unit = new StringBuilder();
                for (int k = 0; k < Draws; k++)
                {
                    ulong x = SplitMix64.Derive(seed, (ulong)k);
                    if (k > 0) { raw.Append(", "); unit.Append(", "); }
                    raw.Append(Q(RngStreams.SeedToHex(x)));
                    unit.Append(SplitMix64.ToUnit(x).ToString("R", CultureInfo.InvariantCulture));
                }
                sb.Append($"    {{\"seed\": {Q(RngStreams.SeedToHex(seed))},\n     \"raw\": [{raw}],\n     \"unit\": [{unit}]}}");
                sb.Append(i + 1 < Seeds.Length ? ",\n" : "\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"pulls\": [\n");
            var banners = new List<BannerDef>();
            foreach (var b in db.Banners.Values) if (b.Enabled && b.Tier == Tier.M0) banners.Add(b);
            banners.Sort((x, y) => x.SortOrder.CompareTo(y.SortOrder));
            for (int i = 0; i < banners.Count; i++)
            {
                var def = banners[i];
                var st = new BannerState();
                var col = new CollectionState();
                var rng = new StreamUniformSource(PullSeed, 0);
                var batches = Batches(def.GuaranteeBatchSize);
                var results = new StringBuilder();
                bool first = true;
                foreach (int count in batches)
                {
                    foreach (var r in PullAlgorithm.PullBatch(db, def, st, col, rng, count))
                    {
                        if (!first) results.Append(",\n      ");
                        first = false;
                        results.Append($"[{Q(r.Rarity.ToString())}, {Q(r.ItemId)}, {r.PityAfter}, {(r.GuaranteeApplied ? "true" : "false")}]");
                    }
                }
                sb.Append($"    {{\"banner\": {Q(def.Id.ToString())}, \"seed\": {Q(RngStreams.SeedToHex(PullSeed))}, \"counterAfter\": {rng.Counter},\n");
                sb.Append($"     \"batches\": [{string.Join(", ", batches)}],\n");
                sb.Append($"     \"results\": [\n      {results}]}}");
                sb.Append(i + 1 < banners.Count ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        public static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
