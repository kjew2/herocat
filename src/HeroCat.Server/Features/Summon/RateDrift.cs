using HeroCat.Sim.Content;

namespace HeroCat.Server.Features.Summon;

/// <summary>
/// One group of logged pulls the realised-rate job counts (a <c>group by</c> over <c>summon_log</c>): pulls of one banner
/// level, rolled with one rate table, from one pity, with or without the guarantee slot, that landed on one rarity.
/// </summary>
public sealed record RealisedPulls(string Banner, int Level, byte[] RateTableHash, int PityBefore, bool Guarantee, string Rarity, long Count);

/// <summary>
/// One rarity of one banner level over the window: pulls, how many landed on the rarity, the rate the published tables
/// predict for those pulls, and the Wilson score interval of the observed frequency at <c>ODDS_ALARM_Z</c>.
/// </summary>
public sealed record RarityDrift(string Banner, int Level, string Rarity, long Pulls, long Observed, double Expected, double WilsonLow,
                                 double WilsonHigh)
{
    public double ObservedRate => Pulls == 0 ? 0 : (double)Observed / Pulls;

    public bool Inside => Expected >= WilsonLow && Expected <= WilsonHigh;
}

/// <summary>Logged pulls whose <c>rate_table_hash</c> no held content set publishes for their banner and level.</summary>
public sealed record UnknownRateTable(string Banner, int Level, string RateTableHash, long Pulls);

/// <summary>The realised-rate job's result (specs/08 section 10.3): every rarity of every banner level seen, and the unknown tables.</summary>
public sealed record RateDriftReport(DateTimeOffset From, DateTimeOffset To, double Z, IReadOnlyList<RarityDrift> Rarities,
                                     IReadOnlyList<UnknownRateTable> UnknownTables)
{
    public IEnumerable<RarityDrift> Drifted => Rarities.Where(r => !r.Inside);

    /// <summary><c>RB-SUMMON-RATES</c> fires: a rarity outside its interval, or pulls rolled with a table nobody published.</summary>
    public bool Alarm => Drifted.Any() || UnknownTables.Count > 0;
}

/// <summary>
/// The realised-rate check of specs/08 section 10.3 and 10 section 10 (issue #200), pure. The published rate of a rarity
/// for a set of pulls is the mean, over those pulls, of the probability the published table gives it from the pull's
/// logged pity and guarantee (<see cref="PublishedRates.PullProbabilities"/>): exactly the table's rate for a pull below
/// soft pity and not forced, and the pity ramp and guarantee for the rest, so pity never reads as drift. The table of
/// each pull is the one its <c>rate_table_hash</c> names among the held content sets; a rarity drifts when that rate
/// lies outside the Wilson score interval of the observed frequency at <see cref="OddsAlarmZ"/>.
/// </summary>
public static class RateDrift
{
    /// <summary><c>ODDS_ALARM_Z</c> (specs/10 constants): the Wilson interval's z.</summary>
    public const double OddsAlarmZ = 3;

    /// <summary>The trailing window of the nightly job (specs/08 section 10.3).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public const string Runbook = "RB-SUMMON-RATES";

    /// <summary>Every (banner, level) table of the given content sets, by its <see cref="RateTableHash"/> hex.</summary>
    public static IReadOnlyDictionary<string, (BannerDef Def, int Level)> TableIndex(IEnumerable<ContentDb> sets)
    {
        var index = new Dictionary<string, (BannerDef, int)>(StringComparer.Ordinal);
        foreach (var db in sets)
            foreach (var def in db.Banners.Values)
                for (int level = 1; level <= def.Levels.Count; level++)
                    index[RateTableHash.Hex(RateTableHash.Of(def, level))] = (def, level);
        return index;
    }

    public static RateDriftReport Evaluate(IEnumerable<RealisedPulls> groups, IReadOnlyDictionary<string, (BannerDef Def, int Level)> tables,
                                           DateTimeOffset from, DateTimeOffset to, double z = OddsAlarmZ)
    {
        var pulls = new Dictionary<(string, int), long>();
        var observed = new Dictionary<(string, int, Rarity), long>();
        var expected = new Dictionary<(string, int, Rarity), double>();
        var unknown = new Dictionary<(string, int, string), long>();
        foreach (var g in groups)
        {
            if (g.Count <= 0) continue;
            string hex = RateTableHash.Hex(g.RateTableHash);
            if (!tables.TryGetValue(hex, out var t) || t.Def.Id.ToString() != g.Banner || t.Level != g.Level)
            {
                unknown[(g.Banner, g.Level, hex)] = unknown.GetValueOrDefault((g.Banner, g.Level, hex)) + g.Count;
                continue;
            }
            var key = (g.Banner, g.Level);
            pulls[key] = pulls.GetValueOrDefault(key) + g.Count;
            var landed = RarityCodes.Parse(g.Rarity);
            observed[(g.Banner, g.Level, landed)] = observed.GetValueOrDefault((g.Banner, g.Level, landed)) + g.Count;
            foreach (var (r, p) in PublishedRates.PullProbabilities(t.Def, t.Level, g.PityBefore, g.Guarantee))
                expected[(g.Banner, g.Level, r)] = expected.GetValueOrDefault((g.Banner, g.Level, r)) + p * g.Count;
        }

        var rows = new List<RarityDrift>();
        foreach (var ((banner, level), n) in pulls.OrderBy(k => k.Key.Item1, StringComparer.Ordinal).ThenBy(k => k.Key.Item2))
            foreach (var r in PublishedRates.Order)
            {
                long k = observed.GetValueOrDefault((banner, level, r));
                var (lo, hi) = Wilson(k, n, z);
                rows.Add(new RarityDrift(banner, level, RarityCodes.Of(r), n, k, expected.GetValueOrDefault((banner, level, r)) / n, lo, hi));
            }
        var unknownRows = unknown.Select(u => new UnknownRateTable(u.Key.Item1, u.Key.Item2, u.Key.Item3, u.Value))
                                 .OrderBy(u => u.Banner, StringComparer.Ordinal).ThenBy(u => u.Level).ToList();
        return new RateDriftReport(from, to, z, rows, unknownRows);
    }

    /// <summary>The Wilson score interval of <paramref name="k"/> successes in <paramref name="n"/> trials at <paramref name="z"/>.</summary>
    public static (double Low, double High) Wilson(long k, long n, double z)
    {
        if (n <= 0) return (0, 1);
        double p = (double)k / n, z2 = z * z, denom = 1 + z2 / n;
        double center = (p + z2 / (2.0 * n)) / denom;
        double half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denom;
        return (Math.Max(0, center - half), Math.Min(1, center + half));
    }
}
