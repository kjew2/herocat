namespace HeroCat.Server.Features.Summon;

/// <summary><c>POST /summon/pull</c> body (specs/08 section 4.3). <c>payment</c> is <c>DIAMOND</c> or <c>TICKET</c>.</summary>
public sealed record SummonPullRequest(string? Banner, int? Count, string? Payment, string? ClientContentVersion);

/// <summary>One pull of the response: 01's <c>SummonPull</c> with the rarity as its one-letter code.</summary>
public sealed record SummonPullDto(string ItemId, string Rarity, bool IsNew, int LevelBefore, int LevelAfter, int CopiesAfter,
                                   long OverflowRockCandy, bool GuaranteeApplied);

/// <summary>What the pull cost, as a negative amount of <c>DIAMOND</c> or the banner's ticket currency.</summary>
public sealed record CurrencyDebitDto(string Currency, long Amount);

/// <summary>
/// <c>POST /summon/pull</c> 200: 01 section 2.5's <c>SummonResultPayload</c> on the wire, plus <c>balances</c> (the
/// ledger balances of <c>DIAMOND</c> and the banner's ticket after the charge) and the <c>rateTableHash</c> of the table
/// the last pull used (each pull's own is in <c>summon_log</c>).
/// </summary>
public sealed record SummonPullResponse(string RequestId, string Banner, int Count, SummonPullDto[] Pulls, int PityBefore, int PityAfter,
                                        int BannerLevelBefore, int BannerLevelAfter, long BannerExpAfter, CurrencyDebitDto[] Debits,
                                        IReadOnlyDictionary<string, long> Balances, string RateTableHash, long ServerTime);

/// <summary>One banner of <c>GET /summon/state</c>: server pity, derived level, cumulative EXP, total pulls.</summary>
public sealed record BannerStateDto(int Pity, int Level, long BannerExp, long TotalPulls);

/// <summary><c>GET /summon/state</c> (specs/08 section 4.5): every banner of the active content, keyed by its name.</summary>
public sealed record SummonStateResponse(IReadOnlyDictionary<string, BannerStateDto> Banners);

/// <summary>One <c>summon_log</c> row as the player sees it: no uniforms (specs/08 section 4.4).</summary>
public sealed record SummonHistoryRowDto(string RequestId, string Banner, int BatchIndex, string ItemId, string Rarity, int PityBefore,
                                         int PityAfter, int BannerLevel, bool Guarantee, string Payment, long CreatedAt);

/// <summary><c>GET /summon/history</c>: the player's own rows, newest first.</summary>
public sealed record SummonHistoryResponse(SummonHistoryRowDto[] Pulls);

/// <summary>The Summon endpoints' own error codes (specs/08 section 4.3).</summary>
public static class SummonErrors
{
    public const string INSUFFICIENT_FUNDS = "INSUFFICIENT_FUNDS";
    public const string PAYMENT_MISMATCH = "PAYMENT_MISMATCH";
    public const string BANNER_CLOSED = "BANNER_CLOSED";
    public const string SUMMON_DISABLED = "SUMMON_DISABLED";
}

/// <summary>The wire's <c>payment</c> values.</summary>
public static class SummonPayments
{
    public const string Diamond = "DIAMOND";
    public const string Ticket = "TICKET";
}
