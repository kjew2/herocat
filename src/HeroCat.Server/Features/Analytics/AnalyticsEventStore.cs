using System.Data.Common;
using System.Text;
using HeroCat.Server.Infrastructure.Db;

namespace HeroCat.Server.Features.Analytics;

/// <summary>
/// Writes the backend <c>events</c> table (specs/02 section 12; 00 section 3). A row is stored once per
/// <c>(event_id, ts)</c>: a retried event is a no-op, whichever batch carries it.
/// </summary>
public sealed class AnalyticsEventStore
{
    readonly IDbConnectionFactory _db;

    public AnalyticsEventStore(IDbConnectionFactory db) => _db = db;

    /// <summary>The <c>events_pYYYYMMDD</c> partitions among <paramref name="names"/> that exist.</summary>
    public async Task<HashSet<string>> ExistingPartitionsAsync(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (names.Count == 0) return found;
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("analytics.existing_partitions", 
            "select c.relname::text from pg_inherits i join pg_class c on c.oid = i.inhrelid " +
            "where i.inhparent = 'events'::regclass and c.relname = any(@names)", null, ("names", names.ToArray()));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) found.Add(r.GetString(0));
        return found;
    }

    /// <summary>Inserts the rows on a connection of its own; returns how many were new.</summary>
    public async Task<int> InsertAsync(IReadOnlyList<EventRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await InsertAsync(c, null, rows, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the rows inside the caller's transaction (a ledger write records its server-side economy event in the same
    /// transaction; <see cref="ServerEconomyEvents"/>). Every row's day must have a partition. Returns how many were new.
    /// </summary>
    public static async Task<int> InsertAsync(DbConnection c, DbTransaction? tx, IReadOnlyList<EventRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var sql = new StringBuilder(
            "insert into events (event_id, ts, name, player_id_hash, session_id, day_index, stage, props, env, source, device_id, " +
            "client_version, content_version, schema_version, debug_tainted, clock_authoritative, age_gate) values ");
        var args = new List<(string, object?)>(rows.Count * 17);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (i > 0) sql.Append(", ");
            sql.Append($"(@id{i}::uuid, @ts{i}::timestamptz, @n{i}::text, @h{i}::bytea, @sid{i}::uuid, @day{i}::integer, @st{i}::integer, " +
                       $"@p{i}::jsonb, @env{i}::text, @src{i}::text, @dev{i}::uuid, @cv{i}::text, @ctv{i}::text, @sv{i}::integer, " +
                       $"@dt{i}::boolean, @ca{i}::boolean, @ag{i}::smallint)");
            args.Add(($"id{i}", r.EventId));
            args.Add(($"ts{i}", r.Ts.ToUniversalTime()));
            args.Add(($"n{i}", r.Name));
            args.Add(($"h{i}", r.PlayerIdHash));
            args.Add(($"sid{i}", r.SessionId));
            args.Add(($"day{i}", r.DayIndex));
            args.Add(($"st{i}", r.Stage));
            args.Add(($"p{i}", r.PropsJson));
            args.Add(($"env{i}", r.Env));
            args.Add(($"src{i}", r.Source));
            args.Add(($"dev{i}", r.DeviceId));
            args.Add(($"cv{i}", r.ClientVersion));
            args.Add(($"ctv{i}", r.ContentVersion));
            args.Add(($"sv{i}", r.SchemaVersion));
            args.Add(($"dt{i}", r.DebugTainted));
            args.Add(($"ca{i}", r.ClockAuthoritative));
            args.Add(($"ag{i}", r.AgeGate));
        }
        sql.Append(" on conflict (event_id, ts) do nothing");
        await using var cmd = c.Command("analytics.insert_events", sql.ToString(), tx, args.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
