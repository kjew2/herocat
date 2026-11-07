// Generated once from tools/export_content.py REQUIRED_CONSTANTS; ContentDbTests pins the two together (09 acceptance 4).
using System;
using System.Collections.Generic;

namespace HeroCat.Sim.Content
{
    /// <summary>
    /// Every REQUIRED_CONSTANTS row as a typed, get-only property (09 section 9.2). Property name = PascalCase of the
    /// key (TICK_HZ -> TickHz). Sim code reads these and never a literal (00 contract 3).
    /// </summary>
    public sealed class Constants
    {
        Constants() { }

        /// <summary>TICK_HZ (I).</summary>
        public int TickHz { get; private set; }
        /// <summary>BASE_ATK (I).</summary>
        public double BaseAtk { get; private set; }
        /// <summary>BASE_HP (S).</summary>
        public double BaseHp { get; private set; }
        /// <summary>ATTACK_INTERVAL_TICKS (I).</summary>
        public int AttackIntervalTicks { get; private set; }
        /// <summary>KILLS_PER_STAGE (I).</summary>
        public int KillsPerStage { get; private set; }
        /// <summary>SPAWN_DELAY_TICKS (I).</summary>
        public int SpawnDelayTicks { get; private set; }
        /// <summary>BOSS_EVERY (I).</summary>
        public int BossEvery { get; private set; }
        /// <summary>BOSS_HP_MULT (I).</summary>
        public double BossHpMult { get; private set; }
        /// <summary>BOSS_TIME_TICKS (S).</summary>
        public int BossTimeTicks { get; private set; }
        /// <summary>BOSS_GOLD_MULT (I).</summary>
        public double BossGoldMult { get; private set; }
        /// <summary>CHAPTER_WIDTH (S).</summary>
        public int ChapterWidth { get; private set; }
        /// <summary>STAGE_CAP (I).</summary>
        public int StageCap { get; private set; }
        /// <summary>TAP_ZAP_DMG_PCT (S).</summary>
        public double TapZapDmgPct { get; private set; }
        /// <summary>TAP_ZAP_MAX_PER_SEC (S).</summary>
        public int TapZapMaxPerSec { get; private set; }
        /// <summary>TRAIN_BULK_STEPS (I).</summary>
        public IReadOnlyList<int> TrainBulkSteps { get; private set; }
        /// <summary>DIAMONDS_PER_DAY_F2P (S).</summary>
        public int DiamondsPerDayF2p { get; private set; }
        /// <summary>DAY_SECONDS (I).</summary>
        public int DaySeconds { get; private set; }
        /// <summary>OFFLINE_RATE (I).</summary>
        public double OfflineRate { get; private set; }
        /// <summary>OFFLINE_CAP_H (S).</summary>
        public double OfflineCapH { get; private set; }
        /// <summary>OFFLINE_MIN_S (S).</summary>
        public int OfflineMinS { get; private set; }
        /// <summary>PARTY_SLOTS (I): party size, all open from the start (00 decision 109).</summary>
        public int PartySlots { get; private set; }
        /// <summary>RUNE_SLOTS (S).</summary>
        public int RuneSlots { get; private set; }
        /// <summary>DUPE_LVL_BONUS (I).</summary>
        public double DupeLvlBonus { get; private set; }
        /// <summary>FISH_BOARD_W (S).</summary>
        public int FishBoardW { get; private set; }
        /// <summary>FISH_BOARD_H (S).</summary>
        public int FishBoardH { get; private set; }
        /// <summary>FISH_MAX_LVL0 (S).</summary>
        public int FishMaxLvl0 { get; private set; }
        /// <summary>FISH_MERGE_COINS_PCT (S).</summary>
        public double FishMergeCoinsPct { get; private set; }
        /// <summary>FISH_BOARD_DPS_FACTOR (S).</summary>
        public double FishBoardDpsFactor { get; private set; }
        /// <summary>FISH_DISCARD_COINS_PCT (S).</summary>
        public double FishDiscardCoinsPct { get; private set; }
        /// <summary>ATTACK_PROGRESS_UNIT (I).</summary>
        public int AttackProgressUnit { get; private set; }
        /// <summary>CRIT_DMG_BASE (S).</summary>
        public double CritDmgBase { get; private set; }
        /// <summary>MP_POOL0 (S).</summary>
        public double MpPool0 { get; private set; }
        /// <summary>MP_REGEN0 (S).</summary>
        public double MpRegen0 { get; private set; }
        /// <summary>BOSS_ROCK_CANDY_P (S).</summary>
        public double BossRockCandyP { get; private set; }
        /// <summary>FIRST_CLEAR_DIAMONDS (I).</summary>
        public int FirstClearDiamonds { get; private set; }
        /// <summary>INBOX_EXPIRY_DAYS (S).</summary>
        public int InboxExpiryDays { get; private set; }
        /// <summary>DAILY_GIFT_WEAPON_TICKETS (S).</summary>
        public int DailyGiftWeaponTickets { get; private set; }
        /// <summary>LOGIN_DIAMONDS (I).</summary>
        public int LoginDiamonds { get; private set; }
        /// <summary>LIMIT_BREAK_RANKS (S).</summary>
        public int LimitBreakRanks { get; private set; }
        /// <summary>REF_DAILIES_DONE (S).</summary>
        public int RefDailiesDone { get; private set; }
        /// <summary>REF_NEW_BOSSES (S).</summary>
        public int RefNewBosses { get; private set; }
        /// <summary>CLOCK_BACKWARDS_TOLERANCE_S (S): a server time behind the virtual clock by at most this is jitter (issue #246).</summary>
        public int ClockBackwardsToleranceS { get; private set; }

