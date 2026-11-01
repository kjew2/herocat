#!/usr/bin/env python3
"""Fish Board throughput model (specs/05 section 9). Stdlib only; reads content/*.json.

Per-minute model of the Magic Tank and the board for one policy:
  * tank stock and refill in sim ticks (05 section 3), offline fill between sessions capped by TankCap;
  * the board as a count per level (placement does not change throughput; 05 section 9);
  * the policy's release / merge / discard rules (05 sections 6.1 and 8.2);
  * coins and missiles per merge from fish_levels (05 section 2.2), FISH_BOARD_DPS_PCT per 05 section 5.2.

Prints a per-day table, the attentive / lazy / never comparison at days 1, 3, 7, 14, 30, the 05 section 8.1 and 8.3
tables regenerated from content, the S-constant checks (PASS/FAIL) and a FISHLINE summary line for 03's report,
and writes tools/out/fish_<policy>.json. Exits 1 when any band in 05 section 9 fails.

  python3 tools/fish_model.py [--policy attentive] [--days 30] [--research "RES_TANK_REFILL=5"] [--override NAME=VALUE]
"""
import argparse
import json
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

# Research effects (05 section 3, 09 research rows). M0 content has no research table; these are 09's documented row
# values, used only when content/research.json is absent (TODO(#167): read the rows once the table ships).
RESEARCH_FALLBACK = {
    "res_tank_cap": {"effect_per_level": 5.0, "effect_floor": None, "max_level": 10},
    "res_tank_refill": {"effect_per_level": 0.4, "effect_floor": 2.0, "max_level": 10},
    "res_fish_cap": {"effect_per_level": 1.0, "effect_floor": None, "max_level": 10},
}
AUTO_MERGE_INTERVAL_S_FALLBACK = 3   # 05 section 6.2 (P1); not a constants row in M0 content

POLICIES = ("attentive", "lazy", "never", "auto")


# ------------------------------------------------------------------------------------------------ content

def _rows(content_dir, table):
    path = os.path.join(content_dir, table + ".json")
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)["rows"]


class Content:
    def __init__(self, content_dir, overrides):
        consts = {r["key"]: r["value"] for r in _rows(content_dir, "constants")}
        self.levels = sorted(_rows(content_dir, "fish_levels"), key=lambda r: r["level"])
        tank = _rows(content_dir, "tank")[0]
        research = _rows(content_dir, "research")
        self.research = {r["id"]: r for r in research} if research else dict(RESEARCH_FALLBACK)
        self.research_from_content = research is not None

        self.c = dict(consts)
        self.c.update({
            "TANK_CAP0": tank["cap0"], "TANK_REFILL_S0": tank["refill_s0"], "TANK_REFILL_FLOOR_S": tank["refill_floor_s"],
            "TANK_RELEASE_LEVEL": tank["release_level"],
            "RES_TANK_CAP_PER_LVL": self.research["res_tank_cap"]["effect_per_level"],
            "RES_TANK_REFILL_PER_LVL": self.research["res_tank_refill"]["effect_per_level"],
            "RES_FISH_CAP_PER_LVL": self.research["res_fish_cap"]["effect_per_level"],
        })
        self.c.setdefault("AUTO_MERGE_INTERVAL_S", AUTO_MERGE_INTERVAL_S_FALLBACK)
        for k, v in overrides.items():
            if k not in self.c:
                sys.exit(f"--override: unknown name {k}")
            self.c[k] = type(self.c[k])(v) if not isinstance(self.c[k], bool) else v.lower() == "true"

        k = self.c
        self.tick_hz = k["TICK_HZ"]
        self.cells = k["FISH_BOARD_W"] * k["FISH_BOARD_H"]
        self.hard = len(self.levels)
        self.discovery = [0.0] + [float(r["discovery_coins"]) for r in self.levels]   # index = level
        self.missile = [0.0] + [float(r["missile_pct"]) for r in self.levels]
        self.board_dps = [0.0] + [k["FISH_BOARD_DPS_FACTOR"] * m for m in self.missile[1:]]
        self.merge_coins = [k["FISH_MERGE_COINS_PCT"] * d for d in self.discovery]
        self.attack_s = k["ATTACK_INTERVAL_TICKS"] / self.tick_hz
        self.offline_cap_s = k["OFFLINE_CAP_H"] * 3600

    def tank_cap(self, res):
        return int(self.c["TANK_CAP0"] + self.c["RES_TANK_CAP_PER_LVL"] * res.get("RES_TANK_CAP", 0))

    def refill_ticks(self, res):
        s = max(self.c["TANK_REFILL_FLOOR_S"], self.c["TANK_REFILL_S0"] - self.c["RES_TANK_REFILL_PER_LVL"] * res.get("RES_TANK_REFILL", 0))
        return int(round(s * self.tick_hz))

    def fish_cap(self, res):
        return min(self.hard, int(self.c["FISH_MAX_LVL0"] + self.c["RES_FISH_CAP_PER_LVL"] * res.get("RES_FISH_CAP", 0)))

    def missile_hits_per_fish(self, cap):
        """05 section 5.1: sum over n = 2..cap of FISH_MISSILE_PCT[n] / 100 * 2^-(n-1)."""
        return sum(self.missile[n] / 100.0 / 2 ** (n - 1) for n in range(2, cap + 1))

    def missile_dps_pct(self, fish_per_min, cap, speed=1.0):
        return 100.0 * fish_per_min * self.missile_hits_per_fish(cap) / (60.0 / self.attack_s * speed)


