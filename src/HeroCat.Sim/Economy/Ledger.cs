using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Economy
{
    /// <summary>
    /// <c>Wallet.Apply(CurrencyDelta, CurrencyReason)</c> of 06 section 1 and 00 contract 6: the single path every currency
    /// change in the sim takes. It lives here rather than on <see cref="Wallet"/> because HERO_EXP credits
    /// <c>Hero.Exp</c>, which the wallet cannot reach (00 contract 9).
    /// </summary>
    public static class Ledger
    {
        /// <summary>
        /// Applies <paramref name="d"/> with <paramref name="reason"/>. Returns false and changes nothing when the amount is
        /// NaN or infinite, not whole on a counted currency, or a debit that would go below zero (it rejects, it never
        /// clamps). Otherwise sets the balance, updates the lifetime counters and emits exactly one CurrencyChanged.
        /// </summary>
        public static bool Apply(GameState s, in CurrencyDelta d, CurrencyReason reason, IEventEmitter events)
        {
            double amount = d.Amount;
            if (double.IsNaN(amount) || double.IsInfinity(amount)) return false;
            var c = d.Currency;
            if (Wallet.IsCounted(c) && amount != System.Math.Floor(amount)) return false;

            double before = c == Currency.HERO_EXP ? s.Hero.Exp : s.Wallet.Get(c);
            double next = before + amount;
            if (next < 0 || double.IsInfinity(next)) return false;
            if (c == Currency.HERO_EXP)
                s.Hero.Exp = next;   // CheckHeroLevel runs below, after the CurrencyChanged (03 section 5.2)
            else
                s.Wallet.Set(c, next);

            Count(s.Stats, c, amount, reason);
            if (reason == CurrencyReason.KILL && (c == Currency.GOLD || c == Currency.HERO_EXP) && events is Simulation folding && folding.CoalescingKills)
                folding.FoldKillCurrency(c, amount);   // never allocated (01 2.3); the batch keeps the amount for analytics
            else events?.Emit(new CurrencyChanged(c, amount, next, reason));
            if (c == Currency.HERO_EXP && events is Simulation sim) sim.CheckHeroLevel();   // the one EXP path (03 section 5.2)
            return true;
        }

        /// <summary>True when <paramref name="reason"/> is applied by the sim itself, so the server credits it on upload (issue #49).</summary>
        public static bool IsClientReason(CurrencyReason reason) =>
            !(reason == CurrencyReason.IAP || reason == CurrencyReason.MAIL || reason == CurrencyReason.ADMIN ||
              reason == CurrencyReason.REFUND || reason == CurrencyReason.SUMMON);

        static void Count(LifetimeStats st, Currency c, double amount, CurrencyReason reason)
        {
            switch (c)
            {
                case Currency.GOLD:
                    if (amount > 0) st.GoldEarned += amount; else st.GoldSpent -= amount;
                    return;
                case Currency.FISH_COIN:
                    if (amount > 0) st.FishCoinEarned += amount;
                    return;
                case Currency.DIAMOND:
                    long n = (long)amount;
                    if (n > 0)
                    {
                        st.DiamondsEarned += n;
                        if (reason == CurrencyReason.IAP) st.DiamondsPurchased += n;
                        if (IsClientReason(reason)) Add(st.ClientDiamondsEarned, reason, n);
                    }
                    else if (n < 0)
                    {
                        st.DiamondsSpent -= n;
                        if (IsClientReason(reason)) Add(st.ClientDiamondsSpent, reason, -n);
                    }
                    return;
                default:
                    int banner = Wallet.SummonTicketIndex(c);
                    if (banner >= 0 && amount > 0 && IsClientReason(reason))
                    {
                        var b = (Banner)banner;
                        if (!st.ClientTicketsEarned.TryGetValue(b, out var byReason))
                            st.ClientTicketsEarned[b] = byReason = new System.Collections.Generic.Dictionary<CurrencyReason, long>();
                        Add(byReason, reason, (long)amount);
                    }
                    return;
            }
        }

        static void Add(System.Collections.Generic.Dictionary<CurrencyReason, long> map, CurrencyReason r, long n)
        {
            map.TryGetValue(r, out long v);
            map[r] = v + n;
        }
    }
}
