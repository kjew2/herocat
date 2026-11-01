using System;
using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Summons
{
    /// <summary>The uniform draws a pull consumes, in [0, 1). Tests script it; Local mode reads a summon_* stream.</summary>
    public interface IUniformSource
    {
        double Next();
    }

    /// <summary>
    /// A reader over a persisted <c>(seed, counter)</c> stream that never touches the saved <see cref="RngStreamState"/>:
    /// it starts at the given counter and keeps its own (LocalSummonService rolls on a clone, 04 section 12).
    /// </summary>
    public sealed class StreamUniformSource : IUniformSource
    {
        readonly ulong _seed;

        public StreamUniformSource(ulong seed, long counter)
        {
            if (counter < 0) throw new ArgumentOutOfRangeException(nameof(counter));
            _seed = seed;
            Counter = counter;
        }

        /// <summary>Starts at <paramref name="s"/>'s seed and counter; <paramref name="s"/> is not modified.</summary>
        public static StreamUniformSource From(RngStreamState s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!RngStreams.TryParseSeed(s.Seed, out ulong seed)) throw new InvalidOperationException("stream seed is not 16 hex chars");
            return new StreamUniformSource(seed, s.Counter);
        }

        /// <summary>Draws consumed so far, including those before this reader was made.</summary>
        public long Counter { get; private set; }

        public double Next()
        {
            ulong k = (ulong)Counter;
            Counter++;
            return SplitMix64.ToUnit(SplitMix64.Derive(_seed, k));
        }
    }

    /// <summary>One pull's outcome (04 section 3.2). A readonly struct: <c>readonly record struct</c> is C# 10.</summary>
    public readonly struct PullResult : IEquatable<PullResult>
    {
        public PullResult(Banner banner, string itemId, Rarity rarity, bool isNew, bool guaranteeApplied, int copiesAfter,
                          int levelBefore, int levelAfter, long overflowRockCandy, int pityAfter, int bannerLevelAfter)
        {
            Banner = banner; ItemId = itemId; Rarity = rarity; IsNew = isNew; GuaranteeApplied = guaranteeApplied;
            CopiesAfter = copiesAfter; LevelBefore = levelBefore; LevelAfter = levelAfter; OverflowRockCandy = overflowRockCandy;
            PityAfter = pityAfter; BannerLevelAfter = bannerLevelAfter;
        }

        public Banner Banner { get; }
        public string ItemId { get; }
        public Rarity Rarity { get; }
        public bool IsNew { get; }
        public bool GuaranteeApplied { get; }
        public int CopiesAfter { get; }
        public int LevelBefore { get; }
        public int LevelAfter { get; }
        public long OverflowRockCandy { get; }
        public int PityAfter { get; }
        public int BannerLevelAfter { get; }

        public bool Equals(PullResult o) =>
            Banner == o.Banner && ItemId == o.ItemId && Rarity == o.Rarity && IsNew == o.IsNew && GuaranteeApplied == o.GuaranteeApplied &&
            CopiesAfter == o.CopiesAfter && LevelBefore == o.LevelBefore && LevelAfter == o.LevelAfter &&
            OverflowRockCandy == o.OverflowRockCandy && PityAfter == o.PityAfter && BannerLevelAfter == o.BannerLevelAfter;

        public override bool Equals(object obj) => obj is PullResult o && Equals(o);
        public override int GetHashCode() => ((ItemId?.GetHashCode() ?? 0) * 31 + (int)Rarity) * 31 + PityAfter;
        public override string ToString() =>
            $"{Rarity} {ItemId} (copies {CopiesAfter}, lv {LevelBefore}->{LevelAfter}, pity {PityAfter}, banner lv {BannerLevelAfter}" +
            (IsNew ? ", new" : "") + (GuaranteeApplied ? ", guarantee" : "") + (OverflowRockCandy > 0 ? $", +{OverflowRockCandy} rock candy" : "") + ")";
    }

    /// <summary>
    /// The one pull algorithm of 04 section 3.2 (00 decision 56), wallet-free so the server (08) runs the same code.
    /// <paramref name="st"/> and <paramref name="col"/> are always working copies, never the live GameState (issue #13):
    /// LocalSummonService clones them, the server loads its own rows. Exactly three draws per pull, always consumed
    /// (u1 L+ check, u2 tier, u3 pool index), so a stream's counter is 3 * TotalPulls.
    /// </summary>
    public static class PullAlgorithm
    {
        public const int DrawsPerPull = 3;

        /// <summary>One pull. <paramref name="forceEplus"/>: the 10-pull guarantee slot (the L+ check still runs first).</summary>
        public static PullResult Pull(ContentDb db, BannerDef def, BannerState st, CollectionState col, IUniformSource rng, bool forceEplus)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (def == null) throw new ArgumentNullException(nameof(def));
            if (st == null) throw new ArgumentNullException(nameof(st));
            if (col == null) throw new ArgumentNullException(nameof(col));
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            int n = st.Pity + 1;                                   // pulls since last L+, counting this one (issue #24)
            st.TotalPulls += 1;
            st.Exp += def.ExpPerPull;
            double u1 = rng.Next(), u2 = rng.Next(), u3 = rng.Next();
            int level = def.LevelFor(st.Exp);                       // evaluated per pull, after this pull's EXP

            bool lplus = u1 < def.PityRate(n);
            st.Pity = lplus ? 0 : n;
            Rarity rarity;
            if (lplus) rarity = u2 < def.MShareOfLplus ? Rarity.MYTHIC : Rarity.LEGENDARY;
            else if (forceEplus) rarity = def.GuaranteeMinRarity;
            else rarity = SampleCre(def.RateTable(level), u2);

            var pool = def.Pool[rarity];
            if (pool.Count == 0) throw new InvalidOperationException($"banner {def.Id} has no {rarity} items");
            int index = (int)(u3 * pool.Count);
            if (index >= pool.Count) index = pool.Count - 1;        // u3 < 1 always; guards a scripted 1.0
            string itemId = pool[index];

            if (!col.Items.TryGetValue(itemId, out var owned) || owned == null)
                col.Items[itemId] = owned = new OwnedItem();
            int before = db.DupeLevel(owned.Copies);
            bool isNew = owned.Copies == 0;
            long overflow = 0;
            if (owned.Copies >= MaxCopies(db)) overflow = db.Rarities[rarity].DupeOverflowRockCandy;   // copy 161+ converts (5.3)
            else owned.Copies += 1;
            int after = db.DupeLevel(owned.Copies);
            return new PullResult(def.Id, itemId, rarity, isNew, forceEplus && !lplus, owned.Copies, before, after, overflow,
                                  st.Pity, level);
        }

        /// <summary>
        /// A guaranteed batch (GUARANTEE_BATCH_SIZE, 10): the last slot is forced to GuaranteeMinRarity (E) only if the
        /// earlier slots had none at or above it, and still runs the normal L+ check first (00 section 6.3).
        /// </summary>
        public static PullResult[] Pull10(ContentDb db, BannerDef def, BannerState st, CollectionState col, IUniformSource rng)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            int size = def.GuaranteeBatchSize;
            var r = new PullResult[size];
            bool any = false;
            for (int i = 0; i < size - 1; i++)
            {
                r[i] = Pull(db, def, st, col, rng, forceEplus: false);
                if (r[i].Rarity >= def.GuaranteeMinRarity) any = true;
            }
            r[size - 1] = Pull(db, def, st, col, rng, forceEplus: !any);
            return r;
        }

        /// <summary>1 pull, or a guaranteed batch when <paramref name="count"/> is the banner's guarantee batch size.</summary>
        public static PullResult[] PullBatch(ContentDb db, BannerDef def, BannerState st, CollectionState col, IUniformSource rng, int count)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            if (count == def.GuaranteeBatchSize) return Pull10(db, def, st, col, rng);
            if (count != 1) throw new ArgumentOutOfRangeException(nameof(count), count, "a summon is 1 or a guaranteed batch");
            return new[] { Pull(db, def, st, col, rng, forceEplus: false) };
        }

        /// <summary>
        /// Walks C, R, E with each weight divided by (C + R + E) and returns the first tier whose cumulative weight
        /// exceeds <paramref name="u"/> (strict); E if rounding leaves u above the last boundary (04 section 3.2).
        /// The boundaries are cumulative weight over total, (C) / T and (C + R) / T, not a sum of per-tier quotients: with
        /// level-1 rates that keeps the R/E boundary exactly 0.88 / 0.97 as 04 vector 4 states (the summed quotients land
        /// one ulp above it). gacha_model.py does the same arithmetic.
        /// </summary>
        public static Rarity SampleCre(IReadOnlyDictionary<Rarity, double> rates, double u)
        {
            double c = rates[Rarity.COMMON], r = rates[Rarity.RARE], e = rates[Rarity.EPIC];
            double total = c + r + e;
            double cum = c;
            if (u < cum / total) return Rarity.COMMON;
            cum += r;
            if (u < cum / total) return Rarity.RARE;
            return Rarity.EPIC;
        }

        /// <summary>DUPE_LVL_CUM[^1] (160): copies past this convert to Rock Candy.</summary>
        public static int MaxCopies(ContentDb db) => db.DupeLevels[db.DupeLevels.Count - 1];

        /// <summary>The summon_&lt;banner&gt; stream name (00 contract 2).</summary>
        public static string StreamName(Banner b)
        {
            switch (b)
            {
                case Banner.WEAPON: return RngStreams.SummonWeapon;
                case Banner.SKILL: return RngStreams.SummonSkill;
                case Banner.COMPANION: return RngStreams.SummonCompanion;
                default: return "summon_" + b.ToString().ToLowerInvariant();
            }
        }
    }
}
