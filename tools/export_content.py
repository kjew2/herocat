#!/usr/bin/env python3
"""Content exporter: content_src/<table>.csv -> content/<table>.json + manifest.json (specs/09).

    python3 tools/export_content.py pull  --sheet <id> [--service-account creds.json | --csv-export] [--src content_src]
    python3 tools/export_content.py build [--src content_src] [--out content] [--dto-out PATH] [--previous DIR]
                                          [--ui-keys FILE] [--strict-warnings]
    python3 tools/export_content.py check [--src content_src] [--out content] [--dto-out PATH] [--previous DIR]
                                          [--ui-keys FILE] [--strict-warnings]
    python3 tools/export_content.py gen-dtos [--dto-out PATH]
    python3 tools/export_content.py diff --a <old content dir> --b <new content dir>
    python3 tools/export_content.py translator-csv --locale ja --out strings_ja.csv [--missing-only] [--src DIR]
    python3 tools/export_content.py import-translations --csv strings_ja.csv --locale ja [--src DIR]
    python3 tools/export_content.py pseudo --out content_pseudo [--locale xx] [--src DIR]

`build` parses, validates and emits every table plus the manifest (09 sections 6.1, 6.2) and regenerates the C#
DTO file (`HeroCat.Sim.Content.Dto`, 09 section 9.1) from SCHEMA, so the Python and C# shapes cannot drift.
`check` builds to memory and exits 1 if content/ or the DTO file differ byte for byte (CI's stale guard); without
--previous it reproduces the committed manifest's hot_apply instead of recomputing it.
`--previous` runs 6.3's version rules (E090-E093, W030, W031) and computes hot_apply from the changed-file set.
`diff` prints the row-level changes between two built content dirs for a PR description (7.1).
`pull` fetches every workbook tab named as a table (or `meta`) to content_src/<tab>.csv in one canonical CSV form, so a
pull of an unchanged sheet leaves no git diff (7.1, issue #132). `--service-account` uses Sheets API v4 batchGet and
needs the optional google-api-python-client (imported only by pull); `--csv-export` uses the gviz CSV URL.
`translator-csv` / `import-translations` are the translator round trip (13.2, E076); `pseudo` writes the normal build
plus localization.xx.json with every en string pseudo-localized (13.3) to a dir that is never the shipped content/.

Implemented: SCHEMA for every 09 section 5 table, column types (3.2), ENUMS (3.3), REQUIRED_CONSTANTS (5.1),
SCREEN_IDS (00 section 5.3), structural codes E001-E003, E010, E011, E020-E022, E070, E071, E094, constants
E040-E043, the table rules of 8.4 and warnings W010-W013 (RULES, issue #122), versioning E090-E093 / W030 / W031 /
hot_apply / diff (issue #123), localization E072-E075 / W020 / W021 (issue #128), pull (issue #132), translator-csv,
import-translations (E076) and pseudo (13.2, 13.3; issue #213). Fixtures for every code live in
tests/content_fixtures/ (tools/content_fixtures.py, issue #124).

Exit codes: 0 ok, 1 validation errors (all listed) or drift, 2 usage / IO / network / auth error. Python 3.9+, stdlib
only except `pull --service-account`.
"""
import argparse
import csv
import hashlib
import io
import json
import math
import os
import re
import sys
import urllib.parse
import urllib.request

SCHEMA_VERSION = 1
DTO_PATH = "src/HeroCat.Sim/Content/Dto/ContentDtos.g.cs"

# ------------------------------------------------------------------------------------------------ enums (3.3)
# Order is load-bearing where 09 section 3.3 says so; mirrors HeroCat.Sim.Content.Enums exactly.
ENUMS = {
    "Rarity": ["COMMON", "RARE", "EPIC", "LEGENDARY", "MYTHIC"],
    "Stat": ["ATK", "HP", "DEF", "CRIT_RATE", "CRIT_DMG", "ATK_SPD", "GOLD_GAIN", "EXP_GAIN"],
    "Banner": ["WEAPON", "SKILL", "COMPANION", "RUNE"],
    "Currency": ["GOLD", "DIAMOND", "FISH_COIN", "ROCK_CANDY", "HERO_EXP", "TICKET_FISH_COIN", "TICKET_ROCK_CANDY",
                 "TICKET_EXP", "TICKET_SUMMON_WEAPON", "TICKET_SUMMON_SKILL", "TICKET_SUMMON_COMPANION", "GUILD_COIN",
                 "TICKET_SUMMON_RUNE"],
    "Dungeon": ["FISH_COIN", "ROCK_CANDY", "EXP"],
    "CastType": ["INSTANT", "DOT", "BUFF"],
    "AbilityType": ["DAMAGE", "BUFF_ATK", "BUFF_GOLD", "MP_REGEN"],
    "ResearchEffect": ["RESEARCH_PCT", "GOLD_MULT", "MP_POOL", "MP_REGEN", "TANK_CAP", "TANK_REFILL", "FISH_CAP",
                       "OFFLINE_CAP"],
    "QuestKind": ["DAILY", "WEEKLY", "ACHIEVEMENT"],
    "QuestGoal": ["LOGIN", "LOGIN_DAYS", "KILL_ENEMIES", "ATTEMPT_BOSS", "CLEAR_STAGES", "SUMMON", "RELEASE_FISH",
                  "MERGE_FISH", "CLEAR_DUNGEON", "TRAIN_LEVELS", "DAILIES_COMPLETED", "WEEKLIES_COMPLETED",
                  "REACH_STAGE", "REACH_HERO_LEVEL", "OWN_WEAPONS", "OWN_SKILLS", "OWN_COMPANIONS", "LIFETIME_MERGES",
                  "FISH_LEVEL", "AWAKEN_RANK", "LIFETIME_SUMMONS", "WATCH_AD"],
    "Element": ["NONE", "FIRE", "WATER", "LEAF", "LIGHT", "DARK"],
    "PackageLimit": ["ONCE", "DAILY", "WEEKLY", "MONTHLY"],
    "InboxKind": ["IAP", "MAIL", "COMPENSATION", "COUPON", "SYSTEM"],
    "TutorialTrigger": ["PROFILE_CREATED", "STAGE_REACHED", "FIRST_BOSS_FAILED", "SCREEN_OPENED", "PREVIOUS_DONE"],
    "FeatureCondition": ["ALWAYS", "STAGE_HIGHEST", "AWAKEN_RANK"],
    "ConstantType": ["int", "long", "double", "bool", "intlist"],
    "Tier": ["M0", "MVP", "P1", "P2"],
    "Locale": ["en", "ja", "ko", "zh-Hans"],
}

# Item kinds a grantlist may name (`WEAPON:weapon_c_01:1`), with the table the id must exist in.
GRANT_ITEM_KINDS = {"WEAPON": "weapons", "SKILL": "skills", "COMPANION": "companions", "RUNE": "runes"}

# 00 section 5.3, the screen ids `screen`-typed columns are checked against (E021).
SCREEN_IDS = ["SCR_BOOT", "SCR_TITLE", "SCR_MAIN", "SCR_HERO", "SCR_WEAPONS", "SCR_SKILLS", "SCR_COMPANIONS",
              "SCR_RUNES", "SCR_RESEARCH", "SCR_SUMMON", "SCR_SUMMON_RESULT", "SCR_DUNGEONS", "SCR_DUNGEON_RUN",
              "SCR_STAGE_SELECT", "SCR_QUESTS", "SCR_SHOP", "SCR_INBOX", "SCR_OFFLINE_REWARD", "SCR_ITEM_DETAIL",
              "SCR_SETTINGS", "SCR_PROFILE", "SCR_GUILD", "SCR_RANKING", "SCR_EVENTS", "SCR_TUTORIAL", "SCR_CONFIRM",
              "SCR_ERROR"]

# 00 section 5.1's sim event vocabulary, the names `tutorial_steps.expect_event` may list (E021; issue #246 item 1).
SIM_EVENT_NAMES = [
    "EnemySpawned", "HitLanded", "EnemyKilled", "EnemyKilledBatch", "StageChanged", "StageCleared", "StageFirstCleared",
    "BossCleared", "ChapterEntered", "BossFailed", "StageCapReached", "SkillCast", "CompanionAbilityUsed",
    "FishMissileHit", "TapZapped", "StatTrained", "LevelUp", "Awakened", "ResearchLeveled",
    "ItemAcquired", "ItemEquipped", "ItemLimitBroken", "BannerLeveledUp",
    "FishReleased", "FishMerged", "FishMoved", "FishDiscarded", "FishDiscovered", "TankFishReady",
    "CurrencyChanged", "QuestCompleted", "QuestClaimed", "QuestsBulkClaimed", "DayRolledOver", "WeekRolledOver",
    "OfflineRewardComputed", "OfflineRewardClaimed", "DungeonStarted", "DungeonSwept", "DungeonCleared",
    "DungeonFloorFirstCleared", "DungeonFailed", "DungeonAbandoned", "ClockWentBackwards",
    "TutorialStepChanged",
]

# --------------------------------------------------------------------------------- required constants (5.1)
# key -> (type, kind). Mirrors HeroCat.Sim.Content.Constants (one get-only property per key, 09 section 9.2).
REQUIRED_CONSTANTS = {
    "TICK_HZ": ("int", "I"), "BASE_ATK": ("double", "I"), "BASE_HP": ("double", "S"),
    "ATTACK_INTERVAL_TICKS": ("int", "I"), "KILLS_PER_STAGE": ("int", "I"), "SPAWN_DELAY_TICKS": ("int", "I"),
    "BOSS_EVERY": ("int", "I"), "BOSS_HP_MULT": ("double", "I"), "BOSS_TIME_TICKS": ("int", "S"),
    "BOSS_GOLD_MULT": ("double", "I"), "CHAPTER_WIDTH": ("int", "S"), "STAGE_CAP": ("int", "I"),
    "TAP_ZAP_DMG_PCT": ("double", "S"), "TAP_ZAP_MAX_PER_SEC": ("int", "S"), "TRAIN_BULK_STEPS": ("intlist", "I"),
    "DIAMONDS_PER_DAY_F2P": ("int", "S"), "DAY_SECONDS": ("int", "I"), "OFFLINE_RATE": ("double", "I"),
    "OFFLINE_CAP_H": ("double", "S"), "OFFLINE_MIN_S": ("int", "S"), "SKILL_SLOTS": ("int", "I"),
    "COMPANION_SLOTS": ("int", "S"), "RUNE_SLOTS": ("int", "S"), "DUPE_LVL_BONUS": ("double", "I"),
    "FISH_BOARD_W": ("int", "S"), "FISH_BOARD_H": ("int", "S"), "FISH_MAX_LVL0": ("int", "S"),
    "FISH_MERGE_COINS_PCT": ("double", "S"), "FISH_BOARD_DPS_FACTOR": ("double", "S"),
    "FISH_DISCARD_COINS_PCT": ("double", "S"),
    "ATTACK_PROGRESS_UNIT": ("int", "I"), "CRIT_DMG_BASE": ("double", "S"), "MP_POOL0": ("double", "S"),
    "MP_REGEN0": ("double", "S"), "BOSS_ROCK_CANDY_P": ("double", "S"), "FIRST_CLEAR_DIAMONDS": ("int", "I"),
    "INBOX_EXPIRY_DAYS": ("int", "S"), "DAILY_GIFT_WEAPON_TICKETS": ("int", "S"), "LOGIN_DIAMONDS": ("int", "I"),
    "LIMIT_BREAK_RANKS": ("int", "S"), "REF_DAILIES_DONE": ("int", "S"), "REF_NEW_BOSSES": ("int", "S"),
    "CLOCK_BACKWARDS_TOLERANCE_S": ("int", "S"),
}

POSITIVE = ["TICK_HZ", "ATTACK_INTERVAL_TICKS", "BOSS_TIME_TICKS", "KILLS_PER_STAGE", "BOSS_EVERY", "CHAPTER_WIDTH",
            "STAGE_CAP", "TAP_ZAP_MAX_PER_SEC", "DAY_SECONDS", "OFFLINE_MIN_S", "SKILL_SLOTS", "COMPANION_SLOTS",
            "RUNE_SLOTS", "FISH_BOARD_W", "FISH_BOARD_H", "FISH_MAX_LVL0", "ATTACK_PROGRESS_UNIT",
            "LIMIT_BREAK_RANKS", "BASE_ATK", "BASE_HP", "BOSS_HP_MULT", "BOSS_GOLD_MULT", "OFFLINE_CAP_H",
            "DUPE_LVL_BONUS", "MP_POOL0", "MP_REGEN0"]
NON_NEGATIVE = ["SPAWN_DELAY_TICKS", "REF_DAILIES_DONE", "REF_NEW_BOSSES", "CLOCK_BACKWARDS_TOLERANCE_S"]
UNIT_INTERVAL = ["TAP_ZAP_DMG_PCT", "OFFLINE_RATE", "FISH_MERGE_COINS_PCT", "FISH_BOARD_DPS_FACTOR",
                 "BOSS_ROCK_CANDY_P", "FISH_DISCARD_COINS_PCT"]

HOT_APPLY_TABLES = ["localization", "shop_products", "shop_packages", "inbox_templates"]   # 00 contract 3


# ------------------------------------------------------------------------------------------------ schema (5)
def T(dto, columns, key, required=True, folds=None, todo=None):
    """columns: list of (name, type, nullable). key: column name or tuple (composite)."""
    tiered = any(c[0] == "tier" for c in columns)
    return {"dto": dto, "columns": columns, "key": key, "required": required, "folds": folds or {},
            "tiered": tiered, "todo": todo}


def c(name, typ, nullable=False):
    return (name, typ, nullable)


TIER = c("tier", "enum(Tier)")
NAME, DESC = c("name_key", "key"), c("desc_key", "key")

