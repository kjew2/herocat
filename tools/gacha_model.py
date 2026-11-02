#!/usr/bin/env python3
"""Summon model for HeroCat (specs/04 section 14): exact pity math, banner-level rate tables, the test vectors and a
Monte Carlo cross-check on a SplitMix64 that matches src/HeroCat.Sim/Rng/SplitMix64.cs bit for bit.

Every rate, pity constant, pool and dupe threshold is read from the built content (content/*.json, specs/09); only the
banner-level shift constants live here, because they exist only to regenerate the banner_levels rows (04 section 4).

  gacha_model.py                         pity block, soft ramp, ten-row rate table with P(E+) and guarantee numbers,
                                         collection fill curve
  gacha_model.py --cadence [--seeds 1-20] [--days 30] [--quiet]
                                         the above plus the cadence block (pulls/day per banner from
                                         power_curve_model.py's F2P bot, banner level days, first L+ / M, item levels on
                                         days 7/30/90) and the 04 section 14 S-constant checks; exit 1 on a hard FAIL
  gacha_model.py --banner SKILL --level 4
  gacha_model.py --levels                the ten banner_levels rows regenerated from the shift constants
  gacha_model.py --vectors [--out PATH]  Fixtures/Summon/vectors.json (stdout unless --out); decision 46, issue #14
  gacha_model.py --mc 1000000 --seed 7 [--level 10]
  gacha_model.py --check                 self-checks (04 section 3.1 numbers, rows vs content, SplitMix64 vs the C#
                                         stream fixture) and the vectors drift check; run by check.sh
  --override NAME=VALUE                  sweep a constant (PITY_SOFT_START, PITY_SOFT_STEP, PITY_HARD,
                                         M_SHARE_OF_LPLUS, BANNER_EXP_PER_PULL, BANNER_LVL_C_SHIFT, ...)

The pull logic mirrors HeroCat.Sim.Summons.PullAlgorithm operation for operation (same float arithmetic in the same
order), so a scripted-u vector computed here is the C# result exactly. Not an estimator: no pow/exp in the pull path.
"""
import argparse
import bisect
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RARITIES = ["COMMON", "RARE", "EPIC", "LEGENDARY", "MYTHIC"]
CODE = {"COMMON": "C", "RARE": "R", "EPIC": "E", "LEGENDARY": "L", "MYTHIC": "M"}
TIERS = ["M0", "MVP", "P1", "P2"]
DRAWS_PER_PULL = 3
VECTORS_FORMAT = 1
VECTORS_PATH = os.path.join(ROOT, "Fixtures", "Summon", "vectors.json")
STREAM_FIXTURE_PATH = os.path.join(ROOT, "Fixtures", "Summon", "stream.json")

# 04 section 4 / section 17: S constants that generate the banner_levels rows. Not in content (09 section 5.14).
SHIFTS = {"BANNER_LVL_C_SHIFT": 0.020, "BANNER_LVL_R_SHIFT": 0.012, "BANNER_LVL_E_SHIFT": 0.008}

# ------------------------------------------------------------------------------------------------ SplitMix64

MASK = (1 << 64) - 1
GAMMA = 0x9E3779B97F4A7C15


def mix(z):
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
    return z ^ (z >> 31)


def derive(seed, k):
    """Output k (0-based) of SplitMix64 seeded with seed (SplitMix64.Derive)."""
    return mix((seed + (k + 1) * GAMMA) & MASK)


def to_unit(x):
    return (x >> 11) * (1.0 / (1 << 53))


class Stream:
    """StreamUniformSource: (seed, counter)."""

    def __init__(self, seed, counter=0):
        self.seed = seed
        self.counter = counter

    def next(self):
        u = to_unit(derive(self.seed, self.counter))
        self.counter += 1
        return u


class Scripted:
    def __init__(self, us):
        self.us = list(us)
        self.i = 0

    def next(self):
        u = self.us[self.i]
        self.i += 1
        return u


# ------------------------------------------------------------------------------------------------ content


def load_table(content_dir, name):
    with open(os.path.join(content_dir, name + ".json"), encoding="utf-8") as f:
        return json.load(f)["rows"]


class Banner:
    """BannerDef as ContentDb.Build makes it, for one build tier."""

    def __init__(self, content_dir, banner_id, tier="M0", overrides=None):
        overrides = overrides or {}
        row = next(r for r in load_table(content_dir, "banners") if r["id"] == banner_id)
        self.id = banner_id
        self.pull_cost = row["pull_cost_diamonds"]
        self.ticket_currency = row["ticket_currency"]
        self.pity_soft_start = int(float(overrides.get("PITY_SOFT_START", row["pity_soft_start"])))
        self.pity_soft_step = float(overrides.get("PITY_SOFT_STEP", row["pity_soft_step"]))
        self.pity_hard = int(float(overrides.get("PITY_HARD", row["pity_hard"])))
        self.m_share = float(overrides.get("M_SHARE_OF_LPLUS", row["m_share_of_lplus"]))
        self.guarantee_batch = row["guarantee_batch_size"]
        self.guarantee_min = RARITIES.index(row["guarantee_min_rarity"])
        self.exp_per_pull = int(float(overrides.get("BANNER_EXP_PER_PULL", row["exp_per_pull"])))
        self.pool_size = row["pool_size"]
        self.levels = sorted((r for r in load_table(content_dir, "banner_levels") if r["banner"] == banner_id),
                             key=lambda r: r["level"])
        if not self.levels:
            raise SystemExit(f"banner {banner_id} has no banner_levels rows")
        self.base_lplus = self.levels[0]["rates"]["LEGENDARY"] + self.levels[0]["rates"]["MYTHIC"]
        items = {"WEAPON": "weapons", "SKILL": "skills", "COMPANION": "companions"}.get(banner_id)
        self.pool = {r: [] for r in RARITIES}
        if items:
            for it in load_table(content_dir, items):
                if TIERS.index(it["tier"]) <= TIERS.index(tier):
                    self.pool[it["rarity"]].append(it["id"])
        for r in RARITIES:
            self.pool[r].sort()   # ordinal; ids are ASCII (09 section 3.4)
        dupes = sorted(load_table(content_dir, "dupe_levels"), key=lambda r: r["level"])
        self.dupe_cum = [r["cum_copies"] for r in dupes]
        self.overflow = {r["id"]: r["dupe_overflow_rock_candy"] for r in load_table(content_dir, "rarities")}
        # Precomputed with the same float expressions the C# evaluates per pull, so the values are bit-identical.
        self._thresholds = [lv["exp_required"] for lv in self.levels]
        self._bounds = []
        for lv in self.levels:
            r = lv["rates"]
            c, rr, e = r["COMMON"], r["RARE"], r["EPIC"]
            total = c + rr + e
            self._bounds.append((c / total, (c + rr) / total))
        self._pity = [self._pity_rate(n) for n in range(0, self.pity_hard + 1)]

    def pity_rate(self, n):
        return self._pity[n] if 0 <= n <= self.pity_hard else self._pity_rate(n)

    # BannerDef.PityRate
    def _pity_rate(self, n):
        if n >= self.pity_hard:
            return 1.0
        over = n - self.pity_soft_start
        r = self.base_lplus + self.pity_soft_step * (over if over > 0 else 0)
        return r if r < 1.0 else 1.0

    # BannerDef.LevelFor: count of thresholds <= exp, at least 1 (thresholds strictly increase, 09 E031)
    def level_for(self, exp):
        n = bisect.bisect_right(self._thresholds, exp)
        return n if n >= 1 else 1

    def rates(self, level):
        i = max(0, min(len(self.levels) - 1, level - 1))
        return self.levels[i]["rates"]

    def threshold(self, level):
        return self.levels[level - 1]["exp_required"]

    def dupe_level(self, copies):
        return bisect.bisect_right(self.dupe_cum, copies)

    def cre_bounds(self, level):
        return self._bounds[max(0, min(len(self._bounds) - 1, level - 1))]


