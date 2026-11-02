#!/usr/bin/env python3
"""Unit tests for tools/export_content.py (issues #121-#124, #128, #132, #250) and the content fixtures in tests/content_fixtures/.
Run: python3 tools/test_export_content.py (check.sh does, --no-dotnet included)."""
import json
import math
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

    def test_enums_mirror_the_sim_enums(self):   # Enums.cs says ENUMS mirrors it; Locale/ConstantType are tool-only
        src = open(os.path.join(REPO, "src", "HeroCat.Sim", "Content", "Enums.cs"), encoding="utf-8").read()
        src = re.sub(r"//[^\n]*|/\*.*?\*/", "", src, flags=re.S)
        sim = {m.group(1): [v.split("=")[0].strip() for v in m.group(2).split(",") if v.strip()]
               for m in re.finditer(r"public enum (\w+)(?:\s*:\s*\w+)?\s*\{([^}]*)\}", src)}
        self.assertEqual(set(ex.ENUMS) - set(sim), {"Locale", "ConstantType"})
        for name, members in sim.items():
            self.assertEqual(ex.ENUMS.get(name), members, name)

    def test_quest_goal_has_train_levels(self):   # 06 section 5.1 order; weekly w_train uses it (issue #256)
        goals = ex.ENUMS["QuestGoal"]
        self.assertEqual(goals.index("TRAIN_LEVELS"), goals.index("CLEAR_DUNGEON") + 1)
        errs = ex.Errors()
        self.assertEqual(ex.parse_cell("enum(QuestGoal)", "TRAIN_LEVELS", "quests_weekly[w_train].goal", errs),
                         "TRAIN_LEVELS")
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
        # Edits the whole value, whatever content_src's current version is (a same-day bump adds a ".N").
        with open(os.path.join(self.src, "meta.csv"), encoding="utf-8") as f:
            cur = "content_version," + re.search(r"^content_version,(.*)$", f.read(), re.M).group(1).strip()
        for ok in ("2026.11.03", "2026.11.03.2"):
            self.edit("meta", cur, "content_version," + ok)
            self.assertEqual(self.codes(), [])
            self.edit("meta", "content_version," + ok, cur)
        self.edit("meta", cur, "content_version,1.0")
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
NO_FIXTURE = {}   # code -> why it has no fixture yet (every section 8 code has one since issue #213's E076)


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

    def two_common_skills_previous(self, c02_tier="M0"):
        """Rebuilds the previous build with Pool(SKILL, COMMON) = [skill_c_01, skill_c_02]; the current src matches it."""
        fx.apply_edits(self.src, {"edits": [["banners", "TICKET_SUMMON_SKILL,1,1,1,1,1", "TICKET_SUMMON_SKILL,2,1,1,1,1"]],
                                  "append": [["skills", f"skill_c_02,COMMON,10.0,INSTANT,80,,,{c02_tier},skill.c_01.name,"
                                              "skill.c_01.desc,icon_skill_c_02,vfx_skill_c_02"]]})
        shutil.rmtree(self.prev)
        rc, lines = fx.run_main(["build", "--src", self.src, "--out", self.prev, "--dto-out", os.path.join(self.tmp, "d.cs")])
        self.assertEqual(rc, 0, lines)

    def test_pool_lists_filter_by_tier_like_content_db(self):   # issue #250
        self.two_common_skills_previous(c02_tier="MVP")
        prev = ex.load_previous(self.prev)["rows"]
        self.assertEqual(ex.pool_lists(prev, prev["banners"], "M0")[("SKILL", "COMMON")], ["skill_c_01"])
        self.assertEqual(ex.pool_lists(prev, prev["banners"], "MVP")[("SKILL", "COMMON")], ["skill_c_01", "skill_c_02"])
        self.assertNotIn(("COMPANION", "COMMON"), ex.pool_lists(prev, prev["banners"], "M0"), "an MVP banner is off in M0")

    def test_retiring_the_last_pool_row_by_tier_passes(self):   # issue #250: retire by tier only at the end of a pool
        self.two_common_skills_previous()
        self.bump()
        self.edit("skills", "skill_c_02,COMMON,10.0,INSTANT,80,,,M0,", "skill_c_02,COMMON,10.0,INSTANT,80,,,P2,")
        self.edit("banners", "TICKET_SUMMON_SKILL,2,1,1,1,1", "TICKET_SUMMON_SKILL,1,1,1,1,1")
        (_, tables, _), errs = self.build()
        self.assertEqual(errs, [])
        self.assertEqual(ex.pool_lists(tables, tables["banners"], "MVP")[("SKILL", "COMMON")], ["skill_c_01"])

    def test_retiring_a_mid_pool_row_by_tier_is_E090(self):   # issue #250 (also bad_E090_pool_retired_by_tier)
        self.two_common_skills_previous()
        self.bump()
        self.edit("skills", "skill_c_01,COMMON,10.0,INSTANT,80,,,M0,", "skill_c_01,COMMON,10.0,INSTANT,80,,,P2,")
        self.edit("banners", "TICKET_SUMMON_SKILL,2,1,1,1,1", "TICKET_SUMMON_SKILL,1,1,1,1,1")
        _, errs = self.build()
        self.assertEqual(len(errs), 1, errs)
        self.assertTrue(errs[0].startswith("CONTENT E090 skills: skills: 'skill_c_02' in Pool(SKILL, COMMON) of previous "
                                           "build 2026.11.01 moved (M0 pool index 1 -> 0; MVP pool index 1 -> 0)"), errs[0])

    def test_a_tier_change_that_shifts_only_the_m0_pool_is_E090(self):   # issue #250: every shippable tier is checked
        self.two_common_skills_previous()
        self.bump()
        self.edit("skills", "skill_c_01,COMMON,10.0,INSTANT,80,,,M0,", "skill_c_01,COMMON,10.0,INSTANT,80,,,MVP,")
        _, errs = self.build()
        self.assertEqual(len(errs), 1, errs)
        self.assertIn("'skill_c_02' in Pool(SKILL, COMMON) of previous build 2026.11.01 moved (M0 pool index 1 -> 0)", errs[0])
        self.assertNotIn("MVP pool index", errs[0], "the MVP pool is unchanged")

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


