using HeroCat.Sim.Content;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Gacha
{
    /// <summary>One gacha result. (Readonly struct rather than a record struct: the sim is C# 9.)</summary>
    public readonly struct PullResult : System.IEquatable<PullResult>
    {
        public PullResult(string hero, Rarity tier, bool isNew, int levelBefore, int levelAfter, long shardsGained)
        {
            Hero = hero; Tier = tier; IsNew = isNew; LevelBefore = levelBefore; LevelAfter = levelAfter; ShardsGained = shardsGained;
        }

        public string Hero { get; }
        public Rarity Tier { get; }
        public bool IsNew { get; }
        public int LevelBefore { get; }
        public int LevelAfter { get; }
        public long ShardsGained { get; }

        public bool Equals(PullResult o) => Hero == o.Hero && Tier == o.Tier && IsNew == o.IsNew &&
            LevelBefore == o.LevelBefore && LevelAfter == o.LevelAfter && ShardsGained == o.ShardsGained;
        public override bool Equals(object obj) => obj is PullResult o && Equals(o);
        public override int GetHashCode() => (Hero?.GetHashCode() ?? 0) * 31 + (int)Tier;
        public override string ToString() => $"{Tier} {Hero} (lv {LevelBefore}->{LevelAfter}{(IsNew ? ", new" : "")}{(ShardsGained > 0 ? $", +{ShardsGained} shards" : "")})";
    }

    /// <summary>
    /// Server seam (specs/00 section 5, specs/08). P0 ships LocalGachaService, which runs specs/01 section 8 on the
    /// gacha_hero stream. Payment is NOT the service's job (Simulation.Pull handles it); the service rolls results and
    /// writes copies/pity/shards.
    /// </summary>
    public interface IGachaService
    {
        PullResult[] Pull(ContentDb content, BannerDef banner, BannerState bannerState, HeroState heroes,
                          Currencies currencies, IRandomSource rng, int count);
    }

    public sealed class LocalGachaService : IGachaService
    {
        public PullResult[] Pull(ContentDb content, BannerDef banner, BannerState bannerState, HeroState heroes,
                                 Currencies currencies, IRandomSource rng, int count) =>
            GachaAlgorithm.PullBatch(content, banner, bannerState, heroes, currencies, rng, count);
    }

    /// <summary>specs/01 section 8. Exactly three draws per pull: u1 L+ check, u2 tier, u3 hero index.</summary>
    public static class GachaAlgorithm
    {
        /// <summary>
        /// count pulls in sequence. When count &gt;= GuaranteeBatchSize, the last slot is forced to at least
        /// GuaranteeMinTier if none of the earlier slots reached it (specs/01 section 4).
        /// </summary>
        public static PullResult[] PullBatch(ContentDb content, BannerDef banner, BannerState st, HeroState heroes,
                                             Currencies cur, IRandomSource rng, int count)
        {
            var r = new PullResult[count];
            bool guarantee = count >= banner.GuaranteeBatchSize && banner.GuaranteeBatchSize >= 2;
            bool hit = false;
            for (int i = 0; i < count; i++)
            {
                bool force = guarantee && i == count - 1 && !hit;
                r[i] = PullOne(content, banner, st, heroes, cur, rng, force);
                if (r[i].Tier >= banner.GuaranteeMinTier) hit = true;
            }
            return r;
        }

        public static PullResult PullOne(ContentDb content, BannerDef banner, BannerState st, HeroState heroes,
                                         Currencies cur, IRandomSource rng, bool forceMinTier)
        {
            st.PityCounter += 1;
            st.TotalPulls += 1;
            double u1 = rng.NextDouble(), u2 = rng.NextDouble(), u3 = rng.NextDouble();

            Rarity tier = RollTier(banner, st.PityCounter, forceMinTier, u1, u2);
            if (tier >= Rarity.L) st.PityCounter = 0;

            var pool = banner.Pool[(int)tier];
            string id = pool[(int)(u3 * pool.Count)];

            heroes.Copies.TryGetValue(id, out int copies);
            int before = content.HeroLevel(copies);
            copies += 1;
            heroes.Copies[id] = copies;
            int after = content.HeroLevel(copies);
            long shards = 0;
            var th = content.HeroLevelThresholds;
            if (copies > th[th.Count - 1])
            {
                shards = content.HeroTiers[(int)tier].ShardValue;
                cur.Shards += shards;
            }
            return new PullResult(id, tier, copies == 1, before, after, shards);
        }

        /// <summary>
        /// L+ check first at PityRate(n); if it fails and forceMinTier, draw from [GuaranteeMinTier..E] renormalised
        /// (just E with P0 content); otherwise from C/R/E renormalised.
        /// </summary>
        public static Rarity RollTier(BannerDef banner, int pityN, bool forceMinTier, double u1, double u2)
        {
            if (u1 < banner.PityRate(pityN))
                return u2 < banner.MythicShareOfLplus ? Rarity.M : Rarity.L;
            var lo = forceMinTier ? banner.GuaranteeMinTier : Rarity.C;
            if (lo > Rarity.E) return lo;
            return SampleRange(banner.Rates, lo, Rarity.E, u2);
        }

        /// <summary>Walks cumulative weights over [lo..hi], renormalised to sum to 1.</summary>
        public static Rarity SampleRange(System.Collections.Generic.IReadOnlyList<double> w, Rarity lo, Rarity hi, double u)
        {
            double total = 0;
            for (int r = (int)lo; r <= (int)hi; r++) total += w[r];
            double acc = 0;
            for (int r = (int)lo; r <= (int)hi; r++)
            {
                acc += w[r] / total;
                if (u < acc) return (Rarity)r;
            }
            return hi;
        }
    }
}
