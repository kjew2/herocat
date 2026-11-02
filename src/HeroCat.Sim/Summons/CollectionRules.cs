using System;
using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Summons
{
    /// <summary>
    /// Shared collection reads (04 sections 5.1, 6.3, 7.2): which collection a banner feeds, an item's level and
    /// LevelMult, and the auto-equip ranking. The power-formula layers (weaponMult, skillMult and their breakdown) are
    /// the Power layer's (03 section 7); this class ranks items by the Power layer's per-item rated value.
    /// </summary>
    public static class CollectionRules
    {
        /// <summary>The collection a banner feeds (02 section 3.1).</summary>
        public static CollectionState For(GameState s, Banner b)
        {
            switch (b)
            {
                case Banner.WEAPON: return s.Weapons;
                case Banner.SKILL: return s.Skills;
                case Banner.COMPANION: return s.Companions;
                default: return s.Runes;
            }
        }

        public static bool Owns(CollectionState c, string id) =>
            id != null && c.Items.TryGetValue(id, out var item) && item != null && item.Copies > 0;

        /// <summary>Level(copies) + limitBreaks, 0 when unowned (04 section 5.1).</summary>
        public static int EffectiveLevel(ContentDb db, OwnedItem item) =>
            item == null || item.Copies <= 0 ? 0 : db.DupeLevel(item.Copies) + item.LimitBreaks;

        /// <summary>1 + DUPE_LVL_BONUS * (EffectiveLevel - 1); 0 when unowned. The Power layer's LevelMult.</summary>
        public static double LevelMult(ContentDb db, OwnedItem item) => PowerFormula.ItemLevelMult(db, item);

        /// <summary>
        /// The rated value auto-equip ranks by (04 sections 6.3 and 7.2), in percentage points after LevelMult:
        /// WEAPON_ATK_PCT * LevelMult for a weapon, dps_pct * LevelMult for a skill or companion. 0 when unowned or unknown.
        /// </summary>
        public static double RatedValue(ContentDb db, Banner b, CollectionState col, string id)
        {
            if (id == null || !col.Items.TryGetValue(id, out var item)) return 0.0;
            return BaseValue(db, b, id) * LevelMult(db, item);
        }

        /// <summary>The level-1 value of <paramref name="id"/>: WEAPON_ATK_PCT for a weapon, dps_pct for a skill or companion; 0 otherwise.</summary>
        public static double BaseValue(ContentDb db, Banner b, string id)
        {
            if (id == null) return 0.0;
            if (b == Banner.WEAPON && db.Weapons.TryGetValue(id, out var w)) return w.AtkPct;
            if (b == Banner.SKILL && db.Skills.TryGetValue(id, out var k)) return k.DpsPct;
            if (b == Banner.COMPANION && db.Companions.TryGetValue(id, out var c)) return c.DpsPct;
            return 0.0;
        }

        public static Rarity RarityOf(ContentDb db, Banner b, string id)
        {
            if (b == Banner.WEAPON && db.Weapons.TryGetValue(id, out var w)) return w.Rarity;
            if (b == Banner.SKILL && db.Skills.TryGetValue(id, out var k)) return k.Rarity;
            if (b == Banner.COMPANION && db.Companions.TryGetValue(id, out var c)) return c.Rarity;
            return Rarity.COMMON;
        }

        /// <summary>The icon asset of an item of banner <paramref name="b"/>; "" when unknown.</summary>
        public static string IconOf(ContentDb db, Banner b, string id)
        {
            if (b == Banner.WEAPON && db.Weapons.TryGetValue(id, out var w)) return w.Icon;
            if (b == Banner.SKILL && db.Skills.TryGetValue(id, out var k)) return k.Icon;
            if (b == Banner.COMPANION && db.Companions.TryGetValue(id, out var c)) return c.Icon;
            return "";
        }

        /// <summary>True when the content knows <paramref name="id"/> as an item of banner <paramref name="b"/>.</summary>
        public static bool IsKnown(ContentDb db, Banner b, string id)
        {
            if (id == null) return false;
            switch (b)
            {
                case Banner.WEAPON: return db.Weapons.ContainsKey(id);
                case Banner.SKILL: return db.Skills.ContainsKey(id);
                case Banner.COMPANION: return db.Companions.ContainsKey(id);
                default: return false;   // TODO(#227): rune rows
            }
        }

        /// <summary>The collectible kinds a limit break applies to, in lookup order (04 section 5.2).</summary>
        public static readonly Banner[] LimitBreakKinds = { Banner.WEAPON, Banner.SKILL, Banner.COMPANION };

        /// <summary>
        /// The kind of content id <paramref name="id"/> among <see cref="LimitBreakKinds"/>; false for an unknown id. Ids are
        /// table-prefixed (weapon_*, skill_*, comp_*, 09), so at most one kind matches; the first in lookup order wins.
        /// </summary>
        public static bool TryKindOf(ContentDb db, string id, out Banner kind)
        {
            foreach (var b in LimitBreakKinds)
                if (IsKnown(db, b, id)) { kind = b; return true; }
            kind = default;
            return false;
        }

        /// <summary>
        /// Rock Candy for the next limit-break rank: <c>LIMIT_BREAK_COST[rarity] * (LimitBreaks + 1)</c> (04 section 5.2);
        /// 0 when unowned, unknown or at LIMIT_BREAK_RANKS.
        /// </summary>
        public static long NextLimitBreakCost(ContentDb db, Banner b, CollectionState col, string id)
        {
            if (!IsKnown(db, b, id) || !col.Items.TryGetValue(id, out var item) || item == null || item.Copies <= 0) return 0;
            if (item.LimitBreaks >= db.Constants.LimitBreakRanks) return 0;
            return db.Rarities[RarityOf(db, b, id)].LimitBreakCost * (item.LimitBreaks + 1);
        }

        /// <summary>
        /// Owned items of <paramref name="b"/> ordered best first: <see cref="RatedValue"/> (higher first), ties by rarity
        /// (higher first) then id ordinal (04 sections 6.3 and 7.2). Deterministic for any dictionary order.
        /// </summary>
        public static List<string> RankOwned(ContentDb db, GameState s, Banner b)
        {
            var col = For(s, b);
            var ids = new List<string>();
            foreach (var kv in col.Items) if (kv.Value != null && kv.Value.Copies > 0 && IsKnown(db, b, kv.Key)) ids.Add(kv.Key);
            ids.Sort((x, y) => Compare(db, b, col, x, y));
            return ids;
        }

        /// <summary>Negative when <paramref name="x"/> ranks before (is better than) <paramref name="y"/>; see <see cref="RankOwned"/>.</summary>
        public static int Compare(ContentDb db, Banner b, CollectionState col, string x, string y)
        {
            int c = RatedValue(db, b, col, y).CompareTo(RatedValue(db, b, col, x));
            if (c != 0) return c;
            c = RarityOf(db, b, y).CompareTo(RarityOf(db, b, x));
            if (c != 0) return c;
            return string.CompareOrdinal(x, y);
        }

        /// <summary>
        /// True when <paramref name="x"/>'s rated value is strictly above <paramref name="y"/>'s: the only case
        /// AutoEquipBestWeapons switches (04 section 6.3). A tie on value never switches, whatever the rarity or id.
        /// </summary>
        public static bool StrictlyBetter(ContentDb db, Banner b, CollectionState col, string x, string y) =>
            RatedValue(db, b, col, x) > RatedValue(db, b, col, y);

        /// <summary>A copy of <paramref name="slots"/> with one field set from <paramref name="ids"/> per slot (a probe loadout).</summary>
        public static PartySlot[] WithField(PartySlot[] slots, string[] ids, Action<PartySlot, string> set)
        {
            var r = new PartySlot[slots.Length];
            for (int i = 0; i < r.Length; i++)
            {
                var x = slots[i] ?? new PartySlot();
                r[i] = new PartySlot { Companion = x.Companion, Weapon = x.Weapon, Skill = x.Skill, AwakenRank = x.AwakenRank };
                if (i < ids.Length) set(r[i], ids[i]);
            }
            return r;
        }

        /// <summary>
        /// Occupied slots ordered by <paramref name="weight"/> desc, ties by slot index ascending (04 sections 6.3, 7.2). The
        /// weight is the slot's coefficient on the factor being assigned: RatedDps is linear in each slot's weapon (or skill)
        /// factor, so pairing the best item with the heaviest slot maximises it (rearrangement inequality).
        /// </summary>
        static List<int> SlotsByWeight(in PowerBreakdown p, Func<int, double> weight)
        {
            var order = new List<int>();
            for (int i = 0; i < p.SlotCount; i++) if (p.Occupied(i)) order.Add(i);
            order.Sort((x, y) =>
            {
                int c = weight(y).CompareTo(weight(x));
                return c != 0 ? c : x.CompareTo(y);
            });
            return order;
        }

        static string[] Assign(int n, List<int> slotOrder, List<string> ranked)
        {
            var r = new string[n];
            for (int k = 0; k < slotOrder.Count && k < ranked.Count; k++) r[slotOrder[k]] = ranked[k];
            return r;
        }

        /// <summary>
        /// The weapon per party slot AutoEquipBestWeapons would set (04 section 6.3): the top-ranked owned weapons go to the
        /// occupied slots, best to the slot with the highest weight (the slot's coefficient on its weapon factor,
        /// <c>Awakening * skillFactor * ownFactor</c>; party-wide factors are common to every slot); empty slots and the
        /// lowest-weight slots past the owned count are unarmed. Sized PARTY_SLOTS.
        /// </summary>
        public static string[] BestWeapons(ContentDb db, GameState s)
        {
            var p = PowerFormula.Compute(db, s);
            var order = SlotsByWeight(p, i => p.Awakening(i) * p.SkillFactor(i) * p.OwnFactor(i));
            return Assign(db.Constants.PartySlots, order, RankOwned(db, s, Banner.WEAPON));
        }

        /// <summary>
        /// The skill per party slot AutoEquipBestSkills would set (04 section 7.2), as <see cref="BestWeapons"/> with the
        /// weight <c>Awakening * weaponFactor * ownFactor</c>. Sized PARTY_SLOTS; empty slots get none.
        /// </summary>
        public static string[] BestSkills(ContentDb db, GameState s)
        {
            var p = PowerFormula.Compute(db, s);
            var order = SlotsByWeight(p, i => p.Awakening(i) * p.WeaponFactor(i) * p.OwnFactor(i));
            return Assign(db.Constants.PartySlots, order, RankOwned(db, s, Banner.SKILL));
        }

        /// <summary>
        /// The companion per party slot AutoEquipBestCompanions would set (04 section 8.2), sized PARTY_SLOTS. Pick: the five
        /// best owned DAMAGE and BUFF_ATK companions, then MP_REGEN, then BUFF_GOLD to fill. MP: while a picked companion's
        /// pool is short and an unpicked MP_REGEN is owned, the best one replaces the lowest-ranked non-MP_REGEN pick if
        /// that raises the rated DPS. Place: DAMAGE companions by <c>mpSufficiency * dps_pct * LevelMult</c> to the slots
        /// with the highest <c>Awakening * weaponFactor * skillFactor</c>; the others take the remaining slots in pick order.
        /// </summary>
        public static string[] BestCompanions(ContentDb db, GameState s)
        {
            int n = db.Constants.PartySlots;
            var ranked = RankOwned(db, s, Banner.COMPANION);
            var pick = new List<string>();
            var regen = new List<string>();
            foreach (var id in ranked) if (db.Companions[id].CountsInLayer && pick.Count < n) pick.Add(id);
            foreach (var id in ranked) if (db.Companions[id].AbilityType == AbilityType.MP_REGEN) regen.Add(id);
            foreach (var id in regen) if (pick.Count < n) pick.Add(id);
            foreach (var id in ranked) if (db.Companions[id].AbilityType == AbilityType.BUFF_GOLD && pick.Count < n) pick.Add(id);

            var best = Place(db, s, pick);
            double bestDps = PowerFormula.Compute(db, s, 0.0, WithField(s.Party.Slots, best, (x, id) => x.Companion = id)).RatedDps;
            while (true)
            {
                var p = PowerFormula.Compute(db, s, 0.0, WithField(s.Party.Slots, best, (x, id) => x.Companion = id));
                bool shortPool = false;
                for (int i = 0; i < n; i++) if (p.Occupied(i) && p.MpSufficiency(i) < 1.0) shortPool = true;
                string nextRegen = regen.Find(id => !pick.Contains(id));
                int victim = pick.FindLastIndex(id => db.Companions[id].AbilityType != AbilityType.MP_REGEN);
                if (!shortPool || nextRegen == null || victim < 0) break;
                var trial = new List<string>(pick) { [victim] = nextRegen };
                var placed = Place(db, s, trial);
                double dps = PowerFormula.Compute(db, s, 0.0, WithField(s.Party.Slots, placed, (x, id) => x.Companion = id)).RatedDps;
                if (!(dps > bestDps)) break;
                pick = trial; best = placed; bestDps = dps;
            }
            return best;
        }

        /// <summary>04 section 8.2 step 3: DAMAGE companions to the heaviest slots, the rest to the remaining slots in pick order.</summary>
        static string[] Place(ContentDb db, GameState s, List<string> pick)
        {
            int n = db.Constants.PartySlots;
            var all = new string[n];
            for (int i = 0; i < n && i < pick.Count; i++) all[i] = pick[i];
            var probe = WithField(s.Party.Slots, all, (x, id) => x.Companion = id);
            var p = PowerFormula.Compute(db, s, 0.0, probe);
            var slots = new List<int>();
            for (int i = 0; i < n; i++) slots.Add(i);
            slots.Sort((x, y) =>
            {
                double wx = p.Awakening(x) * p.WeaponFactor(x) * p.SkillFactor(x), wy = p.Awakening(y) * p.WeaponFactor(y) * p.SkillFactor(y);
                int c = wy.CompareTo(wx);
                return c != 0 ? c : x.CompareTo(y);
            });
            var damage = pick.FindAll(id => db.Companions[id].AbilityType == AbilityType.DAMAGE);
            var mpOf = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < n && i < pick.Count; i++) mpOf[pick[i]] = p.MpSufficiency(i);
            damage.Sort((x, y) =>
            {
                double vx = mpOf[x] * RatedValue(db, Banner.COMPANION, s.Companions, x), vy = mpOf[y] * RatedValue(db, Banner.COMPANION, s.Companions, y);
                int c = vy.CompareTo(vx);
                return c != 0 ? c : Compare(db, Banner.COMPANION, s.Companions, x, y);
            });
            var r = new string[n];
            int k = 0;
            foreach (var id in damage) r[slots[k++]] = id;
            var rest = new List<int>(slots.GetRange(k, n - k));
            rest.Sort();
            int m = 0;
            foreach (var id in pick)
                if (db.Companions[id].AbilityType != AbilityType.DAMAGE && m < rest.Count) r[rest[m++]] = id;
            return r;
        }

        /// <summary>
        /// The "better skill available" badge (04 open question 4 default): an owned, unequipped skill outranks an
        /// equipped one, or a slot is empty while an owned skill is unequipped.
        /// </summary>
        public static bool BetterSkillAvailable(ContentDb db, GameState s)
        {
            var slots = s.Party.Slots ?? Array.Empty<PartySlot>();
            var equipped = new HashSet<string>(StringComparer.Ordinal);
            string worst = null;
            bool empty = false;
            for (int i = 0; i < db.Constants.PartySlots; i++)
            {
                if (i >= slots.Length || slots[i] == null || slots[i].Companion == null) continue;   // an empty slot's skill would idle
                string id = slots[i].Skill;
                if (id == null || !Owns(s.Skills, id)) { empty = true; continue; }
                equipped.Add(id);
                if (worst == null || Compare(db, Banner.SKILL, s.Skills, id, worst) > 0) worst = id;
            }
            foreach (var id in RankOwned(db, s, Banner.SKILL))
            {
                if (equipped.Contains(id)) continue;
                if (empty) return true;
                return worst != null && StrictlyBetter(db, Banner.SKILL, s.Skills, id, worst);   // best unequipped vs worst equipped
            }
            return false;
        }
    }
}
