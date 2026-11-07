using System.Data.Common;
using System.Text.Json;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Sim;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Analytics;

/// <summary>
/// Server-written economy events (issue #209): a server ledger write (summon debit, inbox claim, refund, IAP grant)
/// records its <c>currency_changed</c> row (06 section 13's fields) in the <c>events</c> table itself, in the ledger write's
/// own transaction, so the warehouse never depends on the client for money events. Rows carry <c>source = 'server'</c>
/// (the client's rows are <c>'client'</c>) and no session or device. Their <c>day_index</c> counts days from the account's
/// creation, <c>floor((ts - accounts.created_at) / DAY_SECONDS)</c>, read in the ledger write's transaction (issue #305;
/// specs/02 section 14.1), because the server never sees the save's <c>CreatedServerTime</c>.
/// </summary>
public sealed class ServerEconomyEvents
{
    readonly ServerOptions _options;
    readonly TimeProvider _clock;
    readonly IContentSets _content;

    public ServerEconomyEvents(IOptions<ServerOptions> options, TimeProvider clock, IContentSets content)
    {
        _options = options.Value;
        _clock = clock;
        _content = content;
    }

    /// <summary>
    /// The account fields a server event carries, read in the caller's transaction; null when there is no account row.
    /// <paramref name="lockRow"/> also takes the summon pull's <c>for no key update</c> lock on the row (08 section 4).
    /// </summary>
    public static async Task<EconomyAccount?> AccountAsync(DbConnection c, DbTransaction tx, Guid player, CancellationToken ct, bool lockRow = false)
    {
        await using var cmd = c.Command("select age_gate, created_at from accounts where player_id = @p" + (lockRow ? " for no key update" : ""),
                                        tx, ("p", player));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new EconomyAccount(r.GetInt16(0), r.GetFieldValue<DateTimeOffset>(1));
    }

    /// <summary>
    /// A server row's <c>dayIndex</c>: whole <c>DAY_SECONDS</c> since the account was created, floored (issue #305). For an
    /// offline-first guest (01 boot step 3) the account is younger than the save, so this runs behind the client's own
    /// <c>dayIndex</c> by the days played before the first connection.
    /// </summary>
    public static int DayIndex(DateTimeOffset ts, DateTimeOffset accountCreatedAt, int daySeconds) =>
        DayKeys.DayKey(ts.ToUnixTimeSeconds() - accountCreatedAt.ToUnixTimeSeconds(), daySeconds);

    /// <summary>
    /// A <c>currency_changed</c> row: <paramref name="currency"/> is the 00 section 5.2 code (<c>DIAMOND</c>, a ticket),
    /// <paramref name="reason"/> a CurrencyReason name, <paramref name="account"/> the player's <see cref="AccountAsync"/> row.
    /// </summary>
    public EventRow CurrencyChanged(Guid playerId, EconomyAccount account, string currency, long delta, long balanceAfter, string reason, string? refId)
    {
        if (!AnalyticsEventRules.IsReason(reason)) throw new ArgumentException($"'{reason}' is not a CurrencyReason (00 contract 6)", nameof(reason));
        var props = new Dictionary<string, object?>
        {
            ["currency"] = currency, ["delta"] = delta, ["balanceAfter"] = balanceAfter, ["reason"] = reason,
        };
        if (refId != null) props["refId"] = refId;
        return Row(AnalyticsEventRules.CurrencyChanged, playerId, account, props);
    }

    /// <summary>Any other server-emitted economy event (08 section 5's <c>iap_status</c>), with its owner section's fields.</summary>
    public EventRow Row(string name, Guid playerId, EconomyAccount account, IReadOnlyDictionary<string, object?> props)
    {
        if (!AnalyticsEventRules.Economy.Contains(name)) throw new ArgumentException($"'{name}' is not an economy event (02 section 14.1)", nameof(name));
        foreach (string k in props.Keys)
            if (!AnalyticsEventRules.IsCamelCase(k) || AnalyticsEventRules.EnvelopeFields.Contains(k))
                throw new ArgumentException($"'{k}' is not an owner field name (02 section 14.1)", nameof(props));
        var ts = _clock.GetUtcNow();
        return new EventRow(Guid.NewGuid(), ts, name, AnalyticsEventRules.HashPlayerId(playerId, _options.Analytics.RequireSalt()),
                            null, account.CreatedAt is { } created ? DayIndex(ts, created, _content.Active.Constants.DaySeconds) : null,
                            null, JsonSerializer.Serialize(props),
                            _options.RequireEnv(), AnalyticsEventRules.SourceServer,
                            null, null, null, null, null, true, AnalyticsEventRules.EnvelopeAgeGate(account.AgeGate));
    }

    /// <summary>Stores the rows in the caller's transaction (the ledger write's).</summary>
    public static Task<int> WriteAsync(DbConnection c, DbTransaction tx, IReadOnlyList<EventRow> rows, CancellationToken ct) =>
        AnalyticsEventStore.InsertAsync(c, tx, rows, ct);
}

/// <summary>
/// What a server-written event needs of the player's <c>accounts</c> row: <c>age_gate</c> and <c>created_at</c>.
/// <see cref="Unknown"/> (no row) writes age gate 0 and a null day index.
/// </summary>
public sealed record EconomyAccount(int AgeGate, DateTimeOffset? CreatedAt)
{
    public static readonly EconomyAccount Unknown = new(0, null);
}
