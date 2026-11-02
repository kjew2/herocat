using System.Data.Common;
using System.Text.Json;
using HeroCat.Server.Hosting;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Analytics;

/// <summary>
/// Server-written economy events (issue #209): a server ledger write (summon debit, inbox claim, refund, IAP grant)
/// records its <c>currency_changed</c> row (06 section 13's fields) in the <c>events</c> table itself, in the ledger write's
/// own transaction, so the warehouse never depends on the client for money events. Rows carry <c>source = 'server'</c>
/// (the client's rows are <c>'client'</c>), no session, device or day index.
/// TODO(batch6-gap-1): with no day_index, these rows group under a null day in currency_daily, which takes server-ledger
/// pairs only from them (migration 0008).
/// </summary>
public sealed class ServerEconomyEvents
{
    readonly ServerOptions _options;
    readonly TimeProvider _clock;

    public ServerEconomyEvents(IOptions<ServerOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    /// <summary>
    /// A <c>currency_changed</c> row: <paramref name="currency"/> is the 00 section 5.2 code (<c>DIAMOND</c>, a ticket),
    /// <paramref name="reason"/> a CurrencyReason name, <paramref name="accountAgeGate"/> the account's <c>age_gate</c>.
    /// </summary>
    public EventRow CurrencyChanged(Guid playerId, int accountAgeGate, string currency, long delta, long balanceAfter, string reason, string? refId)
    {
        if (!AnalyticsEventRules.IsReason(reason)) throw new ArgumentException($"'{reason}' is not a CurrencyReason (00 contract 6)", nameof(reason));
        var props = new Dictionary<string, object?>
        {
            ["currency"] = currency, ["delta"] = delta, ["balanceAfter"] = balanceAfter, ["reason"] = reason,
        };
        if (refId != null) props["refId"] = refId;
        return Row(AnalyticsEventRules.CurrencyChanged, playerId, accountAgeGate, props);
    }

    /// <summary>Any other server-emitted economy event (08 section 5's <c>iap_status</c>), with its owner section's fields.</summary>
    public EventRow Row(string name, Guid playerId, int accountAgeGate, IReadOnlyDictionary<string, object?> props)
    {
        if (!AnalyticsEventRules.Economy.Contains(name)) throw new ArgumentException($"'{name}' is not an economy event (02 section 14.1)", nameof(name));
        foreach (string k in props.Keys)
            if (!AnalyticsEventRules.IsCamelCase(k) || AnalyticsEventRules.EnvelopeFields.Contains(k))
                throw new ArgumentException($"'{k}' is not an owner field name (02 section 14.1)", nameof(props));
        return new EventRow(Guid.NewGuid(), _clock.GetUtcNow(), name, AnalyticsEventRules.HashPlayerId(playerId, _options.Analytics.RequireSalt()),
                            null, null, null, JsonSerializer.Serialize(props), _options.RequireEnv(), AnalyticsEventRules.SourceServer,
                            null, null, null, null, null, true, AnalyticsEventRules.EnvelopeAgeGate(accountAgeGate));
    }

    /// <summary>Stores the rows in the caller's transaction (the ledger write's).</summary>
    public static Task<int> WriteAsync(DbConnection c, DbTransaction tx, IReadOnlyList<EventRow> rows, CancellationToken ct) =>
        AnalyticsEventStore.InsertAsync(c, tx, rows, ct);
}
