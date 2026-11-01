using HeroCat.Sim.Content;

namespace HeroCat.Sim.Events
{
    // Sim-side shop events (06 sections 8.3-8.4 and 13; issue #174). Each is emitted before the CurrencyChanged rows it
    // explains, like QuestClaimed.

    /// <summary>ClaimDailyGift accepted; its CurrencyChanged (reason DAILY_GIFT) follows. Analytics <c>daily_gift_claimed</c>.</summary>
    public sealed record DailyGiftClaimed(Currency Currency, long Amount) : Event;

    /// <summary>
    /// ExchangeItem accepted: <see cref="Qty"/> of <see cref="OfferId"/> for <see cref="Diamonds"/> in all; the debit and the
    /// grant CurrencyChanged rows (reason SHOP_EXCHANGE) follow. Analytics <c>exchange</c> (offerId, qty, diamonds).
    /// </summary>
    public sealed record ItemExchanged(string OfferId, int Qty, long Diamonds) : Event;
}
