namespace HeroCat.Server.Pipeline;

/// <summary>A plausibility flag (specs/08 section 7.2 table: code and severity) raised against a player.</summary>
public sealed record PlausibilityFlag(Guid PlayerId, string Code, string Severity, object? Details = null)
{
    public const string DuplicateRequestId = "DUPLICATE_REQUEST_ID";
    public const string Certain = "certain";
    public const string Suspicious = "suspicious";
}

/// <summary>
/// Where request-time flags go (the idempotency store's <c>DUPLICATE_REQUEST_ID</c>, issue #194). The anti-cheat module
/// owns what a flag leads to (section 7.4's action matrix).
/// </summary>
public interface IPlausibilityFlagSink
{
    Task RaiseAsync(PlausibilityFlag flag, CancellationToken ct);
}

/// <summary>Logs the flag (with the player id hashed, section 9.4): the default until the action matrix consumes flags.</summary>
public sealed class LoggingPlausibilityFlagSink : IPlausibilityFlagSink
{
    readonly ILogger<LoggingPlausibilityFlagSink> _log;

    public LoggingPlausibilityFlagSink(ILogger<LoggingPlausibilityFlagSink> log) => _log = log;

    // TODO(#206): hand flags to the action matrix (moderation_actions, bans) instead of only logging them.
    public Task RaiseAsync(PlausibilityFlag flag, CancellationToken ct)
    {
        _log.LogWarning("plausibility flag {FlagCode} ({Severity}) on player {PlayerIdHash}", flag.Code, flag.Severity,
                        Observability.LogFields.PlayerIdHash(flag.PlayerId));
        return Task.CompletedTask;
    }
}
