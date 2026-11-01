using System.Data.Common;
using HeroCat.Server.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HeroCat.Server.Infrastructure.Db;

/// <summary>
/// Opens pooled PostgreSQL connections (specs/08 section 9.1). Handlers depend on this interface, not on Npgsql types,
/// so unit tests run without a database; the Testcontainers integration tests (#219) use the real one.
/// </summary>
public interface IDbConnectionFactory
{
    ValueTask<DbConnection> OpenAsync(CancellationToken ct = default);
}

/// <summary>One <see cref="NpgsqlDataSource"/> per process: it owns the connection pool. Built on first use.</summary>
public sealed class NpgsqlConnectionFactory : IDbConnectionFactory, IAsyncDisposable, IDisposable
{
    readonly Lazy<NpgsqlDataSource> _dataSource;

    public NpgsqlConnectionFactory(IOptions<ServerOptions> options)
    {
        string cs = options.Value.Database.ConnectionString;
        _dataSource = new Lazy<NpgsqlDataSource>(() =>
        {
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("HeroCat:Database:ConnectionString is not configured");
            return NpgsqlDataSource.Create(cs);
        });
    }

    public async ValueTask<DbConnection> OpenAsync(CancellationToken ct = default) =>
        await _dataSource.Value.OpenConnectionAsync(ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_dataSource.IsValueCreated) await _dataSource.Value.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_dataSource.IsValueCreated) _dataSource.Value.Dispose();
    }
}
