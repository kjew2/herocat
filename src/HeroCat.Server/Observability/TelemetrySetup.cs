using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace HeroCat.Server.Observability;

/// <summary>
/// Logs, metrics and traces for a role (specs/08 section 9.4, issue #210): structured JSON logs on stdout for every
/// role; for the api and the worker, the <see cref="ServerMetrics.Meter"/> on a loopback Prometheus endpoint and traces
/// over OTLP to the collector.
/// </summary>
public static class TelemetrySetup
{
    public const string ServiceName = "herocat-server";

    /// <summary>Histogram buckets in seconds; 0.3, 0.5 and 0.8 are the load-test and alarm latency lines (specs/10).</summary>
    public static readonly double[] SecondsBuckets = { 0.005, 0.01, 0.025, 0.05, 0.1, 0.2, 0.3, 0.5, 0.8, 1, 2, 5, 10, 30, 60, 300 };

    /// <summary>Save sizes up to <c>SAVE_BLOB_MAX_BYTES</c> (262144).</summary>
    public static readonly double[] SaveBytesBuckets = { 1024, 4096, 8192, 16384, 32768, 65536, 131072, 262144 };

    public static IHostApplicationBuilder AddHeroCatTelemetry(this IHostApplicationBuilder builder, ServerRole role)
    {
        builder.Logging.ClearProviders();
        builder.Services.TryAddSingleton<ILogSink, StdoutLogSink>();
        builder.Services.AddSingleton<ILoggerProvider, StructuredLoggerProvider>();
        if (role == ServerRole.Migrate) return builder;

        var section = builder.Configuration.GetSection(TelemetryOptions.Section);
        builder.Services.Configure<TelemetryOptions>(section);
        var o = section.Get<TelemetryOptions>() ?? new TelemetryOptions();
        MetricCatalog.EnsureRegistered();

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: builder.Configuration["HeroCat:Version"])
                                     .AddAttributes(new KeyValuePair<string, object>[]
                                     {
                                         new("herocat.role", ServerRoles.Name(role)),
                                         new("deployment.environment", builder.Configuration["HeroCat:Env"] ?? ""),
                                     }))
            .WithMetrics(m =>
            {
                m.AddMeter(ServerMetrics.MeterName);
                foreach (var def in MetricCatalog.All.Where(d => d.Kind == MetricKind.Histogram))
                    m.AddView(def.Name, new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = def.Name == SaveMetrics.UploadBytes ? SaveBytesBuckets : SecondsBuckets,
                    });
                if (o.MetricsPort > 0)
                    m.AddPrometheusHttpListener(l => l.UriPrefixes = new[] { MetricsPrefix(o.MetricsPort) });
            })
            .WithTracing(t =>
            {
                t.SetSampler(new ErrorKeepingSampler(o.TraceSamplePct / 100.0));
                if (role == ServerRole.Api) t.AddAspNetCoreInstrumentation(a => a.RecordException = true);
                if (!string.IsNullOrWhiteSpace(o.OtlpEndpoint)) t.AddOtlpExporter(x => x.Endpoint = new Uri(o.OtlpEndpoint));
            });
        return builder;
    }

    /// <summary>The scrape endpoint's listener prefix: loopback only, never the load balancer's interface.</summary>
    public static string MetricsPrefix(int port) => $"http://127.0.0.1:{port}/";
}
