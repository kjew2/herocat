using System.Data.Common;
using System.Text;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Server.Features.Summon;

/// <summary>A <c>banner_pity</c> row (specs/02 section 12): pity, cumulative EXP, total pulls.</summary>
public sealed record BannerPityRow(int Pity, long Exp, long TotalPulls);

/// <summary>
/// The Summon service's SQL (specs/02 section 12: <c>banner_pity</c>, <c>summon_log</c>, <c>diamond_ledger</c>,
/// <c>ticket_ledger</c>, <c>granted_items</c>). The pull's statements take the handler's connection and transaction.
/// </summary>
public sealed class SummonStore
{
    readonly IDbConnectionFactory _db;

    public SummonStore(IDbConnectionFactory db) => _db = db;

    public ValueTask<DbConnection> OpenAsync(CancellationToken ct) => _db.OpenAsync(ct);

    /// <summary>
    /// Locks the player's <c>accounts</c> row for the transaction (<c>for no key update</c>, which ledger inserts do not
    /// wait on), so two pulls of one player never read the same balance even when the Redis lock failed open. False when
    /// the account does not exist.
    /// </summary>
    public static async Task<bool> LockAccountAsync(DbConnection c, DbTransaction tx, Guid player, CancellationToken ct)
    {
        await using var cmd = c.Command("select 1 from accounts where player_id = @p for no key update", tx, ("p", player));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) != null;
    }

    /// <summary>The banner's pity row, created with defaults when absent, locked <c>for update</c>.</summary>
    public static async Task<BannerPityRow> LockPityAsync(DbConnection c, DbTransaction tx, Guid player, Banner banner, CancellationToken ct)
    {
        await using (var ins = c.Command("insert into banner_pity (player_id, banner) values (@p, @b) on conflict (player_id, banner) do nothing", tx,
                                         ("p", player), ("b", banner.ToString())))
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var sel = c.Command("select pity, exp, total_pulls from banner_pity where player_id = @p and banner = @b for update", tx,
                                        ("p", player), ("b", banner.ToString()));
        await using var r = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("banner_pity row vanished");
        return new BannerPityRow(r.GetInt16(0), r.GetInt64(1), r.GetInt64(2));
    }

    /// <summary>The diamond balance: the sum of the player's <c>diamond_ledger</c>.</summary>
    public static async Task<long> DiamondBalanceAsync(DbConnection c, DbTransaction? tx, Guid player, CancellationToken ct)
    {
        await using var cmd = c.Command("select coalesce(sum(delta), 0)::bigint from diamond_ledger where player_id = @p", tx, ("p", player));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>One ticket's balance: the sum of the player's <c>ticket_ledger</c> rows for it.</summary>
    public static async Task<long> TicketBalanceAsync(DbConnection c, DbTransaction? tx, Guid player, Currency ticket, CancellationToken ct)
    {
        await using var cmd = c.Command("select coalesce(sum(delta), 0)::bigint from ticket_ledger where player_id = @p and ticket = @t", tx,
                                        ("p", player), ("t", ticket.ToString()));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The banner's collection from the player's item ledger (specs/08 section 4.3): one copy per <c>summon_log</c> row
    /// of this banner, plus <c>granted_items</c> of the banner's pool, plus the content's profile-created grants every
    /// account starts with (the starter weapon, 09 <c>tut_welcome</c>), capped at the copy cap as the Sim caps them.
    /// </summary>
    public static async Task<CollectionState> CollectionAsync(DbConnection c, DbTransaction tx, Guid player, ContentDb db, BannerDef def, CancellationToken ct)
    {
        var copies = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var g in db.ProfileCreatedGrants)
            if (g.Currency == null && string.Equals(g.ItemKind, def.Id.ToString(), StringComparison.Ordinal) && g.ItemId != null)
                copies[g.ItemId] = copies.GetValueOrDefault(g.ItemId) + g.Amount;

        string[] pool = def.Pool.Values.SelectMany(x => x).ToArray();
        await using var cmd = c.Command(
            "select item_id, sum(n)::bigint from (" +
            "  select item_id, count(*)::bigint as n from summon_log where player_id = @p and banner = @b group by item_id" +
            "  union all" +
            "  select item_id, sum(amount)::bigint as n from granted_items where player_id = @p and item_id = any(@ids) group by item_id" +
            ") x group by item_id", tx, ("p", player), ("b", def.Id.ToString()), ("ids", pool));
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string id = r.GetString(0);
                copies[id] = copies.GetValueOrDefault(id) + r.GetInt64(1);
            }

        int max = Sim.Summons.PullAlgorithm.MaxCopies(db);
        var col = new CollectionState();
        foreach (var (id, n) in copies)
            if (n > 0) col.Items[id] = new OwnedItem { Copies = (int)Math.Min(n, max) };
        return col;
    }

    /// <summary>One <c>summon_log</c> row per pull, in one statement.</summary>
    public static async Task InsertLogAsync(DbConnection c, DbTransaction tx, Guid player, string requestId, Banner banner, string payment,
                                            SummonRoll roll, CancellationToken ct)
    {
        var sql = new StringBuilder(
            "insert into summon_log (player_id, request_id, banner, batch_index, item_id, rarity, pity_before, pity_after, banner_level, " +
            "guarantee, rate_table_hash, u1, u2, u3, payment) values ");
        var args = new List<(string, object?)> { ("p", player), ("rid", requestId), ("b", banner.ToString()), ("pay", payment) };
        for (int i = 0; i < roll.Results.Length; i++)
        {
            var r = roll.Results[i];
            var a = roll.Audits[i];
            if (i > 0) sql.Append(", ");
            sql.Append($"(@p, @rid, @b, @i{i}, @item{i}, @rar{i}, @pb{i}, @pa{i}, @lv{i}, @g{i}, @h{i}, @ua{i}, @ub{i}, @uc{i}, @pay)");
            args.Add(("i" + i, (short)a.BatchIndex));
            args.Add(("item" + i, r.ItemId));
            args.Add(("rar" + i, RarityCodes.Of(r.Rarity)));
            args.Add(("pb" + i, (short)a.PityBefore));
            args.Add(("pa" + i, (short)a.PityAfter));
            args.Add(("lv" + i, (short)a.BannerLevel));
            args.Add(("g" + i, a.Guarantee));
            args.Add(("h" + i, a.RateTableHash));
            args.Add(("ua" + i, a.U1));
            args.Add(("ub" + i, a.U2));
            args.Add(("uc" + i, a.U3));
        }
        await using var cmd = c.Command(sql.ToString(), tx, args.ToArray());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The charge: a <c>diamond_ledger</c> row (<c>-cost</c>) or a <c>ticket_ledger</c> row (<c>-count</c>), reason <c>SUMMON</c>, ref <c>summon</c>/requestId.</summary>
    public static async Task InsertChargeAsync(DbConnection c, DbTransaction tx, Guid player, string requestId, Currency currency, long amount,
                                               CancellationToken ct)
    {
        await using var cmd = currency == Currency.DIAMOND
            ? c.Command("insert into diamond_ledger (player_id, delta, reason, ref_type, ref_id, request_id) values (@p, @d, 'SUMMON', 'summon', @rid, @rid)",
                        tx, ("p", player), ("d", checked((int)amount)), ("rid", requestId))
            : c.Command("insert into ticket_ledger (player_id, ticket, delta, reason, ref_type, ref_id, request_id) values (@p, @t, @d, 'SUMMON', 'summon', @rid, @rid)",
                        tx, ("p", player), ("t", currency.ToString()), ("d", checked((int)amount)), ("rid", requestId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task UpdatePityAsync(DbConnection c, DbTransaction tx, Guid player, Banner banner, BannerState st, CancellationToken ct)
    {
        await using var cmd = c.Command(
            "update banner_pity set pity = @pity, exp = @exp, total_pulls = @total, updated_at = now() where player_id = @p and banner = @b", tx,
            ("pity", (short)st.Pity), ("exp", st.Exp), ("total", st.TotalPulls), ("p", player), ("b", banner.ToString()));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Every <c>banner_pity</c> row of the player, by banner name.</summary>
    public async Task<Dictionary<string, BannerPityRow>> StateAsync(Guid player, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("select banner, pity, exp, total_pulls from banner_pity where player_id = @p", null, ("p", player));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new Dictionary<string, BannerPityRow>(StringComparer.Ordinal);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            rows[r.GetString(0)] = new BannerPityRow(r.GetInt16(1), r.GetInt64(2), r.GetInt64(3));
        return rows;
    }

    /// <summary>The player's newest <c>summon_log</c> rows, optionally of one banner.</summary>
    public async Task<SummonHistoryRowDto[]> HistoryAsync(Guid player, Banner? banner, int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "select request_id, banner, batch_index, item_id, rarity, pity_before, pity_after, banner_level, guarantee, payment, created_at " +
            "from summon_log where player_id = @p and (@b::text is null or banner = @b) order by created_at desc, id desc limit @n", null,
            ("p", player), ("b", banner?.ToString()), ("n", limit));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<SummonHistoryRowDto>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new SummonHistoryRowDto(r.GetString(0), r.GetString(1), r.GetInt16(2), r.GetString(3), r.GetString(4).Trim(), r.GetInt16(5),
                                             r.GetInt16(6), r.GetInt16(7), r.GetBoolean(8), r.GetString(9),
                                             new DateTimeOffset(r.GetDateTime(10), TimeSpan.Zero).ToUnixTimeSeconds()));
        return rows.ToArray();
    }
}
