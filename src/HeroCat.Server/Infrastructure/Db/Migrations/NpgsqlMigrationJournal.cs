using System.Data.Common;
using Npgsql;

namespace HeroCat.Server.Infrastructure.Db.Migrations;

/// <summary>
/// <c>schema_migrations</c> in PostgreSQL. The run is serialised by a session-level advisory lock on a dedicated
/// connection; each script runs in its own transaction with its journal row (PostgreSQL DDL is transactional, so a
/// script that needs to run outside a transaction, such as <c>CREATE INDEX CONCURRENTLY</c>, is not supported yet).
/// </summary>
public sealed class NpgsqlMigrationJournal : IMigrationJournal
{
    /// <summary>Advisory lock key: any constant unique to this runner.</summary>
    const long LockKey = 0x4865_726F_4361_7401;   // "HeroCat" + 1

    readonly IDbConnectionFactory _db;

    public NpgsqlMigrationJournal(IDbConnectionFactory db) { _db = db; }

    public async Task<IAsyncDisposable> LockAsync(CancellationToken ct)
    {
        var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await Exec(conn, null, "SELECT pg_advisory_lock(@k)", ct, ("k", LockKey)).ConfigureAwait(false);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new Releaser(conn);
    }

    public async Task EnsureCreatedAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await Exec(conn, null, @"CREATE TABLE IF NOT EXISTS schema_migrations (
    version    integer     PRIMARY KEY,
    name       text        NOT NULL,
    checksum   text        NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
)", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AppliedMigration>> AppliedAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, name, checksum FROM schema_migrations ORDER BY version";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AppliedMigration>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new AppliedMigration(r.GetInt32(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public async Task ApplyAsync(MigrationScript script, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Exec(conn, tx, script.Sql, ct).ConfigureAwait(false);
        await Exec(conn, tx, "INSERT INTO schema_migrations (version, name, checksum) VALUES (@v, @n, @c)", ct,
            ("v", script.Version), ("n", script.Name), ("c", script.Checksum)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    static async Task Exec(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct, params (string name, object value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.Add(new NpgsqlParameter(name, value));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    sealed class Releaser : IAsyncDisposable
    {
        readonly DbConnection _conn;
        public Releaser(DbConnection conn) { _conn = conn; }

        public async ValueTask DisposeAsync()
        {
            try { await Exec(_conn, null, "SELECT pg_advisory_unlock(@k)", CancellationToken.None, ("k", LockKey)).ConfigureAwait(false); }
            finally { await _conn.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
