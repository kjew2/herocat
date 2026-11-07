using System.Collections.Generic;

namespace HeroCat.Sim.Content
{
    // Row types built by ContentDb.Build (09 section 9.3). Sealed, get-only, collections read-only. Strings are
    // localization keys and asset addresses only; the text lives in Runtime's Localizer (09 section 13).
    // Defs for tables whose systems have not landed (companions, runes, research, quests, dungeons, shop, inbox,
    // enemies, bosses, plausibility) are added by those issues on top of their generated DTOs.

    /// <summary>A grantlist entry: a currency grant (Currency set) or an item grant (ItemKind and ItemId set).</summary>
    public readonly struct Grant
    {
        public Grant(Currency? currency, string itemKind, string itemId, long amount)
        {
            Currency = currency;
            ItemKind = itemKind;
            ItemId = itemId;
            Amount = amount;
        }

        public Currency? Currency { get; }
        public string ItemKind { get; }   // WEAPON, SKILL, COMPANION, RUNE
        public string ItemId { get; }
        public long Amount { get; }
    }

    public sealed class StatDef
    {
        internal StatDef(Stat id, bool trainable, double? perLvl, int? cap, double? baseValue, Tier tier, string nameKey,
                         string descKey, string icon)
        {
            Id = id; Trainable = trainable; PerLvl = perLvl; Cap = cap; Base = baseValue; Tier = tier;
            NameKey = nameKey; DescKey = descKey; Icon = icon;
        }

        public Stat Id { get; }
        public bool Trainable { get; }
        public double? PerLvl { get; }   // TRAIN_PER_LVL[stat]
        public int? Cap { get; }         // TRAIN_CAP[stat]; null = uncapped
        public double? Base { get; }
        public Tier Tier { get; }
        public string NameKey { get; }
        public string DescKey { get; }
        public string Icon { get; }
    }

    public sealed class TrainingCurveDef
    {
        internal TrainingCurveDef(Stat stat, CurveDef cost) { Stat = stat; Cost = cost; }

        public Stat Stat { get; }
        public CurveDef Cost { get; }   // TRAIN_COST0 * TRAIN_COST_G^n
    }

    public sealed class ChapterDef
    {
        internal ChapterDef(string id, int index, int fromStage, string nameKey, string background, string music, Tier tier)
        {
            Id = id; Index = index; FromStage = fromStage; NameKey = nameKey; Background = background; Music = music;
            Tier = tier;
        }

        public string Id { get; }
        public int Index { get; }
        public int FromStage { get; }
        public string NameKey { get; }
        public string Background { get; }
        public string Music { get; }
        public Tier Tier { get; }
    }

    public sealed class RarityDef
    {
        internal RarityDef(Rarity id, string code, string color, string shapeIcon, double weaponAtkPct,
                           double weaponPassivePct, double skillDpsPct, double companionDpsPct, double companionAtkMult, double runeStatPct,
                           long limitBreakCost, long dupeOverflowRockCandy, double overrideTolerance, string nameKey)
        {
            Id = id; Code = code; Color = color; ShapeIcon = shapeIcon; WeaponAtkPct = weaponAtkPct;
            WeaponPassivePct = weaponPassivePct; SkillDpsPct = skillDpsPct; CompanionDpsPct = companionDpsPct;
            CompanionAtkMult = companionAtkMult;
            RuneStatPct = runeStatPct; LimitBreakCost = limitBreakCost; DupeOverflowRockCandy = dupeOverflowRockCandy;
            OverrideTolerance = overrideTolerance; NameKey = nameKey;
        }

        public Rarity Id { get; }
        public string Code { get; }
        public string Color { get; }
        public string ShapeIcon { get; }
        public double WeaponAtkPct { get; }
        public double WeaponPassivePct { get; }
        public double SkillDpsPct { get; }
        public double CompanionDpsPct { get; }

        /// <summary>COMPANION_ATK_MULT[r]: a companion's auto-attack multiplier by rarity, not level (00 decision 111).</summary>
        public double CompanionAtkMult { get; }
        public double RuneStatPct { get; }
        public long LimitBreakCost { get; }
        public long DupeOverflowRockCandy { get; }
        public double OverrideTolerance { get; }
        public string NameKey { get; }
    }

