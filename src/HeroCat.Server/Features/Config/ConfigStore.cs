using System.Data.Common;
using System.Text.Json.Nodes;
using HeroCat.Server.Infrastructure.Db;

namespace HeroCat.Server.Features.Config;

/// <summary>
/// The <c>config_versions</c> table (specs/02 section 12): published payloads per environment, at most one active.
/// Publishing inserts an inactive row; activation is a single-row flip, so rollback is activating the previous id (08 section 6.2).
/// </summary>
public interface IConfigStore
{
    Task<ConfigVersionRow?> GetActiveAsync(string env, CancellationToken ct);
    Task<ConfigVersionRow?> GetAsync(string env, int id, CancellationToken ct);
    Task<int> InsertAsync(string env, JsonObject payload, ConfigColumns columns, string createdBy, DateTimeOffset now, CancellationToken ct);

    /// <summary>Makes <paramref name="id"/> the env's only active row; false when the env has no such row.</summary>
    Task<bool> ActivateAsync(string env, int id, CancellationToken ct);
}

/// <summary>
/// PostgreSQL. Activations of one env serialise on a transaction-scoped advisory lock, and the partial unique index
/// <c>config_versions (env) where active</c> (migration 0006) makes two active rows impossible.
/// </summary>
public sealed class PostgresConfigStore : IConfigStore
{
    const string Columns = "id, env, payload::text, active, created_at, created_by";

    readonly IDbConnectionFactory _db;

    public PostgresConfigStore(IDbConnectionFactory db) => _db = db;

    public async Task<ConfigVersionRow?> GetActiveAsync(string env, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        // At most one row is active per env (the partial unique index of migration 0006).
        await using var cmd = c.Command($"select {Columns} from config_versions where env = @env and active order by id desc limit 1", null, ("env", env));
        return await ReadOneAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<ConfigVersionRow?> GetAsync(string env, int id, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command($"select {Columns} from config_versions where env = @env and id = @id", null, ("env", env), ("id", id));
        return await ReadOneAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<int> InsertAsync(string env, JsonObject payload, ConfigColumns columns, string createdBy, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "insert into config_versions (env, payload, min_client_version, content_version, rollout_pct, active, created_at, created_by) " +
            "values (@env, @payload::jsonb, @minClient, @content, @pct, false, @now, @by) returning id", null,
            ("env", env), ("payload", payload.ToJsonString()), ("minClient", columns.MinClientVersion), ("content", columns.ContentVersion),
            ("pct", (short)columns.RolloutPct), ("now", now), ("by", createdBy));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<bool> ActivateAsync(string env, int id, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var lk = c.Command("select pg_advisory_xact_lock(hashtext('config_versions/' || @env))", tx, ("env", env)))
            await lk.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // The old row goes inactive first: the partial unique index (env) where active (migration 0006, issue #294) is
        // checked per statement, so turning the new row on first would collide with it.
        await using (var off = c.Command("update config_versions set active = false where env = @env and active and id <> @id", tx, ("env", env), ("id", id)))
            await off.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using (var on = c.Command("update config_versions set active = true where env = @env and id = @id", tx, ("env", env), ("id", id)))
        {
            if (await on.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    static async Task<ConfigVersionRow?> ReadOneAsync(DbCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        var payload = JsonNode.Parse(r.GetString(2)) as JsonObject ?? new JsonObject();
        return new ConfigVersionRow(r.GetInt32(0), r.GetString(1), payload, r.GetBoolean(3),
                                    new DateTimeOffset(r.GetDateTime(4), TimeSpan.Zero), r.GetString(5));
    }
}

/// <summary>Per-process rows: tests, and a local run without a database.</summary>
public sealed class InMemoryConfigStore : IConfigStore
{
    readonly List<ConfigVersionRow> _rows = new();

    public Task<ConfigVersionRow?> GetActiveAsync(string env, CancellationToken ct)
    {
        lock (_rows) return Task.FromResult(Copy(_rows.Where(r => r.Env == env && r.Active).OrderByDescending(r => r.Id).FirstOrDefault()));
    }

    public Task<ConfigVersionRow?> GetAsync(string env, int id, CancellationToken ct)
    {
        lock (_rows) return Task.FromResult(Copy(_rows.FirstOrDefault(r => r.Env == env && r.Id == id)));
    }

    public Task<int> InsertAsync(string env, JsonObject payload, ConfigColumns columns, string createdBy, DateTimeOffset now, CancellationToken ct)
    {
        lock (_rows)
        {
            int id = _rows.Count + 1;
            _rows.Add(new ConfigVersionRow(id, env, (JsonObject)payload.DeepClone(), false, now, createdBy));
            return Task.FromResult(id);
        }
    }

    public Task<bool> ActivateAsync(string env, int id, CancellationToken ct)
    {
        lock (_rows)
        {
            if (!_rows.Any(r => r.Env == env && r.Id == id)) return Task.FromResult(false);
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Env == env) _rows[i] = _rows[i] with { Active = _rows[i].Id == id };
            return Task.FromResult(true);
        }
    }

    static ConfigVersionRow? Copy(ConfigVersionRow? r) => r == null ? null : r with { Payload = (JsonObject)r.Payload.DeepClone() };
}
