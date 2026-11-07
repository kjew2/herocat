using System.Data;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HeroCat.Server.Features.Config;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Infrastructure;
using HeroCat.Server.Infrastructure.Redis;
using HeroCat.Server.Worker;

namespace HeroCat.Server.Observability;

public enum MetricKind { Counter, Histogram, Gauge }

/// <summary>Who produces a metric's values.</summary>
public enum MetricSource
{
    /// <summary>The server records it.</summary>
    Emitted,

    /// <summary>The instrument is registered, but the feature that records it does not exist yet (see <see cref="MetricDef.Note"/>).</summary>
    NoEmitterYet,

    /// <summary>Computed in the metrics store from other metrics (a recording rule in <c>server/ops/alarms.json</c>), never by the server.</summary>
    Derived,
}

/// <summary>One row of the specs/08 section 9.4 metrics table.</summary>
public sealed record MetricDef(string Name, MetricKind Kind, string[] Labels, MetricSource Source, string Note = "");

/// <summary>
/// The specs/08 section 9.4 metrics table as code. A test diffs it against the spec table, checks every non-derived
/// row is an instrument on <see cref="ServerMetrics.Meter"/>, and scrapes each from the Prometheus endpoint.
/// </summary>
public static class MetricCatalog
{
    public static readonly IReadOnlyList<MetricDef> All = new MetricDef[]
    {
        new(HttpMetrics.RequestsTotal, MetricKind.Counter, new[] { "route", "status" }, MetricSource.Emitted),
        new(HttpMetrics.DurationSeconds, MetricKind.Histogram, new[] { "route" }, MetricSource.Emitted),
        new(SummonMetrics.PullsTotal, MetricKind.Counter, new[] { "banner", "rarity" }, MetricSource.Emitted),
        new("summon_lplus_rate_5m", MetricKind.Gauge, new[] { "banner" }, MetricSource.Derived, "recording rule over summon_pulls_total"),
        new(IapMetrics.ValidateTotal, MetricKind.Counter, new[] { "platform", "status" }, MetricSource.NoEmitterYet, "POST /v1/iap/validate, issue #202"),
        new(IapMetrics.WebhookTotal, MetricKind.Counter, new[] { "platform", "type" }, MetricSource.NoEmitterYet, "store webhooks, issue #203"),
        new(SaveMetrics.UploadBytes, MetricKind.Histogram, Array.Empty<string>(), MetricSource.Emitted),
        new(SaveMetrics.FlagsTotal, MetricKind.Counter, new[] { "flag", "severity" }, MetricSource.Emitted),
        new(ModerationMetrics.ActionsTotal, MetricKind.Counter, new[] { "action" }, MetricSource.NoEmitterYet, "the action matrix, issue #206"),
        new(IdempotencyMetrics.ReplaysTotal, MetricKind.Counter, new[] { "route" }, MetricSource.Emitted),
        new(DbMetrics.PoolInUse, MetricKind.Gauge, Array.Empty<string>(), MetricSource.Emitted),
        new(DbMetrics.QueryDurationSeconds, MetricKind.Histogram, new[] { "name" }, MetricSource.NoEmitterYet, "stores do not name their queries yet; TODO(gap: db query names)"),
        new(LockMetrics.WaitSeconds, MetricKind.Histogram, Array.Empty<string>(), MetricSource.Emitted),
        new(WorkerJobMetrics.DurationSeconds, MetricKind.Histogram, new[] { "job" }, MetricSource.Emitted),
        new(WorkerJobMetrics.FailuresTotal, MetricKind.Counter, new[] { "job" }, MetricSource.Emitted),
        new(PartitionMaintenanceService.HorizonMetric, MetricKind.Gauge, new[] { "table" }, MetricSource.Emitted),
        new(ConfigMetrics.ActiveVersion, MetricKind.Gauge, Array.Empty<string>(), MetricSource.Emitted),
        new(ConfigMetrics.ContentActiveVersionInfo, MetricKind.Gauge, new[] { "version" }, MetricSource.Emitted),
    };