# Optional tables are not authored yet; each flips to required with its owning system issue (TODO numbers).
SCHEMA = {
    "constants": T("ConstantRowDto", [c("key", "string"), c("type", "enum(ConstantType)"), c("value", "constval"),
                                      c("kind", "string")], "key"),
    "curves": T("CurveRowDto", [c("id", "id"), c("base", "double"), c("growth", "double"), c("x_offset", "int")], "id"),
    "stats": T("StatRowDto", [c("id", "enum(Stat)"), c("trainable", "bool"), c("per_lvl", "double", True),
                              c("cap", "int", True), c("base", "double", True), TIER, NAME, DESC, c("icon", "asset")],
               "id"),
    "training_curves": T("TrainingCurveRowDto", [c("stat", "ref(stats)"), c("cost0", "double"), c("cost_g", "double")],
                         "stat"),
    "chapters": T("ChapterRowDto", [c("id", "id"), c("index", "int"), c("from_stage", "int"), NAME,
                                    c("background", "asset"), c("music", "asset"), TIER], "id"),
    "enemies": T("EnemyRowDto", [c("id", "id"), c("chapter", "ref(chapters)"), c("weight", "double"),
                                 c("sprite", "asset"), c("anim_set", "asset"), NAME], "id",
                 required=False, todo=113),
    "bosses": T("BossRowDto", [c("id", "id"), c("chapter", "ref(chapters)"), c("sprite", "asset"),
                               c("anim_set", "asset"), NAME, c("intro_vfx", "asset", True)], "id",
                required=False, todo=113),
    "rarities": T("RarityRowDto", [c("id", "enum(Rarity)"), c("code", "string"), c("color", "color"),
                                   c("shape_icon", "asset"), c("weapon_atk_pct", "double"),
                                   c("weapon_passive_pct", "double"), c("skill_dps_pct", "double"),
                                   c("companion_dps_pct", "double"), c("rune_stat_pct", "double"),
                                   c("limit_break_cost", "long"), c("dupe_overflow_rock_candy", "long"),
                                   c("override_tolerance", "double"), NAME], "id"),
    "weapons": T("WeaponRowDto", [c("id", "id"), c("rarity", "enum(Rarity)"), c("atk_pct", "double"),
                                  c("passive_pct", "double"), c("element", "enum(Element)"), TIER, NAME, DESC,
                                  c("icon", "asset"), c("sprite", "asset")], "id"),
    "skills": T("SkillRowDto", [c("id", "id"), c("rarity", "enum(Rarity)"), c("dps_pct", "double"),
                                c("cast_type", "enum(CastType)"), c("cooldown_ticks", "int"),
                                c("duration_ticks", "int", True), c("buff_pct", "double", True), TIER, NAME, DESC,
                                c("icon", "asset"), c("vfx", "asset")], "id"),
    "companions": T("CompanionRowDto", [c("id", "id"), c("rarity", "enum(Rarity)"), c("ability_type", "enum(AbilityType)"),
                                        c("dps_pct", "double"), c("mp_cost", "int"), c("cooldown_ticks", "int"),
                                        c("duration_ticks", "int", True), TIER, NAME, DESC, c("icon", "asset"),
                                        c("sprite", "asset"), c("vfx", "asset")], "id", required=False, todo=161),
    "runes": T("RuneRowDto", [c("id", "id"), c("rarity", "enum(Rarity)"), c("stat", "ref(stats)"),
                              c("stat_pct", "double"), c("slot_mask", "int"), TIER, NAME, DESC, c("icon", "asset")],
               "id", required=False),
    "banners": T("BannerRowDto", [c("id", "enum(Banner)"), c("enabled", "bool"), TIER, c("pull_cost_diamonds", "int"),
                                  c("ticket_currency", "enum(Currency)"), c("pool_c", "int"), c("pool_r", "int"),
                                  c("pool_e", "int"), c("pool_l", "int"), c("pool_m", "int"),
                                  c("pity_soft_start", "int"), c("pity_soft_step", "double"), c("pity_hard", "int"),
                                  c("m_share_of_lplus", "double"), c("guarantee_batch_size", "int"),
                                  c("guarantee_min_rarity", "enum(Rarity)"), c("exp_per_pull", "int"),
                                  c("sort_order", "int"), NAME, c("icon", "asset"), c("banner_art", "asset")], "id",
                 folds={"pool_size": (["pool_c", "pool_r", "pool_e", "pool_l", "pool_m"], "int")}),
    "banner_levels": T("BannerLevelRowDto", [c("banner", "ref(banners)"), c("level", "int"), c("exp_required", "int"),
                                             c("rate_c", "double"), c("rate_r", "double"), c("rate_e", "double"),
                                             c("rate_l", "double"), c("rate_m", "double")], ("banner", "level"),
                       folds={"rates": (["rate_c", "rate_r", "rate_e", "rate_l", "rate_m"], "double")}),
    "dupe_levels": T("DupeLevelRowDto", [c("level", "int"), c("cum_copies", "int")], "level"),
    "awakening": T("AwakeningRowDto", [c("rank", "int"), c("stage_gate", "int"), c("rock_candy_cost", "long"),
                                       c("mult", "double"), c("companion_slots_after", "int"),
                                       c("rune_slots_after", "int"), TIER, NAME, c("icon", "asset")], "rank"),
    "hero_levels": T("HeroLevelsRowDto", [c("id", "id"), c("exp0", "double"), c("exp_g", "double"),
                                          c("atk_pct_per_level", "double", True), c("mult_per_level", "double", True),
                                          c("level_cap", "int")], "id"),
    "research": T("ResearchRowDto", [c("id", "id"), c("effect", "enum(ResearchEffect)"),
                                     c("effect_per_level", "double"), c("effect_floor", "double", True),
                                     c("max_level", "int"), c("cost0", "double"), c("cost_g", "double"),
                                     c("unlock_awaken_rank", "int"), c("layout_row", "int"), c("layout_col", "int"),
                                     TIER, NAME, DESC, c("icon", "asset")], "id", required=False),
    "fish_levels": T("FishLevelRowDto", [c("level", "int"), c("discovery_coins", "long"), c("missile_pct", "double"),
                                         c("sprite", "asset"), c("tint", "color", True), NAME], "level"),
    "tank": T("TankRowDto", [c("id", "id"), c("cap0", "int"), c("refill_s0", "double"), c("release_level", "int"),
                             c("refill_floor_s", "double")], "id"),
    "quests_daily": T("QuestRowDto", [c("id", "id"), c("goal", "enum(QuestGoal)"), c("filter", "string", True),
                                      c("target", "long"), c("reward_currency", "enum(Currency)"),
                                      c("reward_amount", "long"), c("sort_order", "int"), TIER, NAME,
                                      c("icon", "asset")], "id"),
    "quests_weekly": T("QuestRowDto", [c("id", "id"), c("goal", "enum(QuestGoal)"), c("filter", "string", True),
                                       c("target", "long"), c("reward_currency", "enum(Currency)"),
                                       c("reward_amount", "long"), c("sort_order", "int"), TIER, NAME,
                                       c("icon", "asset")], "id"),
    "achievements": T("AchievementRowDto", [c("id", "id"), c("ladder", "id"), c("rung", "int"),
                                            c("goal", "enum(QuestGoal)"), c("target", "long"),
                                            c("reward_currency", "enum(Currency)"), c("reward_amount", "long"), TIER,
                                            NAME], "id", required=False, todo=171),
    "dungeons": T("DungeonRowDto", [c("id", "id"), c("kind", "enum(Dungeon)", True),
                                    c("ticket_currency", "enum(Currency)", True), c("tickets_per_day", "int"),
                                    c("floor_stage_step", "int"), c("floors", "int"), c("sweep_allowed", "bool"), TIER,
                                    NAME, DESC, c("icon", "asset"), c("background", "asset")], "id",
                  required=False, todo=173),
    "dungeon_floors": T("DungeonFloorRowDto", [c("dungeon", "ref(dungeons)"), c("floor", "int"),
                                               c("reward_currency", "enum(Currency)"), c("reward_amount", "long", True),
                                               c("reward_kills_equiv", "int", True), c("boss", "ref(bosses)", True),
                                               c("first_clear_bonus", "grantlist", True)], ("dungeon", "floor"),
                        required=False, todo=173),
    "shop_products": T("ShopProductRowDto", [c("sku", "id"), c("apple_product_id", "string"),
                                             c("google_product_id", "string"), c("usd_tier", "int"), c("usd", "double"),
                                             c("grant_currency", "enum(Currency)"), c("base_amount", "long"),
                                             c("bonus_pct", "double"), c("first_purchase_mult", "double"),
                                             c("sort_order", "int"), c("enabled", "bool"), TIER, NAME,
                                             c("icon", "asset")], "sku", required=False, todo=175),
    "shop_packages": T("ShopPackageRowDto", [c("id", "id"), c("apple_product_id", "string"),
                                             c("google_product_id", "string"), c("usd_tier", "int"), c("usd", "double"),
                                             c("grants", "grantlist"), c("limit", "enum(PackageLimit)"),
                                             c("limit_count", "int"), c("daily_drip", "grantlist", True),
                                             c("drip_days", "int", True), c("enabled", "bool"), TIER, NAME, DESC,
                                             c("icon", "asset")], "id", required=False, todo=175),
    "inbox_templates": T("InboxTemplateRowDto", [c("id", "id"), c("kind", "enum(InboxKind)"), c("title_key", "key"),
                                                 c("body_key", "key"), c("grants", "grantlist", True),
                                                 c("expiry_days", "int"), c("icon", "asset"), TIER], "id",
                         required=False, todo=176),
    "tutorial_steps": T("TutorialStepRowDto", [c("id", "id"), c("order", "int"), c("trigger", "enum(TutorialTrigger)"),
                                               c("trigger_value", "string", True), c("screen", "screen"),
                                               c("highlight", "string", True), c("text_key", "key"),
                                               c("expect_event", "string", True), c("expect_count", "int", True),
                                               c("grants", "grantlist", True), c("skippable", "bool"), TIER], "id"),
    "localization": T(None, [c("key", "key"), c("en", "string"), c("ja", "string", True), c("ko", "string", True),
                             c("zh_hans", "string", True), c("max_chars", "int"), c("plural", "bool")], "key"),
    "progress_targets": T("ProgressTargetRowDto", [c("day", "int"), c("stage", "int"), c("tolerance", "double")], "day"),
    "feature_unlocks": T("FeatureUnlockRowDto", [c("feature", "feature"), c("condition", "enum(FeatureCondition)"),
                                                 c("threshold", "int", True), c("screen", "screen"),
                                                 c("locked_text_key", "key", True), TIER], "feature"),
    "awakening_targets": T("AwakeningTargetRowDto", [c("rank", "int"), c("max_days_after_gate", "int")], "rank"),
    # Generated by the bots' stage-bound verb (09 section 5.36), not authored. TODO(#206).
    "plausibility_stage_bound": T("PlausibilityStageBoundRowDto", [c("day", "int"), c("stage", "int")], "day",
                                  required=False, todo=206),
}

# Tables whose absence still emits an empty file (09 section 5.12).
EMIT_WHEN_ABSENT = {"runes"}
META_REQUIRED = ["content_version", "min_client_version", "source_revision"]
META_KEYS = META_REQUIRED + ["ship_locales", "notes"]
LOCALE_COLUMNS = {"en": "en", "ja": "ja", "ko": "ko", "zh-Hans": "zh_hans"}   # enum form <-> CSV column (3.3)

ID_RE = re.compile(r"^[a-z][a-z0-9_]{0,63}$")
KEY_RE = re.compile(r"^[a-z0-9_]+(\.[a-z0-9_]+)+$")
FEATURE_RE = re.compile(r"^[A-Z][A-Z0-9_]*$")
CONST_KEY_RE = re.compile(r"^[A-Z][A-Z0-9_]*$")
COLOR_RE = re.compile(r"^#[0-9A-F]{6}$")
INT_RE = re.compile(r"^-?[0-9]+$")
DOUBLE_RE = re.compile(r"^-?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][-+]?[0-9]+)?$")
VERSION_RE = re.compile(r"^[0-9]{4}\.[0-9]{2}\.[0-9]{2}(\.[1-9][0-9]*)?$")
SEMVER_RE = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
INT32 = (-2 ** 31, 2 ** 31 - 1)
LONG53 = (-2 ** 53, 2 ** 53)


class Errors(list):
    """Error lines in 8.1's format; `warnings` holds the W0xx lines (7.1: they fail only with --strict-warnings)."""

    def __init__(self, *a):
        super().__init__(*a)
        self.warnings = Warnings()

    def add(self, code, loc, msg):
        self.append(f"CONTENT {code} {loc}: {msg}")


class Warnings(list):
    def add(self, code, loc, msg):
        self.append(f"CONTENT {code} {loc}: {msg}")


# ----------------------------------------------------------------------------------------------- cell parsing
def parse_int(text, lo_hi, typ, loc, errs):
    if not INT_RE.match(text):
        errs.add("E010", loc, f"expected {typ}, got '{text}'")
        return None
    v = int(text)
    if not lo_hi[0] <= v <= lo_hi[1]:
        errs.add("E010", loc, f"expected {typ}, got '{text}'")
        return None
    return v


def parse_double(text, loc, errs):
    if not DOUBLE_RE.match(text):
        errs.add("E010", loc, f"expected double, got '{text}'")
        return None
    return float(text)


def parse_grantlist(text, loc, errs):
    grants, seen = [], set()
    for part in text.split(";"):
        bits = part.split(":")
        if len(bits) == 2 and bits[0] in ENUMS["Currency"]:
            amt = parse_int(bits[1], LONG53, "long", loc, errs)
            if amt is None:
                return None
            if amt < 1 or bits[0] in seen:
                errs.add("E010", loc, f"expected grantlist, got '{text}'")
                return None
            seen.add(bits[0])
            grants.append({"currency": bits[0], "amount": amt})
        elif len(bits) == 3 and bits[0] in GRANT_ITEM_KINDS and ID_RE.match(bits[1]):
            amt = parse_int(bits[2], LONG53, "long", loc, errs)
            if amt is None or amt < 1:
                errs.add("E010", loc, f"expected grantlist, got '{text}'")
                return None
            grants.append({"kind": bits[0], "id": bits[1], "amount": amt})
        else:
            errs.add("E010", loc, f"expected grantlist, got '{text}'")
            return None
    return grants


def parse_cell(typ, text, loc, errs):
    """Returns the parsed value or None (error recorded). `text` is trimmed and non-empty."""
    if typ == "int":
        return parse_int(text, INT32, "int", loc, errs)
    if typ == "long":
        return parse_int(text, LONG53, "long", loc, errs)
    if typ == "double":
        return parse_double(text, loc, errs)
    if typ == "bool":
        if text not in ("true", "false"):
            errs.add("E010", loc, f"expected bool, got '{text}'")
            return None
        return text == "true"
    if typ == "string":
        return text
    if typ in ("id", "asset"):
        if not ID_RE.match(text):
            errs.add("E071", loc, f"'{text}' does not match {ID_RE.pattern}")
            return None
        return text
    if typ.startswith("ref("):
        if not ID_RE.match(text) and not CONST_KEY_RE.match(text):
            errs.add("E071", loc, f"'{text}' does not match {ID_RE.pattern}")
            return None
        return text   # resolved in resolve_refs (E022)
    if typ.startswith("enum("):
        name = typ[5:-1]
        if text not in ENUMS[name]:
            errs.add("E021", loc, f"'{text}' is not a valid {name} (allowed: {', '.join(ENUMS[name])})")
            return None
        return text
    if typ == "screen":
        if text not in SCREEN_IDS:
            errs.add("E021", loc, f"'{text}' is not a valid Screen (allowed: {', '.join(SCREEN_IDS)})")
            return None
        return text
    if typ == "feature":
        if not FEATURE_RE.match(text):
            errs.add("E071", loc, f"'{text}' does not match {FEATURE_RE.pattern}")
            return None
        return text
    if typ == "key":
        if not KEY_RE.match(text):
            errs.add("E071", loc, f"'{text}' does not match {KEY_RE.pattern}")
            return None
        return text
    if typ == "color":
        if not COLOR_RE.match(text):
            errs.add("E070", loc, f"'{text}' is not a #RRGGBB color")
            return None
        return text
    if typ == "intlist":
        out = []
        for part in text.split("|"):
            v = parse_int(part.strip(), INT32, "intlist", loc, errs)
            if v is None:
                return None
            out.append(v)
        return out
    if typ == "grantlist":
        return parse_grantlist(text, loc, errs)
    raise ValueError(f"unknown column type {typ}")


# ---------------------------------------------------------------------------------------------- read a table
def row_location(table, schema, raw, n):
    key = schema["key"]
    cols = key if isinstance(key, tuple) else (key,)
    vals = [raw.get(k, "").strip() for k in cols]
    if all(vals):
        return f"{table}[{','.join(vals)}]"
    return f"{table} row {n}"


def read_table(path, table, schema, errs):
    with open(path, newline="", encoding="utf-8-sig") as f:
        rows = list(csv.reader(f))
    if not rows:
        errs.add("E002", table, "missing required column '" + schema["columns"][0][0] + "'")
        return []
    header = [h.strip() for h in rows[0]]
    known = {cdef[0] for cdef in schema["columns"]}
    for h in header:
        if h and not h.startswith("#") and h not in known:
            errs.add("E003", table, f"unknown column '{h}' (remove it or add it to SCHEMA)")
    for cdef in schema["columns"]:
        if cdef[0] not in header:
            errs.add("E002", table, f"missing required column '{cdef[0]}'")
    idx = {h: i for i, h in enumerate(header) if h and not h.startswith("#")}
    out, first_seen = [], {}
    for n, cells in enumerate(rows[1:], start=1):
        if all(not x.strip() for x in cells):
            continue   # blank rows are skipped but counted, so `row n` matches the sheet
        raw = {h: (cells[i] if i < len(cells) else "") for h, i in idx.items()}
        loc_row = row_location(table, schema, raw, n)
        parsed, ok = {}, True
        for name, typ, nullable in schema["columns"]:
            if name not in idx:
                ok = False
                continue
            text = raw[name].strip()
            loc = f"{loc_row}.{name}"
            if text == "":
                if nullable:
                    parsed[name] = None
                    continue
                errs.add("E011", loc, "required cell is empty")
                ok = False
                continue
            if typ == "constval":
                parsed[name] = text   # typed in check_constants once the declared type is known
                continue
            v = parse_cell(typ, text, loc, errs)
            if v is None:
                ok = False
            parsed[name] = v
        parsed["_row"] = n
        parsed["_loc"] = loc_row
        if not ok:
            out.append(parsed)
            continue
        key = schema["key"]
        kv = tuple(parsed[k] for k in key) if isinstance(key, tuple) else parsed[key]
        if kv in first_seen:
            shown = ",".join(str(x) for x in kv) if isinstance(kv, tuple) else kv
            errs.add("E020", loc_row, f"duplicate key '{shown}' (first seen at row {first_seen[kv]})")
        else:
            first_seen[kv] = n
        out.append(parsed)
    return out


