using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Observability;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Worker;

/// <summary>A partitioned table and its grain (specs/02 section 12: <c>month</c> or <c>day</c>).</summary>
public sealed record PartitionedTable(string Name, string Grain);

/// <summary>One table after a pass: partitions created now, and the days from now to the end of its last partition.</summary>
public sealed record PartitionHorizon(string Table, int Created, double HorizonDays, bool BelowOnePeriod);

/// <summary>
/// The worker's <c>partitions</c> job (specs/02 section 12; issue #262). PostgreSQL refuses a row that falls in no
/// partition and there is deliberately no default partition, so this job keeps <c>diamond_ledger</c> and
/// <c>summon_log</c> <c>PARTITION_AHEAD_MONTHS</c> months and <c>events</c> <c>PARTITION_AHEAD_DAYS</c> days ahead of now
/// through migration 0003's idempotent <c>ensure_partitions</c>. It runs at worker start and then daily, and publishes
/// each table's horizon as <c>db_partition_horizon_days{table}</c> (specs/08 section 9.4; the DB alarm of specs/10 section
/// 10 fires below one period). It never drops a partition.
/// </summary>
public sealed class PartitionMaintenanceService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public static readonly IReadOnlyList<PartitionedTable> Tables = new[]
    {
        new PartitionedTable("diamond_ledger", "month"),
        new PartitionedTable("summon_log", "month"),
        new PartitionedTable("events", "day"),
    };

    // The end of each table's last partition; the gauge reads it against the wall clock on every collection, so the
    // horizon keeps shrinking (and the alarm fires) if the job stops running or keeps failing.
    static readonly ConcurrentDictionary<string, DateTimeOffset> Ends = new(StringComparer.Ordinal);
    public const string HorizonMetric = "db_partition_horizon_days";

    static readonly ObservableGauge<double> HorizonGauge = ServerMetrics.Meter.CreateObservableGauge(
        HorizonMetric,
        () => Ends.Select(kv => new Measurement<double>((kv.Value - TimeProvider.System.GetUtcNow()).TotalDays,
                                                        new KeyValuePair<string, object?>("table", kv.Key))),
        unit: "d", description: "Days from now to the end of the table's last partition (specs/02 section 12).");

    readonly IDbConnectionFactory _db;
    readonly TimeProvider _clock;
    readonly int _aheadMonths;
    readonly int _aheadDays;
    readonly bool _hasDatabase;
    readonly ILogger<PartitionMaintenanceService> _log;

    public PartitionMaintenanceService(IDbConnectionFactory db, TimeProvider clock, IOptions<ServerOptions> options, ILogger<PartitionMaintenanceService> log)
    {
        _db = db;
        _clock = clock;
        var limits = options.Value.Limits;
        _aheadMonths = limits.PartitionAheadMonths > 0 ? limits.PartitionAheadMonths : 3;
        _aheadDays = limits.PartitionAheadDays > 0 ? limits.PartitionAheadDays : 14;
        _hasDatabase = !string.IsNullOrWhiteSpace(options.Value.Database.ConnectionString);
        _log = log;
    }

    /// <summary>The end of each table's last partition as this process last measured it (the gauge's source).</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> LastPartitionEnds => Ends;

    /// <summary>
    /// One pass: per table, creates every missing partition from now's period through now plus the horizon, then measures
    /// the table's horizon (also when the create failed). Rerunning it creates nothing. A table that ends less than one
    /// period ahead is logged as an error; a failed create is rethrown after every table was tried.
    /// </summary>
    /// <summary>Remembers where a table's last partition ends, for the <c>db_partition_horizon_days</c> gauge.</summary>
    internal static void RecordHorizon(string table, DateTimeOffset end) => Ends[table] = end;

    public async Task<IReadOnlyList<PartitionHorizon>> EnsureOnceAsync(CancellationToken ct) => await PassAsync(create: true, ct).ConfigureAwait(false);

    /// <summary>Measures every table's horizon without creating anything.</summary>
    public async Task<IReadOnlyList<PartitionHorizon>> MeasureAsync(CancellationToken ct) => await PassAsync(create: false, ct).ConfigureAwait(false);

    async Task<IReadOnlyList<PartitionHorizon>> PassAsync(bool create, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var result = new List<PartitionHorizon>();
        var failures = new List<Exception>();
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        foreach (var t in Tables)
        {
            int created = 0;
            if (create)
            {
                var until = t.Grain == "month" ? now.AddMonths(_aheadMonths) : now.AddDays(_aheadDays);
                try
                {
                    await using var cmd = c.Command("select ensure_partitions(@t::regclass, @g, @from, @until)", null,
                                                    ("t", t.Name), ("g", t.Grain), ("from", now.ToUniversalTime()), ("until", until.ToUniversalTime()));
                    created = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogError(e, "ensure_partitions failed for {Table}", t.Name);
                    failures.Add(e);
                }
            }

            string? last;
            await using (var cmd = c.Command(
                "select max(c.relname::text) from pg_inherits i join pg_class c on c.oid = i.inhrelid where i.inhparent = @t::regclass", null,
                ("t", t.Name)))
                last = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;

            var end = last == null ? null : PartitionEnd(t, last);
            if (end is { } e2) RecordHorizon(t.Name, e2);
            double horizon = end is { } e3 ? (e3 - now).TotalDays : 0;
            var onePeriod = (t.Grain == "month" ? now.AddMonths(1) : now.AddDays(1)) - now;
            bool below = end == null || end.Value - now < onePeriod;
            if (below)
                _log.LogError("partition horizon of {Table} is {Days:F1} days, less than one {Grain} ahead; inserts will fail soon (RB-DB)",
                              t.Name, horizon, t.Grain);
            else if (created > 0)
                _log.LogInformation("created {Count} {Grain} partitions of {Table}; horizon {Days:F1} days", created, t.Grain, t.Name, horizon);
            result.Add(new PartitionHorizon(t.Name, created, horizon, below));
        }
        if (failures.Count > 0) throw new AggregateException("partition job failed", failures);
        return result;
    }

    /// <summary>The exclusive end of partition <c>&lt;table&gt;_pYYYYMM</c> or <c>_pYYYYMMDD</c> (UTC; 0003's naming).</summary>
    public static DateTimeOffset? PartitionEnd(PartitionedTable t, string partition)
    {
        string prefix = t.Name + "_p";
        if (!partition.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string fmt = t.Grain == "month" ? "yyyyMM" : "yyyyMMdd";
        if (!DateTime.TryParseExact(partition[prefix.Length..], fmt, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start))
            return null;
        var s = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc));
        return t.Grain == "month" ? s.AddMonths(1) : s.AddDays(1);
    }

    // TODO(#208): run under the worker's job registry and Redis leader lock. Two workers racing
    // on one new partition make one pass fail with "already exists"; the next pass (or the retry below) finds it made.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = HorizonGauge;
        if (!_hasDatabase)
        {
            _log.LogWarning("partition job idle: HeroCat:Database:ConnectionString is not configured");
            return;
        }
        using var timer = new PeriodicTimer(Interval, _clock);
        do
        {
            try { await WorkerJobMetrics.TimeAsync("partitions", EnsureOnceAsync, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                _log.LogError(e, "partition job failed; retrying in an hour");
                try { await Task.Delay(TimeSpan.FromHours(1), _clock, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try { await WorkerJobMetrics.TimeAsync("partitions", EnsureOnceAsync, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception e2) { _log.LogError(e2, "partition job failed again; retrying tomorrow"); }
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
