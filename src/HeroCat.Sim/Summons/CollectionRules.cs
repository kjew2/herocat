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
        /// WEAPON_ATK_PCT * LevelMult for a weapon, dps_pct * LevelMult for a skill. 0 when unowned or unknown.
        /// </summary>
        public static double RatedValue(ContentDb db, Banner b, CollectionState col, string id)
        {
            if (id == null || !col.Items.TryGetValue(id, out var item)) return 0.0;
            return BaseValue(db, b, id) * LevelMult(db, item);
        }

        /// <summary>The level-1 value of <paramref name="id"/>: WEAPON_ATK_PCT for a weapon, dps_pct for a skill; 0 otherwise.</summary>
        public static double BaseValue(ContentDb db, Banner b, string id)
        {
            if (id == null) return 0.0;
            if (b == Banner.WEAPON && db.Weapons.TryGetValue(id, out var w)) return w.AtkPct;
            if (b == Banner.SKILL && db.Skills.TryGetValue(id, out var k)) return k.DpsPct;
            return 0.0;
        }

        public static Rarity RarityOf(ContentDb db, Banner b, string id)
        {
            if (b == Banner.WEAPON && db.Weapons.TryGetValue(id, out var w)) return w.Rarity;
            if (b == Banner.SKILL && db.Skills.TryGetValue(id, out var k)) return k.Rarity;
            return Rarity.COMMON;
        }

        /// <summary>True when the content knows <paramref name="id"/> as an item of banner <paramref name="b"/>.</summary>
        public static bool IsKnown(ContentDb db, Banner b, string id)
        {
            if (id == null) return false;
            switch (b)
            {
                case Banner.WEAPON: return db.Weapons.ContainsKey(id);
                case Banner.SKILL: return db.Skills.ContainsKey(id);
                default: return false;   // TODO(#161, #227): companion and rune rows
            }
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
        /// AutoEquipBestWeapon switches (04 section 6.3). A tie on value never switches, whatever the rarity or id.
        /// </summary>
        public static bool StrictlyBetter(ContentDb db, Banner b, CollectionState col, string x, string y) =>
            RatedValue(db, b, col, x) > RatedValue(db, b, col, y);

        /// <summary>The best owned weapon, or null when none is owned.</summary>
        public static string BestWeapon(ContentDb db, GameState s)
        {
            var ranked = RankOwned(db, s, Banner.WEAPON);
            return ranked.Count > 0 ? ranked[0] : null;
        }

        /// <summary>
        /// The skill loadout AutoEquipBestSkills would set: the SKILL_SLOTS best owned skills, best in slot 0, null for
        /// slots past the owned count.
        /// </summary>
        public static string[] BestSkills(ContentDb db, GameState s)
        {
            var ranked = RankOwned(db, s, Banner.SKILL);
            var r = new string[db.Constants.SkillSlots];
            for (int i = 0; i < r.Length && i < ranked.Count; i++) r[i] = ranked[i];
            return r;
        }

        /// <summary>
        /// The "better skill available" badge (04 open question 4 default): an owned, unequipped skill outranks an
        /// equipped one, or a slot is empty while an owned skill is unequipped.
        /// </summary>
        public static bool BetterSkillAvailable(ContentDb db, GameState s)
        {
            var slots = s.Loadout.Skills ?? Array.Empty<string>();
            var equipped = new HashSet<string>(StringComparer.Ordinal);
            string worst = null;
            bool empty = false;
            for (int i = 0; i < db.Constants.SkillSlots; i++)
            {
                string id = i < slots.Length ? slots[i] : null;
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
