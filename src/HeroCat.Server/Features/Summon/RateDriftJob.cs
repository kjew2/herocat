using System.Diagnostics.Metrics;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure;
using HeroCat.Server.Infrastructure.Db;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Summon;

/// <summary>
/// Where the realised-rate job reports (specs/08 section 10.3). The default logs and counts; the alarm framework
/// (issue #210) replaces it with its own <c>RB-SUMMON-RATES</c> alarm.
/// </summary>
public interface IRateDriftAlarm
{
    void Report(RateDriftReport report);
}

/// <summary>
/// Logs every run, an error naming <c>RB-SUMMON-RATES</c> when the report alarms, and publishes
/// <c>summon_rate_drift_alarms_total</c> (one per alarming run) and <c>summon_rate_drift_rarities</c> (the rarities
/// outside their interval in the last run).
/// </summary>
public sealed class LogRateDriftAlarm : IRateDriftAlarm
{
    public const string AlarmsTotal = "summon_rate_drift_alarms_total";
    public const string DriftedGauge = "summon_rate_drift_rarities";

    static readonly Counter<long> Alarms = ServerMetrics.Meter.CreateCounter<long>(AlarmsTotal, "{run}",
        "Realised-rate job runs that raised RB-SUMMON-RATES (specs/08 section 10.3).");
    static long _lastDrifted;
    static readonly ObservableGauge<long> Drifted = ServerMetrics.Meter.CreateObservableGauge(DriftedGauge, () => Interlocked.Read(ref _lastDrifted),
        "{rarity}", "Rarities of banner levels outside their Wilson interval in the last realised-rate run.");

    readonly ILogger<LogRateDriftAlarm> _log;

    public LogRateDriftAlarm(ILogger<LogRateDriftAlarm> log) => _log = log;

    public void Report(RateDriftReport report)
    {
        _ = Drifted;
        var drifted = report.Drifted.ToList();
        Interlocked.Exchange(ref _lastDrifted, drifted.Count);
        if (!report.Alarm)
        {
            _log.LogInformation("realised rates {From:o}..{To:o}: {Rows} rarity rows inside their z={Z} Wilson interval",
                                report.From, report.To, report.Rarities.Count, report.Z);
            return;
        }
        Alarms.Add(1);
        foreach (var d in drifted)
            _log.LogError("{Runbook}: {Banner} level {Level} rarity {Rarity}: published {Expected:R} outside [{Low:R}, {High:R}] " +
                          "(observed {Observed} of {Pulls})", RateDrift.Runbook, d.Banner, d.Level, d.Rarity, d.Expected, d.WilsonLow, d.WilsonHigh,
                          d.Observed, d.Pulls);
        foreach (var u in report.UnknownTables)
            _log.LogError("{Runbook}: {Pulls} pulls of {Banner} level {Level} used rate table {Hash}, which no held content set publishes",
                          RateDrift.Runbook, u.Pulls, u.Banner, u.Level, u.RateTableHash);
    }
}

/// <summary>
/// The worker's nightly realised-rate job (specs/08 section 10.3; specs/10 section 9.3; issue #200): counts the
/// trailing 30 days of <c>summon_log</c> per banner level, rate table, pity, guarantee and rarity, evaluates them with
/// <see cref="RateDrift"/> against the held content sets, and reports to <see cref="IRateDriftAlarm"/>.
/// </summary>
public sealed class RateDriftJob : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    readonly IDbConnectionFactory _db;
    readonly IContentSets _content;
    readonly IRateDriftAlarm _alarm;
    readonly TimeProvider _clock;
    readonly bool _hasDatabase;
    readonly ILogger<RateDriftJob> _log;

    public RateDriftJob(IDbConnectionFactory db, IContentSets content, IRateDriftAlarm alarm, TimeProvider clock, IOptions<ServerOptions> options,
                        ILogger<RateDriftJob> log)
    {
        _db = db;
        _content = content;
        _alarm = alarm;
        _clock = clock;
        _hasDatabase = !string.IsNullOrWhiteSpace(options.Value.Database.ConnectionString);
        _log = log;
    }

    /// <summary>One run over the trailing window ending now: query, evaluate, report. Returns the report.</summary>
    public async Task<RateDriftReport> RunOnceAsync(CancellationToken ct)
    {
        var to = _clock.GetUtcNow();
        var from = to - RateDrift.Window;
        var groups = await CountAsync(from, to, ct).ConfigureAwait(false);
        var report = RateDrift.Evaluate(groups, RateDrift.TableIndex(_content.All), from, to);
        _alarm.Report(report);
        return report;
    }

    /// <summary>The window's <c>summon_log</c> rows grouped by banner, level, table, pity, guarantee and rarity.</summary>
    public async Task<List<RealisedPulls>> CountAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "select banner, banner_level, rate_table_hash, pity_before, guarantee, rarity, count(*)::bigint from summon_log " +
            "where created_at >= @from and created_at < @to group by banner, banner_level, rate_table_hash, pity_before, guarantee, rarity", null,
            ("from", from.UtcDateTime), ("to", to.UtcDateTime));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<RealisedPulls>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new RealisedPulls(r.GetString(0), r.GetInt16(1), (byte[])r.GetValue(2), r.GetInt16(3), r.GetBoolean(4), r.GetString(5).Trim(),
                                       r.GetInt64(6)));
        return rows;
    }

    // TODO(#208): run under the worker's job registry and Redis leader lock, with worker_job_* metrics, at a fixed hour.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_hasDatabase)
        {
            _log.LogWarning("realised-rate job idle: HeroCat:Database:ConnectionString is not configured");
            return;
        }
        using var timer = new PeriodicTimer(Interval, _clock);
        do
        {
            try { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { _log.LogError(e, "realised-rate job failed; next run in {Interval}", Interval); }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