    public sealed class WeaponDef
    {
        internal WeaponDef(string id, Rarity rarity, double atkPct, double passivePct, Element element, Tier tier,
                           string nameKey, string descKey, string icon, string sprite)
        {
            Id = id; Rarity = rarity; AtkPct = atkPct; PassivePct = passivePct; Element = element; Tier = tier;
            NameKey = nameKey; DescKey = descKey; Icon = icon; Sprite = sprite;
        }

        public string Id { get; }
        public Rarity Rarity { get; }
        public double AtkPct { get; }
        public double PassivePct { get; }
        public Element Element { get; }
        public Tier Tier { get; }
        public string NameKey { get; }
        public string DescKey { get; }
        public string Icon { get; }
        public string Sprite { get; }
    }

    public sealed class SkillDef
    {
        internal SkillDef(string id, Rarity rarity, double dpsPct, CastType castType, int cooldownTicks, int? durationTicks,
                          double? buffPct, double damagePctPerCast, double damagePctPerTick, Tier tier, string nameKey,
                          string descKey, string icon, string vfx)
        {
            Id = id; Rarity = rarity; DpsPct = dpsPct; CastType = castType; CooldownTicks = cooldownTicks;
            DurationTicks = durationTicks; BuffPct = buffPct; DamagePctPerCast = damagePctPerCast;
            DamagePctPerTick = damagePctPerTick; Tier = tier; NameKey = nameKey; DescKey = descKey; Icon = icon; Vfx = vfx;
        }

        public string Id { get; }
        public Rarity Rarity { get; }
        public double DpsPct { get; }
        public CastType CastType { get; }
        public int CooldownTicks { get; }
        public int? DurationTicks { get; }
        public double? BuffPct { get; }

        /// <summary>INSTANT: dps_pct * cooldown / ATTACK_INTERVAL_TICKS, in % of one SkillUnit (04 section 7.1); else 0.</summary>
        public double DamagePctPerCast { get; }

        /// <summary>DOT: dps_pct * cooldown / duration / ATTACK_INTERVAL_TICKS (04 section 7.1); else 0.</summary>
        public double DamagePctPerTick { get; }

        public Tier Tier { get; }
        public string NameKey { get; }
        public string DescKey { get; }
        public string Icon { get; }
        public string Vfx { get; }
    }

    /// <summary>
    /// A companion (04 section 8.1, 09 section 5.11; issue #161). <see cref="Magnitude"/> is derived at build from
    /// dps_pct by ability type, at level 1; the tick loop scales it by the item's LevelMult.
    /// </summary>
    public sealed class CompanionDef
    {
        internal CompanionDef(string id, Rarity rarity, AbilityType abilityType, double dpsPct, int mpCost, int cooldownTicks,
                              int? durationTicks, double magnitude, Tier tier, string nameKey, string descKey, string icon,
                              string sprite, string vfx)
        {
            Id = id; Rarity = rarity; AbilityType = abilityType; DpsPct = dpsPct; MpCost = mpCost; CooldownTicks = cooldownTicks;
            DurationTicks = durationTicks; Magnitude = magnitude; Tier = tier; NameKey = nameKey; DescKey = descKey;
            Icon = icon; Sprite = sprite; Vfx = vfx;
        }

        public string Id { get; }
        public Rarity Rarity { get; }
        public AbilityType AbilityType { get; }

        /// <summary>Rated contribution at full MP uptime, level 1 (COMPANION_DPS_PCT[r]).</summary>
        public double DpsPct { get; }

        public int MpCost { get; }
        public int CooldownTicks { get; }

        /// <summary>BUFF_ATK and BUFF_GOLD only.</summary>
        public int? DurationTicks { get; }