# ------------------------------------------------------------------------------------------------ the pull (04 3.2)


def sample_cre(bounds, u):
    """PullAlgorithm.SampleCre: boundaries C / T and (C + R) / T with T = C + R + E, strict (Banner.cre_bounds)."""
    if u < bounds[0]:
        return 0
    if u < bounds[1]:
        return 1
    return 2


class State:
    def __init__(self, pity=0, exp=0, total=0, copies=None):
        self.pity = pity
        self.exp = exp
        self.total = total
        self.copies = dict(copies or {})


def pull(b, st, rng, force_eplus):
    n = st.pity + 1
    st.total += 1
    st.exp += b.exp_per_pull
    u1, u2, u3 = rng.next(), rng.next(), rng.next()
    level = b.level_for(st.exp)
    lplus = u1 < b.pity_rate(n)
    st.pity = 0 if lplus else n
    if lplus:
        rarity = 4 if u2 < b.m_share else 3
    elif force_eplus:
        rarity = b.guarantee_min
    else:
        rarity = sample_cre(b.cre_bounds(level), u2)
    pool = b.pool[RARITIES[rarity]]
    index = int(u3 * len(pool))
    if index >= len(pool):
        index = len(pool) - 1
    item = pool[index]
    copies = st.copies.get(item, 0)
    before = b.dupe_level(copies)
    is_new = copies == 0
    overflow = 0
    if copies >= b.dupe_cum[-1]:
        overflow = b.overflow[RARITIES[rarity]]
    else:
        copies += 1
        st.copies[item] = copies
    after = b.dupe_level(copies)
    return {
        "itemId": item, "rarity": RARITIES[rarity], "poolIndex": index, "isNew": is_new,
        "guaranteeApplied": force_eplus and not lplus, "copiesAfter": copies, "levelBefore": before, "levelAfter": after,
        "overflowRockCandy": overflow, "pityAfter": st.pity, "bannerLevelAfter": level,
    }


def pull10(b, st, rng):
    out = []
    any_eplus = False
    for _ in range(b.guarantee_batch - 1):
        r = pull(b, st, rng, False)
        out.append(r)
        if RARITIES.index(r["rarity"]) >= b.guarantee_min:
            any_eplus = True
    out.append(pull(b, st, rng, not any_eplus))
    return out


def hold_level(b, st, level):
    """MC helper (C# does the same): the next pull's +EXP lands exactly on the level's threshold."""
    st.exp = b.threshold(level) - b.exp_per_pull


# ------------------------------------------------------------------------------------------------ closed form


def pity_distribution(b):
    surv = 1.0
    out = []
    for n in range(1, b.pity_hard + 1):
        p = b.pity_rate(n)
        out.append((n, surv * p))
        surv *= 1 - p
    assert abs(sum(q for _, q in out) - 1) < 1e-12
    return out


def pity_numbers(b):
    dist = pity_distribution(b)
    ev = sum(n * q for n, q in dist)
    cum, marks = 0.0, {}
    for n, q in dist:
        cum += q
        for m in (0.5, 0.9, 0.99):
            if m not in marks and cum >= m:
                marks[m] = n
    return {
        "expected": ev, "lplus": 1 / ev, "mythic": b.m_share / ev,
        "p50": marks[0.5], "p90": marks[0.9], "p99": marks[0.99],
        "no_lplus_50": sum(q for n, q in dist if n > b.pity_soft_start), "hard": dist[-1][1],
    }


def regenerated_rows(b, shifts):
    """04 section 4: per level above 1, C -= c_shift, R += r_shift, E += e_shift; L and M fixed."""
    base = b.levels[0]["rates"]
    rows = []
    for lv in b.levels:
        k = lv["level"] - 1
        r = {"COMMON": base["COMMON"] - shifts["BANNER_LVL_C_SHIFT"] * k, "RARE": base["RARE"] + shifts["BANNER_LVL_R_SHIFT"] * k,
             "EPIC": base["EPIC"] + shifts["BANNER_LVL_E_SHIFT"] * k, "LEGENDARY": base["LEGENDARY"], "MYTHIC": base["MYTHIC"]}
        assert abs(sum(r.values()) - 1.0) < 1e-9, f"level {lv['level']} sums to {sum(r.values())}"
        rows.append((lv["level"], lv["exp_required"], r))
    return rows


def eplus(b, rates):
    return sum(rates[r] for r in RARITIES[b.guarantee_min:])


