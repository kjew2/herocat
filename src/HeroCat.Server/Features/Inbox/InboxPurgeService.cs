using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Db;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Inbox;

/// <summary>
/// The worker's nightly purge of expired, unclaimed inbox items (specs/08 section 3.2). <c>GET /inbox</c> and the claims
/// already ignore expired rows, so a late purge only costs disk.
/// </summary>
public sealed class InboxPurgeService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    readonly IDbConnectionFactory _db;
    readonly TimeProvider _clock;
    readonly ILogger<InboxPurgeService> _log;
    readonly bool _hasDatabase;

    public InboxPurgeService(IDbConnectionFactory db, TimeProvider clock, ILoggerFactory logs, IOptions<ServerOptions>? options = null)
    {
        _hasDatabase = options == null || !string.IsNullOrWhiteSpace(options.Value.Database.ConnectionString);
        _db = db;
        _clock = clock;
        _log = logs.CreateLogger<InboxPurgeService>();
    }

    /// <summary>One pass. Returns how many rows it deleted.</summary>
    public async Task<int> PurgeOnceAsync(CancellationToken ct)
    {
        int n = await InboxStore.PurgeExpiredAsync(_db, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        _log.LogInformation("inbox purge removed {Rows} expired items", n);
        return n;
    }

    // TODO(#208): run under the worker's job registry and Redis leader lock at a fixed nightly time.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_hasDatabase)
        {
            _log.LogInformation("inbox purge off: no database configured");
            return;
        }
        using var timer = new PeriodicTimer(Interval, _clock);
        do
        {
            try { await PurgeOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { _log.LogError(e, "inbox purge failed; retrying next night"); }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
