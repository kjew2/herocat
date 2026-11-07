using System.Diagnostics.Metrics;
using HeroCat.Server.Features.Analytics;
using HeroCat.Server.Infrastructure;
using HeroCat.Server.Infrastructure.Redis;
using HeroCat.Server.Pipeline;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.State;
using Microsoft.AspNetCore.Mvc;

namespace HeroCat.Server.Features.Summon;

/// <summary>Summon metrics (specs/08 section 9.4), exported with the rest of <see cref="Observability.MetricCatalog"/>.</summary>
public static class SummonMetrics
{
    public const string PullsTotal = "summon_pulls_total";

    static readonly Counter<long> Pulls = ServerMetrics.Meter.CreateCounter<long>(PullsTotal, "{pull}", "Committed summon pulls, by banner and rarity.");

    public static void RecordPull(Banner banner, Rarity rarity) =>
        Pulls.Add(1, new KeyValuePair<string, object?>("banner", banner.ToString()), new KeyValuePair<string, object?>("rarity", RarityCodes.Of(rarity)));
}

/// <summary>
/// The <c>POST /summon/pull</c> handler (specs/08 section 4.3, issue #199). The idempotency middleware has already
/// replayed or refused a known request id; this takes the per-player Redis lock, resolves the client's content set,
/// and in one transaction locks the account and the banner's pity row, charges diamonds or tickets, runs the Sim's pull
/// algorithm with CSPRNG uniforms, writes one <c>summon_log</c> row per pull, the ledger row with its server-side
/// <c>currency_changed</c> event (specs/08 section 3.5) and the pity row, and stores the final idempotency row, so a retry after commit replays the same bytes.
/// </summary>
public sealed class SummonService
{
    readonly SummonStore _store;
    readonly IContentSets _content;
    readonly ISummonConfigSource _config;
    readonly SummonLock _lock;
    readonly IUniformSourceFactory _uniforms;
    readonly ServerEconomyEvents _events;
    readonly TimeProvider _clock;

    public SummonService(SummonStore store, IContentSets content, ISummonConfigSource config, SummonLock summonLock, IUniformSourceFactory uniforms,
                         ServerEconomyEvents events, TimeProvider clock)
    {
        _events = events;
        _store = store;
        _content = content;
        _config = config;
        _lock = summonLock;
        _uniforms = uniforms;
        _clock = clock;
    }

    public async Task<IActionResult> PullAsync(Guid player, string requestId, SummonPullRequest req, IIdempotencyFeature? idempotency, CancellationToken ct)
    {
        var banner = ParseBanner(req.Banner);
        int count = req.Count is 1 or 10 ? req.Count.Value
            : throw ApiException.Of(ErrorCodes.VALIDATION, "count must be 1 or 10.", new { field = "count" });
        string payment = req.Payment is SummonPayments.Diamond or SummonPayments.Ticket ? req.Payment
            : throw ApiException.Of(ErrorCodes.VALIDATION, "payment must be DIAMOND or TICKET.", new { field = "payment" });
        if (string.IsNullOrWhiteSpace(req.ClientContentVersion))
            throw ApiException.Of(ErrorCodes.VALIDATION, "clientContentVersion is required.", new { field = "clientContentVersion" });

        // The kill switch is a 503, which the idempotency middleware never stores: once it is lifted the retry rolls.
        var switches = await _config.GetAsync(ct).ConfigureAwait(false);
        if (!switches.IsEnabled(banner))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, SummonErrors.SUMMON_DISABLED, "Summons on this banner are disabled.",
                                   new { banner = banner.ToString() });

        await using var lease = await _lock.AcquireAsync(player, ct).ConfigureAwait(false);

        var db = _content.Resolve(req.ClientContentVersion);
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (!db.Banners.TryGetValue(banner, out var def) || !def.Enabled || def.Levels.Count == 0 || !switches.IsOpen(banner, now))
            throw new ApiException(StatusCodes.Status409Conflict, SummonErrors.BANNER_CLOSED, "This banner is not open.", new { banner = banner.ToString() });
        if (count != 1 && count != def.GuaranteeBatchSize)
            throw ApiException.Of(ErrorCodes.VALIDATION, $"count must be 1 or {def.GuaranteeBatchSize}.", new { field = "count" });

        await using var c = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var account = await SummonStore.LockAccountAsync(c, tx, player, ct).ConfigureAwait(false)
            ?? throw ApiException.Of(ErrorCodes.UNAUTHENTICATED, "No account for this token.");
        var pity = await SummonStore.LockPityAsync(c, tx, player, banner, ct).ConfigureAwait(false);

        // 06 rule, tickets first: a diamond pull while holding enough tickets is a stale client. Checked before the funds
        // check so a player with tickets but too few diamonds gets 409, not 402 (issue #199; acceptance 6).
        long diamonds = await SummonStore.DiamondBalanceAsync(c, tx, player, ct).ConfigureAwait(false);
        long tickets = await SummonStore.TicketBalanceAsync(c, tx, player, def.TicketCurrency, ct).ConfigureAwait(false);
        Currency charged;
        long cost;
        if (payment == SummonPayments.Diamond)
        {
            if (tickets >= count)
                throw new ApiException(StatusCodes.Status409Conflict, SummonErrors.PAYMENT_MISMATCH, "Tickets are spent before diamonds.",
                                       new { expected = SummonPayments.Ticket, tickets });
            charged = Currency.DIAMOND;
            cost = (long)def.PullCostDiamonds * count;
            if (diamonds < cost) throw InsufficientFunds(charged, cost, diamonds);
        }
        else
        {
            charged = def.TicketCurrency;
            cost = count;
            if (tickets < cost) throw InsufficientFunds(charged, cost, tickets);
        }

