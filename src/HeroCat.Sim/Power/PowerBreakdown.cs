namespace HeroCat.Sim.Power
{
    /// <summary>
    /// The layers of the rated power formula (00 section 7, 03 section 7.1), in the formula's order. The layer-share
    /// report (03 section 8.5) is <c>ln(factor) / ln(RatedDps / BASE_ATK)</c> per layer; <see cref="Base"/> is not a layer
    /// for the 5% floor.
    /// </summary>
    public enum PowerLayer { Base, Training, Hero, Weapons, Skills, Companions, Fish, Research, Runes, Awakening, Crit, Speed }

    /// <summary>
    /// Every factor and unit of the power formula for one (ContentDb, GameState), as a value (03 sections 7.1-7.2).
    /// Percent fields are percentage points (<c>40</c> = +40%); <see cref="ResearchPct"/>, <see cref="RunePct"/> and
    /// <see cref="CritRate"/> are fractions. Factors are multipliers (1.0 = no effect).
    /// </summary>
    public struct PowerBreakdown
    {
        // ------------------------------------------------------------------ factors of the rated formula (03 7.1)
        public double Base { get; internal set; }          // BASE_ATK
        public double Training { get; internal set; }      // 1 + TRAIN_PER_LVL[ATK] * trainLvl[ATK]
        public double Hero { get; internal set; }          // HERO_LVL_MULT ^ heroLevel
        public double Weapons { get; internal set; }       // 1 + (equippedWeaponPct + weaponPassivePct) / 100
        public double Skills { get; internal set; }        // 1 + skillDpsPct / 100
        public double Companions { get; internal set; }    // 1 + mpSufficiency * companionDpsPct / 100
        public double Fish { get; internal set; }          // 1 + (FISH_BOARD_DPS_PCT + missileDpsPct) / 100 (rated)
        public double Research { get; internal set; }      // 1 + researchPct
        public double Runes { get; internal set; }         // 1 + runePct (P1)
        public double Awakening { get; internal set; }     // AWAKEN_MULT ^ awakenRank
        public double Crit { get; internal set; }          // critFactor = 1 + critRate * (critDmg - 1)
        public double Speed { get; internal set; }         // speedFactor = 1 + TRAIN_PER_LVL[ATK_SPD] * min(n, cap)

        // ------------------------------------------------------------------ inputs behind the factors
        public double EquippedWeaponPct { get; internal set; }
        public double WeaponPassivePct { get; internal set; }
        public double SkillDpsPct { get; internal set; }
        public double CompanionDpsPct { get; internal set; }
        public double MpSufficiency { get; internal set; }
        public double FishBoardDpsPct { get; internal set; }
        public double MissileDpsPct { get; internal set; }   // rated-only (issue #15); 0 unless supplied
        public double ResearchPct { get; internal set; }
        public double RunePct { get; internal set; }
        public double CritRate { get; internal set; }        // 0 .. TRAIN_CAP * TRAIN_PER_LVL
        public double CritDmg { get; internal set; }         // CRIT_DMG_BASE + TRAIN_PER_LVL * n

        /// <summary>Hero attack progress per tick in ATTACK_PROGRESS_UNIT fixed point (03 section 2.1).</summary>
        public int AttackGainUnits { get; internal set; }

        /// <summary>Gold and EXP per kill multipliers (03 section 5.1; Research's gold factor is MVP).</summary>
        public double GoldMult { get; internal set; }
        public double ExpMult { get; internal set; }

        // ------------------------------------------------------------------ realised units (03 7.1, issue #10)

        /// <summary>One non-crit, unbuffed hero hit: Base * Training * Hero * Weapons * live board * Research * Runes * Awakening.</summary>
        public double HeroHit { get; internal set; }

        /// <summary>One second of expected hero attack: HeroHit * Crit * Speed. Skill per-cast numbers are % of it.</summary>
        public double SkillUnit { get; internal set; }

        /// <summary>SkillUnit * Skills. Companion DAMAGE per-cast numbers are % of it (issue #243).</summary>
        public double CompanionUnit { get; internal set; }

        /// <summary>The rated DPS: the product of every factor (00 section 7). Excludes transient buffs.</summary>
        public double RatedDps { get; internal set; }

        public double Factor(PowerLayer layer)
        {
            switch (layer)
            {
                case PowerLayer.Base: return Base;
                case PowerLayer.Training: return Training;
                case PowerLayer.Hero: return Hero;
                case PowerLayer.Weapons: return Weapons;
                case PowerLayer.Skills: return Skills;
                case PowerLayer.Companions: return Companions;
                case PowerLayer.Fish: return Fish;
                case PowerLayer.Research: return Research;
                case PowerLayer.Runes: return Runes;
                case PowerLayer.Awakening: return Awakening;
                case PowerLayer.Crit: return Crit;
                case PowerLayer.Speed: return Speed;
                default: return 1.0;
            }
        }
    }
}
