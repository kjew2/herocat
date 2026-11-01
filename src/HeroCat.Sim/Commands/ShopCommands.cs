namespace HeroCat.Sim.Commands
{
    // Sim-side shop commands (06 sections 8.3-8.4, 01 section 2.1; issue #174). Handlers are in Simulation.Shop.cs. Both
    // run EnsureDay first, then the SHOP feature gate, then their own checks; a reject changes nothing and emits nothing.

    /// <summary>Once per day key: DAILY_GIFT_WEAPON_TICKETS TICKET_SUMMON_WEAPON with reason DAILY_GIFT, DailyGiftClaimed.</summary>
    public sealed record ClaimDailyGift : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleClaimDailyGift(this);
    }

    /// <summary>
    /// Buys <see cref="Qty"/> of a shop_exchange offer: debits Qty x price and grants Qty x grant, both with reason
    /// SHOP_EXCHANGE, within the offer's daily limit (06 section 8.4).
    /// </summary>
    public sealed record ExchangeItem(string OfferId, int Qty) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleExchangeItem(this);
    }

    /// <summary>06 section 1.3's shop reject reasons, localized as <c>reject.&lt;reason_lower&gt;</c>.</summary>
    public static class ShopRejectReasons
    {
        /// <summary>(Locked) the feature_unlocks row SHOP is not in this build (an M0 build: the shop is MVP, 06 section 1.2).</summary>
        public const string SHOP_LOCKED = "SHOP_LOCKED";

        /// <summary>(AlreadyClaimed) ClaimDailyGift a second time on one day key.</summary>
        public const string DAILY_GIFT_ALREADY_CLAIMED = "DAILY_GIFT_ALREADY_CLAIMED";

        /// <summary>(InvalidTarget) ExchangeItem with an offer id not in shop_exchange.</summary>
        public const string UNKNOWN_ID = "UNKNOWN_ID";

        /// <summary>(BadArgument) ExchangeItem with qty &lt; 1.</summary>
        public const string BAD_QUANTITY = "BAD_QUANTITY";

        /// <summary>(Capped) ExchangeItem whose qty exceeds what is left of the offer's daily limit.</summary>
        public const string EXCHANGE_LIMIT_REACHED = "EXCHANGE_LIMIT_REACHED";
    }
}
