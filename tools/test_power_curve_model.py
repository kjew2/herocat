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
        levels = db.table("party_levels", None) or db.table("hero_levels")
        self.assertEqual(self.p.c["PARTY_LVL_MULT"], levels[0]["mult_per_level"])
        if "PARTY_SLOTS" in db.constants:   # party content (00 decision 109); older content takes the 00 values
            for k in ("PARTY_SLOTS", "BASE_ATK", "MP_REGEN0"):
                self.assertEqual(self.p.c[k], db.constants[k])
        else:
            self.assertEqual((self.p.c["PARTY_SLOTS"], self.p.c["BASE_ATK"], self.p.c["MP_REGEN0"]), (5, 2.0, 0.5))
        stats = {r["id"]: r for r in db.table("stats")}
        self.assertEqual(self.p.c["TRAIN_CAP[ATK_SPD]"], stats["ATK_SPD"]["cap"])
        self.assertEqual(self.p.c["TUTORIAL_DIAMONDS"], 1500)

    def test_override_replaces_a_value(self):
        p = pcm.Params(content.DEFAULT_DIR, {"HP_G": "1.05", "TRAIN_CAP[ATK_SPD]": "200"}, m0=False)
        self.assertEqual(p.c["HP_G"], 1.05)
        self.assertEqual(p.c["TRAIN_CAP[ATK_SPD]"], 200)
        with self.assertRaises(SystemExit):
            pcm.Params(content.DEFAULT_DIR, {"NO_SUCH_NAME": "1"}, m0=False)

    def test_m0_drops_mvp_layers_but_keeps_the_party(self):
        self.assertEqual(self.m0.banner_ids, ["WEAPON", "SKILL", "COMPANION"])   # companions are M0 (00 decision 107)
        self.assertEqual(self.m0.research, {})
        self.assertEqual(self.m0.awakening, [])   # awakening rows are tier MVP
        bot = pcm.Bot(self.m0, 7).run(2, 120, 2)
        for row in bot.daily:
            self.assertEqual(sum(1 for s in row["slots"] if s["comp"]), 5)   # 03 section 8.5: --m0 keeps companions
            self.assertEqual(row["layers"]["Research"], 1.0)
            self.assertEqual(row["layers"]["Awaken"], 1.0)
            self.assertEqual(row["awaken_ranks"], [0] * 5)

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
        self.assertAlmostEqual(sum(last["shares"].values()), 1.0, places=9)   # layers plus the Base (party size) term
        self.assertAlmostEqual(math.prod(last["layers"].values()) * last["base"], last["dps"], delta=last["dps"] * 1e-9)
        self.assertLessEqual(last["base"], 5 * self.p.c["BASE_ATK"] + 1e-9)   # exp(H(w)) <= number of slots

    def test_training_values_follow_closed_forms(self):
        bot = pcm.Bot(self.p, 7)
        bot.train["ATK"] = 10
        per = self.p.c["TRAIN_PER_LVL[ATK]"]
        self.assertAlmostEqual(bot.train_value("ATK"), math.log((1 + per * 11) / (1 + per * 10)))
        bot.train["ATK_SPD"] = self.p.c["TRAIN_CAP[ATK_SPD]"]
        self.assertEqual(bot.train_value("ATK_SPD"), 0.0)
        self.assertEqual(bot.train_value("CRIT_DMG"), 0.0)   # worthless at crit rate 0