        var col = await SummonStore.CollectionAsync(c, tx, player, db, def, ct).ConfigureAwait(false);
        var st = new BannerState { Pity = pity.Pity, Exp = pity.Exp, TotalPulls = pity.TotalPulls };
        int levelBefore = def.LevelFor(st.Exp);
        var roll = SummonEngine.Roll(db, def, st, col, _uniforms.Create(), count);

        await SummonStore.InsertLogAsync(c, tx, player, requestId, banner, payment, roll, ct).ConfigureAwait(false);
        await SummonStore.InsertChargeAsync(c, tx, player, requestId, charged, -cost, ct).ConfigureAwait(false);
        await SummonStore.UpdatePityAsync(c, tx, player, banner, st, ct).ConfigureAwait(false);

        if (charged == Currency.DIAMOND) diamonds -= cost;
        else tickets -= cost;
        await ServerEconomyEvents.WriteAsync(c, tx, new[]
        {
            _events.CurrencyChanged(player, account,charged.ToString(), -cost, charged == Currency.DIAMOND ? diamonds : tickets,
                                    nameof(CurrencyReason.SUMMON), requestId),
        }, ct).ConfigureAwait(false);
        var body = new SummonPullResponse(
            requestId, banner.ToString(), count,
            roll.Results.Select(r => new SummonPullDto(r.ItemId, RarityCodes.Of(r.Rarity), r.IsNew, r.LevelBefore, r.LevelAfter, r.CopiesAfter,
                                                       r.OverflowRockCandy, r.GuaranteeApplied)).ToArray(),
            pity.Pity, st.Pity, levelBefore, def.LevelFor(st.Exp), st.Exp,
            new[] { new CurrencyDebitDto(charged.ToString(), -cost) },
            new Dictionary<string, long> { [Currency.DIAMOND.ToString()] = diamonds, [def.TicketCurrency.ToString()] = tickets },
            RateTableHash.Hex(roll.Audits[^1].RateTableHash), now);

        IActionResult result = idempotency != null
            ? await idempotency.CompleteWithinAsync(c, tx, StatusCodes.Status200OK, body, ct).ConfigureAwait(false)
            : new OkObjectResult(body);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        foreach (var r in roll.Results) SummonMetrics.RecordPull(banner, r.Rarity);
        return result;
    }

    /// <summary><c>GET /summon/state</c>: every banner of the active content, with defaults for one never pulled.</summary>
    public async Task<SummonStateResponse> StateAsync(Guid player, CancellationToken ct)
    {
        var rows = await _store.StateAsync(player, ct).ConfigureAwait(false);
        var db = _content.Active;
        var banners = new SortedDictionary<string, BannerStateDto>(StringComparer.Ordinal);
        foreach (var def in db.Banners.Values.Where(d => d.Levels.Count > 0).OrderBy(d => d.SortOrder))
        {
            var row = rows.GetValueOrDefault(def.Id.ToString()) ?? new BannerPityRow(0, 0, 0);
            banners[def.Id.ToString()] = new BannerStateDto(row.Pity, def.LevelFor(row.Exp), row.Exp, row.TotalPulls);
        }
        return new SummonStateResponse(banners);
    }

    public const int HistoryDefaultLimit = 100;
    public const int HistoryMaxLimit = 500;

    /// <summary><c>GET /summon/history</c>: the player's newest rows (default 100, at most 500), optionally of one banner.</summary>
    public async Task<SummonHistoryResponse> HistoryAsync(Guid player, string? banner, int? limit, CancellationToken ct)
    {
        Banner? b = string.IsNullOrEmpty(banner) ? null : ParseBanner(banner);
        int n = limit ?? HistoryDefaultLimit;
        if (n < 1 || n > HistoryMaxLimit)
            throw ApiException.Of(ErrorCodes.VALIDATION, $"limit must be 1..{HistoryMaxLimit}.", new { field = "limit" });
        return new SummonHistoryResponse(await _store.HistoryAsync(player, b, n, ct).ConfigureAwait(false));
    }

    static Banner ParseBanner(string? s) =>
        s != null && Enum.TryParse<Banner>(s, ignoreCase: false, out var b) && Enum.IsDefined(b) && s == b.ToString()
            ? b
            : throw ApiException.Of(ErrorCodes.VALIDATION, "Unknown banner.", new { field = "banner" });

    static ApiException InsufficientFunds(Currency currency, long need, long have) =>
        new(StatusCodes.Status402PaymentRequired, SummonErrors.INSUFFICIENT_FUNDS,
            currency == Currency.DIAMOND ? "Not enough diamonds." : "Not enough tickets.",
            new { need, have, currency = currency.ToString() });
}
