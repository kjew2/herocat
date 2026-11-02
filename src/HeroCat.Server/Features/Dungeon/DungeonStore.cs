using System.Data.Common;
using HeroCat.Server.Infrastructure.Db;

namespace HeroCat.Server.Features.Dungeon;

/// <summary>A <c>dungeon_seeds</c> row as the correlation reads it (specs/02 section 12).</summary>
public sealed record IssuedSeed(Guid GrantId, string Kind, int Floor);

/// <summary>
/// The Dungeon feature's SQL (specs/02 section 12: <c>dungeon_seeds</c>, and <c>dungeon_progress</c> from <c>0010</c>).
/// Every statement takes the caller's connection and transaction.
/// </summary>
public sealed class DungeonStore
{
    readonly IDbConnectionFactory _db;

    public DungeonStore(IDbConnectionFactory db) => _db = db;

    public ValueTask<DbConnection> OpenAsync(CancellationToken ct) => _db.OpenAsync(ct);

    /// <summary>
    /// Locks the player's <c>accounts</c> row (<c>for no key update</c>) so two seed requests of one player count the
    /// daily cap one after the other. False when the account does not exist.
    /// </summary>
    public static async Task<bool> LockAccountAsync(DbConnection c, DbTransaction tx, Guid player, CancellationToken ct)
    {
        await using var cmd = c.Command("select 1 from accounts where player_id = @p for no key update", tx, ("p", player));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) != null;
    }

    /// <summary>The last uploaded save's <c>BestFloor[kind]</c> (<c>dungeon_progress</c>), 0 when no upload carried it.</summary>
    public static async Task<int> BestFloorAsync(DbConnection c, DbTransaction? tx, Guid player, string kind, CancellationToken ct)
    {
        await using var cmd = c.Command("select best_floor from dungeon_progress where player_id = @p and kind = @k", tx, ("p", player), ("k", kind));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is int best ? best : 0;
    }

    /// <summary>Every kind's best floor as of the last upload that carried <c>Dungeons</c>.</summary>
    public static async Task<Dictionary<string, int>> BestFloorsAsync(DbConnection c, DbTransaction tx, Guid player, CancellationToken ct)
    {
        await using var cmd = c.Command("select kind, best_floor from dungeon_progress where player_id = @p", tx, ("p", player));
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    /// <summary>Records this upload's best floors (the last upload wins, lower or not).</summary>
    public static async Task UpsertBestFloorsAsync(DbConnection c, DbTransaction tx, Guid player, long saveSeq, IReadOnlyDictionary<string, int> best,
                                                   CancellationToken ct)
    {
        foreach (var (kind, floor) in best)
        {
            await using var cmd = c.Command(
                "insert into dungeon_progress (player_id, kind, best_floor, save_seq) values (@p, @k, @f, @s) " +
                "on conflict (player_id, kind) do update set best_floor = excluded.best_floor, save_seq = excluded.save_seq, updated_at = now()", tx,
                ("p", player), ("k", kind), ("f", floor), ("s", saveSeq));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Seeds of this kind and day key that have not expired at <paramref name="now"/> (the daily cap's count).</summary>
    public static async Task<int> OutstandingAsync(DbConnection c, DbTransaction tx, Guid player, string kind, int dayKey, DateTimeOffset now,
                                                   CancellationToken ct)
    {
        await using var cmd = c.Command(
            "select count(*)::int from dungeon_seeds where player_id = @p and kind = @k and day_key = @d and expires_at > @now", tx,
            ("p", player), ("k", kind), ("d", dayKey), ("now", now));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public static async Task InsertAsync(DbConnection c, DbTransaction tx, Guid grantId, Guid player, string requestId, string kind, int floor,
                                         int dayKey, ulong seed, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var cmd = c.Command(
            "insert into dungeon_seeds (grant_id, player_id, request_id, kind, floor, day_key, seed, issued_at, expires_at) " +
            "values (@g, @p, @r, @k, @f, @d, @s, @i, @e)", tx,
            ("g", grantId), ("p", player), ("r", requestId), ("k", kind), ("f", floor), ("d", dayKey), ("s", unchecked((long)seed)),
            ("i", issuedAt), ("e", expiresAt));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The player's seeds among <paramref name="grantIds"/>.</summary>
    public static async Task<Dictionary<Guid, IssuedSeed>> IssuedAsync(DbConnection c, DbTransaction tx, Guid player, IReadOnlyCollection<Guid> grantIds,
                                                                     CancellationToken ct)
    {
        var map = new Dictionary<Guid, IssuedSeed>();
        if (grantIds.Count == 0) return map;
        await using var cmd = c.Command("select grant_id, kind, floor from dungeon_seeds where player_id = @p and grant_id = any(@g)", tx,
                                        ("p", player), ("g", grantIds.ToArray()));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var g = r.GetGuid(0);
            map[g] = new IssuedSeed(g, r.GetString(1), r.GetInt32(2));
        }
        return map;
    }

    /// <summary>The floors of <paramref name="kind"/> among <paramref name="floors"/> that have at least one seed issued to the player.</summary>
    public static async Task<HashSet<int>> FloorsWithSeedAsync(DbConnection c, DbTransaction tx, Guid player, string kind, int[] floors, CancellationToken ct)
    {
        await using var cmd = c.Command("select distinct floor from dungeon_seeds where player_id = @p and kind = @k and floor = any(@f)", tx,
                                        ("p", player), ("k", kind), ("f", floors));
        var set = new HashSet<int>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) set.Add(r.GetInt32(0));
        return set;
    }

    /// <summary>Fills <c>dungeon_seeds.result</c> (<c>cleared</c> or <c>failed</c>) for one of the player's grants.</summary>
    public static async Task SetResultAsync(DbConnection c, DbTransaction tx, Guid player, Guid grantId, bool cleared, CancellationToken ct)
    {
        await using var cmd = c.Command("update dungeon_seeds set result = @r where player_id = @p and grant_id = @g", tx,
                                        ("r", cleared ? "cleared" : "failed"), ("p", player), ("g", grantId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
