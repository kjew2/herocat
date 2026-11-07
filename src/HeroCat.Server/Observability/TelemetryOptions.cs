namespace HeroCat.Server.Observability;

/// <summary>
/// <c>HeroCat:Telemetry</c> (specs/08 section 9.4, issue #210): where metrics and traces go. Bound on its own, beside
/// <see cref="Hosting.ServerOptions"/>.
/// </summary>
public sealed class TelemetryOptions
{
    public const string Section = "HeroCat:Telemetry";

    /// <summary>
    /// The loopback port of the Prometheus text endpoint (<c>http://127.0.0.1:{port}/metrics</c>) that the collector
    /// sidecar in the same task scrapes. Every role but <c>--migrate</c> opens it; 0 turns it off.
    /// </summary>
    public int MetricsPort { get; set; } = 9464;

    /// <summary>The OTLP endpoint of the collector traces are pushed to (<c>http://127.0.0.1:4317</c>). Empty: no trace export.</summary>
    public string OtlpEndpoint { get; set; } = "";

    /// <summary>Head-sampled share of traces, in percent (section 9.4: 10). A trace whose request ends 5xx is always kept.</summary>
    public double TraceSamplePct { get; set; } = 10;
}
