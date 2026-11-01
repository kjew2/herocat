namespace HeroCat.Server.Pipeline;

/// <summary>
/// Which client routes each gate covers (specs/08 section 1.1, 8.4). Routes are matched on the request path under the
/// <c>/v1</c> base, so a feature gets the right gates by its route alone. The gates apply to client routes only:
/// <c>/health</c> and the OpenAPI document are outside <c>/v1</c>, and <c>/v1/admin/*</c> is the internal admin API
/// (section 8.5), whose callers send no client headers and which must stay reachable to lift maintenance.
/// </summary>
public static class RouteRules
{
    public const string ApiBase = "/v1";
    public const string AdminBase = "/v1/admin";

    static bool Under(PathString path, string prefix) => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase);

    static bool Is(PathString path, string route) =>
        string.Equals(path.Value?.TrimEnd('/'), route, StringComparison.OrdinalIgnoreCase);

    public static bool IsClientRoute(PathString path) => Under(path, ApiBase) && !Under(path, AdminBase);

    public static bool IsAdminRoute(PathString path) => Under(path, AdminBase);

    /// <summary><c>X-Client-Version &lt; minClientVersion</c> -> 426 on every client route except <c>/config</c>, <c>/time</c>, <c>/auth/*</c>.</summary>
    public static bool ChecksClientVersion(PathString path) =>
        IsClientRoute(path) && !Under(path, "/v1/config") && !Under(path, "/v1/time") && !Under(path, "/v1/auth");

    /// <summary>Maintenance blocks every client route except <c>/config</c> and <c>/time</c>.</summary>
    public static bool BlockedByMaintenance(PathString path) =>
        IsClientRoute(path) && !Under(path, "/v1/config") && !Under(path, "/v1/time");

    /// <summary>The sim-sensitive routes that check <c>X-Content-Version</c>: <c>POST /summon/pull</c>, <c>POST /dungeon/seed</c>, <c>PUT /save</c>.</summary>
    public static bool ChecksContentVersion(string method, PathString path) =>
        (HttpMethods.IsPost(method) && Is(path, "/v1/summon/pull"))
        || (HttpMethods.IsPost(method) && Is(path, "/v1/dungeon/seed"))
        || (HttpMethods.IsPut(method) && Is(path, "/v1/save"));
}
