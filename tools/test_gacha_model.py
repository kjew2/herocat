#!/usr/bin/env python3
"""Unit tests for tools/gacha_model.py's collection fill, cadence and 04 section 14 S-constant checks (issue #164).
Each check has a fixture (an --override) that trips it. Run: python3 tools/test_gacha_model.py (check.sh does).
The bot runs use two seeds to stay fast; check.sh's `gacha_model.py --cadence` runs the real 20-seed gate."""
import io
import math
import os
import sys
import unittest
from contextlib import redirect_stdout

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import content  # noqa: E402
import gacha_model as gm  # noqa: E402

SEEDS = [1, 2]
DAYS = 30
L4 = "BANNER_LVL_EXP / BANNER_EXP_PER_PULL"
EPLUS = "BANNER_LVL_*_SHIFT"
COMMON = "weapon pool size: a specific Common"
LEGEND = "weapon pool size: a specific Legendary"
PASSIVE = "WEAPON_PASSIVE_PCT"
COMPANION = "PARTY_SLOTS / companion dps_pct"


def cadence(overrides):
    p, runs = gm.run_bots(content.DEFAULT_DIR, overrides, SEEDS, DAYS)
    return p, gm.Cadence(p, runs, DAYS)


def find(checks, prefix):
    hits = [c for c in checks if c[1].startswith(prefix)]
    assert hits, f"no check named {prefix!r}"
    return hits


class CollectionFillTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = gm.Banner(content.DEFAULT_DIR, "WEAPON")

    def test_rarity_rates_sum_to_one_and_keep_the_consolidated_lplus(self):
        for level in (1, 10):
            rr = gm.rarity_rates(self.w, level)
            self.assertAlmostEqual(sum(rr.values()), 1.0, places=12)
            self.assertAlmostEqual(rr["LEGENDARY"] + rr["MYTHIC"], gm.pity_numbers(self.w)["lplus"], places=12)

    def test_coupon_collector(self):
        self.assertAlmostEqual(gm.pulls_to_complete(1, 0.5), 2.0)
        self.assertAlmostEqual(gm.pulls_to_complete(2, 1.0), 3.0)   # 2 * (1 + 1/2)
        p = gm.rarity_rates(self.w, 1)["COMMON"]
        self.assertAlmostEqual(gm.pulls_to_complete(10, p), 10 / p * sum(1 / k for k in range(1, 11)))

    def test_fill_curve_starts_empty_and_saturates(self):
        owned, new = gm.fill_after(self.w, 1, 0)
        self.assertEqual(owned, 0.0)
        self.assertAlmostEqual(new, 1.0, places=12)   # every first pull is new
        owned, new = gm.fill_after(self.w, 1, 100000)
        self.assertAlmostEqual(owned, sum(gm.pool_sizes(self.w).values()), places=6)
        self.assertLess(new, 1e-9)

    def test_expected_copies_walk_the_banner_level(self):
        # pull 1 lands at level 2 only if exp_per_pull reaches the level-2 threshold; either way it is item_rate there
        self.assertAlmostEqual(gm.expected_copies(self.w, "COMMON", 1),
                               gm.item_rate(self.w, "COMMON", self.w.level_for(self.w.exp_per_pull)))
        lo = gm.expected_copies(self.w, "COMMON", 100)
        held = 100 * gm.item_rate(self.w, "COMMON", 1)
        self.assertLess(lo, held)   # Commons get rarer as the banner levels up

    def test_report_prints_the_blocks_in_section_14_order(self):
        out = io.StringIO()
        with redirect_stdout(out):
            gm.main(["--banner", "WEAPON"])
        text = out.getvalue()
        heads = ["pity (", "soft pity ramp", "rate table per banner level", "collection fill curve"]
        pos = [text.index(h) for h in heads]
        self.assertEqual(pos, sorted(pos))


class CadenceCheckTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.p, cls.cad = cadence({})
        cls.base = gm.cadence_checks(cls.p, cls.cad, dict(gm.SHIFTS))

    def test_every_rule_is_a_named_check_with_its_gate_kind(self):
        kinds = {L4: True, EPLUS: True, COMMON: False, LEGEND: True, PASSIVE: True, COMPANION: False}
        for prefix, hard in kinds.items():
            for c in find(self.base, prefix):
                self.assertEqual(c[3], hard, c[1])
        self.assertEqual(len(find(self.base, PASSIVE)), 2)   # days 7 and 30

    def test_current_content_passes_the_decided_pool_and_passive_checks(self):
        for prefix in (EPLUS, LEGEND, PASSIVE):
            for c in find(self.base, prefix):
                self.assertEqual(c[0], "PASS", f"{c[1]}: {c[2]}")

    def test_eplus_check_trips_on_a_steeper_e_shift(self):
        shifts = dict(gm.SHIFTS, BANNER_LVL_C_SHIFT=0.032, BANNER_LVL_E_SHIFT=0.020)   # rows still sum to 1
        (c,) = find(gm.cadence_checks(self.p, self.cad, shifts), EPLUS)
        self.assertEqual(c[0], "FAIL")

    def test_weapon_level_4_check_trips_when_levels_come_too_fast(self):
        p, cad = cadence({"BANNER_EXP_PER_PULL": "10"})
        (c,) = find(gm.cadence_checks(p, cad, dict(gm.SHIFTS)), L4)
        self.assertEqual(c[0], "FAIL", c[2])

    def test_legendary_check_trips_and_common_check_passes_at_a_whale_cadence(self):
        p, cad = cadence({"TICKETS_PER_DAY[WEAPON]": "20"})
        checks = gm.cadence_checks(p, cad, dict(gm.SHIFTS))
        self.assertEqual(find(checks, LEGEND)[0][0], "FAIL")
        self.assertEqual(find(checks, COMMON)[0][0], "PASS")
        self.assertEqual(find(self.base, COMMON)[0][0], "FAIL")   # 04 open question 8: unreachable at F2P cadence

    def test_passive_check_trips_when_the_passive_dominates(self):
        over = {f"WEAPON_PASSIVE_PCT[{r}]": str(v * 10) for r, v in
                (("COMMON", 1), ("RARE", 2), ("EPIC", 4), ("LEGENDARY", 8), ("MYTHIC", 16))}
        p, cad = cadence(over)
        for c in find(gm.cadence_checks(p, cad, dict(gm.SHIFTS)), PASSIVE):
            self.assertEqual(c[0], "FAIL", f"{c[1]}: {c[2]}")

    def test_companion_check_trips_without_companion_damage(self):
        p, cad = cadence({f"COMPANION_DPS_PCT[{r}]": "0" for r in gm.RARITIES})
        (c,) = find(gm.cadence_checks(p, cad, dict(gm.SHIFTS)), COMPANION)
        self.assertEqual(c[0], "FAIL")
        self.assertTrue(math.isclose(cad.companion_share, 0.0, abs_tol=1e-12))

    def test_cli_exits_1_on_a_hard_failure_only(self):
        out = io.StringIO()
        with redirect_stdout(out):
            code = gm.main(["--cadence", "--quiet", "--seeds", "1", "--override", "BANNER_EXP_PER_PULL=10"])
        self.assertEqual(code, 1)
        self.assertIn("FAIL  " + L4, out.getvalue())


if __name__ == "__main__":
    unittest.main()
