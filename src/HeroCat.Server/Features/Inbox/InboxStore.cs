using System.Data.Common;
using System.Text.Json;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Pipeline;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using Microsoft.AspNetCore.Mvc;

namespace HeroCat.Server.Features.Inbox;

/// <summary>A row of <c>inbox_items</c> (specs/02 section 12) as the Inbox reads it.</summary>
public sealed record InboxRow(Guid Id, string Kind, string TitleKey, string BodyKey, string ParamsJson, string GrantsJson,
                              DateTimeOffset CreatedAt, DateTimeOffset AvailableAt, DateTimeOffset? ExpiresAt);

/// <summary>Stores the final idempotency row inside the claim's transaction and returns the response to write (specs/08 section 4.3).</summary>
public delegate Task<IActionResult> CompleteWithin(DbConnection connection, DbTransaction transaction, object body, CancellationToken ct);

/// <summary>
/// The Inbox service's tables (specs/08 section 3.2; specs/02 section 12: <c>inbox_items</c>, <c>diamond_ledger</c>,
/// <c>ticket_ledger</c>, <c>granted_items</c>). A claim is one transaction: the row is locked, checked, marked claimed and
/// its currency lines written to the ledgers, and the final idempotency row commits with them.
/// </summary>
public sealed class InboxStore
{
    /// <summary>08 section 3.2: claim-all takes at most this many items, oldest first.</summary>
    public const int ClaimAllMax = 50;

    /// <summary>
    /// Claim-all also takes rows that expired at most this long ago, and reports them <c>INBOX_EXPIRED</c>, so an item that
    /// expired between the client's list and its claim-all is reported rather than silently missing. The nightly purge
    /// keeps the same window.
    /// </summary>
    public static readonly TimeSpan ExpiredReportWindow = TimeSpan.FromHours(1);