class PartyModelTests(unittest.TestCase):
    """The five-companion party (00 decisions 109, 110; 03 sections 5.3, 7.1, 7.4, 8.3; 04 sections 6.3, 7.2, 8.2)."""

    @classmethod
    def setUpClass(cls):
        cls.p = pcm.Params(content.DEFAULT_DIR, {}, m0=False)

    def bot(self):
        b = pcm.Bot(self.p, 7)
        b.missile_pct = 0.0
        return b

    def comp(self, kind, rarity="COMMON"):
        return sorted(i for i, r in self.p.companions.items() if r["ability_type"] == kind and r["rarity"] == rarity)

    def test_starter_party_fills_five_slots_with_the_weapon_in_slot_0(self):
        b = self.bot()
        self.assertEqual(len(b.slots), 5)
        self.assertEqual([s["comp"] for s in b.slots], self.p.starter_companions)
        self.assertEqual(len(set(self.p.starter_companions)), 5)
        self.assertTrue(all(self.p.companions[c]["rarity"] == "COMMON" for c in self.p.starter_companions))
        self.assertEqual(b.slots[0]["weapon"], self.p.starter_weapon)
        self.assertTrue(all(s["weapon"] is None for s in b.slots[1:]))
        self.assertTrue(all(s["skill"] is None and s["rank"] == 0 for s in b.slots))

    def test_rated_dps_is_the_sum_of_the_slots(self):
        b = self.bot()
        ev = b.evaluate(b.slots)
        self.assertAlmostEqual(ev["rated"], sum(ev["slot_dps"]) * ev["party_buff"], delta=ev["rated"] * 1e-12)
        one = [dict(s) if i == 0 else dict(s, comp=None) for i, s in enumerate(b.slots)]
        self.assertLess(b.evaluate(one)["rated"], ev["rated"])   # an empty slot contributes nothing
        self.assertEqual(b.evaluate(one)["slots"], [0])

    def test_default_pool_runs_at_half_uptime(self):
        """00 decision 110: 10 MP per 10 s against MP_REGEN0 = 0.5 per pool."""
        b = self.bot()
        dmg = self.comp("DAMAGE")[:2]
        row = self.p.companions[dmg[0]]
        demand = row["mp_cost"] / (row["cooldown_ticks"] / self.p.c["TICK_HZ"])
        self.assertEqual(b.mp_sufficiency(dmg), [min(1.0, self.p.c["MP_REGEN0"] / demand)] * 2)

    def test_mp_regen_restores_every_pool(self):
        b = self.bot()
        regen = self.comp("MP_REGEN", "EPIC")[0]
        dmg = self.comp("DAMAGE")[0]
        b.state["COMPANION"].copies[regen] = 1
        tick = self.p.c["TICK_HZ"]
        r = self.p.companions[regen]
        restored = r["dps_pct"] / 4.0 * b.item_mult("COMPANION", regen)
        cd = r["cooldown_ticks"] / tick
        up = 1.0 if restored >= r["mp_cost"] else min(1.0, b.mp_regen() / ((r["mp_cost"] - restored) / cd))
        d = self.p.companions[dmg]
        want = min(1.0, (b.mp_regen() + up * restored / cd) / (d["mp_cost"] / (d["cooldown_ticks"] / tick)))
        alone = b.mp_sufficiency([dmg])[0]
        self.assertAlmostEqual(b.mp_sufficiency([dmg, regen])[0], want)
        self.assertGreater(want, alone)

    def test_layer_fold_is_exact_on_unequal_slots(self):
        b = self.bot()
        b.slots[0]["rank"] = 2
        b.slots[1]["rank"] = 1
        skill = sorted(self.p.skills)[0]
        b.state["SKILL"].copies[skill] = 1
        b.slots[2]["skill"] = skill
        b.missile_pct = 3.0
        b.dirty()
        L = b.layers()
        self.assertAlmostEqual(math.prod(L.values()) * b._base, b.dps(), delta=b.dps() * 1e-9)
        self.assertGreater(L["Awaken"], 1.0)
        self.assertLess(L["Awaken"], 1.5 ** 2)   # a weighted mean of the slots' factors, not the product
        self.assertLess(b._base, 5 * self.p.c["BASE_ATK"])

    def test_auto_equip_puts_the_best_weapon_and_skill_on_the_awakened_slot(self):
        b = self.bot()
        b.slots[3]["rank"] = 1
        w = max(self.p.weapons, key=lambda i: (self.p.weapons[i]["atk_pct"], i))
        k = max(self.p.skills, key=lambda i: (self.p.skills[i]["dps_pct"], i))
        for banner, iid in (("WEAPON", w), ("SKILL", k)):
            b.state[banner].copies[iid] = 1
        b.items_changed()
        slots = b.auto_equip(b.slots)
        self.assertEqual(slots[3]["weapon"], w)
        self.assertEqual(slots[3]["skill"], k)
        self.assertEqual([s["rank"] for s in slots], [0, 0, 0, 1, 0])   # ranks stay with the slots
        self.assertEqual(sorted(s["comp"] for s in slots), sorted(self.p.starter_companions))
        self.assertGreaterEqual(b.evaluate(slots)["rated"], b.evaluate(b.slots)["rated"])
        weapons = [s["weapon"] for s in slots if s["weapon"]]
        self.assertEqual(len(weapons), len(set(weapons)))   # an owned item sits in at most one slot

    def test_awakening_goes_to_the_strongest_slot_that_qualifies(self):
        cost = self.p.c["AWAKEN_COST_ROCK_CANDY[1]"]
        b = self.bot()
        b.highest, b.candy, b.day, b.day_counters = 60, float(cost), 1, {"limit_breaks": 0}
        b.spend_candy()
        self.assertEqual([s["rank"] for s in b.slots], [1, 0, 0, 0, 0])   # slot 0 holds the weapon: the main slot
        self.assertEqual((b.awaken_day[1], b.slot_awaken_day[(0, 1)]), (1, 1))
        b = self.bot()
        gate2 = self.p.c["AWAKEN_STAGES[2]"]
        b.highest, b.candy, b.day, b.day_counters = gate2 - 10, 10.0 * cost, 1, {"limit_breaks": 0}
        b.spend_candy()   # every slot passes rank 1; rank 2's gate is near (<= 50 stages), so no limit breaks
        self.assertEqual([s["rank"] for s in b.slots], [1] * 5)
        self.assertEqual(b.candy, 5.0 * cost)
        b.highest = gate2 - 60   # every next gate more than 50 stages away: the rest goes to limit breaks
        b.spend_candy()
        self.assertLess(b.candy, 5.0 * cost)

    def test_content_before_the_party_model_takes_the_00_values(self):
        real = pcm.contentlib.load_or_exit
        def legacy(d):
            db = real(d)
            for k in ("PARTY_SLOTS",):
                db.constants.pop(k, None)
            db.constants["BASE_ATK"], db.constants["MP_REGEN0"] = 10.0, 2.0
            return db
        pcm.contentlib.load_or_exit = legacy
        try:
            p = pcm.Params(content.DEFAULT_DIR, {}, m0=False)
        finally:
            pcm.contentlib.load_or_exit = real
        self.assertEqual((p.c["PARTY_SLOTS"], p.c["BASE_ATK"], p.c["MP_REGEN0"]), (5, 2.0, 0.5))
        self.assertTrue(any(f.startswith("BASE_ATK = 2") for f in p.fallbacks))
        self.assertEqual(len(pcm.Bot(p, 7).slots), 5)


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
