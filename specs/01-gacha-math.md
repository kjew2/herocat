# 01 - Gacha Math

Scope: the P0 hero banner. Pet banner (P1) and rate-up banners (P1) reuse every mechanism here with a different table.
Numbers come from `tools/gacha_model.py`; rerun it after any change and paste the new output into section 3.

## 1. Design goals

1. A free player (6 pulls/day) sees a Legendary-or-better about every 4-5 days and never waits more than ~2 weeks.
2. The displayed rates are honest and the "consolidated rate" (what players actually experience with pity) can be shown next to them.
3. No result depends on anything the client controls. Reloading a save, changing the device clock, or killing the app mid-pull cannot change what the next pull gives.
4. Dupes are never dead: every copy either levels the hero or becomes shards.
5. Pulls are characters the player can get attached to: every hero has a name, a portrait and a visible place in the fight, so a new Legendary is someone joining the party, not a number going up.

## 2. Rate table

| Tier | Code | Base rate | Pool size | Base DPS% |
|---|---|---|---|---|
| Common | C | 0.600 | 20 | 10 |
| Rare | R | 0.280 | 15 | 20 |
| Epic | E | 0.090 | 10 | 40 |
| Legendary | L | 0.025 | 6 | 80 |
| Mythic | M | 0.005 | 3 | 160 |

`L+` means L or M. Base `P(L+) = 0.03`. Within an L+ result, `P(M | L+) = 1/6`.
Within a tier, the specific hero is uniform over the pool (no per-hero weights in P0).

## 3. Pity

### 3.1 Rule

Let `n` be the number of pulls since the last L+ result on this banner, counting the current pull (so the first pull after an L+ has `n = 1`).

```
P_Lplus(n) = 1.0                                   if n >= 80          (hard pity)
           = min(1.0, 0.03 + 0.0325 * (n - 50))    if 50 < n < 80      (soft pity ramp)
           = 0.03                                   if n <= 50          (base)
```

When the roll is *not* L+, the result tier is drawn from the non-L+ distribution renormalised: C 60/97, R 28/97, E 9/97.
When the roll *is* L+, M with probability 1/6 else L. The pity counter resets to 0 on any L+, including forced ones from the 10-pull guarantee.

### 3.2 Exact distribution (output of `tools/gacha_model.py`)

```
base L+ rate            : 0.0300  (1 in 33.3)
expected pulls per L+   : 27.35
consolidated L+ rate    : 3.6567%
consolidated M rate     : 0.6094%  (expected 164 pulls per Mythic)
P50/P90/P99 pulls to L+ : 23 / 56 / 62
P(no L+ in first 50)    : 0.218
P(hit hard pity @80)    : 0.0000

soft pity ramp (pull -> L+ rate):
   50: 0.0300    51: 0.0625    55: 0.1925    60: 0.3550
   65: 0.5175    70: 0.6800    75: 0.8425    79: 0.9725    80: 1.0000

3 pulls/day -> L+ every 9.1 days, worst case 26.7 days
6 pulls/day -> L+ every 4.6 days, worst case 13.3 days
9 pulls/day -> L+ every 3.0 days, worst case 8.9 days
```

Reading: 78% of players get their L+ inside the base-rate window; the remaining 22% are caught by the soft ramp and are essentially all done by pull 62. The hard pity at 80 exists for the rate-disclosure text and as a safety net if the ramp constants are ever lowered; it is not where the experience is tuned.

### 3.3 Derivation

With `q_n = P_Lplus(n)` and survival `S_n = prod_{k<n} (1 - q_k)`, the probability that the first L+ lands exactly on pull `n` is `f(n) = S_n * q_n`, and `E[pulls] = sum n * f(n)`. The consolidated rate is `1 / E[pulls]`. There is no simulation involved; this is what should be shown in the in-game rate screen.

## 4. 10-pull guarantee

A 10-pull executes 10 ordinary pulls in sequence (each advancing the pity counter and consuming one RNG draw as in section 6). After slot 9, if none of slots 1-9 is E+, slot 10 draws its tier from the E+ sub-distribution renormalised: E 0.09/0.12, L 0.025/0.12, M 0.005/0.12. The specific hero is then rolled as usual. If slot 10 lands L+ this way it resets pity like any other L+.

Effect (from the model): `P(guarantee fires) = 0.88^9 = 0.316`; expected E+ per 10-pull rises from 1.20 to 1.48. The guarantee never *reduces* anyone's odds; it only upgrades slot 10 in the bad case.

