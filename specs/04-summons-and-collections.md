## 04 - Summons and Collections

This spec owns the four `Banner`s, the exact pull algorithm and its pity rules, banner levels, the dupe economy including limit breaks, and the three collectible kinds the banners feed: `Weapon`, `Skill`, `Companion` (and the P1 `Rune` outline). It fixes the data each collectible carries, how the tick loop in 03 consumes it, what the collection screens need from the model, the `ISummonService` seam between the M0 local roll and the MVP server roll, the analytics events, and how `tools/gacha_model.py` is extended for banner levels. The server-side implementation of the same algorithm (audit log, idempotency, published rate endpoint) is 08's.

Tiers covered: M0 (`WEAPON` and `SKILL` banners with a local roll, dupes to level 10, weapon passive, 6 skill slots, and the banner-level mechanic, since the one pull algorithm evaluates banner level per pull, 00 decision 56), MVP (`COMPANION` banner, banner levels as a tuned feature (00 section 2.1, decision 70), limit breaks, server roll, analytics), P1 (`RUNE` banner, manual skill cast, rate-up and event banners).

Depends on: 00 (section 6.3 constants, section 5 names), 02 (`GameState` collection fields), 03 (tick loop, `MarkDpsDirty`, Rock Candy), 06 (diamond and ticket income, Rock Candy income), 08 (server summon endpoint).

## 1. Design goals

1. Published odds are the odds. Banner levels only move weight between C, R and E; the L and M rates printed on `SCR_SUMMON` are the same on day 1 and day 300.
2. Hard pity is a guarantee, not a lever. The experience is tuned by the soft ramp; `PITY_HARD = 80` exists for the disclosure text (P99 to L+ is 62).
3. Every copy is worth something: dupes level the item, overflow converts to Rock Candy, and every owned weapon contributes passively whether equipped or not.
4. Nothing the client holds can change a result. After M0 the seed never leaves the server; in M0 the local stream is `(seed, counter)` so a reloaded save re-receives the same results.
5. The cheapest banner is the one with the collection hook. Weapons cost 100 because the passive makes every weapon pull progress; skills and companions cost more because their value is concentrated in the equipped few.

## 2. Banners

| Banner | Tier | Pull cost (`PULL_COST`) | Ticket | Pool | Slots fed | Dupe effect |
|---|---|---|---|---|---|---|
| `WEAPON` | M0 | 100 `DIAMOND` | `TICKET_SUMMON_WEAPON` | 30 | 1 equipped + passive from all owned | level raises equipped pct and passive pct |
| `SKILL` | M0 | 200 `DIAMOND` | `TICKET_SUMMON_SKILL` | 40 | `SKILL_SLOTS = 6` | level raises `dpsPct` |
| `COMPANION` | MVP | 300 `DIAMOND` | `TICKET_SUMMON_COMPANION` | 50 | `COMPANION_SLOTS = 4` (slots 2-4 unlock with Awakening, 03 section 5.3) | level raises ability magnitude |
| `RUNE` | P1 | 300 `DIAMOND` | `TICKET_SUMMON_RUNE` | 36 | `RUNE_SLOTS = 6` | level raises stat value |

A 10-pull costs exactly `10 * PULL_COST` or 10 tickets of the banner; there is no diamond discount (00 section 6.2). Mixed payment is not allowed: a pull is paid with one ticket or with diamonds; a 10-pull with ten tickets or with diamonds. Tickets are spent before diamonds when the player presses the diamond button and has tickets (the button label shows which will be used, 07). The pull command is one request whether it is 1 or 10 pulls:

```csharp
public enum SummonPayment { Diamonds, Tickets }
public sealed record SummonRequest(string RequestId /* GUID "N" format, 01 section 2.5 */, Banner Banner, int Count /* 1 or 10 */, SummonPayment Payment);
```