def read_meta(path, errs):
    meta = {}
    if not os.path.exists(path):
        errs.add("E001", "meta", "missing required table 'meta'")
        return meta
    with open(path, newline="", encoding="utf-8-sig") as f:
        rows = list(csv.reader(f))
    for row in rows[1:]:
        if len(row) >= 2 and row[0].strip() and not row[0].startswith("#"):
            k = row[0].strip()
            if k not in META_KEYS:
                errs.add("E003", "meta", f"unknown column '{k}' (remove it or add it to SCHEMA)")
            meta[k] = row[1].strip()
    for k in META_REQUIRED:
        if not meta.get(k):
            errs.add("E011", f"meta[{k}]", "required cell is empty")
    v = meta.get("content_version")
    if v and not VERSION_RE.match(v):
        errs.add("E094", "meta[content_version]", f"content_version '{v}' must be YYYY.MM.DD or YYYY.MM.DD.N")
    mv = meta.get("min_client_version")
    if mv and not SEMVER_RE.match(mv):
        errs.add("E010", "meta[min_client_version]", f"expected semver, got '{mv}'")
    for loc in (meta.get("ship_locales") or "en").split(","):
        if loc.strip() not in ENUMS["Locale"]:
            errs.add("E021", "meta[ship_locales]", f"'{loc.strip()}' is not a valid Locale (allowed: {', '.join(ENUMS['Locale'])})")
    return meta


# --------------------------------------------------------------------------------------------------- rules
def check_constants(tables, errs):
    rows = tables.get("constants", [])
    seen = set()
    for r in rows:
        k = r.get("key")
        if k is None:
            continue
        loc = r["_loc"]
        if not CONST_KEY_RE.match(k):
            errs.add("E071", f"{loc}.key", f"'{k}' does not match {CONST_KEY_RE.pattern}")
            continue
        seen.add(k)
        if k not in REQUIRED_CONSTANTS:
            errs.add("E042", loc, f"unknown constant '{k}' (add it to REQUIRED_CONSTANTS and ContentDb.Constants first)")
            continue
        req_type, _ = REQUIRED_CONSTANTS[k]
        declared = r.get("type")
        if declared is not None and declared != req_type:
            errs.add("E041", f"{loc}.type", f"declared type '{declared}' does not match required type '{req_type}'")
            continue
        raw = r.get("value")
        if raw is None or declared is None:
            continue
        typ = {"int": "int", "long": "long", "double": "double", "bool": "bool", "intlist": "intlist"}[declared]
        v = parse_cell(typ, raw, f"{loc}.value", errs)
        r["value"] = v
        if v is not None:
            check_constant_range(k, v, f"{loc}.value", errs)
    for k in REQUIRED_CONSTANTS:
        if k not in seen and rows:
            errs.add("E040", "constants", f"missing required constant '{k}'")
    vals = {r["key"]: r.get("value") for r in rows if r.get("key") in REQUIRED_CONSTANTS}
    w, h = vals.get("FISH_BOARD_W"), vals.get("FISH_BOARD_H")
    if isinstance(w, int) and isinstance(h, int) and w * h > 64:
        errs.add("E043", "constants[FISH_BOARD_W]", f"value {w} out of range (FISH_BOARD_W * FISH_BOARD_H <= 64)")


def check_constant_range(k, v, loc, errs):
    def bad(rule):
        errs.add("E043", loc, f"value {v!r} out of range ({rule})")

    if k in POSITIVE and not v > 0:
        bad("> 0")
    if k in NON_NEGATIVE and not v >= 0:
        bad(">= 0")
    if k in UNIT_INTERVAL and not 0 <= v <= 1:
        bad("in [0, 1]")
    if k == "CRIT_DMG_BASE" and not v >= 1:
        bad(">= 1")
    if k == "TRAIN_BULK_STEPS":
        if not v or v[0] != 1 or any(b <= a for a, b in zip(v, v[1:])):
            bad("strictly increasing with first element 1")


def resolve_refs(tables, errs):
    for table, schema in SCHEMA.items():
        for name, typ, _ in schema["columns"]:
            if typ.startswith("ref("):
                target = typ[4:-1]
                tkey = SCHEMA[target]["key"]
                have = {r.get(tkey) for r in tables.get(target, [])}
                for r in tables.get(table, []):
                    v = r.get(name)
                    if v is not None and v not in have:
                        errs.add("E022", f"{r['_loc']}.{name}", f"'{v}' does not exist in {target}")
            if typ == "grantlist":
                for r in tables.get(table, []):
                    for g in r.get(name) or []:
                        if "kind" in g:
                            target = GRANT_ITEM_KINDS[g["kind"]]
                            have = {x.get("id") for x in tables.get(target, [])}
                            if g["id"] not in have:
                                errs.add("E022", f"{r['_loc']}.{name}", f"'{g['id']}' does not exist in {target}")


def check_tutorial_events(tables, errs):
    """09 section 5.29: `expect_event` is a `|`-separated list of sim event names; any of them completes the step."""
    for r in tables.get("tutorial_steps", []):
        text = r.get("expect_event")
        if text is None:
            continue
        for name in text.split("|"):
            if name not in SIM_EVENT_NAMES:
                errs.add("E021", f"{r['_loc']}.expect_event", f"'{name}' is not a valid Event (allowed: {', '.join(SIM_EVENT_NAMES)})")


# ------------------------------------------------------------------------------- table rules (8.4), warnings (8.6)
# One small function per rule, appended to RULES in the order they run. validate() sorts their errors by table
# (TABLE_ORDER, the SCHEMA order) so a set of independent defects reports in table order (09 acceptance 7); within a
# table the rule order below holds. ContentDb.Build (src/HeroCat.Sim/Content/ContentDb.Rules.cs) runs the loader-scope
# rules in the same order with byte-identical messages (09 section 7.2, acceptance 20).
# Rules over a table that is not authored yet (optional and absent: companions, research, achievements,
# dungeons, enemies, bosses, shop) skip; each starts checking the day its table lands.

TABLE_ORDER = list(SCHEMA)
RATE_COLS = ["rate_c", "rate_r", "rate_e", "rate_l", "rate_m"]
POOL_COLS = ["pool_c", "pool_r", "pool_e", "pool_l", "pool_m"]
POOL_TABLES = {"WEAPON": "weapons", "SKILL": "skills", "COMPANION": "companions", "RUNE": "runes"}
UI_FISH_SPRITE_LEVELS = 12   # 07 section 13 (issue #48): distinct fish sprites in the MVP art set
CODE_BUILT_KEY_PREFIXES = ("err.", "reject.", "notice.")   # 13.1: built from wire codes, checked by 01 AC 14's test
# Keys the Unity client builds its screens from (07; client Core view-models). Judged only with --ui-keys.
CLIENT_UI_KEY_PREFIXES = ("ui.", "scr.", "boot.")
RULES = []


def rule(fn):
    RULES.append(fn)
    return fn


def fmt(v):
    """A value as 8.1 prints it: doubles by repr, bools lowercase, ints and strings as-is."""
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, float):
        return repr(v)
    if v is None:
        return "empty"
    return str(v)


def table_of(loc):
    return re.split(r"[\[. :]", loc, maxsplit=1)[0]


def tier_le(r, tier="MVP"):
    t = r.get("tier")
    return t is None or ENUMS["Tier"].index(t) <= ENUMS["Tier"].index(tier)


class Ctx:
    def __init__(self, tables, errs, warns, meta, ui_keys):
        self.tables, self.errs, self.warns, self.meta, self.ui_keys = tables, errs, warns, meta, ui_keys
        self.k = {r["key"]: r.get("value") for r in tables.get("constants", [])
                  if r.get("key") in REQUIRED_CONSTANTS and not isinstance(r.get("value"), str)}

    def rows(self, table):
        return self.tables.get(table, [])

    def has(self, table):
        return table in self.tables

    def err(self, code, loc, msg):
        self.errs.add(code, loc, msg)

    def warn(self, code, loc, msg):
        self.warns.add(code, loc, msg)


def groups(rows, col):
    """Rows grouped by `col`, groups in first-seen order, rows in row order."""
    out = {}
    for r in rows:
        out.setdefault(r[col], []).append(r)
    return out


def strictly_increasing(ctx, code, rows, col):
    for prev, r in zip(rows, rows[1:]):
        if r[col] is not None and prev[col] is not None and not r[col] > prev[col]:
            ctx.err(code, f"{r['_loc']}.{col}",
                    f"{col} must be strictly increasing; {r['_loc']} has {fmt(r[col])} <= previous {fmt(prev[col])}")


def non_decreasing(ctx, rows, col, order, decreasing=False):
    for prev, r in zip(rows, rows[1:]):
        if r[col] is None or prev[col] is None:
            continue
        if decreasing and r[col] > prev[col]:
            ctx.err("E048", f"{r['_loc']}.{col}",
                    f"{col} must be non-increasing in {order}; {r['_loc']} has {fmt(r[col])} > previous {fmt(prev[col])}")
        elif not decreasing and r[col] < prev[col]:
            ctx.err("E048", f"{r['_loc']}.{col}",
                    f"{col} must be non-decreasing in {order}; {r['_loc']} has {fmt(r[col])} < previous {fmt(prev[col])}")


def exactly_one_to_n(ctx, loc, label, values, n=None):
    n = len(values) if n is None else n
    if values != list(range(1, n + 1)):
        ctx.err("E060", loc, f"{label} must be exactly 1..{n} in row order, got {', '.join(fmt(v) for v in values)}")


def rng(ctx, table, col, test, rule_text, rows=None):
    for r in ctx.rows(table) if rows is None else rows:
        v = r.get(col)
        if v is not None and not test(v):
            ctx.err("E043", f"{r['_loc']}.{col}", f"value {fmt(v)} out of range ({rule_text})")


def cond(ctx, r, col, must_set, cond_col, cond_val):
    is_set = r.get(col) is not None
    if is_set != must_set:
        ctx.err("E054", f"{r['_loc']}.{col}",
                f"{col} must be {'set' if must_set else 'empty'} when {cond_col} = {fmt(cond_val)}")


@rule
def rule_e044_enum_tables_complete(ctx):
    for table, enum in (("stats", "Stat"), ("rarities", "Rarity"), ("banners", "Banner")):
        have = {r["id"] for r in ctx.rows(table)}
        for v in ENUMS[enum]:
            if v not in have:
                ctx.err("E044", table, f"missing row for {enum} value '{v}'")
    have = {r["id"] for r in ctx.rows("curves")}
    for cid in ("enemy_hp", "gold_per_kill", "exp_per_kill"):
        if ctx.has("curves") and cid not in have:
            ctx.err("E044", "curves", f"missing row for curve '{cid}'")


@rule
def rule_e045_one_row_tables(ctx):
    for table in ("hero_levels", "tank"):
        if ctx.has(table) and len(ctx.rows(table)) != 1:
            ctx.err("E045", table, f"exactly one row required, found {len(ctx.rows(table))}")


@rule
def rule_e060_index_columns(ctx):
    for table, enum in (("stats", "Stat"), ("rarities", "Rarity"), ("banners", "Banner")):
        ids = [r["id"] for r in ctx.rows(table)]
        if sorted(ids) == sorted(ENUMS[enum]) and ids != ENUMS[enum]:
            ctx.err("E060", table, f"id must be exactly {ENUMS[enum][0]}..{ENUMS[enum][-1]} in enum order, got {', '.join(ids)}")
    for table, col in (("chapters", "index"), ("dupe_levels", "level"), ("awakening", "rank"), ("fish_levels", "level"),
                       ("tutorial_steps", "order"), ("plausibility_stage_bound", "day")):
        if ctx.has(table):
            exactly_one_to_n(ctx, table, col, [r[col] for r in ctx.rows(table)])
    for banner, rows in groups(ctx.rows("banner_levels"), "banner").items():
        exactly_one_to_n(ctx, "banner_levels", f"level for banner {banner}", [r["level"] for r in rows])
    for ladder, rows in groups(ctx.rows("achievements"), "ladder").items():
        exactly_one_to_n(ctx, "achievements", f"rung for ladder {ladder}", [r["rung"] for r in rows])
    floors = {r["id"]: r["floors"] for r in ctx.rows("dungeons")}
    for dungeon, rows in groups(ctx.rows("dungeon_floors"), "dungeon").items():
        exactly_one_to_n(ctx, "dungeon_floors", f"floor for dungeon {dungeon}", [r["floor"] for r in rows],
                         floors.get(dungeon))
    if ctx.has("awakening_targets"):
        ranks = [r["rank"] for r in ctx.rows("awakening_targets")]
        exactly_one_to_n(ctx, "awakening_targets", "rank", ranks, len(ctx.rows("awakening")) if ctx.has("awakening") else None)


@rule
def rule_e043_table_ranges(ctx):
    pos, ge0, ge1, gt1 = (lambda v: v > 0), (lambda v: v >= 0), (lambda v: v >= 1), (lambda v: v > 1)
    unit = lambda v: 0 <= v <= 1  # noqa: E731
    rng(ctx, "curves", "base", pos, "> 0")
    rng(ctx, "curves", "growth", gt1, "> 1")
    rng(ctx, "stats", "per_lvl", pos, "> 0")
    rng(ctx, "stats", "cap", ge1, ">= 1")
    rng(ctx, "training_curves", "cost0", pos, "> 0")
    rng(ctx, "training_curves", "cost_g", gt1, "> 1")
    rng(ctx, "enemies", "weight", pos, "> 0")
    rng(ctx, "rarities", "limit_break_cost", ge1, ">= 1")
    rng(ctx, "rarities", "dupe_overflow_rock_candy", ge0, ">= 0")
    rng(ctx, "skills", "cooldown_ticks", lambda v: 30 <= v <= 300, "in [30, 300]")
    rng(ctx, "skills", "buff_pct", pos, "> 0")
    rng(ctx, "companions", "mp_cost", ge1, ">= 1")
    rng(ctx, "companions", "cooldown_ticks", lambda v: 20 <= v <= 600, "in [20, 600]")
    rng(ctx, "runes", "slot_mask", pos, "> 0")
    rng(ctx, "banners", "pull_cost_diamonds", pos, "> 0")
    rng(ctx, "banners", "pity_soft_start", ge1, ">= 1")
    rng(ctx, "banners", "pity_soft_step", pos, "> 0")
    rng(ctx, "banners", "m_share_of_lplus", unit, "in [0, 1]")
    rng(ctx, "banners", "guarantee_batch_size", lambda v: v >= 2, ">= 2")
    rng(ctx, "banners", "exp_per_pull", ge0, ">= 0")
    for col in RATE_COLS:
        rng(ctx, "banner_levels", col, unit, "in [0, 1]")
    rng(ctx, "banner_levels", "exp_required", lambda v: v == 0, "level 1 must be 0",
        rows=[r for r in ctx.rows("banner_levels") if r["level"] == 1])
    rng(ctx, "awakening", "mult", gt1, "> 1")
    rng(ctx, "awakening", "companion_slots_after", lambda v: v <= ctx.k.get("COMPANION_SLOTS", v), "<= COMPANION_SLOTS")
    rng(ctx, "awakening", "rune_slots_after", lambda v: v <= ctx.k.get("RUNE_SLOTS", v), "<= RUNE_SLOTS")
    rng(ctx, "hero_levels", "exp0", pos, "> 0")
    rng(ctx, "hero_levels", "exp_g", gt1, "> 1")
    rng(ctx, "hero_levels", "level_cap", ge1, ">= 1")
    rng(ctx, "research", "effect_per_level", pos, "> 0")
    rng(ctx, "research", "max_level", ge1, ">= 1")
    rng(ctx, "research", "cost0", pos, "> 0")
    rng(ctx, "research", "cost_g", gt1, "> 1")
    rng(ctx, "research", "unlock_awaken_rank", ge0, ">= 0")
    rng(ctx, "tank", "cap0", ge1, ">= 1")
    rng(ctx, "tank", "refill_s0", pos, "> 0")
    hz = ctx.k.get("TICK_HZ")
    if isinstance(hz, int):
        rng(ctx, "tank", "refill_s0", lambda v: v * hz == round(v * hz),
            "refill_s0 * TICK_HZ must be a whole number of ticks")
    for q in ("quests_daily", "quests_weekly"):
        rng(ctx, q, "target", ge1, ">= 1")
        for r in ctx.rows(q):
            rng(ctx, q, "reward_amount", ge0 if r["goal"] == "LOGIN" else ge1,
                ">= 0 for LOGIN" if r["goal"] == "LOGIN" else ">= 1", rows=[r])
    rng(ctx, "achievements", "reward_amount", ge1, ">= 1")
    rng(ctx, "dungeons", "tickets_per_day", ge0, ">= 0")
    rng(ctx, "dungeons", "floor_stage_step", ge1, ">= 1")
    rng(ctx, "shop_products", "usd_tier", ge1, ">= 1")
    rng(ctx, "shop_products", "base_amount", ge1, ">= 1")
    rng(ctx, "shop_products", "bonus_pct", unit, "in [0, 1]")
    rng(ctx, "shop_packages", "limit_count", ge1, ">= 1")
    rng(ctx, "inbox_templates", "expiry_days", ge1, ">= 1")
    rng(ctx, "tutorial_steps", "expect_count", ge1, ">= 1")
    rng(ctx, "localization", "max_chars", ge0, ">= 0")
    rng(ctx, "progress_targets", "stage", ge1, ">= 1")
    rng(ctx, "feature_unlocks", "threshold", ge1, ">= 1")
    rng(ctx, "awakening_targets", "max_days_after_gate", ge0, ">= 0")
    rng(ctx, "plausibility_stage_bound", "stage", ge1, ">= 1")