def print_report(b, level):
    p = pity_numbers(b)
    print(f"banner {b.id}: pull cost {b.pull_cost} DIAMOND or 1 {b.ticket_currency}; pool "
          + " / ".join(f"{CODE[r]} {len(b.pool[r]) or b.pool_size[r]}" for r in RARITIES))
    print()
    print("pity (identical for every banner level: levels never touch L or M)")
    print(f"  base L+ rate                : {b.base_lplus:.4f}  (1 in {1 / b.base_lplus:.1f})")
    print(f"  expected pulls per L+       : {p['expected']:.2f}")
    print(f"  consolidated L+ rate        : {p['lplus']:.3%}")
    print(f"  consolidated M rate         : {p['mythic']:.3%}   (one Mythic per {1 / p['mythic']:.0f} pulls)")
    print(f"  P50 / P90 / P99 pulls to L+ : {p['p50']} / {p['p90']} / {p['p99']}")
    print(f"  P(no L+ in first {b.pity_soft_start})      : {p['no_lplus_50']:.3f}")
    print(f"  P(reach hard pity at {b.pity_hard})    : {p['hard']:.2e}")
    print()
    print("soft pity ramp (n = pity + 1 -> L+ rate)")
    for n in (1, b.pity_soft_start, 51, 55, 60, 65, 70, 75, 79, 80):
        print(f"  {n:>3}: {b.pity_rate(n):.4f}")
    print()
    print("rate table per banner level (banner_levels) with the 10-pull guarantee")
    print("  lvl  exp    C      R      E      L      M      P(E+)  P(guarantee)  E+ per 10")
    for lv in b.levels:
        r = lv["rates"]
        pe = eplus(b, r)
        fire = (1 - pe) ** (b.guarantee_batch - 1)
        per10 = b.guarantee_batch * pe + fire * (1 - pe)
        mark = " <" if lv["level"] == level else ""
        print(f"  {lv['level']:>3}  {lv['exp_required']:>5}  " + "  ".join(f"{r[x]:.3f}" for x in RARITIES)
              + f"  {pe:.3f}  {fire:.3f}         {per10:.2f}{mark}")


def print_levels(b, shifts):
    print("banner,level,exp_required,rate_c,rate_r,rate_e,rate_l,rate_m")
    for level, exp, r in regenerated_rows(b, shifts):
        print(f"{b.id},{level},{exp}," + ",".join(repr(round(r[x], 12)) for x in RARITIES))


# ------------------------------------------------------------------------------------------------ collection fill (04 14.4)
# Closed form over independent pulls: the rarity of a pull is drawn at the consolidated L+ / M rates (pity averaged out)
# and the level's renormalised C/R/E split, the item uniformly inside its rarity's pool. The 10-pull guarantee is left
# out (it only adds Epics, so the E numbers are slightly pessimistic for players who 10-pull).

FILL_PULLS = (10, 30, 60, 100, 200, 300, 500)


def pool_sizes(b):
    return {r: len(b.pool[r]) or b.pool_size[r] for r in RARITIES}


def rarity_rates(b, level, rates=None):
    """Per-pull probability of each rarity at a banner level, pity averaged (consolidated L+ rate)."""
    rates = rates or b.rates(level)
    lplus = pity_numbers(b)["lplus"]
    cre = rates["COMMON"] + rates["RARE"] + rates["EPIC"]
    out = {r: (1 - lplus) * rates[r] / cre for r in ("COMMON", "RARE", "EPIC")}
    out["LEGENDARY"] = lplus * (1 - b.m_share)
    out["MYTHIC"] = lplus * b.m_share
    return out


def item_rate(b, rarity, level):
    """Per-pull probability of one specific item of `rarity` at a banner level (memoised on the banner)."""
    cache = b.__dict__.setdefault("_item_rate", {})
    key = (rarity, level)
    if key not in cache:
        cache[key] = rarity_rates(b, level)[rarity] / pool_sizes(b)[rarity]
    return cache[key]


def pulls_to_complete(n, p):
    """Expected pulls to own all n items of a rarity drawn at per-pull probability p (coupon collector)."""
    return n / p * sum(1.0 / k for k in range(1, n + 1)) if n and p > 0 else float("inf")


def fill_after(b, level, pulls):
    """(expected distinct items owned, expected new items on the next pull) after `pulls` pulls held at `level`."""
    rr, sizes = rarity_rates(b, level), pool_sizes(b)
    owned = new = 0.0
    for r in RARITIES:
        if not sizes[r]:
            continue
        miss = (1 - rr[r] / sizes[r]) ** pulls
        owned += sizes[r] * (1 - miss)
        new += rr[r] * miss
    return owned, new


def print_fill(b):
    sizes = pool_sizes(b)
    top = len(b.levels)
    print()
    print("collection fill curve (independent pulls at the consolidated rates; the 10-pull guarantee only adds Epics)")
    print("  expected pulls to own every item of a rarity, banner held at level 1 / level " + str(top))
    for r in ("COMMON", "RARE", "EPIC"):
        lo = pulls_to_complete(sizes[r], rarity_rates(b, 1)[r])
        hi = pulls_to_complete(sizes[r], rarity_rates(b, top)[r])
        print(f"    all {sizes[r]:>2} {CODE[r]}: {lo:7.0f} / {hi:7.0f}")
    print(f"  pulls   owned (of {sum(sizes.values())}) L1 / L{top}   new items per pull L1 / L{top}")
    for n in FILL_PULLS:
        o1, n1 = fill_after(b, 1, n)
        o10, n10 = fill_after(b, top, n)
        print(f"  {n:>5}   {o1:6.1f} / {o10:6.1f}            {n1:.3f} / {n10:.3f}")


# ------------------------------------------------------------------------------------------------ cadence (04 14.5)
# The F2P pull cadence per banner is never hand-entered (issue #38): it comes from power_curve_model.py's bot, which
# earns 06's diamonds and tickets and spends them with 03's policy, pulling with this file's algorithm. The cadence
# block runs that bot over the seeds (MVP tier) and reads pulls and banner levels per day from it.

CADENCE_ITEM_DAYS = (7, 30, 90)
CADENCE_TAIL_DAYS = 15        # days past the run are extrapolated at the median pulls/day of its last 15 days
WEAPON_L4_BAND = (20, 35)     # 00 decision 70, 04 section 4: 20-seed median day the F2P bot reaches weapon level 4
EPLUS_L10_MAX = 0.20          # 04 section 14: level-10 P(E+), the paid E+ edge stays under 1.7x
COMMON_L5_LEVEL, COMMON_L5_DAYS = 5, 30        # 04 section 14 / open question 8 (report-only, #249 item 11)
LEGEND_L3_LEVEL, LEGEND_L3_DAYS = 3, 60        # 04 section 14
PASSIVE_SHARE_BAND, PASSIVE_SHARE_DAYS = (0.10, 0.40), (7, 30)   # 04 sections 6.2, 14
COMPANION_SHARE_MIN, COMPANION_SHARE_DAY = 0.05, 7               # 04 section 14 with 03 section 8.5