        /// <summary>
        /// 04 section 8.1, at level 1: DAMAGE <c>damagePctPerCast = dps_pct * cooldown / ATTACK_INTERVAL_TICKS</c> (% of one
        /// CompanionUnit); BUFF_ATK <c>buff_pct = dps_pct * cooldown / duration</c>; BUFF_GOLD
        /// <c>gold_buff_pct = 2 * dps_pct * cooldown / duration</c>; MP_REGEN <c>mp_restored = dps_pct / 4</c> (MP per cast).
        /// </summary>
        public double Magnitude { get; }

        /// <summary>
        /// The ability's dps_pct counts in companionMult: DAMAGE and BUFF_ATK. MP_REGEN counts 0 (04 section 8.1) and so does
        /// BUFF_GOLD, which deals no damage (its dps_pct is a value proxy; tools/power_curve_model.py reads it the same way).
        /// </summary>
        public bool CountsInLayer => AbilityType == AbilityType.DAMAGE || AbilityType == AbilityType.BUFF_ATK;

        public Tier Tier { get; }
        public string NameKey { get; }
        public string DescKey { get; }
        public string Icon { get; }
        public string Sprite { get; }
        public string Vfx { get; }
    }

    public sealed class BannerLevelDef
    {
        internal BannerLevelDef(int level, int expRequired, IReadOnlyDictionary<Rarity, double> rates)
        {
            Level = level; ExpRequired = expRequired; Rates = rates;
        }

        public int Level { get; }
        public int ExpRequired { get; }   // BANNER_LVL_EXP, cumulative
        public IReadOnlyDictionary<Rarity, double> Rates { get; }
    }

    public sealed class BannerDef
    {
        internal BannerDef(Banner id, bool enabled, Tier tier, int pullCostDiamonds, Currency ticketCurrency,
                           IReadOnlyDictionary<Rarity, int> poolSize, int pitySoftStart, double pitySoftStep, int pityHard,
                           double mShareOfLplus, int guaranteeBatchSize, Rarity guaranteeMinRarity, int expPerPull,
                           int sortOrder, string nameKey, string icon, string bannerArt,
                           IReadOnlyList<BannerLevelDef> levels, IReadOnlyDictionary<Rarity, IReadOnlyList<string>> pool)
        {
            Id = id; Enabled = enabled; Tier = tier; PullCostDiamonds = pullCostDiamonds; TicketCurrency = ticketCurrency;
            PoolSize = poolSize; PitySoftStart = pitySoftStart; PitySoftStep = pitySoftStep; PityHard = pityHard;
            MShareOfLplus = mShareOfLplus; GuaranteeBatchSize = guaranteeBatchSize; GuaranteeMinRarity = guaranteeMinRarity;
            ExpPerPull = expPerPull; SortOrder = sortOrder; NameKey = nameKey; Icon = icon; BannerArt = bannerArt;
            Levels = levels; Pool = pool;
            BaseLplus = levels.Count > 0 ? levels[0].Rates[Rarity.LEGENDARY] + levels[0].Rates[Rarity.MYTHIC] : 0.0;
        }

        public Banner Id { get; }
        public bool Enabled { get; }
        public Tier Tier { get; }
        public int PullCostDiamonds { get; }
        public Currency TicketCurrency { get; }
        public IReadOnlyDictionary<Rarity, int> PoolSize { get; }
        public int PitySoftStart { get; }
        public double PitySoftStep { get; }
        public int PityHard { get; }
        public double MShareOfLplus { get; }
        public int GuaranteeBatchSize { get; }
        public Rarity GuaranteeMinRarity { get; }
        public int ExpPerPull { get; }
        public int SortOrder { get; }
        public string NameKey { get; }
        public string Icon { get; }
        public string BannerArt { get; }

        /// <summary>[0] is level 1. Empty for a banner without banner_levels rows (a disabled one).</summary>
        public IReadOnlyList<BannerLevelDef> Levels { get; }

        /// <summary>Level-1 L + M rate.</summary>
        public double BaseLplus { get; }

        /// <summary>Item ids per rarity, ordinal-sorted (09 section 3.4): what the server's audit row indexes.</summary>
        public IReadOnlyDictionary<Rarity, IReadOnlyList<string>> Pool { get; }

        public int MaxLevel => Levels.Count;