@rule
def rule_banner_levels(ctx):
    """E030 rates sum, E037 L/M constant, E031 exp_required, E048 rate monotonicity (09 section 5.14, acceptance 12)."""
    for banner, rows in groups(ctx.rows("banner_levels"), "banner").items():
        for r in rows:
            s = 0.0
            for col in RATE_COLS:
                s += r[col]
            if abs(s - 1.0) > 1e-9:
                ctx.err("E030", f"{r['_loc']}.rates", f"rates sum to {s!r} (abs error {abs(s - 1.0)!r}), expected 1 within 1e-9")
        base = rows[0]
        for r in rows[1:]:
            for col in ("rate_l", "rate_m"):
                if abs(r[col] - base[col]) > 1e-12:
                    ctx.err("E037", f"{r['_loc']}.{col}", f"{col} must not change with level; level {r['level']} has "
                            f"{fmt(r[col])}, level {base['level']} has {fmt(base[col])}")
        strictly_increasing(ctx, "E031", rows, "exp_required")
        non_decreasing(ctx, rows, "rate_c", "level", decreasing=True)
        non_decreasing(ctx, rows, "rate_r", "level")
        non_decreasing(ctx, rows, "rate_e", "level")


@rule
def rule_monotonic_columns(ctx):
    """E031 / E032 strictly increasing, E048 non-decreasing (09 sections 5.8-5.36)."""
    for col in ("weapon_atk_pct", "weapon_passive_pct", "skill_dps_pct", "companion_dps_pct", "rune_stat_pct",
                "limit_break_cost", "dupe_overflow_rock_candy"):
        non_decreasing(ctx, ctx.rows("rarities"), col, "rarity")
    strictly_increasing(ctx, "E031", ctx.rows("dupe_levels"), "cum_copies")
    strictly_increasing(ctx, "E032", ctx.rows("awakening"), "stage_gate")
    strictly_increasing(ctx, "E032", ctx.rows("awakening"), "rock_candy_cost")
    non_decreasing(ctx, ctx.rows("awakening"), "companion_slots_after", "rank")
    non_decreasing(ctx, ctx.rows("awakening"), "rune_slots_after", "rank")
    strictly_increasing(ctx, "E031", ctx.rows("fish_levels"), "discovery_coins")
    strictly_increasing(ctx, "E031", ctx.rows("fish_levels"), "missile_pct")
    for rows in groups(ctx.rows("achievements"), "ladder").values():
        strictly_increasing(ctx, "E031", rows, "target")
    for rows in groups(ctx.rows("dungeon_floors"), "dungeon").values():
        non_decreasing(ctx, rows, "reward_amount", "floor")
    shop = sorted(ctx.rows("shop_products"), key=lambda r: r["sort_order"])
    strictly_increasing(ctx, "E031", shop, "usd")
    non_decreasing(ctx, shop, "bonus_pct", "sort_order")
    strictly_increasing(ctx, "E032", ctx.rows("progress_targets"), "day")
    non_decreasing(ctx, ctx.rows("progress_targets"), "stage", "day")
    non_decreasing(ctx, ctx.rows("plausibility_stage_bound"), "stage", "day")


@rule
def rule_e034_counts(ctx):
    """Pool sizes for enabled banners (tier <= MVP), dungeon floors, awakening targets (09 sections 5.13, 5.24, 5.35)."""
    for b in ctx.rows("banners"):
        table = POOL_TABLES[b["id"]]
        if not b["enabled"] or not tier_le(b) or not ctx.has(table):
            continue   # TODO(#161): companions; a banner whose pool table is not authored yet is skipped
        rows = [r for r in ctx.rows(table) if tier_le(r)]
        for rarity, col in zip(ENUMS["Rarity"], POOL_COLS):
            n = sum(1 for r in rows if r["rarity"] == rarity)
            if n != b[col]:
                ctx.err("E034", table, f"{table} has {n} rows of {rarity}, expected {b[col]} from {b['_loc']}.{col}")
    floors = groups(ctx.rows("dungeon_floors"), "dungeon")
    for d in ctx.rows("dungeons"):
        n = len(floors.get(d["id"], []))
        if n != d["floors"]:
            ctx.err("E034", "dungeon_floors", f"dungeon_floors has {n} rows of {d['id']}, expected {d['floors']} from {d['_loc']}.floors")
    if ctx.has("awakening_targets") and ctx.has("awakening"):
        n, want = len(ctx.rows("awakening_targets")), len(ctx.rows("awakening"))
        if n != want:
            ctx.err("E034", "awakening_targets", f"awakening_targets has {n} rows of rank, expected {want} from awakening")


@rule
def rule_e035_override_tolerance(ctx):
    rar = {r["id"]: r for r in ctx.rows("rarities")}
    for table, col, default_col in (("weapons", "atk_pct", "weapon_atk_pct"), ("weapons", "passive_pct", "weapon_passive_pct"),
                                    ("skills", "dps_pct", "skill_dps_pct"), ("companions", "dps_pct", "companion_dps_pct"),
                                    ("runes", "stat_pct", "rune_stat_pct")):
        for r in ctx.rows(table):
            rd = rar.get(r["rarity"])
            if rd is None or not rd[default_col] > 0:
                continue
            d, tol = rd[default_col], rd["override_tolerance"]
            dev = abs(r[col] - d) / d
            if dev > tol + 1e-12:
                ctx.err("E035", f"{r['_loc']}.{col}", f"{col} {fmt(r[col])} deviates {fmt(dev * 100.0)}% from rarities "
                        f"default {fmt(d)} (tolerance {fmt(tol * 100.0)}%)")


@rule
def rule_e036_rune_banner(ctx):
    for b in ctx.rows("banners"):
        if b["id"] == "RUNE" and b["enabled"] and not ctx.rows("runes"):
            ctx.err("E036", f"{b['_loc']}.enabled", "banner RUNE is enabled but runes has no rows")


def fish_cap_levels(ctx):
    node = next((r for r in ctx.rows("research") if r["effect"] == "FISH_CAP"), None)
    extra = 0 if node is None else int(math.ceil(node["effect_per_level"] * node["max_level"]))
    lvl0 = ctx.k.get("FISH_MAX_LVL0")
    return None if not isinstance(lvl0, int) else lvl0 + extra


@rule
def rule_e038_fish_levels_cover_cap(ctx):
    needed = fish_cap_levels(ctx)
    n = len(ctx.rows("fish_levels"))
    if ctx.has("fish_levels") and needed is not None and n < needed:
        ctx.err("E038", "fish_levels", f"fish_levels has {n} rows but FISH_MAX_LVL0 + research cap needs {needed}")


def unique(ctx, rows, cols, label=None):
    seen = {}
    label = label or ",".join(cols)
    for r in rows:
        v = tuple(r[c] for c in cols)
        if any(x is None for x in v):
            continue
        if v in seen:
            ctx.err("E046", f"{r['_loc']}.{cols[0]}", f"duplicate {label} {','.join(fmt(x) for x in v)} (also at {seen[v]})")
        else:
            seen[v] = r["_loc"]


@rule
def rule_e046_unique_columns(ctx):
    unique(ctx, ctx.rows("banners"), ["sort_order"])
    unique(ctx, ctx.rows("research"), ["effect"])
    unique(ctx, ctx.rows("research"), ["layout_row", "layout_col"])
    for q in ("quests_daily", "quests_weekly"):
        unique(ctx, ctx.rows(q), ["sort_order"])
    unique(ctx, ctx.rows("dungeons"), ["kind"])
    unique(ctx, ctx.rows("shop_products"), ["sort_order"])
    both = ctx.rows("shop_products") + ctx.rows("shop_packages")
    unique(ctx, both, ["apple_product_id"])
    unique(ctx, both, ["google_product_id"])


@rule
def rule_banner_pity_and_guarantee(ctx):
    """E051 pity_hard > pity_soft_start; E052 a guarantee rarity with a level-1 rate (09 section 5.13)."""
    level1 = {r["banner"]: r for r in ctx.rows("banner_levels") if r["level"] == 1}
    for b in ctx.rows("banners"):
        if not b["pity_hard"] > b["pity_soft_start"]:
            ctx.err("E051", f"{b['_loc']}.pity_hard",
                    f"pity_hard ({b['pity_hard']}) must be greater than pity_soft_start ({b['pity_soft_start']})")
        lv = level1.get(b["id"])
        if lv is not None:
            i = ENUMS["Rarity"].index(b["guarantee_min_rarity"])
            s = 0.0
            for col in RATE_COLS[i:]:
                s += lv[col]
            if not s > 0:
                ctx.err("E052", f"{b['_loc']}.guarantee_min_rarity",
                        f"guarantee_min_rarity '{b['guarantee_min_rarity']}' has zero combined level-1 rate")


@rule
def rule_e054_conditional_columns(ctx):
    for r in ctx.rows("stats"):
        cond(ctx, r, "per_lvl", r["trainable"], "trainable", r["trainable"])
    for r in ctx.rows("skills"):
        cond(ctx, r, "duration_ticks", r["cast_type"] != "INSTANT", "cast_type", r["cast_type"])
        cond(ctx, r, "buff_pct", r["cast_type"] == "BUFF", "cast_type", r["cast_type"])
    for r in ctx.rows("companions"):
        cond(ctx, r, "duration_ticks", r["ability_type"].startswith("BUFF_"), "ability_type", r["ability_type"])
    for r in ctx.rows("hero_levels"):
        if (r["atk_pct_per_level"] is None) == (r["mult_per_level"] is None):
            cond(ctx, r, "mult_per_level", r["atk_pct_per_level"] is None, "atk_pct_per_level", r["atk_pct_per_level"])
    for r in ctx.rows("research"):
        if r["effect"] == "TANK_REFILL":
            cond(ctx, r, "effect_floor", True, "effect", r["effect"])
    for q in ("quests_daily", "quests_weekly"):
        for r in ctx.rows(q):
            # SUMMON and CLEAR_DUNGEON take an optional filter (empty = any banner / dungeon, 09 section 5.21)
            enum = {"SUMMON": "Banner", "CLEAR_DUNGEON": "Dungeon"}.get(r["goal"])
            if enum is None:
                cond(ctx, r, "filter", False, "goal", r["goal"])
            if enum and r["filter"] is not None and r["filter"] not in ENUMS[enum]:
                ctx.err("E021", f"{r['_loc']}.filter",
                        f"'{r['filter']}' is not a valid {enum} (allowed: {', '.join(ENUMS[enum])})")
    for r in ctx.rows("dungeons"):
        cond(ctx, r, "ticket_currency", r["kind"] is not None, "kind", r["kind"])
    for r in ctx.rows("dungeon_floors"):
        if (r["reward_amount"] is None) == (r["reward_kills_equiv"] is None):
            cond(ctx, r, "reward_kills_equiv", r["reward_amount"] is None, "reward_amount", r["reward_amount"])
    for r in ctx.rows("shop_packages"):
        cond(ctx, r, "drip_days", r["daily_drip"] is not None, "daily_drip", "set" if r["daily_drip"] else None)
    for r in ctx.rows("tutorial_steps"):
        cond(ctx, r, "trigger_value", r["trigger"] in ("STAGE_REACHED", "SCREEN_OPENED"), "trigger", r["trigger"])
        cond(ctx, r, "expect_count", r["expect_event"] is not None, "expect_event", r["expect_event"])
    for r in ctx.rows("feature_unlocks"):
        cond(ctx, r, "threshold", r["condition"] != "ALWAYS", "condition", r["condition"])
        cond(ctx, r, "locked_text_key", r["condition"] != "ALWAYS", "condition", r["condition"])


@rule
def rule_e055_training_curves(ctx):
    if not ctx.has("training_curves") or not ctx.has("stats"):
        return
    want = [r["id"] for r in ctx.rows("stats") if r["trainable"]]
    have = [r["stat"] for r in ctx.rows("training_curves")]
    missing = [s for s in want if s not in have]
    extra = [s for s in have if s not in want]
    if any(s not in ENUMS["Stat"] for s in extra):
        return   # an unresolved stat is E022's to report
    if missing or extra:
        ctx.err("E055", "training_curves", f"rows must be exactly the trainable stats; missing "
                f"{', '.join(missing) or 'none'}, extra {', '.join(extra) or 'none'}")


@rule
def rule_e056_derived_equalities(ctx):
    w = ctx.k.get("CHAPTER_WIDTH")
    if isinstance(w, int):
        for r in ctx.rows("chapters"):
            want = (r["index"] - 1) * w + 1
            if r["from_stage"] != want:
                ctx.err("E056", f"{r['_loc']}.from_stage",
                        f"from_stage must equal {want} ((index - 1) * CHAPTER_WIDTH + 1), got {r['from_stage']}")
    node = next((r for r in ctx.rows("research") if r["effect"] == "TANK_REFILL"), None)
    if node is not None and node["effect_floor"] is not None:
        for r in ctx.rows("tank"):
            if r["refill_floor_s"] != node["effect_floor"]:
                ctx.err("E056", f"{r['_loc']}.refill_floor_s", f"refill_floor_s must equal {fmt(node['effect_floor'])} "
                        f"({node['_loc']}.effect_floor), got {fmt(r['refill_floor_s'])}")
    for ladder, rows in groups(ctx.rows("achievements"), "ladder").items():
        for r in rows[1:]:
            if r["goal"] != rows[0]["goal"]:
                ctx.err("E056", f"{r['_loc']}.goal",
                        f"goal must equal {rows[0]['goal']} (same goal for every rung of ladder {ladder}), got {r['goal']}")