def run_bots(content_dir, overrides, seeds, days):
    """power_curve_model's greedy F2P bot (MVP tier, 120 active min in 2 sessions a day) for each seed."""
    import power_curve_model as pc   # imports this module; deferred so gacha_model stays importable on its own
    p = pc.Params(content_dir, overrides, False)
    return p, {s: pc.Bot(p, s).run(days, 120, 2) for s in seeds}


def median(xs):
    xs = sorted(xs)
    n = len(xs)
    return xs[n // 2] if n % 2 else (xs[n // 2 - 1] + xs[n // 2]) / 2


class Cadence:
    """Per-banner median cumulative pulls by day over the bot runs, extrapolated past the last day."""

    def __init__(self, p, runs, days):
        self.days = days
        self.seeds = sorted(runs)
        self.banners = list(p.banner_ids)
        self.cum = {b: [0.0] + [median(runs[s].daily[d - 1]["pulls"][b] for s in runs) for d in range(1, days + 1)]
                    for b in self.banners}
        tail = min(CADENCE_TAIL_DAYS, days)
        self.tail_rate = {b: (self.cum[b][days] - self.cum[b][days - tail]) / tail for b in self.banners}
        self.per_day = {b: median((runs[s].daily[days - 1]["pulls"][b]) / days for s in runs) for b in self.banners}
        self.level_day = {b: {lv: median(runs[s].banner_level_day[b].get(lv, days + 1) for s in runs)
                              for lv in range(2, len(p.banner[b].levels) + 1)} for b in self.banners}
        self.level_missed = {b: {lv: sum(1 for s in runs if lv not in runs[s].banner_level_day[b])
                                 for lv in range(2, len(p.banner[b].levels) + 1)} for b in self.banners}

        def med_day(d, f):
            return median(f(runs[s].daily[d - 1]) for s in runs) if d <= days else None
        self.passive_share = {d: med_day(d, lambda r: r["weapon_pct"]["passive"]
                                         / max(1e-9, r["weapon_pct"]["passive"] + r["weapon_pct"]["equipped"]))
                              for d in PASSIVE_SHARE_DAYS}
        self.companion_share = (med_day(COMPANION_SHARE_DAY, lambda r: r["shares"]["Companions"])
                                if "COMPANION" in self.banners else None)

    def pulls_by(self, b, day):
        if day <= self.days:
            return self.cum[b][max(0, day)]
        return self.cum[b][self.days] + self.tail_rate[b] * (day - self.days)

    def day_for(self, b, pulls, horizon=3650):
        """First day whose median cumulative pulls reach `pulls` (extrapolated past the run), or None."""
        for d in range(1, horizon + 1):
            if self.pulls_by(b, d) >= pulls:
                return d
        return None


def expected_copies(b, rarity, pulls):
    """Expected copies of one specific item after `pulls` pulls on a banner from EXP 0, each at its banner level."""
    n = int(pulls)
    total = sum(item_rate(b, rarity, b.level_for(k * b.exp_per_pull)) for k in range(1, n + 1))
    return total + (pulls - n) * item_rate(b, rarity, b.level_for((n + 1) * b.exp_per_pull))


def item_level_day(b, cad, rarity, level, horizon=3650):
    """First day the expected copies of a specific item reach `level`'s DUPE_LVL_CUM threshold, or None."""
    need = b.dupe_cum[level - 1]
    lo = cad.day_for(b.id, 0) or 1
    copies, pulls_done = 0.0, 0
    for d in range(lo, horizon + 1):
        target = int(cad.pulls_by(b.id, d))
        while pulls_done < target:
            pulls_done += 1
            copies += item_rate(b, rarity, b.level_for(pulls_done * b.exp_per_pull))
        if copies >= need:
            return d
    return None


def cadence_checks(p, cad, shifts):
    """[(status, name, value, hard)] for the S constants 04 section 14 validates. status PASS/FAIL; hard=False marks
    the report-only checks whose target is undecided or whose fix is the pending balance call (#249)."""
    out = []

    def add(ok, name, val, hard=True):
        out.append(("PASS" if ok else "FAIL", name, val, hard))

    w = p.banner["WEAPON"]
    l4 = cad.level_day["WEAPON"].get(4)
    add(l4 is not None and WEAPON_L4_BAND[0] <= l4 <= WEAPON_L4_BAND[1],
        f"BANNER_LVL_EXP / BANNER_EXP_PER_PULL: median day the F2P bot reaches weapon banner level 4 in "
        f"{WEAPON_L4_BAND[0]}..{WEAPON_L4_BAND[1]}",
        f"day {l4:g} ({cad.level_missed['WEAPON'][4]}/{len(cad.seeds)} seeds not by day {cad.days}, counted as day "
        f"{cad.days + 1})")

    rows = regenerated_rows(w, shifts)
    top = rows[-1][2]
    pe = eplus(w, top)
    add(pe <= EPLUS_L10_MAX + 1e-12, f"BANNER_LVL_*_SHIFT: level-{rows[-1][0]} P(E+) <= {EPLUS_L10_MAX}",
        f"{pe:.3f} (x{pe / eplus(w, rows[0][2]):.2f} the level-1 rate)")

    # TODO(#249): item 11 (04 open question 8) decides whether this target stays; report-only until then.
    cd = item_level_day(w, cad, "COMMON", COMMON_L5_LEVEL)
    add(cd is not None and cd <= COMMON_L5_DAYS,
        f"weapon pool size: a specific Common reaches level {COMMON_L5_LEVEL} within {COMMON_L5_DAYS} days "
        "(04 open question 8, report-only until #249 item 11)",
        f"expected level {w.dupe_level(expected_copies(w, 'COMMON', cad.pulls_by('WEAPON', COMMON_L5_DAYS)))} on day "
        f"{COMMON_L5_DAYS}; level {COMMON_L5_LEVEL} on day {cd if cd is not None else '> 3650'}", hard=False)

    ld = item_level_day(w, cad, "LEGENDARY", LEGEND_L3_LEVEL)
    add(ld is None or ld > LEGEND_L3_DAYS,
        f"weapon pool size: a specific Legendary does not reach level {LEGEND_L3_LEVEL} within {LEGEND_L3_DAYS} days",
        f"expected level {w.dupe_level(expected_copies(w, 'LEGENDARY', cad.pulls_by('WEAPON', LEGEND_L3_DAYS)))} on day "
        f"{LEGEND_L3_DAYS}; level {LEGEND_L3_LEVEL} on day {ld if ld is not None else '> 3650'}")

    for d in PASSIVE_SHARE_DAYS:
        sh = cad.passive_share[d]
        if sh is None:
            continue
        add(PASSIVE_SHARE_BAND[0] <= sh <= PASSIVE_SHARE_BAND[1],
            f"WEAPON_PASSIVE_PCT: passive {PASSIVE_SHARE_BAND[0]:.0%}..{PASSIVE_SHARE_BAND[1]:.0%} of the weapon layer "
            f"on day {d} (median)", f"{sh:.1%}")

    if cad.companion_share is not None:
        # TODO(#249): item 1 (layer floors). Misses under the current content, as power_curve_model.py's share check
        # does; retuning companions is the pending balance call, so this stays report-only like that check.
        add(cad.companion_share >= COMPANION_SHARE_MIN,
            f"COMPANION_SLOTS / companion dps_pct: companion layer >= {COMPANION_SHARE_MIN:.0%} of ln(DPS) by day "
            f"{COMPANION_SHARE_DAY} (median, report-only until #249 item 1)", f"{cad.companion_share:.1%}", hard=False)
    return out


def print_cadence(p, cad):
    print()
    print(f"cadence (F2P bot of power_curve_model.py, {len(cad.seeds)} seeds x {cad.days} days, MVP tier; medians; days "
          f"past {cad.days} extrapolated at the last {min(CADENCE_TAIL_DAYS, cad.days)} days' pulls/day)")
    for b in cad.banners:
        bn = p.banner[b]
        pn = pity_numbers(bn)
        reached = [f"L{lv} d{d:g}" for lv, d in cad.level_day[b].items() if d <= cad.days]
        print(f"  {b}: {cad.per_day[b]:.2f} pulls/day ({cad.pulls_by(b, cad.days):g} by day {cad.days}, then "
              f"{cad.tail_rate[b]:.2f}/day); banner level on day: {', '.join(reached) or 'none past L1'}")

        def when(pulls):
            d = cad.day_for(b, pulls)
            return f"day {d}" if d is not None else "never"
        print(f"    first L+ expected {when(pn['expected'])}, P90 {when(pn['p90'])}; first M expected "
              f"{when(1 / pn['mythic'])}")
        parts = []
        for r in ("COMMON", "RARE", "EPIC", "LEGENDARY"):
            if not pool_sizes(bn)[r]:
                continue
            lv = [bn.dupe_level(expected_copies(bn, r, cad.pulls_by(b, d))) for d in CADENCE_ITEM_DAYS]
            parts.append(f"{CODE[r]} " + "/".join(str(x) for x in lv))
        print(f"    expected level of a specific item on days {'/'.join(str(d) for d in CADENCE_ITEM_DAYS)}: "
              + ", ".join(parts))


def run_cadence(content_dir, overrides, shifts, seeds, days, quiet):
    p, runs = run_bots(content_dir, overrides, seeds, days)
    cad = Cadence(p, runs, days)
    if not quiet:
        print_cadence(p, cad)
    checks = cadence_checks(p, cad, shifts)
    print()
    print("S-constant checks (04 section 14)")
    for st, name, val, hard in checks:
        tag = st if hard or st == "PASS" else "WARN"
        print(f"  {tag:<4}  {name}: {val}")
    hard_fail = [c for c in checks if c[0] == "FAIL" and c[3]]
    soft_fail = [c for c in checks if c[0] == "FAIL" and not c[3]]
    if soft_fail:
        print(f"  REPORT-ONLY: {len(soft_fail)} check(s) miss (balance decision #249 items 1 and 11 pending)")
    return checks, (1 if hard_fail else 0)


# ------------------------------------------------------------------------------------------------ Monte Carlo


def monte_carlo(b, n, seed, level):
    """n single pulls at a held banner level on stream seed; then n // 10 ten-pulls, each from pity 0 at that level's
    threshold, on a fresh stream of the same seed."""
    st = State()
    rng = Stream(seed)
    counts = {r: 0 for r in RARITIES}
    gaps = {}
    gap = 0
    for _ in range(n):
        hold_level(b, st, level)
        r = pull(b, st, rng, False)
        counts[r["rarity"]] += 1
        gap += 1
        if r["rarity"] in ("LEGENDARY", "MYTHIC"):
            gaps[gap] = gaps.get(gap, 0) + 1
            gap = 0
    rng10 = Stream(seed)
    fired = 0
    batches = n // b.guarantee_batch
    for _ in range(batches):
        st10 = State(pity=0, exp=b.threshold(level))   # pity 0 per batch: 04 criterion 4's base-rate fire rate (1 - P(E+))^9
        res = pull10(b, st10, rng10)
        # "fires" = slot 10 was forced (no E+ in slots 1-9); GuaranteeApplied is false when that slot's L+ check hit
        if not any(RARITIES.index(r["rarity"]) >= b.guarantee_min for r in res[:-1]):
            fired += 1
    return {"counts": counts, "gaps": gaps, "batches": batches, "guaranteeFired": fired, "counter": rng.counter}


def split(counts):
    total = sum(counts.values())
    non_lplus = counts["COMMON"] + counts["RARE"] + counts["EPIC"]
    return {
        "lplus": (counts["LEGENDARY"] + counts["MYTHIC"]) / total, "mythic": counts["MYTHIC"] / total,
        "C": counts["COMMON"] / non_lplus, "R": counts["RARE"] / non_lplus, "E": counts["EPIC"] / non_lplus,
    }


def print_mc(b, n, seed, level):
    res = monte_carlo(b, n, seed, level)
    s = split(res["counts"])
    print(f"Monte Carlo: {n} pulls, banner {b.id}, level {level}, seed {seed}")
    for r in RARITIES:
        print(f"  {CODE[r]}: {res['counts'][r]:>8}  {res['counts'][r] / n:.4%}")
    print(f"  L+ {s['lplus']:.4%} (closed form {pity_numbers(b)['lplus']:.4%}), M {s['mythic']:.4%}")
    print(f"  non-L+ split C/R/E {s['C']:.4f} / {s['R']:.4f} / {s['E']:.4f}")
    print(f"  guarantee fired in {res['guaranteeFired']} of {res['batches']} ten-pulls ({res['guaranteeFired'] / max(1, res['batches']):.2%})")
    print("  L+ gap histogram (pulls since last L+, bucketed by 10):")
    buckets = {}
    for g, c in res["gaps"].items():
        buckets[(g - 1) // 10] = buckets.get((g - 1) // 10, 0) + c
    for k in sorted(buckets):
        print(f"    {k * 10 + 1:>3}-{k * 10 + 10:<3} {buckets[k]}")


# ------------------------------------------------------------------------------------------------ vectors (04 3.4)


def fresh(b, pity=0, level=1, copies=None):
    st = State(pity=pity, copies=copies)
    st.exp = b.threshold(level) - b.exp_per_pull if level > 1 else 0
    return st


def scripted(name, b, st, us, mode="pull", force=False, note=""):
    setup = {"pity": st.pity, "exp": st.exp, "totalPulls": st.total, "copies": dict(sorted(st.copies.items()))}
    rng = Scripted(us)
    if mode == "pull10":
        res = pull10(b, st, rng)
    else:
        res = [pull(b, st, rng, force)]
    assert rng.i == len(us), f"{name}: {len(us)} u's for {rng.i} draws"
    return {"name": name, "note": note, "banner": b.id, "mode": mode, "forceEplus": force, "setup": setup, "u": us,
            "expect": res, "expectState": {"pity": st.pity, "exp": st.exp, "totalPulls": st.total}}


def build_vectors(content_dir, mc_pulls):
    w = Banner(content_dir, "WEAPON")
    s = Banner(content_dir, "SKILL")
    rates1 = w.rates(1)
    total = rates1["COMMON"] + rates1["RARE"] + rates1["EPIC"]
    cr = rates1["COMMON"] / total
    re_ = (rates1["COMMON"] + rates1["RARE"]) / total
    c0 = w.pool["COMMON"][0]
    v = []

    # 1: PityRate (n = pity + 1)
    ns = [1, 50, 51, 55, 60, 65, 70, 75, 79, 80, 81]
    pity_rates = [{"n": n, "rate": w.pity_rate(n)} for n in ns]
    # 2
    v.append(scripted("v02_common_first_item", w, fresh(w, pity=1), [0.5, 0.0, 0.0], note="C, first item of C pool, Pity 2"))
    # 3, 4: strict C/R and R/E boundaries
    v.append(scripted("v03_cr_below", w, fresh(w), [0.5, cr - 1e-9, 0.0], note="C"))
    v.append(scripted("v03_cr_at", w, fresh(w), [0.5, cr, 0.0], note="R (boundary is strict)"))
    v.append(scripted("v04_re_below", w, fresh(w), [0.5, re_ - 1e-9, 0.0], note="R"))
    v.append(scripted("v04_re_at", w, fresh(w), [0.5, re_, 0.0], note="E"))
    # 5: soft pity at n = 79
    r79 = w.pity_rate(79)
    v.append(scripted("v05_pity79_below", w, fresh(w, pity=78), [r79 - 1e-9, 0.5, 0.0], note="L+"))
    v.append(scripted("v05_pity79_at", w, fresh(w, pity=78), [r79, 0.0, 0.0], note="not L+"))
    # 6, 7: hard pity, M share
    v.append(scripted("v06_hard_mythic", w, fresh(w, pity=79), [0.999999, w.m_share - 1e-9, 0.0], note="M, Pity 0"))
    v.append(scripted("v07_hard_legendary", w, fresh(w, pity=79), [0.999999, w.m_share, 0.0], note="L, Pity 0"))
    # 8, 9, 10: the 10-pull guarantee
    v.append(scripted("v08_guarantee_fires", w, fresh(w), [0.99, 0.0, 0.0] * 10, "pull10",
                      note="slots 1-9 C, slot 10 E with GuaranteeApplied"))
    us = [0.99, 0.0, 0.0] * 10
    us[2 * 3 + 1] = 0.95
    v.append(scripted("v09_guarantee_not_needed", w, fresh(w), us, "pull10", note="slot 3 E, slot 10 C"))
    us = [0.99, 0.0, 0.0] * 9 + [0.0, 0.0, 0.0]
    v.append(scripted("v10_guarantee_lplus_first", w, fresh(w), us, "pull10", note="slot 10 is L+ (L+ check before forced E)"))
    # 11: draw count
    st = State()
    rng = Stream(7)
    counts = {r: 0 for r in RARITIES}
    for _ in range(1000):
        counts[pull(w, st, rng, False)["rarity"]] += 1
    draw_count = {"seed": 7, "pulls": 1000, "counterAfter": rng.counter, "totalPulls": st.total, "exp": st.exp,
                  "pityAfter": st.pity, "rarityCounts": counts}
    # 12: copies -> levels, overflow at 161
    copies = sorted(set([1, 2, 3, 6, 10, 160, 161] + w.dupe_cum))
    levels = [{"copies": c, "level": w.dupe_level(c)} for c in copies]
    v.append(scripted("v12_overflow_copy_161", w, fresh(w, copies={c0: w.dupe_cum[-1]}), [0.5, 0.0, 0.0],
                      note="copy 161: OverflowRockCandy == DUPE_OVERFLOW_ROCK_CANDY[C], CopiesAfter 160"))
    v.append(scripted("v12_copy_160", w, fresh(w, copies={c0: w.dupe_cum[-1] - 1}), [0.5, 0.0, 0.0], note="copy 160: level 10"))
    # 16: banner level thresholds
    exps = [0, w.threshold(2) - 1, w.threshold(2), w.threshold(len(w.levels)) - 1, w.threshold(len(w.levels)),
            w.threshold(len(w.levels)) + 7000]
    banner_levels = [{"exp": e, "level": w.level_for(e)} for e in exps]
    # the 50th pull already rolls at level 2 (04 section 3.2: level after this pull's EXP)
    st = State(exp=w.threshold(2) - w.exp_per_pull)
    v.append(scripted("v16_level_after_exp", w, st, [0.5, 0.0, 0.0], note="the pull that reaches the threshold rolls at level 2"))

    # 08: row vectors (level, pity, u1, u2, u3, forceEplus) -> (rarity, poolIndex, pityAfter, guarantee)
    rows = []
    gen = Stream(0x5EED0008)
    for i in range(512):
        b = w if i % 2 == 0 else s
        level = 1 + int(gen.next() * len(b.levels))
        pick = gen.next()
        if pick < 0.5:
            pity = int(gen.next() * 50)
        elif pick < 0.9:
            pity = 50 + int(gen.next() * (b.pity_hard - 50))
        else:
            pity = b.pity_hard - 1
        u1, u2, u3 = gen.next(), gen.next(), gen.next()
        if i % 7 == 0:   # land near the L+ boundary
            u1 = b.pity_rate(pity + 1) + (1e-12 if i % 14 == 0 else -1e-12)
        force = gen.next() < 0.25
        st = fresh(b, pity=pity, level=level)
        r = pull(b, st, Scripted([u1, u2, u3]), force)
        rows.append({"banner": b.id, "level": level, "pity": pity, "u": [u1, u2, u3], "forceEplus": force,
                     "rarity": r["rarity"], "poolIndex": r["poolIndex"], "itemId": r["itemId"], "pityAfter": r["pityAfter"],
                     "guaranteeApplied": r["guaranteeApplied"], "bannerLevelAfter": r["bannerLevelAfter"]})
    # 08: batch rows
    batches = []
    for i in range(20):
        b = w if i % 2 == 0 else s
        level = 1 + (i * 3) % len(b.levels)
        pity = [0, 30, 60, 72, 78][i % 5]
        us = []
        for _ in range(b.guarantee_batch):
            us += [0.2 + 0.79 * gen.next(), gen.next() * (0.88 if i % 3 else 1.0), gen.next()]
        st = fresh(b, pity=pity, level=level)
        res = pull10(b, st, Scripted(us))
        batches.append({"banner": b.id, "level": level, "pity": pity, "u": us,
                        "expect": [{"rarity": r["rarity"], "poolIndex": r["poolIndex"], "itemId": r["itemId"],
                                    "pityAfter": r["pityAfter"], "guaranteeApplied": r["guaranteeApplied"]} for r in res]})

    # 13, 14: Monte Carlo, exact counts here plus the spec tolerances
    mc = []
    for name, level in (("v13_mc_level1", 1), ("v14_mc_level10", len(w.levels))):
        res = monte_carlo(w, mc_pulls, 7, level)
        r = w.rates(level)
        tot = r["COMMON"] + r["RARE"] + r["EPIC"]
        mc.append({"name": name, "banner": w.id, "seed": 7, "pulls": mc_pulls, "level": level, "rarityCounts": res["counts"],
                   "batches": res["batches"], "guaranteeFired": res["guaranteeFired"],
                   "expect": {"lplus": pity_numbers(w)["lplus"], "mythic": pity_numbers(w)["mythic"],
                              "C": r["COMMON"] / tot, "R": r["RARE"] / tot, "E": r["EPIC"] / tot,
                              "guaranteeFire": (1 - eplus(w, r)) ** (w.guarantee_batch - 1)},
                   "tolerancePp": {"lplus": 0.1, "mythic": 0.03, "cre": 0.3, "guaranteeFire": 0.5}})

    manifest = json.load(open(os.path.join(content_dir, "manifest.json"), encoding="utf-8"))
    return {
        "format": VECTORS_FORMAT,
        "description": "specs/04 section 3.4 and specs/08 section 4: the summon algorithm's test vectors. Generated by "
                       "tools/gacha_model.py (the independent oracle, decision 46); HeroCat.Sim.Tests only reads it. Scripted "
                       "vectors: start from 'setup' (pity, exp, totalPulls, copies), feed 'u' in order to Pull (mode pull) or "
                       "Pull10 (mode pull10), compare each result and the banner state after. Rows: one Pull from pity and exp "
                       "= threshold(level) - exp_per_pull. Monte Carlo: stream seed 7 from counter 0, banner exp held so each "
                       "pull rolls at 'level'; then pulls/10 ten-pulls on a fresh stream of the same seed, each from pity 0 "
                       "and exp = threshold(level); guaranteeFired counts ten-pulls whose slots 1-9 had no E+.",
        "regenerate": "python3 tools/gacha_model.py --vectors --out Fixtures/Summon/vectors.json",
        "contentVersion": manifest["content_version"],
        "contentHash": manifest["content_hash"],
        "tier": "M0",
        "pityRate": pity_rates,
        "drawCount": draw_count,
        "levels": levels,
        "bannerLevels": banner_levels,
        "scripted": v,
        "rows": rows,
        "batches": batches,
        "monteCarlo": mc,
    }


def dump(obj):
    return json.dumps(obj, indent=1, sort_keys=False) + "\n"


# ------------------------------------------------------------------------------------------------ self-checks


def check(content_dir, mc_pulls):
    failures = []

    def expect(cond, msg):
        if not cond:
            failures.append(msg)

    w = Banner(content_dir, "WEAPON")
    p = pity_numbers(w)
    expect(round(p["expected"], 2) == 27.35, f"expected pulls per L+ {p['expected']:.4f} != 27.35")
    expect(round(p["lplus"] * 100, 3) == 3.657, f"L+ {p['lplus']:.5%} != 3.657%")
    expect(round(p["mythic"] * 100, 3) == 0.609, f"M {p['mythic']:.5%} != 0.609%")
    expect((p["p50"], p["p90"], p["p99"]) == (23, 56, 62), f"P50/P90/P99 {p['p50']}/{p['p90']}/{p['p99']} != 23/56/62")
    expect(round(p["no_lplus_50"], 3) == 0.218, f"P(no L+ in first 50) {p['no_lplus_50']:.4f} != 0.218")
    expect(p["hard"] < 1e-4, "P(reach hard pity) >= 1e-4")
    spec_ramp = {1: 0.03, 50: 0.03, 51: 0.0625, 55: 0.1925, 60: 0.355, 65: 0.5175, 70: 0.68, 75: 0.8425, 79: 0.9725, 80: 1.0, 81: 1.0}
    for n, r in spec_ramp.items():
        expect(abs(w.pity_rate(n) - r) < 1e-12, f"PityRate({n}) {w.pity_rate(n)} != {r}")
    for n in range(1, w.pity_hard + 5):
        expect(w.pity_rate(n + 1) >= w.pity_rate(n), f"PityRate not monotone at {n}")

    for bid in ("WEAPON", "SKILL", "COMPANION"):
        b = Banner(content_dir, bid)
        for level, exp, r in regenerated_rows(b, SHIFTS):
            row = b.rates(level)
            for x in RARITIES:
                expect(abs(row[x] - r[x]) < 1e-12, f"{bid} level {level} {x}: content {row[x]} != regenerated {r[x]}")
            expect(abs(sum(row.values()) - 1.0) < 1e-9, f"{bid} level {level} does not sum to 1")
    r10 = w.rates(len(w.levels))
    expect(eplus(w, r10) <= 0.20, "level 10 P(E+) > 0.20 (paid E+ edge must stay under 1.7x)")
    g1 = (1 - eplus(w, w.rates(1))) ** 9
    expect(round(g1, 3) == 0.316, f"level-1 guarantee fire {g1:.4f} != 0.316")
    g10 = (1 - eplus(w, r10)) ** 9
    expect(round(g10, 3) == 0.147, f"level-10 guarantee fire {g10:.4f} != 0.147")

    # SplitMix64 and the pull sequence against the C# stream fixture (issue #14)
    if os.path.exists(STREAM_FIXTURE_PATH):
        fx = json.load(open(STREAM_FIXTURE_PATH, encoding="utf-8"))
        for d in fx["draws"]:
            seed = int(d["seed"], 16)
            for k, (hexv, unit) in enumerate(zip(d["raw"], d["unit"])):
                x = derive(seed, k)
                if x != int(hexv, 16) or to_unit(x) != unit:
                    failures.append(f"SplitMix64 seed {d['seed']} draw {k}: python {x:016x} != C# {hexv}")
                    break
        for seq in fx["pulls"]:
            b = Banner(content_dir, seq["banner"])
            st = State()
            rng = Stream(int(seq["seed"], 16))
            got = []
            for count in seq["batches"]:
                res = pull10(b, st, rng) if count == b.guarantee_batch else [pull(b, st, rng, False)]
                got += [[r["rarity"], r["itemId"], r["pityAfter"], r["guaranteeApplied"]] for r in res]
            if got != seq["results"]:
                bad = next(i for i, (a, c) in enumerate(zip(got, seq["results"])) if a != c)
                failures.append(f"pull sequence {seq['banner']} seed {seq['seed']}: first mismatch at pull {bad}: "
                                f"python {got[bad]} != C# {seq['results'][bad]}")
            expect(rng.counter == seq["counterAfter"], f"stream counter {rng.counter} != C# {seq['counterAfter']}")
    else:
        failures.append(f"missing {os.path.relpath(STREAM_FIXTURE_PATH, ROOT)} (dotnet run --project src/HeroCat.Bots -- summon-stream --update)")

    # vectors drift (decision 46): regenerate and compare
    fresh_text = dump(build_vectors(content_dir, mc_pulls))
    if not os.path.exists(VECTORS_PATH):
        failures.append(f"missing {os.path.relpath(VECTORS_PATH, ROOT)}: run python3 tools/gacha_model.py --vectors --out Fixtures/Summon/vectors.json")
    elif open(VECTORS_PATH, encoding="utf-8").read() != fresh_text:
        failures.append("Fixtures/Summon/vectors.json is stale: run python3 tools/gacha_model.py --vectors --out Fixtures/Summon/vectors.json")

    if failures:
        print("gacha_model.py --check FAILED:")
        for f in failures:
            print("  - " + f)
        return 1
    print("gacha_model.py --check: pity numbers, rate rows, SplitMix64 and pull sequence vs the C# fixture, vectors up to date")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--content", default=os.path.join(ROOT, "content"))
    ap.add_argument("--banner", default="WEAPON", choices=["WEAPON", "SKILL", "COMPANION", "RUNE"])
    ap.add_argument("--level", type=int, default=1)
    ap.add_argument("--override", action="append", default=[], metavar="NAME=VALUE")
    ap.add_argument("--levels", action="store_true")
    ap.add_argument("--vectors", action="store_true")
    ap.add_argument("--out")
    ap.add_argument("--mc", type=int)
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--mc-vectors", type=int, default=200000,
                    help="Monte Carlo pulls whose exact counts go in the vectors file (vectors 13, 14; the C# test runs 1,000,000)")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--cadence", action="store_true",
                    help="also run power_curve_model.py's F2P bot for the cadence block and the S-constant checks of "
                         "04 section 14; exits 1 when a hard check fails (run by check.sh)")
    ap.add_argument("--seeds", default="1-20", help="bot seeds for --cadence (default 1-20)")
    ap.add_argument("--days", type=int, default=30, help="bot days for --cadence (default 30; later days extrapolate)")
    ap.add_argument("--quiet", action="store_true", help="with --cadence: print only the checks")
    a = ap.parse_args(argv)

    overrides = {}
    for o in a.override:
        k, _, val = o.partition("=")
        overrides[k.strip()] = val.strip()
    shifts = {k: float(overrides.get(k, v)) for k, v in SHIFTS.items()}
    bot_overrides = {k: v for k, v in overrides.items() if k not in SHIFTS}   # shifts only regenerate the rows

    if a.check:
        return check(a.content, a.mc_vectors)
    if a.vectors:
        text = dump(build_vectors(a.content, a.mc_vectors))
        if a.out:
            os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
            with open(a.out, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
        else:
            sys.stdout.write(text)
        return 0
    b = Banner(a.content, a.banner, overrides=overrides)
    if a.levels:
        print_levels(b, shifts)
        return 0
    if a.mc:
        print_mc(b, a.mc, a.seed, a.level)
        return 0
    if not (a.cadence and a.quiet):
        print_report(b, a.level)
        print_fill(b)
    if a.cadence:
        _, code = run_cadence(a.content, bot_overrides, shifts, parse_seeds(a.seeds), a.days, a.quiet)
        return code
    return 0


def parse_seeds(text):
    out = []
    for part in text.split(","):
        if "-" in part:
            lo, hi = part.split("-")
            out += list(range(int(lo), int(hi) + 1))
        elif part:
            out.append(int(part))
    return out


if __name__ == "__main__":
    sys.exit(main())
