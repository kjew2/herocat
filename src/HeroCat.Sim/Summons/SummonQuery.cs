using System;
using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Summons
{
    /// <summary>One collectible as the collection screens show it (04 section 11). Unowned items are included.</summary>
    public sealed record CollectibleVm(
        string Id, Rarity Rarity, string NameKey, string Icon,
        bool Owned, int Copies, int Level, int LimitBreaks, int EffectiveLevel,
        int CopiesToNextLevel,                    // 0 at level 10
        int NextLimitBreakCost,                   // Rock Candy; 0 when maxed or unowned
        double ValueNow, double ValueNextLevel,   // weapons: equipped pct; skills and companions: dps_pct; after LevelMult (0 / level-1 value when unowned)
        double PassiveNow,                        // weapons only: passive pct after LevelMult; 0 otherwise
        bool Equipped, int? Slot,
        bool IsNew);                              // from the client's NEW badge store (ClientPrefs), never sim state

    /// <summary>A collection screen (04 section 11).</summary>
    public sealed record CollectionVm(
        Banner Banner, IReadOnlyList<CollectibleVm> Items,
        int OwnedCount, int PoolCount,
        double LayerMult,                         // weaponMult / skillMult / companionMult from the Power layer; 1 for runes (P1)
        double PassiveTotal,                      // weapons: weaponPassivePct (percentage points); 0 otherwise
        int NewCount,
        int BannerLevel, int PullsToNextLevel);

    /// <summary>One banner level's published rates, all five rarities (04 section 4).</summary>
    public sealed record RateTableRow(int Level, int ExpRequired, IReadOnlyDictionary<Rarity, double> Rates, double EplusRate);

    /// <summary>What SCR_SUMMON shows for one banner (04 section 4, 01 section 8.5.1 step 1).</summary>
    public sealed record SummonBannerVm(
        Banner Banner, bool Enabled, int PullCost, Currency TicketCurrency,
        int BannerLevel, int MaxLevel, long Exp, int PullsToNextLevel, RateTableRow Rates,
        int Pity, int PityHard, long TotalPulls,
        long Diamonds, long Tickets,
        bool CanAffordDiamonds1, bool CanAffordDiamonds10, bool CanAffordTickets1, bool CanAffordTickets10);

    /// <summary>
    /// The read side of summons and collections (04 sections 4 and 11; 00 section 8 item 8). Pure reads over a
    /// Simulation's state; nothing here mutates.
    /// </summary>
    public static class SummonQuery
    {
        public static int BannerLevel(Simulation sim, Banner b) => BannerLevel(sim.Db, sim.State, b);

        public static int BannerLevel(ContentDb db, GameState s, Banner b) => db.Banners[b].LevelFor(BannerExp(s, b));

        public static long BannerExp(GameState s, Banner b) => s.Banners.TryGetValue(b, out var bs) && bs != null ? bs.Exp : 0;

        /// <summary>Pulls to the next banner level; 0 at the top level (04 section 4: "12 pulls to level 4").</summary>
        public static int PullsToNextLevel(ContentDb db, GameState s, Banner b)
        {
            var def = db.Banners[b];
            int level = def.LevelFor(BannerExp(s, b));
            if (level >= def.MaxLevel || def.ExpPerPull <= 0) return 0;
            long need = def.Levels[level].ExpRequired - BannerExp(s, b);
            return (int)((need + def.ExpPerPull - 1) / def.ExpPerPull);
        }

        /// <summary>The <c>banner_levels</c> row for <paramref name="level"/> (1-based, clamped), all five rarities.</summary>
        public static RateTableRow RateTable(ContentDb db, Banner b, int level)
        {
            var def = db.Banners[b];
            if (def.Levels.Count == 0) return null;
            int i = Math.Max(0, Math.Min(def.Levels.Count - 1, level - 1));
            var row = def.Levels[i];
            double eplus = 0;
            foreach (var kv in row.Rates) if (kv.Key >= def.GuaranteeMinRarity) eplus += kv.Value;
            return new RateTableRow(row.Level, row.ExpRequired, row.Rates, eplus);
        }

        /// <summary>Every level's row, for the published-odds screen and GetRateTableAsync.</summary>
        public static IReadOnlyList<RateTableRow> RateTables(ContentDb db, Banner b)
        {
            var def = db.Banners[b];
            var r = new List<RateTableRow>(def.Levels.Count);
            for (int l = 1; l <= def.Levels.Count; l++) r.Add(RateTable(db, b, l));
            return r;
        }

        public static SummonBannerVm BannerView(Simulation sim, Banner b) => BannerView(sim.Db, sim.State, b);

        public static SummonBannerVm BannerView(ContentDb db, GameState s, Banner b)
        {
            var def = db.Banners[b];
            s.Banners.TryGetValue(b, out var bs);
            int level = def.LevelFor(bs?.Exp ?? 0);
            long diamonds = s.Wallet.Diamond;
            long tickets = (long)s.Wallet.Get(def.TicketCurrency);
            int batch = def.GuaranteeBatchSize;
            return new SummonBannerVm(
                b, def.Enabled, def.PullCostDiamonds, def.TicketCurrency,
                level, def.MaxLevel, bs?.Exp ?? 0, PullsToNextLevel(db, s, b), RateTable(db, b, level),
                bs?.Pity ?? 0, def.PityHard, bs?.TotalPulls ?? 0,
                diamonds, tickets,
                diamonds >= def.PullCostDiamonds, diamonds >= (long)def.PullCostDiamonds * batch, tickets >= 1, tickets >= batch);
        }

        /// <summary>Content ids of banner <paramref name="b"/>'s pool, by rarity then id ordinal (09 pool order).</summary>
        public static List<string> PoolIds(ContentDb db, Banner b)
        {
            var r = new List<string>();
            var pool = db.Banners[b].Pool;
            foreach (Rarity rr in Enum.GetValues(typeof(Rarity)))
                if (pool.TryGetValue(rr, out var ids)) r.AddRange(ids);
            return r;
        }

        public static CollectionVm Collection(Simulation sim, Banner b, Func<string, bool> isNew = null) =>
            Collection(sim.Db, sim.State, b, isNew);

        /// <summary>
        /// The collection screen model. <paramref name="isNew"/> reads Runtime's NEW-badge store (00 section 5.1,
        /// issue #47); null means nothing is new.
        /// </summary>
        public static CollectionVm Collection(ContentDb db, GameState s, Banner b, Func<string, bool> isNew = null)
        {
            var col = CollectionRules.For(s, b);
            var items = new List<CollectibleVm>();
            int owned = 0, newCount = 0;
            foreach (var id in PoolIds(db, b))
            {
                var vm = Item(db, s, b, col, id, isNew != null && isNew(id));
                items.Add(vm);
                if (vm.Owned) owned++;
                if (vm.IsNew) newCount++;
            }
            return new CollectionVm(b, items, owned, items.Count, LayerMult(db, s, b), PassiveTotal(db, s, b), newCount,
                                    BannerLevel(db, s, b), PullsToNextLevel(db, s, b));
        }

        /// <summary>The collection's power-formula layer (03 section 7.1): weaponMult, skillMult, companionMult; 1 for runes until #227.</summary>
        public static double LayerMult(ContentDb db, GameState s, Banner b)
        {
            switch (b)
            {
                case Banner.WEAPON: return 1.0 + (PowerFormula.EquippedWeaponPct(db, s) + PowerFormula.WeaponPassivePct(db, s)) / Units.Percent;
                case Banner.SKILL: return 1.0 + PowerFormula.SkillDpsPct(db, s) / Units.Percent;
                case Banner.COMPANION: return 1.0 + PowerFormula.MpSufficiency(db, s) * PowerFormula.CompanionDpsPct(db, s) / Units.Percent;
                default: return 1.0;
            }
        }

        /// <summary>weaponPassivePct for weapons (04 section 6.1), 0 for every other kind.</summary>
        public static double PassiveTotal(ContentDb db, GameState s, Banner b) =>
            b == Banner.WEAPON ? PowerFormula.WeaponPassivePct(db, s) : 0.0;

        static CollectibleVm Item(ContentDb db, GameState s, Banner b, CollectionState col, string id, bool isNew)
        {
            col.Items.TryGetValue(id, out var item);
            int copies = item?.Copies ?? 0;
            bool owned = copies > 0;
            int level = db.DupeLevel(copies);
            int lb = owned ? item.LimitBreaks : 0;
            int eff = owned ? level + lb : 0;
            int maxLevel = db.DupeLevels.Count;
            int toNext = level >= maxLevel ? 0 : db.DupeLevels[level] - copies;

            string nameKey = "", icon = "";
            Rarity rarity = Rarity.COMMON;
            bool equipped = false;
            int? slot = null;
            if (b == Banner.WEAPON && db.Weapons.TryGetValue(id, out var w))
            {
                nameKey = w.NameKey; icon = w.Icon; rarity = w.Rarity;
                if (s.Loadout.Weapon == id) { equipped = true; slot = 0; }
            }
            else if (b == Banner.SKILL && db.Skills.TryGetValue(id, out var k))
            {
                nameKey = k.NameKey; icon = k.Icon; rarity = k.Rarity;
                var slots = s.Loadout.Skills;
                if (slots != null)
                    for (int i = 0; i < slots.Length; i++)
                        if (slots[i] == id) { equipped = true; slot = i; break; }
            }
            else if (b == Banner.COMPANION && db.Companions.TryGetValue(id, out var cp))
            {
                nameKey = cp.NameKey; icon = cp.Icon; rarity = cp.Rarity;
                var slots = s.Loadout.Companions;
                if (slots != null)
                    for (int i = 0; i < slots.Length; i++)
                        if (slots[i] == id) { equipped = true; slot = i; break; }
            }

            // Headline numbers after LevelMult (04 section 11), the Power layer's per-item math: ValueNow at the effective level
            // (0 when unowned), ValueNextLevel one copies-level up (the level-1 value when unowned; ValueNow at level 10).
            double baseValue = CollectionRules.BaseValue(db, b, id);
            double now = owned ? baseValue * PowerFormula.ItemLevelMult(db, item) : 0.0;
            double next = !owned ? baseValue * db.LevelMult(1) : level >= maxLevel ? now : baseValue * db.LevelMult(eff + 1);
            double passiveNow = owned && b == Banner.WEAPON && db.Weapons.TryGetValue(id, out var pw)
                ? pw.PassivePct * PowerFormula.ItemLevelMult(db, item) : 0.0;
            int lbCost = (int)CollectionRules.NextLimitBreakCost(db, b, col, id);   // 04 section 5.2
            return new CollectibleVm(id, rarity, nameKey, icon, owned, copies, level, lb, eff, toNext, lbCost, now, next, passiveNow,
                                     equipped, slot, isNew);
        }
    }
}