    /// <summary>Creates every instrument now, so one with no emitter yet is still registered from start-up.</summary>
    public static void EnsureRegistered()
    {
        foreach (var t in new[]
                 {
                     typeof(HttpMetrics), typeof(SummonMetrics), typeof(IapMetrics), typeof(SaveMetrics), typeof(ModerationMetrics),
                     typeof(IdempotencyMetrics), typeof(DbMetrics), typeof(LockMetrics), typeof(WorkerJobMetrics),
                     typeof(PartitionMaintenanceService), typeof(ConfigMetrics),
                 })
            RuntimeHelpers.RunClassConstructor(t.TypeHandle);
    }
}

/// <summary><c>http_requests_total{route,status}</c> and <c>http_request_duration_seconds{route}</c>, recorded by <see cref="RequestTelemetryMiddleware"/>.</summary>
public static class HttpMetrics
{
    public const string RequestsTotal = "http_requests_total";
    public const string DurationSeconds = "http_request_duration_seconds";

    static readonly Counter<long> Requests = ServerMetrics.Meter.CreateCounter<long>(RequestsTotal, null, "Requests by route template and status.");
    static readonly Histogram<double> Duration = ServerMetrics.Meter.CreateHistogram<double>(DurationSeconds, "s", "Request latency by route template.");

    public static void Record(string route, int status, double seconds)
    {
        Requests.Add(1, new KeyValuePair<string, object?>("route", route), new KeyValuePair<string, object?>("status", status.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Duration.Record(seconds, new KeyValuePair<string, object?>("route", route));
    }
}

/// <summary>IAP metrics. Registered with no emitter until the IAP feature lands (issues #202, #203).</summary>
public static class IapMetrics
{
    public const string ValidateTotal = "iap_validate_total";
    public const string WebhookTotal = "iap_webhook_total";

    static readonly Counter<long> Validate = ServerMetrics.Meter.CreateCounter<long>(ValidateTotal, null, "Receipt validations by platform and result status.");
    static readonly Counter<long> Webhook = ServerMetrics.Meter.CreateCounter<long>(WebhookTotal, null, "Store notifications by platform and type.");

    // TODO(#202): call from POST /v1/iap/validate.
    public static void RecordValidate(string platform, string status) =>
        Validate.Add(1, new KeyValuePair<string, object?>("platform", platform), new KeyValuePair<string, object?>("status", status));

    // TODO(#203): call from the App Store and Play webhooks (type REFUND drives the refund spike alarm).
    public static void RecordWebhook(string platform, string type) =>
        Webhook.Add(1, new KeyValuePair<string, object?>("platform", platform), new KeyValuePair<string, object?>("type", type));
}

/// <summary><c>save_upload_bytes</c> and <c>save_flags_total{flag,severity}</c>, recorded by <c>PUT /save</c>.</summary>
public static class SaveMetrics
{
    public const string UploadBytes = "save_upload_bytes";
    public const string FlagsTotal = "save_flags_total";

    static readonly Histogram<long> Upload = ServerMetrics.Meter.CreateHistogram<long>(UploadBytes, "By", "Size of uploaded save files.");
    static readonly Counter<long> Flags = ServerMetrics.Meter.CreateCounter<long>(FlagsTotal, null, "Plausibility flags raised on save uploads.");

    public static void RecordUpload(long bytes) => Upload.Record(bytes);

    public static void RecordFlag(string flag, string severity) =>
        Flags.Add(1, new KeyValuePair<string, object?>("flag", flag), new KeyValuePair<string, object?>("severity", severity));
}

/// <summary><c>moderation_actions_total{action}</c>. Registered with no emitter until the action matrix lands (issue #206).</summary>
public static class ModerationMetrics
{
    public const string ActionsTotal = "moderation_actions_total";

    static readonly Counter<long> Actions = ServerMetrics.Meter.CreateCounter<long>(ActionsTotal, null, "Moderation actions by action (log, soft_flag, rollback, ban).");

    // TODO(#206): call where moderation_actions rows are written.
    public static void RecordAction(string action) => Actions.Add(1, new KeyValuePair<string, object?>("action", action));
}

/// <summary><c>idempotency_replays_total{route}</c>, recorded when the idempotency store replays a stored response.</summary>
public static class IdempotencyMetrics
{
    public const string ReplaysTotal = "idempotency_replays_total";

    static readonly Counter<long> Replays = ServerMetrics.Meter.CreateCounter<long>(ReplaysTotal, null, "Responses replayed from the idempotency store, by route.");

    public static void RecordReplay(string route) => Replays.Add(1, new KeyValuePair<string, object?>("route", route));
}

/// <summary><c>db_pool_in_use</c> (open pooled connections, tracked by the connection factory) and <c>db_query_duration_seconds{name}</c>.</summary>
public static class DbMetrics
{
    public const string PoolInUse = "db_pool_in_use";
    public const string QueryDurationSeconds = "db_query_duration_seconds";

    static long _inUse;

    static readonly ObservableGauge<long> InUse =
        ServerMetrics.Meter.CreateObservableGauge(PoolInUse, () => Interlocked.Read(ref _inUse), null, "Pooled Postgres connections handed out and not yet returned.");

    static readonly Histogram<double> Query = ServerMetrics.Meter.CreateHistogram<double>(QueryDurationSeconds, "s", "Query latency by query name.");

    public static long InUseNow => Interlocked.Read(ref _inUse);

    /// <summary>Counts an opened connection as in use until it closes (back to the pool).</summary>
    public static DbConnection Track(DbConnection c)
    {
        if (c.State != ConnectionState.Open) return c;
        Interlocked.Increment(ref _inUse);
        int released = 0;
        void OnChange(object? sender, StateChangeEventArgs e)
        {
            if (e.CurrentState is ConnectionState.Closed or ConnectionState.Broken && Interlocked.Exchange(ref released, 1) == 0)
            {
                Interlocked.Decrement(ref _inUse);
                c.StateChange -= OnChange;
            }
        }
        c.StateChange += OnChange;
        return c;
    }

    // TODO(gap: db query names): stores call this once they name their queries.
    public static void RecordQuery(string name, TimeSpan elapsed) => Query.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("name", name));
}

/// <summary><c>worker_job_duration_seconds{job}</c> and <c>worker_job_failures_total{job}</c>: one pass of a worker job.</summary>
public static class WorkerJobMetrics
{
    public const string DurationSeconds = "worker_job_duration_seconds";
    public const string FailuresTotal = "worker_job_failures_total";

