using Microsoft.AspNetCore.Mvc;

namespace HeroCat.Server.Health;

/// <summary>
/// <c>GET /ready</c>: dependency readiness (specs/08 section 9.1, issue #319). 200 with <c>status: ready</c> when Postgres
/// and Redis answer, else 503 with <c>status: not_ready</c> and which check failed. The deploy pipeline's gate before a new
/// task set takes traffic (specs/10 section 6.2), never the load balancer's liveness check, which stays <c>/health</c> so
/// a dependency outage does not drain every task. Outside <c>/v1</c>, no auth, like <c>/health</c>.
/// </summary>
[ApiController]
[Route("ready")]
public sealed class ReadyController : ControllerBase
{
    readonly ReadinessProbe _probe;

    public ReadyController(ReadinessProbe probe) => _probe = probe;

    [HttpGet]
    [ProducesResponseType(typeof(ReadyBody), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ReadyBody), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ReadyBody>> Get()
    {
        var body = await _probe.CheckAsync().ConfigureAwait(false);
        return body.IsReady ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