        /// <summary>L+ rate on pull n since the last L+ (00 section 6.3). n is 1-based.</summary>
        public double PityRate(int n)
        {
            if (n >= PityHard) return 1.0;
            int over = n - PitySoftStart;
            double r = BaseLplus + PitySoftStep * (over > 0 ? over : 0);
            return r < 1.0 ? r : 1.0;
        }

        /// <summary>Count of levels with ExpRequired &lt;= exp, at least 1 (04 section 4).</summary>
        public int LevelFor(long exp)
        {
            int n = 0;
            for (int i = 0; i < Levels.Count; i++) if (Levels[i].ExpRequired <= exp) n++;
            return n < 1 ? 1 : n;
        }

        /// <summary>The rate table for a banner level (1-based), clamped to the table.</summary>
        public IReadOnlyDictionary<Rarity, double> RateTable(int level)
        {
            int i = level - 1;
            if (i < 0) i = 0;
            if (i >= Levels.Count) i = Levels.Count - 1;
            return Levels[i].Rates;
        }
    }

    public sealed class AwakeningDef
    {
        internal AwakeningDef(int rank, int stageGate, long rockCandyCost, double mult,
                              int runeSlotsAfter, Tier tier, string nameKey, string icon)
        {
            Rank = rank; StageGate = stageGate; RockCandyCost = rockCandyCost; Mult = mult;
            RuneSlotsAfter = runeSlotsAfter; Tier = tier; NameKey = nameKey;
            Icon = icon;
        }

        public int Rank { get; }
        public int StageGate { get; }
        public long RockCandyCost { get; }
        public double Mult { get; }
        public int RuneSlotsAfter { get; }
        public Tier Tier { get; }
        public string NameKey { get; }
        public string Icon { get; }
    }

    public sealed class PartyLevelsDef
    {
        internal PartyLevelsDef(CurveDef exp, double? atkPctPerLevel, double? multPerLevel, int levelCap, CurveDef mult)
        {
            Exp = exp; AtkPctPerLevel = atkPctPerLevel; MultPerLevel = multPerLevel; LevelCap = levelCap; Mult = mult;
        }

        /// <summary>PARTY_LVL_EXP(l) = exp0 * exp_g^l, EXP to go from level l to l + 1.</summary>
        public CurveDef Exp { get; }

        public double? AtkPctPerLevel { get; }
        public double? MultPerLevel { get; }
        public int LevelCap { get; }

        /// <summary>PARTY_LVL_MULT^level as a cached table (base 1); null when the linear column is the populated one.</summary>
        public CurveDef Mult { get; }
    }

    public sealed class FishLevelDef
    {
        internal FishLevelDef(int level, long discoveryCoins, double missilePct, double mergeCoins, double boardDpsPct,
                              string sprite, string tint, string nameKey)
        {
            Level = level; DiscoveryCoins = discoveryCoins; MissilePct = missilePct; MergeCoins = mergeCoins;
            BoardDpsPct = boardDpsPct; Sprite = sprite; Tint = tint; NameKey = nameKey;
        }

        public int Level { get; }
        public long DiscoveryCoins { get; }   // FISH_DISCOVERY_COINS[n]
        public double MissilePct { get; }     // FISH_MISSILE_PCT[n], % of one hero hit

        /// <summary>discovery_coins * FISH_MERGE_COINS_PCT, unrounded (issue #16).</summary>
        public double MergeCoins { get; }

        /// <summary>FISH_BOARD_DPS_FACTOR * missile_pct (05 section 5.2).</summary>
        public double BoardDpsPct { get; }

        public string Sprite { get; }
        public string Tint { get; }
        public string NameKey { get; }
    }

    public sealed class TankDef
    {
        internal TankDef(int cap0, double refillS0, int refillTicks0, int releaseLevel, double refillFloorS)
        {
            Cap0 = cap0; RefillS0 = refillS0; RefillTicks0 = refillTicks0; ReleaseLevel = releaseLevel;
            RefillFloorS = refillFloorS;
        }

        public int Cap0 { get; }            // TANK_CAP0
        public double RefillS0 { get; }     // TANK_REFILL_S0
        public int RefillTicks0 { get; }    // round(refill_s0 * TICK_HZ)
        public int ReleaseLevel { get; }
        public double RefillFloorS { get; }
    }

