#!/usr/bin/env python3
"""Greedy F2P power-curve model for HeroCat (specs/03 section 8.6). Stdlib only; reads content/*.json via content.py.

A per-minute aggregate model of the 03 section 8.3 bot, not the real sim: every layer of the power formula
(03 section 7.1: Training x6 stats, Hero Level, Weapons, Skills, Companions, Fish Board, Research, Awakening, crit and
speed) is driven by the income that feeds it. Per active minute it computes the rated DPS, runs kills with the
03 section 2.8 boss check, plays the Fish Board with 05's attentive policy (tools/fish_model.py's board as a count per
level) and spends gold, Rock Candy and Fish Coins greedily; once per day it grants income and pulls with 04's exact
algorithm (tools/gacha_model.py: banner level per pull, pity, 10-pull guarantee). Offline gaps credit gold and EXP at
OFFLINE_RATE and fill the tank (06 section 7). Enemy and cost curves use `**`: this is an estimator, bit-exactness is
the C# harness's job (10).

  power_curve_model.py [--seed 7] [--days 30] [--active-min 120] [--sessions 2] [--m0] [--override NAME=VALUE ...]
                       [--tickets W,S,C] [--rock-candy-per-day N] [--fish-coin-per-day N] [--policy greedy|lazy]
                       [--diamonds content|flat]
  power_curve_model.py --check [--seeds 1-20]    20-seed sweep: median day table vs progress_targets (03 AC16) and
                                                 every S-constant check of 03 section 8.6 and 06 economy check per
                                                 seed; exits 1 on FAIL

Diamonds follow 06's rules by default (login, the quest rows the bot's day completes, first clears, achievement rungs;
issue #178); --diamonds flat grants DIAMONDS_PER_DAY_F2P a day instead. Prints the day table (days 1, 2, 3, 5, 7, 10,
14, 21, 30), the layer-share table with PASS/FAIL/WARN per layer (03 section 8.5), the wall report, the economy
report, the per-banner pull cadence, 06's section 2 (diamond income by day) and section 11 (sinks by day) tables and
the `DAYTABLE` line for 00 section 6.6, and writes tools/out/power_curve_<seed>.json. MVP tables the M0 content does
not ship yet (research, companions, dungeons, achievements) fall back to their spec rows, flagged in the output.
"""
import argparse
import json
import math
import os
import random
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import content as contentlib  # noqa: E402
import fish_model  # noqa: E402
import gacha_model  # noqa: E402

RARITIES = gacha_model.RARITIES
TIERS = gacha_model.TIERS
BANNERS = ["WEAPON", "SKILL", "COMPANION"]
TICKET = {"WEAPON": "TICKET_SUMMON_WEAPON", "SKILL": "TICKET_SUMMON_SKILL", "COMPANION": "TICKET_SUMMON_COMPANION"}
TRAINABLE = ["ATK", "CRIT_RATE", "CRIT_DMG", "ATK_SPD", "GOLD_GAIN", "EXP_GAIN"]
LAYERS = ["Training", "Hero", "Weapons", "Skills", "Companions", "Fish", "Research", "Runes", "Awaken", "Crit", "Speed"]
REPORT_DAYS = [1, 2, 3, 5, 7, 10, 14, 21, 30]
SHARE_DAYS = [1, 3, 7, 14, 30]

# ---- 03 section 8.3 bot policy parameters (the policy's definition, not balance constants)
INVEST_VALUE_SHARE = 0.5       # GOLD_GAIN / EXP_GAIN / res_gold_gain valued at half the levels their income buys
LIMIT_BREAK_GATE_STAGES = 50   # limit breaks only when the next Awakening gate is more than 50 stages away
RES_TANK_NATS_PER_LEVEL = 0.08 # res_tank_* value: measured board growth per 10% more fish
RES_FISH_CAP_VALUE = 0.5       # res_fish_cap value: 0.5 * ln(1.5) * fraction of the board already at the cap
COMPANION_SLOTS_AT_RANK0 = 1   # 03 section 5.3: companion slot 1 is open from the start

# ---- 03 section 8.5 / 10 section 3.6 share rule (acceptance-targets.json mirrors it for the C# sweep)
SHARE_MAX, SHARE_MIN = 0.40, 0.05
SHARE_MAX_DAYS, SHARE_MIN_DAYS = (1, 7, 30), (7, 30)
SHARE_FLOOR_EXCLUDED = {"Speed", "Runes"}                 # speed permanently exempt (issue #36); runes not before P1
SHARE_FLOOR_EXEMPT = {"M0": {"Research", "Companions"}}   # WARN instead of FAIL (issue #12)
STAGE_CHECK_DAYS = (1, 7, 30)                              # 03 AC16
FIRST_WALL_BAND = (60, 200)                                # 03 section 8.6, AC18
EARLY_WALL_MAX_ACTIVE_MIN, EARLY_WALL_LAST_DAY = 240, 14   # 03 AC18
LATE_WALL_MAX_ACTIVE_MIN = None                            # acceptance-targets.json lateWallMaxActiveMinutes: pending (issue #36)
TRAIN_GOLD_SHARE_BAND = (0.05, 0.50)
HERO_LEVEL_PER_STAGE_BAND = (0.25, 0.45)
TAP_WALL_SPEEDUP_MAX = 2.5
BOSS_CANDY_SHARE_MAX = 0.20
RESEARCH_ALL_NODES_BY_DAY, FISH_CAP_2_BY_DAY = 7, 10
LIMIT_BREAK_DAILY_FROM_DAY, LIMIT_BREAK_CANDY_SHARE_MAX = 10, 0.60   # 04 section 5.2
FISH_DUNGEON_SHARE_BAND, FISH_DUNGEON_DAYS = (0.20, 0.50), (7, 30)  # 06 section 6.3 (a); #249 item 6, agreed

# ---- MVP tables the M0 content does not ship yet. Each is the owning spec's row set, used only when content lacks it.
RESEARCH_FALLBACK = [   # 03 section 6 / 09 section 5.18. Only for content without a research table (content/ ships it, #158).
    {"id": "res_atk", "effect": "ATK_PCT", "effect_per_level": 0.05, "effect_floor": None, "max_level": 50, "cost0": 150.0, "cost_g": 1.20, "unlock_awaken_rank": 1},
    {"id": "res_gold_gain", "effect": "GOLD_GAIN", "effect_per_level": 0.05, "effect_floor": None, "max_level": 20, "cost0": 250.0, "cost_g": 1.40, "unlock_awaken_rank": 1},
    {"id": "res_mp_pool", "effect": "MP_POOL", "effect_per_level": 20.0, "effect_floor": None, "max_level": 10, "cost0": 200.0, "cost_g": 1.50, "unlock_awaken_rank": 1},
    {"id": "res_mp_regen", "effect": "MP_REGEN", "effect_per_level": 0.5, "effect_floor": None, "max_level": 10, "cost0": 300.0, "cost_g": 1.50, "unlock_awaken_rank": 1},
    {"id": "res_tank_cap", "effect": "TANK_CAP", "effect_per_level": 5.0, "effect_floor": None, "max_level": 10, "cost0": 200.0, "cost_g": 1.50, "unlock_awaken_rank": 1},
    {"id": "res_tank_refill", "effect": "TANK_REFILL", "effect_per_level": 0.4, "effect_floor": 2.0, "max_level": 10, "cost0": 300.0, "cost_g": 1.60, "unlock_awaken_rank": 1},
    {"id": "res_fish_cap", "effect": "FISH_CAP", "effect_per_level": 1.0, "effect_floor": None, "max_level": 10, "cost0": 2500.0, "cost_g": 2.00, "unlock_awaken_rank": 1},
    {"id": "res_offline_cap", "effect": "OFFLINE_CAP", "effect_per_level": 2.0, "effect_floor": None, "max_level": 6, "cost0": 500.0, "cost_g": 1.50, "unlock_awaken_rank": 1},
]
# 04 section 8.1: ability mix 50% DAMAGE, 20% BUFF_ATK, 15% BUFF_GOLD, 15% MP_REGEN; mp_cost 10, cooldown 100 ticks.
# Used only for content without a companions table (it ships since issue #161): pool sizes from banners[COMPANION].pool_size,
# dps_pct from rarities.companion_dps_pct.
COMPANION_FALLBACK = {"mix": [("DAMAGE", 0.50), ("BUFF_ATK", 0.20), ("BUFF_GOLD", 0.15), ("MP_REGEN", 0.15)],
                      "mp_cost": 10, "cooldown_ticks": 100}
# 06 sections 6.1-6.3 / 09 sections 5.24-5.25: equal to content/dungeons.json and dungeon_floors.json today.
# The Fish Coin curve is settled at 50 * f (#165).
DUNGEON_FALLBACK = {"tickets_per_day": 2, "floor_stage_step": 20, "floors": 40,
                    "FISH_COIN": 50.0, "ROCK_CANDY": 10.0, "EXP_KILLS": 300}
# 06 section 1 / 00 decision 15: weeklies and achievements pay about 0.15 skill and 0.15 companion tickets a day.
# The weekly share is content now (w_bonus_all_skill / w_bonus_all_companion, 1 a week each); the achievement share is not.
# TODO(#171): derive both from quests_weekly + achievements rows once achievements ship. The weapon ticket is content.
SUMMON_TICKETS_FALLBACK = {"SKILL": 0.15, "COMPANION": 0.15}
# 06 section 5.4 achievement ladders as (goal, [(target, diamonds), ...]). TODO(#171): read content/achievements.json
# once it ships (the table is preferred whenever it exists).
ACHIEVEMENTS_FALLBACK = [
    ("REACH_STAGE", [(10, 20), (30, 30), (50, 50), (100, 80), (150, 100), (200, 100), (300, 150), (400, 150), (500, 200),
                     (700, 250), (1000, 300)]),
    ("REACH_HERO_LEVEL", [(10, 20), (25, 30), (50, 50), (75, 80), (100, 100), (150, 150), (200, 200)]),
    ("OWN_WEAPONS", [(5, 30), (10, 50), (20, 100), (25, 200)]),
    ("OWN_SKILLS", [(5, 30), (10, 50), (20, 100), (30, 150), (35, 250)]),
    ("OWN_COMPANIONS", [(5, 30), (10, 50), (20, 100), (35, 150), (45, 300)]),
    ("LIFETIME_MERGES", [(50, 20), (200, 30), (500, 50), (1000, 80), (3000, 100), (10000, 200)]),
    ("FISH_LEVEL", [(5, 30), (8, 50), (10, 100), (12, 150), (15, 300)]),
    ("AWAKEN_RANK", [(1, 50), (2, 100), (3, 150), (4, 200), (5, 300)]),
    ("LIFETIME_SUMMONS", [(10, 20), (50, 50), (100, 80), (300, 100), (500, 150), (1000, 300)]),
]

# ---- 06 economy checks (issue #178): sections 2, 6.3, 11, open questions 1 and 5, Constants introduced
WEEK_DAYS = 7                                  # bot epoch is a Monday (00 decision 25): weeks are days 1-7, 8-14, ...
DIAMOND_STEADY_DAYS = (15, 28)                 # 06 section 2 "steady state after day 14": the two whole weeks after it
DIAMOND_BUDGET_TOL = 0.10                      # 06 open question 1 names no number; E039's 10% (09) is used
FIRST_CLEAR_TOL = 0.20                         # 06 Constants introduced, FIRST_CLEAR_DIAMONDS
FIRST_CLEAR_PERIODS = ((1, 1), (2, 3), (4, 7), (8, 14), (15, 30))   # 06 section 2 table rows, as day ranges
TENPULL_MAX_DAYS = {"WEAPON": 2.5, "SKILL": 5.0, "COMPANION": 7.5}   # 06 section 2: income affords a 10-pull every N days
DIAMOND_HOLD_MAX_DAYS = 2.5                    # 06 section 11 observation 2
GOLD_SPENT_MIN = 0.99                          # 06 section 11 observation 1: "100% of gold is spent", less rounding
EXP_DUNGEON_SHARE_BAND, EXP_DUNGEON_DAYS = (0.10, 0.20), (7, 30)    # 06 Constants introduced, EXP_DUNGEON_KILLS
BEST_FLOOR_BAND, BEST_FLOOR_DAY = (25, 29), 30                      # 06 open question 5
SECTION11_DAYS, SECTION2_DAYS = (1, 7, 30), (1, 3, 7, 14, 30)
DIAMOND_SOURCES = ("TUTORIAL", "LOGIN", "QUEST_DAILY", "QUEST_WEEKLY", "FIRST_CLEAR", "ACHIEVEMENT", "FLAT")