    static readonly Histogram<double> Duration = ServerMetrics.Meter.CreateHistogram<double>(DurationSeconds, "s", "Duration of one worker job pass.");
    static readonly Counter<long> Failures = ServerMetrics.Meter.CreateCounter<long>(FailuresTotal, null, "Worker job passes that threw.");

    public static void Record(string job, TimeSpan elapsed, bool failed)
    {
        var tag = new KeyValuePair<string, object?>("job", job);
        Duration.Record(elapsed.TotalSeconds, tag);
        if (failed) Failures.Add(1, tag);
    }

    /// <summary>Runs one pass, recording its duration and (when it throws anything but cancellation) a failure; rethrows.</summary>
    public static async Task<T> TimeAsync<T>(string job, Func<CancellationToken, Task<T>> pass, CancellationToken ct)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool failed = false;
        try { return await pass(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { failed = true; throw; }
        finally { Record(job, System.Diagnostics.Stopwatch.GetElapsedTime(start), failed); }
    }
}

/// <summary>
/// <c>config_active_version</c> (the active <c>config_versions</c> id this process serves) and
/// <c>content_active_version_info{version}</c> (1 for the active config's content version), set when the config cache loads.
/// </summary>
public static class ConfigMetrics
{
    public const string ActiveVersion = "config_active_version";
    public const string ContentActiveVersionInfo = "content_active_version_info";

    sealed record Active(int Id, string? ContentVersion);

    static volatile Active? _active;

    static readonly ObservableGauge<int> Version = ServerMetrics.Meter.CreateObservableGauge(ActiveVersion,
        () => _active is { } a ? new[] { new Measurement<int>(a.Id) } : Array.Empty<Measurement<int>>(),
        null, "The active config_versions id this process serves.");

    static readonly ObservableGauge<int> Content = ServerMetrics.Meter.CreateObservableGauge(ContentActiveVersionInfo,
        () => _active is { ContentVersion: { } v }
            ? new[] { new Measurement<int>(1, new KeyValuePair<string, object?>("version", v)) }
            : Array.Empty<Measurement<int>>(),
        null, "1 for the content version of the active config.");

    public static void Observe(ActiveConfig? config) =>
        _active = config == null ? null : new Active(config.Id,
            (config.Payload["content"] as JsonObject)?["version"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null);

    internal static void Observe(int id, string? contentVersion) => _active = new Active(id, contentVersion);
}