    public sealed class TutorialStepDef
    {
        internal TutorialStepDef(string id, int order, TutorialTrigger trigger, string triggerValue, string screen,
                                 string highlight, string textKey, string expectEvent, int? expectCount,
                                 IReadOnlyList<Grant> grants, bool skippable, Tier tier)
        {
            Id = id; Order = order; Trigger = trigger; TriggerValue = triggerValue; Screen = screen;
            Highlight = highlight; TextKey = textKey; ExpectEvent = expectEvent; ExpectCount = expectCount;
            Grants = grants; Skippable = skippable; Tier = tier;
        }

        public string Id { get; }
        public int Order { get; }
        public TutorialTrigger Trigger { get; }
        public string TriggerValue { get; }
        public string Screen { get; }
        public string Highlight { get; }
        public string TextKey { get; }
        public string ExpectEvent { get; }
        public int? ExpectCount { get; }

        /// <summary>
        /// The event names <see cref="ExpectEvent"/> lists, split on <c>|</c> (09 section 5.29; issue #246 item 1): any
        /// of them counts toward <see cref="ExpectCount"/>. Empty for a tap-to-continue step.
        /// </summary>
        public IReadOnlyList<string> ExpectEvents => Names();

        /// <summary>True when an event of this name counts toward the step.</summary>
        public bool Expects(string eventName) => eventName != null && System.Array.IndexOf(Names(), eventName) >= 0;

        string[] Names() =>
            _expectEvents ??= string.IsNullOrEmpty(ExpectEvent) ? System.Array.Empty<string>() : ExpectEvent.Split('|');

        string[] _expectEvents;
        public IReadOnlyList<Grant> Grants { get; }
        public bool Skippable { get; }
        public Tier Tier { get; }
    }

    public sealed class ProgressTargetDef
    {
        internal ProgressTargetDef(int day, int stage, double tolerance) { Day = day; Stage = stage; Tolerance = tolerance; }

        public int Day { get; }
        public int Stage { get; }
        public double Tolerance { get; }
    }

    public sealed class FeatureUnlockDef
    {
        internal FeatureUnlockDef(string feature, FeatureCondition condition, int? threshold, string screen,
                                  string lockedTextKey, Tier tier)
        {
            Feature = feature; Condition = condition; Threshold = threshold; Screen = screen;
            LockedTextKey = lockedTextKey; Tier = tier;
        }

        public string Feature { get; }
        public FeatureCondition Condition { get; }
        public int? Threshold { get; }
        public string Screen { get; }
        public string LockedTextKey { get; }
        public Tier Tier { get; }
    }

    public sealed class AwakeningTargetDef
    {
        internal AwakeningTargetDef(int rank, int maxDaysAfterGate) { Rank = rank; MaxDaysAfterGate = maxDaysAfterGate; }

        public int Rank { get; }
        public int MaxDaysAfterGate { get; }
    }

    /// <summary>A Research node (09 section 5.18, 03 section 6). MVP rows; an M0 build has none.</summary>
    public sealed class ResearchDef
    {
        internal ResearchDef(string id, ResearchEffect effect, double effectPerLevel, double? effectFloor, int maxLevel,
                             double cost0, double costG, int unlockAwakenRank, Tier tier, int layoutRow = 0, int layoutCol = 0,
                             string nameKey = null, string descKey = null, string icon = null)
        {
            Id = id; Effect = effect; EffectPerLevel = effectPerLevel; EffectFloor = effectFloor; MaxLevel = maxLevel;
            Cost0 = cost0; CostG = costG; UnlockAwakenRank = unlockAwakenRank; Tier = tier;
            LayoutRow = layoutRow; LayoutCol = layoutCol; NameKey = nameKey; DescKey = descKey; Icon = icon;
            _cost = new CurveDef("research_cost_" + id, cost0, costG, 0, maxLevel < 0 ? 1 : maxLevel + 1);
        }

        readonly CurveDef _cost;

