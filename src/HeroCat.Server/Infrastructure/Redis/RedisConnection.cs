using HeroCat.Server.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HeroCat.Server.Infrastructure.Redis;

/// <summary>
/// The process's one Redis multiplexer (specs/08 section 9.1: a cache and lock service only; rate limits, summon locks,
/// the maintenance flag). Connected on first use, so the api starts, and serves <c>/health</c>, without Redis.
/// Key names and TTLs live in <see cref="RedisKeys"/> (#154).
/// </summary>
public interface IRedisConnection
{
    Task<IDatabase> DatabaseAsync();
}

public sealed class RedisConnection : IRedisConnection, IAsyncDisposable, IDisposable
{
    readonly Lazy<Task<ConnectionMultiplexer>> _mux;

    public RedisConnection(IOptions<ServerOptions> options)
    {
        string config = options.Value.Redis.Configuration;
        _mux = new Lazy<Task<ConnectionMultiplexer>>(() =>
        {
            if (string.IsNullOrWhiteSpace(config))
                throw new InvalidOperationException("HeroCat:Redis:Configuration is not configured");
            return ConnectionMultiplexer.ConnectAsync(config);
        });
    }

    public async Task<IDatabase> DatabaseAsync() => (await _mux.Value.ConfigureAwait(false)).GetDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_mux.IsValueCreated && _mux.Value.IsCompletedSuccessfully)
            await (await _mux.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_mux.IsValueCreated && _mux.Value.IsCompletedSuccessfully) _mux.Value.Result.Dispose();
    }
}
