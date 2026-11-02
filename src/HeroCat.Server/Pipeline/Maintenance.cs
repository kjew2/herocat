using System.Text.Json;
using System.Text.Json.Serialization;
using HeroCat.Server.Infrastructure.Redis;

namespace HeroCat.Server.Pipeline;

/// <summary>
/// The value of the Redis <c>maint</c> key (specs/02 section 12 "maintenance JSON", 08 section 8.4): what
/// <c>POST /admin/maintenance</c> stores, <c>{ "active": true, "messageKey": "maint.db_upgrade", "until": 1793680000 }</c>.
/// </summary>
public sealed record MaintenanceState(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("messageKey")] string? MessageKey,
    [property: JsonPropertyName("until")] long? Until);

/// <summary>Reads whether maintenance is on (null means off), and sets or clears it (<c>POST /admin/maintenance</c>, section 8.4).</summary>
public interface IMaintenanceFlag
{
    Task<MaintenanceState?> GetAsync(CancellationToken ct);

    /// <summary>Stores an active state, or clears the flag for null or <c>active: false</c>.</summary>
    Task SetAsync(MaintenanceState? state, CancellationToken ct);
}

/// <summary>A process-local flag: tests, and a local run without Redis.</summary>
public sealed class InMemoryMaintenanceFlag : IMaintenanceFlag
{
    volatile MaintenanceState? _state;

    public void Set(MaintenanceState? state) => _state = state;

    public Task SetAsync(MaintenanceState? state, CancellationToken ct)
    {
        Set(state is { Active: true } ? state : null);
        return Task.CompletedTask;
    }

    public Task<MaintenanceState?> GetAsync(CancellationToken ct) =>
        Task.FromResult(_state is { Active: true } s ? s : null);
}

/// <summary>
/// The Redis <c>maint</c> key. Maintenance is on while the key holds a value whose <c>active</c> is not false (an absent
/// <c>active</c> counts as on: the spec's rule is "when <c>maint</c> is set"). An unreadable value is treated as on with
/// no message, so a botched write never silently reopens the routes.
/// </summary>
public sealed class RedisMaintenanceFlag : IMaintenanceFlag
{
    public const string Key = RedisKeys.Maintenance;

    readonly IRedisConnection _redis;

    public RedisMaintenanceFlag(IRedisConnection redis) => _redis = redis;

    public async Task<MaintenanceState?> GetAsync(CancellationToken ct)
    {
        var db = await _redis.DatabaseAsync().ConfigureAwait(false);
        var value = await db.StringGetAsync(Key).ConfigureAwait(false);
        return Parse(value.HasValue ? value.ToString() : null);
    }

    public async Task SetAsync(MaintenanceState? state, CancellationToken ct)
    {
        var db = await _redis.DatabaseAsync().ConfigureAwait(false);
        if (state is not { Active: true }) await db.KeyDeleteAsync(Key).ConfigureAwait(false);
        else await db.StringSetAsync(Key, JsonSerializer.Serialize(state)).ConfigureAwait(false);
    }

    public static MaintenanceState? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new MaintenanceState(true, null, null);
            var root = doc.RootElement;
            bool active = !root.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False;
            if (!active) return null;
            string? key = root.TryGetProperty("messageKey", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            long? until = root.TryGetProperty("until", out var u) && u.ValueKind == JsonValueKind.Number && u.TryGetInt64(out long t) ? t : null;
            return new MaintenanceState(true, key, until);
        }
        catch (JsonException)
        {
            return new MaintenanceState(true, null, null);
        }
    }
}

public static class MaintenanceGate
{
    /// <summary>
    /// <c>503 MAINTENANCE</c> with <c>details: { messageKey, until }</c> and, while <c>until</c> is ahead, <c>retryAfterSec</c>
    /// until then (the client polls <c>/config</c> every <c>MAINT_POLL_S</c> regardless; 01 section 5.2).
    /// </summary>
    public static ApiException Error(MaintenanceState state, long nowSec)
    {
        int? retry = state.Until is long until && until > nowSec ? (int)Math.Min(until - nowSec, int.MaxValue) : null;
        return ApiException.Of(ErrorCodes.MAINTENANCE, details: new { messageKey = state.MessageKey, until = state.Until }, retryAfterSec: retry);
    }
}