        /// <summary>
        /// Fish Coins for level <paramref name="level"/> -> level + 1: <c>RES_COST0 * RES_COST_G^level</c> (03 section 6), from
        /// the node's precomputed GeometricTable (repeated multiplication, never Math.Pow; 00 section 3). Unrounded.
        /// </summary>
        public double Cost(int level) => _cost.Eval(level < 0 ? 0 : level);

        public string Id { get; }
        public ResearchEffect Effect { get; }
        public double EffectPerLevel { get; }
        public double? EffectFloor { get; }
        public int MaxLevel { get; }
        public double Cost0 { get; }
        public double CostG { get; }
        public int UnlockAwakenRank { get; }
        public Tier Tier { get; }

        /// <summary>Presentation only (03 section 6): SCR_RESEARCH orders its rows by (row, col); the sim ignores both.</summary>
        public int LayoutRow { get; }
        public int LayoutCol { get; }

        public string NameKey { get; }
        public string DescKey { get; }
        public string Icon { get; }
    }

    /// <summary>
    /// A daily or weekly mission row (09 sections 5.21-5.22, 06 sections 5.1-5.3), or an achievement rung (09 section
    /// 5.23, 06 section 5.4; issue #171). The rows are the only home of quest reward amounts (issues #55, #244);
    /// <see cref="RewardAmount"/> 0 is a counting row (d_login) that pays nothing.
    /// </summary>
    public sealed class QuestDef
    {
        internal QuestDef(string id, QuestKind kind, QuestGoal goal, string filter, int filterValue, long target,
                          Currency rewardCurrency, long rewardAmount, int sortOrder, Tier tier, string nameKey, string icon,
                          string ladder = null, int rung = 0)
        {
            Id = id; Kind = kind; Goal = goal; Filter = filter; FilterValue = filterValue; Target = target;
            RewardCurrency = rewardCurrency; RewardAmount = rewardAmount; SortOrder = sortOrder; Tier = tier;
            NameKey = nameKey; Icon = icon; Ladder = ladder; Rung = rung;
        }

        /// <summary>The achievement ladder id (06 section 5.4); null on a daily or weekly row.</summary>
        public string Ladder { get; }

        /// <summary>The rung within <see cref="Ladder"/>, 1..N (E060); 0 on a daily or weekly row.</summary>
        public int Rung { get; }

        public string Id { get; }
        public QuestKind Kind { get; }
        public QuestGoal Goal { get; }

        /// <summary>A Banner name for SUMMON, a Dungeon name for CLEAR_DUNGEON; null matches any (w_summons, w_dungeons).</summary>
        public string Filter { get; }

        /// <summary><see cref="Filter"/> as its enum ordinal, or -1 for any; what the sim compares (no string per event).</summary>
        internal int FilterValue { get; }

        public long Target { get; }
        public Currency RewardCurrency { get; }
        public long RewardAmount { get; }
        /// <summary>Display and claim-all order: sort_order for missions; for rungs, ladder by first row then rung.</summary>
        public int SortOrder { get; }
        public Tier Tier { get; }
        public string NameKey { get; }

        /// <summary>The row's icon; null on an achievement rung (09's achievements table has no icon column).</summary>
        public string Icon { get; }

        /// <summary>True when an event with <paramref name="filter"/> (an enum ordinal, -1 for none) counts toward this row.</summary>
        internal bool Matches(int filter) => FilterValue < 0 || FilterValue == filter;
    }

    /// <summary>
    /// A diamond-for-dungeon-ticket exchange offer (09 section 5.37, 06 section 8.4; issue #174): Qty of them costs
    /// Qty x <see cref="PriceAmount"/> <see cref="PriceCurrency"/> (DIAMOND, E059) and grants Qty x <see cref="GrantAmount"/>
    /// <see cref="GrantCurrency"/> (a dungeon ticket, E059), at most <see cref="DailyLimit"/> a day key.
    /// </summary>
    public sealed class ExchangeOfferDef
    {
        internal ExchangeOfferDef(string id, Currency priceCurrency, long priceAmount, Currency grantCurrency, long grantAmount,
                                  int dailyLimit, int sortOrder, Tier tier, string nameKey, string icon)
        {
            Id = id; PriceCurrency = priceCurrency; PriceAmount = priceAmount; GrantCurrency = grantCurrency; GrantAmount = grantAmount;
            DailyLimit = dailyLimit; SortOrder = sortOrder; Tier = tier; NameKey = nameKey; Icon = icon;
        }