# ------------------------------------------------------------------------------------------------ board model

class Board:
    """The board as a count per level, plus the tank and the day's counters."""

    def __init__(self, db):
        self.db = db
        self.count = [0] * (db.hard + 1)
        self.discovered = set()
        self.tank = 0
        self.refill = 0
        self.day = None

    def occupancy(self):
        return sum(self.count)

    def highest(self):
        return max(self.discovered) if self.discovered else 0

    def dps_pct(self):
        # Sum in level order; the sim sums over cells in index order, which only reorders equal-sized float additions.
        return sum(self.db.board_dps[n] * self.count[n] for n in range(1, self.db.hard + 1))

    def _discover(self, n):
        if n in self.discovered:
            return False
        self.discovered.add(n)
        return True

    def tick_tank(self, ticks, cap, refill):
        """05 section 3 TickTank repeated `ticks` times (closed form, same as CatchUpTank). Returns refill ticks wasted."""
        if self.tank >= cap:
            self.refill = 0
            return ticks
        total = self.refill + ticks
        gained = total // refill
        room = cap - self.tank
        if gained >= room:
            self.tank = cap
            used = room * refill - self.refill
            self.refill = 0
            return ticks - used
        self.tank += gained
        self.refill = total % refill
        return 0

    def release_max(self, level):
        n = min(self.tank, self.db.cells - self.occupancy())
        if n <= 0:
            return 0
        self.tank -= n
        self.count[level] += n
        self.day["released"] += n
        if self._discover(level):
            self.day["coins_discovery"] += self.db.discovery[level]
        return n

    def mergeable(self, cap):
        return any(self.count[l] >= 2 for l in range(1, cap))

    def merge_all(self, cap):
        """05 section 6.1 on counts: lowest level first, repeated passes until none merges."""
        merges = 0
        found = True
        while found:
            found = False
            for l in range(1, cap):
                p = self.count[l] // 2
                if p == 0:
                    continue
                found = True
                self.count[l] -= 2 * p
                self.count[l + 1] += p
                merges += p
                n = l + 1
                self.day["missile_hits"] += p * self.db.missile[n] / 100.0
                if self._discover(n):
                    self.day["coins_discovery"] += self.db.discovery[n]
                    p -= 1
                self.day["coins_merge"] += p * self.db.merge_coins[n]
        self.day["merges"] += merges
        return merges

    def max_cap_fish(self, cap):
        """The most cap fish a board can ever hold: making one needs `cap` cells at its peak, so c + cap <= cells + 1."""
        return min(self.db.cells, self.db.cells - cap + 1) if cap > 1 else self.db.cells

    def is_finished(self, cap):
        return self.count[cap] >= self.max_cap_fish(cap)

    def ceiling_counts(self, cap):
        """The reachable ceiling (05 section 4.3, issue #247): cells - cap + 1 cap fish plus one fish of each level
        1..cap-1, which fills the board exactly. Index = level."""
        out = [0] * (self.db.hard + 1)
        out[cap] = self.max_cap_fish(cap)
        if out[cap] < self.db.cells:
            for n in range(1, cap):
                out[n] = 1
        return out

    def ceiling_fish(self, cap):
        """Level-1 fish it takes to build the reachable ceiling."""
        return sum(c * 2 ** (n - 1) for n, c in enumerate(self.ceiling_counts(cap)) if n > 0)

    def ceiling_dps_pct(self, cap):
        return sum(self.db.board_dps[n] * c for n, c in enumerate(self.ceiling_counts(cap)))

    def at_ceiling(self, cap):
        return self.count == self.ceiling_counts(cap)

    def discard_lowest_below(self, cap):
        for l in range(1, cap):
            if self.count[l] > 0:
                self.count[l] -= 1
                self.day["discards"] += 1
                self.day["coins_discard"] += self.db.c["FISH_DISCARD_COINS_PCT"] * self.db.discovery[l]
                return True
        return False


