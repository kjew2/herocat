using HeroCat.Sim.Content;

namespace HeroCat.Server.Features.Summon;

/// <summary>One <c>config.banners[]</c> window (specs/08 sections 6.1, 8.1), in unix seconds; <c>OpenUntil</c> null is open-ended.</summary>
public sealed record BannerWindow(Banner Banner, long OpenFrom, long? OpenUntil);

/// <summary>
/// The parts of the active config the summon handler reads: <c>killSwitches.summonEnabled</c> and the
/// <c>banners[]</c> schedule. As built (issue #199): a banner missing from <c>summonEnabled</c> is enabled, and a banner
/// with no window in <c>banners[]</c> is open (the schedule only restricts the banners it lists); content's
/// <c>banners.enabled</c> still closes a banner.
/// </summary>
public sealed record SummonSwitches(IReadOnlyDictionary<Banner, bool> SummonEnabled, IReadOnlyList<BannerWindow> Banners)
{
    public static readonly SummonSwitches AllOpen = new(new Dictionary<Banner, bool>(), Array.Empty<BannerWindow>());

    public bool IsEnabled(Banner b) => !SummonEnabled.TryGetValue(b, out bool on) || on;

    public bool IsOpen(Banner b, long nowUnix)
    {
        bool listed = false;
        foreach (var w in Banners)
        {
            if (w.Banner != b) continue;
            listed = true;
            if (w.OpenFrom <= nowUnix && (w.OpenUntil == null || nowUnix < w.OpenUntil)) return true;
        }
        return !listed;
    }
}

/// <summary>Where the summon handler reads the active config's switches. The Config feature replaces the default.</summary>
public interface ISummonConfigSource
{
    ValueTask<SummonSwitches> GetAsync(CancellationToken ct);
}

/// <summary>Every banner enabled and open: the api's default until the Config feature serves the active payload.</summary>
public sealed class OpenSummonConfigSource : ISummonConfigSource
{
    // The Config feature registers ActiveConfigSummonSource over the active payload; this stays for hosts without it.
    public ValueTask<SummonSwitches> GetAsync(CancellationToken ct) => ValueTask.FromResult(SummonSwitches.AllOpen);
}
