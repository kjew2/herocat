using System;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Power
{
    /// <summary>
    /// Every layer function of the power formula (00 section 7, 03 section 7), pure over (ContentDb, GameState). The sim
    /// caches the result in <see cref="SimQuery"/>; bots and tools may call these directly on any state. No Math.Pow:
    /// curve powers come from the content's precomputed tables (00 contract 1).
    ///
    /// M0 layers read state other systems fill (Collections and Loadout from 04, Fish.Cells from 05); a layer whose
    /// system has not landed returns its neutral value (Companions and Research are MVP, Runes P1).
    /// </summary>
    public static class PowerFormula
    {
        // ================================================================================================ Training (03 5.1)

        /// <summary>The Training level of <paramref name="stat"/> (0 when the array is short).</summary>
        public static int Level(GameState s, Stat stat)
        {
            var lv = s.Training.Level;
            int i = (int)stat;
            return lv != null && i < lv.Length ? lv[i] : 0;
        }

        static double PerLvl(ContentDb db, Stat stat) => db.Stat(stat).PerLvl.GetValueOrDefault();

        /// <summary>Level clamped to the stat's TRAIN_CAP (uncapped stats pass through).</summary>
        static int Capped(ContentDb db, Stat stat, int level)
        {
            int? cap = db.Stat(stat).Cap;
            if (level < 0) return 0;
            return cap.HasValue && level > cap.Value ? cap.Value : level;
        }

        /// <summary>atkMult = 1 + TRAIN_PER_LVL[ATK] * n.</summary>
        public static double TrainingMult(ContentDb db, GameState s) => 1.0 + PerLvl(db, Stat.ATK) * Level(s, Stat.ATK);

        /// <summary>critRate = min(TRAIN_CAP * TRAIN_PER_LVL, TRAIN_PER_LVL * n), a fraction (03 section 2.2).</summary>
        public static double CritRate(ContentDb db, GameState s) =>
            PerLvl(db, Stat.CRIT_RATE) * Capped(db, Stat.CRIT_RATE, Level(s, Stat.CRIT_RATE));

        /// <summary>critDmg = CRIT_DMG_BASE + TRAIN_PER_LVL[CRIT_DMG] * n.</summary>
        public static double CritDmg(ContentDb db, GameState s) =>
            db.Constants.CritDmgBase + PerLvl(db, Stat.CRIT_DMG) * Capped(db, Stat.CRIT_DMG, Level(s, Stat.CRIT_DMG));

        /// <summary>critFactor = 1 + critRate * (critDmg - 1): the expected damage multiplier per hero hit.</summary>
        public static double CritFactor(double critRate, double critDmg) => 1.0 + critRate * (critDmg - 1.0);

        /// <summary>speedFactor = 1 + TRAIN_PER_LVL[ATK_SPD] * min(n, TRAIN_CAP[ATK_SPD]).</summary>
        public static double SpeedFactor(ContentDb db, GameState s) =>
            1.0 + PerLvl(db, Stat.ATK_SPD) * Capped(db, Stat.ATK_SPD, Level(s, Stat.ATK_SPD));

        /// <summary>
        /// Attack progress per tick: <c>ATTACK_PROGRESS_UNIT + round(ATTACK_PROGRESS_UNIT * TRAIN_PER_LVL[ATK_SPD] * n)</c>,
        /// half away from zero (issue #83's decision; exact for the shipped 0.01, so the realised rate equals speedFactor).
        /// </summary>
        public static int AttackGainUnits(ContentDb db, GameState s)
        {
            int unit = db.Constants.AttackProgressUnit;
            double bonus = unit * PerLvl(db, Stat.ATK_SPD) * Capped(db, Stat.ATK_SPD, Level(s, Stat.ATK_SPD));
            return unit + (int)Math.Round(bonus, MidpointRounding.AwayFromZero);
        }

        /// <summary>goldMult = 1 + TRAIN_PER_LVL[GOLD_GAIN] * n. TODO(#158): times Research's res_gold_gain factor (MVP).</summary>
        public static double GoldMult(ContentDb db, GameState s) => 1.0 + PerLvl(db, Stat.GOLD_GAIN) * Level(s, Stat.GOLD_GAIN);

        /// <summary>expMult = 1 + TRAIN_PER_LVL[EXP_GAIN] * n.</summary>
        public static double ExpMult(ContentDb db, GameState s) => 1.0 + PerLvl(db, Stat.EXP_GAIN) * Level(s, Stat.EXP_GAIN);

        // ================================================================================================ Hero Level (03 5.2)

        /// <summary>HERO_LVL_MULT ^ heroLevel from the cached table (bit-identical to repeated multiplication).</summary>
        public static double HeroMult(ContentDb db, GameState s) => db.HeroMult(s.Hero.Level < 0 ? 0 : s.Hero.Level);

        // ================================================================================================ Collections (04 5-7)

        /// <summary>LevelMult of an owned item: 1 + DUPE_LVL_BONUS * (Level(copies) + limitBreaks - 1); 0 when not owned.</summary>
        public static double ItemLevelMult(ContentDb db, OwnedItem item)
        {
            if (item == null) return 0.0;
            int level = db.DupeLevel(item.Copies);
            if (level < 1) return 0.0;
            return db.LevelMult(level + (item.LimitBreaks > 0 ? item.LimitBreaks : 0));
        }

        static double OwnedLevelMult(ContentDb db, CollectionState col, string id)
        {
            if (id == null || col == null) return 0.0;
            return col.Items.TryGetValue(id, out var item) ? ItemLevelMult(db, item) : 0.0;
        }

        /// <summary>equippedWeaponPct = WEAPON_ATK_PCT * LevelMult(equipped); 0 when nothing (owned) is equipped (04 section 6.1).</summary>
        public static double EquippedWeaponPct(ContentDb db, GameState s)
        {
            string id = s.Loadout.Weapon;
            if (id == null || !db.Weapons.TryGetValue(id, out var w)) return 0.0;
            return w.AtkPct * OwnedLevelMult(db, s.Weapons, id);
        }

        /// <summary>
        /// Sum over owned weapons (equipped included) of WEAPON_PASSIVE_PCT * LevelMult (04 section 6.1). Summed in a fixed
        /// order (rarity, then the weapon pool's ordinal id order), never dictionary order, so the bits do not depend on how
        /// the collection was filled or loaded (00 contract 1).
        /// </summary>
        public static double WeaponPassivePct(ContentDb db, GameState s)
        {
            var items = s.Weapons.Items;
            if (items.Count == 0 || !db.Banners.TryGetValue(Banner.WEAPON, out var banner)) return 0.0;
            double sum = 0.0;
            foreach (Rarity r in Rarities)
            {
                if (!banner.Pool.TryGetValue(r, out var ids)) continue;
                for (int i = 0; i < ids.Count; i++)
                    if (items.TryGetValue(ids[i], out var item) && db.Weapons.TryGetValue(ids[i], out var w))
                        sum += w.PassivePct * ItemLevelMult(db, item);
            }
            return sum;
        }

        static readonly Rarity[] Rarities = { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY, Rarity.MYTHIC };

        /// <summary>Sum over equipped skills of dps_pct * LevelMult (04 section 7.2); unowned or unknown ids count 0.</summary>
        public static double SkillDpsPct(ContentDb db, GameState s)
        {
            var slots = s.Loadout.Skills;
            if (slots == null) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < slots.Length; i++)
            {
                string id = slots[i];
                if (id == null || !db.Skills.TryGetValue(id, out var def)) continue;
                sum += def.DpsPct * OwnedLevelMult(db, s.Skills, id);
            }
            return sum;
        }

        /// <summary>LevelMult of the skill in <paramref name="slot"/>; scales its per-cast numbers (04 section 5.1).</summary>
        public static double SkillLevelMult(ContentDb db, GameState s, string skillId) => OwnedLevelMult(db, s.Skills, skillId);

        /// <summary>TODO(#156, #161): sum over equipped companions of companionDpsPct (MVP). 0 in M0.</summary>
        public static double CompanionDpsPct(ContentDb db, GameState s) => 0.0;

        /// <summary>TODO(#156, #161): min(1, regen / demand) with MP_REGEN companions counted as regen (issue #39). 1 in M0.</summary>
        public static double MpSufficiency(ContentDb db, GameState s) => 1.0;

        // ================================================================================================ Fish Board (05 5)

        /// <summary>FISH_BOARD_DPS_PCT = sum over occupied cells of BoardDpsPct[level] (05 section 5.2). Levels past the table use its last row.</summary>
        public static double FishBoardDpsPct(ContentDb db, GameState s)
        {
            var cells = s.Fish.Cells;
            var levels = db.FishLevels;
            if (cells == null || levels.Count == 0) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < cells.Length; i++)
            {
                int lv = cells[i];
                if (lv <= 0) continue;
                sum += levels[(lv > levels.Count ? levels.Count : lv) - 1].BoardDpsPct;
            }
            return sum;
        }

        /// <summary>
        /// 05 section 5.1's closed form: hero hits from missiles per released level-1 fish when every fish is merged up to
        /// level <paramref name="cap"/>: sum over n = 2..cap of FISH_MISSILE_PCT[n] / 100 * 2^-(n-1).
        /// </summary>
        public static double MissileHitsPerFish(ContentDb db, int cap)
        {
            var levels = db.FishLevels;
            if (cap > levels.Count) cap = levels.Count;
            double sum = 0.0, share = 1.0;
            for (int n = 2; n <= cap; n++)
            {
                share *= 0.5;   // each level-n merge is shared by 2^(n-1) released fish
                sum += levels[n - 1].MissilePct / 100.0 * share;
            }
            return sum;
        }

        /// <summary>
        /// missileDpsPct = 100 * f * missileHitsPerFish(cap) / (60 * speedFactor), percentage points, for a player merging
        /// <paramref name="fishPerMinute"/> level-1 fish per minute (05 section 5.1). Rated-only (issue #15): the sim cannot
        /// know the merge rate, so <see cref="SimQuery.RatedDps"/> uses 0 and callers that model a rate (bots, tools,
        /// SCR_HERO) pass it to <see cref="SimQuery.RatedDpsWithMissiles"/>.
        /// </summary>
        public static double MissileDpsPct(ContentDb db, double fishPerMinute, int cap, double speedFactor) =>
            100.0 * fishPerMinute * MissileHitsPerFish(db, cap) / (60.0 * speedFactor);

        // ================================================================================================ Research, Runes, Awakening

        /// <summary>TODO(#158): sum of res_atk effects (MVP). 0 in M0.</summary>
        public static double ResearchPct(ContentDb db, GameState s) => 0.0;

        /// <summary>P1 (TODO(#227)). 0 until then.</summary>
        public static double RunePct(ContentDb db, GameState s) => 0.0;

        /// <summary>AWAKEN_MULT ^ awakenRank: the product of the awakening rows' mult for ranks 1..AwakenRank (03 section 5.3).</summary>
        public static double AwakenMult(ContentDb db, GameState s)
        {
            int rank = s.Hero.AwakenRank;
            double m = 1.0;
            var rows = db.Awakening;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Rank <= rank) m *= rows[i].Mult;
            return m;
        }

        // ================================================================================================ the formula (03 7.1)

        /// <summary>Every factor and unit for this state. <paramref name="missileDpsPct"/> is rated-only (issue #15).</summary>
        public static PowerBreakdown Compute(ContentDb db, GameState s, double missileDpsPct = 0.0)
        {
            var p = new PowerBreakdown
            {
                Base = db.Constants.BaseAtk,
                Training = TrainingMult(db, s),
                Hero = HeroMult(db, s),
                EquippedWeaponPct = EquippedWeaponPct(db, s),
                WeaponPassivePct = WeaponPassivePct(db, s),
                SkillDpsPct = SkillDpsPct(db, s),
                CompanionDpsPct = CompanionDpsPct(db, s),
                MpSufficiency = MpSufficiency(db, s),
                FishBoardDpsPct = FishBoardDpsPct(db, s),
                MissileDpsPct = missileDpsPct,
                ResearchPct = ResearchPct(db, s),
                RunePct = RunePct(db, s),
                Awakening = AwakenMult(db, s),
                CritRate = CritRate(db, s),
                CritDmg = CritDmg(db, s),
                Speed = SpeedFactor(db, s),
                AttackGainUnits = AttackGainUnits(db, s),
                GoldMult = GoldMult(db, s),
                ExpMult = ExpMult(db, s),
            };
            p.Weapons = 1.0 + (p.EquippedWeaponPct + p.WeaponPassivePct) / 100.0;
            p.Skills = 1.0 + p.SkillDpsPct / 100.0;
            p.Companions = 1.0 + p.MpSufficiency * p.CompanionDpsPct / 100.0;
            p.Fish = 1.0 + (p.FishBoardDpsPct + p.MissileDpsPct) / 100.0;
            p.Research = 1.0 + p.ResearchPct;
            p.Runes = 1.0 + p.RunePct;
            p.Crit = CritFactor(p.CritRate, p.CritDmg);

            p.HeroHit = p.Base * p.Training * p.Hero * p.Weapons * (1.0 + p.FishBoardDpsPct / 100.0)
                      * p.Research * p.Runes * p.Awakening;
            p.SkillUnit = p.HeroHit * p.Crit * p.Speed;
            p.CompanionUnit = p.SkillUnit * p.Skills;
            p.RatedDps = p.Base * p.Training * p.Hero * p.Weapons * p.Skills * p.Companions * p.Fish * p.Research * p.Runes
                       * p.Awakening * p.Crit * p.Speed;
            return p;
        }
    }
}