        public string Id { get; }
        public Currency PriceCurrency { get; }
        public long PriceAmount { get; }
        public Currency GrantCurrency { get; }
        public long GrantAmount { get; }
        public int DailyLimit { get; }
        public int SortOrder { get; }
        public Tier Tier { get; }
        public string NameKey { get; }
        public string Icon { get; }
    }

    /// <summary>
    /// A dungeon (09 section 5.24, 06 section 6.1; issue #173): a ladder of <see cref="Floors"/> fixed boss fights, floor f
    /// fought at stage-equivalent <c>f * FloorStageStep</c>. <see cref="Kind"/> and <see cref="TicketCurrency"/> are null only
    /// for the P1 Tower row. The unlock is the feature_unlocks row DUNGEONS, not a column (issue #45).
    /// </summary>
    public sealed class DungeonDef
    {
        internal DungeonDef(string id, Dungeon? kind, Currency? ticketCurrency, int ticketsPerDay, int floorStageStep, int floors,
                            bool sweepAllowed, Tier tier, string nameKey, string descKey, string icon, string background,
                            IReadOnlyList<DungeonFloorDef> floorDefs)
        {
            Id = id; Kind = kind; TicketCurrency = ticketCurrency; TicketsPerDay = ticketsPerDay; FloorStageStep = floorStageStep;
            Floors = floors; SweepAllowed = sweepAllowed; Tier = tier; NameKey = nameKey; DescKey = descKey; Icon = icon;
            Background = background; FloorDefs = floorDefs;
        }

        public string Id { get; }
        public Dungeon? Kind { get; }
        public Currency? TicketCurrency { get; }
        public int TicketsPerDay { get; }        // DUNGEON_TICKETS_PER_DAY
        public int FloorStageStep { get; }       // DUNGEON_FLOOR_STAGE_STEP
        public int Floors { get; }               // DUNGEON_FLOORS_MVP; == FloorDefs.Count (E034)
        public bool SweepAllowed { get; }
        public Tier Tier { get; }
        public string NameKey { get; }
        public string DescKey { get; }
        public string Icon { get; }
        public string Background { get; }

        /// <summary>By floor: FloorDefs[f - 1] is floor f (E060).</summary>
        public IReadOnlyList<DungeonFloorDef> FloorDefs { get; }

        /// <summary>Floor <paramref name="floor"/>, or null outside 1..Floors.</summary>
        public DungeonFloorDef Floor(int floor) => floor >= 1 && floor <= FloorDefs.Count ? FloorDefs[floor - 1] : null;
    }

    /// <summary>
    /// One dungeon floor (09 section 5.25): its stage-equivalent and its reward. <see cref="RewardAmount"/> is resolved at build:
    /// the fixed reward_amount, or for PARTY_EXP rows reward_kills_equiv x ExpPerKill(StageEquiv) from curves[exp_per_kill]
    /// (06 section 6.3, EXP_FLOOR before EXP_GAIN training).
    /// </summary>
    public sealed class DungeonFloorDef
    {
        internal DungeonFloorDef(int floor, int stageEquiv, Currency rewardCurrency, double rewardAmount, int? rewardKillsEquiv,
                                 string boss, IReadOnlyList<Grant> firstClearBonus)
        {
            Floor = floor; StageEquiv = stageEquiv; RewardCurrency = rewardCurrency; RewardAmount = rewardAmount;
            RewardKillsEquiv = rewardKillsEquiv; Boss = boss; FirstClearBonus = firstClearBonus;
        }

        public int Floor { get; }
        public int StageEquiv { get; }
        public Currency RewardCurrency { get; }
        public double RewardAmount { get; }
        public int? RewardKillsEquiv { get; }
        public string Boss { get; }
        public IReadOnlyList<Grant> FirstClearBonus { get; }
    }
}
