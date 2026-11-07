using HeroCat.Server.Hosting;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Infrastructure.Idempotency;

/// <summary>
/// The worker's hourly purge of idempotency rows older than <c>IDEMPOTENCY_TTL_H</c> (specs/02 section 13.1, 00 decision
/// 28; issue #194). The store already ignores expired rows, so a late purge only costs disk.
/// </summary>
public sealed class IdempotencyPurgeService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    readonly IIdempotencyStore _store;
    readonly TimeProvider _clock;
    readonly TimeSpan _ttl;
    readonly ILogger<IdempotencyPurgeService> _log;

    public IdempotencyPurgeService(IIdempotencyStore store, TimeProvider clock, IOptions<ServerOptions> options, ILogger<IdempotencyPurgeService> log)
    {
        _store = store;
        _clock = clock;
        _ttl = IdempotencyTtl(options.Value);
        _log = log;
    }

    public static TimeSpan IdempotencyTtl(ServerOptions o) =>
        TimeSpan.FromHours(o.Limits.IdempotencyTtlHours > 0 ? o.Limits.IdempotencyTtlHours : 24);

    /// <summary>One pass: deletes rows created more than the TTL before now. Returns how many.</summary>
    public async Task<int> PurgeOnceAsync(CancellationToken ct)
    {
        int n = await _store.PurgeAsync(_clock.GetUtcNow() - _ttl, ct).ConfigureAwait(false);
        _log.LogInformation("idempotency purge removed {Rows} rows", n);
        return n;
    }

    // TODO(#208): run under the worker's job registry and Redis leader lock (one worker instance today, so no overlap).
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _clock);
        do
        {
            try { await Observability.WorkerJobMetrics.TimeAsync("idempotency_purge", PurgeOnceAsync, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { _log.LogError(e, "idempotency purge failed; retrying next hour"); }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