def new_day():
    return {k: 0 for k in ("released", "merges", "discards", "coins_discovery", "coins_merge", "coins_discard",
                           "missile_hits", "jammed_min", "wasted_ticks")}


def run(db, policy, days, active_min, sessions, research_for_day, dungeon_coins):
    """Simulates `days` days. Each day: `sessions` equal sessions spread over 24 h; offline fills the tank between them."""
    b = Board(db)
    b.tank = db.tank_cap(research_for_day(1))   # a new profile starts with a full tank (00 decision 99, issue #246)
    level = max(1, min(db.hard, int(db.c["TANK_RELEASE_LEVEL"])))
    out = []
    session_min = [active_min // sessions + (1 if i < active_min % sessions else 0) for i in range(sessions)]
    gap_s = max(0, (24 * 60 - active_min) * 60 // sessions)
    credited_gap_ticks = min(gap_s, db.offline_cap_s) * db.tick_hz
    minute_ticks = 60 * db.tick_hz
    ceiling_day = finished_day = None
    for d in range(1, days + 1):
        res = research_for_day(d)
        cap, refill, fcap = db.tank_cap(res), db.refill_ticks(res), db.fish_cap(res)
        b.day = new_day()
        # The attentive and auto players act whenever the tank is full: every minute at Research 0, more often when
        # RES_TANK_REFILL outpaces RES_TANK_CAP (05 section 8.2).
        step = min(minute_ticks, cap * refill)
        for s in range(sessions):
            left = session_min[s] * minute_ticks
            while left > 0:
                t = min(step, left)
                left -= t
                b.day["wasted_ticks"] += b.tick_tank(t, cap, refill)
                if policy in ("attentive", "auto"):
                    act(b, level, fcap, discard=(policy == "attentive"))
                    if b.occupancy() == db.cells and not b.mergeable(fcap) and b.tank > 0:
                        b.day["jammed_min"] += t / minute_ticks
            if policy == "lazy":
                act(b, level, fcap, discard=False)
            b.tick_tank(credited_gap_ticks, cap, refill)   # offline catch-up between sessions (05 section 7)
        dps = b.dps_pct()
        missile_pct = 100.0 * b.day["missile_hits"] / (active_min * 60.0 / db.attack_s) if active_min else 0.0
        ceiling = b.at_ceiling(fcap)
        if ceiling and ceiling_day is None:
            ceiling_day = d
        if b.is_finished(fcap) and finished_day is None:
            finished_day = d
        row = dict(b.day)
        row.update({
            "day": d, "fish_board_dps_pct": dps, "missile_dps_pct": missile_pct,
            "fish_mult": 1 + (dps + missile_pct) / 100.0, "fish_mult_live": 1 + dps / 100.0,
            "highest_level": b.highest(), "occupancy": b.occupancy(), "board_at_ceiling": ceiling,
            "cap_fish": b.count[fcap], "board_finished": b.is_finished(fcap),
            "coins_dungeon": dungeon_coins, "fish_cap": fcap, "tank_cap": cap, "refill_s": refill / db.tick_hz,
        })
        row["coins_board"] = row["coins_discovery"] + row["coins_merge"] + row["coins_discard"]
        out.append(row)
    return out, (ceiling_day, finished_day)


def act(b, level, fcap, discard):
    """One policy visit: Release max and Merge all until neither changes anything; the attentive bot discards to unjam."""
    while True:
        moved = b.release_max(level) + b.merge_all(fcap)
        if b.tank > 0 and b.occupancy() == b.db.cells and not b.mergeable(fcap):
            # Jammed (05 section 11). Building a cap fish needs `fcap` free cells at its peak (fcap-1, ..., 2, 1, 1), so
            # once more than cells - fcap cap fish sit on the board no further cap fish can be made and discarding a
            # lower fish only throws fish away: the board is finished at this cap (see is_finished).
            if discard and not b.is_finished(fcap) and b.discard_lowest_below(fcap):
                continue
            return
        if moved == 0:
            return


# ------------------------------------------------------------------------------------------------ CLI

def parse_kv(text, what):
    out = {}
    for part in (text or "").split(","):
        part = part.strip()
        if not part:
            continue
        if "=" not in part:
            sys.exit(f"{what}: expected NAME=VALUE, got {part!r}")
        k, v = part.split("=", 1)
        out[k.strip()] = v.strip()
    return out


def research_schedule(args):
    fixed = {k: int(v) for k, v in parse_kv(args.research, "--research").items()}
    for k in fixed:
        if k not in ("RES_TANK_CAP", "RES_TANK_REFILL", "RES_FISH_CAP"):
            sys.exit(f"--research: unknown node {k}")
    if not args.research_schedule:
        return lambda d: fixed
    with open(args.research_schedule, encoding="utf-8") as f:
        data = json.load(f)
    days = data.get("days", data) if isinstance(data, dict) else data
    by_day = {}
    for row in days:
        r = row.get("research", {})
        by_day[int(row["day"])] = {k.upper(): int(v) for k, v in r.items()}

    def at(d):
        known = [k for k in by_day if k <= d]
        r = dict(by_day[max(known)]) if known else {}
        r.update(fixed)
        return r
    return at


def fmt(x, nd=1):
    return f"{x:,.{nd}f}"


def print_days(rows):
    print("day  released  merges  discards  board_dps%  fishMult  coins(disc/merge/discard)        highest  occ  jammed_min")
    for r in rows:
        print(f"{r['day']:>3}  {r['released']:>8}  {r['merges']:>6}  {r['discards']:>8}  {r['fish_board_dps_pct']:>10.1f}  "
              f"x{r['fish_mult']:>7.2f}  {fmt(r['coins_discovery'], 0):>9} / {fmt(r['coins_merge'], 1):>8} / {fmt(r['coins_discard'], 0):>6}"
              f"  {r['highest_level']:>7}  {r['occupancy']:>3}  {r['jammed_min']:>10.1f}")


def regenerated_tables(db, active_min):
    print("\n05 section 8.1: fish per minute by RES_TANK_REFILL level")
    cap0 = db.fish_cap({})
    max_caps = Board(db).max_cap_fish(cap0)
    print(f"level  TankRefillS  fish/min  fish/2h  days to the cap-{cap0} ceiling ({max_caps} cap fish + one each of 1-{cap0 - 1})")
    full_fish = Board(db).ceiling_fish(cap0)
    for lvl in (0, 2, 5, 8, 10):
        ticks = db.refill_ticks({"RES_TANK_REFILL": lvl})
        per_min = 60.0 * db.tick_hz / ticks
        per_day = per_min * active_min
        print(f"{lvl:>5}  {ticks / db.tick_hz:>11.1f}  {per_min:>8.1f}  {per_day:>7,.0f}  {full_fish / per_day:>12.1f}")
    print("\n05 section 8.3: Fish Coins per level-1 fish fully merged, and per active minute")
    for cap in (10, 15, 20):
        cpf = sum(db.merge_coins[n] / 2 ** (n - 1) for n in range(2, min(cap, db.hard) + 1))
        print(f"cap {cap:>2}: {cpf:.2f} coins/fish, missile hits/fish {db.missile_hits_per_fish(min(cap, db.hard)):.3f}")
    for refill, fcap in ((0, 0), (10, 5)):
        res = {"RES_TANK_REFILL": refill, "RES_FISH_CAP": fcap}
        per_min = 60.0 * db.tick_hz / db.refill_ticks(res)
        cap = db.fish_cap(res)
        cpf = sum(db.merge_coins[n] / 2 ** (n - 1) for n in range(2, cap + 1))
        print(f"RES_TANK_REFILL {refill:>2}, cap {cap}: {per_min * cpf:.0f} coins/min, {per_min * cpf * active_min:,.0f}/day, "
              f"missileDpsPct {db.missile_dps_pct(per_min, cap):.1f}%")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--content", default=os.path.join(REPO, "content"))
    p.add_argument("--days", type=int, default=30)
    p.add_argument("--active-min", type=int, default=120)
    p.add_argument("--sessions", type=int, default=2)
    p.add_argument("--policy", choices=POLICIES, default="attentive")
    p.add_argument("--research", default="")
    p.add_argument("--research-schedule", default=None)
    p.add_argument("--dungeon-coins", type=float, default=1000.0)
    p.add_argument("--seed", type=int, default=7)
    p.add_argument("--override", action="append", default=[])
    p.add_argument("--out", default=os.path.join(HERE, "out"))
    p.add_argument("--quiet", action="store_true", help="checks and FISHLINE only")
    a = p.parse_args(argv)
    if a.days < 1 or a.sessions < 1 or a.active_min < 0:
        sys.exit("--days and --sessions must be >= 1, --active-min >= 0")

    overrides = {}
    for o in a.override:
        overrides.update(parse_kv(o, "--override"))
    db = Content(a.content, overrides)
    sched = research_schedule(a)

    def go(policy, schedule=sched, days=a.days):
        return run(db, policy, days, a.active_min, a.sessions, schedule, a.dungeon_coins)

    runs = {pol: go(pol) for pol in POLICIES}
    rows, (ceiling_day, finished_day) = runs[a.policy]

    if not a.quiet:
        if not db.research_from_content:
            print("note: content has no research table; using 09's documented research effects (TODO(#167))")
        print(f"policy {a.policy}, {a.days} days, {a.active_min} active min in {a.sessions} sessions")
        print_days(rows)
        print("\npolicy comparison (fishMult / fish released so far / board coins so far)")
        print("day   " + "".join(f"{pol:>32}" for pol in ("attentive", "lazy", "never")))
        for d in (1, 3, 7, 14, 30):
            if d > a.days:
                continue
            cells = []
            for pol in ("attentive", "lazy", "never"):
                rr = runs[pol][0]
                fish = sum(r["released"] for r in rr[:d])
                coins = sum(r["coins_board"] for r in rr[:d])
                cells.append(f"x{rr[d - 1]['fish_mult']:.2f} / {fish:,} / {coins:,.0f}")
            print(f"{d:>3}   " + "".join(f"{c:>32}" for c in cells))
        regenerated_tables(db, a.active_min)

    # ---- S-constant checks (05 section 9, AC13). Measured on Research 0 runs regardless of --research.
    r0 = lambda d: {}
    att, (att_ceiling, att_finished) = run(db, "attentive", max(a.days, 30), a.active_min, a.sessions, r0, a.dungeon_coins)
    lazy, _ = run(db, "lazy", max(a.days, 30), a.active_min, a.sessions, r0, a.dungeon_coins)
    auto, _ = run(db, "auto", max(a.days, 30), a.active_min, a.sessions, r0, a.dungeon_coins)
    fast, _ = run(db, "attentive", 1, a.active_min, a.sessions, lambda d: {"RES_TANK_REFILL": 10}, a.dungeon_coins)
    att7, lazy7, auto7 = att[6]["fish_mult"], lazy[6]["fish_mult"], auto[6]["fish_mult"]
    ratio = att7 / lazy7
    day1_coins = att[0]["coins_board"]
    steady = att[2]["coins_merge"]   # day 3: merging at cap 10 every minute, before the board finishes
    missile_max = max(db.missile_dps_pct(60.0 * db.tick_hz / db.refill_ticks({"RES_TANK_REFILL": t}),
                                         db.fish_cap({"RES_FISH_CAP": c}))
                      for t in range(0, db.research["res_tank_refill"]["max_level"] + 1)
                      for c in range(0, db.research["res_fish_cap"]["max_level"] + 1))
    merge_total = sum(r["coins_merge"] for r in att)
    discard_total = sum(r["coins_discard"] for r in att)
    triple = fast[0]["released"] / att[0]["released"]

    rng = random.Random(a.seed)   # jam test: random full boards below the cap always hold a pair (05 section 11)
    cap0 = db.fish_cap({})
    jam_free = all(any(v >= 2 for v in _level_counts([rng.randint(1, cap0 - 1) for _ in range(db.cells)]).values())
                   for _ in range(10_000)) if cap0 > 1 else True

    probe = Board(db)
    max_caps = probe.max_cap_fish(cap0)
    max_dps = probe.ceiling_dps_pct(cap0)
    ceiling_name = (f"{max_caps} cap fish + one each of levels 1-{cap0 - 1} at cap {cap0}" if max_caps < db.cells
                    else f"{db.cells} cap fish at cap {cap0}")
    checks = [
        ("TANK_CAP0, TANK_REFILL_S0: attentive fish on day 1 in 1,000..1,500", 1000 <= att[0]["released"] <= 1500, f"{att[0]['released']:,}"),
        (f"FISH_MAX_LVL0: attentive board reaches the reachable ceiling ({ceiling_name}) between day 6 and day 12",
         att_ceiling is not None and 6 <= att_ceiling <= 12, f"day {att_ceiling}"),
        ("FISH_DISCOVERY_COINS, FISH_MERGE_COINS_PCT: day-1 Fish Coins in 8,000..14,000", 8000 <= day1_coins <= 14000, f"{day1_coins:,.0f}"),
        ("FISH_MERGE_COINS_PCT: steady merge coins 1,500..3,000 a day at cap 10", 1500 <= steady <= 3000, f"{steady:,.0f}"),
        ("FISH_MISSILE_PCT, FISH_BOARD_DPS_FACTOR: attentive fishMult day 7 in x12..x25", 12 <= att7 <= 25, f"x{att7:.2f}"),
        ("lazy fishMult day 7 in x1.5..x3", 1.5 <= lazy7 <= 3, f"x{lazy7:.2f}"),
        ("attentive / lazy ratio at day 7 in x3..x12", 3 <= ratio <= 12, f"x{ratio:.2f}"),
        ("missileDpsPct under 10% at every research level", missile_max < 10, f"max {missile_max:.2f}%"),
        ("FISH_DISCARD_COINS_PCT: discard coins under 10% of merge coins (attentive)", discard_total < 0.10 * merge_total,
         f"{discard_total:,.0f} vs {merge_total:,.0f}"),
        ("RES_TANK_*: RES_TANK_REFILL 10 at most triples fish per day", triple <= 3.0 + 1e-9, f"x{triple:.2f}"),
        ("AUTO_MERGE_INTERVAL_S: auto day-7 fishMult within 10% of attentive", abs(auto7 / att7 - 1) <= 0.10, f"x{auto7:.2f}"),
        ("jam test: 10,000 random full boards below the cap all hold a pair", jam_free, f"seed {a.seed}"),
    ]
    print(f"\nnote: at cap {cap0} a {db.cells}-cell board holds at most {max_caps} cap fish (making one needs {cap0} free cells), "
          f"so its largest FISH_BOARD_DPS_PCT is {max_dps:,.1f}% (x{1 + max_dps / 100:.2f}) from {probe.ceiling_fish(cap0):,} level-1 fish; "
          f"attentive holds {max_caps} cap fish on day {att_finished} and fills the ceiling on day {att_ceiling}")
    print("\nS-constant checks (Research 0)")
    for name, ok, val in checks:
        print(f"  {'PASS' if ok else 'FAIL'}  {name}: {val}")
    print(f"FISHLINE day7 attentive x{att7:.2f} lazy x{lazy7:.2f} ratio {ratio:.1f}")

    os.makedirs(a.out, exist_ok=True)
    path = os.path.join(a.out, f"fish_{a.policy}.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"policy": a.policy, "days": rows, "ceiling_day": ceiling_day, "finished_board_day": finished_day,
                   "checks": [{"name": n, "pass": ok, "value": v} for n, ok, v in checks],
                   "fishline": {"attentive_day7": att7, "lazy_day7": lazy7, "ratio": ratio}}, f, indent=1)
    if not a.quiet:
        print(f"wrote {os.path.relpath(path, REPO)}")
    return 0 if all(ok for _, ok, _ in checks) else 1


def _level_counts(cells):
    out = {}
    for c in cells:
        out[c] = out.get(c, 0) + 1
    return out


if __name__ == "__main__":
    sys.exit(main())
