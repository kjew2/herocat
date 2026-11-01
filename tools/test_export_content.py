#!/usr/bin/env python3
"""Unit tests for tools/export_content.py (issues #121-#124, #128) and the content fixtures in tests/content_fixtures/.
Run: python3 tools/test_export_content.py (check.sh does, --no-dotnet included)."""
import json
import os
import re
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_content as ex  # noqa: E402
import content_fixtures as fx  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, "content_src")
# The shipped content_version (meta.csv), read rather than hard-coded so a version bump does not break these tests.
VERSION = re.search(r"^content_version,([^\s,]+)", open(os.path.join(SRC, "meta.csv"), encoding="utf-8").read(), re.M).group(1)

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
            self.edit("meta", "content_version," + VERSION + "\n", "content_version," + ok + "\n")
            self.assertEqual(self.codes(), [])
            self.edit("meta", "content_version," + ok + "\n", "content_version," + VERSION + "\n")
        self.edit("meta", "content_version," + VERSION + "\n", "content_version,1.0\n")
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


# Codes with no fixture yet, and why. Everything else in 09 section 8 (E0xx-E09x) must have a bad_<code>_* fixture.
NO_FIXTURE = {
    "E076": "import-translations (13.2) is not built yet; E076 lands with it",
}


class FixtureTests(unittest.TestCase):
    """09 acceptance 6, 7, 14 and 21: every fixture's first stderr line(s) equal its expected.txt."""

    def test_good_minimal_builds_clean_with_strict_warnings(self):
        with tempfile.TemporaryDirectory() as tmp:
            rc, lines = fx.run_main(["build", "--src", fx.BASE, "--out", tmp, "--dto-out", os.path.join(tmp, "d.cs"),
                                     "--strict-warnings"])
        self.assertEqual((rc, lines), (0, []))

    def test_every_fixture_matches_expected(self):
        for name in fx.names():
            with self.subTest(fixture=name):
                rc, lines = fx.run(name)
                want = fx.expected(name)
                self.assertEqual(lines[:len(want)], want)
                self.assertEqual(rc, 1, "an error, or a warning under --strict-warnings, exits 1")

    def test_three_defects_report_in_table_order(self):   # acceptance 7
        self.assertEqual([ln.split()[1] for ln in fx.expected("three_defects")], ["E043", "E030", "E061"])

    def test_every_error_code_in_section_8_has_a_fixture(self):   # acceptance 6
        with open(os.path.join(REPO, "specs", "09-content-data-pipeline.md"), encoding="utf-8") as f:
            spec = f.read()
        section8 = spec[spec.index("## 8. Validator checklist"):spec.index("## 9. ")]
        codes = sorted(set(re.findall(r"\| `(E0\d\d)`", section8)))
        self.assertGreater(len(codes), 40)
        have = {n.split("_")[1] for n in fx.names() if n.startswith("bad_")}
        missing = [c for c in codes if c not in have and c not in NO_FIXTURE]
        self.assertEqual(missing, [], "add tests/content_fixtures/bad_<code>_<name>/ or list the code in NO_FIXTURE")
        for c in NO_FIXTURE:
            self.assertNotIn(c, have, f"{c} has a fixture now; drop it from NO_FIXTURE")

    def test_strict_warnings_turns_w021_into_exit_1(self):   # acceptance 14
        with tempfile.TemporaryDirectory() as tmp:
            src, args = fx.materialize("warn_W021_unused_ui_key", tmp)
            base = ["build", "--src", src, "--out", os.path.join(tmp, "o"), "--dto-out", os.path.join(tmp, "d.cs")]
            lax = [a for a in args if a != "--strict-warnings"]
            rc, lines = fx.run_main(base + lax)
            self.assertEqual(rc, 0)
            self.assertTrue(lines[0].startswith("CONTENT W021"), lines)
            rc, _ = fx.run_main(base + args)
            self.assertEqual(rc, 1)