@rule
def rule_e057_skins_per_chapter(ctx):
    for table, word, ok, rule_text in (("enemies", "enemies", lambda n: n >= 1, "at least 1"),
                                       ("bosses", "bosses", lambda n: n == 1, "exactly 1")):
        if not ctx.has(table):
            continue   # TODO(#113): enemies and bosses are cosmetic tables not authored yet
        counts = {}
        for r in ctx.rows(table):
            counts[r["chapter"]] = counts.get(r["chapter"], 0) + 1
        for ch in ctx.rows("chapters"):
            n = counts.get(ch["id"], 0)
            if not ok(n):
                ctx.err("E057", table, f"chapter '{ch['id']}' has {n} {word}, expected {rule_text}")


@rule
def rule_e058_duration_within_cooldown(ctx):
    for table in ("skills", "companions"):
        for r in ctx.rows(table):
            d, c = r["duration_ticks"], r["cooldown_ticks"]
            if d is not None and not 1 <= d <= c:
                ctx.err("E058", f"{r['_loc']}.duration_ticks", f"duration_ticks {d} must be in 1..cooldown_ticks {c}")


@rule
def rule_e059_currency_allowlists(ctx):
    for b in ctx.rows("banners"):
        want = "TICKET_SUMMON_" + b["id"]
        if b["ticket_currency"] != want:
            ctx.err("E059", f"{b['_loc']}.ticket_currency",
                    f"ticket_currency '{b['ticket_currency']}' is not an allowed currency here ({want} for banner {b['id']})")
    for d in ctx.rows("dungeons"):
        if d["kind"] is not None and d["ticket_currency"] is not None and d["ticket_currency"] != "TICKET_" + d["kind"]:
            ctx.err("E059", f"{d['_loc']}.ticket_currency", f"ticket_currency '{d['ticket_currency']}' is not an allowed "
                    f"currency here (TICKET_{d['kind']} for kind {d['kind']})")
    for p in ctx.rows("shop_products"):
        if p["grant_currency"] != "DIAMOND":
            ctx.err("E059", f"{p['_loc']}.grant_currency",
                    f"grant_currency '{p['grant_currency']}' is not an allowed currency here (DIAMOND only)")


@rule
def rule_e061_dupe_level_one(ctx):
    rows = ctx.rows("dupe_levels")
    if rows and rows[0]["level"] == 1 and rows[0]["cum_copies"] != 1:
        ctx.err("E061", f"{rows[0]['_loc']}.cum_copies", f"cum_copies for level 1 must be 1, got {rows[0]['cum_copies']}")


@rule
def rule_e062_profile_created_step(ctx):
    if not ctx.has("tutorial_steps"):
        return
    pc = [r for r in ctx.rows("tutorial_steps") if r["trigger"] == "PROFILE_CREATED"]
    if len(pc) != 1 or pc[0]["order"] != 1:
        ctx.err("E062", "tutorial_steps", "exactly one PROFILE_CREATED step at order 1 required")


@rule
def rule_e063_feature_gates(ctx):
    unlocks = {r["feature"]: r for r in ctx.rows("feature_unlocks")}
    gate = unlocks.get("RESEARCH")
    if gate is not None and gate["condition"] == "AWAKEN_RANK" and gate["threshold"] is not None:
        for r in ctx.rows("research"):
            if r["unlock_awaken_rank"] < gate["threshold"]:
                ctx.err("E063", f"{r['_loc']}.unlock_awaken_rank", f"research[{r['id']}] unlocks at "
                        f"{r['unlock_awaken_rank']}, before feature 'RESEARCH' ({gate['threshold']})")
    shop = unlocks.get("SHOP")
    if shop is not None and shop["condition"] != "ALWAYS":
        ctx.err("E063", f"{shop['_loc']}.condition", "feature 'SHOP' must be ALWAYS")


@rule
def rule_e064_daily_bonus_rows(ctx):
    if not ctx.has("quests_daily"):
        return
    rows = [r for r in ctx.rows("quests_daily") if tier_le(r)]
    bonus = [r for r in rows if r["goal"] == "DAILIES_COMPLETED"]
    if not bonus:
        ctx.err("E064", "quests_daily", "missing or unreachable daily bonus rows (no DAILIES_COMPLETED row with tier <= MVP)")
    for r in bonus:
        if r["target"] > len(rows):
            ctx.err("E064", f"{r['_loc']}.target", f"missing or unreachable daily bonus rows ({r['id']} target "
                    f"{r['target']} exceeds the {len(rows)} daily rows)")


@rule
def rule_e065_owned_counts_below_pool(ctx):
    for r in ctx.rows("achievements"):   # TODO(#171): achievements are not authored yet
        table = {"OWN_WEAPONS": "weapons", "OWN_SKILLS": "skills", "OWN_COMPANIONS": "companions"}.get(r["goal"])
        if table is None or not ctx.has(table):
            continue
        pool = sum(1 for x in ctx.rows(table) if tier_le(x))
        if not r["target"] < pool:
            ctx.err("E065", f"{r['_loc']}.target", f"{r['goal']} target {r['target']} must be below the pool size {pool} (pillar 4)")


@rule
def rule_e039_reference_day_diamonds(ctx):
    """06 section 2's reference day recomputed from the quest rows (09 section 5.22, issues #55, #244)."""
    if not ctx.has("quests_daily"):
        return
    k = ctx.k
    need = ("LOGIN_DIAMONDS", "REF_DAILIES_DONE", "REF_NEW_BOSSES", "FIRST_CLEAR_DIAMONDS", "DIAMONDS_PER_DAY_F2P")
    if not all(isinstance(k.get(x), int) for x in need):
        return
    daily = [r for r in ctx.rows("quests_daily") if r["reward_currency"] == "DIAMOND" and tier_le(r)]
    weekly = [r for r in ctx.rows("quests_weekly") if r["reward_currency"] == "DIAMOND" and tier_le(r)]
    paying = [r["reward_amount"] for r in daily if r["reward_amount"] > 0 and r["goal"] != "DAILIES_COMPLETED"]
    total = float(k["LOGIN_DIAMONDS"])
    if paying:
        total += k["REF_DAILIES_DONE"] * (sum(paying) / len(paying))
    total += sum(r["reward_amount"] for r in daily if r["goal"] == "DAILIES_COMPLETED" and r["target"] <= k["REF_DAILIES_DONE"])
    total += sum(r["reward_amount"] for r in weekly) / 7.0
    total += k["FIRST_CLEAR_DIAMONDS"] * k["REF_NEW_BOSSES"]
    target = k["DIAMONDS_PER_DAY_F2P"]
    if abs(total - target) > 0.1 * target:
        ctx.err("E039", "quests_daily", f"reference-day diamond sum {fmt(total)} is outside 10% of DIAMONDS_PER_DAY_F2P {target}")


# ----------------------------------------------------------------------------------------- localization (13)
def icu_parse(text):
    """Parses the ICU MessageFormat subset 13.4 ships (literal text, {arg}, {arg, number}, {arg, plural|select, ...}).
    Returns a list of parts: str, or (name, kind, {selector: parts}). Raises ValueError with a detail."""
    pos = 0

    def message(depth):
        nonlocal pos
        parts, buf = [], []
        while pos < len(text):
            ch = text[pos]
            if ch == "'":
                if text.startswith("''", pos):
                    buf.append("'")
                    pos += 2
                    continue
                if pos + 1 < len(text) and text[pos + 1] in "{}#":
                    end = text.find("'", pos + 1)
                    if end < 0:
                        raise ValueError(f"unterminated quote at {pos}")
                    buf.append(text[pos + 1:end])
                    pos = end + 1
                    continue
                buf.append(ch)
                pos += 1
            elif ch == "{":
                if buf:
                    parts.append("".join(buf))
                    buf = []
                parts.append(argument())
            elif ch == "}":
                if depth == 0:
                    raise ValueError(f"unmatched '}}' at {pos}")
                break
            else:
                buf.append(ch)
                pos += 1
        if buf:
            parts.append("".join(buf))
        return parts

    def word():
        nonlocal pos
        while pos < len(text) and text[pos].isspace():
            pos += 1
        start = pos
        while pos < len(text) and text[pos] not in ",{} \t\n":
            pos += 1
        w = text[start:pos]
        while pos < len(text) and text[pos].isspace():
            pos += 1
        return w

    def argument():
        nonlocal pos
        pos += 1   # {
        name = word()
        if not name:
            raise ValueError(f"empty argument at {pos}")
        if pos < len(text) and text[pos] == "}":
            pos += 1
            return (name, "simple", {})
        if pos >= len(text) or text[pos] != ",":
            raise ValueError(f"expected ',' or '}}' after '{name}'")
        pos += 1
        kind = word()
        if kind == "number":
            if pos < len(text) and text[pos] == ",":
                pos += 1
                word()
            if pos >= len(text) or text[pos] != "}":
                raise ValueError(f"unterminated number argument '{name}'")
            pos += 1
            return (name, "number", {})
        if kind not in ("plural", "select"):
            raise ValueError(f"unsupported argument type '{kind}'")
        if pos >= len(text) or text[pos] != ",":
            raise ValueError(f"expected ',' after '{kind}'")
        pos += 1
        branches = {}
        while True:
            sel = word()
            if sel.startswith("offset:"):
                sel = word()
            if not sel:
                break
            if pos >= len(text) or text[pos] != "{":
                raise ValueError(f"branch '{sel}' has no message")
            pos += 1
            branches[sel] = message(1)
            if pos >= len(text) or text[pos] != "}":
                raise ValueError(f"unterminated branch '{sel}'")
            pos += 1
        if pos >= len(text) or text[pos] != "}":
            raise ValueError(f"unterminated {kind} argument '{name}'")
        pos += 1
        return (name, kind, branches)

    return message(0)


def plural_args(parts):
    out = []
    for p in parts:
        if isinstance(p, tuple):
            if p[1] == "plural":
                out.append(p)
            for b in p[2].values():
                out += plural_args(b)
    return out


def rendered_len(parts):
    """Longest rendering, counting a `#` or an argument as one character (E074's measure)."""
    n = 0
    for p in parts:
        if isinstance(p, str):
            n += len(p)
        elif p[1] in ("plural", "select") and p[2]:
            n += max(rendered_len(b) for b in p[2].values())
        else:
            n += 1
    return n


# CLDR plural categories the shipped locales need (13.2): en one + other; ja, ko, zh-Hans other only.
PLURAL_CATEGORIES = {"en": ["one", "other"], "ja": ["other"], "ko": ["other"], "zh-Hans": ["other"]}


def key_cells(tables):
    """(loc, key) for every `key`-typed cell outside localization."""
    for table, schema in SCHEMA.items():
        if table == "localization":
            continue
        for name, typ, _ in schema["columns"]:
            if typ == "key":
                for r in tables.get(table, []):
                    if r.get(name) is not None:
                        yield f"{r['_loc']}.{name}", r[name]


def text_errors(r, locale, text):
    """[(code, message)] for one locale's text of localization row r: E075 (the ICU plural branches the locale needs,
    13.2) or else E074 (length budget). Shared by rule_localization and import-translations.
    TODO(#260): nothing checks that the text keeps the en message's ICU arguments."""
    try:
        parts = icu_parse(text)
    except ValueError as e:
        parts = None
        if r["plural"]:
            return [("E075", f"plural message must have 'one' and 'other' branches: {locale} {e}")]
    if r["plural"]:
        plurals = plural_args(parts)
        missing = [c for c in PLURAL_CATEGORIES[locale] if not plurals or any(c not in p[2] for p in plurals)]
        if missing:
            detail = f"{locale} has no plural argument" if not plurals else f"{locale} lacks {', '.join(missing)}"
            return [("E075", f"plural message must have 'one' and 'other' branches: {detail}")]
    n = rendered_len(parts) if parts is not None else len(text)
    if r["max_chars"] and n > r["max_chars"]:
        return [("E074", f"{locale} text is {n} chars, budget {r['max_chars']}")]
    return []


@rule
def rule_localization(ctx):
    """E072-E075 and W020-W021 (09 sections 5.30, 8.2, 8.6, 13.1; acceptance 14)."""
    if not ctx.has("localization"):
        return
    loc_rows = ctx.rows("localization")
    have = {r["key"] for r in loc_rows}
    referenced = set()
    for loc, key in key_cells(ctx.tables):
        referenced.add(key)
        if key not in have:
            ctx.err("E072", loc, f"key '{key}' has no row in localization")
    for key in ctx.ui_keys or []:
        referenced.add(key)
        if key not in have:
            ctx.err("E072", "ui_keys", f"key '{key}' has no row in localization")
    ship = [x.strip() for x in (ctx.meta.get("ship_locales") or "en").split(",")]
    for locale, col in LOCALE_COLUMNS.items():
        if locale != "en" and locale in ship:
            for r in loc_rows:
                if r.get(col) is None:
                    ctx.err("E073", f"{r['_loc']}.{col}", f"key '{r['key']}' has no {locale} text and {locale} is in ship_locales")
    for r in loc_rows:
        for locale, col in LOCALE_COLUMNS.items():
            text = r.get(col)
            if text is None:
                continue
            for code, msg in text_errors(r, locale, text):
                ctx.err(code, f"{r['_loc']}.{col}", msg)
    for locale, col in LOCALE_COLUMNS.items():
        if locale == "en" or locale in ship:
            continue
        empty = [r for r in loc_rows if r.get(col) is None]
        if len(empty) == len(loc_rows) and loc_rows:
            ctx.warn("W020", "localization", f"{locale} has no text; all {len(loc_rows)} keys fall back to en")
        else:
            for r in empty:
                ctx.warn("W020", f"{r['_loc']}.{col}", f"key '{r['key']}' has no {locale} text (falls back to en)")
    for r in loc_rows:
        key = r["key"]
        if key in referenced or key.startswith(CODE_BUILT_KEY_PREFIXES):
            continue
        if key.startswith(CLIENT_UI_KEY_PREFIXES) and ctx.ui_keys is None:
            continue   # the client's ui_keys.txt (07 section 12) was not given; client UI rows cannot be judged
        ctx.warn("W021", r["_loc"], f"key '{key}' is not referenced by any table or by --ui-keys")


@rule
def rule_warnings(ctx):
    """W010-W013 (09 section 8.6)."""
    counts = {}
    for r in ctx.rows("skills"):
        if r["cast_type"] == "BUFF":
            counts[r["rarity"]] = counts.get(r["rarity"], 0) + 1
    for rarity in ENUMS["Rarity"]:
        if counts.get(rarity, 0) > 2:
            ctx.warn("W010", "skills", f"{counts[rarity]} BUFF skills share rarity {rarity} (more than two; buffs do not stack)")
    if ctx.has("companions"):
        for rarity in ("COMMON", "RARE"):
            if not any(r["rarity"] == rarity and r["ability_type"] == "MP_REGEN" for r in ctx.rows("companions")):
                ctx.warn("W011", "companions", f"rarity {rarity} has no MP_REGEN companion (auto-equip's MP reserve needs one)")
    enabled = [b["id"] for b in ctx.rows("banners") if b["enabled"]]
    levels = groups(ctx.rows("banner_levels"), "banner")
    n = {b: len(levels.get(b, [])) for b in enabled}
    if len(set(n.values())) > 1:
        ctx.warn("W012", "banner_levels", "enabled banners have different level counts: " +
                 ", ".join(f"{b} {c}" for b, c in n.items()))
    reach = fish_cap_levels(ctx)
    if reach is not None:
        seen = set()
        for r in ctx.rows("fish_levels"):
            reused = r["sprite"] in seen
            seen.add(r["sprite"])
            if r["level"] <= reach and r["tint"] is None and (reused or r["level"] > UI_FISH_SPRITE_LEVELS):
                why = f"reuses sprite '{r['sprite']}'" if reused else f"is above UI_FISH_SPRITE_LEVELS {UI_FISH_SPRITE_LEVELS}"
                ctx.warn("W013", f"{r['_loc']}.tint", f"reachable fish level {r['level']} {why} and has no tint")


