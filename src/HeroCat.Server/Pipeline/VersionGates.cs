namespace HeroCat.Server.Pipeline;

/// <summary>
/// The version floors of the active config (specs/08 section 6.1): <c>minClientVersion</c> and
/// <c>minContentVersionByClient</c>. A null or empty value gates nothing.
/// </summary>
public sealed record ClientGateConfig(string? MinClientVersion, IReadOnlyDictionary<string, string> MinContentVersionByClient)
{
    public static readonly ClientGateConfig None = new(null, new Dictionary<string, string>());

    /// <summary>
    /// The content floor for a client: the entry for its version, else the entry for the highest listed version below it
    /// (a patch release the config does not list inherits its predecessor's floor), else none.
    /// </summary>
    public ContentVersion? MinContentVersionFor(ClientVersion client)
    {
        ClientVersion? best = null;
        ContentVersion? floor = null;
        foreach (var (key, value) in MinContentVersionByClient)
        {
            if (!ClientVersion.TryParse(key, out var v) || v > client) continue;
            if (best != null && v <= best) continue;
            if (!ContentVersion.TryParse(value, out var c)) continue;
            best = v;
            floor = c;
        }
        return floor;
    }
}

/// <summary>Where the gates read the active config's version floors.</summary>
public interface IClientGateConfigSource
{
    ValueTask<ClientGateConfig> GetAsync(CancellationToken ct);
}

/// <summary>Gates nothing. The api's default until the Config feature serves the active <c>config_versions</c> payload.</summary>
public sealed class UngatedClientGateConfigSource : IClientGateConfigSource
{
    // TODO(#204): the Config feature registers an IClientGateConfigSource over the active config_versions payload.
    public ValueTask<ClientGateConfig> GetAsync(CancellationToken ct) => ValueTask.FromResult(ClientGateConfig.None);
}

/// <summary>
/// The version gates of specs/08 section 1.1. On a matched client route: a missing or unparseable
/// <c>X-Client-Version</c> is <c>400 VALIDATION</c>, one below <c>minClientVersion</c> is <c>426 CLIENT_TOO_OLD</c>
/// (except <c>/config</c>, <c>/time</c>, <c>/auth/*</c>); on the sim-sensitive routes a missing or unparseable
/// <c>X-Content-Version</c> is <c>400 VALIDATION</c> and one below the client's <c>minContentVersionByClient</c> floor is
/// <c>409 CONTENT_TOO_OLD</c>.
/// </summary>
public static class VersionGates
{
    public static async Task<ApiException?> CheckClientVersionAsync(HttpContext ctx, IClientGateConfigSource source)
    {
        if (!RouteRules.ChecksClientVersion(ctx.Request.Path)) return null;
        if (!ClientVersion.TryParse(ctx.Request.Headers[ApiHeaders.ClientVersion], out var client))
            return MissingHeader(ApiHeaders.ClientVersion);
        var config = await source.GetAsync(ctx.RequestAborted).ConfigureAwait(false);
        if (ClientVersion.TryParse(config.MinClientVersion, out var min) && client < min)
            return ApiException.Of(ErrorCodes.CLIENT_TOO_OLD, details: new { minClientVersion = config.MinClientVersion });
        return null;
    }

    public static async Task<ApiException?> CheckContentVersionAsync(HttpContext ctx, IClientGateConfigSource source)
    {
        if (!RouteRules.ChecksContentVersion(ctx.Request.Method, ctx.Request.Path)) return null;
        if (!ClientVersion.TryParse(ctx.Request.Headers[ApiHeaders.ClientVersion], out var client))
            return MissingHeader(ApiHeaders.ClientVersion);
        if (!ContentVersion.TryParse(ctx.Request.Headers[ApiHeaders.ContentVersion], out var content))
            return MissingHeader(ApiHeaders.ContentVersion);
        var config = await source.GetAsync(ctx.RequestAborted).ConfigureAwait(false);
        var floor = config.MinContentVersionFor(client);
        if (floor != null && content < floor)
            return ApiException.Of(ErrorCodes.CONTENT_TOO_OLD, details: new { minContentVersion = Format(floor) });
        return null;
    }

    static string Format(ContentVersion v) =>
        v.N == 0 ? $"{v.Year:D4}.{v.Month:D2}.{v.Day:D2}" : $"{v.Year:D4}.{v.Month:D2}.{v.Day:D2}.{v.N}";

    static ApiException MissingHeader(string header) =>
        ApiException.Of(ErrorCodes.VALIDATION, $"Missing or malformed {header} header.", new { header });
}
