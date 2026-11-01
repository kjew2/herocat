#!/usr/bin/env python3
"""Unit tests for tools/power_curve_model.py (issue #87). Run: python3 tools/test_power_curve_model.py (check.sh does)."""
import math
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import content  # noqa: E402
import power_curve_model as pcm  # noqa: E402


class PowerCurveModelTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.p = pcm.Params(content.DEFAULT_DIR, {}, m0=False)
        cls.m0 = pcm.Params(content.DEFAULT_DIR, {}, m0=True)

    def test_parameters_come_from_content(self):
        db = content.load()
        self.assertEqual((self.p.c["HP0"], self.p.c["HP_G"]), db.curves["enemy_hp"][:2])
        self.assertEqual(self.p.c["HERO_LVL_MULT"], db.table("hero_levels")[0]["mult_per_level"])
        stats = {r["id"]: r for r in db.table("stats")}
        self.assertEqual(self.p.c["TRAIN_CAP[ATK_SPD]"], stats["ATK_SPD"]["cap"])
        self.assertEqual(self.p.c["TUTORIAL_DIAMONDS"], 1500)

    def test_override_replaces_a_value(self):
        p = pcm.Params(content.DEFAULT_DIR, {"HP_G": "1.05", "TRAIN_CAP[ATK_SPD]": "200"}, m0=False)
        self.assertEqual(p.c["HP_G"], 1.05)
        self.assertEqual(p.c["TRAIN_CAP[ATK_SPD]"], 200)
        with self.assertRaises(SystemExit):
            pcm.Params(content.DEFAULT_DIR, {"NO_SUCH_NAME": "1"}, m0=False)

    def test_m0_drops_mvp_layers(self):
        self.assertEqual(self.m0.banner_ids, ["WEAPON", "SKILL"])
        self.assertEqual(self.m0.research, {})
        self.assertEqual(self.m0.awakening, [])   # awakening rows are tier MVP
        bot = pcm.Bot(self.m0, 7).run(2, 120, 2)
        for row in bot.daily:
            self.assertEqual(row["layers"]["Companions"], 1.0)
            self.assertEqual(row["layers"]["Research"], 1.0)
            self.assertEqual(row["layers"]["Awaken"], 1.0)

    def test_boss_wall_condition(self):
        """03 section 2.8 / AC3: a boss needing exactly 30 * speedFactor hits is cleared, one more hit fails."""
        bot = pcm.Bot(self.p, 7)
        s = 10
        boss_hp = self.p.c["BOSS_HP_MULT"] * self.p.hp(s)
        exact = boss_hp / 30.0                     # speed 1: rated DPS == one hit per second
        self.assertTrue(bot.boss_fight(s, exact * (1 + 1e-12))[0])
        self.assertFalse(bot.boss_fight(s, boss_hp / 31.0)[0])

    def test_shares_sum_to_one_and_run_is_deterministic(self):
        a = pcm.Bot(self.p, 3).run(3, 120, 2)
        b = pcm.Bot(self.p, 3).run(3, 120, 2)
        self.assertEqual([r["stage"] for r in a.daily], [r["stage"] for r in b.daily])
        self.assertEqual(a.econ, b.econ)
        last = a.daily[-1]
        self.assertAlmostEqual(sum(last["shares"].values()), 1.0, places=9)
        self.assertAlmostEqual(math.prod(last["layers"].values()) * self.p.c["BASE_ATK"], last["dps"], delta=last["dps"] * 1e-9)

    def test_training_values_follow_closed_forms(self):
        bot = pcm.Bot(self.p, 7)
        bot.train["ATK"] = 10
        per = self.p.c["TRAIN_PER_LVL[ATK]"]
        self.assertAlmostEqual(bot.train_value("ATK"), math.log((1 + per * 11) / (1 + per * 10)))
        bot.train["ATK_SPD"] = self.p.c["TRAIN_CAP[ATK_SPD]"]
        self.assertEqual(bot.train_value("ATK_SPD"), 0.0)
        self.assertEqual(bot.train_value("CRIT_DMG"), 0.0)   # worthless at crit rate 0


if __name__ == "__main__":
    unittest.main()
