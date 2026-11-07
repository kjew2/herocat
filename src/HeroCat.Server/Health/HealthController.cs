using HeroCat.Server.Hosting;
using HeroCat.Sim;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Health;

/// <summary>
/// <c>GET /health</c>: liveness for the load balancer, and the version the compat matrix reads (specs/10 section 6.3:
/// "image tag, /health body"). Outside <c>/v1</c> (it is not a client route) and touches no database, so a Postgres or
/// Redis outage does not take every task out of the target group; dependency readiness is <see cref="ReadyController"/>.
/// The <c>sim</c> block calls into the referenced HeroCat.Sim assembly, so a server built against a broken Sim fails here.
/// </summary>
[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    static readonly string EmptyStateHash = StateHasher.Hash(new GameState());

    readonly ServerOptions _options;
    readonly ServerRoleInfo _role;

    public HealthController(IOptions<ServerOptions> options, ServerRoleInfo role)
    {
        _options = options.Value;
        _role = role;
    }

    [HttpGet]
    public ActionResult<HealthBody> Get() => new HealthBody(
        "ok",
        _options.Version,
        _role.Name,
        new SimInfo(SimVersion, GameState.CurrentSchemaVersion, EmptyStateHash));

    public static string SimVersion => typeof(Simulation).Assembly.GetName().Version?.ToString() ?? "unknown";
}

/// <param name="Sim">The Sim build: assembly version, save schema version, and the StateHash of a new GameState
/// (a fingerprint of the canonical state encoding the re-simulator and the client must share, 00 contract 12).</param>
public sealed record HealthBody(string Status, string Version, string Role, SimInfo Sim);

public sealed record SimInfo(string Version, int SaveSchemaVersion, string NewStateHash);
