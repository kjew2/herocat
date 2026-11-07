using HeroCat.Sim.Content;

namespace HeroCat.Server.Features.Summon;

/// <summary><c>pity</c> of <c>GET /summon/rates</c>: the soft ramp and hard pity, and the L+ rate up to the soft start.</summary>
public sealed record PublishedPityDto(int SoftStart, double SoftStepPerPull, int Hard, double LplusAtSoftStart);

/// <summary>
/// <c>effective</c> of <c>GET /summon/rates</c>: the pity closed form of <c>tools/gacha_model.py</c> (04 section 3.1):
/// the L+ rate averaged over the pity cycle, the expected pulls to an L+ and the 99th percentile of pulls to an L+.
/// </summary>
public sealed record PublishedEffectiveDto(double LplusPerPullAveraged, double ExpectedPullsToLplus, int P99PullsToLplus);

/// <summary>One pool item of <c>GET /summon/rates</c>: its rarity and its weight inside that rarity (1: pools are uniform, 04 section 3.2).</summary>
public sealed record PublishedItemDto(string ItemId, string Rarity, int Weight);

/// <summary>
/// <c>GET /summon/rates</c> 200 (specs/08 section 4.5): one banner level's published table. <c>rateTableHash</c> is
/// <see cref="RateTableHash"/> of exactly this table, the hash <c>summon_log</c> stores for every pull rolled with it.
/// </summary>
public sealed record SummonRatesResponse(string Banner, int Level, string ContentVersion, string RateTableHash,
                                         IReadOnlyDictionary<string, double> Rates, PublishedPityDto Pity, string TenPullGuarantee,
                                         PublishedEffectiveDto Effective, PublishedItemDto[] Items);

/// <summary>
/// Builds the published rate table of one banner level from a content set (specs/08 sections 4.5 and 10.3; issue #200).
/// Pure: the endpoint, the odds export and the realised-rate job all read it from here, so they cannot disagree.
/// </summary>
public static class PublishedRates
{
    /// <summary>The rarities in the order the wire lists them (C, R, E, L, M).</summary>
    public static readonly Rarity[] Order = { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY, Rarity.MYTHIC };

    public static SummonRatesResponse Build(ContentDb db, BannerDef def, int level)
    {
        if (level < 1 || level > def.Levels.Count) throw new ArgumentOutOfRangeException(nameof(level), level, $"banner {def.Id} has {def.Levels.Count} levels");
        var row = def.Levels[level - 1].Rates;
        var rates = new Dictionary<string, double>();
        foreach (var r in Order) rates[RarityCodes.Of(r)] = row[r];

        var items = new List<PublishedItemDto>();
        foreach (var r in Order)
            if (def.Pool.TryGetValue(r, out var ids))
                foreach (string id in ids) items.Add(new PublishedItemDto(id, RarityCodes.Of(r), 1));

        return new SummonRatesResponse(
            def.Id.ToString(), level, db.Version, RateTableHash.Hex(RateTableHash.Of(def, level)), rates,
            new PublishedPityDto(def.PitySoftStart, def.PitySoftStep, def.PityHard, def.PityRate(def.PitySoftStart)),
            TenPullGuarantee(def), Effective(def), items.ToArray());
    }

    /// <summary><c>EPIC_OR_BETTER</c>: the rarity slot 10 of a guaranteed batch is forced to when slots 1-9 had none at or above it.</summary>
    public static string TenPullGuarantee(BannerDef def) => def.GuaranteeMinRarity + "_OR_BETTER";

    /// <summary>
    /// <c>tools/gacha_model.py</c>'s <c>pity_numbers</c>, operation for operation: q(n) = P(first L+ at pull n) from
    /// <c>PityRate</c>, E[n] = sum n q(n), the L+ rate 1 / E[n], P99 = the first n whose cumulative q reaches 0.99.
    /// Fixtures/Summon/vectors.json's <c>effective</c> block pins this to the model (issue #200).
    /// </summary>
    public static PublishedEffectiveDto Effective(BannerDef def)
    {
        double surv = 1.0, expected = 0.0, cum = 0.0;
        int p99 = 0;
        for (int n = 1; n <= def.PityHard; n++)
        {
            double p = def.PityRate(n);
            double q = surv * p;
            surv *= 1 - p;
            expected += n * q;
            cum += q;
            if (p99 == 0 && cum >= 0.99) p99 = n;
        }
        if (p99 == 0) p99 = def.PityHard;
        return new PublishedEffectiveDto(1 / expected, expected, p99);
    }

    /// <summary>
    /// The probability of each rarity for one pull rolled with this banner level's table from <paramref name="pityBefore"/>,
    /// with or without the guarantee slot's <c>forceEplus</c> (04 section 3.2): L+ at <c>PityRate(pity + 1)</c>, split
    /// M / L by <c>MShareOfLplus</c>; otherwise the guarantee's rarity when forced, else C/R/E in the level's proportions.
    /// </summary>
    public static IReadOnlyDictionary<Rarity, double> PullProbabilities(BannerDef def, int level, int pityBefore, bool guarantee)
    {
        double lplus = def.PityRate(pityBefore + 1);
        var p = new Dictionary<Rarity, double>
        {
            [Rarity.COMMON] = 0, [Rarity.RARE] = 0, [Rarity.EPIC] = 0,
            [Rarity.LEGENDARY] = lplus * (1 - def.MShareOfLplus),
            [Rarity.MYTHIC] = lplus * def.MShareOfLplus,
        };
        if (guarantee)
        {
            p[def.GuaranteeMinRarity] += 1 - lplus;
            return p;
        }
        var rates = def.Levels[level - 1].Rates;
        double c = rates[Rarity.COMMON], r = rates[Rarity.RARE], e = rates[Rarity.EPIC], total = c + r + e;
        p[Rarity.COMMON] += (1 - lplus) * c / total;
        p[Rarity.RARE] += (1 - lplus) * r / total;
        p[Rarity.EPIC] += (1 - lplus) * e / total;
        return p;
    }
}
