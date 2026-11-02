namespace HeroCat.Sim.Power
{
    /// <summary>
    /// The layers of the rated power formula (00 section 7, 03 sections 7.1 and 7.4), in the formula's order. The
    /// layer-share report (03 section 8.5) needs the weighted per-slot factors of 03 section 7.4, which take logs, so they
    /// are computed outside the sim (Runtime's <c>LayerFactors</c>); <see cref="Base"/> is not a layer for the 5% floor.
    /// </summary>
    public enum PowerLayer { Base, Training, PartyLevel, Weapons, Skills, Companions, Fish, Research, Runes, Awakening, Crit, Speed }

    /// <summary>
    /// Every factor and unit of the power formula for one (ContentDb, GameState), as a value (03 sections 7.1-7.2; 00
    /// decision 109). Party-wide factors are scalars; per-slot ones are read by slot index 0..PARTY_SLOTS-1 (an empty slot
    /// reads zero units and neutral factors). Percent fields are percentage points (<c>40</c> = +40%);
    /// <see cref="ResearchPct"/>, <see cref="RunePct"/>, <see cref="CritRate"/> and <see cref="MpSufficiency"/> are
    /// fractions. Factors are multipliers (1.0 = no effect). The per-slot arrays are owned by this value and never
    /// written after <see cref="PowerFormula.Compute"/> returns.
    /// </summary>
    public struct PowerBreakdown
    {
        internal PowerBreakdown(int slots) : this()
        {
            SlotCount = slots;
            OccupiedArr = new bool[slots];
            EquippedWeaponPctArr = new double[slots];
            SkillDpsPctArr = new double[slots];
            DamagePctArr = new double[slots];
            BuffAtkPctArr = new double[slots];
            MpSufficiencyArr = new double[slots];
            AwakeningArr = new double[slots];
            CompanionAtkMultArr = new double[slots];
            WeaponFactorArr = new double[slots];
            SkillFactorArr = new double[slots];
            OwnFactorArr = new double[slots];
            SlotHitArr = new double[slots];
            SlotUnitArr = new double[slots];
            CompanionUnitArr = new double[slots];
            SlotDpsArr = new double[slots];
        }

        // ------------------------------------------------------------------ party-wide factors (03 7.1)
        public double Base { get; internal set; }          // BASE_ATK, one companion's auto-attack
        public double Training { get; internal set; }      // 1 + TRAIN_PER_LVL[ATK] * trainLvl[ATK]
        public double Party { get; internal set; }         // PARTY_LVL_MULT ^ partyLevel
        public double Fish { get; internal set; }          // 1 + FISH_BOARD_DPS_PCT / 100, the passive board in every slot hit
        public double Research { get; internal set; }      // 1 + researchPct
        public double Runes { get; internal set; }         // 1 + runePct (P1)
        public double Crit { get; internal set; }          // critFactor = 1 + critRate * (critDmg - 1)
        public double Speed { get; internal set; }         // speedFactor = 1 + TRAIN_PER_LVL[ATK_SPD] * min(n, cap)
        public double PartyBuff { get; internal set; }     // 1 + sum_i mpSufficiency_i * buffAtkPct_i / 100 (BUFF_ATK companions)

        // ------------------------------------------------------------------ party-wide inputs
        public double WeaponPassivePct { get; internal set; }
        public double FishBoardDpsPct { get; internal set; }
        public double MissileDpsPct { get; internal set; }   // rated-only (issue #15); 0 unless supplied
        public double ResearchPct { get; internal set; }
        public double RunePct { get; internal set; }
        public double CritRate { get; internal set; }        // 0 .. TRAIN_CAP * TRAIN_PER_LVL
        public double CritDmg { get; internal set; }         // CRIT_DMG_BASE + TRAIN_PER_LVL * n

        /// <summary>Every slot's attack progress per tick in ATTACK_PROGRESS_UNIT fixed point (03 section 2.1), party-wide.</summary>
        public int AttackGainUnits { get; internal set; }

        /// <summary>Gold and EXP per kill multipliers (03 section 5.1; Research's gold factor is MVP).</summary>
        public double GoldMult { get; internal set; }
        public double ExpMult { get; internal set; }

        /// <summary>MpPoolMax and MpRegenPerTick with their Research terms (03 section 2.4), the same for every pool.</summary>
        public double MpPoolMax { get; internal set; }
        public double MpRegenPerTick { get; internal set; }

        // ------------------------------------------------------------------ party totals (03 7.1)

        /// <summary>PARTY_SLOTS: the length of every per-slot reading.</summary>
        public int SlotCount { get; }

        /// <summary>Occupied slots: a known, owned companion (00 decision 109).</summary>
        public int OccupiedCount { get; internal set; }

        /// <summary>sum_i SlotHit_i over occupied slots: the unit of fish missiles and Tap Zap (03 section 2.5).</summary>
        public double PartyHit { get; internal set; }

        /// <summary>(sum_i SlotDps_i) * PartyBuff: the party's rated damage without missiles.</summary>
        public double AutoDps { get; internal set; }

        /// <summary>PartyHit * Speed * missileDpsPct / 100 (05; rated only).</summary>
        public double MissileDps { get; internal set; }

        /// <summary>The rated DPS, AutoDps + MissileDps (00 section 7). Excludes transient buffs.</summary>
        public double RatedDps { get; internal set; }

        // ------------------------------------------------------------------ per slot (03 7.1-7.2)
        internal bool[] OccupiedArr;
        internal double[] EquippedWeaponPctArr, SkillDpsPctArr, DamagePctArr, BuffAtkPctArr, MpSufficiencyArr, AwakeningArr, CompanionAtkMultArr;
        internal double[] WeaponFactorArr, SkillFactorArr, OwnFactorArr, SlotHitArr, SlotUnitArr, CompanionUnitArr, SlotDpsArr;

        static double At(double[] a, int i, double none = 0.0) => a != null && i >= 0 && i < a.Length ? a[i] : none;

        /// <summary>The slot holds a known, owned companion (it attacks and casts).</summary>
        public bool Occupied(int slot) => OccupiedArr != null && slot >= 0 && slot < OccupiedArr.Length && OccupiedArr[slot];

        /// <summary>WEAPON_ATK_PCT * LevelMult of the slot's weapon (0 unarmed).</summary>
        public double EquippedWeaponPct(int slot) => At(EquippedWeaponPctArr, slot);

        /// <summary>dps_pct * LevelMult of the slot's skill (0 none).</summary>
        public double SkillDpsPct(int slot) => At(SkillDpsPctArr, slot);

        /// <summary>The slot's companion's level-scaled dps_pct when it is DAMAGE, else 0.</summary>
        public double DamagePct(int slot) => At(DamagePctArr, slot);

        /// <summary>The slot's companion's level-scaled dps_pct when it is BUFF_ATK, else 0.</summary>
        public double BuffAtkPct(int slot) => At(BuffAtkPctArr, slot);

        /// <summary>mpSufficiency_i of the slot's own pool, a fraction (1 for an empty slot).</summary>
        public double MpSufficiency(int slot) => At(MpSufficiencyArr, slot, 1.0);

        /// <summary>COMPANION_ATK_MULT of the slot's companion's rarity (00 decision 111); 1 for an empty slot.</summary>
        public double CompanionAtkMult(int slot) => At(CompanionAtkMultArr, slot, 1.0);

        /// <summary>AWAKEN_MULT ^ rank of the slot.</summary>
        public double Awakening(int slot) => At(AwakeningArr, slot, 1.0);

        /// <summary>1 + (equippedWeaponPct(i) + weaponPassivePct) / 100.</summary>
        public double WeaponFactor(int slot) => At(WeaponFactorArr, slot, 1.0);

        /// <summary>1 + skillDpsPct(i) / 100.</summary>
        public double SkillFactor(int slot) => At(SkillFactorArr, slot, 1.0);

        /// <summary>1 + mpSufficiency_i * damagePct(i) / 100: the slot's own DAMAGE ability.</summary>
        public double OwnFactor(int slot) => At(OwnFactorArr, slot, 1.0);

        /// <summary>One non-crit, unbuffed auto-attack of the slot; 0 for an empty slot.</summary>
        public double SlotHit(int slot) => At(SlotHitArr, slot);

        /// <summary>SlotHit * Crit * Speed: the unit of the slot's INSTANT/DOT skill per-cast numbers.</summary>
        public double SlotUnit(int slot) => At(SlotUnitArr, slot);

        /// <summary>SlotUnit * SkillFactor: the unit of the slot's DAMAGE ability per-cast numbers (decision 94, per slot).</summary>
        public double CompanionUnit(int slot) => At(CompanionUnitArr, slot);

        /// <summary>CompanionUnit * OwnFactor: the slot's rated DPS before the party BUFF_ATK term.</summary>
        public double SlotDps(int slot) => At(SlotDpsArr, slot);
    }
}
