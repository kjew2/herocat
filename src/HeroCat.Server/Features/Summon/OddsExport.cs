using System.Text.Encodings.Web;
using System.Text.Json;
using HeroCat.Sim.Content;

namespace HeroCat.Server.Features.Summon;

/// <summary>One item's probability per pull at the level's table rate (no pity ramp, no guarantee): the Korea per-item odds.</summary>
public sealed record ItemOddsDto(string ItemId, string Rarity, double Probability);

/// <summary>One banner level of the odds export: the exact <c>/summon/rates</c> body, and its per-item odds.</summary>
public sealed record OddsExportLevel(SummonRatesResponse Published, ItemOddsDto[] ItemOdds);

/// <summary>
/// One banner of the odds export: the Japan figures (expected pulls to an L+ from <c>effective</c>, and what reaching
/// <c>PITY_HARD</c> costs in diamonds), and every level.
/// </summary>
public sealed record OddsExportBanner(string Banner, int PullCostDiamonds, double ExpectedPullsToLplus, int HardPity, long HardPityCostDiamonds,
                                      OddsExportLevel[] Levels);

/// <summary>The store listing's odds page data (specs/08 section 10.3): every banner level of one content set.</summary>
public sealed record OddsExportDocument(string ContentVersion, string ContentHash, OddsExportBanner[] Banners);

/// <summary>
/// The odds export (specs/08 sections 4.5 and 10.3; issue #200): <c>HeroCat.Server --export-odds [--out PATH]</c>
/// writes, for the newest held content set, every banner level's <c>/summon/rates</c> body (built by the same
/// <see cref="PublishedRates"/>, so the page and the endpoint cannot disagree) plus the per-item and Japan figures.
/// </summary>
public static class OddsExport
{
    public const string Flag = "--export-odds";
    public const string OutFlag = "--out";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static OddsExportDocument Build(ContentDb db)
    {
        var banners = new List<OddsExportBanner>();
        foreach (var def in db.Banners.Values.Where(d => d.Levels.Count > 0).OrderBy(d => d.SortOrder))
        {
            var levels = new List<OddsExportLevel>();
            for (int level = 1; level <= def.Levels.Count; level++)
            {
                var published = PublishedRates.Build(db, def, level);
                var odds = new List<ItemOddsDto>();
                foreach (var r in PublishedRates.Order)
                    if (def.Pool.TryGetValue(r, out var ids) && ids.Count > 0)
                        foreach (string id in ids) odds.Add(new ItemOddsDto(id, RarityCodes.Of(r), def.Levels[level - 1].Rates[r] / ids.Count));
                levels.Add(new OddsExportLevel(published, odds.ToArray()));
            }
            var effective = PublishedRates.Effective(def);
            banners.Add(new OddsExportBanner(def.Id.ToString(), def.PullCostDiamonds, effective.ExpectedPullsToLplus, def.PityHard,
                                             (long)def.PityHard * def.PullCostDiamonds, levels.ToArray()));
        }
        return new OddsExportDocument(db.Version, db.ContentHash, banners.ToArray());
    }

    public static string Serialize(OddsExportDocument doc) => JsonSerializer.Serialize(doc, Json) + "\n";

    /// <summary>Splits <c>--out PATH</c> (or <c>--out=PATH</c>) off the arguments; the rest go to the configuration.</summary>
    public static (string? outPath, string[] rest) ParseArgs(string[] args)
    {
        string? path = null;
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == OutFlag)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{OutFlag} needs a path");
                path = args[++i];
            }
            else if (args[i].StartsWith(OutFlag + "=", StringComparison.Ordinal)) path = args[i][(OutFlag.Length + 1)..];
            else rest.Add(args[i]);
        }
        return (path, rest.ToArray());
    }
}
