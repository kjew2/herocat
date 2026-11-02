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
    /// system has not landed returns its neutral value (Companions and Research are MVP and neutral in M0 content, Runes P1).
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

        /// <summary>
        /// goldMult = (1 + TRAIN_PER_LVL[GOLD_GAIN] * n) * (1 + 0.05 k): Training times Research's res_gold_gain, one factor
        /// for the node, not one per level (03 section 6). The Research factor is exactly 1 at level 0 and in M0 content.
        /// </summary>
        public static double GoldMult(ContentDb db, GameState s) =>
            (1.0 + PerLvl(db, Stat.GOLD_GAIN) * Level(s, Stat.GOLD_GAIN)) * (1.0 + ResearchEffectTotal(db, s, ResearchEffect.GOLD_MULT));

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

        /// <summary>
        /// The companion in loadout slot <paramref name="slot"/> when it counts: the slot is open at the current Awakening
        /// rank (03 section 5.3), the id is a known companion and it is owned. Null otherwise; <paramref name="levelMult"/>
        /// is its LevelMult (04 section 5.1). The tick loop and the rated formula both read slots through this.
        /// </summary>
        public static CompanionDef ActiveCompanion(ContentDb db, GameState s, int slot, int openSlots, out double levelMult)
        {
            levelMult = 0.0;
            var slots = s.Loadout.Companions;
            if (slots == null || slot < 0 || slot >= slots.Length || slot >= openSlots) return null;
            string id = slots[slot];
            if (id == null || !db.Companions.TryGetValue(id, out var def)) return null;
            levelMult = OwnedLevelMult(db, s.Companions, id);
            return levelMult > 0 ? def : null;
        }

        /// <summary>Companion slots open now (COMPANION_SLOTS capped by Awakening, 04 section 8.2).</summary>
        public static int OpenCompanionSlots(ContentDb db, GameState s) => AwakeningRules.CompanionSlots(db, s);

        /// <summary>
        /// Sum over equipped companions of dps_pct * LevelMult (04 section 8.2), MP_REGEN and BUFF_GOLD counting 0 (04 section 8.1).
        /// Locked slots, unowned and unknown ids count 0. 0 in a build without companion rows.
        /// </summary>
        public static double CompanionDpsPct(ContentDb db, GameState s)
        {
            var slots = s.Loadout.Companions;
            if (slots == null || db.Companions.Count == 0) return 0.0;
            int open = OpenCompanionSlots(db, s);
            double sum = 0.0;
            for (int i = 0; i < slots.Length; i++)
            {
                var c = ActiveCompanion(db, s, i, open, out double lm);
                if (c != null && c.CountsInLayer) sum += c.DpsPct * lm;
            }
            return sum;
        }

        /// <summary>MP restored by one cast of an MP_REGEN companion at <paramref name="levelMult"/>: mp_restored * LevelMult (04 section 8.1).</summary>
        public static double MpRestored(CompanionDef c, double levelMult) =>
            c.AbilityType == AbilityType.MP_REGEN ? c.Magnitude * levelMult : 0.0;

        /// <summary>
        /// The inclusive mpSufficiency (04 section 8.2, 03 section 7.1; issue #39, 00 decision 71):
        /// <c>supply = mpRegenPerSecond + sum MP_REGEN (mpRestored - mpCost) / cooldownSeconds</c>,
        /// <c>demand = sum other (mpCost / cooldownSeconds)</c>, <c>demand == 0 ? 1 : min(1, supply / demand)</c>, floored at
        /// 0 (a net-draining loadout cannot make the layer negative). 1 in a build without companion rows.
        /// </summary>
        public static double MpSufficiency(ContentDb db, GameState s)
        {
            var slots = s.Loadout.Companions;
            if (slots == null || db.Companions.Count == 0) return 1.0;
            int open = OpenCompanionSlots(db, s);
            double hz = db.Constants.TickHz;
            double supply = ResearchRules.MpRegenPerSecond(db, s), demand = 0.0;
            for (int i = 0; i < slots.Length; i++)
            {
                var c = ActiveCompanion(db, s, i, open, out double lm);
                if (c == null || c.CooldownTicks <= 0) continue;
                double perSec = hz / c.CooldownTicks;   // casts per second at full uptime
                if (c.AbilityType == AbilityType.MP_REGEN) supply += (MpRestored(c, lm) - c.MpCost) * perSec;
                else demand += c.MpCost * perSec;
            }
            if (demand <= 0) return 1.0;
            double r = supply / demand;
            return r >= 1.0 ? 1.0 : r <= 0.0 ? 0.0 : r;
        }

        // ================================================================================================ Fish Board (05 5)

        /// <summary>FISH_BOARD_DPS_PCT (05 section 5.2). The fish track owns the sum and its floating-point order (05 section 12 rule 5).</summary>
        public static double FishBoardDpsPct(ContentDb db, GameState s) => HeroCat.Sim.Fish.FishBoard.BoardDpsPct(s, db);

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
                share /= 2;   // each level-n merge is shared by 2^(n-1) released fish
                sum += levels[n - 1].MissilePct / Units.Percent * share;
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
            Units.Percent * fishPerMinute * MissileHitsPerFish(db, cap) / (Units.SecondsPerMinute * speedFactor);

        // ================================================================================================ Research, Runes, Awakening

        /// <summary>researchPct: res_atk's 0.05 per level as a fraction (03 sections 6 and 7.2; 0.35 at level 7). 0 in M0.</summary>
        public static double ResearchPct(ContentDb db, GameState s) => ResearchEffectTotal(db, s, ResearchEffect.RESEARCH_PCT);

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

        /// <summary>
        /// The bought level of the content's node with <paramref name="effect"/>, clamped to 0..max_level; 0 when the content
        /// has no such node (M0) or the node was never bought (03 section 6).
        /// </summary>
        public static int ResearchLevel(ContentDb db, GameState s, ResearchEffect effect)
        {
            var node = db.ResearchFor(effect);
            if (node == null || !s.Research.Level.TryGetValue(node.Id, out int lv) || lv <= 0) return 0;
            return lv > node.MaxLevel ? node.MaxLevel : lv;
        }

        /// <summary>The summed effect of that node: effect_per_level * ResearchLevel (0 without the node).</summary>
        public static double ResearchEffectTotal(ContentDb db, GameState s, ResearchEffect effect)
        {
            int lv = ResearchLevel(db, s, effect);
            return lv == 0 ? 0.0 : db.ResearchFor(effect).EffectPerLevel * lv;
        }

        // ================================================================================================ offline and kill time

        /// <summary>
        /// The effective offline cap in hours: OFFLINE_CAP_H plus res_offline_cap's hours per level (00 decision 12, 03
        /// section 6; issue #21). 24 h to 36 h with the shipped rows.
        /// </summary>
        public static double OfflineCapH(ContentDb db, GameState s) =>
            db.Constants.OfflineCapH + ResearchEffectTotal(db, s, ResearchEffect.OFFLINE_CAP);

        /// <summary>
        /// Rated time to kill one enemy of <paramref name="hp"/> (03 section 7.1, 06 section 7.1): whole attacks at the rated
        /// per-attack damage, <c>ceil(hp / (RatedDps * attackSec)) * attackSec + SPAWN_DELAY_TICKS / TICK_HZ</c>, where
        /// <c>attackSec = ATTACK_INTERVAL_TICKS / TICK_HZ / speedFactor</c>. Skills, companions and crits enter through the
        /// rated DPS (their expected share per attack). Positive infinity when the rated DPS is not positive.
        /// </summary>
        public static double KillTimeSeconds(ContentDb db, in PowerBreakdown p, double hp)
        {
            var k = db.Constants;
            double hz = k.TickHz;
            double attackSec = k.AttackIntervalTicks / hz / (p.Speed > 0 ? p.Speed : 1.0);
            double perAttack = p.RatedDps * attackSec;
            if (!(perAttack > 0) || double.IsInfinity(perAttack)) return double.PositiveInfinity;
            double hits = Math.Ceiling(hp / perAttack);
            if (hits < 1) hits = 1;
            return hits * attackSec + k.SpawnDelayTicks / hz;
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
                MpPoolMax = ResearchRules.MpPoolMax(db, s),
                MpRegenPerTick = ResearchRules.MpRegenPerTick(db, s),
            };
            p.Weapons = 1.0 + (p.EquippedWeaponPct + p.WeaponPassivePct) / Units.Percent;
            p.Skills = 1.0 + p.SkillDpsPct / Units.Percent;
            p.Companions = 1.0 + p.MpSufficiency * p.CompanionDpsPct / Units.Percent;
            p.Fish = 1.0 + (p.FishBoardDpsPct + p.MissileDpsPct) / Units.Percent;
            p.Research = 1.0 + p.ResearchPct;
            p.Runes = 1.0 + p.RunePct;
            p.Crit = CritFactor(p.CritRate, p.CritDmg);

            p.HeroHit = p.Base * p.Training * p.Hero * p.Weapons * (1.0 + p.FishBoardDpsPct / Units.Percent)
                      * p.Research * p.Runes * p.Awakening;
            p.SkillUnit = p.HeroHit * p.Crit * p.Speed;
            p.CompanionUnit = p.SkillUnit * p.Skills;
            p.RatedDps = p.Base * p.Training * p.Hero * p.Weapons * p.Skills * p.Companions * p.Fish * p.Research * p.Runes
                       * p.Awakening * p.Crit * p.Speed;
            return p;
        }
    }
}
