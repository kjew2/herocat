#!/usr/bin/env python3
"""Unit tests for tools/export_content.py (issue #121). Run: python3 tools/test_export_content.py (check.sh does)."""
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_content as ex  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, "content_src")

# 09 section 4: where every 00 section 6 entry lives, as (table, column). A constants row is ("constants", KEY).
SECTION_4 = [
    ("constants", "TICK_HZ"), ("constants", "BASE_ATK"), ("constants", "BASE_HP"),
    ("constants", "ATTACK_INTERVAL_TICKS"), ("curves", "base"), ("curves", "growth"),
    ("constants", "KILLS_PER_STAGE"), ("constants", "SPAWN_DELAY_TICKS"), ("constants", "BOSS_EVERY"),
    ("constants", "BOSS_HP_MULT"), ("constants", "BOSS_TIME_TICKS"), ("constants", "BOSS_GOLD_MULT"),
    ("constants", "CHAPTER_WIDTH"), ("constants", "STAGE_CAP"), ("constants", "TAP_ZAP_DMG_PCT"),
    ("constants", "TAP_ZAP_MAX_PER_SEC"), ("stats", "per_lvl"), ("training_curves", "cost0"),
    ("training_curves", "cost_g"), ("constants", "TRAIN_BULK_STEPS"), ("constants", "DIAMONDS_PER_DAY_F2P"),
    ("banners", "pull_cost_diamonds"), ("constants", "DAY_SECONDS"), ("constants", "OFFLINE_RATE"),
    ("constants", "OFFLINE_CAP_H"), ("constants", "OFFLINE_MIN_S"), ("tutorial_steps", "grants"),
    ("constants", "LOGIN_DIAMONDS"), ("banner_levels", "rate_c"), ("banner_levels", "rate_m"),
    ("banners", "pity_soft_start"), ("banners", "pity_soft_step"), ("banners", "pity_hard"),
    ("banners", "m_share_of_lplus"), ("banners", "guarantee_batch_size"), ("banners", "guarantee_min_rarity"),
    ("banners", "exp_per_pull"), ("constants", "SKILL_SLOTS"), ("constants", "COMPANION_SLOTS"),
    ("constants", "RUNE_SLOTS"), ("dupe_levels", "cum_copies"), ("constants", "DUPE_LVL_BONUS"),
    ("constants", "LIMIT_BREAK_RANKS"), ("rarities", "limit_break_cost"), ("rarities", "dupe_overflow_rock_candy"),
    ("banners", "pool_c"), ("rarities", "weapon_passive_pct"), ("weapons", "passive_pct"),
    ("constants", "FISH_BOARD_W"), ("constants", "FISH_BOARD_H"), ("tank", "cap0"), ("tank", "refill_s0"),
    ("constants", "FISH_MAX_LVL0"), ("fish_levels", "discovery_coins"), ("constants", "FISH_MERGE_COINS_PCT"),
    ("fish_levels", "missile_pct"), ("constants", "FISH_BOARD_DPS_FACTOR"), ("awakening", "stage_gate"),
    ("awakening", "rock_candy_cost"), ("awakening", "mult"), ("hero_levels", "exp0"), ("hero_levels", "exp_g"),
    ("hero_levels", "atk_pct_per_level"), ("hero_levels", "mult_per_level"), ("progress_targets", "day"),
    ("progress_targets", "stage"), ("feature_unlocks", "condition"), ("feature_unlocks", "threshold"),
    ("awakening_targets", "max_days_after_gate"), ("plausibility_stage_bound", "stage"),
    ("constants", "REF_DAILIES_DONE"), ("constants", "REF_NEW_BOSSES"),
]


def build(src=SRC):
    result, errs = ex.build(src)
    return result, list(errs)


class ExporterTests(unittest.TestCase):
    def test_build_is_deterministic_and_check_passes(self):
        (a, _, _), e1 = build()
        (b, _, _), e2 = build()
        self.assertEqual(e1, [])
        self.assertEqual(e2, [])
        self.assertEqual(a, b)
        self.assertEqual(ex.main(["check"]), 0)

    def test_required_constants_equal_constants_json_keys(self):
        (_, tables, _), _ = build()
        self.assertEqual({r["key"] for r in tables["constants"]}, set(ex.REQUIRED_CONSTANTS))

    def test_every_section_4_entry_has_a_schema_column(self):
        for table, col in SECTION_4:
            if table == "constants":
                self.assertIn(col, ex.REQUIRED_CONSTANTS, col)
            else:
                self.assertIn(col, [c[0] for c in ex.SCHEMA[table]["columns"]], f"{table}.{col}")

    def test_dto_field_names_snake_case_back_to_columns(self):
        self.assertEqual(ex.newtonsoft_snake("XOffset"), "x_offset")
        self.assertEqual(ex.newtonsoft_snake("RefillS0"), "refill_s0")
        self.assertEqual(ex.newtonsoft_snake("MShareOfLplus"), "m_share_of_lplus")
        errs = ex.Errors()
        ex.check_dto_names(errs)
        self.assertEqual(list(errs), [])


class ErrorCodeTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.src = os.path.join(self.dir, "src")
        shutil.copytree(SRC, self.src)

    def tearDown(self):
        shutil.rmtree(self.dir)

    def edit(self, table, old, new):
        p = os.path.join(self.src, table + ".csv")
        s = open(p, encoding="utf-8").read()
        self.assertIn(old, s)
        open(p, "w", encoding="utf-8").write(s.replace(old, new, 1))

    def codes(self):
        _, errs = build(self.src)
        return [e.split()[1] for e in errs]

    def test_content_version_formats(self):
        for ok in ("2026.11.03", "2026.11.03.2"):
            self.edit("meta", "2026.11.01", ok)
            self.assertEqual(self.codes(), [])
            self.edit("meta", ok, "2026.11.01")
        self.edit("meta", "2026.11.01", "1.0")
        _, errs = build(self.src)
        self.assertEqual(errs, ["CONTENT E094 meta[content_version]: content_version '1.0' must be YYYY.MM.DD or YYYY.MM.DD.N"])

    def test_structural_codes(self):
        self.edit("curves", "enemy_hp,20,1.04", "enemy_hp,1;04,1.04")
        self.edit("dupe_levels", "2,3", "1,3")
        self.edit("stats", "ATK,true", "ATTACK,true")
        self.edit("rarities", "#9E9E9E", "#9e9e9e")
        self.edit("weapons", "weapon_c_02,", "Weapon_C_02,")
        self.assertEqual(sorted(self.codes()), sorted(["E010", "E020", "E021", "E070", "E071"]))

    def test_unresolved_reference_is_E022(self):
        self.edit("training_curves", "CRIT_RATE,50", "ATTACK,50")
        self.edit("tutorial_steps", "WEAPON:weapon_c_01:1", "WEAPON:weapon_c_99:1")
        _, errs = build(self.src)
        self.assertEqual(errs, ["CONTENT E022 training_curves[ATTACK].stat: 'ATTACK' does not exist in stats",
                                "CONTENT E022 tutorial_steps[tut_welcome].grants: 'weapon_c_99' does not exist in weapons"])

    def test_missing_unknown_columns_and_tables(self):
        os.remove(os.path.join(self.src, "tank.csv"))
        self.edit("dupe_levels", "level,cum_copies", "level,cum_copies,extra")
        self.edit("hero_levels", "id,exp0", "id,exp0x")
        codes = self.codes()
        self.assertIn("E001", codes)
        self.assertIn("E003", codes)
        self.assertIn("E002", codes)

    def test_empty_required_cell_is_E011(self):
        self.edit("tank", "default,10,", "default,,")
        _, errs = build(self.src)
        self.assertEqual(errs, ["CONTENT E011 tank[default].cap0: required cell is empty"])

    def test_constants_rules(self):
        self.edit("constants", "TICK_HZ,int,10", "TICK_HZ,double,10")
        self.edit("constants", "BOSS_EVERY,int,10", "BOSS_EVERY,int,0")
        self.edit("constants", "LOGIN_DIAMONDS,int,50", "LOGIN_GEMS,int,50")
        self.edit("constants", "TRAIN_BULK_STEPS,intlist,1|10|100", "TRAIN_BULK_STEPS,intlist,1|100|10")
        self.assertEqual(sorted(self.codes()), ["E040", "E041", "E042", "E043", "E043"])


    def test_expect_event_is_a_pipe_list_of_known_events(self):   # 09 section 5.29, issue #246 item 1
        (_, tables, _), errs = build(self.src)
        self.assertEqual(errs, [])
        boss = [r for r in tables["tutorial_steps"] if r["id"] == "tut_boss"][0]
        self.assertEqual(boss["expect_event"], "BossCleared|BossFailed")
        self.edit("tutorial_steps", "BossCleared|BossFailed", "BossCleared|BossLost")
        _, errs = build(self.src)
        self.assertEqual(len(errs), 1)
        self.assertTrue(errs[0].startswith("CONTENT E021 tutorial_steps[tut_boss].expect_event: 'BossLost' is not a valid Event"),
                        errs[0])

if __name__ == "__main__":
    unittest.main(verbosity=1)