        public bool IsBossStage(int s) => s % BossEvery == 0;

        /// <summary>Chapter = floor((s-1)/CHAPTER_WIDTH)+1, cosmetic (00 section 5.1).</summary>
        public int ChapterOf(int s) => (s - 1) / ChapterWidth + 1;

        /// <summary>A parsed constants value: exactly one member is meaningful, picked by the row's declared type.</summary>
        internal struct Value
        {
            public int Int;
            public long Long;
            public double Double;
            public bool Bool;
            public int[] IntList;
        }

        /// <summary>Keys this class holds, in REQUIRED_CONSTANTS order.</summary>
        public static readonly IReadOnlyList<string> RequiredKeys = new[]
        {
            "TICK_HZ",
            "BASE_ATK",
            "BASE_HP",
            "ATTACK_INTERVAL_TICKS",
            "KILLS_PER_STAGE",
            "SPAWN_DELAY_TICKS",
            "BOSS_EVERY",
            "BOSS_HP_MULT",
            "BOSS_TIME_TICKS",
            "BOSS_GOLD_MULT",
            "CHAPTER_WIDTH",
            "STAGE_CAP",
            "TAP_ZAP_DMG_PCT",
            "TAP_ZAP_MAX_PER_SEC",
            "TRAIN_BULK_STEPS",
            "DIAMONDS_PER_DAY_F2P",
            "DAY_SECONDS",
            "OFFLINE_RATE",
            "OFFLINE_CAP_H",
            "OFFLINE_MIN_S",
            "PARTY_SLOTS",
            "RUNE_SLOTS",
            "DUPE_LVL_BONUS",
            "FISH_BOARD_W",
            "FISH_BOARD_H",
            "FISH_MAX_LVL0",
            "FISH_MERGE_COINS_PCT",
            "FISH_BOARD_DPS_FACTOR",
            "FISH_DISCARD_COINS_PCT",
            "ATTACK_PROGRESS_UNIT",
            "CRIT_DMG_BASE",
            "MP_POOL0",
            "MP_REGEN0",
            "BOSS_ROCK_CANDY_P",
            "FIRST_CLEAR_DIAMONDS",
            "INBOX_EXPIRY_DAYS",
            "DAILY_GIFT_WEAPON_TICKETS",
            "LOGIN_DIAMONDS",
            "LIMIT_BREAK_RANKS",
            "REF_DAILIES_DONE",
            "REF_NEW_BOSSES",
            "CLOCK_BACKWARDS_TOLERANCE_S",
        };

        /// <summary>Required declared type per key ("int", "long", "double", "bool", "intlist").</summary>
        public static string RequiredType(string key)
        {
            switch (key)
            {
                case "TICK_HZ": return "int";
                case "BASE_ATK": return "double";
                case "BASE_HP": return "double";
                case "ATTACK_INTERVAL_TICKS": return "int";
                case "KILLS_PER_STAGE": return "int";
                case "SPAWN_DELAY_TICKS": return "int";
                case "BOSS_EVERY": return "int";
                case "BOSS_HP_MULT": return "double";
                case "BOSS_TIME_TICKS": return "int";
                case "BOSS_GOLD_MULT": return "double";
                case "CHAPTER_WIDTH": return "int";
                case "STAGE_CAP": return "int";
                case "TAP_ZAP_DMG_PCT": return "double";
                case "TAP_ZAP_MAX_PER_SEC": return "int";
                case "TRAIN_BULK_STEPS": return "intlist";
                case "DIAMONDS_PER_DAY_F2P": return "int";
                case "DAY_SECONDS": return "int";
                case "OFFLINE_RATE": return "double";
                case "OFFLINE_CAP_H": return "double";
                case "OFFLINE_MIN_S": return "int";
                case "PARTY_SLOTS": return "int";
                case "RUNE_SLOTS": return "int";
                case "DUPE_LVL_BONUS": return "double";
                case "FISH_BOARD_W": return "int";
                case "FISH_BOARD_H": return "int";
                case "FISH_MAX_LVL0": return "int";
                case "FISH_MERGE_COINS_PCT": return "double";
                case "FISH_BOARD_DPS_FACTOR": return "double";
                case "FISH_DISCARD_COINS_PCT": return "double";
                case "ATTACK_PROGRESS_UNIT": return "int";
                case "CRIT_DMG_BASE": return "double";
                case "MP_POOL0": return "double";
                case "MP_REGEN0": return "double";
                case "BOSS_ROCK_CANDY_P": return "double";
                case "FIRST_CLEAR_DIAMONDS": return "int";
                case "INBOX_EXPIRY_DAYS": return "int";
                case "DAILY_GIFT_WEAPON_TICKETS": return "int";
                case "LOGIN_DIAMONDS": return "int";
                case "LIMIT_BREAK_RANKS": return "int";
                case "REF_DAILIES_DONE": return "int";
                case "REF_NEW_BOSSES": return "int";
                case "CLOCK_BACKWARDS_TOLERANCE_S": return "int";
                default: return null;
            }
        }

