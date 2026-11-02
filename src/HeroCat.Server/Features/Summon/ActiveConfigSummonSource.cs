using System.Text.Json.Nodes;
using HeroCat.Server.Features.Config;
using HeroCat.Sim.Content;

namespace HeroCat.Server.Features.Summon;

/// <summary>
/// The summon handler's switches from the active config (specs/08 section 6.1): <c>killSwitches.summonEnabled</c> and the
/// <c>banners[]</c> schedule. It reads the Config feature's cache, which reloads every <c>CONFIG_REFRESH_S</c>, so a kill
/// switch reaches every instance within acceptance 17's 60 s. With no active config every banner is enabled and open.
/// </summary>
public sealed class ActiveConfigSummonSource : ISummonConfigSource
{
    readonly IActiveConfigSource _active;

    public ActiveConfigSummonSource(IActiveConfigSource active) { _active = active; }

    public async ValueTask<SummonSwitches> GetAsync(CancellationToken ct)
    {
        var config = await _active.GetActiveAsync(ct);
        return config == null ? SummonSwitches.AllOpen : From(config);
    }

    public static SummonSwitches From(ActiveConfig config)
    {
        var enabled = new Dictionary<Banner, bool>();
        foreach (Banner b in Enum.GetValues<Banner>())
            enabled[b] = config.SummonEnabled(b.ToString());

        var windows = new List<BannerWindow>();
        if (config.Payload["banners"] is JsonArray rows)
            foreach (var row in rows)
            {
                if (row is not JsonObject o) continue;
                if (o["banner"] is not JsonValue name || !name.TryGetValue<string>(out var s) || !Enum.TryParse(s, out Banner banner)) continue;
                long from = o["openFrom"] is JsonValue f && f.TryGetValue<long>(out long fv) ? fv : 0;
                long? until = o["openUntil"] is JsonValue u && u.TryGetValue<long>(out long uv) ? uv : null;
                windows.Add(new BannerWindow(banner, from, until));
            }
        return new SummonSwitches(enabled, windows);
    }
}