def validate(tables, errs, meta=None, ui_keys=None):
    found = Errors()
    check_constants(tables, found)
    resolve_refs(tables, found)
    check_tutorial_events(tables, found)
    ctx = Ctx(tables, found, errs.warnings, meta or {}, ui_keys)
    for fn in RULES:
        fn(ctx)
    order = {t: i for i, t in enumerate(TABLE_ORDER)}
    errs.extend(sorted(found, key=lambda e: order.get(table_of(e.split(" ", 2)[2]), len(order))))


# ----------------------------------------------------------------------------------------------------- emit
def shape_row(r, schema):
    out = {}
    folded = {}
    for fname, (cols, _) in schema["folds"].items():
        for col in cols:
            folded[col] = fname
    for name, _, _ in schema["columns"]:
        if name in folded:
            fname = folded[name]
            if fname in out:
                continue
            cols, _ = schema["folds"][fname]
            out[fname] = {ENUMS["Rarity"][i]: r[col] for i, col in enumerate(cols)}
            continue
        out[name] = r[name]
    return out


def dumps(doc):
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


def build(src, previous=None, ui_keys=None, emit_invalid=False, hot_apply_default=False):
    """Parse, validate, emit. Returns ((files, tables, manifest) or None, errs); errs.warnings holds the W0xx lines.
    previous: a loaded previous build (load_previous) for 6.3's version rules and hot_apply. emit_invalid (fixtures
    only): still emit when every error comes after parsing, so ContentDb.Build can be fed the same defect."""
    errs = Errors()
    tables = {}
    for name, schema in SCHEMA.items():
        path = os.path.join(src, name + ".csv")
        if not os.path.exists(path):
            if schema["required"]:
                errs.add("E001", name, f"missing required table '{name}'")
            continue
        tables[name] = read_table(path, name, schema, errs)
    meta = read_meta(os.path.join(src, "meta.csv"), errs)
    parsed = all(e.split()[1] == "E020" for e in errs)
    if not errs or (emit_invalid and parsed):
        validate(tables, errs, meta, ui_keys)
    if errs and not (emit_invalid and parsed):
        return None, errs
    files, manifest = emit(tables, meta, hot_apply_default)
    if previous is not None and not errs:
        version_rules(tables, meta, files, manifest, previous, errs)
        manifest["hot_apply"] = compute_hot_apply(files, previous)
        files["manifest.json"] = dumps(manifest).encode("utf-8")
    return (files, tables, manifest), errs


def emit(tables, meta, hot_apply):
    files = {}
    entries = {}
    for name in sorted(SCHEMA):
        if name == "localization":
            continue
        if name not in tables and name not in EMIT_WHEN_ABSENT:
            continue
        rows = [shape_row(r, SCHEMA[name]) for r in tables.get(name, [])]
        data = dumps({"table": name, "rows": rows}).encode("utf-8")
        fname = name + ".json"
        files[fname] = data
        entries[name] = {"file": fname, "rows": len(rows), "sha256": hashlib.sha256(data).hexdigest()}

    loc_entries = {}
    loc_rows = tables.get("localization", [])
    ship = [x.strip() for x in (meta.get("ship_locales") or "en").split(",")]
    for locale in ENUMS["Locale"]:
        col = LOCALE_COLUMNS[locale]
        rows = [{"key": r["key"], "text": r[col]} for r in loc_rows if r.get(col)]
        if locale != "en" and not rows and locale not in ship:
            continue
        data = dumps({"table": "localization", "locale": locale, "rows": rows}).encode("utf-8")
        fname = f"localization.{locale}.json"
        files[fname] = data
        loc_entries[locale] = {"file": fname, "rows": len(rows), "sha256": hashlib.sha256(data).hexdigest()}

    content_hash = hashlib.sha256(
        "".join(e["sha256"] for e in list(entries.values()) + list(loc_entries.values())).encode("ascii")).hexdigest()
    s_constants = sorted(k for k, (_, kind) in REQUIRED_CONSTANTS.items() if kind == "S")
    manifest = {
        "schema_version": SCHEMA_VERSION,
        "content_version": meta.get("content_version", ""),
        "min_client_version": meta.get("min_client_version", ""),
        "source_revision": meta.get("source_revision", ""),
        "hot_apply": bool(hot_apply),   # recomputed against --previous by build(); `check` keeps the committed value
        "tables": entries,
        "localization": loc_entries,
        "content_hash": content_hash,
        "s_constants": s_constants,
        "notes": meta.get("notes", ""),
    }
    files["manifest.json"] = dumps(manifest).encode("utf-8")
    return files, manifest


# ------------------------------------------------------------------------------------- versioning (6.2, 6.3)
# B5: tables whose row count may never shrink (and tutorial_steps may never reorder). B3 applies to every table keyed
# by an id, enum or ref, plus these: saves and the server's audit rows reference those keys (02, 08).
NEVER_SHRINK = ["dupe_levels", "awakening", "banner_levels", "fish_levels", "stats", "tutorial_steps"]


def parse_semver(v):
    try:
        return tuple(int(x) for x in v.split("."))
    except (AttributeError, ValueError):
        return (0, 0, 0)


def version_key(v):
    """ContentVersion.Compare's order (09 section 6.3): numeric per component, a missing .N reads as 0."""
    parts = [int(x) for x in v.split(".")]
    return tuple(parts + [0] * (4 - len(parts)))


def load_previous(path):
    """A previous build directory: its manifest, the bytes of every listed file and the parsed rows per table."""
    mpath = os.path.join(path, "manifest.json")
    if not os.path.exists(mpath):
        raise FileNotFoundError(f"--previous {path}: no manifest.json")
    with open(mpath, encoding="utf-8") as f:
        manifest = json.load(f)
    files, rows = {}, {}
    for group in ("tables", "localization"):
        for name, e in (manifest.get(group) or {}).items():
            p = os.path.join(path, e["file"])
            if os.path.exists(p):
                with open(p, "rb") as f:
                    data = f.read()
                files[e["file"]] = data
                if group == "tables":
                    rows[name] = json.loads(data.decode("utf-8"))["rows"]
    return {"manifest": manifest, "files": files, "rows": rows}


def row_key(table, r):
    key = SCHEMA[table]["key"]
    return ",".join(fmt(r[k]) for k in key) if isinstance(key, tuple) else fmt(r[key])


def keyed_tables():
    out = []
    for table, schema in SCHEMA.items():
        key = schema["key"]
        col = next(c for c in schema["columns"] if c[0] == (key[0] if isinstance(key, tuple) else key))
        if col[1] == "id" or col[1].startswith(("enum(", "ref(")) or table in NEVER_SHRINK:
            out.append(table)
    return out


SHIPPABLE_TIERS = ["M0", "MVP"]   # the tiers a client is built for; B4 compares the pool each of them derives


def pool_lists(rows_by_table, banners, tier="MVP"):
    """Pool(banner, rarity) as a client built for `tier` derives it (09 sections 3.4, 5.32; ContentDb.BuildBanners):
    the ids of the pool table's rows with tier <= `tier`, ordinal-sorted, for the banners enabled at that tier."""
    out = {}
    for b in banners:
        if not b.get("enabled") or not tier_le(b, tier):
            continue
        rows = [r for r in rows_by_table.get(POOL_TABLES[b["id"]], []) if tier_le(r, tier)]
        for rarity in ENUMS["Rarity"]:
            out[(b["id"], rarity)] = sorted((r["id"] for r in rows if r["rarity"] == rarity),
                                            key=lambda x: x.encode("utf-8"))
    return out


def pool_shifts(old_rows, new_rows):
    """B4 (issue #250): for every shippable tier, the first id per (banner, rarity) of the previous build's pool that
    is still in the pool but at another index. Returns {(banner, rarity, id): [(tier, old index, new index), ...]}."""
    out = {}
    for tier in SHIPPABLE_TIERS:
        old_pools = pool_lists(old_rows, old_rows.get("banners", []), tier)
        new_pools = pool_lists(new_rows, new_rows.get("banners", []), tier)
        for (banner, rarity), ids in old_pools.items():
            now = new_pools.get((banner, rarity), [])
            for i, rid in enumerate(ids):
                if rid in now and now.index(rid) != i:
                    out.setdefault((banner, rarity, rid), []).append((tier, i, now.index(rid)))
                    break
    return out


def version_rules(tables, meta, files, manifest, prev, errs):
    """E090-E093 and W030-W031 against --previous (09 sections 6.3, 8.5, 8.6)."""
    pm = prev["manifest"]
    pv, cv = pm.get("content_version", ""), manifest["content_version"]
    vloc = "meta[content_version]"
    if VERSION_RE.match(pv or "") and VERSION_RE.match(cv) and version_key(cv) < version_key(pv):
        errs.add("E093", vloc, f"content_version {cv} is lower than previous {pv}")
    if changed_files(files, prev) and cv == pv:
        errs.add("E091", vloc, f"content differs from previous build but content_version is still {cv}")

    cur_rows = {t: [shape_row(r, SCHEMA[t]) for r in rows] for t, rows in tables.items() if SCHEMA[t]["dto"]}

    def e090(table, rid):
        errs.add("E090", table, f"{table}: '{rid}' existed in previous build {pv} and is missing or reordered; "
                 "retire rows with enabled=false or tier=P2 instead")

    for table in keyed_tables():   # B3, B5
        if table not in prev["rows"]:
            continue
        have = {row_key(table, r) for r in cur_rows.get(table, [])}
        for r in prev["rows"][table]:
            k = row_key(table, r)
            if k not in have:
                e090(table, k)
    if "tutorial_steps" in prev["rows"]:   # B5: appended only, never reordered (issue #46)
        before = [r["id"] for r in prev["rows"]["tutorial_steps"]]
        now = [r["id"] for r in cur_rows.get("tutorial_steps", [])]
        for i, rid in enumerate(before):
            if rid in now and (i >= len(now) or now[i] != rid):
                e090("tutorial_steps", rid)
                break
    # B4: an insert that sorts before a shipped id, or a retirement (tier raised past a shippable tier) before one,
    # shifts the indices of the tier-filtered pool the runtime draws from (issue #250).
    for (banner, rarity, rid), moves in pool_shifts(prev["rows"], cur_rows).items():
        table = POOL_TABLES[banner]
        where = "; ".join(f"{t} pool index {i} -> {j}" for t, i, j in moves)
        errs.add("E090", table, f"{table}: '{rid}' in Pool({banner}, {rarity}) of previous build {pv} moved ({where}); "
                 "add ids that sort after every shipped one, and retire a pool row by tier only when it is last in its pool")

    if schema_changed(cur_rows, prev) and \
            not parse_semver(manifest["min_client_version"]) > parse_semver(pm.get("min_client_version", "")):
        errs.add("E092", "meta[min_client_version]", "schema changed (new required column / removed enum value) but "
                 f"min_client_version {manifest['min_client_version']} did not increase")

    old_k = {r["key"]: r["value"] for r in prev["rows"].get("constants", [])}
    notes = meta.get("notes", "") or ""
    for r in cur_rows.get("constants", []):
        if r["kind"] == "S" and r["key"] in old_k and old_k[r["key"]] != r["value"] and r["key"] not in notes:
            errs.warnings.add("W030", f"constants[{r['key']}].value", f"S constant changed from {fmt(old_k[r['key']])} "
                              f"to {fmt(r['value'])} without a note in meta.notes")
    new_k = {r["key"]: r["value"] for r in cur_rows.get("constants", [])}
    if isinstance(old_k.get("FISH_MAX_LVL0"), int) and isinstance(new_k.get("FISH_MAX_LVL0"), int) and \
            new_k["FISH_MAX_LVL0"] < old_k["FISH_MAX_LVL0"]:
        errs.warnings.add("W031", "constants[FISH_MAX_LVL0].value", f"FISH_MAX_LVL0 decreased from "
                          f"{old_k['FISH_MAX_LVL0']} to {new_k['FISH_MAX_LVL0']} (saved fish keep their level, issue #41)")
    old_cap = {r["id"]: r["max_level"] for r in prev["rows"].get("research", []) if r.get("effect") == "FISH_CAP"}
    for r in cur_rows.get("research", []):
        if r["effect"] == "FISH_CAP" and r["id"] in old_cap and r["max_level"] < old_cap[r["id"]]:
            errs.warnings.add("W031", f"research[{r['id']}].max_level", f"max_level decreased from {old_cap[r['id']]} "
                              f"to {r['max_level']} (saved fish keep their level, issue #41)")


def changed_files(files, prev):
    """Data files (not the manifest) added, removed or with different bytes."""
    names = (set(files) | set(prev["files"])) - {"manifest.json"}
    return sorted(f for f in names if files.get(f) != prev["files"].get(f))


def schema_changed(cur_rows, prev):
    """B6: a non-nullable column the previous build's rows lack, or an enum value the previous rows use that is gone."""
    for table, rows in cur_rows.items():
        old = prev["rows"].get(table)
        if not old or not rows:
            continue
        nullable = {c[0]: c[2] for c in SCHEMA[table]["columns"]}
        for col in rows[0]:
            if col not in old[0] and not nullable.get(col, False):
                return True
    for table, rows in prev["rows"].items():
        if table not in SCHEMA:
            continue
        for name, typ, _ in SCHEMA[table]["columns"]:
            if typ.startswith("enum("):
                allowed = ENUMS[typ[5:-1]]
                if any(r.get(name) is not None and r.get(name) not in allowed for r in rows):
                    return True
    return False


def compute_hot_apply(files, prev):
    """6.2: true only when every changed file is in the hot-apply allowlist (00 contract 3, issue #54). No change at
    all keeps the previous build's value, so an identical rebuild reproduces identical bytes."""
    changed = changed_files(files, prev)
    if not changed:
        return bool(prev["manifest"].get("hot_apply", False))
    allowed = {t + ".json" for t in HOT_APPLY_TABLES if t != "localization"}
    return all(f in allowed or f.startswith("localization.") for f in changed)


def diff_dirs(a, b):
    """Row-level added/removed/changed per table between two content dirs (7.1 `diff`), as printable lines."""
    pa, pb = load_previous(a), load_previous(b)
    out = [f"content {pa['manifest'].get('content_version')} -> {pb['manifest'].get('content_version')}"]
    for table in sorted(set(pa["rows"]) | set(pb["rows"])):
        ra, rb = pa["rows"].get(table, []), pb["rows"].get(table, [])
        if table in SCHEMA:
            ka = {row_key(table, r): r for r in ra}
            kb = {row_key(table, r): r for r in rb}
        else:
            ka, kb = {str(i): r for i, r in enumerate(ra)}, {str(i): r for i, r in enumerate(rb)}
        lines = [f"  + {k}" for k in kb if k not in ka] + [f"  - {k}" for k in ka if k not in kb]
        for k in kb:
            if k in ka and ka[k] != kb[k]:
                cols = [c for c in kb[k] if ka[k].get(c) != kb[k].get(c)] + [c for c in ka[k] if c not in kb[k]]
                lines.append(f"  ~ {k}: " + "; ".join(
                    f"{c} {json.dumps(ka[k].get(c))} -> {json.dumps(kb[k].get(c))}" for c in cols))
        if lines:
            out.append(f"{table}:")
            out += lines

    def texts(p, locale):
        e = p["manifest"].get("localization", {}).get(locale)
        if not e or e["file"] not in p["files"]:
            return {}
        return {r["key"]: r["text"] for r in json.loads(p["files"][e["file"]].decode("utf-8"))["rows"]}

    for locale in sorted(set(pa["manifest"].get("localization", {})) | set(pb["manifest"].get("localization", {}))):
        ta, tb = texts(pa, locale), texts(pb, locale)
        lines = [f"  + {k}" for k in tb if k not in ta] + [f"  - {k}" for k in ta if k not in tb]
        lines += [f"  ~ {k}: {json.dumps(ta[k], ensure_ascii=False)} -> {json.dumps(tb[k], ensure_ascii=False)}"
                  for k in tb if k in ta and ta[k] != tb[k]]
        if lines:
            out.append(f"localization.{locale}:")
            out += lines
    if len(out) == 1:
        out.append("no row changes")
    return out


# ------------------------------------------------------------------------------------------------ C# DTOs
def pascal(snake):
    return "".join(p[:1].upper() + p[1:] for p in snake.split("_"))


