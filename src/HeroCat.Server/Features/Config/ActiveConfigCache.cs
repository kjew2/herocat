using HeroCat.Server.Hosting;
using HeroCat.Server.Pipeline;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Config;

/// <summary>The active config as features read it (kill switches, the <c>GET /config</c> body). Null when none is active.</summary>
public interface IActiveConfigSource
{
    ValueTask<ActiveConfig?> GetActiveAsync(CancellationToken ct);

    /// <summary>Drops this process's copy, so the next read loads the table (after an activation on this instance).</summary>
    void Invalidate();
}

/// <summary>
/// The in-process copy of the env's active <c>config_versions</c> row (specs/08 section 6.2): a read older than
/// <c>CONFIG_REFRESH_S</c> reloads it, so an activation (a kill switch, a version floor) reaches every api instance within
/// that time (acceptance 17: within 60 s). One reload at a time; while the store is down the last copy keeps serving
/// (logged) and the next reload is tried a refresh interval later. Also the pipeline's <see cref="IClientGateConfigSource"/>,
/// which fails open (no gates, no limit overrides) when no copy was ever loaded.
/// </summary>
public sealed class ActiveConfigCache : IActiveConfigSource, IClientGateConfigSource
{
    public const int DefaultRefreshSeconds = 30;

    sealed record Snapshot(ActiveConfig? Config, DateTimeOffset LoadedAt);

    readonly IConfigStore _store;
    readonly TimeProvider _clock;
    readonly ILogger<ActiveConfigCache> _log;
    readonly string _env;
    readonly TimeSpan _refresh;
    readonly SemaphoreSlim _reload = new(1, 1);
    volatile Snapshot? _snapshot;

    public ActiveConfigCache(IConfigStore store, TimeProvider clock, IOptions<ServerOptions> options, ILogger<ActiveConfigCache> log)
    {
        _store = store;
        _clock = clock;
        _log = log;
        _env = options.Value.Env;
        int s = options.Value.Limits.ConfigRefreshSeconds;
        _refresh = TimeSpan.FromSeconds(s > 0 ? s : DefaultRefreshSeconds);
    }

    public TimeSpan RefreshInterval => _refresh;

    public async ValueTask<ActiveConfig?> GetActiveAsync(CancellationToken ct)
    {
        var s = _snapshot;
        if (Fresh(s)) return s!.Config;

        await _reload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            s = _snapshot;
            if (Fresh(s)) return s!.Config;
            try
            {
                var row = await _store.GetActiveAsync(_env, ct).ConfigureAwait(false);
                var config = row == null ? null : new ActiveConfig(row.Id, row.Payload);
                _snapshot = new Snapshot(config, _clock.GetUtcNow());
                return config;
            }
            catch (Exception e) when (s != null && e is not OperationCanceledException)
            {
                _log.LogWarning(e, "config_versions unavailable; serving config {ConfigVersion} loaded at {LoadedAt}", s.Config?.Id, s.LoadedAt);
                _snapshot = s with { LoadedAt = _clock.GetUtcNow() };
                return s.Config;
            }
        }
        finally
        {
            _reload.Release();
        }
    }

    public void Invalidate() => _snapshot = null;

    async ValueTask<ClientGateConfig> IClientGateConfigSource.GetAsync(CancellationToken ct)
    {
        try
        {
            return (await GetActiveAsync(ct).ConfigureAwait(false))?.Gates ?? ClientGateConfig.None;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "config_versions unavailable and no config loaded yet; gates fail open");
            return ClientGateConfig.None;
        }
    }

    bool Fresh(Snapshot? s) => s != null && _clock.GetUtcNow() - s.LoadedAt < _refresh;
}