Pity ramp and the guarantee interact only through the counter. Slot 10's forced E+ draw still rolls L+ at `P_Lplus(n)` first; if that fails, it draws from {E} only. Implementation: in `RollTier(n, forceEplus)`, do the L+ check first, then if `forceEplus` return E, else draw from the C/R/E renormalised table.

## 5. Currency and cadence

| Source | Gems/day | Notes |
|---|---|---|
| Daily login | 50 | |
| Daily quests (3 x 30) | 90 | Kill 500 mobs, clear 1 boss, do 3 pulls (free pulls count). |
| First-clear bonus, boss stages | ~60 | 10 gems per new boss stage cleared; ~6/day in the mid game. |
| Idle chest (every 4 h active or offline) | 100 | 2 chests/day realistically. |
| **Total F2P** | **~300** | = 3 paid pulls, plus 3 free pulls = 6 pulls/day. |

Costs: 100 gems per pull, 1000 per 10-pull (no discount in P0; a 10-pull is a convenience plus the E+ guarantee). A new player receives 1000 gems and 10 free pulls in the tutorial so their first 10-pull happens in the first five minutes.

## 6. RNG commitment and anti-save-scum

The gacha stream is `gacha_hero` (see `specs/03` for the generator). Its state is `(seed, counter)`:

- `seed` is a 64-bit value assigned once when the profile is created. Prototype: derived from the profile GUID. Live: issued by the server and **never sent to the client in a form the client can use to peek ahead** (see `specs/08`; the server returns results, the client only displays them).
- `counter` is the total number of pulls ever made on this banner. It is stored in the save.
- Result of pull `k` = `Derive(seed, counter=k)`. Pull `k` always yields the same result for the same profile. Restoring an older save restores an older `counter`, so the player simply re-receives the same results they already got.

Each pull consumes exactly three draws from the stream, in this order, so results are reproducible and auditable:
1. `u1` -> L+ check against `P_Lplus(n)`.
2. `u2` -> tier within the branch (M vs L, or C/R/E renormalised, or forced E).
3. `u3` -> hero index within the tier's pool.

Draws are consumed even when their outcome is predetermined (e.g. `u1` at hard pity) so the counter arithmetic stays `3 * pulls`.

## 7. Dupe economy

A hero has `copies` (lifetime) and derived `level`:

```
HERO_LVL_CUM = [1, 3, 6, 10, 16, 25, 40, 64, 100, 160]
level(copies) = count of thresholds <= copies          # 1..10
heroPct(tier, level) = basePct[tier] * (1 + 0.20 * (level - 1))
```

Level 10 is 2.8x base. A level-10 Common (28%) is worth less than a level-1 Epic (40%), so collection matters, but a Common you pull 160 times is not nothing. Expected time to level a specific L hero to 5 (16 copies): 16 copies * 6 L heroes in pool / (6 pulls/day * 0.0366 * 5/6) ~ 520 days. That is intentional: L/M leveling is the long tail, C/R/E leveling is the daily visible progress.

Copies past 160 convert to shards at pull time (P1 shard shop: buy a chosen E for 250 shards, L for 1500). In P0 the shard counter is tracked but nothing spends it.

Party: `HERO_SLOTS = 6`. The client offers "auto-equip best six" (sort by `heroPct`). `heroMult = 1 + sum(party heroPct) / 100`.

### 7.1 What a hero is in P0

A hero is a content row (`specs/05` section 5.7) with a tier, a name, a portrait and a base DPS%. In P0 its only mechanical effect is the passive `heroPct` while it is in the party. On the combat screen the six party members stand behind the player as portraits and play an attack flash on each player hit (`specs/07` section 11.1). That animation is cosmetic: their damage is already inside `heroMult`, so the sim, the bot and the power curve in `specs/02` do not change.

Why passive only: it keeps the gacha a single multiplicative layer, which is what the power curve and the bot were tuned against. Anything that gives heroes their own behaviour (below) needs a bot rerun before it ships.

P1 ideas, not commitments:
- **Roles.** Each hero gets a role (striker, support, treasure hunter) that adds a small second bonus, for example boss damage, gold per kill or gear drop chance. Needs a `role` column and per-role caps so one role cannot dominate.
- **Active abilities.** Legendary and Mythic heroes get an ultimate on a cooldown. This is the part of skill gacha worth keeping, attached to a character instead of a floating icon.
- **Party synergy.** Small bonus for fielding several heroes of the same faction. Adds collection goals beyond raw tier.

## 8. Algorithms