Each banner has its own `BannerState` in `GameState.Banners` (02): `Pity`, `TotalPulls` and cumulative `Exp`; banner level is derived from `Exp` and never stored (issue #7). Each banner also has a `summon_<banner>` stream in `GameState.Rng`, kept in every save (00 decision 14) and drawn only in Local mode. Pity does not carry across banners.

No banner has an unlock gate: in 09's `feature_unlocks` table the summon banners, including `COMPANION`, are always open (issue #45). Only companion slots 2-4 are gated (section 8.2).

## 3. The pull algorithm

### 3.1 Rates and pity

Base rates at banner level 1 (00 section 6.3): C 0.60, R 0.28, E 0.09, L 0.025, M 0.005. `L+` means L or M; base `P(L+) = 0.03`; `P(M | L+) = M_SHARE_OF_LPLUS = 1/6`.

`Pity` (stored in `BannerState`) is the number of pulls since the last L+ on this banner before this pull, `0..PITY_HARD-1`. The rate for a pull uses `n = Pity + 1`, the count including the current pull (issue #24).

```
PityRate(n) = 1.0                                        if n >= PITY_HARD (80)
            = min(1.0, 0.03 + PITY_SOFT_STEP * (n - 50))  if 50 < n < 80          PITY_SOFT_STEP = 0.0325
            = 0.03                                        if n <= PITY_SOFT_START (50)
```

When the L+ check fails, the tier is drawn from the banner level's C/R/E weights renormalised to sum to 1 (section 4). When it succeeds, M with probability 1/6 else L, and the counter resets to 0, including on L+ results that came out of the 10-pull guarantee.

Exact distribution (closed form over the counter, from `gacha_model.py`; identical for every banner and every banner level because levels never touch L or M):

```
expected pulls per L+       : 27.35
consolidated L+ rate        : 3.657%
consolidated M rate         : 0.609%   (one Mythic per 164 pulls)
P50 / P90 / P99 pulls to L+ : 23 / 56 / 62
P(no L+ in first 50)        : 0.218
P(reach hard pity at 80)    : < 1e-4
```

### 3.2 One pull

Three uniform draws per pull, always consumed, in this order, so the audit log (08) and the M0 stream counter are `3 * TotalPulls`:

1. `u1` against `PityRate(n)` for the L+ check.
2. `u2` for the tier inside the branch (M vs L; or C/R/E renormalised; or forced E).
3. `u3` for the item index inside the tier's pool, uniform.

```csharp
public readonly record struct PullResult(
    Banner Banner, string ItemId, Rarity Rarity, bool IsNew, bool GuaranteeApplied,
    int CopiesAfter, int LevelBefore, int LevelAfter, int OverflowRockCandy, int PityAfter, int BannerLevelAfter);

// Shared by LocalSummonService (M0, HeroCat.Runtime) and the server (MVP, HeroCat.Server). Lives in HeroCat.Sim.Summons.
// st and col are working copies (LocalSummonService clones them; the server loads its own rows); never the live GameState (issue #13).
public static PullResult Pull(BannerDef def, BannerState st, CollectionState col, IUniformSource rng, bool forceEplus)
{
    int n = st.Pity + 1;                                   // pulls since last L+, counting this one (issue #24)
    st.TotalPulls += 1; st.Exp += def.BannerExpPerPull;
    double u1 = rng.Next(), u2 = rng.Next(), u3 = rng.Next();
    int level = BannerLevel(def, st.Exp);                  // evaluated per pull, after this pull's EXP
    Rarity rarity;
    bool lplus = u1 < PityRate(def, n);
    st.Pity = lplus ? 0 : n;
    if (lplus)            { rarity = u2 < def.MythicShareOfLplus ? Rarity.M : Rarity.L; }
    else if (forceEplus)  { rarity = Rarity.E; }
    else                  { rarity = SampleCRE(def.RateTable(level), u2); }        // walks C, R, E cumulative weights / (1 - 0.03)
    var pool = def.Pool[rarity];
    string itemId = pool[(int)(u3 * pool.Count)];

    var owned = col.GetOrAdd(itemId);
    int before = Level(owned.Copies);
    bool isNew = owned.Copies == 0;
    int overflow = 0;
    if (owned.Copies >= DUPE_LVL_CUM[^1]) overflow = DUPE_OVERFLOW_ROCK_CANDY[(int)rarity];   // copy 161+ converts, section 5.3
    else owned.Copies += 1;
    int after = Level(owned.Copies);
    return new PullResult(def.Banner, itemId, rarity, isNew, guaranteeApplied: forceEplus && !lplus,
                          owned.Copies, before, after, overflow, st.Pity, level);
}

public static PullResult[] Pull10(BannerDef def, BannerState st, CollectionState col, IUniformSource rng)
{
    var r = new PullResult[10];
    for (int i = 0; i < 9; i++) r[i] = Pull(def, st, col, rng, forceEplus: false);
    bool anyEplus = r.Take(9).Any(x => x.Rarity >= Rarity.E);
    r[9] = Pull(def, st, col, rng, forceEplus: !anyEplus);
    return r;
}

static double PityRate(BannerDef d, int n) =>
    n >= d.PityHard ? 1.0 : Math.Min(1.0, d.BaseLplus + d.PitySoftStep * Math.Max(0, n - d.PitySoftStart));
```

`SampleCRE(rates, u)` walks C then R then E with each weight divided by `(rates.C + rates.R + rates.E) = 0.97` and returns the first tier whose cumulative weight exceeds `u`; it returns E if rounding leaves `u` above the last boundary. The forced E+ slot still runs the L+ check first, so a 10-pull's last slot can be L or M; if the check fails it is E, never a renormalised draw over E/L/M. This matches the sibling's implementation and keeps "at least one E+" literally true without changing the L+ distribution.

`Pull` has no wallet in scope, so the same function runs on the server. The service maps each `PullResult` to a `SummonPull` of 01's `SummonResultPayload` (section 12); the live state, wallet and quests change only in `Simulation.ApplySummonResult` (section 10, issue #13).

### 3.3 10-pull guarantee numbers

`P(guarantee fires) = (1 - P(E+))^9`. At banner level 1, `P(E+) = 0.12`, so the slot-10 upgrade fires in 31.6% of 10-pulls and expected E+ per 10-pull rises from 1.20 to 1.48. At banner level 10, `P(E+) = 0.192`, it fires in 14.7% and expected E+ per 10-pull is 2.04. The guarantee never lowers anyone's odds.

### 3.4 Test vectors

`tools/gacha_model.py --vectors` generates the one vectors file `Fixtures/Summon/vectors.json`: the 16 named cases below (13 and 14 as tolerance checks) plus 08's row and batch vectors. `HeroCat.Sim.Tests/SummonTests.cs` and `HeroCat.Server.Tests/SummonAlgorithmTests.cs` only read it; `check.sh` regenerates and diffs it. Python is the independent oracle for scripted-`u` vectors. A C#-generated stream-sequence fixture (seed -> 1,000 pulls) in the same `Fixtures/Summon/` directory stays as a determinism golden, and Python's SplitMix64 must reproduce it (issues #14, #24). "Pity" in the table is the stored value before the pull (`n = Pity + 1`).

| # | Setup | Expect |
|---|---|---|
| 1 | `PityRate(n)` (n = Pity + 1) for n = 1, 50, 51, 55, 60, 65, 70, 75, 79, 80, 81 | 0.03, 0.03, 0.0625, 0.1925, 0.355, 0.5175, 0.68, 0.8425, 0.9725, 1.0, 1.0 |
| 2 | Level-1 rates, `u1 = 0.5, u2 = 0.0, u3 = 0.0`, Pity 1 | C, first item of C pool, Pity 2 |
| 3 | Level-1 rates, `u2 = 0.60/0.97 - 1e-9` and `0.60/0.97` | C then R (boundary is strict) |
| 4 | `u2 = 0.88/0.97 - 1e-9` and `0.88/0.97`, `u1 = 0.5` | R then E |
| 5 | Pity 78 (n = 79), `u1 = 0.9725 - 1e-9` and `0.9725` | L+ then not L+ |
| 6 | Pity 79 (n = 80, rate 1.0), `u1 = 0.999999`, `u2 = 1/6 - 1e-9` | M, Pity reset to 0 |
| 7 | Pity 79, `u1 = 0.999999`, `u2 = 1/6` | L, Pity 0 |
| 8 | `Pull10` with `u1 = 0.99` for all, `u2 = 0.0` | slots 1-9 C, slot 10 E with `GuaranteeApplied` |
| 9 | `Pull10` with slot 3 scripted to E, otherwise as 8 | slot 10 C (guarantee did not fire) |
| 10 | `Pull10` with slots 1-9 C and slot 10 `u1 = 0.0` | slot 10 is L+ (L+ check runs before the forced E) |
| 11 | Draw count after 1,000 pulls | stream advanced by exactly 3,000; `TotalPulls == 1000`; `Exp == 1000` |
| 12 | Copies 1, 2, 3, 6, 10, 160, 161 | levels 1, 1, 2, 3, 4, 10, 10; copy 161 returns `OverflowRockCandy == DUPE_OVERFLOW_ROCK_CANDY[r]` and `CopiesAfter == 160` |
| 13 | Monte Carlo 1,000,000 pulls, seed 7, level 1 | L+ frequency within 0.1 pp of 3.657%; M within 0.03 pp of 0.609%; C/R/E split within 0.3 pp of 0.60/0.28/0.09 of non-L+ |
| 14 | Monte Carlo 1,000,000 pulls at banner level 10 | C/R/E split within 0.3 pp of 0.42/0.388/0.162 of non-L+; L+ unchanged |
| 15 | Same seed and counter on two `BannerState`s | identical 1,000-pull sequences; after a save/load round-trip the next pull equals the un-restored copy's |
| 16 | Banner level thresholds | `Exp` 0, `BANNER_LVL_EXP[2] - 1`, `BANNER_LVL_EXP[2]`, `BANNER_LVL_EXP[10] - 1`, `BANNER_LVL_EXP[10]` and a value above it give levels 1, 1, 2, 9, 10, 10 (values read from content; with today's thresholds: 0, 9, 10, 539, 540, 10000; issue #38) |

## 4. Banner level

Each pull on a banner grants `BANNER_EXP_PER_PULL = 1` EXP to that banner, paid or ticket, single or 10-pull slot. Level is the count of thresholds reached:

| Level | `BANNER_LVL_EXP` (cumulative pulls; tuned from the tool's cadence, issue #38) | C | R | E | L | M | P(E+) |
|---|---|---|---|---|---|---|---|
| 1 | 0 | 0.600 | 0.280 | 0.090 | 0.025 | 0.005 | 0.120 |
| 2 | 10 | 0.580 | 0.292 | 0.098 | 0.025 | 0.005 | 0.128 |
| 3 | 25 | 0.560 | 0.304 | 0.106 | 0.025 | 0.005 | 0.136 |
| 4 | 55 | 0.540 | 0.316 | 0.114 | 0.025 | 0.005 | 0.144 |
| 5 | 90 | 0.520 | 0.328 | 0.122 | 0.025 | 0.005 | 0.152 |
| 6 | 145 | 0.500 | 0.340 | 0.130 | 0.025 | 0.005 | 0.160 |
| 7 | 215 | 0.480 | 0.352 | 0.138 | 0.025 | 0.005 | 0.168 |
| 8 | 305 | 0.460 | 0.364 | 0.146 | 0.025 | 0.005 | 0.176 |
| 9 | 415 | 0.440 | 0.376 | 0.154 | 0.025 | 0.005 | 0.184 |
| 10 | 540 | 0.420 | 0.388 | 0.162 | 0.025 | 0.005 | 0.192 |

Rule: per level above 1, `C -= BANNER_LVL_C_SHIFT (0.020)`, `R += BANNER_LVL_R_SHIFT (0.012)`, `E += BANNER_LVL_E_SHIFT (0.008)`; L and M never change. Every row sums to 1.0 within 1e-9 and the content validator (09) rejects a `banner_levels` table that does not. The table is content, not code: the sim reads the row for the current level; the shift constants exist only so the sheet can regenerate the rows.

What the player sees: `SCR_SUMMON` shows the current level's full rate table, the next threshold ("12 pulls to level 4"), and a note "Legendary and Mythic rates do not change with level".

Threshold rule (issue #38): F2P pull cadence per banner is never hand-entered. `gacha_model.py` computes it from 06's diamond and ticket income and 03's bot spending policy (section 14), and `BANNER_LVL_EXP` is retuned from that output so the F2P bot reaches weapon banner level 4 between day 20 and day 35. The table above is that retune: the original placeholder column (0, 50, 150, ..., 3000) scaled by 0.18 and rounded to multiples of 5, which keeps the level count and the shape. At the computed cadence (`power_curve_model.py --check`, 20 seeds, 30 days, diamonds from 06's quests, login, first clears and achievements since issue #178: weapon 2.37 pulls/day, range 1.70..2.97; skill 1.37; companion 0.58) the weapon banner reaches level 2 on day 1, level 3 around day 6-7 and level 4 on a median day 23.5 (range 9..33 over a 45-day run; 19 of 20 seeds by day 30). The skill banner reaches level 2 around day 5 and level 3 around day 14 and does not reach level 4 inside 30 days (1 of 20 seeds does); the companion banner reaches level 2 around day 15 and level 3 after day 30 on 17 of 20 seeds. (The retune itself was made at the earlier flat 400 diamonds a day, median day 27.) Skill and companion have no separate level target. A whale buying 540 pulls reaches level 10 on one banner; its E+ rate is 1.6x the F2P player's, its L+ rate identical. That is the intended shape of the paid edge on summons: more dupes and more Epics, not more Mythics per pull.

Banner level is a per-banner integer derived from the cumulative `BannerState.Exp` at read time, never stored, and not reset by anything (issue #7). P1 rate-up banners (section 9) share the parent banner's level and pity.

## 5. Dupe economy

### 5.1 Levels from copies

```
DUPE_LVL_CUM = [1, 3, 6, 10, 16, 25, 40, 64, 100, 160]       # total copies for level 1..10, all collectible kinds
Level(copies)        = count of thresholds <= copies            # 0 if unowned, 1..10
EffectiveLevel(item) = Level(copies) + limitBreaks              # 1..15
LevelMult(item)      = 1 + DUPE_LVL_BONUS * (EffectiveLevel - 1) # DUPE_LVL_BONUS = 0.20; level 10 = 2.8x, level 15 = 3.8x
```

`LevelMult` scales a weapon's equipped pct and passive pct, a skill's `dpsPct` and per-cast numbers, a companion's ability magnitude, a rune's stat value. Level is derived from copies at read time and never stored (02's `OwnedItem { Copies, LimitBreaks, FirstAcquiredServerTime }`, issue #7), so retuning `DUPE_LVL_CUM` reflows every save.

Expected pace, per weapon pull at banner level 1: a specific Common weapon (10 in the pool, 0.60 rate) gains a copy about every 17 pulls, so level 5 (16 copies) takes about 270 weapon pulls and level 10 about 2,700. A specific Legendary weapon (4 in the pool, consolidated L rate about 3.05%) gains a copy about every 130 pulls: level 3 (6 copies) takes about 790. The pace in days comes from the per-banner cadence the tools compute (section 14, issue #38); no hand-entered pulls-per-day figure is used. At the computed F2P weapon cadence (20-seed median 2.37 pulls/day, range 1.70..2.97) about 71 weapon pulls land in 30 days, so a specific Common reaches about level 2-3 (4-5 copies) by day 30 and level 5 after roughly 115 days; the weapon banner level 4 (55 pulls) lands on a median day 23.5. L and M leveling is the long tail; C/R/E leveling is the daily visible progress. That is the same split the sibling tuned and it is why the pools are small.

### 5.2 Limit breaks (MVP, Rock Candy sink)

Past level 10 a copy cannot raise the level. The reference game's answer is a limit break, and 00 section 5.2 already names "weapon/skill limit breaks" as a Rock Candy sink.

```
LimitBreak(itemId):                                         # command, from SCR_ITEM_DETAIL
  if itemId is no weapon/skill/companion row:    return Reject(InvalidTarget, "UNKNOWN_ITEM")   # as EquipWeapon (issue #162)
  item = col.Items[itemId]                     # col: the kind's collection; absent key (or Copies 0) == not owned
  if item is null:                               return Reject(NotOwned, "ITEM_NOT_OWNED")
  if item.LimitBreaks >= LIMIT_BREAK_RANKS (5):  return Reject(Capped, "LIMIT_BREAK_CAPPED")
  cost = LIMIT_BREAK_COST[rarity(itemId)] * (item.LimitBreaks + 1)   # rarities.limit_break_cost (09)
  if Wallet.RockCandy < cost:                    return Reject(CannotAfford, "NOT_ENOUGH_ROCK_CANDY")
  Wallet.Apply(-cost ROCK_CANDY, LIMIT_BREAK)
  item.LimitBreaks += 1; MarkDpsDirty()
  publish ItemLimitBroken(itemId, item.LimitBreaks, cost)
```

| Rarity | `LIMIT_BREAK_COST[r]` per rank (x rank; placeholder, issue #36) | Ranks 1-5 total |
|---|---|---|
| C | 10 | 150 |
| R | 20 | 300 |
| E | 40 | 600 |
| L | 80 | 1200 |
| M | 150 | 2250 |

A limit break does not require level 10 first; it is a flat +1 effective level, which is what makes it useful on a level-1 Mythic weapon (+20% of 700% = +140% ATK for 150 candy). The bot spends them almost all on the equipped Mythic or Legendary weapon and the top two skills; when they start and how many it holds by day 30 is `power_curve_model.py` output, not a hand figure. They are the main reason the summon layers keep growing after the pools saturate (03 section 8.2), so `LIMIT_BREAK_COST` is an `S` constant that `power_curve_model.py` validates: the bot must buy at least one limit break a day from day 10, and limit breaks must stay under 60% of its Rock Candy spend over 30 days (Awakening must still happen). Limit-break repricing is a candidate lever for the day-14-to-30 wall (03 section 9); the cost values above stay placeholders until the tool has run and the lever is chosen (issue #36).

Bot hook (issue #162): `Query.NextLimitBreakCost(itemId)` is the charge above (0 when unknown, unowned or maxed) and `Query.RatedDpsAfterLimitBreak(itemId)` is `RatedDps` with one more rank on that item; the bot ranks by `ln(after / RatedDps) / cost` and takes the log itself, since the sim never calls `Math.Log` (01 AC 1).

Content home (issue #37): `LIMIT_BREAK_RANKS` is a scalar in 09's `constants`; `LIMIT_BREAK_COST[r]` is the per-rarity column `rarities.limit_break_cost` and `DUPE_OVERFLOW_ROCK_CANDY[r]` (section 5.3) is `rarities.dupe_overflow_rock_candy`. All three are in 09's `REQUIRED_CONSTANTS`.

Limit breaks apply to weapons, skills and companions (and runes in P1); 00 decision 13 names companions as a Rock Candy sink.

### 5.3 Overflow past 160 copies

`Copies` is capped at 160 and there is no stored overflow counter (issue #7). A copy received when `Copies == 160` is not stored; it converts to Rock Candy immediately at pull time:

| Rarity | `DUPE_OVERFLOW_ROCK_CANDY[r]` |
|---|---|
| C | 1 |
| R | 2 |
| E | 5 |
| L | 20 |
| M | 50 |

Decision: Rock Candy conversion, not shards. 00 has no shard currency and adding one adds a wallet column, a shop and a migration for a currency that would only matter to players with 160 copies of something (a Common weapon after about seven months of F2P play). Rock Candy already has two sinks the overflow feeds (Awakening and limit breaks). The `SummonPull.OverflowRockCandy` field and the `ItemAcquired` event carry it so `SCR_SUMMON_RESULT` can show "+1 Rock Candy (max copies)".

## 6. Weapons

### 6.1 Pool

30 weapons. Rarity split and the two percentages each carries:

| Rarity | Count | `WEAPON_ATK_PCT[r]` (equipped, at level 1) | `WEAPON_PASSIVE_PCT[r]` (owned, at level 1) |
|---|---|---|---|
| C | 10 | 20 | 1 |
| R | 8 | 50 | 2 |
| E | 6 | 120 | 4 |
| L | 4 | 300 | 8 |
| M | 2 | 700 | 16 |

Weapon layer in the power formula (03 section 7):

```
equippedWeaponPct = WEAPON_ATK_PCT[equipped.Rarity] * LevelMult(equipped)            # 0 if nothing equipped
weaponPassivePct  = sum over owned weapons w of WEAPON_PASSIVE_PCT[w.Rarity] * LevelMult(w)   # equipped weapon included
weaponMult        = 1 + (equippedWeaponPct + weaponPassivePct) / 100
```

A weapon's `weapons` table row (09): `id`, `rarity`, `name_key`, `icon`, `atk_pct` (defaults to the rarity value; a row may override for flavour within +/-10%), `passive_pct` (same), `element` (cosmetic in MVP).

### 6.2 Why the passive exists and what it is worth

The equipped pct is a step function of luck: the player's best weapon. The passive is a sum over everything they have ever pulled, so every weapon pull moves it. Worked numbers, by weapon pulls made (which day the F2P bot reaches each row comes from the computed cadence, issue #38: at the 20-seed median of 2.20 weapon pulls/day, 36 pulls is roughly days 15-20 and 94 pulls is past day 30):

| Collection state | equipped | passive | `weaponMult` |
|---|---|---|---|
| Day 1 after the tutorial 10-pull (typical: 6 C, 3 R, 1 E) | 120 (E) | 6 + 6 + 4 = 16 | x2.36 |
| After 36 weapon pulls (all 10 C owned at level 2-3, 6 R, 3 E, 1 L) | 300 (L) | ~24 + 16 + 12 + 8 = 60 | x4.6 |
| After 94 weapon pulls (1 M at level 1 plus 3 limit breaks, full C and R sets at level 3-4) | 700 x 1.6 = 1120 | ~40 + 25 + 20 + 16 + 16 = 117 | x13.4 |
| Complete collection, every weapon level 10 | 700 x 2.8 = 1960 | 319 | x23.8 |
| Complete collection, every weapon level 15 | 700 x 3.8 = 2660 | 433 | x31.9 |

The passive is 10-25% of the weapon layer in the middle rows and it is the part that moves every day. A pull that lands a duplicate level-3 Common still adds 0.2% ATK, which `SCR_SUMMON_RESULT` shows as "+0.2% ATK (collection)". Low rarities also matter because the C and R sets are the bulk of the passive at level 4+: the 10 Commons at level 10 are 28% ATK, more than a level-1 Legendary.

### 6.3 Equip

`EquipWeapon(weaponId)` requires ownership, sets `Loadout.Weapon` (02's `LoadoutState`, issue #7), marks DPS dirty, publishes `ItemEquipped(WEAPON, slot 0, weaponId)`. `AutoEquipBestWeapon()` picks the owned weapon with the highest `WEAPON_ATK_PCT * LevelMult`, ties broken by rarity then `id` ordinal, and is called automatically after every summon result in M0 and MVP (the player can switch manually; the next summon that finds a strictly better weapon switches back and says so in a toast). There is no unequipped state after the tutorial (`Loadout.Weapon` is non-null from then on): the tutorial grants `TUTORIAL_WEAPON_ID` (a fixed Common) before the first pull so `equippedWeaponPct` is never 0.

## 7. Skills

### 7.1 Pool and data

40 skills: C 14, R 11, E 8, L 5, M 2. Each row in `skills` (09):

| Column | Type | Meaning |
|---|---|---|
| `id` | string | `skill_<rarity>_<nn>` |
| `rarity` | C..M | |
| `dps_pct` | double | Rated average DPS as % of hero DPS at level 1; defaults `SKILL_DPS_PCT[r]` = C 10 / R 20 / E 40 / L 80 / M 160 |
| `cast_type` | `INSTANT`, `DOT`, `BUFF` | |
| `cooldown_ticks` | int | 30..300 (3 s to 30 s) |
| `duration_ticks` | int | `DOT` and `BUFF` only; <= `cooldown_ticks` |
| `buff_pct` | double | `BUFF` only: ATK buff while active |
| `name_key`, `icon`, `vfx` | string | presentation |

Derived per-cast numbers, so a skill's rated `dpsPct` is exactly what it delivers on an infinitely long fight. They are multiples of 03's `SkillUnit` (section 7.1: one second of expected hero attack, `HeroHit * critFactor * speedFactor`, equal to `HeroHit` at speed 1 with no crit), which excludes the skill, companion and missile layers so no layer is counted twice (issue #10):

```
INSTANT: damagePctPerCast = dps_pct * cooldown_ticks / ATTACK_INTERVAL_TICKS            # in % of one SkillUnit (03 section 7.1; = 1 s of hero DPS)
DOT:     damagePctPerTick = dps_pct * cooldown_ticks / duration_ticks / ATTACK_INTERVAL_TICKS
BUFF:    dps_pct is informational = buff_pct * duration_ticks / cooldown_ticks           # the buff multiplies hero hits while active
```

Example: `skill_E_03`, E, `dps_pct 40`, `INSTANT`, cooldown 100 ticks (10 s). One cast every 10 s must deliver 40% of 10 s of hero DPS = 4 hero hits' worth of damage, so `damagePctPerCast = 400` (% of one hit). At level 10 the cast does 1,120% of a hit. A `BUFF` skill with `buff_pct 60`, duration 50, cooldown 150 has an informational `dps_pct` of 20.

Mix in the pool: about 60% `INSTANT`, 25% `DOT`, 15% `BUFF`. `BUFF` skills do not stack with each other (03 section 2.3: strongest active applies) so the content validator warns if more than two `BUFF` skills share a rarity.

### 7.2 Slots and auto-cast order

`SKILL_SLOTS = 6`. `EquipSkill(slot, skillId)` requires ownership and that the skill is not in another slot; `UnequipSkill(slot)`; `AutoEquipBestSkills()` fills the six slots with the highest `dps_pct * LevelMult`, ties by rarity then id. Auto-cast order is slot order (03 section 2.3): slot 0 is checked first every tick, so the player's slot arrangement is the priority. `AutoEquipBestSkills` places the highest-rated skill in slot 0. `SCR_SKILLS` offers drag-to-reorder; the sim only sees `EquipSkill` calls.

Skill layer: `skillMult = 1 + sum over equipped of dps_pct * LevelMult / 100`. With the bot's day-30 loadout (2 L at level 1-2, 3 E at level 2-3, 1 R) that is about x5.5.

Manual cast (P1): `CastSkill(slot)` fires a ready skill immediately, bypassing the one-cast-per-tick limit for that tick; cooldown rules unchanged. M0 and MVP have no manual cast; the tap target during a boss is Tap Zap.

## 8. Companions (MVP)

### 8.1 Pool and data

50 companions: C 18, R 14, E 10, L 6, M 2. Each row in `companions` (09):

| Column | Type | Meaning |
|---|---|---|
| `id` | string | `comp_<rarity>_<nn>` |
| `rarity` | C..M | |
| `ability_type` | `DAMAGE`, `BUFF_ATK`, `BUFF_GOLD`, `MP_REGEN` | |
| `dps_pct` | double | Rated contribution at full MP uptime, level 1; defaults `COMPANION_DPS_PCT[r]` = C 16 / R 32 / E 64 / L 128 / M 256 |
| `mp_cost` | int | default 10 |
| `cooldown_ticks` | int | default 100 (10 s) |
| `duration_ticks` | int | `BUFF_*` only |
| `magnitude` | double | type-specific, derived from `dps_pct` as below |

```
DAMAGE:    damagePctPerCast = dps_pct * cooldown_ticks / ATTACK_INTERVAL_TICKS   # like an INSTANT skill, in % of one CompanionUnit (03 section 7.1; SkillUnit x skill factor; issues #10, #243)
BUFF_ATK:  buff_pct = dps_pct * cooldown_ticks / duration_ticks                 # hero ATK buff while active; strongest active wins (shared with skill buffs)
BUFF_GOLD: gold_buff_pct = 2 * dps_pct * cooldown_ticks / duration_ticks        # gold from kills while active; no DPS; rated dps_pct is a value proxy
MP_REGEN:  mp_restored = dps_pct / 4 per cast                                  # e.g. E restores 16 MP for 10 MP spent; dps_pct counts as 0 in the formula
```

Rule (issue #243): companion damage scales with the skill layer, because `CompanionUnit = SkillUnit * (1 + skillDpsPct)` (03 section 7.1); this keeps the rated formula multiplicative and exact. It may be revisited when `power_curve_model.py` reports layer shares.

Mix: 50% `DAMAGE`, 20% `BUFF_ATK`, 15% `BUFF_GOLD`, 15% `MP_REGEN`. `MP_REGEN` companions are what lets a player raise the uptime of the other companions before Research catches up; the bot values them through `mpSufficiency`.

### 8.2 Slots and MP

`COMPANION_SLOTS = 4`; slot 1 is open from the start, slots 2-4 unlock at Awakening ranks 1-3 (03 section 5.3). `EquipCompanion(slot, id)`, `UnequipCompanion(slot)`, `AutoEquipBestCompanions()` as for skills, except that auto-equip reserves one slot for the best `MP_REGEN` companion whenever `mpSufficiency < 1` with the pure-damage loadout.

Companion layer: `companionMult = 1 + mpSufficiency * sum over equipped of dps_pct * LevelMult / 100`, with the inclusive formula (issue #39, shared with 03 section 7):

```
mpSupply     = mpRegenPerSecond + sum over equipped MP_REGEN c of (mp_restored(c) - mp_cost(c)) / cooldownSeconds(c)   # net restored MP counts as regen
mpDemand     = sum over equipped non-MP_REGEN c of mp_cost(c) / cooldownSeconds(c)
mpSufficiency = mpDemand == 0 ? 1 : min(1, mpSupply / mpDemand)
```

MP pool and regen come from Research (03 section 6). The pool is drained at spawn by whichever abilities are ready (one per tick), so a full pool at `MP_POOL0 = 100` fires all four abilities in the first four ticks of a boss fight; the pool's size is burst, the regen is uptime.

## 9. Runes (P1) and other banners

Runes: 36 in the pool (C 12, R 10, E 8, L 4, M 2), `RUNE_SLOTS = 6` unlocked three at Awakening rank 4 and three at rank 5. Each rune carries one `Stat` and a value: `ATK` (the rune adds a percentage to the stat; 09's `runes.stat` is `ref(stats)`, issue #26), `CRIT_RATE`, `CRIT_DMG`, `ATK_SPD` (uncapped by Training caps, capped by its own `RUNE_CAP`), `GOLD_GAIN`, `EXP_GAIN`. `runePct` in 03's formula is the sum of equipped `ATK` runes; the other stats add to their Training counterparts. Same pity, dupe, limit break and banner-level machinery; a fourth `BannerState`. Nothing in MVP code needs to change beyond a new `Banner` enum value and a new collection dictionary, both of which 02's schema reserves. `TICKET_SUMMON_RUNE` is appended to `Currency` after `GUILD_COIN` in schema v1 and stays unused until P1 (issue #26).

Rate-up banners (P1): a child banner with the parent's pool, pity and level, plus a `featured` list that takes `RATE_UP_SHARE = 0.5` of each rarity's weight. Costumes (P2) are not summoned; they are shop items.

## 10. Applying a result in the sim

Whether the result came from the local M0 roll or the server, the sim applies it the same way:

```
ApplySummonResult(SummonResultPayload p):                  # HeroCat.Sim system command (01 section 2.5), the only summon mutator (issue #13)
  if Applied.Contains(p.RequestId): return                   # idempotent against retries (01, 08)
  Local mode:  for d in p.Debits: Wallet.Apply(d, SUMMON)     # the debit happens here, not in LocalSummonService (issue #6)
                 (unaffordable -> Reject(CannotAfford, "NOT_ENOUGH_DIAMOND" / "NOT_ENOUGH_TICKET_SUMMON_<BANNER>"), nothing changes; issue #244)
               Rng[summon_<banner>].Counter = p.StreamCounterAfter
  Remote mode: for d in p.Debits: Wallet.Apply(d, SUMMON); then assert each debited DIAMOND / TICKET_SUMMON_<banner> balance equals the server's;
               on mismatch snap to the server value and publish summon_reconciled (server ledger wins, 00 section 4; issue #49)
  old = BannerLevel(banner.Exp)
  banner.Pity = p.PityAfter; banner.TotalPulls += p.Count; banner.Exp = p.BannerExpAfter
  for r in p.Pulls:
    item = col.Items.GetOrAdd(r.ItemId, FirstAcquiredServerTime: p.ServerTimeSec)
    item.Copies = r.CopiesAfter
    if r.OverflowRockCandy > 0: Wallet.Apply(+r.OverflowRockCandy ROCK_CANDY, SUMMON)
    publish ItemAcquired(banner, r.ItemId, r.Rarity, r.IsNew, r.LevelBefore, r.LevelAfter, r.OverflowRockCandy)
  if p.BannerLevelAfter > old: publish BannerLeveledUp(banner, p.BannerLevelAfter)
  Quests.OnSummon(banner, p.Count)                            # 06
  if banner == WEAPON: AutoEquipBestWeapon()
  MarkDpsDirty(); Applied.Add(p.RequestId)
```

`SummonResultPayload` is 01's (section 2.5); `SummonPull` carries `ItemId`, `Rarity`, `IsNew`, `LevelBefore`, `LevelAfter`, `GuaranteeApplied`, plus `CopiesAfter` and `OverflowRockCandy`, and in Local mode the payload carries `StreamCounterAfter`, the `summon_<banner>` counter after the batch (issues #6, #13). In MVP the server has already debited diamonds or tickets and the payload carries the resulting balances; `Apply` debits the same `Debits` locally and asserts the result equals the server balances, snapping to the server value on mismatch (`Wallet.SummonTicket` is a mirror of the server ledger in Remote mode, issue #7). A mismatch emits a `summon_reconciled` analytics event. The server can only charge earnings it has seen: locally earned diamonds and tickets reach `diamond_ledger` / `ticket_ledger` through the credit step of `PUT /save` (08 section 3.1), so the client flushes a save upload before `POST /summon/pull` whenever it has unsynced earnings (01's summon flow; issue #49). After that flush a mismatch means a bug or tampering, not lost earnings. In M0 the local service checks the wallet before drawing (`NotEnoughDiamonds` / `NotEnoughTickets` before any draw) and `Apply` debits, so replaying the logged `ApplySummonResult` reproduces the state exactly.

## 11. Collection screen data

`SCR_WEAPONS`, `SCR_SKILLS`, `SCR_COMPANIONS` (and `SCR_RUNES` in P1) bind to one view-model shape built from `Query`, never from `GameState` directly (00 section 8 item 8):

```csharp
public sealed record CollectibleVm(
    string Id, Rarity Rarity, string NameKey, string Icon,
    bool Owned, int Copies, int Level, int LimitBreaks, int EffectiveLevel,
    int CopiesToNextLevel,            // 0 at level 10
    int NextLimitBreakCost,           // Rock Candy; 0 when maxed or unowned
    double ValueNow, double ValueNextLevel,   // the kind's headline number: equipped pct / dps_pct / ability magnitude, after LevelMult
    double PassiveNow,                // weapons only
    bool Equipped, int? Slot,
    bool IsNew);                      // set by ItemAcquired(IsNew) until the player opens SCR_ITEM_DETAIL for it; persisted in the local profile/UI store, not in sim state; no sim command clears it (issue #7)

public sealed record CollectionVm(
    Banner Banner, IReadOnlyList<CollectibleVm> Items,
    int OwnedCount, int PoolCount,                     // "23 / 30"
    double LayerMult,                                  // weaponMult / skillMult / companionMult from Query
    double PassiveTotal,                               // weapons: sum of passive pct
    int NewCount,                                      // red-dot badge
    int BannerLevel, int PullsToNextLevel);
```

Sort options (client-side, over the VM): rarity desc then level desc (default), level, newest acquired, name. Filters: owned / all, rarity chips. Locked (unowned) items render as silhouettes with rarity colour and "?" name, so the pool size is always visible, which is what makes "23 / 30" a goal. `SCR_ITEM_DETAIL` adds the per-cast numbers from sections 7.1 and 8.1 and the two buttons `Equip` and `Limit break (N Rock Candy)`.

## 12. `ISummonService`: M0 local roll, MVP server roll

```csharp
namespace HeroCat.Runtime.Summons
{
    // The result is 01's SummonResultPayload (01 section 2.5, extended per section 10); 08's endpoint returns the same shape (issue #13).
    // InFlight and SaveFailed come from 01's SummonFlow, not a service: a summon intent is already pending, or the intent
    // could not be saved before the roll, so nothing was rolled or charged (01 section 8.5.1 step 2; issue #68).
    public enum SummonError { None, NotEnoughDiamonds, NotEnoughTickets, BannerClosed, RateLimited, Network, Replayed, InFlight, SaveFailed }

    public interface ISummonService
    {
        Task<Result<SummonResultPayload, SummonError>> PullAsync(SummonRequest request, CancellationToken ct);
        Task<RateTableDto> GetRateTableAsync(Banner banner, CancellationToken ct);     // published odds for SCR_SUMMON; cached per content version
    }
}
```

**M0: `LocalSummonService`** (`HeroCat.Runtime`). Reads the `GameState` but never writes it (issues #6, #13): it checks the wallet before drawing, clones the banner's `BannerState` and `CollectionState`, runs `HeroCat.Sim.Summons.Pull` on the clones with a reader over the banner's local stream `summon_<banner>` (`summon_weapon`, `summon_skill`; `summon_companion` is created unused) starting at its saved counter, and returns a `SummonResultPayload` carrying the `Debits` and `StreamCounterAfter` in a synchronously-completed task. `ApplySummonResult` then debits, sets the counter and writes the results. The stream is `(seed, counter)` in the save like every other stream (02); its counter invariant is `3 * TotalPulls`. Every save keeps the `summon_*` streams in every mode (00 decision 14); in Remote mode their counters are ignored. The algorithm, pity state and result shape are identical to the server's, so M0 saves carry forward with no re-roll.

**MVP: `RemoteSummonService`**. If there are locally earned diamonds or tickets not yet acknowledged by a save upload, it first forces an immediate `PUT /save` (sync before pull, 01; issue #49); the round trip is hidden by the 800 ms pre-roll. It then POSTs `SummonRequest` to 08's summon endpoint with the auth header and `requestId`; on a network error it retries with backoff (01) using the same `requestId`, and the server replays the stored result (00 section 8 item 13). The UI (07) shows a spinner on the summon button, disables summon-again until the result is applied, and after a timeout shows "Checking your summon..." rather than an error, because the pull may have landed. The client never holds a seed or sees `u1..u3`; it receives the `SummonResultPayload` and applies it (section 10). The server's pity state is authoritative; `ApplySummonResult` overwrites the local `Pity` with `PityAfter`.

**Server plausibility (08)** needs only `TotalPulls`, `Pity` and cumulative `Exp` per banner from the uploaded save, which it compares with its own summon log.

## 13. Analytics events

This section owns these events' fields (issue #245); every event also carries 02 section 14.1's envelope, and 02 section 14.4 indexes them.

| Event | Fields | When |
|---|---|---|
| `summon` | `requestId`, `banner`, `count`, `payment` (DIAMOND/TICKET), `diamondsSpent`, `ticketsSpent`, `pityBefore`, `pityAfter`, `bannerLevelBefore`, `bannerLevelAfter`, `results[]` of `{itemId, rarity, isNew, levelBefore, levelAfter, overflowRockCandy, guaranteeApplied}`, `lplusCount`, `guaranteeApplied` (bool, any slot), `source` (LOCAL/SERVER), `latencyMs` | once per request, after `ApplySummonResult`; one merged schema (issue #13); per-pull audit detail is also in `summon_log` (08) |
| `summon_reconciled` | `requestId`, `banner`, `currency`, `localAfter`, `serverAfter` | Remote mode, when the local balance minus `Debits` differs from the server balance (issue #13) |
| `summon_pending` / `summon_resolved` | `requestId`, `banner`, `count`; resolved adds `latencySec`, `outcome` (replayed/executed/rejected) | Remote mode: when the pending path is entered, and when it closes (01) |
| `item_acquired` | `banner`, `itemId`, `rarity`, `isNew`, `copiesAfter`, `levelBefore`, `levelAfter`, `acquireReason` (SUMMON/MAIL/TUTORIAL), `requestId` (summon or inbox claim; null otherwise) | once per result item (also for non-summon grants) |
| `item_equipped` | `banner`, `slot`, `itemId`, `previousItemId`, `auto` (bool) | on equip |
| `limit_break` | `banner`, `itemId`, `rarity`, `rankAfter`, `rockCandySpent` | on `ItemLimitBroken` |
| `currency_changed` | 06 section 13, reasons `SUMMON`, `LIMIT_BREAK` | per wallet delta |

Dashboards 10 wants from these: L+ frequency per banner vs the published 3.657% (the odds-drift alarm is 10 section 10's: a sample-size-scaled Wilson interval per banner level, runbook `RB-SUMMON-RATES`; issue #59), guarantee fire rate per banner level, pulls per DAU per banner, first-L+ day distribution, limit-break adoption.

## 14. `tools/gacha_model.py`

Copied from `idle-rng-game/tools/gacha_model.py` into `herocat/tools/` and extended. It remains closed-form (no simulation) except for the optional Monte Carlo cross-check.

Changes:

1. Read rates, pity constants, pool sizes, `DUPE_LVL_CUM`, `BANNER_LVL_EXP` and the shift constants from `content/*.json` (09) instead of module constants; `--override` for sweeps as in `power_curve_model.py`.
2. `--banner WEAPON|SKILL|COMPANION|RUNE` selects the pool; the pity math is shared.
3. `rate_table(level)` applies the section 4 shifts and asserts each row sums to 1 within 1e-9; `--levels` prints the ten rows exactly as the `banner_levels` table should contain them, so the sheet can be checked against the tool.
4. Per level: `P(E+)`, `P(guarantee fires) = (1 - P(E+))^9`, expected E+ per 10-pull, expected new items per pull given an owned set (collection fill curve: expected pulls to own all C, all R, all E of the banner at level 1 and at level 10).
5. F2P cadence computed per banner from 06's diamond and ticket income and 03's bot spending policy (read from content; no hand-entered `--pulls-per-day`, issue #38; sweeps change the inputs through `--override`) prints the pulls per day per banner, the day each banner reaches each level, expected days to first L+, to first M, P90 days to first L+, and expected level of a specific C/R/E/L item at days 7, 30, 90.
6. `--vectors` writes `Fixtures/Summon/vectors.json` (section 3.4: the 16 named cases plus 08's row and batch vectors), which the C# sim and server tests only read, so the tests and the tool cannot drift (issues #14, #24). It also checks that its SplitMix64 reproduces the C# stream-sequence fixture in `Fixtures/Summon/`.
7. `--mc N --seed S` runs N pulls on the Python SplitMix64 reference (sibling 03 section 5.4) with the exact `Pull` logic and prints observed C/R/E/L/M frequencies, guarantee fire rate and the L+ gap histogram, for vectors 13 and 14.

It must print, in this order: the pity block (section 3.1 numbers), the soft ramp table, the ten-row rate table with `P(E+)` and guarantee numbers per level, the collection fill curve, the cadence block. `S` constants it validates: `BANNER_EXP_PER_PULL` and `BANNER_LVL_EXP` (the 20-seed median day the F2P bot reaches weapon level 4 is between day 20 and day 35 at the computed cadence; `power_curve_model.py --check` prints it per banner, today day 23.5; issue #38), the shift constants (level 10 `P(E+) <= 0.20` so the paid E+ edge stays under 1.7x), pool sizes (a specific Common reaches level 5 inside 30 days of F2P weapon pulls, pending the tool's numbers: at the current pool size this needs roughly 270 pulls and may be unreachable, open question 8 in section 15; a specific Legendary does not reach level 3 inside 60 days), `COMPANION_SLOTS` and the companion `dps_pct` defaults (together with `power_curve_model.py`, the companion layer passes 5% by day 7), `WEAPON_PASSIVE_PCT` (passive is between 10% and 40% of the weapon layer on days 7 and 30).

## 15. Open questions

1. **Limit breaks on companions.** Resolved: companions are included (00 decision 13).
2. **Tickets before diamonds** on the diamond button. Default: yes, with the button label showing which; 07 may prefer two buttons.
3. **Rate-up banners share pity with the parent** (industry norm for standard pools, not for limited banners). Owner: 06/08 for P1 events. Default: shared.
4. **Auto-equip after summon** for skills and companions too, or weapons only? Default: weapons only; skills and companions show a "better skill available" badge, since slot order is a player choice.
5. **Tutorial grants**: the starter weapon grant is resolved: 09's `tut_welcome` step grant gives `TUTORIAL_WEAPON_ID` (with `TUTORIAL_DIAMONDS`) at profile creation, before the first pull (issue #46). Still open: whether the 1,500 tutorial diamonds are pre-spent as a scripted weapon 10-pull (fixed results for a controlled first minute) or rolled. Owner: 07. Default: rolled on the real algorithm, with the 10-pull guarantee making an Epic certain.
6. **Soft-pity disclosure**: show the exact ramp or "rates increase after 50 pulls". Default: show the ramp on the rate table screen; it is friendlier and simplifies the Japan and Belgium notes in 08.
7. **`DUPE_OVERFLOW_ROCK_CANDY` values** are placeholders; nobody reaches 160 copies in the 30-day window, so the tool cannot validate them. Default: keep until live data.
8. **Common level-5 target (pending tool).** Section 14's "a specific Common reaches level 5 inside 30 days of F2P weapon pulls" looks unreachable at the current Common pool size (about 270 pulls). Not decided: `gacha_model.py` reports the real day, then the target or the pool size is set from it.

## 16. Acceptance criteria

1. All 16 test vectors in section 3.4 pass in `HeroCat.Sim.Tests` and `HeroCat.Server.Tests` against the same `Fixtures/Summon/vectors.json` produced by `gacha_model.py --vectors`, and Python's SplitMix64 reproduces the C# stream-sequence fixture (issues #14, #24).
2. Every row of the `banner_levels` table sums to 1.0 within 1e-9; L and M columns are constant across rows; the content validator fails otherwise.
3. `PityRate` is monotone non-decreasing in `n` for every banner; `BannerState.Pity` is always in `0..PITY_HARD-1` after any pull.
4. A `Pull10` whose first nine results are below E yields an E+ in slot 10; a `Pull10` with an E+ in slots 1-9 rolls slot 10 normally; across 100,000 scripted 10-pulls at level 1 the guarantee fires in 31.6% +/- 0.5 pp.
5. Levels: copies 1, 3, 6, 10, 16, 25, 40, 64, 100, 160 give levels 1..10; `LevelMult` at level 10 is 2.8 and at level 10 with 5 limit breaks is 3.8.
6. `LimitBreak` is rejected for an unowned item, at 5 ranks, and with insufficient Rock Candy (nothing charged); otherwise charges `LIMIT_BREAK_COST[r] * (rank + 1)` with reason `LIMIT_BREAK` and raises the item's contribution by exactly 20% of its level-1 value.
7. Copy 161 of any item adds `DUPE_OVERFLOW_ROCK_CANDY[r]` Rock Candy with reason `SUMMON` and leaves `Copies == 160`.
8. `weaponMult` for a fixture (equipped L at level 3, owned 10 C at level 2, 4 R at level 1) equals `1 + (300 * 1.4 + 8 * 1.4 + 10 * 1 * 1.2 + 4 * 2 * 1.0) / 100 = 5.512` within 1e-12 (the passive sum includes the equipped L, section 6.1); unequipping is impossible (there is no unequip command for weapons) and `AutoEquipBestWeapon` after pulling an M switches the equipped weapon and publishes `ItemEquipped(..., auto: true)`.
9. A level-1 Epic `INSTANT` skill with cooldown 100 deals exactly `4.0 * SkillUnit` per cast (`== 4.0 * HeroHit` at speed 1, no crit; issue #10), fires on the first `Fighting` tick after its cooldown expires, and over a 600-tick fight with one enemy deals `40%` of hero damage within 1% (hero at speed 1, no crit).
10. Two `BUFF` effects active at once apply only the stronger; the weaker's expiry does not end the stronger.
11. With `MP_REGEN0 = 2`, four `DAMAGE` companions at cost 10 / cooldown 100 fire 12 +/- 1 abilities per minute; replacing one with an E `MP_REGEN` companion (restores 16 per cast) raises the other three to 15.6 +/- 1 abilities per minute in total (supply 120 + 36 MP a minute at 10 MP per cast; issue #39), matching `mpSufficiency` from section 8.2 within 1%.
12. `ApplySummonResult` with a repeated `requestId` changes nothing; with a new one debits exactly the payload's `Debits` once (Local mode) or replaces the balances with the server's (Remote mode), updates pity and banner EXP to the payload's values, publishes one `ItemAcquired` per pull, `BannerLeveledUp` when the level rose, and one `summon` analytics event.
13. `LocalSummonService` never mutates `GameState` (state hash unchanged after `PullAsync`); over 1,000 pulls applied through `ApplySummonResult`, `summon_weapon.Counter` advances by exactly 3,000; a save/load round-trip between pulls 500 and 501 yields the same pull 501; replaying the command log reproduces the state hash (issue #6).
14. `RemoteSummonService` retrying a timed-out request with the same `requestId` receives the original result and applies it once (08's replay test with a stubbed server).
15. `gacha_model.py --mc 1000000 --seed 7` reports L+ within 0.1 pp of 3.657% and, with `--level 10`, C/R/E within 0.3 pp of 0.42/0.388/0.162 of non-L+ results.
16. `CollectionVm.OwnedCount / PoolCount` equals the number of distinct owned ids over the pool size for a fixture; `NewCount` drops to zero after `SCR_ITEM_DETAIL` is opened for each new item (07 PlayMode test).

## 17. Constants introduced

| Name | Value | Kind | Section | Note |
|---|---|---|---|---|
| `BANNER_LVL_EXP` | 0, 10, 25, 55, 90, 145, 215, 305, 415, 540 | S | 4 | Cumulative pulls for levels 1..10. Tuned from the computed cadence: 20-seed median weapon level 4 on day 27 at the flat 400 diamonds a day, day 23.5 under 06's diamond rules (issue #178), inside days 20-35 (issue #38). |
| `BANNER_LVL_C_SHIFT`, `BANNER_LVL_R_SHIFT`, `BANNER_LVL_E_SHIFT` | 0.020, 0.012, 0.008 per level | S | 4 | Generate the `banner_levels` rows; L/M fixed. |
| `LIMIT_BREAK_RANKS` | 5 | S | 5.2 | Per item; 00 section 6.5 name, 09 `constants` (issue #37). |
| `LIMIT_BREAK_COST[r]` | C 10 / R 20 / E 40 / L 80 / M 150, times rank (placeholder) | S | 5.2 | Rock Candy; column `rarities.limit_break_cost` (issue #37). Validated by `power_curve_model.py`; repricing pending the tool (issue #36). |
| `DUPE_OVERFLOW_ROCK_CANDY[r]` | C 1 / R 2 / E 5 / L 20 / M 50 | S | 5.3 | Copy 161+; column `rarities.dupe_overflow_rock_candy` (issue #37). |
| Weapon pool split | C 10 / R 8 / E 6 / L 4 / M 2 | S | 6.1 | Sums to 30. |
| `WEAPON_ATK_PCT[r]` | C 20 / R 50 / E 120 / L 300 / M 700 | S | 6.1 | Equipped weapon ATK% at level 1. |
| `TUTORIAL_WEAPON_ID` | a fixed Common | - | 6.3 | Granted before the first pull. |
| Skill pool split | C 14 / R 11 / E 8 / L 5 / M 2 | S | 7.1 | Sums to 40. |
| `SKILL_DPS_PCT[r]` | C 10 / R 20 / E 40 / L 80 / M 160 | I | 7.1 | Same values as the sibling's skill table. |
| Skill cast-type mix | 60% INSTANT / 25% DOT / 15% BUFF | S | 7.1 | Content guideline, validated by the content validator as a warning. |
| Companion pool split | C 18 / R 14 / E 10 / L 6 / M 2 | S | 8.1 | Sums to 50. |
| `COMPANION_DPS_PCT[r]` | C 16 / R 32 / E 64 / L 128 / M 256 | S | 8.1 | Rated at full MP uptime. |
| Companion defaults | `mp_cost 10`, `cooldown_ticks 100` | S | 8.1 | |
| Companion ability mix | 50% DAMAGE / 20% BUFF_ATK / 15% BUFF_GOLD / 15% MP_REGEN | S | 8.1 | |
| Rune pool split (P1) | C 12 / R 10 / E 8 / L 4 / M 2 | S | 9 | Sums to 36. |
| `RATE_UP_SHARE` (P1) | 0.5 | S | 9 | |
| `summon_weapon`, `summon_skill`, `summon_companion` streams | drawn in Local mode only | - | 12 | Kept in every save (00 decision 14); counters ignored in Remote mode. |
| F2P pulls per day | computed per banner by `gacha_model.py` | derived from 06 and 03 | 14 | Not hand-entered and not a game constant (issue #38). |
