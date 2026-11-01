# 02 - Power Curve Math

Scope: how enemy difficulty, gold income and the four power layers grow against each other, and how the bot in `tools/power_curve_model.py` validates it. Constants are canonical in `specs/00` section 2.

## 1. Design goals

1. Early game is fast: ~90 stages in the first two hours. A new player should see a class advance (stage 50) on day 1.
2. Walls appear from day 2 and dominate from day 7. A wall should last 1-6 hours of real time, not days, until the late game (stage 450+) where 1-2 day walls are acceptable.
3. Every layer stays relevant at every stage: no layer's share of total power exceeds ~70% or falls below ~5% over the first 30 days (section 5 checks this).
4. Idle income alone must not break walls; it should fund the stat layer and enhancement so that the *decision* of what to spend on matters.

## 2. Enemy and income curves

```
HP(s)    = 20 * 1.07^s          # normal mob
BossHP(s)= 10 * HP(s)           # s % 10 == 0, 20 s limit
Gold(s)  = 2 * 1.05^s           # per normal kill; boss kill = 3x
```

| Stage | HP | Boss HP | Gold/kill | DPS for 2 s kills |
|---|---|---|---|---|
| 1 | 21 | - | 2.1 | 11 |
| 10 | 39 | 393 | 3.3 | 20 |
| 50 | 589 | 5.9e3 | 23 | 295 |
| 100 | 1.7e4 | 1.7e5 | 263 | 8.7e3 |
| 200 | 1.5e7 | 1.5e8 | 3.5e4 | 7.5e6 |
| 300 | 1.3e10 | 1.3e11 | 4.5e6 | 6.5e9 |
| 400 | 1.1e13 | 1.1e14 | 6.0e8 | 5.7e12 |
| 550 | 2.9e17 | 2.9e18 | 9.0e11 | 1.5e17 |

Why gold grows slower than HP (1.05 vs 1.07): if gold tracked HP, the gold sink alone would keep pace with the curve and the other layers would be optional. The 1.02/stage gap is what forces the player into gear and gacha.

Numbers this size need a display formatter (K, M, B, T, aa, ab...). `double` is fine for P0 (max ~1e308; stage 550 is 1e17). If stages ever exceed ~9000, switch the sim to a mantissa/exponent type.

## 3. Kill time and the wall

Hits per kill `h = ceil(HP / hitDamage)`; kill time `= h * 1.0 s + 0.5 s spawn`. Minimum kill time is 1.5 s (one hit). With `KILLS_PER_STAGE = 10`, the fastest a player can push is 15 s per stage, 4 stages/min, 480 stages in a 2 h session. The curve is tuned so that this ceiling is only reached in the first ~40 stages.

**Wall condition.** Boss stage `s` is cleared iff `10 * HP(s) <= DPS(s) * 20 s`, equivalently `HP(s)/DPS(s) <= 2 s`: *if normal mobs on this stage take more than 2 hits, the boss cannot be cleared.* This is the single most important tuning relationship in the game because it is what the player feels as "stuck".

When walled, the player farms stage `s` mobs (not `s-1`), earns `Gold(s)`, drops tier-`s` gear, and can retry the boss manually (P0: a Boss button; P1: auto-retry every 60 s). A boss attempt that times out costs nothing but the 20 s.

## 4. The four layers

```
DPS(s) = 10 * Stat(n) * Gear(s') * Hero(t) * Class(s)
```

### 4.1 Stat layer (gold), log-growth

`Stat(n) = 1 + 0.05 n`, `Cost(n) = 50 * 1.07^n`. Total gold to reach level `n` is `50 * (1.07^n - 1) / 0.07 ~ 714 * 1.07^n`.

Inverting: given lifetime gold `G`, `n ~ log(G / 714) / log(1.07)`. Gold income at stage `s` grows like `1.05^s`, so `n` grows *linearly* in `s`: roughly `n ~ 0.70 s + const` when the player is pushing, which is exactly what the bot shows (stat level 64 at stage 90, 461 at stage 547, slope ~0.85 because offline gold adds a bit).

Contribution: x4 on day 1, x16 on day 7, x24 on day 30. It is the smooth "number goes up every minute" layer and the thing idle gold is for.

### 4.2 Gear layer (drops), exponential in tier

Piece ATK% `= (1.0074^T - 1) * rarityMult`, `T = floor(s/10)*10`. Six slots of Common gear at tier `T` multiply to `1.0074^(6T) = 1.045^T`. Rarity multiplies the *percentage* not the multiplier, so at tier 300 a Common piece is +818%, a Legendary +1636%, a Mythic +2454%.