def newtonsoft_snake(name):
    """Newtonsoft.Json SnakeCaseNamingStrategy, so the Runtime loader maps every field back to its column."""
    out, state = [], "start"
    for i, ch in enumerate(name):
        if ch.isupper():
            if state == "upper":
                if i > 0 and i + 1 < len(name) and not name[i + 1].isupper():
                    out.append("_")
            elif state in ("lower", "newword"):
                out.append("_")
            out.append(ch.lower())
            state = "upper"
        else:
            out.append(ch)
            state = "lower"
    return "".join(out)


def cs_type(typ, nullable):
    if typ == "int":
        return "int?" if nullable else "int"
    if typ == "long":
        return "long?" if nullable else "long"
    if typ == "double":
        return "double?" if nullable else "double"
    if typ == "bool":
        return "bool?" if nullable else "bool"
    if typ == "intlist":
        return "int[]"
    if typ == "grantlist":
        return "GrantDto[]"
    if typ == "constval":
        return "object"
    return "string"


def gen_dtos():
    lines = [
        "// <auto-generated>",
        "// Generated by tools/export_content.py from its SCHEMA (specs/09 sections 5 and 9.1). Do not edit by hand:",
        "// change SCHEMA and run `python3 tools/export_content.py build`; `check` fails when this file is stale.",
        "// </auto-generated>",
        "using System.Collections.Generic;",
        "",
        "namespace HeroCat.Sim.Content.Dto",
        "{",
        "    /// <summary>One table's rows per field; null when the table is absent from the bundle. No localization (09 section 13).</summary>",
        "    public sealed class ContentDtos",
        "    {",
        "        public ManifestDto Manifest;",
    ]
    for name, schema in SCHEMA.items():
        if schema["dto"] is None:
            continue
        lines.append(f"        public {schema['dto']}[] {pascal(name)};")
    lines += [
        "    }",
        "",
        "    /// <summary>manifest.json (09 section 6.2). Localization entries are listed but their contents never reach Sim.</summary>",
        "    public sealed class ManifestDto",
        "    {",
        "        public int SchemaVersion;",
        "        public string ContentVersion;",
        "        public string MinClientVersion;",
        "        public string SourceRevision;",
        "        public bool HotApply;",
        "        public Dictionary<string, ManifestEntryDto> Tables;",
        "        public Dictionary<string, ManifestEntryDto> Localization;",
        "        public string ContentHash;",
        "        public string[] SConstants;",
        "        public string Notes;",
        "    }",
        "",
        "    public sealed class ManifestEntryDto",
        "    {",
        "        public string File;",
        "        public int Rows;",
        "        public string Sha256;",
        "    }",
        "",
        "    /// <summary>A grantlist entry (09 section 3.2): a currency grant or an item grant.</summary>",
        "    public sealed class GrantDto",
        "    {",
        "        public string Currency;",
        "        public string Kind;",
        "        public string Id;",
        "        public long Amount;",
        "    }",
    ]
    done = set()
    for name, schema in SCHEMA.items():
        dto = schema["dto"]
        if dto is None or dto in done:
            continue
        done.add(dto)
        lines += ["", f"    /// <summary>{name} (09 section 5).</summary>", f"    public sealed class {dto}", "    {"]
        folded = {}
        for fname, (cols, vtyp) in schema["folds"].items():
            for col in cols:
                folded[col] = (fname, vtyp)
        emitted = set()
        for col, typ, nullable in schema["columns"]:
            if col in folded:
                fname, vtyp = folded[col]
                if fname in emitted:
                    continue
                emitted.add(fname)
                lines.append(f"        public Dictionary<string, {cs_type(vtyp, False)}> {pascal(fname)};")
                continue
            lines.append(f"        public {cs_type(typ, nullable)} {pascal(col)};")
        lines.append("    }")

    def cs_list(name, values, doc):
        out = ["", f"        /// <summary>{doc}</summary>", f"        public static readonly string[] {name} =", "        {"]
        row = []
        for v in values:
            row.append(f'"{v}",')
            if len(" ".join(row)) > 100:
                out.append("            " + " ".join(row))
                row = []
        if row:
            out.append("            " + " ".join(row))
        return out + ["        };"]

    lines += ["", "    /// <summary>The exporter's rule data ContentDb.Build re-checks (09 section 8.6 loader scope), so the lists cannot drift.</summary>",
              "    public static class ContentRules", "    {"]
    sim_tables = [t for t in TABLE_ORDER if t != "localization"]   # strings never reach the Sim (09 section 13)
    lines += cs_list("TableOrder", sim_tables, "SCHEMA order without localization: errors sort by table in this order (09 acceptance 7).")[1:]
    lines += cs_list("Positive", POSITIVE, "Constants that must be &gt; 0 (09 section 5.1).")
    lines += cs_list("NonNegative", NON_NEGATIVE, "Constants that must be &gt;= 0.")
    lines += cs_list("UnitInterval", UNIT_INTERVAL, "Constants that must be in [0, 1].")
    lines += ["    }", "}", ""]
    return "\n".join(lines).encode("utf-8")


def check_dto_names(errs):
    for name, schema in SCHEMA.items():
        cols = [x[0] for x in schema["columns"]] + list(schema["folds"])
        for col in cols:
            if newtonsoft_snake(pascal(col)) != col:
                errs.add("E003", name, f"unknown column '{col}' (its C# field {pascal(col)} does not snake-case back)")


# ------------------------------------------------------------------------------------------------- pull (7.1)
SHEETS_SCOPE = "https://www.googleapis.com/auth/spreadsheets.readonly"
GVIZ_URL = "https://docs.google.com/spreadsheets/d/{sheet}/gviz/tq?tqx=out:csv&headers=1&sheet={tab}"
PULL_INSTALL_HINT = ("pull --service-account needs the optional Google API client: "
                     "python3 -m pip install google-api-python-client google-auth "
                     "(or use --csv-export for a link-readable sheet)")


class PullError(Exception):
    """A network, auth, dependency or workbook-shape failure: `pull` exits 2 (7.1)."""


def pull_tables():
    """The tab names `pull` writes: every SCHEMA table plus `meta` (3.1)."""
    return sorted(SCHEMA) + ["meta"]


def normalize_tab(rows):
    """Cells as text, rows padded to the header width (the API omits trailing empty cells), trailing empty cells past
    the header and trailing blank rows dropped. Interior blank rows stay so build's row numbers match the sheet."""
    rows = [["" if c is None else str(c) for c in r] for r in rows]
    header = rows[0] if rows else []
    while header and header[-1] == "":
        header = header[:-1]
    width = len(header)
    out = [header]
    for r in rows[1:]:
        r = r + [""] * (width - len(r))
        while len(r) > width and r[-1] == "":
            r = r[:-1]
        out.append(r)
    while len(out) > 1 and all(c == "" for c in out[-1]):
        out.pop()
    return out


def csv_bytes(rows):
    """The one CSV form content_src/ is committed in: minimal quoting, LF line ends, UTF-8 without BOM."""
    buf = io.StringIO()
    csv.writer(buf, lineterminator="\n").writerows(rows)
    return buf.getvalue().encode("utf-8")


def tabs_from_batchget(titles, response):
    """{title: rows} from a spreadsheets.values.batchGet response; valueRanges come back in request order."""
    ranges = response.get("valueRanges", [])
    if len(ranges) != len(titles):
        raise PullError(f"batchGet returned {len(ranges)} ranges for {len(titles)} tabs")
    return {t: vr.get("values", []) for t, vr in zip(titles, ranges)}


def fetch_service_account(sheet, creds_path, service=None):
    """Every tab via Sheets API v4: spreadsheets.get for the tab titles, then one values.batchGet. FORMATTED_VALUE
    returns the text a cell displays, the same text --csv-export and File > Download produce."""
    if service is None:
        try:
            from google.oauth2 import service_account
            from googleapiclient.discovery import build as discovery_build
        except ImportError:
            raise PullError(PULL_INSTALL_HINT) from None
        try:
            creds = service_account.Credentials.from_service_account_file(creds_path, scopes=[SHEETS_SCOPE])
            service = discovery_build("sheets", "v4", credentials=creds, cache_discovery=False)
        except (OSError, ValueError) as e:
            raise PullError(f"--service-account {creds_path}: {e}") from None
    try:
        meta = service.spreadsheets().get(spreadsheetId=sheet, fields="sheets.properties.title").execute()
        titles = [sh["properties"]["title"] for sh in meta.get("sheets", [])]
        wanted = [t for t in titles if t in pull_tables()]
        quoted = ["'" + t.replace("'", "''") + "'" for t in wanted]
        resp = service.spreadsheets().values().batchGet(
            spreadsheetId=sheet, ranges=quoted, majorDimension="ROWS", valueRenderOption="FORMATTED_VALUE").execute()
    except Exception as e:   # googleapiclient.errors.HttpError, auth and transport errors: all exit 2
        raise PullError(f"Sheets API: {e}") from None
    tabs = tabs_from_batchget(wanted, resp)
    return tabs, [t for t in titles if t not in tabs]


def header_score(table, rows):
    """How many of the CSV's header cells are columns of `table` (meta: key, value)."""
    names = {c[0] for c in SCHEMA[table]["columns"]} if table in SCHEMA else {"key", "value"}
    return sum(1 for h in (rows[0] if rows else []) if h.strip() in names)


def http_get(url):
    with urllib.request.urlopen(url, timeout=60) as r:
        return r.read()


def fetch_csv_export(sheet, get=http_get):
    """Every known tab via the gviz CSV URL (link-readable sheets, no OAuth). gviz answers a tab name it does not
    have with the workbook's first tab, so a tab counts as absent when its bytes repeat another tab's and its header
    fits its schema worse, or when it is an optional table whose header has none of its columns. Format every tab
    as Plain text: gviz blanks cells whose type differs from the column's majority type."""
    bodies = {}
    for table in pull_tables():
        url = GVIZ_URL.format(sheet=urllib.parse.quote(sheet), tab=urllib.parse.quote(table))
        try:
            bodies[table] = get(url)
        except Exception as e:   # urllib.error.URLError / HTTPError, timeouts: all exit 2
            raise PullError(f"{url}: {e}") from None
    by_body = {}
    for table, body in bodies.items():
        by_body.setdefault(body, []).append(table)
    tabs = {}
    for body, names in by_body.items():
        try:
            text = body.decode("utf-8-sig")
        except UnicodeDecodeError:
            raise PullError(f"tab {names[0]}: the response is not UTF-8") from None
        if text.lstrip().startswith("<"):
            raise PullError(f"tab {names[0]}: got HTML instead of CSV (is the sheet link-readable?)")
        rows = list(csv.reader(io.StringIO(text, newline="")))
        scores = sorted(((header_score(t, rows), t) for t in names), reverse=True)
        if len(scores) > 1 and scores[0][0] > 0 and scores[0][0] == scores[1][0]:
            raise PullError("tabs " + ", ".join(sorted(names)) + " returned the same CSV; cannot tell which exists")
        best_score, best = scores[0]
        optional = best in SCHEMA and not SCHEMA[best]["required"]
        if len(scores) > 1 and best_score == 0 or optional and best_score == 0:
            continue
        tabs[best] = rows
    return tabs, []


def pull(sheet, src, service_account=None, csv_export=False, service=None, get=None):
    """Fetches the workbook into `src` (09 sections 3.1, 7.1). Writes `<tab>.csv` for every tab named as a table or
    `meta`, only when its bytes change; removes the CSV of a known table the workbook no longer has; leaves every other
    file alone. Returns (written, removed, skipped tab titles)."""
    if csv_export:
        tabs, skipped = fetch_csv_export(sheet, get=get or http_get)
    else:
        tabs, skipped = fetch_service_account(sheet, service_account, service=service)
    if "meta" not in tabs or not any(t in tabs for t in SCHEMA):
        raise PullError(f"sheet {sheet} has no meta tab or no table tabs (tab names are table names, 3.1)")
    os.makedirs(src, exist_ok=True)
    written, removed = [], []
    for table in pull_tables():
        path = os.path.join(src, table + ".csv")
        if table not in tabs:
            if os.path.exists(path):
                os.remove(path)
                removed.append(table)
            continue
        data = csv_bytes(normalize_tab(tabs[table]))
        if read_bytes(path) != data:
            tmp = path + ".tmp"
            with open(tmp, "wb") as f:
                f.write(data)
            os.replace(tmp, path)
            written.append(table)
    return written, removed, skipped


# ------------------------------------------------------------------------------------------------------- CLI
def print_summary(tables, manifest):
    print(f"content_version {manifest['content_version']}  content_hash {manifest['content_hash']}  "
          f"hot_apply {str(manifest['hot_apply']).lower()}")
    for name, e in manifest["tables"].items():
        print(f"  {name:26s} {e['rows']:4d} rows")
    for locale, e in manifest["localization"].items():
        print(f"  localization.{locale:13s} {e['rows']:4d} rows")
    for pool in ("weapons", "skills", "companions", "runes"):
        rows = tables.get(pool, [])
        if rows:
            counts = [sum(1 for r in rows if r["rarity"] == rar) for rar in ENUMS["Rarity"]]
            print(f"  pool {pool:10s} " + " / ".join(f"{r[0]} {n}" for r, n in zip(ENUMS["Rarity"], counts)))
    print("  S constants: " + ", ".join(manifest["s_constants"]))


def read_bytes(path):
    if not os.path.exists(path):
        return None
    with open(path, "rb") as f:
        return f.read()


def read_ui_keys(path):
    """The client's ui.* key list (13.1): one key per line; blank lines and `#` comments skipped."""
    with open(path, encoding="utf-8") as f:
        return [ln.strip() for ln in f if ln.strip() and not ln.strip().startswith("#")]


WORKFLOW_HELP = """content change workflow (specs/09 section 12):
  1. Edit values in the sheet; bump meta.content_version (YYYY.MM.DD[.N]) and meta.source_revision.
  2. python3 tools/export_content.py pull --sheet <id> --service-account creds.json   (or --csv-export)
  3. python3 tools/export_content.py build --previous <last shipped content dir>   (fix every CONTENT E0xx;
     W0xx fail CI). ./check.sh --content runs the same rules against the merge base's content/.
  4. python3 tools/export_content.py diff --a <last shipped content dir> --b content   (paste into the PR)
  5. Open a PR with content_src/*.csv and content/*.json together; one that changes an S constant carries the
     tuning tool's before/after table (W030). CI re-runs check, build --strict-warnings and the bot acceptance.
  6. Golden bot files and plausibility_stage_bound change in the same PR as the content that moved them.

pull notes: every tab whose name is a table name (or meta) is written; other tabs are skipped and listed. The CSV
of a known table the workbook no longer has is removed. Cells are read as displayed text, so format every tab as
Plain text in an en-US workbook (--csv-export blanks mixed-type cells otherwise). Exit 2 on network, auth or a
missing optional dependency (pip install google-api-python-client google-auth)."""


# ---------------------------------------------------------------- translator round trip and pseudo-loc (13.2, 13.3)
TRANSLATOR_COLUMNS = ["key", "en", None, "max_chars", "context"]   # None = the target locale's localization column
TRANSLATIONS_LOC = "translations"   # 8.1 location for defects in the returned file itself (header, duplicate rows)
PSEUDO_LOC_PAD_PCT = 0.40   # 09 constants, 07 section 12: a pseudo string renders >= (1 + this) x its en length
PSEUDO_OPEN, PSEUDO_CLOSE, PSEUDO_FILL = "[!!! ", " !!!]", "~"
PSEUDO_LOCALE_RE = re.compile(r"^[a-z]{2,3}(-[A-Za-z0-9]+)?$")
# One precomposed code point per letter, so accenting never changes a length (07's sample: Test -> Ţêšţ).
PSEUDO_ACCENTS = dict(zip("AaCcDdEeGgHhIiJjKkLlNnOoRrSsTtUuWwYyZz", "ÅåÇçÐðÊêĜĝĤĥÎîĴĵĶķĻļÑñÖöŔŕŠšŢţÛûŴŵÝýŽž"))


