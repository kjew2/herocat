# 04 - Economy: Sources and Sinks

Scope: every currency source and sink in the prototype except the gacha pull itself (`specs/01`). This spec formalises the spend policy and the gear and enhancement rules that `tools/power_curve_model.py` (the bot) plays by, so the C# bot in `specs/09` and the Python model agree on what a player can do with gold and gems.

Constants are canonical in `specs/00` section 2 and are referred to by name. Where this spec needs a value that `specs/00` or `specs/01` gives without a name, section 2 below assigns a name and lists it for promotion into `specs/00`. All of them live in `ContentDb` (`specs/05`); code never holds a literal.

## 1. Design goals

1. Gold has exactly two sinks in P0, stat levels and enhancement. The player should feel the choice between them (02 section 1.4), and the bot's greedy policy (section 9) is the reference for what that choice yields.
2. Every rule is a pure function of (sim state, `ContentDb`, one named RNG stream). The only clock is `IClock` (server time). Nothing reads device time.
3. The headless bot and the Unity client call the same sim methods. Any action a button performs is one `IEconomy` call.
4. No currency can be obtained faster by behaving strangely (selling gear, re-clearing content, changing the clock, reloading a save) than by playing forward. Section 12 lists the checks.

## 2. Names used in this spec

| Name | Defined in | Meaning |
|---|---|---|
| `GOLD(s)` | 00 section 2.2 | `GOLD0 * GOLD_G^s`, gold per normal kill at stage `s`. |
| `GEAR_TIER(s)` | 00 section 2.3 | `floor(s/10)*10`. |
| `GEAR_RARITY_W[r]` | 00 section 2.3 "Rarity weights" row | Weight of rarity `r` in the drop table. Named here. |
| `RARITY_MULT[r]` | 00 section 2.3 "Rarity mult" row | Multiplier on a piece's ATK%. Named here. |
| `GEAR_BOSS_DROP_P` | 00 section 2.3, notes on `GEAR_DROP_P` | Per-kill drop chance on a boss kill. Named here. |
| `GEAR_BOSS_MIN_RARITY` | 00 section 2.3, notes on `GEAR_DROP_P` | Lowest rarity a boss drop can be (Rare). Named here. |
| `ENH_COST(s)`, `ENH_SUCCESS(l)`, `ENH_MAX`, `ENH_PER_LVL` | 00 section 2.3 | As defined there. |
| `GEAR_SELL_KILLS` | new, this spec, value 5 | Sell value of a piece in units of kills of gold at the piece's tier (section 5.5). |
| `STAT_BULK_STEPS` | new, this spec, value {1, 10, 100} | The fixed bulk-buy button sizes. Max is a separate button. |
| `DAY_SECONDS` | new, this spec, value 86400 | Length of a day key. Rollover is at 00:00 UTC server time. |
| `LOGIN_GEMS` | 01 section 5 (50) | Daily login reward. |
| `QUEST_GEMS` | 01 section 5 (30) | Reward per daily quest. |
| `QUEST_KILLS_TARGET` | 01 section 5 (500) | Daily quest 1. |
| `QUEST_BOSS_TARGET` | 01 section 5 (1) | Daily quest 2. |
| `QUEST_PULLS_TARGET` | 01 section 5 (3) | Daily quest 3. Free pulls count. |
| `FIRST_CLEAR_GEMS` | 01 section 5 (10) | Per boss stage cleared for the first time. |
| `IDLE_CHEST_HOURS` | 01 section 5 (4) | Timer length for one chest. |
| `IDLE_CHEST_GEMS` | 01 section 5 (50 per chest; the table's 100/day is two chests) | Gems per chest. |
| `TUTORIAL_GEMS` | 01 section 5 (1000) | One-time grant at profile creation. |
| `TUTORIAL_FREE_PULLS` | 01 section 5 (10) | One-time grant of banked free-pull credits. |

The values in parentheses are the ones already published in `specs/00` and `specs/01`; they are repeated here only to pin the mapping. If `specs/00` later adopts different names, this spec follows.

## 3. Currencies

| Currency | Type | Tag | Sources | Sinks | Notes |
|---|---|---|---|---|---|
| Gold | `double` | P0 | Normal kill: `GOLD(s)`. Boss kill: `BOSS_GOLD_MULT * GOLD(s)`. Offline catch-up: `OFFLINE_RATE`, `OFFLINE_CAP_H` (formula in 02 section 7, implemented in `specs/06`). Gear sell (section 5.5). | ATK stat levels (section 4). Enhancement attempts (section 5.6). | P1 sink: substat reroll (section 11.1). `double` per 02 section 2. |
| Gems | `long` | P0 | Daily login, three daily quests, first clears, idle chest (section 7). Tutorial grant (section 8). | Pulls (`specs/01`). | P1 sources: IAP, rewarded ads. P1 sinks: pet banner, rate-up banner. |
| Free pull credits (daily) | `int` | P0 | Reset to `FREE_PULLS_PER_DAY` at rollover (section 7.1). | Single pulls (section 7.5). | Unused credits expire at rollover. |
| Free pull credits (banked) | `int` | P0 | Tutorial grant (section 8). | Single pulls or one 10-pull (section 7.5). | Never expire. P1: event grants. |
| Shards | `long` | P0 tracked, P1 spent | Excess hero copies (01 section 7). | Shard shop (section 11.3). | Nothing spends shards in P0; the counter is saved so P1 players keep what they earned. |
| Enhancement materials | `long` | P1 | Daily dungeon (section 11.2). | Enhancement attempts, replacing gold. | Field exists in the P0 save at 0 so the P1 migration is a no-op. |
| Dungeon tickets | `int` | P1 | Daily reset. | Dungeon entry. | Same `DailyState` reset path as quests. |

```csharp
// HeroCat.Sim. Mutable state containers are records with settable properties so
// tests can compare whole states by value. Serialised by specs/06.
public sealed record Currencies
{
    public double Gold { get; set; }
    public long Gems { get; set; }
    public int FreePullsDaily { get; set; }      // reset to FREE_PULLS_PER_DAY at rollover
    public int FreePullsBanked { get; set; }     // tutorial grant; never expires
    public long Shards { get; set; }             // written by specs/01 Pull(); P1 spends it
    public long EnhMaterials { get; set; }       // P1. Always 0 in P0.
    public int DungeonTickets { get; set; }      // P1. Always 0 in P0.
}
```

`specs/01` section 8 writes `st.Shards` on the banner state. In the sim that field is `Currencies.Shards`; the banner state holds a reference to the profile's `Currencies`. One counter, one home.

## 4. Gold sink: ATK stat levels

### 4.1 Formulas

`n` is the current stat level (`ProgressionState.StatLevel`), starting at 0.

```
StatCost(n)        = STAT_COST0 * STAT_COST_G^n                      // level n -> n+1
BulkCost(n, m)     = STAT_COST0 * STAT_COST_G^n * (STAT_COST_G^m - 1) / (STAT_COST_G - 1)
                                                                      // levels n -> n+m, closed form
StatMult(n)        = 1 + STAT_PER_LVL * n
DeltaPct(n, m)     = STAT_PER_LVL * m / (1 + STAT_PER_LVL * n)       // relative DPS gain of buying m levels
```

`MaxAffordable(n, gold)`:

```
if gold < StatCost(n): return 0
m = floor( ln(1 + gold * (STAT_COST_G - 1) / (STAT_COST0 * STAT_COST_G^n)) / ln(STAT_COST_G) )
while BulkCost(n, m + 1) <= gold: m += 1        // floating-point fix-up, at most one or two iterations
while m > 0 and BulkCost(n, m) > gold: m -= 1
return m
```

The quoted price and the charged price are the same `BulkCost` number. Tests compare the closed form against a loop sum with a relative tolerance of 1e-9; the sim never uses the loop.

### 4.2 Buttons

Four buttons: one per entry in `STAT_BULK_STEPS` (x1, x10, x100) plus Max.

- x1, x10, x100 are all-or-nothing. The button shows `BulkCost(n, m)` and `DeltaPct(n, m)`, and is disabled when `Gold < BulkCost(n, m)`. Pressing it calls `BuyStatLevels(m)`; the call returns `false` and changes nothing if the player cannot afford all `m`.
- Max shows `m = MaxAffordable(n, Gold)`, its cost and delta, and is disabled when `m == 0`. Pressing it calls `BuyStatLevels(m)` with that `m`. The bot's greedy loop ("buy while affordable") is exactly the Max button.

`BuyStatLevels(m)` with `m <= 0` is a no-op returning `false`.

### 4.3 Display

Next to the current level the panel shows, for the selected button:

```
ATK x{StatMult(n)} -> x{StatMult(n+m)}      +{DeltaPct(n, m) as percent} DPS
Cost {BulkCost(n, m)}                        (you have {Gold})
```

All gold values go through the K/M/B formatter from 02 section 2. The delta is relative to current DPS, so it shrinks as `n` grows; that is the intended feel of a log-growth layer.

## 5. Gear

### 5.1 Slots and piece data

```csharp
public enum GearSlot : byte { Weapon = 0, Helm = 1, Chest = 2, Gloves = 3, Boots = 4, Ring = 5 }  // GEAR_SLOTS entries
public enum Rarity   : byte { C = 0, R = 1, E = 2, L = 3, M = 4 }

// Immutable. BasePct is derived from (Tier, Rarity) through ContentDb and is never stored,
// so retuning GEAR_SLOT_G or RARITY_MULT reflows every saved piece without a migration.
public sealed record GearPiece(GearSlot Slot, Rarity Rarity, int Tier, int EnhLevel)
{
    // P1: ImmutableArray<Substat> Substats (empty in P0 saves), bool Locked (false in P0 saves).
}

public sealed record GearState
{
    public GearPiece?[] Equipped { get; init; } = new GearPiece?[6];   // index == (int)GearSlot; null == empty
    // P1: GearPiece?[] Pending, one held candidate per slot (section 11.1).
}
```

```
BasePct(p)   = (GEAR_SLOT_G^p.Tier - 1) * RARITY_MULT[p.Rarity]
EffPct(p)    = BasePct(p) * (1 + ENH_PER_LVL * p.EnhLevel)
SlotMult(s)  = Equipped[s] is null ? 1 : 1 + EffPct(Equipped[s])
GearMult     = prod over slots of SlotMult(s)
```

`Tier` is always a multiple of 10. Tier 0 pieces (dropped on stages 1 to 9) have `BasePct == 0` and are valid; they equip into an empty slot and contribute nothing.

### 5.2 Drop roll procedure

Called once per kill by the combat sim (`specs/03`) through `IEconomy.OnKill`, after the kill is final. A failed boss attempt (timeout) is not a kill and reaches none of this.

```
OnKill(stage, isBoss):
  1. Gold += GOLD(stage) * (isBoss ? BOSS_GOLD_MULT : 1)
  2. Daily counters: Kills += 1; check quests (section 7.2). (`BossAttempts` is incremented when a boss fight STARTS, automatic or via `AttemptBoss`, not on the kill, so a walled player can still complete the quest; see 00 section 7.)
  3. if isBoss: FirstClear(stage) (section 7.3)
  4. u_drop = gear_drop.NextDouble()                        // exactly one draw per kill, always
     p      = isBoss ? GEAR_BOSS_DROP_P : GEAR_DROP_P
     if u_drop >= p: return                                 // strict less-than means drop
  5. u_rar  = gear_rarity.NextDouble()
     u_slot = gear_rarity.NextDouble()                      // exactly two draws per drop, always
     rarity = isBoss ? Sample(GEAR_RARITY_W, u_rar, min: GEAR_BOSS_MIN_RARITY)
                     : Sample(GEAR_RARITY_W, u_rar, min: Rarity.C)
     slot   = (GearSlot)(int)(u_slot * GEAR_SLOTS)          // uniform over the six slots
     piece  = new GearPiece(slot, rarity, GEAR_TIER(stage), EnhLevel: 0)
  6. Receive(piece) (section 5.4)
```

`stage` is the stage the kill happened on. A walled player farms stage `s` mobs and drops tier `GEAR_TIER(s)` gear (02 section 3).

```csharp
// Walks the cumulative weights of rarities >= min, renormalised over that range.
// With min == C the total is 1.0 (the content loader rejects a table that does not sum to 1 within 1e-9).
static Rarity Sample(double[] w, double u, Rarity min)
{
    double total = 0;
    for (var r = min; r <= Rarity.M; r++) total += w[(int)r];
    double acc = 0;
    for (var r = min; r <= Rarity.M; r++) { acc += w[(int)r] / total; if (u < acc) return r; }
    return Rarity.M;   // guards u rounding against acc summing to 0.9999999
}
```

Stream accounting invariants, checked by tests: `gear_drop.Counter == lifetime kills`; `gear_rarity.Counter == 2 * lifetime drops`. Offline catch-up (`specs/06`) grants gold only; it does not simulate kills, so it advances neither stream and does not count toward quests.

### 5.3 Boss drop rule

A boss kill uses `GEAR_BOSS_DROP_P` in step 4 and `GEAR_BOSS_MIN_RARITY` in step 5. "At least Rare" is implemented by renormalising `GEAR_RARITY_W` over R, E, L, M, the same mechanism `specs/01` section 4 uses for the 10-pull guarantee. The alternative, rolling the full table and bumping C to R, keeps E/L/M at their normal rates; renormalising raises them in proportion. The two differ by less than one Epic-or-better piece per hundred boss kills, and the renormalised form reuses one `Sample` helper with one test. If playtests find boss drops too generous, switch `Sample`'s `min` argument for a bump and nothing else changes.

The Python bot does not model either boss rule: it rolls `GEAR_DROP_P` and the full table on every kill including bosses. The real sim is therefore slightly more generous, about one extra Rare-or-better piece per ten boss kills. `specs/09` should expect the C# bot to run a little ahead of the Python model on gear because of this.

### 5.4 Receiving a piece: auto-equip, no inventory (P0)

Decision: P0 has no gear inventory. A dropped piece is compared with the equipped piece in its slot and is either equipped immediately or sold immediately. There is exactly one rule:

```
Receive(piece):
  cur = Equipped[piece.Slot]
  if cur is null or BasePct(piece) > BasePct(cur):      // strictly greater, base only, enhancement ignored
      Equipped[piece.Slot] = piece                       // piece arrives at EnhLevel 0
      if cur is not null: Gold += SellValue(cur)
      Emit GearEquipped(piece, replaced: cur, soldFor)
  else:
      Gold += SellValue(piece)
      Emit GearSold(piece, keptInstead: cur)
```

Why auto-equip rather than a small inventory:

1. It is the bot's rule (`Bot.drop`: `if val > self.gear[slot]` on base value, enhancement reset to 0). The 30-day curve in `specs/02` was tuned with it, and the C# bot in `specs/09` must reproduce that curve.
2. In P0 a piece is one scalar. With main stat only, a lower-base piece is worse in every situation, so there is no decision for an inventory to offer the player. An inventory becomes necessary only when substats (P1) make comparison multi-dimensional; section 11.1 reserves the shape for that.
3. Idle players leave the game running. A held piece that needs a tap is a stall; an auto-equipped piece is progress.

Why compare base and not effective pct: comparing effective pct (new piece at 0 against the equipped piece with its enhancement) would never equip a same-rarity next-tier piece while the current one is above roughly +2, because one tier is worth about `GEAR_SLOT_G^10 - 1` relative and enhancement is worth up to `ENH_PER_LVL * ENH_MAX`. In the late game, where a tier arrives every day or two, that rule freezes a slot until a piece about `1 + ENH_PER_LVL * ENH_MAX` times better in base arrives, which takes a week or more. The gear layer carries the exponential (02 section 4.2), so freezing it misses the day-30 target. Comparing base keeps every slot on the tier frontier and makes enhancement a recurring sink, at the cost of the dip described in section 5.7.

Toast (specs/07 renders it; the sim only emits the event):

```
Equipped {Rarity} {Slot} T{Tier}: +{BasePct} base (was {OldRarity} T{OldTier} +{OldEnh}: +{OldEffPct} eff). Sold old for {gold}.
Sold {Rarity} {Slot} T{Tier} for {gold}: not better than equipped.
```

The toast shows both the base comparison the rule used and the effective value the player is losing or keeping, so a replacement that temporarily lowers DPS is visible rather than mysterious.

### 5.5 Selling

```
SellValue(p) = GEAR_SELL_KILLS * GOLD(p.Tier) * RARITY_MULT[p.Rarity]
```

- Uses the piece's own `Tier`, not the current stage, so a piece is worth the same whenever it is sold. In P0 that is always immediately; in P1 (inventory) it prevents hoarding for a better price.
- Ignores `EnhLevel`. Enhancement gold is sunk; see section 12 item 2 for why that must stay true.
- In P0 selling happens only inside `Receive`. There is no sell button because there is nothing to hold. The `GearSold` event carries the gold so the client can show it.

Expected value check (section 12 item 1): a normal kill yields on average `GEAR_DROP_P * GEAR_SELL_KILLS * E[RARITY_MULT]` kills of gold from selling, which with the P0 values is under 3% of the kill's own gold. The piece's tier is at most the current stage, so `GOLD(Tier) <= GOLD(stage)` and the bound holds at every stage.

### 5.6 Enhancement

Semantics are `specs/00` section 2.3, restated as a procedure:

```
TryEnhance(slot):
  p = Equipped[slot]
  if p is null:                 return NoPiece          // no gold, no draw
  if p.EnhLevel >= ENH_MAX:     return AtMax            // no gold, no draw
  cost = ENH_COST(Stage)                                // Stage = player's current stage, walled or not
  if Gold < cost:               return NotEnoughGold    // no gold, no draw
  Gold -= cost                                          // consumed on success and on failure
  u = enhance.NextDouble()                              // exactly one draw per paid attempt, including level 0
  success = u < ENH_SUCCESS(p.EnhLevel)
  if success: Equipped[slot] = p with { EnhLevel = p.EnhLevel + 1 }
  Emit EnhanceAttempted(slot, success, levelAfter, cost)
  return success ? Success : Fail
```

Fail consumes the cost and changes nothing else: no level loss, no destruction, no pity. `ENH_SUCCESS(0)` is 1.0 so the first level never fails; the draw is still consumed so `enhance.Counter == paid attempts`.

Per-slot panel shows: current `+EnhLevel / ENH_MAX`, cost `ENH_COST(Stage)`, success chance `ENH_SUCCESS(EnhLevel)` as a percent, and the gain on success as `+ENH_PER_LVL * BasePct(p)` absolute pct and as a relative DPS delta `ENH_PER_LVL * BasePct(p) / (1 + EffPct(p))`. One tap is one attempt. An auto-enhance toggle is P1; the P0 bot calls `TryEnhance` directly.

Cost reference for tuning conversations: the expected number of attempts to take one piece from 0 to `ENH_MAX` is `sum over l of 1 / ENH_SUCCESS(l)`, about 26 with the P0 table, so about 26 times `ENH_COST(Stage)`, which is roughly 20 to 25 minutes of kills at the two-second frontier.

### 5.7 Enhancement does not carry on replacement (P0 recommendation)

Recommendation: when `Receive` replaces a piece, the new piece starts at `EnhLevel 0` and the old piece's enhancement is gone with it. No transfer, no refund.

The bot assumes exactly this (`self.enh[slot] = 0` with the comment "fresh piece, enhancement does not carry (prototype rule)").

Justification:

1. It keeps enhancement a recurring gold sink. With carry, the whole game contains at most `GEAR_SLOTS * ENH_MAX` successful enhancements, about 158 expected attempts in total, after which gold has one sink left and the spend decision in 02 section 1.4 disappears. Without carry, every replacement reopens the sink in that slot.
2. It is what the day-30 curve was tuned on. Carry would hold every slot at `1 + ENH_PER_LVL * ENH_MAX` permanently; a rough estimate is a 3x to 4x higher gear multiplier at day 30, worth about 30 stages, and the harness would need rerunning.
3. The visible cost is a dip. On replacement the slot's effective pct can fall by up to a factor `1 / (1 + ENH_PER_LVL * ENH_MAX)` (same rarity, next tier, fully enhanced). That dip is the moment the game asks the player to spend gold, which is the point of the sink. The toast in section 5.4 shows it honestly, and the panel in 5.6 shows how to recover.

What to leave room for (P1 candidate, section 14 item 1): a transfer-at-a-fee action that moves `EnhLevel` from the replaced piece to the new one for a gold cost, or a partial refund of enhancement gold on sale. Both need the replaced piece to still exist at decision time, which the P1 pending-piece inventory (section 11.1) provides. Neither is built in P0, and `GearPiece` needs no new field for either.

### 5.8 Gear multiplier in the power formula

`GearMult` from section 5.1 is the gear layer in 00 section 3. It is recomputed whenever `Equipped` changes (receive or enhance) and cached; the combat sim reads the cached value each tick.

## 6. Class advance

```
OnStageEntered(stage):                                  // called by specs/03 when Stage becomes `stage`
  while ClassAdvances < CLASS_STAGES.Length and stage >= CLASS_STAGES[ClassAdvances]:
      ClassAdvances += 1
      Emit ClassAdvanced(index: ClassAdvances, mult: CLASS_MULT ^ ClassAdvances)
ClassMult = CLASS_MULT ^ ClassAdvances
```

- Automatic, not a button. The multiplier applies on the tick the player enters the milestone stage, before that stage's boss. This is the bot's rule verbatim (`while self.classes < len(CLASS_STAGES) and self.stage >= CLASS_STAGES[self.classes]`), and an idle player with the phone on the desk does not stall on an unpressed button.
- The `while` handles a save loaded at a stage past several milestones (P0 cannot skip stages, but `specs/06` migrations and tests can).
- `ClassAdvances` is stored, not derived from `Stage`, so P2 prestige can reset `Stage` and keep or reset class independently. In P0 a test asserts the stored value equals the count of `CLASS_STAGES` entries `<= Stage`.
- What the player sees (`specs/07`): a non-blocking full-width card from the `ClassAdvanced` event with the class name from `ClassDef`, "ATK x `CLASS_MULT`", and DPS before and after. Combat keeps running behind it. Class names are content (`ClassDef { int Stage; string Name; }`, one row per `CLASS_STAGES` entry; placeholders in P0).

## 7. Daily gem economy

The target is `GEMS_PER_DAY_F2P`, reached in the mid game as `LOGIN_GEMS + 3 * QUEST_GEMS + FIRST_CLEAR_GEMS * (new boss stages today) + IDLE_CHEST_GEMS * (chests claimed today)`. The table in 01 section 5 is taken as given; this section defines the pieces precisely enough to implement.

### 7.1 Day key and rollover

```
DayKey(now) = now / DAY_SECONDS          // integer division on server-time seconds
EnsureDay(now):
  key = DayKey(now)
  if key <= Daily.DayKey: return      // <= not ==: a clock that went backwards must not re-grant the day (specs/06)
  Daily = new DailyState { DayKey = key }  // progress zero, nothing rewarded
  Currencies.FreePullsDaily = FREE_PULLS_PER_DAY
  Emit DayRolledOver(key)
  Progress(Login, +1)                      // the login quest completes on the first tick of the day
```

- `EnsureDay(IClock.NowServerSeconds)` runs at session open, on every `OnTick`, and at the top of every quest, chest and pull-payment call. Rollover is therefore lazy: a player who was away three days gets exactly one reset on return, and the two missed days' quests are simply gone. There is no catch-up and no stacking.
- A new profile has `DayKey = -1`, so the first `EnsureDay` grants the first login bonus and the first daily free pulls. This matches the bot, which starts each day with `gems += GEMS_PER_DAY` and three free pulls.
- Rollover while the app is open applies immediately at the boundary tick.
- Rollover is 00:00 UTC in P0. Regional offsets are an open decision (section 14 item 6); the only code change is a constant added to `now` before dividing.

```csharp
public enum QuestKind : byte { Login, Kills, BossAttempts, Pulls }
public sealed record QuestDef(string Id, QuestKind Kind, int Target, long RewardGems);   // ContentDb rows

public sealed record DailyState
{
    public long DayKey { get; set; } = -1;
    public int[] Progress { get; init; } = new int[4];    // indexed like ContentDb.Quests
    public bool[] Rewarded { get; init; } = new bool[4];
    public int DungeonTicketsUsed { get; set; }           // P1
}
```

P0 content rows:

| Id | Kind | Target | Reward |
|---|---|---|---|
| `daily_login` | Login | 1 | `LOGIN_GEMS` |
| `daily_kills` | Kills | `QUEST_KILLS_TARGET` | `QUEST_GEMS` |
| `daily_boss` | BossAttempts | `QUEST_BOSS_TARGET` | `QUEST_GEMS` |
| `daily_pulls` | Pulls | `QUEST_PULLS_TARGET` | `QUEST_GEMS` |

### 7.2 Quests

```
Progress(kind, delta):
  EnsureDay(now)
  for each quest q of this kind:
    Progress[q] += delta
    if not Rewarded[q] and Progress[q] >= q.Target:
        Rewarded[q] = true
        Gems += q.RewardGems
        Emit QuestCompleted(q.Id, q.RewardGems)
```

- Rewards are granted the moment the target is reached. There is no claim button in P0 (section 14 item 7).
- `Kills` counts every kill, normal or boss, in the live sim. Offline catch-up adds none.
- `BossAttempts` counts every boss fight started (the automatic first attempt on reaching the 10th enemy of a boss stage, and every manual `AttemptBoss` retry), whether or not it succeeds. This keeps the quest completable while walled; without it late-game gem income tapers to about 230-250 per day because fewer than one new boss is cleared per day after day 14.
- `Pulls` counts every pull regardless of payment; a 10-pull adds 10. `specs/01`'s `Pull` calls `Progress(Pulls, 1)` per result.
- Progress past the target keeps counting (for the UI) but never rewards twice.

### 7.3 First clears

```
FirstClear(stage):                       // stage is a boss stage that was just killed
  if stage > Progression.HighestFirstClearBossStage:
      Progression.HighestFirstClearBossStage = stage
      Gems += FIRST_CLEAR_GEMS
      Emit FirstClear(stage, FIRST_CLEAR_GEMS)
```

One integer suffices because stages are linear and monotone in P0. It is stored rather than derived from `Stage` so that P2 prestige (which resets `Stage`) does not re-pay first clears. If P1 stage select ever lets a player clear boss stages out of order, replace the integer with a bitset keyed by `stage / BOSS_EVERY`; the migration is mechanical.

### 7.4 Idle chest

```
ChestReadyAt = Progression.ChestTimerStart + IDLE_CHEST_HOURS * 3600
TryClaimIdleChest():
  EnsureDay(now)
  if now < ChestReadyAt: return false
  Gems += IDLE_CHEST_GEMS
  Progression.ChestTimerStart = now           // restart from the claim, not from ChestReadyAt
  Emit IdleChestClaimed(IDLE_CHEST_GEMS)
  return true
```

- The timer runs on server time whether the app is open or not. It is independent of `OFFLINE_CAP_H`, which caps gold only.
- At most one chest is ever ready. A player away for a day finds one chest, not six. Restarting from the claim time (not the ready time) is what prevents stacking in disguise.
- Manual claim: the client shows a countdown to `ChestReadyAt` and a Claim button when ready. The bot claims at the start of any active minute in which the chest is ready.
- A new profile sets `ChestTimerStart` to the creation time.
- Upper bound: a player who keeps the app open and claims on time can collect `24 / IDLE_CHEST_HOURS` chests a day, above the two chests 01 section 5 assumes. There is no daily cap in P0 (section 14 item 5).
- Two chests a day presumes two sessions roughly half a day apart, which is also the session model 02 section 7 uses for offline gold. A bot that plays one contiguous session a day collects one chest. `specs/09` should split `ACTIVE_MIN` into two sessions to reproduce 01's income.

### 7.5 Free pulls and pull payment

The gacha roll is `specs/01`; paying for it is this spec. `specs/01`'s pull entry point calls `PayForPulls(count)` first and rolls only if it returns a payment.

```
PayForPulls(count):                      // count is 1 or 10
  EnsureDay(now)
  if count == 1:
      if FreePullsDaily  > 0: FreePullsDaily  -= 1; return Paid(FreeDaily)
      if FreePullsBanked > 0: FreePullsBanked -= 1; return Paid(FreeBanked)
      if Gems >= PULL_COST_GEMS: Gems -= PULL_COST_GEMS; return Paid(Gems)
      return CannotAfford
  if count == 10:
      if FreePullsBanked >= 10: FreePullsBanked -= 10; return Paid(FreeBanked)
      if Gems >= 10 * PULL_COST_GEMS: Gems -= 10 * PULL_COST_GEMS; return Paid(Gems)
      return CannotAfford
```

- No mixed payments. Daily credits (at most `FREE_PULLS_PER_DAY`) pay single pulls only and are spent before banked credits because they expire. Banked credits can pay a 10-pull so the tutorial's `TUTORIAL_FREE_PULLS` can be one 10-pull with the E+ guarantee.
- A 10-pull costs `10 * PULL_COST_GEMS`; no discount in P0 (01 section 5).
- The bot spends daily credits as three single pulls, then gems in single pulls while `Gems >= PULL_COST_GEMS`.

### 7.6 Budget check

Realistic daily totals from these rules, by phase, so the number `GEMS_PER_DAY_F2P` is read as the mid-game target it is:

| Phase | Login | Quests | First clears | Chests | Total |
|---|---|---|---|---|---|
| Day 1 (about 9 new bosses, one or two chests) | `LOGIN_GEMS` | `3 * QUEST_GEMS` | about `9 * FIRST_CLEAR_GEMS` | 1 to 2 chests | about 280 to 330 |
| Mid game (about 6 new bosses, two chests) | `LOGIN_GEMS` | `3 * QUEST_GEMS` | about `6 * FIRST_CLEAR_GEMS` | 2 chests | `GEMS_PER_DAY_F2P` |
| Late game (under one new boss a day) | `LOGIN_GEMS` | `2 * QUEST_GEMS` on days with no boss kill | under `1 * FIRST_CLEAR_GEMS` | 2 chests | about 230 to 250 |

The late-game shortfall against the bot's flat `GEMS_PER_DAY` is section 14 item 4.

## 8. Tutorial grants

```
OnProfileCreated(now):
  Gems += TUTORIAL_GEMS
  FreePullsBanked += TUTORIAL_FREE_PULLS
  Progression.TutorialGranted = true
  Progression.ChestTimerStart = now
  EnsureDay(now)                     // first login bonus and daily free pulls
```

Granted once, at profile creation, guarded by `TutorialGranted` so a migration or a repeated init cannot double-grant. In P1 the grant moves behind the tutorial step that opens the banner; the field and the amounts do not change.

## 9. Reference spend policy (the bot)

This is `Bot.spend` in `tools/power_curve_model.py`, expressed as `IEconomy` calls. `specs/09` implements it verbatim as the default bot and runs it at the end of every active minute.

```
Spend():
  m = MaxAffordable(StatLevel, Gold); if m > 0: BuyStatLevels(m)          // stats first, greedy
  slot = argmin over slots of Equipped[slot].EnhLevel (ties: lowest slot index; empty slots skipped)
  if Equipped[slot].EnhLevel < ENH_MAX and Gold >= ENH_COST(Stage): TryEnhance(slot)   // one attempt per minute
  if chest ready: TryClaimIdleChest()
  while PayForPulls(1) succeeds with gems or credits: Pull()                 // credits first by construction
```

Three properties of this policy that the real UI reproduces:

1. Stats are bought before enhancement, so enhancement only ever gets the remainder below the next stat cost. Early on that remainder is often under `ENH_COST(Stage)` and enhancement starves; late on, stat cost dwarfs enhancement cost and an attempt happens nearly every minute. That is the tuned behaviour, not an accident.
2. Enhancement goes to the weakest slot, which spreads levels evenly and means a replacement (reset to 0) immediately attracts the next attempts.
3. Gems are spent as soon as they reach `PULL_COST_GEMS`. The bot never saves for a 10-pull; a human may, and the E+ guarantee is the only reason to.

## 10. Sim API and events

```csharp
public readonly record struct KillInfo(int Stage, bool IsBoss);

public enum EnhanceResult : byte { Success, Fail, NoPiece, AtMax, NotEnoughGold }
public enum PaidWith : byte { FreeDaily, FreeBanked, Gems }
public readonly record struct PullPayment(bool Ok, PaidWith With);

public interface IEconomy
{
    // Stat levels (section 4)
    double StatCost(int n);
    double BulkStatCost(int n, int m);
    int    MaxAffordableStatLevels();
    double StatDeltaPct(int m);
    bool   BuyStatLevels(int m);                 // all-or-nothing

    // Gear (section 5)
    EnhanceResult TryEnhance(GearSlot slot);
    double        SellValue(GearPiece p);
    double        BasePct(GearPiece p);
    double        EffPct(GearPiece p);
    double        GearMult { get; }

    // Gems (section 7)
    bool        TryClaimIdleChest();
    long        ChestReadyAt { get; }
    PullPayment PayForPulls(int count);

    // Hooks from the combat sim (specs/03) and the clock
    void OnKill(in KillInfo k);
    void OnStageEntered(int stage);
    void OnTick(long nowServerSec);              // EnsureDay only; cheap
}

public sealed record ProgressionState
{
    public int  StatLevel { get; set; }
    public int  ClassAdvances { get; set; }
    public int  HighestFirstClearBossStage { get; set; }
    public long ChestTimerStart { get; set; }
    public bool TutorialGranted { get; set; }
}

// Hero-side state the economy touches. Pity, TotalPulls and the stream live on BannerState (specs/01).
public readonly record struct HeroId(Rarity Tier, int Index);
public sealed record HeroState
{
    public Dictionary<HeroId, int> Copies { get; init; } = new();       // level is derived via HERO_LVL_CUM
    public HeroId?[] Equipped { get; init; } = new HeroId?[6];         // HERO_SLOTS
    public bool AutoEquipBest { get; set; } = true;                      // sort by heroPct, top six
}

// Events. The sim appends; the client drains once per frame; the bot counts or ignores.
public abstract record SimEvent;
public sealed record GearEquipped(GearPiece Piece, GearPiece? Replaced, double SoldFor) : SimEvent;
public sealed record GearSold(GearPiece Piece, GearPiece? KeptInstead, double Gold) : SimEvent;
public sealed record EnhanceAttempted(GearSlot Slot, bool Success, int LevelAfter, double Cost) : SimEvent;
public sealed record StatLevelsBought(int Count, double Cost, int LevelAfter) : SimEvent;
public sealed record ClassAdvanced(int Index, double Mult) : SimEvent;
public sealed record QuestCompleted(string QuestId, long Gems) : SimEvent;
public sealed record FirstClear(int Stage, long Gems) : SimEvent;
public sealed record IdleChestClaimed(long Gems) : SimEvent;
public sealed record DayRolledOver(long DayKey) : SimEvent;
```

`Simulation.DrainEvents()` returns and clears the queue. Events are not saved; a save made mid-queue simply loses the toasts, never the state.

## 11. P1 systems and how they plug into the P0 shapes

### 11.1 Substats, reroll, lock, and the pending-piece inventory (P1)

Substat pool from 02 section 10: crit chance, crit damage, attack speed, gold find. Each is a `Substat(SubstatKind Kind, double Value)` on `GearPiece.Substats`; the count of lines is by rarity (content), values roll on a new `gear_substat` stream so P0 stream counters are untouched. The substat term enters the gear layer as one extra multiplier bounded to about x2 total so section 5 of `specs/02` holds. Reroll costs gold (P0 sink number three) and rerolls unlocked lines; `Locked` on the piece freezes all lines and excludes the piece from auto-sell. Because comparison stops being scalar, `Receive` changes to: equip if the new piece is better on base pct and not worse on a content-defined substat score; otherwise hold it in `GearState.Pending[slot]` (one candidate per slot, the newer candidate displacing and selling the older) and let the player equip or sell. The P0 save migrates by adding an empty `Substats` array, `Locked = false` and a null `Pending` array; the P0 bot policy (equip on base) remains a valid bot policy and the harness keeps running.

### 11.2 Daily dungeon tickets and enhancement materials (P1)

`Currencies.DungeonTickets` resets through `EnsureDay` like quests; a dungeon run consumes one ticket and grants `EnhMaterials` scaled to the player's tier. `TryEnhance` reads a `CostKind` from the enhancement content row (`Gold` in P0, `Materials` in P1) and charges `Currencies.EnhMaterials` instead of gold; the success table, `ENH_MAX`, the `enhance` stream and the fail semantics do not change. Per 02 section 10, the per-day expected enhancement levels must stay equal to what the gold path gives today, so the ticket count and material drop are tuned to about one maxed piece's worth of attempts (section 5.6) per day. Decoupling the two sinks means stat levels get all the gold, so `STAT_COST_G` or `GOLD_G` is retuned in the same change and the bot rerun.

### 11.3 Shard shop (P1)

Prices from 01 section 7: a chosen Epic for 250 shards, a chosen Legendary for 1500 shards. Common and Rare offers, and any Mythic offer, are undecided and listed in `specs/01` section 11. The shop is a content table `ShopOfferDef(Rarity Tier, long PriceShards, int DailyLimit)` plus a `BuyHeroCopy(HeroId id)` call that debits `Currencies.Shards` and adds one copy through the same dupe-to-level path `specs/01`'s `Pull` uses, without touching the `gacha_hero` stream, the pity counter or the `Pulls` quest. Because P0 already accrues `Currencies.Shards`, P1 players open the shop with a balance.

### 11.4 Other hooks (P1, P2)

- IAP and rewarded ads (P1) add gems through one `GrantGems(source, amount)` call that also emits an event; nothing else changes. On a ranked profile the call applies a server ledger entry and is logged as `GrantFromLedger` with the ledger id, so segment replay applies the server's number and not the client's (`specs/10` sections 4.3 and 8).
- Stage select or boss rush (P1) must make `ENH_COST` and `GEAR_TIER` read the player's highest stage, not the selected one, or the player farms cheap enhancement on a low stage (section 12 item 7).
- Prestige (P2) resets `StatLevel` and `Stage`, keeps `Equipped` and `ClassAdvances` unless the design says otherwise, and must not reset `HighestFirstClearBossStage`.

## 12. Anti-degenerate-strategy notes

1. Selling gear is not a gold source. Expected sell income per normal kill is `GEAR_DROP_P * GEAR_SELL_KILLS * E[RARITY_MULT] * GOLD(Tier)`, under 3% of `GOLD(stage)`, and per boss kill is `GEAR_BOSS_DROP_P * GEAR_SELL_KILLS * E[RARITY_MULT | R+] * GOLD(Tier)`, under a quarter of the boss's `BOSS_GOLD_MULT * GOLD(stage)`. Kill rate is capped by `ATTACK_INTERVAL_TICKS + SPAWN_DELAY_TICKS`, so farming a low stage for drops yields lower-tier pieces at a lower sell value and a lower gold rate than pushing. Any change to `GEAR_SELL_KILLS` must keep `GEAR_DROP_P * GEAR_SELL_KILLS * E[RARITY_MULT] < 0.05`; test 28 enforces it.
2. Enhance-then-sell is always a loss. `SellValue` ignores `EnhLevel`, and a single attempt costs `ENH_COST(Stage) = 20 * GOLD(Stage)` against a sell value of `GEAR_SELL_KILLS * GOLD(Tier)` with `Tier <= Stage`. Even if sell value ever scaled with enhancement it would need to exceed `ENH_MAX * ENH_COST` to break even; keep it ignoring enhancement regardless.
3. Save-scumming enhancement or drops does nothing. `enhance`, `gear_drop` and `gear_rarity` are `(seed, counter)` streams persisted in the save (00 section 5.2); reloading replays the same outcomes. Draws are consumed even when the outcome is certain so the counter arithmetic is auditable.
4. Device clock changes do nothing. Day keys and the chest timer read `IClock` (server time) only. In the prototype the fake clock is the test seam; the Unity client never passes `DateTime.Now` into the sim.
5. First clears pay once per boss stage because `HighestFirstClearBossStage` is monotone. Re-killing a boss stage (P1 stage select) earns boss gold and quest credit but no first-clear gems.
6. Idle chest cannot stack. One ready chest at a time, timer restarts at claim.
7. Enhancement cost reads the current stage, and in P0 the current stage is the highest stage, so there is no cheap stage to farm enhancement on. P1 stage select must preserve that by reading the highest stage reached.
8. Bulk-buy rounding cannot be exploited. Quote and charge are the same closed-form number; `MaxAffordable` is fixed up by at most one level in each direction and never quotes an unaffordable count.
9. Tier-0 drops have zero value and sell for `GEAR_SELL_KILLS * GOLD(0) * RARITY_MULT`, a few gold. Harmless, and they only occur on stages 1 to 9.
10. Quest counters only advance in the live sim. Offline catch-up adds gold and nothing else, so leaving the app closed earns no quest progress, matching the bot.

## 13. Unit tests

Tests live in `HeroCat.Sim.Tests/EconomyTests.cs` and `GearTests.cs`. All constants come from a test `ContentDb` loaded from the P0 content tables; stubbed streams return scripted `u` values.

Stat levels (section 4):

1. `StatCost(n)` equals `STAT_COST0 * STAT_COST_G^n` for `n` in {0, 1, 10, 100, 461} within 1e-9 relative.
2. `BulkCost(n, m)` equals the loop sum of `StatCost(n..n+m-1)` within 1e-9 relative for `(n, m)` in {(0,1), (0,10), (64,100), (461,100)}.
3. `MaxAffordable`: gold exactly `BulkCost(n, m)` returns `m`; gold `BulkCost(n, m) * (1 - 1e-12)` returns `m - 1`; gold below `StatCost(n)` returns 0; for `n` in {0, 64, 300}, `m` in {1, 10, 1000}.
4. `BuyStatLevels(10)` with insufficient gold returns false and changes nothing; with sufficient gold debits exactly `BulkCost(n, 10)`, sets `StatLevel += 10`, emits one `StatLevelsBought`.
5. `StatDeltaPct(m)` equals `STAT_PER_LVL * m / (1 + STAT_PER_LVL * n)`, and `DPS` after purchase equals `DPS` before times `1 + StatDeltaPct(m)` within 1e-9.

Gear drops (sections 5.2, 5.3):

6. Stream accounting: 1000 kills with the real generator advance `gear_drop.Counter` by exactly 1000 and `gear_rarity.Counter` by exactly `2 * drops`, where `drops` is the count of `GearEquipped` plus `GearSold` events.
7. Drop threshold is strict: normal kill with `u_drop = GEAR_DROP_P - 1e-12` drops; `u_drop = GEAR_DROP_P` does not. Same for a boss kill against `GEAR_BOSS_DROP_P`.
8. Rarity boundaries with `min = C`: `u` in {0.0, 0.6 - 1e-12, 0.6, 0.88, 0.97, 0.995, 1 - 1e-12} gives {C, C, R, E, L, M, M}.
9. Rarity boundaries with `min = R` (boss): `u` in {0.0, 0.7 - 1e-12, 0.7, 0.925, 0.9875, 1 - 1e-12} gives {R, R, E, L, M, M}, and `u = 0.0` is never C.
10. Slot: `u_slot = k / GEAR_SLOTS` for `k` in 0..5 gives slot `k`; `u_slot = 1 - 1e-12` gives Ring.
11. Tier: drops on stages 1, 9, 10, 19, 547 carry tiers 0, 0, 10, 10, 540.
12. `BasePct` equals `(GEAR_SLOT_G^tier - 1) * RARITY_MULT[r]` for tier in {0, 10, 300} and every rarity; tier 0 is exactly 0.
13. Failed boss attempt (timeout) calls none of `OnKill`: gold, counters and streams unchanged.

Receive and sell (sections 5.4, 5.5):

14. Empty slot: any piece equips, no gold credited, `GearEquipped` with `Replaced == null`.
15. Strictly better base replaces: new piece equipped at `EnhLevel 0`, old piece sold for `SellValue(old)`, old `EnhLevel` does not appear anywhere afterward.
16. Equal base does not replace: same tier and rarity as equipped sells the new piece, equipped piece and its `EnhLevel` unchanged.
17. Lower base with equipped piece at `ENH_MAX`: new piece sold; and higher base against equipped piece at `ENH_MAX`: new piece equipped even though `EffPct(new) < EffPct(old)` (documents the dip in section 5.7).
18. `SellValue` equals `GEAR_SELL_KILLS * GOLD(tier) * RARITY_MULT[r]` and is independent of `EnhLevel` (same piece at 0 and at `ENH_MAX` sells for the same gold).
19. `GearMult` equals the product over slots of `1 + BasePct * (1 + ENH_PER_LVL * EnhLevel)`; empty slots contribute 1; a six-slot fixture is checked within 1e-9.

Enhancement (section 5.6):

20. `ENH_SUCCESS(l)` for `l` in {0, 1, 13, 14} equals {1.0, 0.95, 0.35, 0.30}; `TryEnhance` at `EnhLevel == ENH_MAX` returns `AtMax`, charges nothing, consumes no draw.
21. `TryEnhance` with `Gold < ENH_COST(Stage)` returns `NotEnoughGold`, consumes no draw; with an empty slot returns `NoPiece`.
22. Success and failure both debit exactly `ENH_COST(Stage)` and consume one `enhance` draw; failure leaves `EnhLevel` unchanged; success increments by one. Stage used is the player's current stage, including when walled.
23. Reproducibility: save mid-sequence, continue 50 attempts, restore, continue 50 attempts: identical results and identical final `EnhLevel`s.

Class (section 6):

24. Entering stage `CLASS_STAGES[0] - 1` gives `ClassAdvances 0`; entering `CLASS_STAGES[0]` gives 1 and exactly one `ClassAdvanced` event; `ClassMult` equals `CLASS_MULT ^ ClassAdvances`.
25. Loading a save at a stage past three milestones and calling `OnStageEntered(Stage)` yields `ClassAdvances 3` and three events; calling it again yields none.

Daily economy (section 7):

26. `DayKey`: `now = DAY_SECONDS - 1` and `now = DAY_SECONDS` are different keys; `EnsureDay` across that boundary resets `Progress` and `Rewarded`, sets `FreePullsDaily = FREE_PULLS_PER_DAY`, grants `LOGIN_GEMS` exactly once, and a second `EnsureDay` on the same key grants nothing.
27. New profile: first `EnsureDay` grants `LOGIN_GEMS` and `FREE_PULLS_PER_DAY` credits.
28. Quest rewards fire once: `QUEST_KILLS_TARGET` kills grant `QUEST_GEMS`; kill number `QUEST_KILLS_TARGET + 1` grants nothing; `Progress` keeps counting.
29. Boss quest: one boss kill grants `QUEST_GEMS`; a boss timeout does not.
30. Pulls quest counts free pulls and 10-pulls: three single pulls paid with daily credits complete it; one 10-pull completes it in one step.
31. First clears: killing boss 10 grants `FIRST_CLEAR_GEMS` and sets `HighestFirstClearBossStage = 10`; a save with `HighestFirstClearBossStage = 100` killing boss 100 grants nothing and killing boss 110 grants `FIRST_CLEAR_GEMS`.
32. Idle chest: not ready at `ChestTimerStart + IDLE_CHEST_HOURS * 3600 - 1`; ready at `+ IDLE_CHEST_HOURS * 3600`; still exactly one claim available at `+ 3 * IDLE_CHEST_HOURS * 3600`; claim grants `IDLE_CHEST_GEMS` and sets `ChestTimerStart` to the claim time, not to `ChestReadyAt`.
33. Pull payment order: with 2 daily, 5 banked and 1000 gems, seven single pulls spend 2 daily then 5 banked then 0 gems; the eighth spends `PULL_COST_GEMS` gems; a 10-pull with 9 banked credits spends `10 * PULL_COST_GEMS` gems and leaves the 9 credits; with 10 banked credits it spends the credits.
34. Daily credits expire and banked do not: rollover with 1 daily and 4 banked leaves `FREE_PULLS_PER_DAY` daily and 4 banked.
35. Budget: a scripted day (login, `QUEST_KILLS_TARGET` kills, 1 boss kill, 3 pulls, 6 new boss stages, 2 chest claims) adds exactly `GEMS_PER_DAY_F2P` gems.
36. Offline catch-up adds gold only: `Progress[Kills]`, `gear_drop.Counter` and the chest timer semantics are unaffected by `Advance` over an offline span except that the chest becomes ready when wall time says so.

Tutorial (section 8):

37. `OnProfileCreated` grants `TUTORIAL_GEMS` and `TUTORIAL_FREE_PULLS` banked credits, sets `TutorialGranted`; calling it again changes nothing.

Anti-degenerate (section 12):

38. Monte Carlo, fixed seed, 1,000,000 normal kills at a fixed stage with every drop sold: total sell gold divided by total kill gold is below 0.03; the same for 100,000 boss kills is below 0.25.
39. For every tier in {0, 10, 100, 500} and every rarity, `SellValue(piece) < ENH_COST(tier)`.

Bot (section 9):

40. Golden test: a fixed `ProgressionState`, `GearState`, `Currencies` and scripted streams run through `Spend()` once produce the hand-computed `StatLevel`, which slot was enhanced, its result, chest claim and number of pulls.

## 14. Open decisions

1. **Enhancement carry.** P0 does not carry (section 5.7). After the first playtest, decide between keeping that, a gold-fee transfer, and a partial refund on sale. Any change requires rerunning `tools/power_curve_model.py` with the matching rule and re-checking the `specs/09` tolerances; the estimate in 5.7 is about +30 stages at day 30 for full carry.
2. **Names to add to `specs/00` section 2.** `GEAR_RARITY_W`, `RARITY_MULT`, `GEAR_BOSS_DROP_P`, `GEAR_BOSS_MIN_RARITY` (values already there, unnamed); `GEAR_SELL_KILLS`, `STAT_BULK_STEPS`, `DAY_SECONDS` (new); and the quest, chest, tutorial constants in section 2 of this spec (values in `specs/01` section 5). Until `specs/00` adopts them, the names in section 2 here are the content keys `specs/05` uses.
3. **Boss "at least Rare" mechanism.** Renormalise over R+ (chosen, section 5.3) versus bump C to R. Revisit only if boss drops read as too generous in playtests.
4. **Late-game gem income.** With fewer than one new boss a day after day 14, the boss quest fails on some days and first clears fall to a few gems, so realistic income is about 230 to 250 against `GEMS_PER_DAY_F2P`. Options: change `daily_boss` to count attempts (completable while walled), add a P1 boss rematch that grants quest credit only, or accept the taper. The bot assumes the full amount every day.
5. **Idle chest daily cap.** None in P0; an always-on player can collect `24 / IDLE_CHEST_HOURS` chests. Decide whether to cap at two per day key once there is data on how many players leave the app open.
6. **Rollover hour.** 00:00 UTC in P0. Regional rollover (a per-profile offset added to `now` before the division) is a one-constant change but needs the backend contract in `specs/08` to supply it.
7. **Quest claim step.** P0 auto-grants on completion. A claim button is a retention hook many games use; it changes `Progress` to a three-state quest and the bot must claim. Decide with `specs/07`.
8. **Shards home.** This spec puts `Shards` on `Currencies`; `specs/01` section 8's sketch writes `st.Shards`. One of the two texts should be edited to match; the behaviour is identical.
9. **`GEAR_SELL_KILLS` value.** 5 makes selling flavour (under 3% of kill gold). If a sell button arrives with the P1 inventory and selling should matter, raise it, keeping `GEAR_DROP_P * GEAR_SELL_KILLS * E[RARITY_MULT] < 0.05`.
10. **Bot session model.** The chest count and offline gold both depend on whether the bot plays one 2 h session or two 1 h sessions a day. `specs/09` should pick two sessions to match 01 section 5 and 02 section 7, and say so.
