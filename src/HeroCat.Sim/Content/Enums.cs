namespace HeroCat.Sim.Content
{
    // specs/09 section 3.3, mirrored by ENUMS in tools/export_content.py. Member order is load-bearing where the
    // spec says so (Rarity, Stat, Banner, Currency, Dungeon, Tier): reordering is a schema and save change (02 3.2).

    public enum Rarity { COMMON, RARE, EPIC, LEGENDARY, MYTHIC }

    public enum Stat { ATK, HP, DEF, CRIT_RATE, CRIT_DMG, ATK_SPD, GOLD_GAIN, EXP_GAIN }

    public enum Banner { WEAPON, SKILL, COMPANION, RUNE }

    /// <summary>06 section 1. TICKET_SUMMON_RUNE is appended last so earlier ordinals never move (issue #26).</summary>
    public enum Currency : byte
    {
        GOLD, DIAMOND, FISH_COIN, ROCK_CANDY, HERO_EXP,
        TICKET_FISH_COIN, TICKET_ROCK_CANDY, TICKET_EXP,
        TICKET_SUMMON_WEAPON, TICKET_SUMMON_SKILL, TICKET_SUMMON_COMPANION,
        GUILD_COIN,
        TICKET_SUMMON_RUNE,
    }

    public enum Dungeon { FISH_COIN, ROCK_CANDY, EXP }

    public enum CastType { INSTANT, DOT, BUFF }

    public enum AbilityType { DAMAGE, BUFF_ATK, BUFF_GOLD, MP_REGEN }

    public enum ResearchEffect { RESEARCH_PCT, GOLD_MULT, MP_POOL, MP_REGEN, TANK_CAP, TANK_REFILL, FISH_CAP, OFFLINE_CAP }

    public enum QuestKind { DAILY, WEEKLY, ACHIEVEMENT }

    public enum QuestGoal
    {
        LOGIN, LOGIN_DAYS, KILL_ENEMIES, ATTEMPT_BOSS, CLEAR_STAGES, SUMMON, RELEASE_FISH, MERGE_FISH, CLEAR_DUNGEON,
        TRAIN_LEVELS, DAILIES_COMPLETED, WEEKLIES_COMPLETED, REACH_STAGE, REACH_HERO_LEVEL, OWN_WEAPONS, OWN_SKILLS,
        OWN_COMPANIONS, LIFETIME_MERGES, FISH_LEVEL, AWAKEN_RANK, LIFETIME_SUMMONS, WATCH_AD,
    }

    public enum Element { NONE, FIRE, WATER, LEAF, LIGHT, DARK }

    public enum PackageLimit { ONCE, DAILY, WEEKLY, MONTHLY }

    public enum InboxKind { IAP, MAIL, COMPENSATION, COUPON, SYSTEM }

    public enum TutorialTrigger { PROFILE_CREATED, STAGE_REACHED, FIRST_BOSS_FAILED, SCREEN_OPENED, PREVIOUS_DONE }

    public enum FeatureCondition { ALWAYS, STAGE_HIGHEST, AWAKEN_RANK }

    /// <summary>A client built for tier T loads rows with tier &lt;= T (09 section 5.32).</summary>
    public enum Tier { M0, MVP, P1, P2 }
}