    /// <summary>The ticket currencies held in the server's <c>ticket_ledger</c> (00 decision 15; dungeon tickets stay client-side).</summary>
    public static readonly IReadOnlySet<string> LedgerTickets = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Currency.TICKET_SUMMON_WEAPON), nameof(Currency.TICKET_SUMMON_SKILL), nameof(Currency.TICKET_SUMMON_COMPANION),
        nameof(Currency.TICKET_SUMMON_RUNE),
    };

    public const string GrantKindCurrency = "currency";
    public const string GrantKindItem = "item";
    const string RefType = "inbox_item";
    const string Columns = "inbox_item_id, kind, title_key, body_key, params::text, grants::text, created_at, available_at, expires_at";

    readonly IDbConnectionFactory _db;
    readonly ILogger<InboxStore> _log;

    public InboxStore(IDbConnectionFactory db, ILogger<InboxStore> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// <c>GET /inbox</c>: the player's unclaimed, unexpired, available rows, plus the earliest future portion of each
    /// package (rows sharing <c>kind</c> and <c>source_ref</c>; issue #51), oldest first.
    /// </summary>
    public async Task<List<InboxRow>> ListAsync(Guid player, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            $"select {Columns} from inbox_items " +
            "where player_id = @p and claimed_at is null and (expires_at is null or expires_at > @now) and available_at <= @now " +
            $"union all select {Columns} from (" +
            "  select distinct on (kind, source_ref) * from inbox_items " +
            "  where player_id = @p and claimed_at is null and (expires_at is null or expires_at > @now) and available_at > @now " +
            "    and source_ref is not null " +
            "  order by kind, source_ref, available_at, inbox_item_id) upcoming " +
            "order by available_at, created_at, inbox_item_id", null,
            ("p", player), ("now", now));
        var rows = new List<InboxRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) rows.Add(ReadRow(r));
        return rows;
    }

    /// <summary>
    /// <c>POST /inbox/claim</c>. Throws the claim errors as <see cref="ApiException"/>s (nothing written); on success
    /// writes the ledgers, marks the row claimed and commits with the idempotency row from <paramref name="complete"/>.
    /// </summary>
    public async Task<IActionResult> ClaimAsync(Guid player, Guid id, string requestId, DateTimeOffset now, CompleteWithin complete, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);

        InboxRow? row = null;
        bool claimed = false;
        await using (var sel = c.Command(
            $"select {Columns}, claimed_at from inbox_items where inbox_item_id = @id and player_id = @p for update", tx,
            ("id", id), ("p", player)))
        await using (var r = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false))
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                row = ReadRow(r);
                claimed = !r.IsDBNull(9);
            }
        if (row == null) throw new ApiException(StatusCodes.Status404NotFound, InboxErrors.INBOX_NOT_FOUND, "No such inbox item.");

        var lines = ParseGrants(row.GrantsJson);
        string reason = ReasonFor(row.Kind);
        if (claimed)
            throw new ApiException(StatusCodes.Status409Conflict, InboxErrors.INBOX_ALREADY_CLAIMED, "This inbox item was already claimed.",
                                   new InboxClaimedDetails(IdString(id), lines ?? new List<InboxGrantLine>(), reason));
        string? refusal = Refusal(row, lines, now);
        if (refusal != null) throw new ApiException(InboxErrors.Status(refusal), refusal, RefusalMessage(refusal));

        var touched = new SortedSet<string>(StringComparer.Ordinal);
        await ClaimRowAsync(c, tx, player, row, lines!, reason, requestId, now, touched, ct).ConfigureAwait(false);
        var balances = await BalancesAsync(c, tx, player, touched, ct).ConfigureAwait(false);
        var body = new InboxClaimResponse(IdString(id), lines!, balances, reason, now.ToUnixTimeSeconds());
        var result = await complete(c, tx, body, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// <c>POST /inbox/claim-all</c>: up to <see cref="ClaimAllMax"/> unclaimed available rows, oldest first, in one
    /// transaction. A row that cannot be claimed (expired within <see cref="ExpiredReportWindow"/>, an unsupported or bad
    /// grant) is skipped and reported with its code; the rest are claimed.
    /// </summary>
    public async Task<IActionResult> ClaimAllAsync(Guid player, string requestId, DateTimeOffset now, CompleteWithin complete, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);

        var rows = new List<InboxRow>();
        await using (var sel = c.Command(
            $"select {Columns} from inbox_items " +
            "where player_id = @p and claimed_at is null and available_at <= @now and (expires_at is null or expires_at > @oldest) " +
            "order by available_at, created_at, inbox_item_id limit @max for update", tx,
            ("p", player), ("now", now), ("oldest", now - ExpiredReportWindow), ("max", ClaimAllMax)))
        await using (var r = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await r.ReadAsync(ct).ConfigureAwait(false)) rows.Add(ReadRow(r));

        var touched = new SortedSet<string>(StringComparer.Ordinal);
        var results = new List<InboxClaimAllRow>(rows.Count);
        foreach (var row in rows)
        {
            var lines = ParseGrants(row.GrantsJson);
            string? refusal = Refusal(row, lines, now);
            if (refusal != null)
            {
                results.Add(new InboxClaimAllRow(IdString(row.Id), null, null, refusal));
                continue;
            }
            string reason = ReasonFor(row.Kind);
            await ClaimRowAsync(c, tx, player, row, lines!, reason, requestId, now, touched, ct).ConfigureAwait(false);
            results.Add(new InboxClaimAllRow(IdString(row.Id), lines, reason, null));
        }

        var balances = await BalancesAsync(c, tx, player, touched, ct).ConfigureAwait(false);
        var result = await complete(c, tx, new InboxClaimAllResponse(results, balances), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The nightly purge (specs/08 section 3.2): deletes unclaimed rows that expired before
    /// <paramref name="now"/> minus <see cref="ExpiredReportWindow"/>. Claimed rows stay (the audit trail of the ledger rows).
    /// </summary>
    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("delete from inbox_items where claimed_at is null and expires_at is not null and expires_at <= @cutoff", null,
            ("cutoff", now - ExpiredReportWindow));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Why a row cannot be claimed now, as its error code, or null. The order is the claim's: expiry, availability, grants.</summary>
    string? Refusal(InboxRow row, List<InboxGrantLine>? lines, DateTimeOffset now)
    {
        if (row.ExpiresAt is DateTimeOffset e && e <= now) return InboxErrors.INBOX_EXPIRED;
        if (row.AvailableAt > now) return InboxErrors.INBOX_NOT_AVAILABLE;
        if (lines == null)
        {
            _log.LogError("inbox item {InboxItemId} has a malformed grants value; refused as {Code}", row.Id, InboxErrors.INBOX_BAD_GRANT);
            return InboxErrors.INBOX_BAD_GRANT;
        }
        // TODO(#271): item lines are refused until it is decided how the client's ApplyInboxGrant applies them.
        if (lines.Any(l => l.Kind == GrantKindItem)) return InboxErrors.INBOX_ITEM_GRANT_UNSUPPORTED;
        foreach (var l in lines)
            if (l.Kind != GrantKindCurrency || !IsCurrency(l.Id) || l.Amount <= 0 || l.Amount > int.MaxValue)
            {
                _log.LogError("inbox item {InboxItemId} has a bad grant line {Kind}/{Id}/{Amount}; refused as {Code}", row.Id, l.Kind, l.Id, l.Amount,
                              InboxErrors.INBOX_BAD_GRANT);
                return InboxErrors.INBOX_BAD_GRANT;
            }
        return null;
    }

    static string RefusalMessage(string code) => code switch
    {
        InboxErrors.INBOX_EXPIRED => "This inbox item has expired.",
        InboxErrors.INBOX_NOT_AVAILABLE => "This inbox item is not available yet.",
        InboxErrors.INBOX_ITEM_GRANT_UNSUPPORTED => "This inbox item grants an item, which cannot be claimed yet.",
        _ => "This inbox item's grants could not be read.",
    };

    static bool IsCurrency(string id) => Enum.TryParse<Currency>(id, ignoreCase: false, out var c) && Enum.IsDefined(c) && c.ToString() == id;

    /// <summary>
    /// Marks the row claimed and writes its lines: DIAMOND to <c>diamond_ledger</c>, summon tickets to <c>ticket_ledger</c>
    /// (same reason, <c>ref_type = inbox_item</c>). Other currencies (Rock Candy, Fish Coins; 06 rule 9.4) have no server
    /// ledger and are only returned for the client's <c>ApplyInboxGrant</c>.
    /// </summary>
    static async Task ClaimRowAsync(DbConnection c, DbTransaction tx, Guid player, InboxRow row, List<InboxGrantLine> lines, string reason,
                                    string requestId, DateTimeOffset now, ISet<string> touched, CancellationToken ct)
    {
        await using (var upd = c.Command(
            "update inbox_items set claimed_at = @now, claimed_request_id = @rid where inbox_item_id = @id and claimed_at is null", tx,
            ("now", now), ("rid", requestId), ("id", row.Id)))
            if (await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new InvalidOperationException($"inbox item {row.Id} was not claimable under its row lock");

        string refId = row.Id.ToString("D");
        foreach (var l in lines)
        {
            if (l.Id == nameof(Currency.DIAMOND))
            {
                // created_at is the database's clock: the row must land in a partition (specs/02 section 12).
                await using var ins = c.Command(
                    "insert into diamond_ledger (player_id, delta, reason, ref_type, ref_id, request_id) values (@p, @d, @reason, @rt, @ref, @rid)", tx,
                    ("p", player), ("d", (int)l.Amount), ("reason", reason), ("rt", RefType), ("ref", refId), ("rid", requestId));
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else if (LedgerTickets.Contains(l.Id))
            {
                // request_id stays null: ticket_ledger's unique (player_id, request_id) index allows one row per request,
                // and a claim can write several (two ticket lines, or claim-all). The request id is on inbox_items.claimed_request_id.
                await using var ins = c.Command(
                    "insert into ticket_ledger (player_id, ticket, delta, reason, ref_type, ref_id) values (@p, @t, @d, @reason, @rt, @ref)", tx,
                    ("p", player), ("t", l.Id), ("d", (int)l.Amount), ("reason", reason), ("rt", RefType), ("ref", refId));
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                touched.Add(l.Id);
            }
        }
    }

    /// <summary>The player's ledger balances after the claim: DIAMOND always, plus each summon ticket the claim granted.</summary>
    static async Task<IReadOnlyDictionary<string, long>> BalancesAsync(DbConnection c, DbTransaction tx, Guid player, IEnumerable<string> currencies,
                                                                       CancellationToken ct)
    {
        var balances = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (string currency in currencies.Prepend(nameof(Currency.DIAMOND)).Distinct())
        {
            await using var cmd = currency == nameof(Currency.DIAMOND)
                ? c.Command("select coalesce(sum(delta), 0) from diamond_ledger where player_id = @p", tx, ("p", player))
                : c.Command("select coalesce(sum(delta), 0) from ticket_ledger where player_id = @p and ticket = @t", tx, ("p", player), ("t", currency));
            balances[currency] = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        return balances;
    }

    /// <summary>
    /// The ledger reason of an item's kind, as the client's <c>InboxRules.ReasonFor</c> maps 06's types: <c>iap</c> and
    /// <c>package_daily</c> are IAP, <c>refund</c> REFUND, <c>admin</c> ADMIN, everything else (mail, compensation, coupon) MAIL.
    /// </summary>
    public static string ReasonFor(string kind) => kind.ToLowerInvariant() switch
    {
        "iap" or "package_daily" => nameof(CurrencyReason.IAP),
        "refund" => nameof(CurrencyReason.REFUND),
        "admin" => nameof(CurrencyReason.ADMIN),
        _ => nameof(CurrencyReason.MAIL),
    };

    /// <summary>The wire form of an item id.</summary>
    public static string IdString(Guid id) => id.ToString("D");

    /// <summary>The <c>grants</c> jsonb as lines, or null when it is not an array of <c>{kind, id, amount}</c> objects with whole amounts.</summary>
    public static List<InboxGrantLine>? ParseGrants(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var lines = new List<InboxGrantLine>();
            foreach (var g in doc.RootElement.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object ||
                    !g.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
                    !g.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !g.TryGetProperty("amount", out var amount) || !amount.TryGetInt64(out long n))
                    return null;
                lines.Add(new InboxGrantLine(kind.GetString()!, id.GetString()!, n));
            }
            return lines;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static InboxRow ReadRow(DbDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8));
}