Drop rate 0.5%/kill. At 20 kills/min that is one piece per 10 minutes, so in a 2 h session the player sees ~12 pieces across 6 slots and is usually 1-3 tiers behind on some slot. That lag is deliberate: it is what makes a Legendary drop in a lagging slot feel like a jump (and it is). Boss kills drop at 10% and at least Rare so clearing a wall is rewarded.

Enhancement adds up to +75% of the piece's base pct at level 15 with a declining success chance; it is the gold sink that competes with stat levels, and the bot spends ~half its late-game gold here.

Contribution: x31 on day 1, x6e8 on day 7, x1e13 on day 30. This is the layer that carries the exponential.

### 4.3 Hero layer (gacha), stepwise in time

`Hero = 1 + sum(top 6 heroPct) / 100`. It depends on pulls (time and money), not on stage. With 6 pulls/day the bot sits at x1.9 on day 1, x3.4 on day 7, x5.6 on day 30. A whale buying 100 pulls on day 1 would start near x4-5: roughly a two-week head start in this layer, which multiplies through to about 25-35 stages at the day-7 slope. That is the intended size of the pay-to-progress edge; if it feels too big, the lever is hero base pct, not rates.

### 4.4 Class layer (milestones)

x1.5 at stages 50, 150, 300, 500. Each one is placed just past a region where the bot stalls, so the advance is felt as "the wall broke." Class advances also gate cosmetic/identity changes in `specs/07`.

## 5. Equilibrium and layer shares

Taking logs of the wall condition at the frontier (`HP/DPS = 2 s`):

```
s * ln 1.07 + ln 10 = ln 10 + ln Stat + ln Gear + ln Hero + ln Class     (approx: ignores the 2 s and tier flooring)
s * ln 1.07           ~ ln Stat(n) + T * ln 1.045 + ln Hero + k * ln 1.5
```

With `T ~ s`, the residual per stage is `ln(1.07/1.045) = 0.0237`, so `0.0237 s ~ ln Stat + ln Hero + k ln 1.5`. At `s = 550` the left side is 13.0 (= x440,000). Bot at day 30: Stat 24 (3.2), Hero 5.6 (1.7), Class 5.06 (1.6) = 6.5, leaving 6.5 nats (x670) to be covered by gear rarity (average rarityMult of equipped gear > 1 pushes gear above the 1.045 line) and by gear tiers above Common. That matches the bot (gear x1.1e13 vs `1.045^550 = 3.3e10`, a x330 rarity surplus).

Layer shares of `ln DPS` from the bot run:

| Day | Stage | Stat | Gear | Hero | Class |
|---|---|---|---|---|---|
| 1 | 90 | 17% | 42% | 8% | 5% |
| 7 | 380 | 10% | 73% | 4% | 4% |
| 30 | 547 | 8% | 77% | 4% | 4% |

(The remainder is `BASE_ATK`.) Gear is slightly above the 70% goal from day 7 on. Acceptable for P0; if playtests say gear is the only thing that matters, raise hero base pct by 1.5x and lower `GEAR_SLOT_G` to 1.0070. Keep the *sum* of exponents constant, otherwise the day-30 stage moves.

## 6. Bot results (seed 7, `tools/power_curve_model.py`)

```
day  1: stage   90  dps 3.72e+03  statLvl  64 (x 4.2)  gear x 31        heroes x1.90  class x1.50  pulls   6 L+ 0
day  2: stage  167  dps 5.17e+05  statLvl 123 (x 7.2)  gear x 1.24e+03  heroes x2.60  class x2.25  pulls  12 L+ 0
day  3: stage  240  dps 6.15e+07  statLvl 172 (x 9.6)  gear x 9.48e+04  heroes x3.00  class x2.25  pulls  18 L+ 0
day  5: stage  340  dps 5.24e+10  statLvl 249 (x13.5)  gear x 3.39e+07  heroes x3.40  class x3.38  pulls  30 L+ 0
day  7: stage  380  dps 1.14e+12  statLvl 300 (x16.0)  gear x 6.18e+08  heroes x3.40  class x3.38  pulls  42 L+ 0
day 10: stage  450  dps 4.54e+13  statLvl 358 (x18.9)  gear x 1.55e+10  heroes x4.60  class x3.38  pulls  60 L+ 1
day 14: stage  480  dps 7.04e+14  statLvl 396 (x20.8)  gear x 2.18e+11  heroes x4.60  class x3.38  pulls  84 L+ 1
day 21: stage  520  dps 1.35e+16  statLvl 436 (x22.8)  gear x 2.13e+12  heroes x5.48  class x5.06  pulls 126 L+ 3
day 30: stage  547  dps 7.47e+16  statLvl 461 (x24.1)  gear x 1.09e+13  heroes x5.64  class x5.06  pulls 180 L+ 4
```

