namespace HeroCat.Server.Worker;

/// <summary>
/// The <c>--worker</c> role's loop (specs/08 section 9.1). No jobs yet: the job runner, its Redis leader lock and the
/// first jobs (mail blasts, idempotency and inbox purges) are #208; later issues add theirs.
/// </summary>
public sealed class WorkerService : BackgroundService
{
    readonly ILogger<WorkerService> _log;

    public WorkerService(ILogger<WorkerService> log) { _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // TODO(#208): leader lock, job registry, worker_job_* metrics.
        _log.LogInformation("worker role started; no jobs registered yet");
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