class VersioningTests(unittest.TestCase):
    """09 sections 6.2, 6.3; acceptance 11, 15 (issue #123)."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.prev = os.path.join(self.tmp, "prev")
        rc, lines = fx.run_main(["build", "--src", fx.BASE, "--out", self.prev, "--dto-out", os.path.join(self.tmp, "d.cs")])
        self.assertEqual(rc, 0, lines)
        self.src = os.path.join(self.tmp, "src")
        shutil.copytree(fx.BASE, self.src)

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def edit(self, table, old, new):
        fx.apply_edits(self.src, {"edits": [[table, old, new]]})

    def bump(self):
        self.edit("meta", "content_version,2026.11.01", "content_version,2026.11.02")

    def build(self):
        result, errs = ex.build(self.src, previous=ex.load_previous(self.prev))
        return result, list(errs)

    def test_identical_rebuild_passes_and_keeps_hot_apply(self):
        (files, _, manifest), errs = self.build()
        self.assertEqual(errs, [])
        self.assertFalse(manifest["hot_apply"])
        with open(os.path.join(self.prev, "manifest.json"), "rb") as f:
            self.assertEqual(files["manifest.json"], f.read())

    def test_appending_a_pool_id_keeps_indices(self):   # acceptance 11 (the insert half is bad_E090_pool_index_shift)
        self.bump()
        fx.apply_edits(self.src, {"append": [["skills", "skill_c_15,COMMON,10.0,INSTANT,80,,,M0,skill.c_01.name,"
                                              "skill.c_01.desc,icon_skill_c_15,vfx_skill_c_15"]]})
        self.edit("banners", "TICKET_SUMMON_SKILL,1,1,1,1,1", "TICKET_SUMMON_SKILL,2,1,1,1,1")
        (_, tables, _), errs = self.build()
        self.assertEqual(errs, [])
        pools = ex.pool_lists(tables, tables["banners"])
        self.assertEqual(pools[("SKILL", "COMMON")], ["skill_c_01", "skill_c_15"])

    def test_hot_apply_only_for_allowlisted_files(self):   # acceptance 15
        self.bump()
        self.edit("localization", "awakening.rank_1.name,Awakened 1,Awakened 1,", "awakening.rank_1.name,Awakened 1,Kakusei 1,")
        (_, _, manifest), errs = self.build()
        self.assertEqual(errs, [])
        self.assertTrue(manifest["hot_apply"], "only localization.ja.json changed")
        self.edit("skills", "skill_c_01,COMMON,10.0,", "skill_c_01,COMMON,10.5,")
        (_, _, manifest), errs = self.build()
        self.assertEqual(errs, [])
        self.assertFalse(manifest["hot_apply"], "a skills row changed")
        self.assertEqual(sorted(ex.HOT_APPLY_TABLES), ["inbox_templates", "localization", "shop_packages", "shop_products"])

    def test_version_order_matches_content_version_compare(self):   # the vectors of ContentLoaderTests
        k = ex.version_key
        self.assertLess(k("2026.11.03"), k("2026.11.03.1"))
        self.assertLess(k("2026.11.03.2"), k("2026.11.03.10"))
        self.assertGreater(k("2026.12.01"), k("2026.11.30.9"))
        self.assertEqual(k("2026.11.03"), k("2026.11.03"))

    def test_check_without_previous_keeps_the_committed_hot_apply(self):
        out = os.path.join(self.tmp, "out")
        shutil.copytree(self.prev, out)
        p = os.path.join(out, "manifest.json")
        with open(p, encoding="utf-8") as f:
            doc = json.load(f)
        doc["hot_apply"] = True
        with open(p, "w", encoding="utf-8") as f:
            f.write(ex.dumps(doc))
        dto = os.path.join(self.tmp, "d.cs")
        with open(dto, "wb") as f:
            f.write(ex.gen_dtos())
        rc, lines = fx.run_main(["check", "--src", fx.BASE, "--out", out, "--dto-out", dto])
        self.assertEqual(rc, 0, lines)

    def test_diff_lists_row_changes(self):
        self.bump()
        self.edit("skills", "skill_c_01,COMMON,10.0,", "skill_c_01,COMMON,10.5,")
        out = os.path.join(self.tmp, "new")
        rc, lines = fx.run_main(["build", "--src", self.src, "--out", out, "--dto-out", os.path.join(self.tmp, "d.cs")])
        self.assertEqual(rc, 0, lines)
        lines = ex.diff_dirs(self.prev, out)
        self.assertEqual(lines[0], "content 2026.11.01 -> 2026.11.02")
        self.assertIn("skills:", lines)
        self.assertIn("  ~ skill_c_01: dps_pct 10.0 -> 10.5", lines)


class IcuTests(unittest.TestCase):
    """The ICU subset E075 parses (09 section 13.2) and E074's rendered length."""

    def test_parse_plural_select_number(self):
        parts = ex.icu_parse("You have {n, plural, one {# fish} other {# fish}} in {t, select, big {the big} other {a}} tank")
        plurals = ex.plural_args(parts)
        self.assertEqual(len(plurals), 1)
        self.assertEqual(sorted(plurals[0][2]), ["one", "other"])
        self.assertEqual(ex.icu_parse("{gold, number}")[0], ("gold", "number", {}))
        self.assertEqual(ex.icu_parse("it''s '{literal}'"), ["it's {literal}"])

    def test_malformed_messages_raise(self):
        for bad in ("{n, plural, one {x}", "{n, plural, one}", "oops }", "{n, date}"):
            with self.assertRaises(ValueError, msg=bad):
                ex.icu_parse(bad)

    def test_rendered_length_takes_the_longest_branch(self):
        self.assertEqual(ex.rendered_len(ex.icu_parse("{n, plural, one {# fish} other {# fishes}}")), 8)


if __name__ == "__main__":
    unittest.main(verbosity=1)