Pace: 90 stages day 1, ~75/day through day 5, ~20/day days 5-10, ~6/day days 14-30. Mid-game (days 3-7) is faster than the target table in `specs/00`; late game is on target. Known gap: this seed got 4 L+ in 180 pulls against an expectation of 6.6, so day-30 hero mult is a bit low. The harness in `specs/09` runs 20 seeds and reports the median.

Model simplifications the real sim does not share: per-minute aggregation instead of ticks, greedy spending (buy every affordable stat level, one enhancement attempt per minute), no substats, no crit. Treat the bot's stage numbers as +/-15%.

## 7. Offline catch-up math

Offline earnings never push stages; they only accrue gold (and, P1, idle-chest gems). With kill time `k` seconds at the current stage and elapsed offline time `t`:

```
offlineGold = min(t, 8 h) * OFFLINE_RATE * Gold(s) / k
```

This is closed-form, so catch-up is O(1) regardless of how long the player was away. The 50% rate and 8 h cap mean a player who checks in twice a day earns 2 h active + 2 x 4 h-equivalent offline = 10 h-equivalent of gold per day; a once-a-day player earns 6 h-equivalent. At `Gold(s)` growing 1.05/stage, logging in twice a day is worth about 10 stages of gold income, which is a meaningful but not punishing difference.

Why offline does not push stages: pushing requires the gear-drop RNG stream and boss attempts. Simulating that offline would either need a full tick replay (expensive, and the player would miss the drops) or a statistical approximation that cheats the RNG commitment. Idle games in this genre uniformly accrue currency only, and players expect it.

In-app idle (phone on, app open) runs the real sim at full rate and does push stages. That is the "leave it running on the desk" mode and is intentionally better than offline.

## 8. Tuning levers

| Symptom | Lever | Side effect |
|---|---|---|
| Day 1 too fast/slow | `HP0` (shifts whole curve), or `BASE_ATK` | None on late game shape. |
| Late game stalls | `GEAR_SLOT_G` up (1.0074 -> 1.0078 is roughly +30% stage by day 30) | Gear share rises. |
| Gold feels useless late | `GOLD_G` up toward 1.055, or `STAT_COST_G` down toward 1.065 | Stat layer share rises; also makes enhancement cheaper in relative terms. |
| Walls too long | `BOSS_TIME_TICKS` 200 -> 250, or `BOSS_HP_MULT` 10 -> 8 | Fewer walls, flatter feel. Prefer this to touching HP_G. |
| Walls too short | opposite | |
| Gacha feels irrelevant | hero base pct x1.5 | Whale edge grows proportionally. |
| Everything scales but feels samey | add a class at 80 or spread class stages | Cheap, high perceived value. |

Never change `HP_G` to fix a local problem; it moves every stage at once. Change the layer that is misbehaving.

## 9. Acceptance criteria (enforced by `specs/09` harness)

1. Median of 20 seeds lands within +/-20% of the stage targets in `specs/00` section 2.6 for days 1, 7, 30.
2. No layer share (section 5 table) exceeds 80% or drops below 3% on any of days 1, 7, 30.
3. First wall (boss failed at least once) occurs between stage 60 and stage 200 in every seed.
4. No wall opened in the first 14 days lasts more than 48 h wall-clock (one overnight gap plus a session). The harness also reports the longest wall in active hours (see `specs/00` section 7, decision 3).
5. Stat level never exceeds `0.95 * stage + 50` (if it does, gold is too plentiful relative to cost).
6. The run is deterministic for a fixed seed: rerunning yields byte-identical output.

## 10. P1 additions and how they fit

- **Substats** (crit chance, crit damage, attack speed, gold find) go into the gear layer as an extra multiplicative term bounded to ~x2 total so they do not disturb section 5.
- **Daily dungeons** drop enhancement materials (replacing gold as the enhancement cost, which decouples the two gold sinks). Keep the per-day expected enhancement levels equal to what the gold path gives today.
- **Prestige** resets stage and stat levels for a permanent `1.02^maxStage`-style multiplier. Only add once the day-30 curve is stable; it changes everything downstream.
