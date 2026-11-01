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


class EconomyCheckTests(unittest.TestCase):
    """06 economy layer (issue #178): rule-derived diamond income, the section 2 / 11 tables and their checks."""

    @classmethod
    def setUpClass(cls):
        cls.p = pcm.Params(content.DEFAULT_DIR, {}, m0=False)
        cls.run3 = pcm.Bot(cls.p, 7).run(3, 120, 2)

    def reference_day(self, bot):
        """06 section 5.2: the F2P day with no diamond spend (login, kills, boss, weapon gift pull, release, merge, 3 dungeons)."""
        bot.day = 1
        bot.start_day()
        d = bot.day_counters
        d["kills"], d["boss_attempts"], d["pulls"]["WEAPON"] = 300, 3, 1
        d["dungeon_clears"] = {k: 1 for k in d["dungeon_clears"]}
        bot.board.day["released"], bot.board.day["merges"] = 30, 15
        bot.claim_quests()

    def test_reference_day_pays_section_2(self):
        bot = pcm.Bot(self.p, 7)
        self.reference_day(bot)
        src = bot.econ["diamonds_by_source"]
        self.assertEqual(bot.day_counters["dailies_done"], 9)                    # 8 paying rows + d_login
        self.assertEqual(src["LOGIN"], self.p.c["LOGIN_DIAMONDS"])
        self.assertEqual(src["QUEST_DAILY"], 8 * 20 + 30 + 70)
        self.assertEqual(src["QUEST_WEEKLY"], 0)
        self.assertEqual(src["LOGIN"] + src["QUEST_DAILY"] + self.p.c["REF_NEW_BOSSES"] * self.p.c["FIRST_CLEAR_DIAMONDS"]
                         + 7 * 50 / 7, self.p.c["DIAMONDS_PER_DAY_F2P"])   # 06 AC1 arithmetic, weeklies amortised

    def test_weekly_rows_pay_once_a_week(self):
        bot = pcm.Bot(self.p, 7)
        for day in range(1, 9):
            bot.day = day
            bot.start_day()
            bot.day_counters["kills"] = 3000
            bot.claim_quests()
            if day == 1:
                self.assertEqual(bot.econ["diamonds_by_source"]["QUEST_WEEKLY"], 50)   # w_kills only
            if day == 5:
                self.assertEqual(bot.econ["diamonds_by_source"]["QUEST_WEEKLY"], 100)  # + w_login on its 5th day
        self.assertEqual(bot.econ["diamonds_by_source"]["QUEST_WEEKLY"], 150)          # day 8 starts a new week

    def test_achievement_rungs_pay_once(self):
        bot = pcm.Bot(self.p, 7)
        bot.highest = 55
        self.reference_day(bot)
        first = bot.econ["diamonds_by_source"]["ACHIEVEMENT"]
        self.assertGreaterEqual(first, 20 + 30 + 50)   # stage rungs 10, 30, 50 (plus any hero/owned rungs)
        bot.claim_quests()
        self.assertEqual(bot.econ["diamonds_by_source"]["ACHIEVEMENT"], first)

    def test_first_clears_pay_per_boss_stage(self):
        bot = self.run3
        bosses = bot.highest // self.p.c["BOSS_EVERY"]
        self.assertEqual(bot.econ["first_clears"], bosses)
        self.assertEqual(bot.econ["diamonds_by_source"]["FIRST_CLEAR"], bosses * self.p.c["FIRST_CLEAR_DIAMONDS"])

    def test_day_flows_add_up_to_the_cumulative_economy(self):
        bot = self.run3
        flows = [pcm.day_flow(bot, d) for d in range(1, 4)]
        self.assertAlmostEqual(sum(f["dia_total"] for f in flows), bot.econ["diamonds_earned"])
        self.assertAlmostEqual(sum(f["dia_spent"] for f in flows), sum(bot.econ["diamonds_spent"].values()))
        self.assertAlmostEqual(sum(f["gold_spent"] for f in flows), sum(bot.econ["gold_spent"].values()), delta=1e-6 * bot.econ["gold_kills"])
        self.assertEqual(flows[0]["dia_tutorial"], self.p.c["TUTORIAL_DIAMONDS"])
        self.assertEqual(sum(f["new_bosses"] for f in flows), bot.econ["first_clears"])

    def test_flat_mode_is_the_old_400_a_day(self):
        p = pcm.Params(content.DEFAULT_DIR, {}, m0=False, diamonds="flat")
        bot = pcm.Bot(p, 7).run(2, 120, 2)
        self.assertEqual(bot.econ["diamonds_earned"], p.c["TUTORIAL_DIAMONDS"] + 2 * p.c["DIAMONDS_PER_DAY_F2P"])
        self.assertEqual(bot.econ["diamonds_by_source"]["QUEST_DAILY"], 0)
        self.assertGreater(bot.econ["first_clears"], 0)

    def test_m0_earns_login_first_clears_and_tutorial_only(self):
        m0 = pcm.Params(content.DEFAULT_DIR, {}, m0=True)
        self.assertEqual((m0.quests_daily, m0.quests_weekly, m0.achievements), ([], [], []))
        bot = pcm.Bot(m0, 7).run(2, 120, 2)
        src = bot.econ["diamonds_by_source"]
        self.assertEqual(bot.econ["diamonds_earned"], src["TUTORIAL"] + src["LOGIN"] + src["FIRST_CLEAR"])
        self.assertEqual(src["LOGIN"], 2 * m0.c["LOGIN_DIAMONDS"])
        names = [n for _, n, _ in pcm.economy_checks(m0, bot)]
        self.assertEqual(len(names), 1)   # gold only: M0 has no quests, dungeons or Research

    def test_target_stage_interpolates_section_2_rule(self):
        self.assertEqual(pcm.target_stage(self.p, 0), 0)
        t = {r["day"]: r["stage"] for r in self.p.targets}
        self.assertEqual(pcm.target_stage(self.p, 1), t[1])
        self.assertAlmostEqual(pcm.target_stage(self.p, 2), (t[1] + t[3]) / 2)

    def test_economy_checks_cover_every_issue_178_check(self):
        bot = pcm.Bot(self.p, 7).run(30, 120, 2)
        names = " | ".join(n for _, n, _ in pcm.economy_checks(self.p, bot))
        for key in ("DIAMONDS_PER_DAY_F2P", "FIRST_CLEAR_DIAMONDS", "PULL_COST[WEAPON]", "PULL_COST[SKILL]",
                    "PULL_COST[COMPANION]", "PITY_HARD", "EXP_DUNGEON_KILLS", "DUNGEON_FLOOR_STAGE_STEP",
                    "gold is fully spent", "diamonds spent within"):
            self.assertIn(key, names)
        income, held = pcm.steady_diamonds(bot)
        self.assertGreater(income, 0)
        self.assertGreaterEqual(held, 0)


if __name__ == "__main__":
    unittest.main()