def read_localization(src):
    """(typed rows, raw csv rows, errs) of <src>/localization.csv; the raw rows keep #context and every cell as written."""
    path = os.path.join(src, "localization.csv")
    errs = Errors()
    if not os.path.exists(path):
        errs.add("E001", "localization", "missing required table 'localization'")
        return [], [], errs
    rows = read_table(path, "localization", SCHEMA["localization"], errs)
    with open(path, newline="", encoding="utf-8-sig") as f:
        raw = list(csv.reader(f))
    return rows, raw, errs


def target_column(locale):
    """The localization column a translator fills, or raises ValueError (en is the source; xx is pseudo, never real)."""
    if locale not in LOCALE_COLUMNS:
        raise ValueError(f"'{locale}' is not a valid Locale (allowed: {', '.join(ENUMS['Locale'])})")
    if locale == "en":
        raise ValueError("en is the source text, not a translation target")
    return LOCALE_COLUMNS[locale]


def translator_csv(src, locale, missing_only=False):
    """13.2: the CSV text a translator fills: key, en, <locale column>, max_chars, context (#context). Returns (text, errs)."""
    col = target_column(locale)
    rows, raw, errs = read_localization(src)
    if errs:
        return None, errs
    header = [h.strip() for h in raw[0]]
    ctx_i = header.index("#context") if "#context" in header else None
    out = io.StringIO()
    w = csv.writer(out, lineterminator="\n")
    w.writerow([c or col for c in TRANSLATOR_COLUMNS])
    for r in rows:
        if missing_only and r.get(col) is not None:
            continue
        cells = raw[r["_row"]]
        context = cells[ctx_i].strip() if ctx_i is not None and ctx_i < len(cells) else ""
        w.writerow([r["key"], r["en"], r.get(col) or "", r["max_chars"], context])
    return out.getvalue(), errs


def import_translations(src, csv_path, locale):
    """13.2: merges a returned translator CSV into <src>/localization.csv. A row is refused (and listed) when its key
    is unknown (E072), its en changed since the export (E076), it lacks the ICU plural branches the locale needs (E075)
    or it is over budget (E074); every other filled row is merged. Empty cells never clear a translation.
    Returns (merged count, errs)."""
    col = target_column(locale)
    rows, raw, errs = read_localization(src)
    if errs:
        return 0, errs
    by_key = {r["key"]: r for r in rows}
    with open(csv_path, newline="", encoding="utf-8-sig") as f:
        back = list(csv.reader(f))
    header = [h.strip() for h in back[0]] if back else []
    for name in ("key", "en", col):
        if name not in header:
            errs.add("E002", TRANSLATIONS_LOC, f"missing required column '{name}'")
    if errs:
        return 0, errs
    ki, ei, ti = header.index("key"), header.index("en"), header.index(col)
    updates, first_seen = {}, {}
    for n, cells in enumerate(back[1:], start=1):
        cell = lambda i: cells[i].strip() if i < len(cells) else ""   # noqa: E731
        key, exported_en, text = cell(ki), cell(ei), cell(ti)
        if not text:
            continue
        if key in first_seen:
            errs.add("E020", f"{TRANSLATIONS_LOC} row {n}", f"duplicate key '{key}' (first seen at row {first_seen[key]})")
            continue
        first_seen[key] = n
        r = by_key.get(key)
        if r is None:
            errs.add("E072", f"{TRANSLATIONS_LOC} row {n}", f"key '{key}' has no row in localization")
            continue
        loc = f"{r['_loc']}.{col}"
        if exported_en != r["en"]:
            errs.add("E076", loc, f"en text changed since this translation was exported ('{exported_en}' -> '{r['en']}')")
            continue
        found = text_errors(r, locale, text)
        for code, msg in found:
            errs.add(code, loc, msg)
        if not found and text != r.get(col):
            updates[key] = text
    if updates:
        head = [h.strip() for h in raw[0]]
        kc, tc = head.index("key"), head.index(col)
        for cells in raw[1:]:
            if len(cells) > kc and cells[kc].strip() in updates:
                cells.extend([""] * (tc + 1 - len(cells)))
                cells[tc] = updates[cells[kc].strip()]
        out = io.StringIO()
        csv.writer(out, lineterminator="\n").writerows(raw)
        with open(os.path.join(src, "localization.csv"), "w", encoding="utf-8", newline="") as f:
            f.write(out.getvalue())
    return len(updates), errs


def pseudo_icu(text):
    """Accents the literal text of an ICU message (icu_parse's grammar) and copies every argument name, type, style,
    selector, `#` and quote verbatim, so the result parses to the same argument structure."""
    pos, out = 0, []

    def word():
        nonlocal pos
        start = pos
        while pos < len(text) and text[pos].isspace():
            pos += 1
        while pos < len(text) and text[pos] not in ",{} \t\n":
            pos += 1
        w = text[start:pos].strip()
        while pos < len(text) and text[pos].isspace():
            pos += 1
        return w

    def message():
        nonlocal pos
        while pos < len(text):
            ch = text[pos]
            if ch == "'" and text.startswith("''", pos):
                out.append("''")
                pos += 2
            elif ch == "'" and pos + 1 < len(text) and text[pos + 1] in "{}#":
                end = text.index("'", pos + 1)
                out.append("'" + accent(text[pos + 1:end]) + "'")
                pos = end + 1
            elif ch == "{":
                argument()
            elif ch == "}":
                return
            else:
                out.append(PSEUDO_ACCENTS.get(ch, ch))
                pos += 1

    def argument():
        nonlocal pos
        start = pos
        pos += 1
        word()
        if text[pos] == "}":
            pos += 1
            out.append(text[start:pos])
            return
        pos += 1
        kind = word()
        if kind == "number":
            pos = text.index("}", pos) + 1
            out.append(text[start:pos])
            return
        pos += 1   # the ',' before the branches
        out.append(text[start:pos])
        while True:
            s = pos
            sel = word()
            if sel.startswith("offset:"):
                sel = word()
            out.append(text[s:pos])
            if not sel:
                break
            out.append("{")
            pos += 1
            message()
            out.append("}")
            pos += 1
        out.append("}")
        pos += 1

    message()
    return "".join(out)


def accent(s):
    return "".join(PSEUDO_ACCENTS.get(ch, ch) for ch in s)


def pseudo_text(en):
    """13.3: [!!! accented en~~~ !!!], padded so its rendered length (E074's measure) is at least
    ceil((1 + PSEUDO_LOC_PAD_PCT) x) the en one. ICU arguments and plural syntax are kept intact."""
    try:
        n = rendered_len(icu_parse(en))
        body = pseudo_icu(en)
    except ValueError:   # not an ICU message (a stray brace in plain text): everything is literal
        n, body = len(en), accent(en)
    pct = round(PSEUDO_LOC_PAD_PCT * 100)
    target = n + (n * pct + 99) // 100
    pad = max(0, target - n - len(PSEUDO_OPEN) - len(PSEUDO_CLOSE))
    return PSEUDO_OPEN + body + PSEUDO_FILL * pad + PSEUDO_CLOSE


def pseudo_build(src, locale="xx"):
    """13.3: the normal build of src plus localization.<locale>.json (every en string pseudo-localized), listed in the
    manifest's localization block and folded into content_hash. Returns (files or None, errs)."""
    if locale in ENUMS["Locale"] or not PSEUDO_LOCALE_RE.match(locale):
        raise ValueError(f"'{locale}' must be a pseudo locale such as xx, not a shipping Locale "
                         f"({', '.join(ENUMS['Locale'])})")
    result, errs = build(src)
    if result is None:
        return None, errs
    files, tables, manifest = result
    rows = [{"key": r["key"], "text": pseudo_text(r["en"])} for r in tables.get("localization", [])]
    data = dumps({"table": "localization", "locale": locale, "rows": rows}).encode("utf-8")
    fname = f"localization.{locale}.json"
    files[fname] = data
    manifest["localization"][locale] = {"file": fname, "rows": len(rows), "sha256": hashlib.sha256(data).hexdigest()}
    shas = [e["sha256"] for e in list(manifest["tables"].values()) + list(manifest["localization"].values())]
    manifest["content_hash"] = hashlib.sha256("".join(shas).encode("ascii")).hexdigest()
    files["manifest.json"] = dumps(manifest).encode("utf-8")
    return files, errs


def localization_verb(args):
    """translator-csv, import-translations and pseudo (7.1)."""
    try:
        if args.verb == "translator-csv":
            if not args.out or not args.locale:
                raise ValueError("translator-csv needs --locale <locale> --out <file>")
            text, errs = translator_csv(args.src, args.locale, args.missing_only)
        elif args.verb == "import-translations":
            if not args.csv or not args.locale:
                raise ValueError("import-translations needs --csv <file> --locale <locale>")
            merged, errs = import_translations(args.src, args.csv, args.locale)
        else:
            if not args.out:
                raise ValueError("pseudo needs --out <dir> (never the shipped content/)")
            shipped = os.path.realpath(os.path.join(args.src, os.pardir, "content"))
            if os.path.realpath(args.out) in (shipped, os.path.realpath(args.src)):
                raise ValueError(f"pseudo must not write into the shipped content dir {args.out}")
            files, errs = pseudo_build(args.src, args.locale or "xx")
    except (OSError, ValueError) as e:
        print(f"{args.verb}: {e}", file=sys.stderr)
        return 2
    for e in errs:
        print(e, file=sys.stderr)
    if args.verb == "import-translations":
        print(f"merged {merged} {args.locale} translations into {os.path.join(args.src, 'localization.csv')}"
              f"{f'; refused {len(errs)} rows' if errs else ''}" +
              ("; rebuild content/ and bump meta.content_version" if merged else ""))
        return 1 if errs else 0
    if errs:
        return 1
    if args.verb == "translator-csv":
        with open(args.out, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        print(f"wrote {args.out} ({len(list(csv.reader(io.StringIO(text)))) - 1} rows)")
        return 0
    os.makedirs(args.out, exist_ok=True)
    for f in os.listdir(args.out):
        if f.endswith(".json") and f not in files:
            os.remove(os.path.join(args.out, f))
    for fname, data in files.items():
        with open(os.path.join(args.out, fname), "wb") as f:
            f.write(data)
    print(f"wrote {args.out}/ with localization.{args.locale or 'xx'}.json ({len(files)} files)")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0], epilog=WORKFLOW_HELP,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("verb", choices=["pull", "build", "check", "gen-dtos", "diff", "translator-csv",
                                     "import-translations", "pseudo"])
    ap.add_argument("--sheet", help="pull: the Google Sheet id (the part of its URL after /d/)")
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--service-account", metavar="CREDS_JSON",
                      help="pull: Sheets API v4 with this service-account key file (needs google-api-python-client)")
    mode.add_argument("--csv-export", action="store_true", help="pull: the gviz CSV URL (link-readable sheets only)")
    ap.add_argument("--src", default="content_src")
    ap.add_argument("--out", help="content dir (default content); translator-csv: the CSV file; pseudo: required dir")
    ap.add_argument("--dto-out", default=DTO_PATH)
    ap.add_argument("--previous", help="previous build dir: version rules E090-E093, W030, W031 and hot_apply (6.3)")
    ap.add_argument("--ui-keys", help="the client's ui_keys.txt for E072 / W021 (13.1)")
    ap.add_argument("--strict-warnings", action="store_true", help="exit 1 on any W0xx warning (CI)")
    ap.add_argument("--emit-invalid", action="store_true", help=argparse.SUPPRESS)   # content fixtures only
    ap.add_argument("--a", help="diff: the old content dir")
    ap.add_argument("--b", help="diff: the new content dir")
    ap.add_argument("--locale", help="translator-csv / import-translations: ja, ko or zh-Hans; pseudo: default xx")
    ap.add_argument("--missing-only", action="store_true", help="translator-csv: only rows with no text in --locale")
    ap.add_argument("--csv", help="import-translations: the translator's returned CSV")
    args = ap.parse_args(argv)
    if args.verb in ("translator-csv", "import-translations", "pseudo"):
        return localization_verb(args)
    if args.out is None:
        args.out = "content"

    if args.verb == "pull":
        if not args.sheet or not (args.service_account or args.csv_export):
            print("pull needs --sheet <id> and one of --service-account <creds.json> / --csv-export", file=sys.stderr)
            return 2
        try:
            written, removed, skipped = pull(args.sheet, args.src, service_account=args.service_account,
                                             csv_export=args.csv_export)
        except (PullError, OSError) as e:
            print(f"pull: {e}", file=sys.stderr)
            return 2
        print(f"pulled sheet {args.sheet} into {args.src}/: {len(written)} file(s) changed"
              + (" (" + ", ".join(written) + ")" if written else "")
              + (f"; removed {', '.join(removed)}" if removed else "")
              + (f"; skipped tabs {', '.join(skipped)}" if skipped else ""))
        return 0

    if args.verb == "diff":
        if not args.a or not args.b:
            print("diff needs --a <dir> --b <dir>", file=sys.stderr)
            return 2
        try:
            lines = diff_dirs(args.a, args.b)
        except (OSError, ValueError, KeyError) as e:
            print(f"diff: {e}", file=sys.stderr)
            return 2
        print("\n".join(lines))
        return 0

    errs = Errors()
    check_dto_names(errs)
    if errs:
        for e in errs:
            print(e, file=sys.stderr)
        return 1
    dto = gen_dtos()
    if args.verb == "gen-dtos":
        with open(args.dto_out, "wb") as f:
            f.write(dto)
        print(f"wrote {args.dto_out}")
        return 0

    if not os.path.isdir(args.src):
        print(f"source directory not found: {args.src}", file=sys.stderr)
        return 2
    try:
        previous = load_previous(args.previous) if args.previous else None
        ui_keys = read_ui_keys(args.ui_keys) if args.ui_keys else None
    except (OSError, ValueError) as e:
        print(str(e), file=sys.stderr)
        return 2
    hot_apply_default = False
    committed = os.path.join(args.out, "manifest.json")
    if args.verb == "check" and previous is None and os.path.exists(committed):
        try:   # check without --previous reproduces the committed hot_apply instead of recomputing it
            with open(committed, encoding="utf-8") as f:
                hot_apply_default = bool(json.load(f).get("hot_apply", False))
        except ValueError:
            pass
    result, errs = build(args.src, previous=previous, ui_keys=ui_keys, emit_invalid=args.emit_invalid,
                         hot_apply_default=hot_apply_default)
    for e in errs:
        print(e, file=sys.stderr)
    for w in errs.warnings:
        print(w, file=sys.stderr)
    if result is None or (errs and not args.emit_invalid):
        return 1
    files, tables, manifest = result
    failed = bool(errs) or (args.strict_warnings and bool(errs.warnings))

    if args.verb == "check":
        stale = []
        for fname, data in files.items():
            p = os.path.join(args.out, fname)
            if read_bytes(p) != data:
                stale.append(fname)
        if os.path.isdir(args.out):
            stale += [f"{f} (not produced by the build)" for f in sorted(os.listdir(args.out))
                      if f.endswith(".json") and f not in files]
        if read_bytes(args.dto_out) != dto:
            stale.append(args.dto_out)
        if stale:
            print(f"{args.out}/ is stale (run python3 tools/export_content.py build): " + ", ".join(sorted(stale)),
                  file=sys.stderr)
            return 1
        if failed:
            return 1
        print(f"{args.out}/ and {args.dto_out} are up to date ({manifest['content_version']} {manifest['content_hash'][:8]})")
        return 0

    os.makedirs(args.out, exist_ok=True)
    for f in os.listdir(args.out):
        if f.endswith(".json") and f not in files:
            os.remove(os.path.join(args.out, f))
    for fname, data in files.items():
        with open(os.path.join(args.out, fname), "wb") as f:
            f.write(data)
    if args.emit_invalid:
        return 1 if failed else 0
    os.makedirs(os.path.dirname(args.dto_out), exist_ok=True)
    with open(args.dto_out, "wb") as f:
        f.write(dto)
    print_summary(tables, manifest)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