        internal static Constants From(IReadOnlyDictionary<string, Value> values)
        {
            var c = new Constants();
            foreach (var kv in values)
            {
                var v = kv.Value;
                switch (kv.Key)
                {
                    case "TICK_HZ": c.TickHz = v.Int; break;
                    case "BASE_ATK": c.BaseAtk = v.Double; break;
                    case "BASE_HP": c.BaseHp = v.Double; break;
                    case "ATTACK_INTERVAL_TICKS": c.AttackIntervalTicks = v.Int; break;
                    case "KILLS_PER_STAGE": c.KillsPerStage = v.Int; break;
                    case "SPAWN_DELAY_TICKS": c.SpawnDelayTicks = v.Int; break;
                    case "BOSS_EVERY": c.BossEvery = v.Int; break;
                    case "BOSS_HP_MULT": c.BossHpMult = v.Double; break;
                    case "BOSS_TIME_TICKS": c.BossTimeTicks = v.Int; break;
                    case "BOSS_GOLD_MULT": c.BossGoldMult = v.Double; break;
                    case "CHAPTER_WIDTH": c.ChapterWidth = v.Int; break;
                    case "STAGE_CAP": c.StageCap = v.Int; break;
                    case "TAP_ZAP_DMG_PCT": c.TapZapDmgPct = v.Double; break;
                    case "TAP_ZAP_MAX_PER_SEC": c.TapZapMaxPerSec = v.Int; break;
                    case "TRAIN_BULK_STEPS": c.TrainBulkSteps = Array.AsReadOnly(v.IntList); break;
                    case "DIAMONDS_PER_DAY_F2P": c.DiamondsPerDayF2p = v.Int; break;
                    case "DAY_SECONDS": c.DaySeconds = v.Int; break;
                    case "OFFLINE_RATE": c.OfflineRate = v.Double; break;
                    case "OFFLINE_CAP_H": c.OfflineCapH = v.Double; break;
                    case "OFFLINE_MIN_S": c.OfflineMinS = v.Int; break;
                    case "PARTY_SLOTS": c.PartySlots = v.Int; break;
                    case "RUNE_SLOTS": c.RuneSlots = v.Int; break;
                    case "DUPE_LVL_BONUS": c.DupeLvlBonus = v.Double; break;
                    case "FISH_BOARD_W": c.FishBoardW = v.Int; break;
                    case "FISH_BOARD_H": c.FishBoardH = v.Int; break;
                    case "FISH_MAX_LVL0": c.FishMaxLvl0 = v.Int; break;
                    case "FISH_MERGE_COINS_PCT": c.FishMergeCoinsPct = v.Double; break;
                    case "FISH_BOARD_DPS_FACTOR": c.FishBoardDpsFactor = v.Double; break;
                    case "FISH_DISCARD_COINS_PCT": c.FishDiscardCoinsPct = v.Double; break;
                    case "ATTACK_PROGRESS_UNIT": c.AttackProgressUnit = v.Int; break;
                    case "CRIT_DMG_BASE": c.CritDmgBase = v.Double; break;
                    case "MP_POOL0": c.MpPool0 = v.Double; break;
                    case "MP_REGEN0": c.MpRegen0 = v.Double; break;
                    case "BOSS_ROCK_CANDY_P": c.BossRockCandyP = v.Double; break;
                    case "FIRST_CLEAR_DIAMONDS": c.FirstClearDiamonds = v.Int; break;
                    case "INBOX_EXPIRY_DAYS": c.InboxExpiryDays = v.Int; break;
                    case "DAILY_GIFT_WEAPON_TICKETS": c.DailyGiftWeaponTickets = v.Int; break;
                    case "LOGIN_DIAMONDS": c.LoginDiamonds = v.Int; break;
                    case "LIMIT_BREAK_RANKS": c.LimitBreakRanks = v.Int; break;
                    case "REF_DAILIES_DONE": c.RefDailiesDone = v.Int; break;
                    case "REF_NEW_BOSSES": c.RefNewBosses = v.Int; break;
                    case "CLOCK_BACKWARDS_TOLERANCE_S": c.ClockBackwardsToleranceS = v.Int; break;
                }
            }
            return c;
        }
    }
}
