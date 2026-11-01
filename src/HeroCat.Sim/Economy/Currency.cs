using HeroCat.Sim.Content;

namespace HeroCat.Sim.Economy
{
    /// <summary>The closed reason list of 00 contract 6, in that order (issue #11). Every currency change carries one.</summary>
    public enum CurrencyReason : byte
    {
        KILL, BOSS, OFFLINE, LOGIN, TUTORIAL, FIRST_CLEAR, QUEST, ACHIEVEMENT, DAILY_GIFT, DAILY_RESET,
        SUMMON, TRAINING, RESEARCH, AWAKENING, LIMIT_BREAK, IAP, MAIL, DUNGEON, FISH_DISCOVERY, FISH_MERGE,
        SHOP_EXCHANGE, REFUND, ADMIN,
    }

    /// <summary>A signed amount of one currency (06 section 1). A readonly struct, not a record struct: the sim is C# 9.</summary>
    public readonly struct CurrencyDelta
    {
        public CurrencyDelta(Currency currency, double amount)
        {
            Currency = currency;
            Amount = amount;
        }

        public Currency Currency { get; }
        public double Amount { get; }

        public override string ToString() => Currency + " " + Amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
