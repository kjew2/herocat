#!/usr/bin/env python3
"""Content exporter: content_src/<table>.csv -> content/<table>.json + manifest.json (specs/09).

    python3 tools/export_content.py build [--src content_src] [--out content] [--dto-out PATH]
    python3 tools/export_content.py check [--src content_src] [--out content] [--dto-out PATH]
    python3 tools/export_content.py gen-dtos [--dto-out PATH]

`build` parses, validates and emits every table plus the manifest (09 sections 6.1, 6.2) and regenerates the C#
DTO file (`HeroCat.Sim.Content.Dto`, 09 section 9.1) from SCHEMA, so the Python and C# shapes cannot drift.
`check` builds to memory and exits 1 if content/ or the DTO file differ byte for byte (CI's stale guard).

Implemented here (issue #121): SCHEMA for every 09 section 5 table, column types (3.2), ENUMS (3.3),
REQUIRED_CONSTANTS (5.1), SCREEN_IDS (00 section 5.3), structural codes E001-E003, E010, E011, E020-E022,
E070, E071, E094, constants rules E040-E043 and the 5.1 range rules, deterministic emit and the manifest.
Not yet: the table rules of 8.4 and warnings of 8.6 (TODO(#122)), --previous / diff / hot_apply (TODO(#123)),
localization rules E072-E075 (TODO(#128)), pull (TODO(#132)).

Exit codes: 0 ok, 1 validation errors (all listed) or drift, 2 usage / IO error. Python 3.9+, stdlib only.
"""
import argparse
import csv
import hashlib
import json
import os
import re
import sys

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
                  "MERGE_FISH", "CLEAR_DUNGEON", "DAILIES_COMPLETED", "WEEKLIES_COMPLETED", "REACH_STAGE",
                  "REACH_HERO_LEVEL", "OWN_WEAPONS", "OWN_SKILLS", "OWN_COMPANIONS", "LIFETIME_MERGES", "FISH_LEVEL",
                  "AWAKEN_RANK", "LIFETIME_SUMMONS", "WATCH_AD"],
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
}

POSITIVE = ["TICK_HZ", "ATTACK_INTERVAL_TICKS", "BOSS_TIME_TICKS", "KILLS_PER_STAGE", "BOSS_EVERY", "CHAPTER_WIDTH",
            "STAGE_CAP", "TAP_ZAP_MAX_PER_SEC", "DAY_SECONDS", "OFFLINE_MIN_S", "SKILL_SLOTS", "COMPANION_SLOTS",
            "RUNE_SLOTS", "FISH_BOARD_W", "FISH_BOARD_H", "FISH_MAX_LVL0", "ATTACK_PROGRESS_UNIT",
            "LIMIT_BREAK_RANKS", "BASE_ATK", "BASE_HP", "BOSS_HP_MULT", "BOSS_GOLD_MULT", "OFFLINE_CAP_H",
            "DUPE_LVL_BONUS", "MP_POOL0", "MP_REGEN0"]
NON_NEGATIVE = ["SPAWN_DELAY_TICKS", "REF_DAILIES_DONE", "REF_NEW_BOSSES"]
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
                                     TIER, NAME, DESC, c("icon", "asset")], "id", required=False, todo=158),
    "fish_levels": T("FishLevelRowDto", [c("level", "int"), c("discovery_coins", "long"), c("missile_pct", "double"),
                                         c("sprite", "asset"), c("tint", "color", True), NAME], "level"),
    "tank": T("TankRowDto", [c("id", "id"), c("cap0", "int"), c("refill_s0", "double"), c("release_level", "int"),
                             c("refill_floor_s", "double")], "id"),
    "quests_daily": T("QuestRowDto", [c("id", "id"), c("goal", "enum(QuestGoal)"), c("filter", "string", True),
                                      c("target", "long"), c("reward_currency", "enum(Currency)"),
                                      c("reward_amount", "long"), c("sort_order", "int"), TIER, NAME,
                                      c("icon", "asset")], "id", required=False, todo=170),
    "quests_weekly": T("QuestRowDto", [c("id", "id"), c("goal", "enum(QuestGoal)"), c("filter", "string", True),
                                       c("target", "long"), c("reward_currency", "enum(Currency)"),
                                       c("reward_amount", "long"), c("sort_order", "int"), TIER, NAME,
                                       c("icon", "asset")], "id", required=False, todo=170),
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


def validate(tables, errs):
    check_constants(tables, errs)
    resolve_refs(tables, errs)
    # TODO(#122): the table and cross-table rules of 09 section 8.4 and the warnings of 8.6.


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


def build(src):
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
    if not errs:
        validate(tables, errs)
    if errs:
        return None, errs

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
        "content_version": meta["content_version"],
        "min_client_version": meta["min_client_version"],
        "source_revision": meta["source_revision"],
        "hot_apply": False,   # TODO(#123): computed against --previous from HOT_APPLY_TABLES
        "tables": entries,
        "localization": loc_entries,
        "content_hash": content_hash,
        "s_constants": s_constants,
        "notes": meta.get("notes", ""),
    }
    files["manifest.json"] = dumps(manifest).encode("utf-8")
    return (files, tables, manifest), errs


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
    lines += ["}", ""]
    return "\n".join(lines).encode("utf-8")


def check_dto_names(errs):
    for name, schema in SCHEMA.items():
        cols = [x[0] for x in schema["columns"]] + list(schema["folds"])
        for col in cols:
            if newtonsoft_snake(pascal(col)) != col:
                errs.add("E003", name, f"unknown column '{col}' (its C# field {pascal(col)} does not snake-case back)")


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


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("verb", choices=["build", "check", "gen-dtos"])
    ap.add_argument("--src", default="content_src")
    ap.add_argument("--out", default="content")
    ap.add_argument("--dto-out", default=DTO_PATH)
    args = ap.parse_args(argv)

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
    result, errs = build(args.src)
    if errs:
        for e in errs:
            print(e, file=sys.stderr)
        return 1
    files, tables, manifest = result

    if args.verb == "check":
        stale = []
        for fname, data in files.items():
            p = os.path.join(args.out, fname)
            if not os.path.exists(p) or open(p, "rb").read() != data:
                stale.append(fname)
        if os.path.isdir(args.out):
            stale += [f"{f} (not produced by the build)" for f in sorted(os.listdir(args.out))
                      if f.endswith(".json") and f not in files]
        if not os.path.exists(args.dto_out) or open(args.dto_out, "rb").read() != dto:
            stale.append(args.dto_out)
        if stale:
            print(f"{args.out}/ is stale (run python3 tools/export_content.py build): " + ", ".join(sorted(stale)),
                  file=sys.stderr)
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
    os.makedirs(os.path.dirname(args.dto_out), exist_ok=True)
    with open(args.dto_out, "wb") as f:
        f.write(dto)
    print_summary(tables, manifest)
    return 0


if __name__ == "__main__":
    sys.exit(main())
