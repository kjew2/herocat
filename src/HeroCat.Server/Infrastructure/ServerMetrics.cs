using System.Diagnostics.Metrics;

namespace HeroCat.Server.Infrastructure;

/// <summary>
/// The server's one <see cref="Meter"/> (specs/08 section 9.4). Instruments are named as the spec and alarms name them
/// (<c>redis_lock_wait_seconds</c>); <see cref="Observability.MetricCatalog"/> lists them and <see cref="Observability.TelemetrySetup"/> exports them.
/// </summary>
public static class ServerMetrics
{
    public const string MeterName = "HeroCat.Server";
    public static readonly Meter Meter = new(MeterName);
}
