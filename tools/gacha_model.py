#!/usr/bin/env python3
"""Exact math for the hero-banner gacha (no simulation; closed-form over the pity counter).

Rates and pity constants here are the source of truth for specs/02-gacha-math.md.
"""
from fractions import Fraction

BASE = {"C": 0.60, "R": 0.28, "E": 0.09, "L": 0.025, "M": 0.005}  # sums to 1.0
L_PLUS = BASE["L"] + BASE["M"]            # 0.03
M_SHARE_OF_LPLUS = BASE["M"] / L_PLUS     # 1/6 of L+ results are Mythic
SOFT_START = 50                            # pulls 1..50 use base rate
SOFT_STEP = 0.0325                         # added to L+ rate per pull past SOFT_START
HARD = 80                                  # pull 80 since last L+ is forced L+


def p_lplus(n: int) -> float:
    """P(L+ on the n-th pull since the last L+), n is 1-based."""
    if n >= HARD:
        return 1.0
    return min(1.0, L_PLUS + SOFT_STEP * max(0, n - SOFT_START))


def pity_distribution():
    """Returns list of (n, P(first L+ happens exactly on pull n))."""
    surv = 1.0
    out = []
    for n in range(1, HARD + 1):
        p = p_lplus(n)
        out.append((n, surv * p))
        surv *= 1 - p
    assert abs(sum(q for _, q in out) - 1) < 1e-12
    return out


def main():
    dist = pity_distribution()
    ev = sum(n * q for n, q in dist)
    print(f"base L+ rate            : {L_PLUS:.4f}  (1 in {1/L_PLUS:.1f})")
    print(f"expected pulls per L+   : {ev:.2f}")
    print(f"consolidated L+ rate    : {1/ev:.4%}")
    print(f"consolidated M rate     : {M_SHARE_OF_LPLUS/ev:.4%}  (expected {ev/M_SHARE_OF_LPLUS:.0f} pulls per Mythic)")
    cum = 0.0
    marks = {0.5: None, 0.9: None, 0.99: None}
    for n, q in dist:
        cum += q
        for m in marks:
            if marks[m] is None and cum >= m:
                marks[m] = n
    print(f"P50/P90/P99 pulls to L+ : {marks[0.5]} / {marks[0.9]} / {marks[0.99]}")
    print(f"P(no L+ in first 50)    : {sum(q for n, q in dist if n > 50):.3f}")
    print(f"P(hit hard pity @80)    : {dist[-1][1]:.4f}")
    print()
    # soft pity ramp table
    print("soft pity ramp (pull -> L+ rate):")
    for n in (50, 51, 55, 60, 65, 70, 75, 79, 80):
        print(f"  {n:>3}: {p_lplus(n):.4f}")
    print()
    # 10-pull E+ guarantee: P(no E+ in 9 pulls) at base rates
    e_plus = BASE["E"] + L_PLUS
    p_force = (1 - e_plus) ** 9
    print(f"E+ base rate            : {e_plus:.3f}")
    print(f"P(10-pull forces E+ on last slot): {p_force:.3f}")
    print(f"effective E+ per 10-pull: {10*e_plus + p_force*(1-e_plus):.3f} (vs {10*e_plus:.2f} without)")
    print()
    # F2P cadence
    for pulls_per_day in (3, 6, 9):
        print(f"{pulls_per_day} pulls/day -> L+ every {ev/pulls_per_day:.1f} days, worst case {HARD/pulls_per_day:.1f} days")


if __name__ == "__main__":
    main()