# ================================================================================================ parameters

GACHA_OVERRIDES = ("PITY_SOFT_START", "PITY_SOFT_STEP", "PITY_HARD", "M_SHARE_OF_LPLUS", "BANNER_EXP_PER_PULL")

class Params:
    """Every number the model uses, read from content (plus the flagged fallbacks), with --override applied."""

    def __init__(self, content_dir, overrides, m0, diamonds="content"):
        self.content_dir = content_dir
        self.diamond_mode = diamonds   # "content": 06's rules (login, quests, first clears, achievements); "flat": 400/day
        self.db = db = contentlib.load_or_exit(content_dir)
        self.m0 = m0
        self.tier = "M0" if m0 else "MVP"
        self.fallbacks = []
        tier_ok = lambda r: TIERS.index(r.get("tier", "M0")) <= TIERS.index(self.tier)
        c = dict(db.constants)
        for cid, key in (("enemy_hp", "HP"), ("gold_per_kill", "GOLD"), ("exp_per_kill", "EXP")):
            base, growth, off = db.curves[cid]
            c[key + "0"], c[key + "_G"], c[key + "_OFF"] = base, growth, off
        hero = db.table("hero_levels")[0]
        c["HERO_LVL_EXP0"], c["HERO_LVL_EXP_G"], c["HERO_LVL_MULT"] = hero["exp0"], hero["exp_g"], hero["mult_per_level"]
        stats = {r["id"]: r for r in db.table("stats")}
        curves = {r["stat"]: r for r in db.table("training_curves")}
        for s in TRAINABLE:
            c[f"TRAIN_PER_LVL[{s}]"] = stats[s]["per_lvl"]
            c[f"TRAIN_CAP[{s}]"] = stats[s]["cap"]
            c[f"TRAIN_COST0[{s}]"] = curves[s]["cost0"]
            c[f"TRAIN_COST_G[{s}]"] = curves[s]["cost_g"]
        self.rarity = {r["id"]: r for r in db.table("rarities")}
        for r in RARITIES:
            c[f"LIMIT_BREAK_COST[{r}]"] = self.rarity[r]["limit_break_cost"]
            c[f"WEAPON_PASSIVE_PCT[{r}]"] = self.rarity[r]["weapon_passive_pct"]
        self.awakening = sorted((r for r in db.table("awakening") if tier_ok(r)), key=lambda r: r["rank"])
        for r in self.awakening:
            c[f"AWAKEN_STAGES[{r['rank']}]"] = r["stage_gate"]
            c[f"AWAKEN_COST_ROCK_CANDY[{r['rank']}]"] = r["rock_candy_cost"]
            c[f"AWAKEN_MULT[{r['rank']}]"] = r["mult"]
        self.awaken_targets = {r["rank"]: r["max_days_after_gate"] for r in db.table("awakening_targets")}
        self.targets = sorted(db.table("progress_targets"), key=lambda r: r["day"])
        self.unlocks = {r["feature"]: r for r in db.table("feature_unlocks") if tier_ok(r)}

        banners = {r["id"]: r for r in db.table("banners")}
        self.banner_ids = [b for b in BANNERS if b in banners and banners[b]["enabled"] and tier_ok(banners[b])]
        for b in self.banner_ids:
            c[f"PULL_COST[{b}]"] = banners[b]["pull_cost_diamonds"]
        self.banner_rows = banners

        research = db.table("research", None)
        if research is None and not m0:
            research = RESEARCH_FALLBACK
            self.fallbacks.append("research: 03 section 6 rows (content has no research table)")
        self.research = {} if m0 else {r["id"]: dict(r) for r in research if tier_ok(r) or "tier" not in r}
        for rid, r in self.research.items():
            c[f"RES_COST0[{rid}]"], c[f"RES_COST_G[{rid}]"] = r["cost0"], r["cost_g"]
            c[f"RES_EFFECT[{rid}]"], c[f"RES_MAX[{rid}]"] = r["effect_per_level"], r["max_level"]

        dungeons = db.table("dungeons", None)
        if not m0 and dungeons is None:
            self.fallbacks.append("dungeons: 06 section 6.3 formulas 50f / 10f / 300 kills (content has no dungeons table)")
        for k, v in DUNGEON_FALLBACK.items():
            c[f"DUNGEON_{k}"] = v
        if not m0:
            for b, v in SUMMON_TICKETS_FALLBACK.items():
                c[f"TICKETS_PER_DAY[{b}]"] = v
            c["TICKETS_PER_DAY[WEAPON]"] = c["DAILY_GIFT_WEAPON_TICKETS"]
            self.fallbacks.append("skill/companion tickets: 06 section 1 'about 0.15 a day' (no achievements table, TODO(#171))")
        c["ROCK_CANDY_PER_DAY"] = None   # None = dungeon income per 06; a number replaces it (--rock-candy-per-day)
        c["FISH_COIN_PER_DAY"] = None
        self._load_quests(db, tier_ok)

        grants = [g for row in db.table("tutorial_steps") if tier_ok(row) for g in (row["grants"] or [])]
        c["TUTORIAL_DIAMONDS"] = sum(g["amount"] for g in grants if g.get("currency") == "DIAMOND")
        self.tutorial_items = [(g["kind"], g["id"], g["amount"]) for g in grants if "kind" in g]

        self.overrides = dict(overrides)
        for k, v in overrides.items():
            if k in GACHA_OVERRIDES:
                continue   # banner columns, not constants: applied to gacha_model.Banner below
            if k not in c:
                sys.exit(f"--override: unknown name {k} (known: constants keys, HP_G, GOLD_G, EXP_G, HERO_LVL_MULT, "
                         f"TRAIN_PER_LVL[STAT], TRAIN_CAP[STAT], AWAKEN_COST_ROCK_CANDY[RANK], RES_COST0[id], ...)")
            old = c[k]
            if isinstance(old, bool):
                c[k] = v.lower() == "true"
            elif isinstance(old, int) and float(v).is_integer():
                c[k] = int(float(v))
            else:
                c[k] = float(v)
        self.c = c

        # Banners: 04's exact pull algorithm from gacha_model (pity, banner level, guarantee), pools by tier.
        gacha_over = {k: v for k, v in overrides.items() if k in GACHA_OVERRIDES}
        self.banner = {}
        for b in self.banner_ids:
            bn = gacha_model.Banner(content_dir, b, tier=self.tier, overrides=gacha_over)
            bn.pull_cost = c[f"PULL_COST[{b}]"]
            self.banner[b] = bn
        self.weapons = {r["id"]: dict(r) for r in db.table("weapons") if tier_ok(r)}
        for w in self.weapons.values():   # an override rescales the rarity's rows, keeping each row's flavour offset
            r = w["rarity"]
            base = self.rarity[r]["weapon_passive_pct"]
            if c[f"WEAPON_PASSIVE_PCT[{r}]"] != base:
                w["passive_pct"] = w["passive_pct"] * c[f"WEAPON_PASSIVE_PCT[{r}]"] / base
        self.skills = {r["id"]: r for r in db.table("skills") if tier_ok(r)}
        self.companions = {}
        if "COMPANION" in self.banner:
            rows = db.table("companions", None)
            if rows is None:
                rows = self._companion_fallback(banners["COMPANION"])
                self.fallbacks.append("companions: 04 section 8.1 pool mix and defaults (content has no companions table)")
            self.companions = {r["id"]: r for r in rows if tier_ok(r)}
            pool = self.banner["COMPANION"].pool
            for r in RARITIES:
                pool[r] = sorted(i for i, row in self.companions.items() if row["rarity"] == r)
        self.dupe_cum = [r["cum_copies"] for r in sorted(db.table("dupe_levels"), key=lambda r: r["level"])]

        # Fish Board: 05's model (constants, fish_levels, tank, research effects), same overrides where they apply.
        self.fish = fish_model.Content(content_dir, {})
        for k, v in overrides.items():
            if k in self.fish.c and k in c:
                self.fish.c[k] = c[k]
        if self.research:
            for node, key in (("res_tank_cap", "RES_TANK_CAP_PER_LVL"), ("res_tank_refill", "RES_TANK_REFILL_PER_LVL"),
                              ("res_fish_cap", "RES_FISH_CAP_PER_LVL")):
                if node in self.research:
                    self.fish.c[key] = c[f"RES_EFFECT[{node}]"]

    def _load_quests(self, db, tier_ok):
        """06 section 5 quest rows (content) and the section 5.4 ladders (content, else the spec rows, TODO(#171))."""
        pick = lambda rows: [r for r in rows if tier_ok(r)]
        self.quests_daily = pick(db.table("quests_daily", None) or [])
        self.quests_weekly = pick(db.table("quests_weekly", None) or [])
        ach = db.table("achievements", None)
        if ach is None and self.quests_daily:   # achievements ship with quests (MVP); M0 has neither
            ach = [{"id": f"a_{g.lower()}_{t}", "goal": g, "filter": None, "target": t, "reward_currency": "DIAMOND",
                    "reward_amount": d, "tier": "MVP"} for g, rungs in ACHIEVEMENTS_FALLBACK for t, d in rungs]
            self.fallbacks.append("achievements: 06 section 5.4 ladder rows (content has no achievements table, TODO(#171))")
        self.achievements = pick(ach or [])
        if self.diamond_mode == "flat":
            self.fallbacks.append("diamonds: flat DIAMONDS_PER_DAY_F2P a day (--diamonds flat, parity mode)")

    def _companion_fallback(self, banner_row):
        rows = []
        for r in RARITIES:
            n = banner_row["pool_size"][r]
            # Deal the ability types round-robin by the mix weights so every rarity carries the 04 mix.
            kinds, acc = [], {k: 0.0 for k, _ in COMPANION_FALLBACK["mix"]}
            for _ in range(n):
                for k, w in COMPANION_FALLBACK["mix"]:
                    acc[k] += w
                best = max(acc, key=lambda k: acc[k])
                acc[best] -= 1.0
                kinds.append(best)
            for i, kind in enumerate(kinds, 1):
                rows.append({"id": f"comp_{gacha_model.CODE[r].lower()}_{i:02d}", "rarity": r, "ability_type": kind,
                             "dps_pct": self.rarity[r]["companion_dps_pct"], "mp_cost": COMPANION_FALLBACK["mp_cost"],
                             "cooldown_ticks": COMPANION_FALLBACK["cooldown_ticks"], "tier": "MVP"})
        return rows

    # ---- curves
    def hp(self, s):
        c = self.c
        return c["HP0"] * c["HP_G"] ** (s + c["HP_OFF"])

    def gold(self, s):
        c = self.c
        return c["GOLD0"] * c["GOLD_G"] ** (s + c["GOLD_OFF"])

    def exp(self, s):
        c = self.c
        return c["EXP0"] * c["EXP_G"] ** (s + c["EXP_OFF"])

    def train_cost(self, stat, n):
        return self.c[f"TRAIN_COST0[{stat}]"] * self.c[f"TRAIN_COST_G[{stat}]"] ** n

    def hero_lvl_exp(self, lvl):
        return self.c["HERO_LVL_EXP0"] * self.c["HERO_LVL_EXP_G"] ** lvl

    def res_cost(self, rid, k):
        return self.c[f"RES_COST0[{rid}]"] * self.c[f"RES_COST_G[{rid}]"] ** k

    def dupe_level(self, copies):
        return sum(1 for t in self.dupe_cum if t <= copies)

    def level_mult(self, copies, lb):
        lvl = self.dupe_level(copies)
        return 0.0 if lvl < 1 else 1.0 + self.c["DUPE_LVL_BONUS"] * (lvl + lb - 1)


# ================================================================================================ the bot

