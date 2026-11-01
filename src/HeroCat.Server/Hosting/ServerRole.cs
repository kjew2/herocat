namespace HeroCat.Server.Hosting;

/// <summary>
/// What this process runs (specs/08 section 9.1: one image, roles by command-line flag).
/// <list type="bullet">
/// <item><c>api</c> (no flag): the stateless HTTP API, horizontally scaled.</item>
/// <item><c>--worker</c>: background jobs (mail blasts, replays, purges, refund polls); no HTTP. One instance, leader lock in Redis (#208).</item>
/// <item><c>--migrate</c>: applies the SQL migration set and exits (the deploy pipeline's pre-deploy job, and CI).</item>
/// </list>
/// </summary>
public enum ServerRole
{
    Api,
    Worker,
    Migrate,
}

public static class ServerRoles
{
    public const string WorkerFlag = "--worker";
    public const string MigrateFlag = "--migrate";

    /// <summary>Splits the role flag off; the remaining arguments go to the configuration (<c>--HeroCat:Version=...</c>).</summary>
    public static (ServerRole role, string[] rest) Parse(string[] args)
    {
        var role = ServerRole.Api;
        var rest = new List<string>();
        int flags = 0;
        foreach (string a in args)
        {
            if (a == WorkerFlag) { role = ServerRole.Worker; flags++; }
            else if (a == MigrateFlag) { role = ServerRole.Migrate; flags++; }
            else if (a == "api") { flags++; }
            else rest.Add(a);
        }
        if (flags > 1) throw new ArgumentException($"give at most one role: api (default), {WorkerFlag} or {MigrateFlag}");
        return (role, rest.ToArray());
    }

    public static string Name(ServerRole role) => role switch
    {
        ServerRole.Api => "api",
        ServerRole.Worker => "worker",
        ServerRole.Migrate => "migrate",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

/// <summary>The role this process was started in, for <c>/health</c> and logs.</summary>
public sealed record ServerRoleInfo(ServerRole Role)
{
    public string Name => ServerRoles.Name(Role);
}
