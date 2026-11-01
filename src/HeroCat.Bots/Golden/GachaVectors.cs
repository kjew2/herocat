using System.IO;
using System.Text;
using System.Text.Json;
using HeroCat.Sim.Content;
using HeroCat.Sim.Gacha;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Bots.Golden
{
    /// <summary>
    /// Fixtures/Gacha/vectors.json (specs/08 section 4.3 rule 4): the cross-runtime contract for the gacha roll.
    /// Each vector starts from a fresh banner (pity 0), no hero copies and the gacha_hero stream at counter 0,
    /// then runs its batches in order through LocalGachaService. A batch of 10 is a guaranteed 10-pull.
    /// Each pull is [tier, heroId, pityAfter].
    /// </summary>
    public static class GachaVectors
    {
        public const int Format = 1, Seeds = 20, PullsPerSeed = 1000;
        public const string FileName = "vectors.json";
        public const string Regenerate = "dotnet run --project src/HeroCat.Bots -- gacha-vectors --update";

        /// <summary>The repo's Fixtures/Gacha folder: Fixtures is a sibling of the Content folder.</summary>
        public static string DefaultPath(string contentDir) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir).TrimEnd('/', '\\')), "Fixtures", "Gacha", FileName);

        /// <summary>Blocks of 20 pulls: odd seeds open each block with a 10-pull, even seeds close it with one.</summary>
        public static int[] Batches(int profileSeed)
        {
            var b = new int[PullsPerSeed / 20 * 11];
            int i = 0;
            for (int block = 0; block < PullsPerSeed / 20; block++)
            {
                if (profileSeed % 2 == 1) b[i++] = 10;
                for (int k = 0; k < 10; k++) b[i++] = 1;
                if (profileSeed % 2 == 0) b[i++] = 10;
            }
            return b;
        }

        public static string Generate(ContentDb db)
        {
            var banner = db.HeroBanner;
            IGachaService gacha = new LocalGachaService();
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"format\": {Format},\n");
            sb.Append("  \"description\": \"specs/08 section 4.3 rule 4. Per vector: fresh banner (pity 0, no copies), gacha_hero stream ")
              .Append("RngStream(seed = streamSeed, counter = 0), batches run in order (length 10 = guaranteed 10-pull). ")
              .Append("Each pull is [tier, heroId, pityAfter].\",\n");
            sb.Append($"  \"regenerate\": {Q(Regenerate)},\n");
            sb.Append($"  \"contentVersion\": {Q(db.Version)},\n");
            sb.Append($"  \"contentHash\": {Q(db.ContentHash)},\n");
            sb.Append($"  \"bannerId\": {Q(banner.Id)},\n");
            sb.Append($"  \"pullsPerSeed\": {PullsPerSeed},\n");
            sb.Append("  \"vectors\": [\n");
            for (int seed = 1; seed <= Seeds; seed++)
            {
                ulong streamSeed = StreamNames.StreamSeed((ulong)seed, StreamNames.GachaHero);
                var rng = new RngStream(StreamNames.GachaHero, streamSeed);
                var st = new BannerState();
                var heroes = new HeroState();
                var cur = new Currencies();
                int pity = 0;
                sb.Append("    {\n");
                sb.Append($"      \"profileSeed\": {seed},\n");
                sb.Append($"      \"streamSeed\": \"{streamSeed}\",\n");
                sb.Append("      \"batches\": [\n");
                var batches = Batches(seed);
                for (int b = 0; b < batches.Length; b++)
                {
                    var results = gacha.Pull(db, banner, st, heroes, cur, rng, batches[b]);
                    sb.Append("        [");
                    for (int i = 0; i < results.Length; i++)
                    {
                        pity = results[i].Tier >= Rarity.L ? 0 : pity + 1;
                        if (i > 0) sb.Append(',');
                        sb.Append($"[\"{results[i].Tier}\",{Q(results[i].Hero)},{pity}]");
                    }
                    if (pity != st.PityCounter) throw new System.InvalidOperationException("pity bookkeeping drifted");
                    sb.Append(b < batches.Length - 1 ? "],\n" : "]\n");
                }
                sb.Append("      ]\n");
                sb.Append(seed < Seeds ? "    },\n" : "    }\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        static string Q(string s) => JsonSerializer.Serialize(s);
    }
}