class Bot:
    def __init__(self, p, seed, policy="greedy", tap=False, tickets=None):
        self.p, self.c, self.seed, self.policy, self.tap = p, p.c, seed, policy, tap
        c = self.c
        self.loot = random.Random(seed * 1_000_003 + 17)
        self.stage, self.kills, self.walled, self.highest = 1, 0, False, 0
        self.carry = 0.0
        self.gold = self.exp_bank = 0.0
        self.hero_level = 0
        self.train = {s: 0 for s in TRAINABLE}
        self.rank = 0
        self.candy = 0.0
        self.coins = 0.0
        self.diamonds = float(c["TUTORIAL_DIAMONDS"])
        self.tickets = {b: 0.0 for b in BANNERS}
        self.ticket_income = dict(tickets) if tickets is not None else {
            b: (c.get(f"TICKETS_PER_DAY[{b}]", 0) if not p.m0 else 0) for b in BANNERS}
        self.research = {rid: 0 for rid in p.research}
        self.state = {}
        self.rng = {}
        for i, b in enumerate(p.banner_ids):
            self.state[b] = gacha_model.State()
            self.rng[b] = gacha_model.Stream(gacha_model.derive(seed, 100 + i))
        self.lb = {}                                   # (banner, id) -> limit breaks
        for kind, iid, n in p.tutorial_items:
            if kind in self.state:
                self.state[kind].copies[iid] = self.state[kind].copies.get(iid, 0) + n
        self.board = fish_model.Board(p.fish)
        self.board.day = fish_model.new_day()
        self.missile_pct = 0.0
        self.best_floor = {"FISH_COIN": 0, "ROCK_CANDY": 0, "EXP": 0}
        self.dungeon_tickets = {k: 0 for k in self.best_floor}
        self.day = 0
        self.active_minutes = 0
        self.first_wall_stage = None
        self.wall = None
        self.walls = []
        self.gate_day = {}                             # awaken rank -> day its stage gate was first reached
        self.awaken_day = {}
        self.banner_level_day = {b: {1: 0} for b in p.banner_ids}
        self.econ = {"gold_kills": 0.0, "gold_offline": 0.0, "gold_spent": {s: 0.0 for s in TRAINABLE},
                     "exp_kills": 0.0, "exp_offline": 0.0, "exp_dungeon": 0.0,
                     "diamonds_earned": float(c["TUTORIAL_DIAMONDS"]), "diamonds_spent": {b: 0.0 for b in BANNERS},
                     "pulls_ticket": {b: 0 for b in BANNERS}, "pulls_diamond": {b: 0 for b in BANNERS},
                     "lplus": {b: 0 for b in BANNERS},
                     "coins_discovery": 0.0, "coins_merge": 0.0, "coins_discard": 0.0, "coins_dungeon": 0.0,
                     "coins_spent": {rid: 0.0 for rid in p.research},
                     "candy_boss": 0.0, "candy_dungeon": 0.0, "candy_overflow": 0.0,
                     "candy_awaken": 0.0, "candy_limit_break": 0.0, "limit_breaks": 0,
                     "diamonds_by_source": {k: 0.0 for k in DIAMOND_SOURCES}, "first_clears": 0, "merges_total": 0}
        self.econ["diamonds_by_source"]["TUTORIAL"] = float(c["TUTORIAL_DIAMONDS"])
        self.week = None                               # weekly quest counters, reset every WEEK_DAYS (06 section 5.3)
        self.achieved = set()                          # achievement row ids claimed (06 section 5.4)
        self.daily = []
        self.day_counters = None
        self._dirty = True
        self._layers = None

    # ------------------------------------------------------------------------------------------ layers (03 7.1)
    def item_mult(self, banner, iid, extra_copy=0, extra_lb=0):
        copies = self.state[banner].copies.get(iid, 0) + extra_copy
        cap = self.p.dupe_cum[-1]
        return self.p.level_mult(min(copies, cap), self.lb.get((banner, iid), 0) + extra_lb)

    def weapon_parts(self, probe=None):
        """(equippedWeaponPct, weaponPassivePct) of 04 section 6.1, best weapon equipped."""
        eq = passive = 0.0
        for iid, row in self.p.weapons.items():
            m = self.item_mult("WEAPON", iid, *(probe[1:] if probe and probe[0] == iid else (0, 0)))
            if m > 0:
                eq = max(eq, row["atk_pct"] * m)
                passive += row["passive_pct"] * m
        return eq, passive

    def weapon_mult(self, probe=None):
        eq, passive = self.weapon_parts(probe)
        return 1.0 + (eq + passive) / 100.0

    def skill_pct(self, probe=None):
        vals = []
        for iid, row in self.p.skills.items():
            m = self.item_mult("SKILL", iid, *(probe[1:] if probe and probe[0] == iid else (0, 0)))
            if m > 0:
                vals.append(row["dps_pct"] * m)
        vals.sort(reverse=True)
        return sum(vals[: self.c["SKILL_SLOTS"]])

    def companion_slots(self):
        slots = COMPANION_SLOTS_AT_RANK0
        for r in self.p.awakening:
            if r["rank"] <= self.rank:
                slots = r["companion_slots_after"]
        return min(slots, self.c["COMPANION_SLOTS"])

    def mp_regen(self):
        lvl = self.research.get("res_mp_regen", 0)
        return self.c["MP_REGEN0"] + (self.c["RES_EFFECT[res_mp_regen]"] * lvl if lvl else 0.0)

    def companion_layer(self, probe=None):
        """(mult, mpSufficiency, starved): best loadout with zero or one reserved MP_REGEN slot (04 section 8.2)."""
        if "COMPANION" not in self.state:
            return 1.0, 1.0, False
        tick_hz = self.c["TICK_HZ"]
        dmg, regen = [], []
        for iid, row in self.p.companions.items():
            m = self.item_mult("COMPANION", iid, *(probe[1:] if probe and probe[0] == iid else (0, 0)))
            if m <= 0:
                continue
            cd_s = row["cooldown_ticks"] / tick_hz
            if row["ability_type"] in ("DAMAGE", "BUFF_ATK"):
                dmg.append((row["dps_pct"] * m, row["mp_cost"] / cd_s))
            elif row["ability_type"] == "MP_REGEN":
                regen.append(((row["dps_pct"] * m / 4.0 - row["mp_cost"]) / cd_s,))
            # BUFF_GOLD: no DPS (04 section 8.1); the bot does not equip it for the rated layer.
        dmg.sort(reverse=True)
        regen.sort(reverse=True)
        slots = self.companion_slots()
        best = (1.0, 1.0, False)
        for k in range(0, min(1, len(regen), slots) + 1):
            chosen = dmg[: slots - k]
            supply = self.mp_regen() + sum(r[0] for r in regen[:k])
            demand = sum(d[1] for d in chosen)
            suff = 1.0 if demand == 0 else min(1.0, supply / demand)
            mult = 1.0 + suff * sum(d[0] for d in chosen) / 100.0
            if mult > best[0] or k == 0:
                best = (mult, suff, suff < 1.0)
        return best

    def crit(self):
        c = self.c
        n = min(self.train["CRIT_RATE"], c["TRAIN_CAP[CRIT_RATE]"] or 10 ** 9)
        rate = c["TRAIN_PER_LVL[CRIT_RATE]"] * n
        dmg = c["CRIT_DMG_BASE"] + c["TRAIN_PER_LVL[CRIT_DMG]"] * self.train["CRIT_DMG"]
        return 1.0 + rate * (dmg - 1.0)

    def speed(self):
        c = self.c
        return 1.0 + c["TRAIN_PER_LVL[ATK_SPD]"] * min(self.train["ATK_SPD"], c["TRAIN_CAP[ATK_SPD]"] or 10 ** 9)

    def awaken_mult(self):
        m = 1.0
        for r in self.p.awakening:
            if r["rank"] <= self.rank:
                m *= self.c[f"AWAKEN_MULT[{r['rank']}]"]
        return m

    def research_pct(self):
        lvl = self.research.get("res_atk", 0)
        return self.c["RES_EFFECT[res_atk]"] * lvl if lvl else 0.0

    def gold_mult(self):
        m = 1.0 + self.c["TRAIN_PER_LVL[GOLD_GAIN]"] * self.train["GOLD_GAIN"]
        k = self.research.get("res_gold_gain", 0)
        if k:
            m *= 1.0 + self.c["RES_EFFECT[res_gold_gain]"] * k
        return m

    def exp_mult(self):
        return 1.0 + self.c["TRAIN_PER_LVL[EXP_GAIN]"] * self.train["EXP_GAIN"]

    def layers(self):
        if not self._dirty:
            return self._layers
        c = self.c
        comp, suff, starved = self.companion_layer()
        board = self.board.dps_pct()
        L = {
            "Training": 1.0 + c["TRAIN_PER_LVL[ATK]"] * self.train["ATK"],
            "Hero": c["HERO_LVL_MULT"] ** self.hero_level,
            "Weapons": self.weapon_mult(),
            "Skills": 1.0 + self.skill_pct() / 100.0,
            "Companions": comp,
            "Fish": 1.0 + (board + self.missile_pct) / 100.0,
            "Research": 1.0 + self.research_pct(),
            "Runes": 1.0,
            "Awaken": self.awaken_mult(),
            "Crit": self.crit(),
            "Speed": self.speed(),
        }
        self._layers = L
        self._board_pct, self._mp_suff, self._starved = board, suff, starved
        self._dps = c["BASE_ATK"] * math.prod(L.values())
        self._dirty = False
        return L

    def dps(self):
        self.layers()
        return self._dps

    def hero_hit(self):
        L = self.layers()
        return (self.c["BASE_ATK"] * L["Training"] * L["Hero"] * L["Weapons"] * (1 + self._board_pct / 100.0)
                * L["Research"] * L["Runes"] * L["Awaken"])

    def dirty(self):
        self._dirty = True

    # ------------------------------------------------------------------------------------------ combat (03 2.6-2.8)
    def attack_s(self):
        return self.c["ATTACK_INTERVAL_TICKS"] / self.c["TICK_HZ"]

    def spawn_s(self):
        return self.c["SPAWN_DELAY_TICKS"] / self.c["TICK_HZ"]

    def kill_time(self, s, dps=None):
        dps = dps or self.dps()
        sp = self.speed()
        hits = math.ceil(self.p.hp(s) * sp / dps - 1e-9)
        return max(1, hits) * self.attack_s() / sp + self.spawn_s()

    def boss_fight(self, s, dps, allow_tap=True):
        """(cleared, seconds) for the boss of stage s at rated DPS `dps` (03 section 2.8; tap variant adds Tap Zap)."""
        c = self.c
        sp = self.speed()
        hp = c["BOSS_HP_MULT"] * self.p.hp(s)
        hit = dps / sp
        possible = math.floor(c["BOSS_TIME_TICKS"] * sp / c["ATTACK_INTERVAL_TICKS"] + 1e-9)
        timer_s = c["BOSS_TIME_TICKS"] / c["TICK_HZ"]
        tap = self.tap and allow_tap
        tap_dps = self.hero_hit() * c["TAP_ZAP_DMG_PCT"] * c["TAP_ZAP_MAX_PER_SEC"] if tap else 0.0
        if tap:
            ok = possible * hit + tap_dps * timer_s >= hp
            t = min(timer_s, hp / (dps + tap_dps))
        else:
            need = math.ceil(hp / hit - 1e-9)
            ok = need <= possible
            t = need * self.attack_s() / sp
        return ok, (t if ok else timer_s) + self.spawn_s()

    def boss_clearable(self):
        return self.boss_fight(self.stage, self.dps())[0]

    def tap_clearable(self):
        """The same boss with Tap Zap at TAP_ZAP_MAX_PER_SEC for the whole timer (03 section 2.5): the tapping variant."""
        tap, self.tap = self.tap, True
        try:
            return self.boss_fight(self.stage, self.dps())[0]
        finally:
            self.tap = tap

    def grant_kill(self, s, boss):
        g = self.p.gold(s) * self.gold_mult() * (self.c["BOSS_GOLD_MULT"] if boss else 1.0)
        e = self.p.exp(s) * self.exp_mult()
        self.gold += g
        self.econ["gold_kills"] += g
        self.econ["exp_kills"] += e
        self.add_exp(e)
        if self.day_counters is not None:
            self.day_counters["kills"] += 1

    def add_exp(self, e):
        self.exp_bank += e
        while self.exp_bank >= self.p.hero_lvl_exp(self.hero_level):
            self.exp_bank -= self.p.hero_lvl_exp(self.hero_level)
            self.hero_level += 1
            self._dirty = True

    def advance(self):
        self.highest = max(self.highest, self.stage)
        self.stage += 1
        self.kills = 0
        self.walled = False
        for r in self.p.awakening:
            if self.highest >= self.c[f"AWAKEN_STAGES[{r['rank']}]"] and r["rank"] not in self.gate_day:
                self.gate_day[r["rank"]] = self.day

    def fight(self, seconds):
        c = self.c
        t = self.carry + seconds
        dps = self.dps()
        kps, every = c["KILLS_PER_STAGE"], c["BOSS_EVERY"]
        while True:
            s = self.stage
            if self.walled:
                kt = self.kill_time(s, dps)
                n = int(t // kt)
                for _ in range(n):
                    self.grant_kill(s, False)
                t -= n * kt
                break
            if s % every == 0 and self.kills == kps - 1:
                ok, ft = self.boss_fight(s, dps)
                if t < ft:
                    break
                t -= ft
                self.count_boss_attempt()
                if ok:
                    if s > self.highest:
                        self.grant_first_clear()
                    self.grant_kill(s, True)
                    if self.loot.random() < c["BOSS_ROCK_CANDY_P"]:
                        self.candy += 1
                        self.econ["candy_boss"] += 1
                    if self.wall and self.wall["stage"] == s:
                        self.walls.append(self.wall)
                        self.wall = None
                    self.advance()
                else:
                    self.walled = True
                    if self.first_wall_stage is None:
                        self.first_wall_stage = s
                    if self.wall is None:
                        self.wall = {"stage": s, "day": self.day, "active_min": 0}
                    self.wall["attempts"] = self.wall.get("attempts", 0) + 1
                continue
            kt = self.kill_time(s, dps)
            if t < kt:
                break
            t -= kt
            self.grant_kill(s, False)
            self.kills += 1
            if self.kills >= kps:
                self.advance()
        self.carry = t

    # ------------------------------------------------------------------------------------------ spending
    def train_value(self, stat):
        """Marginal ln(DPS) of one more level (03 section 5.1 closed forms); investment stats at half their payback."""
        c, n = self.c, self.train[stat]
        per = c[f"TRAIN_PER_LVL[{stat}]"]
        cap = c[f"TRAIN_CAP[{stat}]"]
        if cap is not None and n >= cap:
            return 0.0
        if stat == "ATK":
            return math.log((1 + per * (n + 1)) / (1 + per * n))
        if stat == "ATK_SPD":
            return math.log((1 + per * (n + 1)) / (1 + per * n))
        if stat in ("CRIT_RATE", "CRIT_DMG"):
            base = self.crit()
            self.train[stat] += 1
            nxt = self.crit()
            self.train[stat] -= 1
            return math.log(nxt / base)
        gain = math.log(1 + per / (1 + per * n))
        if stat == "GOLD_GAIN":
            atk = c["TRAIN_PER_LVL[ATK]"]
            atk_level_value = math.log((1 + atk * (self.train["ATK"] + 1)) / (1 + atk * self.train["ATK"]))
            return INVEST_VALUE_SHARE * gain / math.log(c["TRAIN_COST_G[ATK]"]) * atk_level_value
        return INVEST_VALUE_SHARE * gain / math.log(c["HERO_LVL_EXP_G"]) * math.log(c["HERO_LVL_MULT"])   # EXP_GAIN

    def spend_gold(self):
        while True:
            best, best_v, best_cost = None, 0.0, 0.0
            for s in TRAINABLE:
                cost = self.p.train_cost(s, self.train[s])
                if cost > self.gold:
                    continue
                v = self.train_value(s) / cost
                if v > best_v:
                    best, best_v, best_cost = s, v, cost
            if best is None:
                return
            self.gold -= best_cost
            self.econ["gold_spent"][best] += best_cost
            self.train[best] += 1
            if self.day_counters is not None:
                self.day_counters["train_levels"] += 1
            self._dirty = True

    def next_gate(self):
        for r in self.p.awakening:
            if r["rank"] == self.rank + 1:
                return self.c[f"AWAKEN_STAGES[{r['rank']}]"], self.c[f"AWAKEN_COST_ROCK_CANDY[{r['rank']}]"]
        return None, None

    def spend_candy(self):
        gate, cost = self.next_gate()
        if gate is not None and self.highest >= gate and self.candy >= cost:
            self.candy -= cost
            self.econ["candy_awaken"] += cost
            self.rank += 1
            self.awaken_day[self.rank] = self.day
            self._dirty = True
            return self.spend_candy()
        if self.p.m0 or (gate is not None and gate - self.highest <= LIMIT_BREAK_GATE_STAGES):
            return
        cheapest = min(self.c[f"LIMIT_BREAK_COST[{r}]"] for r in RARITIES)
        while self.candy >= cheapest:
            lb = self.best_limit_break()
            if lb is None:
                return
            (b, iid), cost = lb
            self.candy -= cost
            self.econ["candy_limit_break"] += cost
            self.econ["limit_breaks"] += 1
            self.day_counters["limit_breaks"] += 1
            self.lb[(b, iid)] = self.lb.get((b, iid), 0) + 1
            self._dirty = True

    def layer_of(self, banner, probe=None):
        if banner == "WEAPON":
            return self.weapon_mult(probe)
        if banner == "SKILL":
            return 1.0 + self.skill_pct(probe) / 100.0
        return self.companion_layer(probe)[0]

    def best_limit_break(self):
        best, best_v = None, -1.0
        ranks = self.c["LIMIT_BREAK_RANKS"]
        for b in self.p.banner_ids:
            base = self.layer_of(b)
            rows = self.item_rows(b)
            for iid, n in self.state[b].copies.items():
                if n <= 0 or self.lb.get((b, iid), 0) >= ranks:
                    continue
                cost = self.c[f"LIMIT_BREAK_COST[{rows[iid]['rarity']}]"] * (self.lb.get((b, iid), 0) + 1)
                if cost > self.candy:
                    continue
                v = math.log(self.layer_of(b, (iid, 0, 1)) / base) / cost
                if v > best_v:
                    best, best_v = ((b, iid), cost), v
        return best

    def item_rows(self, b):
        return {"WEAPON": self.p.weapons, "SKILL": self.p.skills, "COMPANION": self.p.companions}[b]

    def research_open(self, rid):
        u = self.p.unlocks.get("RESEARCH")
        gate = u["threshold"] if u and u["condition"] == "AWAKEN_RANK" else 0
        return self.rank >= max(gate, self.p.research[rid].get("unlock_awaken_rank", 0))

    def research_value(self, rid):
        c, k = self.c, self.research[rid]
        eff = c[f"RES_EFFECT[{rid}]"]
        if rid == "res_atk":
            p = self.research_pct()
            return math.log((1 + p + eff) / (1 + p))
        if rid == "res_gold_gain":
            atk = c["TRAIN_PER_LVL[ATK]"]
            atk_level_value = math.log((1 + atk * (self.train["ATK"] + 1)) / (1 + atk * self.train["ATK"]))
            gain = math.log((1 + eff * (k + 1)) / (1 + eff * k))
            return INVEST_VALUE_SHARE * gain / math.log(c["TRAIN_COST_G[ATK]"]) * atk_level_value
        if rid in ("res_tank_cap", "res_tank_refill"):
            return RES_TANK_NATS_PER_LEVEL
        if rid == "res_fish_cap":
            cap = self.fish_cap()
            frac = self.board.count[cap] / max(1, self.board.max_cap_fish(cap))
            return RES_FISH_CAP_VALUE * math.log(1.5) * frac
        return 0.0   # res_mp_pool (burst only, not in the rated DPS) and res_offline_cap: not in 03 8.3 item 4's list

    def spend_coins(self):
        if not self.p.research:
            return
        while True:
            self.layers()
            if self._starved and "res_mp_regen" in self.research and self.research_open("res_mp_regen") \
                    and self.research["res_mp_regen"] < self.c["RES_MAX[res_mp_regen]"]:
                cands = ["res_mp_regen"]
            else:
                cands = [r for r in ("res_atk", "res_gold_gain", "res_tank_cap", "res_tank_refill", "res_fish_cap")
                         if r in self.research]
            best, best_v, best_cost = None, 0.0, 0.0
            for rid in cands:
                if not self.research_open(rid) or self.research[rid] >= self.c[f"RES_MAX[{rid}]"]:
                    continue
                cost = self.p.res_cost(rid, self.research[rid])
                if cost > self.coins:
                    continue
                v = self.research_value(rid) / cost
                if v > best_v or (rid == "res_mp_regen" and best is None):
                    best, best_v, best_cost = rid, v, cost
            if best is None:
                return
            self.coins -= best_cost
            self.econ["coins_spent"][best] += best_cost
            self.research[best] += 1
            self._dirty = True

    # ------------------------------------------------------------------------------------------ summons (04 3, 03 8.3 item 2)
    def pull_value(self, b):
        """Expected ln(layer) gain per pull over the current level's rate table, against the current collection."""
        bn, st = self.p.banner[b], self.state[b]
        rates = bn.rates(bn.level_for(st.exp + bn.exp_per_pull))
        base = self.layer_of(b)
        ev = 0.0
        for r in RARITIES:
            pool = bn.pool[r]
            if not pool or rates[r] <= 0:
                continue
            gain = sum(math.log(self.layer_of(b, (iid, 1, 0)) / base) for iid in pool) / len(pool)
            ev += rates[r] * gain
        return ev

    def apply_pulls(self, b, results, paid):
        bn, st = self.p.banner[b], self.state[b]
        for res in results:
            if res["rarity"] in ("LEGENDARY", "MYTHIC"):
                self.econ["lplus"][b] += 1
            if res["overflowRockCandy"]:
                self.candy += res["overflowRockCandy"]
                self.econ["candy_overflow"] += res["overflowRockCandy"]
        key = "pulls_diamond" if paid else "pulls_ticket"
        self.econ[key][b] += len(results)
        self.day_counters["pulls"][b] += len(results)
        lvl = bn.level_for(st.exp)
        for L in range(1, lvl + 1):
            self.banner_level_day[b].setdefault(L, self.day)
        self._dirty = True

    def do_summons(self):
        for b in self.p.banner_ids:
            while self.tickets[b] >= 1:
                self.tickets[b] -= 1
                self.apply_pulls(b, [gacha_model.pull(self.p.banner[b], self.state[b], self.rng[b], False)], False)
        while True:
            best, best_v = None, -1.0
            for b in self.p.banner_ids:
                cost = self.p.banner[b].pull_cost
                if cost > self.diamonds:
                    continue
                v = self.pull_value(b) / cost
                if v > best_v:
                    best, best_v = b, v
            if best is None:
                return
            bn = self.p.banner[best]
            batch = bn.guarantee_batch
            if self.diamonds >= batch * bn.pull_cost:
                self.diamonds -= batch * bn.pull_cost
                self.econ["diamonds_spent"][best] += batch * bn.pull_cost
                self.apply_pulls(best, gacha_model.pull10(bn, self.state[best], self.rng[best]), True)
            else:
                self.diamonds -= bn.pull_cost
                self.econ["diamonds_spent"][best] += bn.pull_cost
                self.apply_pulls(best, [gacha_model.pull(bn, self.state[best], self.rng[best], False)], True)

    # ------------------------------------------------------------------------------------------ fish board (05 8)
    def fish_res(self):
        return {"RES_TANK_CAP": self.research.get("res_tank_cap", 0),
                "RES_TANK_REFILL": self.research.get("res_tank_refill", 0),
                "RES_FISH_CAP": self.research.get("res_fish_cap", 0)}

    def fish_cap(self):
        return self.p.fish.fish_cap(self.fish_res())

    def board_minute(self):
        f = self.p.fish
        res = self.fish_res()
        cap, refill, fcap = f.tank_cap(res), f.refill_ticks(res), f.fish_cap(res)
        before = dict(self.board.day)
        ticks = 60 * self.c["TICK_HZ"]
        step = min(ticks, cap * refill)
        left = ticks
        while left > 0:
            t = min(step, left)
            left -= t
            self.board.tick_tank(t, cap, refill)
            if self.policy == "greedy":
                fish_model.act(self.board, max(1, int(f.c["TANK_RELEASE_LEVEL"])), fcap, discard=True)
        released = self.board.day["released"] - before["released"]
        merged = self.board.day["merges"] - before["merges"]
        self.missile_pct = f.missile_dps_pct(released, fcap, self.speed()) if merged > 0 else 0.0
        self._collect_coins(before)
        self._dirty = True

    def _collect_coins(self, before):
        for k in ("coins_discovery", "coins_merge", "coins_discard"):
            d = self.board.day[k] - before[k]
            self.coins += d
            self.econ[k] += d
            self.day_counters["coins_board"] += d

    def board_visit(self):
        before = dict(self.board.day)
        f = self.p.fish
        fish_model.act(self.board, max(1, int(f.c["TANK_RELEASE_LEVEL"])), self.fish_cap(), discard=False)
        self._collect_coins(before)
        self._dirty = True

    # ------------------------------------------------------------------------------------------ dungeons (06 6)
    def dungeons_open(self):
        u = self.p.unlocks.get("DUNGEONS")
        if self.p.m0 or u is None:
            return False
        return u["condition"] != "STAGE_HIGHEST" or self.highest >= (u["threshold"] or 0)

    def run_dungeons(self):
        if not self.dungeons_open():
            return
        c = self.c
        L = self.layers()
        dps = self.dps() / L["Fish"] * (1 + self._board_pct / 100.0)   # passive board only, no missiles (issue #40)
        step = c["DUNGEON_floor_stage_step"]
        for kind in self.best_floor:
            while self.dungeon_tickets[kind] >= 1:
                nxt = self.best_floor[kind] + 1
                floor = self.best_floor[kind]
                if nxt <= c["DUNGEON_floors"] and self.boss_fight(step * nxt, dps, allow_tap=False)[0]:   # no Tap Zap in runs
                    floor = self.best_floor[kind] = nxt
                if floor == 0:
                    break
                self.dungeon_tickets[kind] -= 1
                self.day_counters["dungeon_clears"][kind] += 1   # a clear or a sweep (06 section 5.2 d_dungeon_*)
                if kind == "FISH_COIN":
                    amt = c["DUNGEON_FISH_COIN"] * floor
                    if c["FISH_COIN_PER_DAY"] is None:
                        self.coins += amt
                        self.econ["coins_dungeon"] += amt
                        self.day_counters["coins_dungeon"] += amt
                elif kind == "ROCK_CANDY":
                    amt = c["DUNGEON_ROCK_CANDY"] * floor
                    if c["ROCK_CANDY_PER_DAY"] is None:
                        self.candy += amt
                        self.econ["candy_dungeon"] += amt
                else:
                    e = c["DUNGEON_EXP_KILLS"] * self.p.exp(step * floor)
                    self.econ["exp_dungeon"] += e
                    self.add_exp(e)

    # ------------------------------------------------------------------------------------------ diamonds and quests (06 2, 5)
    def grant_diamonds(self, source, amount):
        if amount:
            self.diamonds += amount
            self.econ["diamonds_earned"] += amount
            self.econ["diamonds_by_source"][source] += amount

    def start_day_diamonds(self):
        """Day-start diamonds: LOGIN_DIAMONDS (06 section 4.2), or the flat DIAMONDS_PER_DAY_F2P in --diamonds flat."""
        if self.p.diamond_mode == "flat":
            self.grant_diamonds("FLAT", self.c["DIAMONDS_PER_DAY_F2P"])
        else:
            self.grant_diamonds("LOGIN", self.c["LOGIN_DIAMONDS"])
        if (self.day - 1) % WEEK_DAYS == 0:
            self.week = {"progress": {}, "claimed": set()}

    def count_boss_attempt(self):
        if self.day_counters is not None:
            self.day_counters["boss_attempts"] += 1

    def grant_first_clear(self):
        """FIRST_CLEAR_DIAMONDS for a boss stage cleared for the first time (06 section 1.2, M0)."""
        self.econ["first_clears"] += 1
        if self.p.diamond_mode != "flat":
            self.grant_diamonds("FIRST_CLEAR", self.c["FIRST_CLEAR_DIAMONDS"])

    def _quest_value(self, goal, flt):
        """Today's progress toward a daily or weekly goal (06 section 5.1); counted goals sum over the week."""
        d = self.day_counters
        if goal in ("LOGIN", "LOGIN_DAYS"):
            return 1
        if goal == "KILL_ENEMIES":
            return d["kills"]
        if goal == "ATTEMPT_BOSS":
            return d["boss_attempts"]
        if goal == "SUMMON":
            return d["pulls"][flt] if flt else sum(d["pulls"].values())
        if goal == "RELEASE_FISH":
            return self.board.day["released"]
        if goal == "MERGE_FISH":
            return self.board.day["merges"]
        if goal == "CLEAR_DUNGEON":
            return d["dungeon_clears"][flt] if flt else sum(d["dungeon_clears"].values())
        if goal == "TRAIN_LEVELS":
            return d["train_levels"]
        raise ValueError(f"power_curve_model: no model for quest goal {goal}")

    def _achievement_value(self, goal):
        owned = lambda b: sum(1 for n in self.state[b].copies.values() if n > 0) if b in self.state else 0
        values = {"REACH_STAGE": lambda: self.highest, "REACH_HERO_LEVEL": lambda: self.hero_level,
                  "OWN_WEAPONS": lambda: owned("WEAPON"), "OWN_SKILLS": lambda: owned("SKILL"),
                  "OWN_COMPANIONS": lambda: owned("COMPANION"), "LIFETIME_MERGES": lambda: self.econ["merges_total"],
                  "FISH_LEVEL": lambda: max(self.board.discovered, default=0), "AWAKEN_RANK": lambda: self.rank,
                  "LIFETIME_SUMMONS": lambda: sum(self.econ["pulls_ticket"].values()) + sum(self.econ["pulls_diamond"].values())}
        if goal not in values:
            raise ValueError(f"power_curve_model: no model for achievement goal {goal}")
        return values[goal]()

    def claim_quests(self):
        """End of day: the bot claims every completed daily, weekly and achievement row (10 section 3.4, 06 section 5).

        Only DIAMOND rewards are paid here; summon-ticket income stays on the TICKETS_PER_DAY path."""
        self.econ["merges_total"] += self.board.day["merges"]
        if self.p.diamond_mode == "flat":
            return
        bonus = lambda r: r["goal"] in ("DAILIES_COMPLETED", "WEEKLIES_COMPLETED")
        pay = lambda r, src: self.grant_diamonds(src, r["reward_amount"] if r["reward_currency"] == "DIAMOND" else 0)
        done = [r for r in self.p.quests_daily if not bonus(r) and self._quest_value(r["goal"], r["filter"]) >= r["target"]]
        self.day_counters["dailies_done"] = len(done)
        for r in done + [r for r in self.p.quests_daily if r["goal"] == "DAILIES_COMPLETED" and len(done) >= r["target"]]:
            pay(r, "QUEST_DAILY")
        wk = self.week
        prog = wk["progress"]
        for r in self.p.quests_weekly:
            if r["goal"] == "DAILIES_COMPLETED":
                prog[r["id"]] = prog.get(r["id"], 0) + len(done)
            elif r["goal"] != "WEEKLIES_COMPLETED":
                prog[r["id"]] = prog.get(r["id"], 0) + self._quest_value(r["goal"], r["filter"])
        n_weekly = sum(1 for r in self.p.quests_weekly if not bonus(r) and prog[r["id"]] >= r["target"])
        for r in self.p.quests_weekly:
            ok = n_weekly >= r["target"] if r["goal"] == "WEEKLIES_COMPLETED" else prog[r["id"]] >= r["target"]
            if ok and r["id"] not in wk["claimed"]:
                wk["claimed"].add(r["id"])
                pay(r, "QUEST_WEEKLY")
        for r in self.p.achievements:
            if r["id"] not in self.achieved and self._achievement_value(r["goal"]) >= r["target"]:
                self.achieved.add(r["id"])
                pay(r, "ACHIEVEMENT")

    # ------------------------------------------------------------------------------------------ time
    def offline(self, gap_s):
        c = self.c
        if gap_s < c["OFFLINE_MIN_S"]:
            credited = 0
        else:
            cap_h = c["OFFLINE_CAP_H"] + (c["RES_EFFECT[res_offline_cap]"] * self.research.get("res_offline_cap", 0)
                                          if "res_offline_cap" in self.research else 0.0)
            credited = min(gap_s, cap_h * 3600)
        if credited:
            kt = self.kill_time(self.stage)
            g = credited * c["OFFLINE_RATE"] * self.p.gold(self.stage) * self.gold_mult() / kt
            e = credited * c["OFFLINE_RATE"] * self.p.exp(self.stage) * self.exp_mult() / kt
            self.gold += g
            self.econ["gold_offline"] += g
            self.econ["exp_offline"] += e
            self.add_exp(e)
        f = self.p.fish
        res = self.fish_res()
        self.board.tick_tank(int((credited if credited else gap_s) * c["TICK_HZ"]), f.tank_cap(res), f.refill_ticks(res))
        self.carry = 0.0

    def start_day(self):
        c = self.c
        self.day_counters = {"pulls": {b: 0 for b in BANNERS}, "limit_breaks": 0, "coins_board": 0.0,
                             "coins_dungeon": 0.0, "kills": 0, "boss_attempts": 0, "train_levels": 0,
                             "dungeon_clears": {k: 0 for k in self.best_floor}, "dailies_done": 0}
        self.start_day_diamonds()
        for b in BANNERS:
            self.tickets[b] += self.ticket_income.get(b, 0)
        if not self.p.m0:
            for k in self.dungeon_tickets:
                self.dungeon_tickets[k] = max(self.dungeon_tickets[k], c["DUNGEON_tickets_per_day"])
        if c["ROCK_CANDY_PER_DAY"] is not None:
            self.candy += c["ROCK_CANDY_PER_DAY"]
            self.econ["candy_dungeon"] += c["ROCK_CANDY_PER_DAY"]
        if c["FISH_COIN_PER_DAY"] is not None:
            self.coins += c["FISH_COIN_PER_DAY"]
            self.econ["coins_dungeon"] += c["FISH_COIN_PER_DAY"]
            self.day_counters["coins_dungeon"] += c["FISH_COIN_PER_DAY"]
        self.board.day = fish_model.new_day()

    def session_start(self, first_of_day):
        if first_of_day:
            self.do_summons()
        self.run_dungeons()
        self.spend_all()
        if self.policy == "lazy":
            self.board_visit()
            self.spend_all()

    def spend_all(self):
        self.spend_gold()
        self.spend_candy()
        self.spend_coins()

    def active_minute(self):
        if self.walled and self.policy == "greedy" and self.boss_clearable():
            self.walled = False   # AttemptBoss when Query.BossClearable (10 section 3.4); the attempt itself is free
        elif self.walled and self.policy == "greedy":
            self.count_boss_attempt()   # 03 8.3 item 6: a walled bot retries every active minute (counts for d_boss)
        self.fight(60.0)
        self.active_minutes += 1
        if self.wall is not None:
            self.wall["active_min"] += 1
            if self.walled and self.wall.get("tap_min") is None and self.tap_clearable():
                self.wall["tap_min"] = self.wall["active_min"]   # a tapping player would have broken it by now
        if self.policy == "greedy":
            self.board_minute()
            self.spend_all()

    def lazy_retry(self):
        if self.walled and self.boss_clearable():
            self.walled = False

    def run(self, days, active_min, sessions):
        session_min = [active_min // sessions + (1 if i < active_min % sessions else 0) for i in range(sessions)]
        gap_s = (24 * 60 - active_min) * 60 // sessions
        for d in range(1, days + 1):
            self.day = d
            self.start_day()
            for s in range(sessions):
                if not (d == 1 and s == 0):
                    self.offline(gap_s)
                if self.policy == "lazy":
                    self.lazy_retry()
                self.session_start(s == 0)
                for _ in range(session_min[s]):
                    self.active_minute()
            self.claim_quests()
            self.daily.append(self.snapshot())
        if self.wall is not None:
            self.walls.append(dict(self.wall, open=True))
        return self

    # ------------------------------------------------------------------------------------------ reporting
    def snapshot(self):
        L = dict(self.layers())
        dps = self.dps()
        total = math.log(dps / self.c["BASE_ATK"])
        shares = {k: (math.log(v) / total if total > 0 else 0.0) for k, v in L.items()}
        return {
            "day": self.day, "stage": self.stage, "highest_cleared": self.highest, "dps": dps,
            "layers": L, "shares": shares, "weapon_pct": dict(zip(("equipped", "passive"), self.weapon_parts())), "training": dict(self.train), "hero_level": self.hero_level,
            "awaken_rank": self.rank, "fish_cap": self.fish_cap(), "board_dps_pct": self._board_pct,
            "missile_dps_pct": self.missile_pct, "mp_sufficiency": self._mp_suff, "research": dict(self.research),
            "pulls": {b: self.econ["pulls_ticket"][b] + self.econ["pulls_diamond"][b] for b in self.p.banner_ids},
            "pulls_today": dict(self.day_counters["pulls"]),
            "lplus": {b: self.econ["lplus"][b] for b in self.p.banner_ids},
            "banner_level": {b: self.p.banner[b].level_for(self.state[b].exp) for b in self.p.banner_ids},
            "limit_breaks": self.econ["limit_breaks"], "limit_breaks_today": self.day_counters["limit_breaks"],
            "coins_board_today": self.day_counters["coins_board"], "coins_dungeon_today": self.day_counters["coins_dungeon"],
            "diamonds": self.diamonds, "rock_candy": self.candy, "fish_coins": self.coins,
            "walled": self.walled,
            "econ_cum": flat_econ(self.econ), "best_floor": dict(self.best_floor),
            "dailies_done": self.day_counters["dailies_done"],
        }


# ================================================================================================ checks

def wall_stats(bot, lo_day=1, hi_day=10 ** 9):
    ws = [w for w in bot.walls if lo_day <= w["day"] <= hi_day]
    return max(ws, key=lambda w: w["active_min"]) if ws else None


def flat_econ(e):
    """The econ counters as one flat {name: number} dict (nested per-banner/per-stat dicts become 'name.key')."""
    out = {}
    for k, v in e.items():
        if isinstance(v, dict):
            for kk, vv in v.items():
                out[f"{k}.{kk}"] = vv
        else:
            out[k] = v
    return out


def day_flow(bot, d):
    """06 section 2 / 11 quantities earned and spent on day d (cumulative counters differenced against day d - 1)."""
    row = bot.daily[d - 1]
    cur = row["econ_cum"]
    prev = bot.daily[d - 2]["econ_cum"] if d > 1 else {}
    dx = lambda k: cur.get(k, 0) - prev.get(k, 0)
    sdx = lambda pre: sum(dx(k) for k in cur if k.startswith(pre + "."))
    f = {"day": d, "stage": row["stage"], "new_bosses": dx("first_clears"), "dailies_done": row["dailies_done"]}
    for s in DIAMOND_SOURCES:
        f["dia_" + s.lower()] = dx("diamonds_by_source." + s)
    f["dia_total"] = sdx("diamonds_by_source")
    f["dia_spent"] = sdx("diamonds_spent")
    f["dia_balance"] = row["diamonds"]
    lo = (d - 1) // WEEK_DAYS * WEEK_DAYS + 1
    if lo + WEEK_DAYS - 1 > len(bot.daily) and lo > WEEK_DAYS:
        lo -= WEEK_DAYS   # the run ends mid-week (days 29-30): amortise the last whole week instead
    hi = min(lo + WEEK_DAYS - 1, len(bot.daily))
    week = sum(bot.daily[i - 1]["econ_cum"].get("diamonds_by_source.QUEST_WEEKLY", 0)
               - (bot.daily[i - 2]["econ_cum"].get("diamonds_by_source.QUEST_WEEKLY", 0) if i > 1 else 0)
               for i in range(lo, hi + 1))
    f["dia_weekly_amortised"] = week / WEEK_DAYS
    f["gold_earned"] = dx("gold_kills") + dx("gold_offline")
    f["gold_spent"] = sdx("gold_spent")
    f["coins_board"] = dx("coins_discovery") + dx("coins_merge") + dx("coins_discard")
    f["coins_dungeon"] = dx("coins_dungeon")
    f["coins_spent"] = sdx("coins_spent")
    for k in ("dungeon", "boss", "overflow"):
        f["candy_" + k] = dx("candy_" + k)
    f["candy_earned"] = f["candy_dungeon"] + f["candy_boss"] + f["candy_overflow"]
    f["candy_awaken"], f["candy_limit_break"] = dx("candy_awaken"), dx("candy_limit_break")
    f["candy_spent"] = f["candy_awaken"] + f["candy_limit_break"]
    f["exp_dungeon"] = dx("exp_dungeon")
    f["exp_total"] = dx("exp_kills") + dx("exp_offline") + f["exp_dungeon"]
    for k, v in row["best_floor"].items():
        f["best_floor_" + k.lower()] = v
    f["awaken_rank"] = row["awaken_rank"]
    return f


def target_stage(p, day):
    """00 section 6.6 progress_targets interpolated linearly, stage 0 at day 0 (06 section 2 table rule)."""
    pts = [(0, 0)] + [(t["day"], t["stage"]) for t in p.targets]
    for (d0, s0), (d1, s1) in zip(pts, pts[1:]):
        if day <= d1:
            return s0 + (s1 - s0) * (day - d0) / (d1 - d0)
    return pts[-1][1]


def steady_diamonds(bot):
    """(mean daily diamond income over DIAMOND_STEADY_DAYS, max end-of-day balance there), or None if the run is short."""
    lo, hi = DIAMOND_STEADY_DAYS
    if len(bot.daily) < hi:
        return None
    flows = [day_flow(bot, d) for d in range(lo, hi + 1)]
    return statistics.fmean(f["dia_total"] for f in flows), max(f["dia_balance"] for f in flows)


def economy_checks(p, bot):
    """[(status, name, value)] for the 06 economy checks (issue #178). MVP only: M0 has no quests or dungeons."""
    out = []

    def add(ok, name, val):
        out.append(("PASS" if ok else "FAIL", name, val))

    last = len(bot.daily)
    e = bot.econ
    earned = e["gold_kills"] + e["gold_offline"]
    sp = sum(e["gold_spent"].values())
    add(earned == 0 or sp / earned >= GOLD_SPENT_MIN, "06 11 obs. 1: gold is fully spent on Training (>= 99% of earned)",
        f"{sp / earned:.2%}" if earned else "no gold")
    if p.m0:
        return out
    c = p.c
    st = steady_diamonds(bot)
    if p.diamond_mode == "content" and st is not None:
        mean, held = st
        floor = c["DIAMONDS_PER_DAY_F2P"] * (1 - DIAMOND_BUDGET_TOL)
        add(mean >= floor, f"DIAMONDS_PER_DAY_F2P (06 2, OQ1): steady-state income, days {DIAMOND_STEADY_DAYS[0]}-"
            f"{DIAMOND_STEADY_DAYS[1]}, >= {c['DIAMONDS_PER_DAY_F2P']} - {DIAMOND_BUDGET_TOL:.0%}", f"{mean:.0f} a day")
        add(held <= DIAMOND_HOLD_MAX_DAYS * mean, f"06 11 obs. 2: diamonds spent within {DIAMOND_HOLD_MAX_DAYS} days in steady "
            f"state (end-of-day balance <= {DIAMOND_HOLD_MAX_DAYS} x daily income)", f"max balance {held:.0f}")
    if p.diamond_mode == "content":
        cum = lambda d: bot.daily[d - 1]["econ_cum"]["first_clears"] if d >= 1 else 0
        for a, b in FIRST_CLEAR_PERIODS:
            if b > last:
                continue
            want = (target_stage(p, b) - target_stage(p, a - 1)) / c["BOSS_EVERY"] * c["FIRST_CLEAR_DIAMONDS"]
            got = (cum(b) - cum(a - 1)) * c["FIRST_CLEAR_DIAMONDS"]
            days = b - a + 1
            add(abs(got - want) <= FIRST_CLEAR_TOL * want,
                f"FIRST_CLEAR_DIAMONDS (06 2): first-clear diamonds on day{'s' if days > 1 else ''} {a}"
                f"{'-' + str(b) if days > 1 else ''} within {FIRST_CLEAR_TOL:.0%} of the target stage curve's",
                f"{got / days:.0f} a day vs {want / days:.0f}")
    income = st[0] if st is not None else (e["diamonds_earned"] - c["TUTORIAL_DIAMONDS"]) / max(1, last)
    for b in p.banner_ids:
        # 06 section 2: the day's income (diamonds plus that banner's tickets at its pull price) buys one 10-pull every N days.
        cost = p.banner[b].pull_cost
        eff = income + bot.ticket_income.get(b, 0) * cost
        every = 10 * cost / eff if eff > 0 else float("inf")
        add(every <= TENPULL_MAX_DAYS[b], f"PULL_COST[{b}] (06 2): income affords a {b} 10-pull every {TENPULL_MAX_DAYS[b]} days "
            "or less", f"every {every:.2f} days ({income:.0f} diamonds + {bot.ticket_income.get(b, 0):g} tickets a day)")
        n = e["pulls_ticket"][b] + e["pulls_diamond"][b]
        out.append(("INFO", f"{b} pulls the bot actually made (its value-per-diamond choice, 03 8.3 item 2)",
                    f"{n} over {last} days, a 10-pull's worth every {last / (n / 10) if n else float('inf'):.2f} days"))
        add(e["lplus"][b] >= 1, f"PITY_HARD (06 2): {b} pity resolves (a Legendary or better) within {last} days",
            f"{e['lplus'][b]} L+")
    for d in EXP_DUNGEON_DAYS:
        if d <= last:
            f = day_flow(bot, d)
            sh = f["exp_dungeon"] / f["exp_total"] if f["exp_total"] else 0.0
            add(EXP_DUNGEON_SHARE_BAND[0] <= sh <= EXP_DUNGEON_SHARE_BAND[1],
                f"EXP_DUNGEON_KILLS (06 consts): EXP Dungeon share of day-{d} EXP in "
                f"{EXP_DUNGEON_SHARE_BAND[0]:.0%}..{EXP_DUNGEON_SHARE_BAND[1]:.0%}", f"{sh:.1%}")
    if last >= BEST_FLOOR_DAY:
        floors = bot.daily[BEST_FLOOR_DAY - 1]["best_floor"]
        for k, v in floors.items():
            add(BEST_FLOOR_BAND[0] <= v <= BEST_FLOOR_BAND[1],
                f"DUNGEON_FLOOR_STAGE_STEP (06 OQ5): {k} best floor on day {BEST_FLOOR_DAY} in {BEST_FLOOR_BAND[0]}..{BEST_FLOOR_BAND[1]}",
                f"floor {v}")
    return out


def checks_for(p, bot):
    """[(status, name, value)] for every 03 section 8.6 S-constant check on one run. status: PASS/FAIL/WARN/INFO."""
    out = []
    days = {r["day"]: r for r in bot.daily}
    last = max(days)

    def add(ok, name, val, warn=False):
        out.append(("PASS" if ok else ("WARN" if warn else "FAIL"), name, val))

    # Shares (03 section 8.5)
    exempt = SHARE_FLOOR_EXEMPT.get(p.tier, set())
    for layer in LAYERS:
        for d in SHARE_MAX_DAYS:
            if d in days:
                sh = days[d]["shares"][layer]
                if sh > SHARE_MAX:
                    add(False, f"share {layer} <= 40% on day {d}", f"{sh:.1%}")
        for d in SHARE_MIN_DAYS:
            if d not in days or layer in SHARE_FLOOR_EXCLUDED:
                continue
            if days[d]["layers"][layer] == 1.0:
                continue   # the layer does not exist in this tier (or has not unlocked): nothing to floor-check
            sh = days[d]["shares"][layer]
            if sh < SHARE_MIN:
                add(False, f"share {layer} >= 5% on day {d}", f"{sh:.1%}", warn=layer in exempt)
    if not any(n.startswith("share ") for _, n, _ in out):
        add(True, "shares: every layer <= 40% on days 1/7/30 and >= 5% on days 7/30 (03 8.5)", "all in band")

    # Walls (AC18)
    fw = bot.first_wall_stage
    add(fw is not None and FIRST_WALL_BAND[0] <= fw <= FIRST_WALL_BAND[1],
        f"BOSS_TIME_TICKS: first wall stage in {FIRST_WALL_BAND[0]}..{FIRST_WALL_BAND[1]}", f"stage {fw}")
    early = wall_stats(bot, 1, EARLY_WALL_LAST_DAY)
    add(early is None or early["active_min"] <= EARLY_WALL_MAX_ACTIVE_MIN,
        f"HP_G/GOLD_G/EXP_G/HERO_LVL_MULT: longest wall opened by day {EARLY_WALL_LAST_DAY} <= "
        f"{EARLY_WALL_MAX_ACTIVE_MIN} active min", _wall_text(early))
    late = wall_stats(bot, EARLY_WALL_LAST_DAY + 1)
    if LATE_WALL_MAX_ACTIVE_MIN is None:
        out.append(("WARN", "longest wall opened on days 15-30 (lateWallMaxActiveMinutes pending, issue #36)",
                    _wall_text(late)))
    else:
        add(late is None or late["active_min"] <= LATE_WALL_MAX_ACTIVE_MIN,
            f"longest wall opened on days 15-30 <= {LATE_WALL_MAX_ACTIVE_MIN} active min", _wall_text(late))

    # Training gold split
    spent = bot.econ["gold_spent"]
    total = sum(spent.values()) or 1.0
    for s in TRAINABLE:
        sh = spent[s] / total
        add(TRAIN_GOLD_SHARE_BAND[0] <= sh <= TRAIN_GOLD_SHARE_BAND[1],
            f"TRAIN_PER_LVL/TRAIN_CAP[{s}]: share of Training gold over {last} days in 5%..50%", f"{sh:.2%}")

    # Hero level per stage
    bad = [(d, days[d]["hero_level"] / days[d]["stage"]) for d in REPORT_DAYS if d in days
           and not HERO_LEVEL_PER_STAGE_BAND[0] <= days[d]["hero_level"] / days[d]["stage"] <= HERO_LEVEL_PER_STAGE_BAND[1]]
    add(not bad, "EXP0/HERO_LVL_EXP: hero level between 0.25 s and 0.45 s at every reported day",
        ", ".join(f"day {d}: {r:.2f} s" for d, r in bad) if bad else
        ", ".join(f"{days[d]['hero_level'] / days[d]['stage']:.2f}" for d in REPORT_DAYS if d in days))

    # Awakening cadence (issue #43)
    for r in p.awakening:
        rank = r["rank"]
        if rank not in bot.gate_day:
            continue
        limit = p.awaken_targets.get(rank)
        got = bot.awaken_day.get(rank)
        lag = None if got is None else got - bot.gate_day[rank]
        ok = lag is not None and (limit is None or lag <= limit)
        if got is None and last - bot.gate_day[rank] <= (limit or 0):
            out.append(("INFO", f"AWAKEN rank {rank}: gate day {bot.gate_day[rank]}, not yet bought (window still open)", "-"))
            continue
        add(ok, f"AWAKEN_STAGES/AWAKEN_COST_ROCK_CANDY: rank {rank} bought within {limit} days of its gate",
            f"gate day {bot.gate_day[rank]}, bought day {got}")

    if not p.m0 and p.research:
        d7 = days.get(RESEARCH_ALL_NODES_BY_DAY)
        if d7:
            zero = [rid for rid, k in d7["research"].items() if k == 0]
            add(not zero, f"RES_*: every node has a level by day {RESEARCH_ALL_NODES_BY_DAY}",
                "none at 0" if not zero else "at 0: " + ", ".join(zero))
        d10 = days.get(FISH_CAP_2_BY_DAY)
        if d10 and "res_fish_cap" in d10["research"]:
            add(d10["research"]["res_fish_cap"] >= 2, f"RES_*: res_fish_cap reaches 2 by day {FISH_CAP_2_BY_DAY}",
                f"level {d10['research']['res_fish_cap']}")
        candy_in = bot.econ["candy_boss"] + bot.econ["candy_dungeon"] + bot.econ["candy_overflow"]
        sh = bot.econ["candy_boss"] / candy_in if candy_in else 0.0
        add(sh < BOSS_CANDY_SHARE_MAX, "BOSS_ROCK_CANDY_P: boss drops under 20% of Rock Candy income", f"{sh:.1%}")
        lb_days = [r for r in bot.daily if r["day"] >= LIMIT_BREAK_DAILY_FROM_DAY]
        no_lb = [r["day"] for r in lb_days if r["limit_breaks_today"] == 0]
        add(lb_days and not no_lb, f"LIMIT_BREAK_COST (04 5.2): at least one limit break a day from day {LIMIT_BREAK_DAILY_FROM_DAY}",
            "every day" if not no_lb else f"none on {len(no_lb)} days (first: day {no_lb[0]})")
        spent_c = bot.econ["candy_awaken"] + bot.econ["candy_limit_break"]
        lbs = bot.econ["candy_limit_break"] / spent_c if spent_c else 0.0
        add(lbs < LIMIT_BREAK_CANDY_SHARE_MAX, "LIMIT_BREAK_COST (04 5.2): limit breaks under 60% of Rock Candy spend", f"{lbs:.1%}")
        if p.c["FISH_COIN_PER_DAY"] is None:
            for d in FISH_DUNGEON_DAYS:
                if d in days:
                    r = days[d]
                    tot = r["coins_board_today"] + r["coins_dungeon_today"]
                    sh = r["coins_dungeon_today"] / tot if tot else 0.0
                    add(FISH_DUNGEON_SHARE_BAND[0] <= sh <= FISH_DUNGEON_SHARE_BAND[1],
                        f"FISH_COIN_FLOOR (06 6.3a): dungeon share of day-{d} Fish Coin income in "
                        f"{FISH_DUNGEON_SHARE_BAND[0]:.0%}..{FISH_DUNGEON_SHARE_BAND[1]:.0%}", f"{sh:.1%}")

    # Tap Zap: per wall, idle active minutes over the minutes until the same boss is clearable with full tapping.
    ratios = sorted(w["active_min"] / max(1, w.get("tap_min") or w["active_min"]) for w in bot.walls if w["active_min"] > 0)
    if ratios:
        longest = max(bot.walls, key=lambda w: w["active_min"])
        lr = longest["active_min"] / max(1, longest.get("tap_min") or longest["active_min"])
        med = statistics.median(ratios)
        add(med <= TAP_WALL_SPEEDUP_MAX, "TAP_ZAP_*: a tapping variant clears a wall at most 2.5x faster than idle (median wall)",
            f"median x{med:.2f}, max x{ratios[-1]:.2f}, longest wall x{lr:.2f}")
    out += economy_checks(p, bot)
    return out


def _wall_text(w):
    if w is None:
        return "no wall"
    return f"{w['active_min']} active min at stage {w['stage']} from day {w['day']}" + (" (still open)" if w.get("open") else "")


# ================================================================================================ output

def fmt(x):
    if x == 0:
        return "0"
    if abs(x) >= 1e5 or abs(x) < 1e-2:
        return f"{x:.2e}"
    return f"{x:,.1f}" if abs(x) < 1000 else f"{x:,.0f}"


def print_day_table(p, rows):
    print("\nDay table (stage, DPS, layer multipliers)")
    print("day  stage  hero  rank  fcap        DPS  " + " ".join(f"{k[:6]:>7}" for k in LAYERS))
    for r in rows:
        print(f"{r['day']:>3}  {r['stage']:>5}  {r['hero_level']:>4}  {r['awaken_rank']:>4}  {r['fish_cap']:>4}  "
              f"{r['dps']:>9.2e}  " + " ".join(f"x{r['layers'][k]:>6.2f}" if r['layers'][k] < 1000 else f"{r['layers'][k]:>7.1e}"
                                             for k in LAYERS))
    print("\nday  Training levels (ATK/CR/CD/SPD/GG/EG)   research levels                         pulls (W/S/C) L+ (W/S/C)  banner lvl  LB")
    for r in rows:
        t = "/".join(str(r["training"][s]) for s in TRAINABLE)
        res = " ".join(f"{k[4:]}:{v}" for k, v in r["research"].items()) or "-"
        pulls = "/".join(str(r["pulls"].get(b, 0)) for b in BANNERS)
        lp = "/".join(str(r["lplus"].get(b, 0)) for b in BANNERS)
        bl = "/".join(str(r["banner_level"].get(b, 0)) for b in BANNERS)
        print(f"{r['day']:>3}  {t:<38}  {res:<54}  {pulls:<12} {lp:<10}  {bl:<10}  {r['limit_breaks']}")


def print_shares(p, rows, checks):
    print("\nLayer shares of ln(DPS / BASE_ATK) (03 section 8.2 format)")
    print("day  stage        DPS  " + " ".join(f"{k[:6]:>7}" for k in LAYERS))
    for r in rows:
        print(f"{r['day']:>3}  {r['stage']:>5}  {r['dps']:>9.2e}  " + " ".join(f"{r['shares'][k]:>7.1%}" for k in LAYERS))
    print("\nper-layer rule (<= 40% on days 1/7/30, >= 5% on days 7/30; speed and runes floor-exempt"
          + (f"; {p.tier} exempts {', '.join(sorted(SHARE_FLOOR_EXEMPT.get(p.tier, [])))} as WARN" if SHARE_FLOOR_EXEMPT.get(p.tier) else "") + ")")
    by_day = {r["day"]: r for r in rows}
    for layer in LAYERS:
        status, notes = "PASS", []
        for st, name, val in checks:
            if name.startswith(f"share {layer} "):
                status = "FAIL" if st == "FAIL" or status == "FAIL" else "WARN"
                notes.append(f"{name.split(' ', 2)[2]} = {val}")
        if layer in ("Runes",) and all(by_day[d]["layers"][layer] == 1.0 for d in by_day):
            status = "n/a"
        print(f"  {status:<4} {layer:<10} " + ("; ".join(notes) if notes else ""))


def print_walls(bot):
    print("\nWall report")
    print(f"  first wall stage: {bot.first_wall_stage}")
    w = wall_stats(bot)
    print(f"  longest wall: {_wall_text(w)}")
    print(f"  total walled active minutes: {sum(x['active_min'] for x in bot.walls)} over {len(bot.walls)} walls")
    top = sorted(bot.walls, key=lambda x: -x["active_min"])[:5]
    for x in top:
        print(f"    stage {x['stage']:>4} day {x['day']:>2}: {x['active_min']:>4} active min, {x.get('attempts', 1)} failed attempts")


def print_economy(bot):
    e = bot.econ
    print("\nEconomy report (cumulative)")
    print(f"  gold earned: kills {fmt(e['gold_kills'])}, offline {fmt(e['gold_offline'])}; spent "
          + ", ".join(f"{s} {fmt(v)}" for s, v in e["gold_spent"].items()))
    print(f"  hero EXP: kills {fmt(e['exp_kills'])}, offline {fmt(e['exp_offline'])}, dungeon {fmt(e['exp_dungeon'])}")
    print(f"  diamonds earned {fmt(e['diamonds_earned'])}; spent " + ", ".join(f"{b} {fmt(v)}" for b, v in e["diamonds_spent"].items()))
    print(f"  Fish Coins earned: discovery {fmt(e['coins_discovery'])}, merges {fmt(e['coins_merge'])}, discards "
          f"{fmt(e['coins_discard'])}, dungeon {fmt(e['coins_dungeon'])}; spent "
          + (", ".join(f"{k} {fmt(v)}" for k, v in e["coins_spent"].items()) or "-"))
    print(f"  Rock Candy earned: boss {fmt(e['candy_boss'])}, dungeon {fmt(e['candy_dungeon'])}, overflow {fmt(e['candy_overflow'])}; "
          f"spent: awakening {fmt(e['candy_awaken'])}, limit breaks {fmt(e['candy_limit_break'])} ({e['limit_breaks']} breaks)")


def print_cadence(p, bot):
    e = bot.econ
    days = len(bot.daily)
    print("\nPer-banner pull cadence (input to 04's BANNER_LVL_EXP retune and gacha_model.py, issue #38)")
    for b in p.banner_ids:
        lv = bot.banner_level_day[b]
        n = e["pulls_ticket"][b] + e["pulls_diamond"][b]
        print(f"  {b:<9} {n / days:6.2f} pulls/day ({e['pulls_ticket'][b]} ticket + {e['pulls_diamond'][b]} diamond over "
              f"{days} days); level reached on day: " + ", ".join(f"L{k} d{v}" for k, v in sorted(lv.items()) if k > 1))
    w = bot.banner_level_day.get("WEAPON", {})
    print(f"  weapon banner level 4: {'day ' + str(w[4]) if 4 in w else 'not reached in ' + str(days) + ' days'} "
          f"(target days 20-35 on the 20-seed median, 00 decision 70; one seed, see --check)")


def median_flows(runs, seeds, days):
    """{day: {key: 20-seed median}} of day_flow for the given days (each column its own median)."""
    out = {}
    for d in days:
        fs = [day_flow(runs[s], d) for s in seeds if d <= len(runs[s].daily)]
        if fs:
            out[d] = {k: statistics.median(f[k] for f in fs) for k in fs[0]}
    return out


def print_economy_tables(p, flows, label):
    """06 section 2 (diamond income by day) and section 11 (sinks by day) as the model computes them (issue #178)."""
    print(f"\n06 section 2: diamond income by day ({label}; weekly rows amortised over their week)")
    print("day  stage  new bosses  dailies  login  dailies+bonus  first clears  weekly  achievements  tutorial  flat  "
          "day total  spent  balance")
    for d in SECTION2_DAYS:
        f = flows.get(d)
        if f:
            total = f["dia_total"] - f["dia_quest_weekly"] + f["dia_weekly_amortised"]
            print(f"{d:>3}  {f['stage']:>5g}  {f['new_bosses']:>10g}  {f['dailies_done']:>7g}  {f['dia_login']:>5g}  "
                  f"{f['dia_quest_daily']:>13g}  {f['dia_first_clear']:>12g}  {f['dia_weekly_amortised']:>6.0f}  "
                  f"{f['dia_achievement']:>12g}  {f['dia_tutorial']:>8g}  {f['dia_flat']:>4g}  {total:>9.0f}  "
                  f"{f['dia_spent']:>5g}  {f['dia_balance']:>7.0f}")
    print(f"\n06 section 11: sinks by day ({label})")
    print("day  stage  gold earned  gold spent  dia earned  dia spent  coins board  coins dungeon  coins spent  "
          "candy earned (dungeon/boss/overflow)  candy spent (awaken/LB)  EXP dungeon share  floors F/C/E  rank")
    for d in SECTION11_DAYS:
        f = flows.get(d)
        if f:
            sh = f["exp_dungeon"] / f["exp_total"] if f["exp_total"] else 0.0
            candy = f"{fmt(f['candy_earned'])} ({fmt(f['candy_dungeon'])}/{fmt(f['candy_boss'])}/{fmt(f['candy_overflow'])})"
            spent = f"{fmt(f['candy_spent'])} ({fmt(f['candy_awaken'])}/{fmt(f['candy_limit_break'])})"
            floors = f"{f['best_floor_fish_coin']:g}/{f['best_floor_rock_candy']:g}/{f['best_floor_exp']:g}"
            print(f"{d:>3}  {f['stage']:>5g}  {fmt(f['gold_earned']):>11}  {fmt(f['gold_spent']):>10}  {f['dia_total']:>10.0f}  "
                  f"{f['dia_spent']:>9.0f}  {fmt(f['coins_board']):>11}  {fmt(f['coins_dungeon']):>13}  {fmt(f['coins_spent']):>11}  "
                  f"{candy:<36}  {spent:<23}  {sh:>17.1%}  {floors:<12}  {f['awaken_rank']:g}")


def median_rows(runs, seeds):
    out = []
    for d in SHARE_DAYS:
        rs = [runs[s].daily[d - 1] for s in seeds if d <= len(runs[s].daily)]
        if not rs:
            continue
        out.append({"day": d, "stage": statistics.median(r["stage"] for r in rs),
                    "dps": statistics.median(r["dps"] for r in rs),
                    "shares": {k: statistics.median(r["shares"][k] for r in rs) for k in LAYERS}})
    return out


def print_sweep(p, a, seeds, runs, rows):
    print(f"\n{len(seeds)}-seed medians: stage, DPS and layer shares")
    print("day   stage        DPS  " + " ".join(f"{k[:6]:>7}" for k in LAYERS))
    for r in rows:
        print(f"{r['day']:>3}  {r['stage']:>6g}  {r['dps']:>9.2e}  " + " ".join(f"{r['shares'][k]:>7.1%}" for k in LAYERS))
    fw = sorted(runs[s].first_wall_stage or 0 for s in seeds)
    early = sorted((wall_stats(runs[s], 1, EARLY_WALL_LAST_DAY) or {"active_min": 0})["active_min"] for s in seeds)
    late = sorted((wall_stats(runs[s], EARLY_WALL_LAST_DAY + 1) or {"active_min": 0})["active_min"] for s in seeds)
    print(f"  first wall stage: median {statistics.median(fw):g}, range {fw[0]}..{fw[-1]}")
    print(f"  longest wall opened by day {EARLY_WALL_LAST_DAY} (active min): median {statistics.median(early):g}, range {early[0]}..{early[-1]}")
    print(f"  longest wall opened on days {EARLY_WALL_LAST_DAY + 1}-{a.days} (active min): median {statistics.median(late):g}, "
          f"range {late[0]}..{late[-1]}  <- computed value for lateWallMaxActiveMinutes (pending, issue #36)")
    for b in p.banner_ids:
        ppd = sorted((runs[s].econ["pulls_ticket"][b] + runs[s].econ["pulls_diamond"][b]) / a.days for s in seeds)
        lv = [max(runs[s].banner_level_day[b]) for s in seeds]
        print(f"  {b} pulls/day: median {statistics.median(ppd):.2f} (range {ppd[0]:.2f}..{ppd[-1]:.2f}); "
              f"banner level at day {a.days}: median {statistics.median(lv):g}  <- cadence input for BANNER_LVL_EXP (issue #38)")
        l4 = sorted(runs[s].banner_level_day[b].get(4, a.days + 1) for s in seeds)
        missed = sum(1 for d in l4 if d > a.days)
        print(f"    {b} level 4 day: median {statistics.median(l4):g} (range {l4[0]}..{l4[-1]}; {missed}/{len(seeds)} seeds "
              f"not by day {a.days}, counted as day {a.days + 1})"
              + ("  <- target days 20-35 (00 decision 70)" if b == "WEAPON" else ""))
    for rank in sorted({r for s in seeds for r in runs[s].gate_day}):
        lags = [runs[s].awaken_day[rank] - runs[s].gate_day[rank] for s in seeds
                if rank in runs[s].gate_day and rank in runs[s].awaken_day]
        if lags:
            print(f"  Awakening rank {rank}: days from gate to purchase median {statistics.median(lags):g}, max {max(lags)} "
                  f"({len(lags)}/{len(seeds)} seeds bought it)")


def daytable_line(stages):
    return "DAYTABLE " + " ".join(f"{d}:{s}" for d, s in stages)


def run_one(p, seed, a):
    tickets = None
    if a.tickets:
        w, s, c = (float(x) for x in a.tickets.split(","))
        tickets = {"WEAPON": w, "SKILL": s, "COMPANION": c}
    return Bot(p, seed, policy=a.policy, tickets=tickets).run(a.days, a.active_min, a.sessions)


def parse_seeds(text):
    out = []
    for part in text.split(","):
        if "-" in part:
            lo, hi = part.split("-")
            out += list(range(int(lo), int(hi) + 1))
        elif part:
            out.append(int(part))
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    contentlib.add_content_arg(ap)
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--days", type=int, default=30)
    ap.add_argument("--active-min", type=int, default=120)
    ap.add_argument("--sessions", type=int, choices=(1, 2), default=2)
    ap.add_argument("--override", action="append", default=[], metavar="NAME=VALUE")
    ap.add_argument("--tickets", metavar="W,S,C", help="summon tickets per day per banner (default: content / 06)")
    ap.add_argument("--rock-candy-per-day", type=float, help="flat Rock Candy income instead of the dungeon (06 6.3)")
    ap.add_argument("--fish-coin-per-day", type=float, help="flat Fish Coin income instead of the dungeon (06 6.3)")
    ap.add_argument("--policy", default="greedy", choices=("greedy", "lazy", "spender20", "whale"))
    ap.add_argument("--diamonds", default="content", choices=("content", "flat"),
                    help="content: 06's login, quests, first clears and achievements (default); flat: DIAMONDS_PER_DAY_F2P a day")
    ap.add_argument("--m0", action="store_true", help="M0 tier: no Companions, Research, dungeons or summon tickets")
    ap.add_argument("--check", action="store_true", help="20-seed sweep + every S-constant check; exit 1 on FAIL")
    ap.add_argument("--seeds", default="1-20")
    ap.add_argument("--out", default=os.path.join(HERE, "out"))
    ap.add_argument("--quiet", action="store_true")
    a = ap.parse_args(argv)
    if a.policy in ("spender20", "whale"):
        sys.exit(f"--policy {a.policy} needs the shop_products / shop_packages content (10 section 3.4), "
                 "which this build does not ship (TODO(#175))")

    overrides = {}
    for o in a.override:
        overrides.update(fish_model.parse_kv(o, "--override"))
    if a.rock_candy_per_day is not None:
        overrides["ROCK_CANDY_PER_DAY"] = str(a.rock_candy_per_day)
    if a.fish_coin_per_day is not None:
        overrides["FISH_COIN_PER_DAY"] = str(a.fish_coin_per_day)
    p = Params(a.content, overrides, a.m0, diamonds=a.diamonds)

    print(f"power_curve_model: content {p.db.version} ({p.db.content_hash[:12]}), tier {p.tier}, policy {a.policy}, "
          f"{a.days} days, {a.active_min} active min in {a.sessions} sessions")
    for fb in p.fallbacks:
        print(f"note: fallback {fb}")

    bot = run_one(p, a.seed, a)
    rows = [r for r in bot.daily if r["day"] in REPORT_DAYS]
    checks = checks_for(p, bot)
    if not a.quiet:
        print_day_table(p, rows)
        print_shares(p, [r for r in bot.daily if r["day"] in SHARE_DAYS], checks)
        print_walls(bot)
        print_economy(bot)
        print_cadence(p, bot)
        print_economy_tables(p, {d: day_flow(bot, d) for d in set(SECTION2_DAYS) | set(SECTION11_DAYS) if d <= a.days},
                             f"seed {a.seed}")

    targets = [(t["day"], t["stage"], t["tolerance"]) for t in p.targets if t["day"] <= a.days]
    seed_stages = [(d, bot.daily[d - 1]["stage"]) for d, _, _ in targets]
    sweep = sweep_rows = econ_median = None
    if a.check:
        seeds = parse_seeds(a.seeds)
        runs = {a.seed: bot}
        for s in seeds:
            if s not in runs:
                runs[s] = run_one(p, s, a)
        sweep = {d: statistics.median(runs[s].daily[d - 1]["stage"] for s in seeds) for d, _, _ in targets}
        per_seed_fail = {}
        for s in seeds:
            for st, name, val in checks_for(p, runs[s]):
                if st == "FAIL":
                    per_seed_fail.setdefault(name, []).append(s)
        print(f"\n{len(seeds)}-seed sweep: median stage by day (target +/- tolerance from progress_targets)")
        for d, target, tol in targets:
            m = sweep[d]
            ok = abs(m - target) <= tol * target
            gate = d in STAGE_CHECK_DAYS
            st = ("PASS" if ok else "FAIL") if gate else ("PASS" if ok else "INFO")
            checks.append((st, f"day {d}: median stage within +/-{tol:.0%} of {target} (03 AC16{'' if gate else ', not gated'})",
                           f"{m:g} (range {min(runs[s].daily[d - 1]['stage'] for s in seeds)}.."
                           f"{max(runs[s].daily[d - 1]['stage'] for s in seeds)})"))
        sweep_rows = median_rows(runs, seeds)
        print_sweep(p, a, seeds, runs, sweep_rows)
        econ_median = median_flows(runs, seeds, sorted(set(SECTION2_DAYS) | set(SECTION11_DAYS)))
        print_economy_tables(p, econ_median, f"{len(seeds)}-seed medians")
        sd = [steady_diamonds(runs[s]) for s in seeds]
        if all(sd):
            print(f"  steady-state diamonds (days {DIAMOND_STEADY_DAYS[0]}-{DIAMOND_STEADY_DAYS[1]}): median "
                  f"{statistics.median(x[0] for x in sd):.0f} a day, max balance median {statistics.median(x[1] for x in sd):.0f}")
        for name, ss in sorted(per_seed_fail.items()):
            print(f"  failing on {len(ss)}/{len(seeds)} seeds: {name}")
    else:
        for d, target, tol in targets:
            got = bot.daily[d - 1]["stage"]
            ok = abs(got - target) <= tol * target
            checks.append(("PASS" if ok else "INFO", f"day {d}: seed {a.seed} stage within +/-{tol:.0%} of {target} "
                           "(gate is the 20-seed median, --check)", str(got)))

    print("\nS-constant checks (03 section 8.6" + (f", seed {a.seed}" if not a.check else f", seed {a.seed} + sweep") + ")")
    for st, name, val in checks:
        print(f"  {st:<4}  {name}: {val}")
    stages = [(d, round(sweep[d]) if sweep else s) for d, s in seed_stages]
    print(daytable_line(stages))

    os.makedirs(a.out, exist_ok=True)
    path = os.path.join(a.out, f"power_curve_{a.seed}.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"seed": a.seed, "tier": p.tier, "content_version": p.db.version, "overrides": p.overrides,
                   "fallbacks": p.fallbacks, "days": bot.daily, "walls": bot.walls,
                   "first_wall_stage": bot.first_wall_stage, "economy": bot.econ,
                   "banner_level_day": bot.banner_level_day, "awaken_gate_day": bot.gate_day,
                   "awaken_day": bot.awaken_day, "checks": [{"status": s, "name": n, "value": v} for s, n, v in checks],
                   "daytable": dict(stages), "sweep_median": sweep,
                   "sweep_shares": sweep_rows, "diamond_mode": p.diamond_mode,
                   "economy_by_day": [day_flow(bot, d) for d in range(1, len(bot.daily) + 1)],
                   "economy_sweep_median": econ_median}, f, indent=1, default=str)
    if not a.quiet:
        print(f"wrote {os.path.relpath(path)}")
    failed = [c for c in checks if c[0] == "FAIL"]
    return 1 if (a.check and failed) else 0


if __name__ == "__main__":
    sys.exit(main())