```csharp
// All inputs come from ContentDb; nothing here is a literal in production code.
public PullResult Pull(BannerDef banner, BannerState st, Currencies cur, RngStream rng, bool forceEplus)
{
    st.PityCounter += 1;
    st.TotalPulls  += 1;
    double u1 = rng.NextDouble(), u2 = rng.NextDouble(), u3 = rng.NextDouble();

    Tier tier;
    bool isLplus = u1 < PityRate(banner, st.PityCounter);
    if (isLplus) {
        tier = u2 < banner.MythicShareOfLplus ? Tier.M : Tier.L;
        st.PityCounter = 0;
    } else if (forceEplus) {
        tier = Tier.E;
    } else {
        tier = SampleRenormalised(banner.BaseRates, exclude: {L, M}, u2);
    }
    int idx = (int)(u3 * banner.Pool[tier].Count);
    var heroId = banner.Pool[tier][idx];

    var owned = st.Owned.GetOrAdd(heroId);
    int before = Level(owned.Copies);
    owned.Copies += 1;
    int after = Level(owned.Copies);
    if (owned.Copies > HERO_LVL_CUM.Last()) cur.Shards += ShardValue[tier];

    return new PullResult(heroId, tier, isNew: owned.Copies == 1, levelBefore: before, levelAfter: after);
}

public PullResult[] Pull10(BannerDef b, BannerState st, Currencies cur, RngStream rng)
{
    var r = new PullResult[10];
    for (int i = 0; i < 9; i++) r[i] = Pull(b, st, cur, rng, forceEplus: false);
    bool anyEplus = r.Take(9).Any(x => x.Tier >= Tier.E);
    r[9] = Pull(b, st, cur, rng, forceEplus: !anyEplus);
    return r;
}

static double PityRate(BannerDef b, int n) =>
    n >= b.PityHard ? 1.0 : Math.Min(1.0, b.BaseLplus + b.PitySoftStep * Math.Max(0, n - b.PitySoftStart));
```

## 9. Test vectors

Unit tests in `HeroCat.Sim.Tests/GachaTests.cs` must cover:

1. **Rate sum**: `banner.BaseRates` sums to 1.0 within 1e-9; content loader rejects otherwise.
2. **Pity ramp table**: `PityRate(n)` for n in {1, 50, 51, 55, 60, 65, 70, 75, 79, 80, 81} equals section 3.2 exactly.
3. **Hard pity**: with a stream stubbed to return `u1 = 0.999`, pull 80 is L+ and resets the counter.
4. **Soft pity monotone**: `PityRate(n+1) >= PityRate(n)` for all n.
5. **10-pull guarantee**: stub `u1 = 0.99, u2 = 0.0` for 10 pulls; slots 1-9 are C, slot 10 is E.
6. **10-pull does not downgrade**: stub so slot 3 is E; slot 10 then rolls normally (C under the same stubs).
7. **Reproducibility**: two `BannerState`s with the same seed and counter yield identical 1000-pull sequences; after save/load round-trip the next pull equals the one from the un-restored copy.
8. **Draw count**: 1000 pulls advance the stream counter by exactly 3000.
9. **Consolidated rate**: Monte Carlo 1,000,000 pulls on the real generator yields L+ frequency within 0.1 pp of 3.657% (statistical test with fixed seed, so it is deterministic).
10. **Dupe levels**: copies 1,2,3,6,10,160,161 give levels 1,1,2,3,4,10,10 and copy 161 adds `ShardValue[tier]` shards.

## 10. Tuning levers

| Want | Move |
|---|---|
| L+ a bit rarer without touching the worst case | lower base L+ to 0.025 and leave the ramp. E[pulls] rises to ~30. |
| Shorter worst case | start the ramp at 40. Check that the consolidated rate stays <= the displayed base + ramp text. |
| Mythic scarcer | lower `M_SHARE_OF_LPLUS` to 1/8. Does not affect the L+ pity. |
| Faster C/R leveling | shrink the pool, not the thresholds. Pool size is the dupe rate. |
| Monetisation pressure | do not touch rates. Adjust gem income (`specs/04`) or add a paid 10-pull discount. Rate changes after launch read as a bait-and-switch. |

## 11. Open decisions (not blocking P0)

- Whether P1 hero roles (section 7.1) are worth the extra tuning surface, or whether heroes stay pure passive multipliers with richer art.

- Whether pity carries across banners of the same type (industry norm: per banner for rate-ups, shared for standard). P0 has one banner so it is moot.
- Whether to show the exact soft-pity ramp or just "rates increase after 50 pulls". Showing the ramp is friendlier and legally safer; it is what the rate screen in `specs/07` assumes.
