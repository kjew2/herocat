#!/usr/bin/env python3
"""Greedy-bot power-curve model. Constants here are the source of truth for specs/03-power-curve-math.md.

Model (per-minute aggregate, 30 days):
  * Player is ACTIVE for ACTIVE_MIN minutes/day (pushes stages, spends gold/gems).
  * Rest of the day is OFFLINE: gold accrues at OFFLINE_RATE up to OFFLINE_CAP_H hours at the current stage.
  * DPS = BASE_ATK * statMult(n) * gearMult * heroMult * classMult
  * Normal stage: 10 kills to advance; always clears eventually.
  * Boss stage (every 10th): BOSS_HP_MULT x HP, must die within BOSS_TIME_S or the player stays farming.
"""
import math
import random

random.seed(7)

# ---------- enemy / economy curves ----------
HP0, HP_G = 20.0, 1.07            # HP(s) = HP0 * HP_G^s
GOLD0, GOLD_G = 2.0, 1.05        # gold per kill at stage s
KILLS_PER_STAGE = 10
BOSS_EVERY, BOSS_HP_MULT, BOSS_TIME_S = 10, 10.0, 20.0
SPAWN_S = 0.5                     # fixed overhead per kill
MIN_KILL_S = 1.0

# ---------- gold sink: ATK stat levels ----------
BASE_ATK = 10.0
STAT_PER_LVL = 0.05               # statMult = 1 + 0.05 n
STAT_COST0, STAT_COST_G = 50.0, 1.07

# ---------- gear ----------
GEAR_SLOTS = 6
GEAR_DROP_P = 0.005                # per kill
GEAR_SLOT_G = 1.0074              # piece pct = (GEAR_SLOT_G^tier - 1) * rarityMult ; 1.0074^6 ~= 1.045 per stage
GEAR_RARITY = [("C", 0.60, 1.0), ("R", 0.28, 1.2), ("E", 0.09, 1.5), ("L", 0.025, 2.0), ("M", 0.005, 3.0)]
ENH_MAX, ENH_PER_LVL = 15, 0.05   # enhancement: +5%/lvl of the piece's pct, up to +75%

# ---------- heroes (gacha) ----------
HERO_SLOTS = 6
HERO_BASE_PCT = {"C": 10, "R": 20, "E": 40, "L": 80, "M": 160}
HERO_POOL = {"C": 20, "R": 15, "E": 10, "L": 6, "M": 3}
HERO_LVL_CUM = [1, 3, 6, 10, 16, 25, 40, 64, 100, 160]   # copies needed for level 1..10
HERO_LVL_BONUS = 0.20                                     # +20% of base per level past 1
GEMS_PER_DAY, FREE_PULLS_PER_DAY, PULL_COST = 300, 3, 100
PITY_SOFT, PITY_STEP, PITY_HARD = 50, 0.0325, 80

# ---------- class ----------
CLASS_STAGES, CLASS_MULT = [50, 150, 300, 500], 1.5

# ---------- session ----------
ACTIVE_MIN, OFFLINE_RATE, OFFLINE_CAP_H = 120, 0.5, 8
DAYS = 30


def hp(s): return HP0 * HP_G ** s
def gold(s): return GOLD0 * GOLD_G ** s
def stat_cost(n): return STAT_COST0 * STAT_COST_G ** n


