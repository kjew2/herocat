using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HeroCat.Server.Hosting;
using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HeroCat.Server.Features.Config;

/// <summary><c>GET /config</c> (specs/08 section 3.4, 6.1; issue #204).</summary>
[ApiController]
public sealed class ConfigController : ControllerBase
{
    readonly IActiveConfigSource _config;
    readonly IMaintenanceFlag _maintenance;
    readonly TimeProvider _clock;
    readonly ILogger<ConfigController> _log;

    public ConfigController(IActiveConfigSource config, IMaintenanceFlag maintenance, TimeProvider clock, ILogger<ConfigController> log)
    {
        _config = config;
        _maintenance = maintenance;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// The env's active config with the rollout bucket applied for a valid JWT (without one: the non-rollout default
    /// content and no <c>inBucket</c>). Answers during maintenance, with the live flag in <c>maintenance</c>. No active
    /// config is <c>404 NOT_FOUND</c> (the client keeps its cached config or defaults).
    /// </summary>
    [HttpGet("v1/config"), Auth(AuthPolicy.None), RateLimit(30)]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var active = await _config.GetActiveAsync(ct) ?? throw ApiException.Of(ErrorCodes.NOT_FOUND, "No config is active.");
        MaintenanceState? maint = null;
        try { maint = await _maintenance.GetAsync(ct); }
        catch (Exception e) when (e is RedisException or TimeoutException) { _log.LogWarning(e, "maintenance flag unavailable"); }

        var body = ConfigResponse.Build(active, HttpContext.PlayerId(), maint, _clock.GetUtcNow().ToUnixTimeSeconds());
        Response.Headers.CacheControl = "max-age=60";
        return Content(body.ToJsonString(), "application/json; charset=utf-8");
    }
}

/// <summary>
/// Config publishing and maintenance on the internal admin API (specs/08 section 6.2, 8.4, 8.5): <c>POST /admin/config</c>,
/// <c>POST /admin/config/{id}/activate</c>, <c>POST /admin/maintenance</c>.
/// </summary>
[ApiController, Auth(AuthPolicy.Admin)]
public sealed class AdminConfigController : ControllerBase
{
    readonly IConfigStore _store;
    readonly IActiveConfigSource _config;
    readonly ContentManifestVerifier _verifier;
    readonly IMaintenanceFlag _maintenance;
    readonly TimeProvider _clock;
    readonly string _env;
    readonly ILogger<AdminConfigController> _log;

    public AdminConfigController(IConfigStore store, IActiveConfigSource config, ContentManifestVerifier verifier, IMaintenanceFlag maintenance,
                                 TimeProvider clock, IOptions<ServerOptions> options, ILogger<AdminConfigController> log)
    {
        _store = store;
        _config = config;
        _verifier = verifier;
        _maintenance = maintenance;
        _clock = clock;
        _env = options.Value.Env;
        _log = log;
    }

    string Actor => User.FindFirst(Caller.SubClaim)?.Value ?? "unknown";

    /// <summary>Stores the payload as a new, inactive <c>config_versions</c> row for this env (<c>created_by</c> is the SSO actor).</summary>
    [HttpPost("v1/admin/config")]
    [ProducesResponseType(typeof(ConfigVersionDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Publish([FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object) throw ApiException.Of(ErrorCodes.VALIDATION, "The payload must be a JSON object.");
        var payload = JsonObject.Create(body)!;
        payload = (JsonObject)payload.DeepClone();
        var columns = ConfigPayload.Normalise(payload);
        int id = await _store.InsertAsync(_env, payload, columns, Actor, _clock.GetUtcNow(), ct);
        _log.LogInformation("config {Id} published for {Env} by {Actor} (content {Content}, rollout {Pct}%)", id, _env, Actor, columns.ContentVersion, columns.RolloutPct);
        return StatusCode(StatusCodes.Status201Created, new ConfigVersionDto(id, _env, columns.ContentVersion, columns.RolloutPct, columns.MinClientVersion, false));
    }

    /// <summary>
    /// Makes the row the env's active config after checking its content manifests' <c>sha256</c> on the CDN
    /// (<c>400 CONTENT_HASH_MISMATCH</c>, leaving the previous config active). Rollback is activating the previous id.
    /// </summary>
    [HttpPost("v1/admin/config/{id:int}/activate")]
    [ProducesResponseType(typeof(ConfigVersionDto), StatusCodes.Status200OK)]
    public async Task<ConfigVersionDto> Activate(int id, CancellationToken ct)
    {
        var row = await _store.GetAsync(_env, id, ct) ?? throw ApiException.Of(ErrorCodes.NOT_FOUND, $"No config {id} in {_env}.");
        var mismatch = await _verifier.VerifyAsync(row.Payload, ct);
        if (mismatch != null) throw mismatch;
        if (!await _store.ActivateAsync(_env, id, ct)) throw ApiException.Of(ErrorCodes.NOT_FOUND, $"No config {id} in {_env}.");
        _config.Invalidate();
        var columns = ConfigPayload.Normalise((JsonObject)row.Payload.DeepClone());
        _log.LogInformation("config {Id} activated for {Env} by {Actor}", id, _env, Actor);
        return new ConfigVersionDto(id, _env, columns.ContentVersion, columns.RolloutPct, columns.MinClientVersion, true);
    }

    /// <summary>Sets the Redis <c>maint</c> key (<c>active: true</c>) or clears it (section 8.4).</summary>
    [HttpPost("v1/admin/maintenance")]
    [ProducesResponseType(typeof(MaintenanceState), StatusCodes.Status200OK)]
    public async Task<MaintenanceState> Maintenance(MaintenanceRequest request, CancellationToken ct)
    {
        var state = new MaintenanceState(request.Active, request.MessageKey, request.Until);
        await _maintenance.SetAsync(state, ct);
        _log.LogInformation("maintenance {State} by {Actor} (messageKey {Key}, until {Until})", request.Active ? "on" : "off", Actor, request.MessageKey, request.Until);
        return state;
    }
}

public sealed record ConfigVersionDto(int Id, string Env, string ContentVersion, int RolloutPct, string MinClientVersion, bool Active);

public sealed class MaintenanceRequest
{
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("messageKey")] public string? MessageKey { get; set; }
    [JsonPropertyName("until")] public long? Until { get; set; }
}
