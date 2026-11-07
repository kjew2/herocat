using HeroCat.Runtime.Boot;
using HeroCat.Sim.Content;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// Every localization key the M0 screens use (00 contract 10, 07 section 12: <c>scr.&lt;screen&gt;.&lt;element&gt;</c>).
    /// Lowercase only, so each matches 09's key regex. <c>{0}</c>..<c>{2}</c> are the label's arguments (already formatted
    /// numbers). The English rows live in <c>content_src/localization.csv</c>.
    /// </summary>
    public static class UiKeys
    {
        /// <summary>No key: the label's arguments are its text (a bare formatted number).</summary>
        public const string Raw = "";

        // shared
        public const string ComingSoon = "scr.common.coming_soon";
        public const string NeedMore = "scr.common.need_more";            // "Need {0} more"
        public const string Back = "scr.common.back";
        public const string Close = "scr.common.close";

        // SCR_BOOT
        public const string BootTitle = "scr.boot.title";
        public const string BootStepContent = "boot.step.content", BootStepConfig = "boot.step.config", BootStepAuth = "boot.step.auth",
                            BootStepSave = "boot.step.save", BootStepCatchup = "boot.step.catchup", BootStepWarmup = "boot.step.warmup";
        public const string BootCaption = "scr.boot.caption";             // "v{0} · content {1} · build {2}"

        public static string BootStepKey(BootStep s) => s switch
        {
            BootStep.Content => BootStepContent,
            BootStep.Config => BootStepConfig,
            BootStep.Auth => BootStepAuth,
            BootStep.Save => BootStepSave,
            BootStep.CatchUp => BootStepCatchup,
            _ => BootStepWarmup,
        };

        // SCR_MAIN
        public const string MainStage = "scr.main.stage";                 // "Stage {0} · Ch.{1}"
        public const string MainStageFarming = "scr.main.stage_farming";  // "Farming {0} · frontier {1}"
        public const string MainKills = "scr.main.kills";
        public const string MainWall = "scr.main.wall";
        public const string MainDps = "scr.main.dps";
        public const string MainBossNeed = "scr.main.boss_need";          // "Boss: need ~{0}x"
        public const string MainBossClearable = "scr.main.boss_clearable";
        public const string MainTapHint = "scr.main.tap_hint";
        public const string TabParty = "scr.main.tab_party", TabCollection = "scr.main.tab_collection", TabSummon = "scr.main.tab_summon",
                            TabDungeons = "scr.main.tab_dungeons", TabQuests = "scr.main.tab_quests", TabShop = "scr.main.tab_shop";

        public static string TabKey(MainTab t) => t switch
        {
            MainTab.Party => TabParty,
            MainTab.Collection => TabCollection,
            MainTab.Summon => TabSummon,
            MainTab.Dungeons => TabDungeons,
            MainTab.Quests => TabQuests,
            _ => TabShop,
        };

        // Fish Board
        public const string FishRelease = "scr.main.fish_release", FishReleaseMax = "scr.main.fish_release_max", FishMergeAll = "scr.main.fish_merge_all";
        public const string FishTankEmpty = "scr.main.fish_tank_empty", FishBoardFull = "scr.main.fish_board_full",
                            FishNothingToMerge = "scr.main.fish_nothing_to_merge", FishBoardLocked = "scr.main.fish_board_locked";
        public const string FishTank = "scr.main.fish_tank";
        public const string FishLevel = "scr.main.fish_level";            // "Fish level {0}"
        public const string FishInfoBody = "scr.main.fish_info_body";     // "Missile {0} of a hit · board DPS {1}"
        public const string FishDiscard = "scr.main.fish_discard";        // "Release back to sea (+{0} coins)"
        public const string FishMaxLevel = "scr.main.fish_max_level";
        public const string FishDiscovered = "scr.main.fish_discovered";  // "New fish! Level {0} (+{1})"

        // toasts
        public const string ToastBossFailed = "scr.main.toast_boss_failed";   // "Boss escaped: need ~{0}x DPS"
        public const string ToastFirstClear = "scr.main.toast_first_clear";   // "First clear: +{0} diamonds"
        public const string ToastStageCap = "scr.main.toast_stage_cap";
        public const string ToastBannerLevel = "scr.summon.toast_banner_level";   // "Banner level {0}: rates improved"
        public const string ToastNewDay = "scr.main.toast_new_day";
        public const string ToastLogin = "scr.main.toast_login";              // "+{0} diamonds: daily login"
        public const string ToastSaveFailed = "scr.main.toast_save_failed";

        // SCR_PARTY
        public const string PartyTitle = "scr.party.title";
        public const string PartyLevel = "scr.party.level";                 // "Lv {0}"
        public const string PartyTraining = "scr.party.training";
        public const string PartyStepN = "scr.party.step_n";                // "x{0}"
        public const string PartyStepMax = "scr.party.step_max";
        public const string PartyPlus = "scr.party.plus";
        public const string PartyMax = "scr.party.max";
        public const string PartyBuyLevels = "scr.party.buy_levels";        // "Buy {0} levels for {1}"
        public const string PartyLoadout = "scr.party.loadout";
        public const string PartyAwakenGate = "scr.party.awaken_gate";   // "Reach stage {0}"

        // collections
        public const string WeaponsTitle = "scr.weapons.title", SkillsTitle = "scr.skills.title";
        public const string CollEquip = "scr.collection.equip";
        public const string CollEquipped = "scr.collection.equipped";
        public const string CollNotOwned = "scr.collection.not_owned";
        public const string CollNothingEquipped = "scr.collection.nothing_equipped";
        public const string CollLevelCopies = "scr.collection.level_copies";   // "Lv {0} ({1}/{2})"
        public const string CollLevelMax = "scr.collection.level_max";         // "Lv {0} (max)"
        public const string CollPassive = "scr.weapons.passive";               // "Collection passive: {0} ATK ({1}/{2} owned)"
        public const string CollNew = "scr.collection.new_count";              // "({0} new)"
        public const string CollChipWeapons = "scr.collection.chip_weapons", CollChipSkills = "scr.collection.chip_skills",
                            CollChipCompanions = "scr.collection.chip_companions";
        public const string WeaponStats = "scr.weapons.stats";                 // "ATK {0} · passive {1}"
        public const string SkillStats = "scr.skills.stats";                   // "DPS {0} · CD {1} · {2}"
        public const string SkillsAutoEquip = "scr.skills.auto_equip";
        public const string SkillsSlots = "scr.skills.slots";
        public const string CompanionsTitle = "scr.companions.title";
        public const string CompanionsSlots = "scr.companions.slots";
        public const string CompanionsStats = "scr.companions.stats";         // "{0} · DPS {1} · every {2}"
        public const string CompanionsMp = "scr.companions.mp";               // "MP {0} / {1} · regen {2}/s"
        public const string CompanionsBudget = "scr.companions.budget";       // "Equipped cost {0} MP/s"
        public const string SortRarity = "scr.collection.sort_rarity", SortLevel = "scr.collection.sort_level", SortNewest = "scr.collection.sort_newest",
                            SortOwned = "scr.collection.sort_owned";
        public const string FilterAll = "scr.collection.filter_all", FilterOwned = "scr.collection.filter_owned";

        public static string SortKey(CollectionSort s) => s switch
        {
            CollectionSort.Level => SortLevel,
            CollectionSort.Newest => SortNewest,
            CollectionSort.OwnedFirst => SortOwned,
            _ => SortRarity,
        };

        public static string FilterKey(CollectionFilter f) => f switch
        {
            CollectionFilter.All => FilterAll,
            CollectionFilter.Owned => FilterOwned,
            _ => RarityName((Rarity)(f - CollectionFilter.Common)),
        };

        public static string CastTypeKey(CastType t) => "scr.skills.cast_" + t.ToString().ToLowerInvariant();

        public static string AbilityKey(AbilityType t) => "scr.companions.ability_" + t.ToString().ToLowerInvariant();

        /// <summary>09's rarity rows: <c>rarity.epic.name</c>.</summary>
        public static string RarityName(Rarity r) => TextFmt.RarityKey(r);

        public static string KindKey(Banner b) => "scr.item_detail.kind_" + b.ToString().ToLowerInvariant();

        public static string CurrencyKey(Currency c) => "currency." + c.ToString().ToLowerInvariant() + ".name";

        // SCR_ITEM_DETAIL
        public const string DetailLevel = "scr.item_detail.level";             // "Level {0}   {1} / {2} copies"
        public const string DetailWeaponAtk = "scr.item_detail.weapon_atk";    // "Equipped ATK {0}"
        public const string DetailNext = "scr.item_detail.next";               // "Next level: {0}"
        public const string DetailAtMax = "scr.item_detail.at_max";            // "Max level"
        public const string DetailPassive = "scr.item_detail.passive";         // "Collection passive {0}"
        public const string DetailSource = "scr.item_detail.source";           // "Source: {0} ({1} {2})"
        public const string DetailCompare = "scr.item_detail.compare";         // "Equipped weapon: {0}"
        public const string DetailCompareButton = "scr.item_detail.compare_button";
        public const string DetailNewRibbon = "scr.item_detail.new";
        public const string DetailLimitBreak = "scr.item_detail.limit_break";   // "Limit break ({0} Rock Candy)"
        public const string DetailLimitBreaks = "scr.item_detail.limit_breaks"; // "Limit breaks {0} / {1}"
        public const string RejectLimitBreakCapped = "reject.limit_break_capped";
        public const string RejectNotEnoughRockCandy = "reject.not_enough_rock_candy";

        // SCR_SUMMON / SCR_SUMMON_RESULT
        public const string SummonTitle = "scr.summon.title";
        public const string SummonBannerLevel = "scr.summon.banner_level";     // "Banner Lv {0}"
        public const string SummonPity = "scr.summon.pity";                    // "Pity {0} / {1}"
        public const string SummonSoftPity = "scr.summon.soft_pity";           // "soft pity from pull {0}"
        public const string SummonGuarantee = "scr.summon.guarantee";          // "{0}x: at least one {1} guaranteed"
        public const string SummonPull = "scr.summon.pull";                    // "Summon x{0}  {1}"
        public const string SummonTicket = "scr.summon.ticket";                // "Use ticket x1 ({0})"
        public const string SummonRates = "scr.summon.rates";
        public const string SummonAgain = "scr.summon_result.again";           // "Summon x{0} again  {1}"
        public const string SummonSkip = "scr.summon_result.skip";
        public const string SummonSummary = "scr.summon_result.summary";       // "{0} Epic+ · {1} Rare · {2} Common"
        public const string CardNew = "scr.summon_result.new";
        public const string CardLevelUp = "scr.summon_result.level_up";        // "Lv {0} -> {1}"
        public const string CardCopy = "scr.summon_result.copy";               // "+1 copy ({0}/{1})"

        // SCR_OFFLINE_REWARD
        public const string OfflineTitle = "scr.offline_reward.title";
        public const string OfflineAway = "scr.offline_reward.away";           // "Away for {0}"
        public const string OfflineRewards = "scr.offline_reward.rewards";     // "Rewards for {0}"
        public const string OfflineRewardsCapped = "scr.offline_reward.rewards_capped";   // "Rewards for {0} (capped at {1})"
        public const string OfflineTank = "scr.offline_reward.tank";           // "Magic Tank refilled (+{0} fish)"
        public const string OfflineStage = "scr.offline_reward.stage";         // "Farmed stage {0} at {1}/s"
        public const string OfflineClaim = "scr.offline_reward.claim";

        // SCR_CONFIRM / SCR_ERROR
        public const string ConfirmOk = "scr.confirm.ok", ConfirmCancel = "scr.confirm.cancel";
        public const string ConfirmCost = "scr.confirm.cost";                  // "{0}: {1}, balance after {2}"
        public const string ExitTitle = "scr.confirm.exit_title", ExitBody = "scr.confirm.exit_body";
        public const string ErrCodeLine = "scr.error.code_line";               // "{0} · support id {1}"

        public static string ErrorActionKey(ErrorAction a) => a switch
        {
            ErrorAction.Retry => "scr.error.retry",
            ErrorAction.UpdateApp => "scr.error.update_app",
            ErrorAction.ResetProfile => "scr.error.reset_profile",
            ErrorAction.RestoreCloud => "scr.error.restore_cloud",
            ErrorAction.ContactSupport => "scr.error.contact_support",
            _ => "scr.error.dismiss",
        };

        public const string ResetTitle = "scr.confirm.reset_title", ResetBody = "scr.confirm.reset_body";

        // SCR_QUESTS (07 section 4.15; issue #183)
        public const string QuestsTitle = "scr.quests.title";
        public const string QuestsTabDaily = "scr.quests.tab_daily", QuestsTabWeekly = "scr.quests.tab_weekly",
                            QuestsTabAchievements = "scr.quests.tab_achievements";
        public const string QuestsDailyProgress = "scr.quests.daily_progress";     // "Daily progress {0} / {1}"
        public const string QuestsWeeklyProgress = "scr.quests.weekly_progress";   // "Weekly progress {0} / {1}"
        public const string QuestsBonus = "scr.quests.bonus";                      // "{0} done: +{1}"
        public const string QuestsProgress = "scr.quests.progress";                // "{0} / {1}"
        public const string QuestsClaim = "scr.quests.claim", QuestsClaimed = "scr.quests.claimed", QuestsDone = "scr.quests.done",
                            QuestsGo = "scr.quests.go";
        public const string QuestsClaimAll = "scr.quests.claim_all";               // "Claim all ({0})"
        public const string QuestsRungs = "scr.quests.rungs";                      // "{0} / {1} rungs"
        public const string QuestsExpiresSoon = "scr.quests.expires_soon";
        public const string QuestsToastClaimed = "scr.quests.toast_claimed";       // "+{0} {1}"
        public const string QuestsToastReset = "scr.quests.toast_reset";

        public static string QuestsTabKey(QuestKind k) => k switch
        {
            QuestKind.DAILY => QuestsTabDaily,
            QuestKind.WEEKLY => QuestsTabWeekly,
            _ => QuestsTabAchievements,
        };

        // SCR_STAGE_SELECT (07 section 4.14; issue #183)
        public const string StageSelectTitle = "scr.stage_select.title";
        public const string StageSelectHighest = "scr.stage_select.highest";                   // "Highest cleared: {0}"
        public const string StageSelectFrontier = "scr.stage_select.frontier";                 // "Frontier: {0}"
        public const string StageSelectFrontierWalled = "scr.stage_select.frontier_walled";    // "Frontier: {0} (walled)"
        public const string StageSelectChapter = "scr.stage_select.chapter";                   // "Chapter {0} ({1}-{2})"
        public const string StageSelectFarming = "scr.stage_select.farming";                   // "Farming: {0}"
        public const string StageSelectReturn = "scr.stage_select.return";
        public const string StageSelectChallenge = "scr.stage_select.challenge";
        public const string StageSelectOfflineNote = "scr.stage_select.offline_note";
    }
}