class Bot:
    def __init__(self):
        self.stage = 1; self.gold = 0.0; self.gems = 0.0
        self.n = 0; self.kills_in_stage = 0
        self.gear = [1.0] * GEAR_SLOTS           # (1+pct) per slot
        self.enh = [0] * GEAR_SLOTS
        self.heroes = {}                          # id -> copies
        self.pity = 0
        self.pulls = 0; self.lplus = 0
        self.classes = 0
        self.walled_min = 0

    # ----- multipliers -----
    def class_mult(self): return CLASS_MULT ** self.classes
    def gear_mult(self):
        m = 1.0
        for g, e in zip(self.gear, self.enh):
            m *= 1 + (g - 1) * (1 + ENH_PER_LVL * e)
        return m
    def hero_mult(self):
        vals = []
        for sid, copies in self.heroes.items():
            tier = sid[0]
            lvl = sum(1 for c in HERO_LVL_CUM if copies >= c)
            vals.append(HERO_BASE_PCT[tier] * (1 + HERO_LVL_BONUS * (lvl - 1)))
        return 1 + sum(sorted(vals, reverse=True)[:HERO_SLOTS]) / 100
    def dps(self):
        return BASE_ATK * (1 + STAT_PER_LVL * self.n) * self.gear_mult() * self.hero_mult() * self.class_mult()

    # ----- gacha -----
    def pull(self):
        self.pulls += 1; self.pity += 1
        p_l = 1.0 if self.pity >= PITY_HARD else min(1.0, 0.03 + PITY_STEP * max(0, self.pity - PITY_SOFT))
        r = random.random()
        if r < p_l:
            tier = "M" if random.random() < 1 / 6 else "L"; self.pity = 0; self.lplus += 1
        else:
            r2 = random.random() * 0.97
            tier = "C" if r2 < 0.60 else "R" if r2 < 0.88 else "E"
        sid = f"{tier}{random.randrange(HERO_POOL[tier])}"
        self.heroes[sid] = self.heroes.get(sid, 0) + 1

    # ----- gear drop -----
    def drop(self):
        r = random.random(); acc = 0
        for name, p, mult in GEAR_RARITY:
            acc += p
            if r < acc: break
        slot = random.randrange(GEAR_SLOTS)
        tier = (self.stage // 10) * 10
        val = 1 + (GEAR_SLOT_G ** tier - 1) * mult
        if val > self.gear[slot]:
            self.gear[slot] = val; self.enh[slot] = 0  # fresh piece, enhancement does not carry (prototype rule)

    # ----- spend -----
    def spend(self):
        # greedy: buy stat levels while affordable; enhancement = 10% of gold per minute on weakest slot (approx)
        while self.gold >= stat_cost(self.n):
            self.gold -= stat_cost(self.n); self.n += 1
        # enhancement cost scales with stage gold; crude: 20 kills worth of gold per attempt
        slot = min(range(GEAR_SLOTS), key=lambda i: self.enh[i])
        cost = 20 * gold(self.stage)
        if self.enh[slot] < ENH_MAX and self.gold >= cost:
            self.gold -= cost
            if random.random() < max(0.3, 1 - 0.05 * self.enh[slot]):
                self.enh[slot] += 1
        while self.gems >= PULL_COST:
            self.gems -= PULL_COST; self.pull()

    # ----- one active minute -----
    def active_minute(self):
        d = self.dps(); t = 0.0
        while t < 60:
            boss_next = (self.stage % BOSS_EVERY == 0) and self.kills_in_stage == KILLS_PER_STAGE - 1
            if boss_next:
                kill_t = BOSS_HP_MULT * hp(self.stage) / d
                if kill_t > BOSS_TIME_S:        # walled: farm normal mobs on this stage instead
                    kill_t = max(MIN_KILL_S, hp(self.stage) / d) + SPAWN_S
                    t += kill_t; self.gold += gold(self.stage); self.walled_min += 1 / (60 / kill_t)
                    if random.random() < GEAR_DROP_P: self.drop()
                    continue
            kill_t = max(MIN_KILL_S, hp(self.stage) / d) + SPAWN_S
            t += kill_t
            self.gold += gold(self.stage) * (3 if boss_next else 1)
            if random.random() < GEAR_DROP_P: self.drop()
            self.kills_in_stage += 1
            if self.kills_in_stage >= KILLS_PER_STAGE:
                self.kills_in_stage = 0; self.stage += 1
                while self.classes < len(CLASS_STAGES) and self.stage >= CLASS_STAGES[self.classes]:
                    self.classes += 1
        self.spend()

    def offline(self):
        d = self.dps()
        kill_t = max(MIN_KILL_S, hp(self.stage) / d) + SPAWN_S
        secs = min(24 * 60 - ACTIVE_MIN, OFFLINE_CAP_H * 60) * 60
        self.gold += (secs / kill_t) * gold(self.stage) * OFFLINE_RATE


def run(verbose=True):
    b = Bot()
    rows = []
    for day in range(1, DAYS + 1):
        b.gems += GEMS_PER_DAY
        for _ in range(FREE_PULLS_PER_DAY): b.pull()
        for _ in range(ACTIVE_MIN): b.active_minute()
        b.offline()
        rows.append((day, b.stage, b.dps(), b.n, b.gear_mult(), b.hero_mult(), b.class_mult(), b.pulls, b.lplus))
        if verbose and (day in (1, 2, 3, 5, 7, 10, 14, 21, 30)):
            d, s, dps, n, gm, sm, cm, pulls, lp = rows[-1]
            print(f"day {d:>2}: stage {s:>4}  dps {dps:>12.3g}  statLvl {n:>4} (x{1+STAT_PER_LVL*n:>6.1f})  "
                  f"gear x{gm:>9.3g}  heroes x{sm:>5.2f}  class x{cm:>5.2f}  pulls {pulls:>3} L+ {lp}")
    return rows


if __name__ == "__main__":
    rows = run()
    s30 = rows[-1][1]
    print(f"\nHP at stage {s30}: {hp(s30):.3g}; boss HP {BOSS_HP_MULT*hp(s30):.3g}; needed DPS for 30s boss: {BOSS_HP_MULT*hp(s30)/BOSS_TIME_S:.3g}")
    print(f"gold/kill at stage {s30}: {gold(s30):.3g}")
