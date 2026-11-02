using StackExchange.Redis;

namespace HeroCat.Server.Pipeline;

/// <summary>
/// The gates every matched endpoint passes before its handler, in this order (specs/08 section 1.1, 7.4, 8.4):
/// maintenance (<c>503</c>), client version (<c>426</c>), auth policy (<c>401</c>/<c>403</c>, ban), content version
/// (<c>409</c>), rate limits (<c>429</c>). An unmatched route skips them all and ends as <c>404 NOT_FOUND</c>.
/// Redis trouble fails open (no maintenance, no limit) with a warning, so a cache outage does not take every route down.
/// </summary>
public sealed class ClientGateMiddleware
{
    readonly RequestDelegate _next;
    readonly TimeProvider _clock;
    readonly IMaintenanceFlag _maintenance;
    readonly IClientGateConfigSource _gates;
    readonly IAccessTokenValidator _tokens;
    readonly IAdminAuthenticator _admins;
    readonly IAccountStatusSource _accounts;
    readonly IRateLimitStore _limits;
    readonly ILogger<ClientGateMiddleware> _log;

    public ClientGateMiddleware(RequestDelegate next, TimeProvider clock, IMaintenanceFlag maintenance, IClientGateConfigSource gates,
                                IAccessTokenValidator tokens, IAdminAuthenticator admins, IAccountStatusSource accounts,
                                IRateLimitStore limits, ILogger<ClientGateMiddleware> log)
    {
        _next = next;
        _clock = clock;
        _maintenance = maintenance;
        _gates = gates;
        _tokens = tokens;
        _admins = admins;
        _accounts = accounts;
        _limits = limits;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var endpoint = ctx.GetEndpoint();
        if (endpoint == null)
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        var error = await CheckAsync(ctx, endpoint).ConfigureAwait(false);
        if (error != null)
        {
            await ApiErrorWriter.WriteAsync(ctx, error).ConfigureAwait(false);
            return;
        }
        await _next(ctx).ConfigureAwait(false);
    }

    async Task<ApiException?> CheckAsync(HttpContext ctx, Endpoint endpoint)
    {
        var path = ctx.Request.Path;
        if (RouteRules.BlockedByMaintenance(path))
        {
            var state = await FailOpen(() => _maintenance.GetAsync(ctx.RequestAborted), "maintenance flag").ConfigureAwait(false);
            if (state != null) return MaintenanceGate.Error(state, _clock.GetUtcNow().ToUnixTimeSeconds());
        }

        var error = await VersionGates.CheckClientVersionAsync(ctx, _gates).ConfigureAwait(false);
        if (error != null) return error;

        var policy = AuthPolicies.Of(endpoint, path);
        error = await AuthGate.CheckAsync(ctx, policy, _tokens, _admins, _accounts).ConfigureAwait(false);
        if (error != null) return error;

        error = await VersionGates.CheckContentVersionAsync(ctx, _gates).ConfigureAwait(false);
        if (error != null) return error;

        var rules = RateLimitGate.RulesOf(endpoint);
        if (rules.Count > 0)
        {
            var remote = (await _gates.GetAsync(ctx.RequestAborted).ConfigureAwait(false)).Limits;
            if (remote != null) rules = remote.Apply(RateLimitGate.RouteOf(ctx), rules);
            string key = RateLimitGate.KeyFor(ctx, policy);
            long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            var decision = await FailOpen<RateLimitDecision>(async () => await _limits.AcquireAsync(key, rules, now, ctx.RequestAborted).ConfigureAwait(false), "rate limit")
                .ConfigureAwait(false) ?? RateLimitDecision.Allow;
            if (!decision.Allowed) return ApiException.Of(ErrorCodes.RATE_LIMITED, retryAfterSec: decision.RetryAfterSec);
        }
        return null;
    }

    async Task<T?> FailOpen<T>(Func<Task<T?>> read, string what) where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            _log.LogWarning(e, "{What} unavailable; failing open", what);
            return null;
        }
    }
}
