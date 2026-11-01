using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;

namespace HeroCat.Sim
{
    /// <summary>
    /// The sim-side shop: the daily free gift and the diamond-for-dungeon-ticket exchange (06 sections 8.3-8.4; issue #174).
    /// Both are client-side grants and spends: they apply through the one Ledger path, which counts them into
    /// Stats.ClientTicketsEarned[WEAPON][DAILY_GIFT] and Stats.ClientDiamondsSpent[SHOP_EXCHANGE] for the server credit on
    /// upload (06 section 1.1 rule 5, issue #49). The flag and the counts reset at the day rollover (06 section 4.2). IAP,
    /// packages and the catalog are the server's (issue #175).
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>The feature_unlocks row that gates the shop commands (09 section 5.34; ALWAYS in MVP, E063; absent in M0).</summary>
        const string ShopFeature = "SHOP";

        partial void ResetShopDaily(int dayKey)
        {
            State.Shop.DailyGiftClaimed = false;
            State.Shop.ExchangeCounts.Clear();
        }

        internal ApplyResult HandleClaimDailyGift(ClaimDailyGift c)
        {
            EnsureDayNow();   // 06 section 4.2: a claim on a new day sees the new day's flag
            if (!Db.IsUnlocked(ShopFeature, State)) return ApplyResult.Reject(RejectCode.Locked, ShopRejectReasons.SHOP_LOCKED);
            if (State.Shop.DailyGiftClaimed)
                return ApplyResult.Reject(RejectCode.AlreadyClaimed, ShopRejectReasons.DAILY_GIFT_ALREADY_CLAIMED);
            long n = Db.Constants.DailyGiftWeaponTickets;
            State.Shop.DailyGiftClaimed = true;
            Emit(new DailyGiftClaimed(Currency.TICKET_SUMMON_WEAPON, n));
            Ledger.Apply(State, new CurrencyDelta(Currency.TICKET_SUMMON_WEAPON, n), CurrencyReason.DAILY_GIFT, this);
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleExchangeItem(ExchangeItem c)
        {
            EnsureDayNow();   // a purchase on a new day counts against the new day's limit
            if (!Db.IsUnlocked(ShopFeature, State)) return ApplyResult.Reject(RejectCode.Locked, ShopRejectReasons.SHOP_LOCKED);
            var offer = Db.ExchangeOffer(c.OfferId);
            if (offer == null) return ApplyResult.Reject(RejectCode.InvalidTarget, ShopRejectReasons.UNKNOWN_ID);
            if (c.Qty < 1) return ApplyResult.Reject(RejectCode.BadArgument, ShopRejectReasons.BAD_QUANTITY);
            State.Shop.ExchangeCounts.TryGetValue(offer.Id, out int used);
            if (c.Qty > offer.DailyLimit - used) return ApplyResult.Reject(RejectCode.Capped, ShopRejectReasons.EXCHANGE_LIMIT_REACHED);
            long price = offer.PriceAmount * c.Qty, grant = offer.GrantAmount * c.Qty;   // qty <= daily_limit, so no overflow
            if (!State.Wallet.CanAfford(offer.PriceCurrency, price))
                return ApplyResult.Reject(RejectCode.CannotAfford, CollectionRejectReasons.NotEnough(offer.PriceCurrency));

            State.Shop.ExchangeCounts[offer.Id] = used + c.Qty;
            Emit(new ItemExchanged(offer.Id, c.Qty, price));
            Ledger.Apply(State, new CurrencyDelta(offer.PriceCurrency, -price), CurrencyReason.SHOP_EXCHANGE, this);
            Ledger.Apply(State, new CurrencyDelta(offer.GrantCurrency, grant), CurrencyReason.SHOP_EXCHANGE, this);
            return ApplyResult.Accepted;
        }
    }
}