class FakeSheets:
    """The slice of googleapiclient's Sheets v4 resource pull calls, answering from the recorded responses."""

    def __init__(self, recorded):
        self.recorded, self.calls = recorded, []

    def spreadsheets(self):
        return self

    def values(self):
        return self

    def get(self, **kw):
        self.calls.append(("get", kw))
        return FakeRequest(self.recorded["get"])

    def batchGet(self, **kw):
        self.calls.append(("batchGet", kw))
        return FakeRequest(self.recorded["batchGet"])


class FakeRequest:
    def __init__(self, doc):
        self.doc = doc

    def execute(self):
        return json.loads(json.dumps(self.doc))


def gviz_csv(path):
    """A good_minimal CSV the way the gviz endpoint answers: every cell quoted, CRLF-free, UTF-8."""
    import csv
    import io
    with open(path, newline="", encoding="utf-8") as f:
        rows = list(csv.reader(f))
    buf = io.StringIO()
    csv.writer(buf, quoting=csv.QUOTE_ALL, lineterminator="\n").writerows(rows)
    return buf.getvalue().encode("utf-8")


class PullTests(unittest.TestCase):
    """`pull` (09 sections 3.1, 7.1, 12; issue #132): no network, recorded batchGet responses."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.src = os.path.join(self.tmp, "content_src")
        with open(fx.PULL_FIXTURE, encoding="utf-8") as f:
            self.recorded = json.load(f)

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def snapshot(self):
        out = {}
        for f in sorted(os.listdir(self.src)):
            with open(os.path.join(self.src, f), "rb") as fh:
                out[f] = fh.read()
        return out

    def test_service_account_pull_writes_csvs_build_accepts_and_repulls_byte_identical(self):
        svc = FakeSheets(self.recorded)
        written, removed, skipped = ex.pull(fx.PULL_SHEET_ID, self.src, service_account="unused.json", service=svc)
        self.assertEqual(skipped, ["README"])
        self.assertEqual(removed, [])
        self.assertEqual(svc.calls[1][1]["valueRenderOption"], "FORMATTED_VALUE")
        self.assertNotIn("'README'", svc.calls[1][1]["ranges"], "only table tabs are fetched")
        first = self.snapshot()
        base = sorted(f for f in os.listdir(fx.BASE) if f.endswith(".csv"))
        self.assertEqual(sorted(first), base)
        for f in base:
            with open(os.path.join(fx.BASE, f), "rb") as fh:
                self.assertEqual(first[f], fh.read(),
                                 f"{f}: pull differs from good_minimal (python3 tools/content_fixtures.py record-pull)")
        result, errs = ex.build(self.src)
        self.assertEqual(list(errs), [])
        self.assertIsNotNone(result)
        written2, removed2, _ = ex.pull(fx.PULL_SHEET_ID, self.src, service_account="unused.json",
                                        service=FakeSheets(self.recorded))
        self.assertEqual((written2, removed2), ([], []))
        self.assertEqual(self.snapshot(), first)

    def test_pull_removes_the_csv_of_a_table_the_workbook_dropped(self):
        os.makedirs(self.src)
        for name in ("runes.csv", "notes.txt"):
            with open(os.path.join(self.src, name), "w", encoding="utf-8") as f:
                f.write("stale\n")
        _, removed, _ = ex.pull(fx.PULL_SHEET_ID, self.src, service_account="x", service=FakeSheets(self.recorded))
        self.assertEqual(removed, ["runes"])
        self.assertTrue(os.path.exists(os.path.join(self.src, "notes.txt")), "files that are not tables stay")

    def test_service_account_without_the_google_client_exits_2_with_an_install_hint(self):
        saved = {m: sys.modules.get(m) for m in ("googleapiclient", "googleapiclient.discovery", "google.oauth2")}
        for m in saved:
            sys.modules[m] = None   # makes `import` raise ImportError even where the client is installed
        try:
            rc, lines = fx.run_main(["pull", "--sheet", "abc", "--service-account", "creds.json", "--src", self.src])
        finally:
            for m, v in saved.items():
                if v is None:
                    sys.modules.pop(m, None)
                else:
                    sys.modules[m] = v
        self.assertEqual(rc, 2)
        self.assertIn("pip install google-api-python-client", lines[0])
        self.assertFalse(os.path.exists(self.src), "nothing written")

    def test_adc_pull_signs_in_with_default_credentials_and_writes_identical_csvs(self):
        import types
        seen = {}
        auth = types.ModuleType("google.auth")
        auth.exceptions = types.ModuleType("google.auth.exceptions")
        auth.exceptions.DefaultCredentialsError = type("DefaultCredentialsError", (Exception,), {})
        auth.default = lambda scopes: (seen.setdefault("scopes", scopes) and "adc-creds", "proj")
        discovery = types.ModuleType("googleapiclient.discovery")
        discovery.build = lambda api, ver, credentials, cache_discovery: (
            seen.setdefault("creds", credentials) and FakeSheets(self.recorded))
        oauth2 = types.ModuleType("google.oauth2")
        oauth2.service_account = types.ModuleType("google.oauth2.service_account")
        fakes = {"google.auth": auth, "google.auth.exceptions": auth.exceptions, "google.oauth2": oauth2,
                 "google.oauth2.service_account": oauth2.service_account,
                 "googleapiclient": types.ModuleType("googleapiclient"), "googleapiclient.discovery": discovery}
        google = sys.modules.get("google") or types.ModuleType("google")
        fakes["google"] = google
        saved = {m: sys.modules.get(m) for m in fakes}
        saved_attrs = {a: getattr(google, a, None) for a in ("auth", "oauth2")}
        sys.modules.update(fakes)
        google.auth, google.oauth2 = auth, oauth2
        try:
            rc, lines = fx.run_main(["pull", "--sheet", fx.PULL_SHEET_ID, "--adc", "--src", self.src])
        finally:
            for m, v in saved.items():
                if v is None:
                    sys.modules.pop(m, None)
                else:
                    sys.modules[m] = v
            for a, v in saved_attrs.items():
                if v is None:
                    delattr(google, a)
                else:
                    setattr(google, a, v)
        self.assertEqual(rc, 0, lines)
        self.assertEqual(seen, {"scopes": [ex.SHEETS_SCOPE], "creds": "adc-creds"})
        for f, data in self.snapshot().items():
            with open(os.path.join(fx.BASE, f), "rb") as fh:
                self.assertEqual(data, fh.read(), f)

    def test_adc_without_the_google_client_exits_2_with_an_install_hint(self):
        saved = {m: sys.modules.get(m) for m in ("google.auth", "googleapiclient", "googleapiclient.discovery")}
        for m in saved:
            sys.modules[m] = None
        try:
            rc, lines = fx.run_main(["pull", "--sheet", "abc", "--adc", "--src", self.src])
        finally:
            for m, v in saved.items():
                if v is None:
                    sys.modules.pop(m, None)
                else:
                    sys.modules[m] = v
        self.assertEqual(rc, 2)
        self.assertIn("pip install google-api-python-client", lines[0])
        self.assertFalse(os.path.exists(self.src), "nothing written")

    def test_pull_usage_errors_exit_2(self):
        self.assertEqual(fx.run_main(["pull", "--csv-export"])[0], 2)
        self.assertEqual(fx.run_main(["pull", "--sheet", "abc"])[0], 2)

    def test_csv_export_resolves_gviz_first_tab_fallback(self):
        bodies = {f[:-4]: gviz_csv(os.path.join(fx.BASE, f)) for f in os.listdir(fx.BASE) if f.endswith(".csv")}
        first = bodies["awakening"]   # gviz answers an unknown tab name with the first tab
        urls = []

        def get(url):
            urls.append(url)
            tab = url.rsplit("sheet=", 1)[1]
            return bodies.get(tab, first)

        written, removed, skipped = ex.pull("abc", self.src, csv_export=True, get=get)
        self.assertTrue(all(u.startswith("https://docs.google.com/spreadsheets/d/abc/gviz/tq?tqx=out:csv&headers=1&")
                            for u in urls))
        self.assertEqual(sorted(written), sorted(bodies))
        for f, data in self.snapshot().items():
            with open(os.path.join(fx.BASE, f), "rb") as fh:
                self.assertEqual(data, fh.read(), f)

    def test_csv_export_network_error_exits_2(self):
        import urllib.error

        def boom(url):
            raise urllib.error.URLError("no route to host")

        saved = ex.http_get
        ex.http_get = boom
        try:
            rc, lines = fx.run_main(["pull", "--sheet", "abc", "--csv-export", "--src", self.src])
        finally:
            ex.http_get = saved
        self.assertEqual(rc, 2)
        self.assertIn("no route to host", lines[0])

    def test_normalize_tab(self):
        rows = [["id", "a", "", ""], ["x", None], ["", ""], ["y", "2", "", ""], [], [""]]
        self.assertEqual(ex.normalize_tab(rows), [["id", "a"], ["x", ""], ["", ""], ["y", "2"]])
        self.assertEqual(ex.csv_bytes([["k", "v"], ["ship_locales", "en,ja"], ["n", 'say "hi"\nnow']]),
                         b'k,v\nship_locales,"en,ja"\nn,"say ""hi""\nnow"\n')


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


LOC_HEADER = "key,en,ja,ko,zh_hans,max_chars,plural,#context\n"


def write_loc(d, body):
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, "localization.csv"), "w", encoding="utf-8", newline="") as f:
        f.write(LOC_HEADER + body)


def read_text(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def translator_rows(text):
    import csv
    import io
    return list(csv.reader(io.StringIO(text)))


def write_rows(path, rows):
    import csv
    with open(path, "w", encoding="utf-8", newline="") as f:
        csv.writer(f, lineterminator="\n").writerows(rows)


class TranslatorRoundTripTests(unittest.TestCase):
    """09 sections 7.1, 13.2 and E076 (issue #213): translator-csv -> fill -> import-translations."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.src = os.path.join(self.tmp, "src")
        shutil.copytree(fx.BASE, self.src)
        self.loc = os.path.join(self.src, "localization.csv")
        text = read_text(self.loc)
        for key in ("awakening.rank_2.name", "awakening.rank_3.name"):   # two rows the translator still owes
            text = re.sub(rf"^{re.escape(key)},([^,]*),[^,]*,", rf"{key},\1,,", text, count=1, flags=re.M)
        with open(self.loc, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        self.before = text

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def export(self, missing_only=True, locale="ja"):
        out = os.path.join(self.tmp, f"strings_{locale}.csv")
        args = ["translator-csv", "--src", self.src, "--locale", locale, "--out", out]
        rc, lines = fx.run_main(args + (["--missing-only"] if missing_only else []))
        self.assertEqual((rc, lines), (0, []))
        return out, translator_rows(read_text(out))

    def import_(self, path, locale="ja"):
        return fx.run_main(["import-translations", "--src", self.src, "--csv", path, "--locale", locale])

    def test_round_trip_fills_missing_rows_and_build_passes(self):
        # Before the import the ship locale ja is incomplete, so the build fails E073.
        rc, lines = fx.run_main(["build", "--src", self.src, "--out", os.path.join(self.tmp, "o0"),
                                 "--dto-out", os.path.join(self.tmp, "d0.cs")])
        self.assertEqual(rc, 1)
        self.assertTrue(lines[0].startswith("CONTENT E073 localization[awakening.rank_2.name].ja"), lines)
        path, rows = self.export()
        self.assertEqual(rows[0], ["key", "en", "ja", "max_chars", "context"])
        self.assertEqual([r[0] for r in rows[1:]], ["awakening.rank_2.name", "awakening.rank_3.name"])
        self.assertEqual(rows[1], ["awakening.rank_2.name", "Awakened 2", "", "24", ""])
        rows[1][2], rows[2][2] = "覚醒 2", "覚醒 3"
        write_rows(path, rows)
        self.assertEqual(self.import_(path), (0, []))
        after = read_text(self.loc)
        self.assertEqual(after, self.before.replace("awakening.rank_2.name,Awakened 2,,", "awakening.rank_2.name,Awakened 2,覚醒 2,")
                         .replace("awakening.rank_3.name,Awakened 3,,", "awakening.rank_3.name,Awakened 3,覚醒 3,"))
        rc, lines = fx.run_main(["build", "--src", self.src, "--out", os.path.join(self.tmp, "o"),
                                 "--dto-out", os.path.join(self.tmp, "d.cs"), "--strict-warnings"])
        self.assertEqual((rc, lines), (0, []))
        with open(os.path.join(self.tmp, "o", "localization.ja.json"), encoding="utf-8") as f:
            ja = {r["key"]: r["text"] for r in json.load(f)["rows"]}
        self.assertEqual(ja["awakening.rank_2.name"], "覚醒 2")
        _, rows = self.export()
        self.assertEqual(rows[1:], [], "nothing is missing any more")

    def test_en_edited_between_export_and_import_is_E076_and_the_rest_merges(self):
        path, rows = self.export()
        rows[1][2], rows[2][2] = "覚醒 2", "覚醒 3"
        write_rows(path, rows)
        with open(self.loc, "w", encoding="utf-8", newline="") as f:
            f.write(self.before.replace("awakening.rank_2.name,Awakened 2,", "awakening.rank_2.name,Awakened Two,"))
        rc, lines = self.import_(path)
        self.assertEqual(rc, 1)
        self.assertEqual(lines, ["CONTENT E076 localization[awakening.rank_2.name].ja: en text changed since this "
                                 "translation was exported ('Awakened 2' -> 'Awakened Two')"])
        after = read_text(self.loc)
        self.assertIn("awakening.rank_2.name,Awakened Two,,", after)
        self.assertIn("awakening.rank_3.name,Awakened 3,覚醒 3,", after)

    def test_full_export_keeps_existing_text_and_empty_cells_never_clear(self):
        path, rows = self.export(missing_only=False)
        self.assertEqual(len(rows) - 1, len(re.findall(r"^\w", self.before, re.M)) - 1)
        self.assertEqual(rows[1], ["awakening.rank_1.name", "Awakened 1", "Awakened 1", "24", ""])
        for r in rows[1:]:
            r[2] = ""
        write_rows(path, rows)
        self.assertEqual(self.import_(path), (0, []))
        self.assertEqual(read_text(self.loc), self.before)

    def test_context_note_and_zh_hans_column(self):
        d = os.path.join(self.tmp, "loc")
        write_loc(d, 'ui.scr_main.claim,Claim,,,,18,false,"Offline popup, main button"\n')
        out = os.path.join(self.tmp, "zh.csv")
        self.assertEqual(fx.run_main(["translator-csv", "--src", d, "--locale", "zh-Hans", "--out", out])[0], 0)
        rows = translator_rows(read_text(out))
        self.assertEqual(rows, [["key", "en", "zh_hans", "max_chars", "context"],
                                ["ui.scr_main.claim", "Claim", "", "18", "Offline popup, main button"]])
        rows[1][2] = "领取"
        write_rows(out, rows)
        self.assertEqual(fx.run_main(["import-translations", "--src", d, "--csv", out, "--locale", "zh-Hans"]), (0, []))
        self.assertEqual(read_text(os.path.join(d, "localization.csv")),
                         LOC_HEADER + 'ui.scr_main.claim,Claim,,,领取,18,false,"Offline popup, main button"\n')

    def test_import_refuses_budget_plural_unknown_and_duplicate_rows(self):
        d = os.path.join(self.tmp, "loc")
        write_loc(d, "ui.a.short,Go,,,,4,false,\n"
                     "ui.a.fish,\"{n, plural, one {# fish} other {# fish}}\",,,,0,true,\n"
                     "ui.a.gold,{gold} gold,,,,0,false,\n")
        rows = [["key", "en", "ja", "max_chars", "context"],
                ["ui.a.short", "Go", "いってらっしゃい", "4", ""],                 # E074: 8 chars, budget 4
                ["ui.a.fish", "{n, plural, one {# fish} other {# fish}}", "{n, plural, one {#匹}}", "0", ""],   # E075
                ["ui.a.nope", "Nope", "いいえ", "0", ""],                          # E072
                ["ui.a.gold", "{gold} gold", "{gold} ゴールド", "0", ""],
                ["ui.a.gold", "{gold} gold", "{gold}G", "0", ""]]                 # E020
        path = os.path.join(self.tmp, "ja.csv")
        write_rows(path, rows)
        rc, lines = fx.run_main(["import-translations", "--src", d, "--csv", path, "--locale", "ja"])
        self.assertEqual(rc, 1)
        self.assertEqual(lines, [
            "CONTENT E074 localization[ui.a.short].ja: ja text is 8 chars, budget 4",
            "CONTENT E075 localization[ui.a.fish].ja: plural message must have 'one' and 'other' branches: ja lacks other",
            "CONTENT E072 translations row 3: key 'ui.a.nope' has no row in localization",
            "CONTENT E020 translations row 5: duplicate key 'ui.a.gold' (first seen at row 4)",
        ])
        text = read_text(os.path.join(d, "localization.csv"))
        self.assertIn("ui.a.gold,{gold} gold,{gold} ゴールド,", text)
        self.assertIn("ui.a.short,Go,,", text)
        # ja needs only the `other` branch (13.2): the same message without `one` is accepted.
        write_rows(path, [rows[0], ["ui.a.fish", rows[2][1], "{n, plural, other {#匹}}", "0", ""]])
        self.assertEqual(fx.run_main(["import-translations", "--src", d, "--csv", path, "--locale", "ja"]), (0, []))

    def test_usage_errors_exit_2(self):
        path = os.path.join(self.tmp, "x.csv")
        for argv in (["translator-csv", "--locale", "en", "--out", path], ["translator-csv", "--locale", "xx", "--out", path],
                     ["translator-csv", "--locale", "ja"], ["import-translations", "--locale", "ja"],
                     ["import-translations", "--locale", "ja", "--csv", os.path.join(self.tmp, "missing.csv")]):
            with self.subTest(argv=argv):
                self.assertEqual(fx.run_main(argv + ["--src", self.src])[0], 2)
        write_rows(path, [["key", "en", "max_chars"]])
        self.assertEqual(self.import_(path),
                         (1, ["CONTENT E002 translations: missing required column 'ja'"]))


def icu_shape(parts):
    """The argument structure of a parsed ICU message, literal text dropped."""
    return [(p[0], p[1], {k: icu_shape(v) for k, v in p[2].items()}) for p in parts if isinstance(p, tuple)]


class PseudoLocTests(unittest.TestCase):
    """09 section 13.3, 07 section 12 (issue #213): the pseudo locale xx."""

    SAMPLES = ["Test", "OK", "Awakened 1", "Claim {gold} gold", "{gold, number} per second",
               "You have {n, plural, one {# fish} other {# fish}} in {t, select, big {the big} other {a}} tank",
               "{n, plural, offset:1 =0 {nobody} one {just you} other {you and # others}}", "it''s '{literal}'",
               "a stray } brace is plain text"]

    def test_accents_brackets_and_padding(self):
        self.assertEqual(ex.pseudo_text("Test"), "[!!! Ţêšţ !!!]")   # 07 section 12's sample
        self.assertEqual(ex.pseudo_text("a e"), "[!!! å ê !!!]")
        long = "Release every fish in the tank"
        p = ex.pseudo_text(long)
        self.assertEqual(p, "[!!! Ŕêļêåšê êvêŕý fîšĥ îñ ţĥê ţåñķ~~ !!!]")   # 30 chars -> 42 = 30 + 40%
        self.assertEqual(len(p), 42)

    def test_icu_structure_kept_and_length_at_least_140_pct(self):
        for en in self.SAMPLES:
            with self.subTest(en=en):
                p = ex.pseudo_text(en)
                try:
                    want = ex.icu_parse(en)
                except ValueError:
                    self.assertEqual(len(p), len(en) + max(10, math.ceil(len(en) * 0.4)))
                    continue
                got = ex.icu_parse(p)
                self.assertEqual(icu_shape(got), icu_shape(want))
                self.assertGreaterEqual(ex.rendered_len(got), 1.4 * ex.rendered_len(want))
                self.assertTrue(p.startswith("[!!! ") and p.endswith(" !!!]"))
        self.assertIn("{n, plural, one {# fîšĥ} other {# fîšĥ}}", ex.pseudo_text(self.SAMPLES[5]))
        self.assertIn("'{ļîţêŕåļ}'", ex.pseudo_text(self.SAMPLES[7]))

    def test_pseudo_build_of_the_shipped_strings(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "content_pseudo")
            rc, lines = fx.run_main(["pseudo", "--src", SRC, "--locale", "xx", "--out", out])
            self.assertEqual((rc, lines), (0, []))
            with open(os.path.join(out, "localization.xx.json"), encoding="utf-8") as f:
                xx = json.load(f)
            with open(os.path.join(out, "localization.en.json"), encoding="utf-8") as f:
                en = json.load(f)
            with open(os.path.join(out, "manifest.json"), encoding="utf-8") as f:
                manifest = json.load(f)
            with open(os.path.join(REPO, "content", "manifest.json"), encoding="utf-8") as f:
                shipped = json.load(f)
        self.assertEqual(xx["locale"], "xx")
        self.assertEqual([r["key"] for r in xx["rows"]], [r["key"] for r in en["rows"]])
        for e, x in zip(en["rows"], xx["rows"]):
            want, got = ex.icu_parse(e["text"]), ex.icu_parse(x["text"])
            self.assertEqual(icu_shape(got), icu_shape(want), e["key"])
            self.assertGreaterEqual(ex.rendered_len(got), 1.4 * ex.rendered_len(want), e["key"])
        self.assertEqual(manifest["localization"]["xx"]["rows"], len(xx["rows"]))
        self.assertEqual(manifest["tables"], shipped["tables"], "only the xx strings are added")
        shas = [v["sha256"] for v in list(manifest["tables"].values()) + list(manifest["localization"].values())]
        self.assertEqual(manifest["content_hash"], ex.hashlib.sha256("".join(shas).encode("ascii")).hexdigest())
        self.assertNotEqual(manifest["content_hash"], shipped["content_hash"])

    def test_xx_never_ships(self):
        errs = ex.Errors()
        with tempfile.TemporaryDirectory() as tmp:
            p = os.path.join(tmp, "meta.csv")
            with open(p, "w", encoding="utf-8") as f:
                f.write('key,value\ncontent_version,2026.11.01\nmin_client_version,0.1.0\nsource_revision,x\n'
                        'ship_locales,"en,xx"\n')
            ex.read_meta(p, errs)
            self.assertEqual(list(errs), ["CONTENT E021 meta[ship_locales]: 'xx' is not a valid Locale "
                                          "(allowed: en, ja, ko, zh-Hans)"])
            for argv in (["pseudo", "--locale", "ja", "--out", os.path.join(tmp, "p")],
                         ["pseudo", "--out", os.path.join(REPO, "content")], ["pseudo"]):
                with self.subTest(argv=argv):
                    self.assertEqual(fx.run_main(argv + ["--src", SRC])[0], 2)
            self.assertFalse(os.path.exists(os.path.join(REPO, "content", "localization.xx.json")))


if __name__ == "__main__":
    unittest.main(verbosity=1)
