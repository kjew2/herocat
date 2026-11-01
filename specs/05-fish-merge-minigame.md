## 05 - Fish Merge Minigame

This spec owns the bottom half of `SCR_MAIN`: the `FishBoard` grid and its cells, the `Fish` data per level, the `MagicTank` that produces level-1 fish on a tick-driven timer, the `ReleaseFish` / `MergeFish` / `DiscardFish` / `MoveFish` commands and their events, Fish Coin income from discovery and merges, the fish missiles that feed damage into 03's tick loop, the passive `FISH_BOARD_DPS_PCT`, the whole touch interaction model, the throughput math, `tools/fish_model.py`, persistence and offline behaviour, edge cases and analytics. Research nodes that upgrade the tank and the level cap are priced in 03 section 6; this spec says what they do.

Tiers covered: M0 (board, tank, release, merge, discard, discovery, missiles, passive DPS, merge-all and release-max buttons), MVP (Research upgrades, analytics, haptics), P1 (Auto Merge, tank level upgrades).

Depends on: 00 (section 6.4 constants), 02 (`FishBoardState` persistence), 03 (tick loop hooks `TickTank`, `MissileQueue`, `MarkDpsDirty`, Research effects), 06 (offline catch-up calls `CatchUpTank`), 07 (rendering and gesture recognition).

## 1. Design goals

1. The board is the thing the thumb does while the cat fights. Every merge should pay out in three ways at once: a coin number, a missile crossing the screen, and a bigger fish that raises the passive DPS line.
2. It is never mandatory. A player who only presses `Release max` and `Merge all` once per session still progresses; one who never touches it still gets the passive DPS of whatever is on the board.
3. It is deterministic. Cell placement is the only random element and it comes from the `fish_spawn` stream; merge results, coins and missiles are pure functions of levels.
4. It is tiny in the sim: 20 cells of board, a stock count and a refill timer. Everything else is presentation.

## 2. Board and fish data

### 2.1 State

02 section 3.1 owns the shape, names, types and defaults (issue #7); this is the part 05 relies on:

```csharp
public sealed class FishBoardState                               // GameState.Fish
{
    public int[]  Cells;              // length FISH_BOARD_W * FISH_BOARD_H = 20; 0 = empty, n >= 1 = fish of level n; index = y * FISH_BOARD_W + x
    public int    TankCount;          // fish in the Magic Tank, 0 .. TankCap
    public int    TankRefillTick;     // 0 .. TankRefillTicks-1; only advances while TankCount < TankCap
    public int    TankReleaseLevel;   // P1 tank upgrade level bought, default 1 (section 3)
    public HashSet<int> DiscoveredLevels; // levels ever created; drives FishDiscovered and the fish book (02 serialises it as a sorted array)
    public int    HighestLevel;       // max of DiscoveredLevels, cached for the HUD
    public long   TotalMerges;
    public bool   AutoMergeEnabled;   // P1
}
// Lifetime counters live in GameState.Stats (02): Stats.FishReleased, Stats.Merges, Stats.FishDiscards,
// Stats.FishCoinEarned (discovery + merge + discard, for the server plausibility check, 08).
```

`FISH_BOARD_W = 5`, `FISH_BOARD_H = 4` (00 section 6.4). The grid is drawn with (0,0) top-left; row-major order is also the order every "first empty cell" or "merge all" rule walks, so results do not depend on rendering.

### 2.2 Fish levels

One row per level in the `fish_levels` table (09). Values are the 00 formulas evaluated; the table exists so that the sheet can override a level (for example to make level 10 a visible milestone) without the code knowing.

| Level `n` | `FISH_DISCOVERY_COINS[n]` = `10 * 2^(n-1)` | `FISH_MERGE_COINS[n]` = 2% of discovery | `FISH_MISSILE_PCT[n]` = `5 * 1.5^(n-1)` (% of one hero hit) | Board DPS per fish = `FISH_BOARD_DPS_FACTOR (0.77) * missile` (%) | Level-1 fish consumed |
|---|---|---|---|---|---|
| 1 | 10 | - | - (never fired: level 1 is released, not merged) | 3.85 | 1 |
| 2 | 20 | 0.4 | 7.5 | 5.78 | 2 |
| 3 | 40 | 0.8 | 11.25 | 8.66 | 4 |
| 4 | 80 | 1.6 | 16.9 | 13.0 | 8 |
| 5 | 160 | 3.2 | 25.3 | 19.5 | 16 |
| 6 | 320 | 6.4 | 38.0 | 29.2 | 32 |
| 7 | 640 | 12.8 | 57.0 | 43.9 | 64 |
| 8 | 1,280 | 25.6 | 85.4 | 65.8 | 128 |
| 9 | 2,560 | 51.2 | 128 | 98.7 | 256 |
| 10 | 5,120 | 102 | 192 | 148 | 512 |
| 11 | 10,240 | 205 | 288 | 222 | 1,024 |
| 12 | 20,480 | 410 | 432 | 333 | 2,048 |
| 13 | 40,960 | 819 | 648 | 500 | 4,096 |
| 14 | 81,920 | 1,638 | 973 | 749 | 8,192 |
| 15 | 163,840 | 3,277 | 1,459 | 1,124 | 16,384 |

`FISH_MERGE_COINS[n]` is an unrounded `double` (00 contract 9: `FISH_COIN` is a `double`); 09 does not round it and the UI floors the display (issue #16).

Columns the table also carries: `name_key`, `sprite`, `splash_vfx`, `rarity_colour` (presentation), `missile_vfx`. MVP art covers 12 levels: rows above 12 reuse a level-1..12 sprite with a tint/frame variant, the merge sound clamps at its top step, and 09's validator warns when content can reach a level above the sprite count (07 owns the art list; issue #48). Levels 1-10 exist at Research 0 (`FISH_MAX_LVL0 = 10`); each `RES_FISH_CAP` level opens one more, up to 20. The table ships all 20 rows; the cap decides which are reachable.

Two reading aids. Missile damage grows x1.5 per level while the fish it takes to make one grows x2, so per level-1 fish the missile value of merging higher is `(0.75)^(n-1)`: merging is for the board DPS and the discovery payout, not for the missiles. Discovery coins double per level and the fish needed double too, so discovery pays a constant 10 coins per level-1 fish on the first climb to any level, which is why the first session feels rich and why `RES_FISH_CAP` opening level 11 is a 10,240-coin event.

## 3. Magic Tank

```
TankCap          = TANK_CAP0 (10) + RES_TANK_CAP_PER_LVL (5) * research[RES_TANK_CAP]              # 10 .. 60
TankRefillS      = max(TANK_REFILL_FLOOR_S (2.0), TANK_REFILL_S0 (6.0) - RES_TANK_REFILL_PER_LVL (0.4) * research[RES_TANK_REFILL])
TankRefillTicks  = round(TankRefillS * TICK_HZ)                                                      # 60 .. 20 ticks, integer

TickTank():                                   # called by 03's Tick() each tick, before combat
  if Fish.TankCount >= TankCap: Fish.TankRefillTick = 0; return
  Fish.TankRefillTick += 1
  if Fish.TankRefillTick >= TankRefillTicks:
    Fish.TankRefillTick -= TankRefillTicks; Fish.TankCount += 1
    publish TankFishReady(Fish.TankCount)     # opt-in event; the HUD reads TankCount from the snapshot instead

CatchUpTank(elapsedTicks):                    # closed form, no per-tick loop; pure arithmetic used by 06's Catchup.Compute (section 7)
  if Fish.TankCount >= TankCap: return (added: 0, refillTickAfter: 0)
  total = Fish.TankRefillTick + elapsedTicks
  gained = total / TankRefillTicks                                   # integer division
  added = min(TankCap - Fish.TankCount, gained)
  refillTickAfter = Fish.TankCount + added >= TankCap ? 0 : total % TankRefillTicks
  return (added, refillTickAfter)
```

A new profile starts with a full tank, `TankCount = TankCap` (`TANK_CAP0` at Research 0) and `TankRefillTick = 0`, set by `ProfileFactory` through the `TankCap` query, so the first session can release and merge at once (02 section 3.1; issue #246).

The timer runs on sim ticks, not wall time, so it advances exactly as fast as combat does and offline catch-up can fill it with the same arithmetic 06 uses for gold (`elapsedTicks = creditedSeconds * TICK_HZ`, credited not full gap; issue #21). The effective offline cap (`OFFLINE_CAP_H` plus `RES_OFFLINE_CAP`) applies to the tank too: a player back after a weekend finds a full tank, not three days of fish. At Research 0 the tank is full one minute after it empties; with `RES_TANK_CAP` 10 and `RES_TANK_REFILL` 10 it holds 60 fish and refills in two minutes. Progress while the tank is full is discarded, which is the whole reason to release fish before closing the app.

P1: tank level upgrades (released fish start at level 2, then 3) would halve the taps per coin. State holds the upgrade level bought, `Fish.TankReleaseLevel` (default 1); content holds what each level releases, with `tank.release_level` (09) as the base value (1 in MVP) (issue #7). Not in MVP content.

## 4. Commands

Board lock (issue #40): the board is hidden and frozen while a dungeon run is active (`Combat.Mode == Dungeon`, 06 and 07). Every board command (`ReleaseFish`, `ReleaseMax`, `MergeFish`, `MergeAll`, `MoveFish`, `DiscardFish`) checks this first, before any other rejection, and returns `Reject(Locked, "BOARD_LOCKED")`. P1 Auto Merge does not run during a run. The tank keeps refilling (`TickTank` runs every tick) and the passive `FISH_BOARD_DPS_PCT` still applies to the dungeon fight; no missiles are enqueued or fired (section 5.1).

### 4.1 `ReleaseFish`

```
ReleaseFish():                                # one fish per command; ReleaseMax loops it (section 6.1)
  if Combat.Mode == Dungeon:                             return Reject(Locked, "BOARD_LOCKED")     # issue #40
  if Fish.TankCount == 0:                                return Reject(TankEmpty, "TANK_EMPTY")
  empties = count of Cells == 0
  if empties == 0:                                       return Reject(BoardFull, "BOARD_FULL")
  u = fish_spawn.NextDouble()                            # exactly one draw per successful release
  k = (int)(u * empties)                                 # 0 .. empties-1
  cell = index of the k-th empty cell in row-major order
  lvl = released level for Fish.TankReleaseLevel         # 1 in MVP (section 3)
  Cells[cell] = lvl; Fish.TankCount -= 1; Stats.FishReleased += 1
  MarkDpsDirty()                                         # a level-1 fish adds 2.5% board DPS
  publish FishReleased(cell, level: lvl, stockAfter: Fish.TankCount)
  if Discover(lvl):                                      # true only the very first time (issue #19)
    publish FishDiscovered(lvl, FISH_DISCOVERY_COINS[lvl])
    Wallet.Apply(+FISH_DISCOVERY_COINS[lvl] FISH_COIN, FISH_DISCOVERY)   # 10 coins for level 1
  Quests.OnFishReleased()                                # 06
  return Ok(cell)
```

Rejections consume no draw. The draw happens only on success so `fish_spawn.Counter == Stats.FishReleased`, which 02 and 08 check. Random placement (rather than first empty cell) is deliberate: it spreads fish so that merges need a drag, which is the tactile part; with first-empty placement every release would land next to the last one and the board would read as a queue.

### 4.2 `MergeFish`

```
MergeFish(source, target):                    # cell indices 0..19
  if Combat.Mode == Dungeon:                             return Reject(Locked, "BOARD_LOCKED")     # issue #40
  if source == target or either index out of range:      return Reject(BadArgument, "BAD_CELL")   # client bug; Apply never throws (issue #11)
  a = Cells[source]; b = Cells[target]
  if a == 0 or b == 0:                                   return Reject(InvalidTarget, "EMPTY_CELL")
  if a != b:                                             return Reject(LevelMismatch, "LEVEL_MISMATCH")
  if a >= FishMaxLvl:                                    return Reject(Capped, "FISH_AT_LEVEL_CAP")
  n = a + 1
  Cells[target] = n; Cells[source] = 0; Fish.TotalMerges += 1; Stats.Merges += 1
  discovered = Discover(n)                               # true and pays FISH_DISCOVERY_COINS[n] the first time
  coins = discovered ? FISH_DISCOVERY_COINS[n] : FISH_MERGE_COINS_PCT * FISH_DISCOVERY_COINS[n]
  Wallet.Apply(+coins FISH_COIN, discovered ? FISH_DISCOVERY : FISH_MERGE)
  Combat.MissileQueue.Enqueue(FISH_MISSILE_PCT[n] / 100) # 03 section 2.5; fired on the next Fighting tick
  MarkDpsDirty()
  publish FishMerged(source, target, newLevel: n, coins, missilePct: FISH_MISSILE_PCT[n], discovered)
  if discovered: publish FishDiscovered(n, FISH_DISCOVERY_COINS[n])
  Quests.OnFishMerged(n)                                 # 06
  return Ok(n)

Discover(n):
  if Fish.DiscoveredLevels.Contains(n): return false
  Fish.DiscoveredLevels.Add(n); Fish.HighestLevel = max(Fish.HighestLevel, n); return true
```

Rejections use 01's `RejectCode` (`TankEmpty`, `BoardFull`, `LevelMismatch`, `InvalidTarget`, `Capped`, `BadArgument`, `Locked`) plus a reason key from this spec's closed list in `ApplyResult.Reason`, localized as `reject.<reason_lower>` (issue #11; lowercased by the client, `reject.tank_empty`, issue #248): `TANK_EMPTY`, `BOARD_FULL`, `LEVEL_MISMATCH`, `EMPTY_CELL`, `CELL_OCCUPIED`, `FISH_AT_LEVEL_CAP`, `BAD_CELL`, `BOARD_LOCKED` (issue #40). A rejection leaves `StateHash` unchanged and emits no events. Every `Wallet.Apply` of Fish Coins also adds to `Stats.FishCoinEarned`.

The merged fish lands in the **target** cell (where the finger let go), and the source empties. Adjacency is not required: any two equal fish on the board can be merged, which keeps the 20-cell board from jamming and keeps `Merge all` simple. A merge pays the full discovery value the first time a level is created and 2% of it on every repeat; both go through the Wallet with their own reason so analytics (06) can tell the one-off income from the recurring one.

### 4.3 At the level cap

Two fish at `FishMaxLvl` cannot merge (`Capped`, `FISH_AT_LEVEL_CAP`). They stay on the board and contribute their full `FISH_BOARD_DPS_FACTOR * FISH_MISSILE_PCT[cap]` each.

**Reachable ceiling** (issue #247). Building one level-`c` fish needs `c` cells at its peak (one fish each of levels `c-1, ..., 2` plus the pair of level-1 fish being merged), so with cap `c` on a board of `W * H` cells greedy play (`Release max` + `Merge all`) holds at most `W * H - c + 1` cap fish, plus one fish of each level `1..c-1`, which fills the board exactly. A board of 20 cap fish is not reachable by play. On the 20-cell board at cap 10 the ceiling is 11 cap fish plus one each of levels 1-9: 6,143 level-1 fish, `1,628 + 288 = 1,916%` passive, x20.2 on the Fish Board layer (`FISH_BOARD_DPS_FACTOR` is 0.77 so that this reachable board gives the intended ~x20; 00 decision 97). Each `RES_FISH_CAP` level lowers the cap-fish count by one and raises its worth x1.5. This ceiling is the Fish Board's finished state at the current Research level. From there the player has three moves: buy `RES_FISH_CAP` (03 section 6) and start merging pairs of cap fish into the new level, discard fish to make room (4.4), or do nothing and keep the DPS. The UI marks the cap fish with a cap badge (styling is 07's) and shows the `Research` shortcut when `Merge all` finds nothing to merge and the tank is not empty (07); before Research unlocks (Awakening rank 1, 09's `feature_unlocks`; issue #45) the shortcut shows the locked state and the discard hint instead.

### 4.4 `DiscardFish` (M0)

```
DiscardFish(cell):
  if Combat.Mode == Dungeon:                             return Reject(Locked, "BOARD_LOCKED")     # issue #40
  if cell out of range:                                  return Reject(BadArgument, "BAD_CELL")
  if Cells[cell] == 0:                                   return Reject(InvalidTarget, "EMPTY_CELL")
  n = Cells[cell]; Cells[cell] = 0; Stats.FishDiscards += 1
  coins = FISH_DISCARD_COINS_PCT (0.10) * FISH_DISCOVERY_COINS[n]
  Wallet.Apply(+coins FISH_COIN, FISH_MERGE)             # same reason as a repeat merge: it is salvage, not discovery
  MarkDpsDirty()
  publish FishDiscarded(cell, n, coins)
  return Ok(coins)
```

Discard is the escape hatch for a jammed board (section 11) and is reached by long-press on a fish, then `Release back to sea`. It pays 10% of the level's discovery value: a cap-10 fish is worth 512 coins, less than the 922 coins the 511 merges that built it already paid, so discarding is never a better coin source than merging, but it is enough that a player is not punished for making room. It is M0 because M0 has no Research: an attentive player reaches the cap-10 ceiling (section 4.3) on day 6 and the M0 bot runs fourteen.

### 4.5 `MoveFish` (MVP)

```
MoveFish(source, target):
  if Combat.Mode == Dungeon:                             return Reject(Locked, "BOARD_LOCKED")     # issue #40
  if source == target or either index out of range:      return Reject(BadArgument, "BAD_CELL")
  if Cells[source] == 0:                                 return Reject(InvalidTarget, "EMPTY_CELL")
  if Cells[target] != 0:                                 return Reject(InvalidTarget, "CELL_OCCUPIED")   # equal level is a MergeFish, not a move
  Cells[target] = Cells[source]; Cells[source] = 0
  publish FishMoved(source, target, Cells[target])
  return Ok()
```

A cosmetic reposition (00 decision 22): it consumes no stream, pays nothing, changes no stat and does not mark DPS dirty (board DPS does not depend on position). The client sends it when a fish is dropped on an empty cell (section 6).

## 5. Missiles and passive board DPS

### 5.1 Missiles

Every merge that creates a level-`n` fish enqueues one missile worth `FISH_MISSILE_PCT[n]` percent of one hero hit (section 4.2). 03 section 2.5 drains the queue on the next `Fighting` tick: no crit, no RNG, never lost, surplus discarded on a kill. The queue belongs to the stage fight: it is not drained while `Combat.Mode == Dungeon`, so missiles queued before a run wait for the stage fight to resume (issue #40). `Combat.MissileQueue` is persisted in the save (02), so a save taken while `Spawning` with missiles queued resumes and re-simulates identically (issue #7). The client draws the missile from the merged fish's cell to the enemy when it sees `FishMerged`, timed so it arrives about when the sim applies it (07; the sim does not wait for the animation).

`missileDpsPct` is rated-only (issue #15): it enters `Query.RatedDps` (`SCR_HERO`, layer shares, bots, the tools and 08's plausibility bound), never the tick loop's hero hit, because missiles already deal their damage as discrete hits. The sim does not keep a trailing average; if `SCR_HERO` needs a live number, the runtime/view-model keeps a 60 s rolling sum of `FishMissileHit`, outside the save and `StateHash`. Closed form for the bot and the tool, for a player merging at `f` level-1 fish per minute with every fish eventually merged up to level `cap`:

```
missileHitsPerFish(cap) = sum over n = 2..cap of FISH_MISSILE_PCT[n] / 100 * 2^-(n-1)      # each level-n merge is shared by 2^(n-1) released fish
                        = 0.05 * sum over n = 2..cap of 0.75^(n-1)
                        = 0.05 * 0.75 * (1 - 0.75^(cap-1)) / 0.25                          # 0.139 hits per fish at cap 10, 0.149 at cap 20 (limit 0.15)
missileDpsPct           = 100 * f * missileHitsPerFish(cap) / (60 * speedFactor)           # hits per minute from missiles over hero hits per minute
```

At Research 0 (`f = 10`, cap 10, speed 1): 1.39 hero hits per minute from missiles, `missileDpsPct = 2.3`. With `RES_TANK_REFILL` 10 (`f = 30`) and cap 15: 7.5%. Missiles are a visible reward, not a DPS layer; the board's passive is where the Fish Board earns its 15% share in 03 section 8.2.

### 5.2 Passive board DPS

```
FISH_BOARD_DPS_PCT = sum over occupied cells of BoardDpsPct[level] = sum of FISH_BOARD_DPS_FACTOR (0.77) * FISH_MISSILE_PCT[level]
fishMult_live      = 1 + FISH_BOARD_DPS_PCT / 100                      # in the tick loop's hero hit (03 section 7.1)
fishMult           = 1 + (FISH_BOARD_DPS_PCT + missileDpsPct) / 100    # rated: Query.RatedDps only (issue #15)
```

Recomputed when DPS is dirty after any board command, at most once per command, even `MergeAll` (section 6.1). Worked board states for the attentive player at Research 0 (section 8 explains the fish counts; regenerated with `tools/fish_model.py`, `fishMult` includes the 2.3% missile share while merging):

| When | Fish released so far | Board (levels) | `FISH_BOARD_DPS_PCT` | `fishMult` |
|---|---|---|---|---|
| End of first 10 minutes | 110 | 7, 6, 4, 3, 2 (binary of 110 = 64 + 32 + 8 + 4 + 2) | 43.9 + 29.2 + 13.0 + 8.7 + 5.8 = 100.5 | x2.0 |
| End of day 1 (2 h) | 1,200 | 10, 10, 8, 6, 5 | 148 + 148 + 66 + 29 + 19 = 410.5 | x5.1 |
| End of day 3 | 3,600 | 10 x7, 5 | 1,036 + 19 = 1,055 | x11.6 |
| End of day 5 | 6,000 | 10 x11, 9, 7, 6, 5 | 1,628 + 99 + 44 + 29 + 19 = 1,819 | x19.2 |
| Day 6 onward (reachable ceiling, section 4.3) | 6,143 | 10 x11, one each of 9..1 | 1,628 + 288 = 1,916 | x20.2 |
| After `RES_FISH_CAP` 1 and 5,120 more fish (cap-11 ceiling) | 11,263 | 11 x10, one each of 10..1 | 2,220 + 436 = 2,656 | x27.6 |
| Cap-12 ceiling | 20,479 | 12 x9, one each of 11..1 | 2,997 + 658 = 3,655 | x37.6 |

The idle contribution is the whole passive. A player who never merges still gets `fishMult = 1.385` from one `Release max` press at Research 0 (`TankCap` 10, 10 x 3.85%), `1.77` once two visits have filled the board's 20 cells, and nothing more until they merge (issue #19; final numbers from `tools/fish_model.py`). That is the floor the design accepts for pillar 1; the tool checks that the attentive and lazy policies in section 8 differ by at least x3 on this layer at day 7, so touching the board is worth it.

## 6. Interaction model (07 renders; this section is the contract)

Gestures on the board, in priority order when they could overlap:

| Gesture | Result | Command sent | Feedback |
|---|---|---|---|
| Drag a fish and release over a fish of the same level | merge into the target cell | `MergeFish(source, target)` on drop | source lifts and follows the finger (lift scale is 07's); valid targets pulse while dragging; on drop the two sprites snap together, splash VFX, coin number floats up, missile launches; haptic `MERGE` (light), `DISCOVER` (medium) when `FishDiscovered` follows |
| Drag and release over an empty cell | move the fish there | `MoveFish(source, target)` on drop (MVP; 00 decision 22) | the fish settles into the new cell; no haptic |
| Drag and release over a different-level fish | nothing | none | source springs back; the target cell shows the invalid-drop feedback (level mismatch; animation is 07's); haptic `INVALID` (soft) |
| Drag and release outside the board | nothing | none | spring back; no haptic |
| Tap a fish (no drag beyond `UI_DRAG_START_PX`) | select it; a second tap on an equal fish merges into the second | `MergeFish(first, second)` on the second tap | first fish shows a selection ring; tapping empty space or a different level clears the selection with the invalid feedback |
| Tap the Magic Tank | release one fish | `ReleaseFish()` | fish pops out of the tank and arcs to its cell; tank counter decrements; haptic `RELEASE` (light); on `TankEmpty` the tank wobbles and shows the refill countdown; on `BoardFull` the board edge flashes and a toast says "Board full: merge or release a fish" |
| Long-press a fish (`UI_LONG_PRESS_MS` = 350) | info popover: level, name, board DPS of this fish, merge count to next level, `Release back to sea (+N coins)` | `DiscardFish(cell)` only if the popover button is tapped | popover; haptic `SELECT` |
| `Release max` button | releases until the tank is empty or the board is full | `ReleaseMax()` (section 6.1) | fish pop out at `UI_RELEASE_MAX_ANIM_MS` intervals in the order the sim chose; the HUD counts; haptic once |
| `Merge all` button | merges every mergeable pair, lowest level first | `MergeAll()` (section 6.1) | merges animate in the sim's order at `UI_MERGE_ALL_ANIM_MS` intervals; coins and missiles per merge; haptic once per `FishDiscovered`, otherwise once at the end |
| Auto Merge toggle (P1) | runs `MergeAll` every `AUTO_MERGE_INTERVAL_S` and `ReleaseMax` when the tank is full | `SetAutoMerge(bool)` | toggle state in the tank widget |

Behaviour, commands, events and `UI_LONG_PRESS_MS` are this spec's contract; pure presentation (scales, shakes, badges, timings of animations) is 07's, and the UI constants live in 07's constants list (issue #18).

Rules: touch targets are the full cell (minimum 64 px on a 360 px wide layout, 07); drag starts after `UI_DRAG_START_PX` of movement so a tap never becomes a drag; while a drag is in progress the combat viewport keeps running and other board input is ignored; a merge during a boss fight is allowed and its missile hits the boss; during a dungeon run the board is hidden (07) and sends nothing (issue #40); `Merge all` and `Release max` are disabled (greyed, not hidden) when they would do nothing, with the reason as the button subtitle ("Tank empty", "Nothing to merge", "Board full").

### 6.1 Bulk commands

```
ReleaseMax():
  if Combat.Mode == Dungeon:                             return Reject(Locked, "BOARD_LOCKED")     # issue #40
  n = 0
  while ReleaseFish().Ok: n += 1                         # each release draws from fish_spawn as in 4.1
  return Ok(n)                                            # 0 is not a rejection; the client disables the button on 0

MergeAll():
  if Combat.Mode == Dungeon:                             return Reject(Locked, "BOARD_LOCKED")     # issue #40
  merges = 0
  loop:
    found = false
    for level in 1 .. FishMaxLvl - 1:
      cells = indices with Cells[i] == level, in row-major order
      while cells.Count >= 2:
        source = cells[0]; target = cells[1]; cells.RemoveRange(0, 2)
        MergeFish(source, target)                         # emits FishMerged and possibly FishDiscovered per merge
        merges += 1; found = true
    if not found: break                                   # repeat, because the new level-(n+1) fish may now pair up
  return Ok(merges)
```

`MergeAll` is fully determined by the board contents: lowest level first, leftmost pair first, result in the second cell. 09's content validator does not care, but 10's golden-file test does: a saved board plus `MergeAll` must give byte-identical boards on every platform. Each inner `MergeFish` marks DPS dirty; the flag is idempotent, so the observable rule is that DPS is recomputed at most once per command (issue #19).

### 6.2 Auto Merge (P1)

`AutoMergeEnabled` in `FishBoardState`, default false, unlocked by a one-time purchase or a P1 Research node (06 decides). While enabled and the app is in the foreground, the sim runs `MergeAll()` every `AUTO_MERGE_INTERVAL_S = 3` seconds of ticks and `ReleaseMax()` whenever `Fish.TankCount == TankCap`. It never discards. It does not run during a dungeon run (section 4) or during offline catch-up (the tank fills; the board does not change), so the active player keeps an edge: a 2 h session with Auto Merge equals the attentive policy; a login-only player with Auto Merge gets one tank of fish merged per session. Events are emitted exactly as for manual commands so the HUD animates them.

## 7. Persistence and offline

`FishBoardState` is part of `GameState` (02) and is saved on every autosave trigger; no board command is lost on a crash because commands are applied synchronously and the save is taken after `Advance`. `Combat.MissileQueue` is saved too (issue #7). Offline catch-up (06) uses `CatchUpTank` and touches nothing else on the board: fish do not merge, no missiles fire, no coins accrue, `DiscoveredLevels` does not change. Phases (issue #21, 00 contract 7): `Catchup.Compute` evaluates `CatchUpTank(creditedSeconds * TICK_HZ)` and stores the count in `Offline.Pending.TankFill` without changing `FishBoardState`; `ClaimOfflineReward` applies `TankCount = min(TankCap, TankCount + added)` and the refill progress per section 3. The live tank keeps ticking between compute and claim (bounded by `TankCap`). A gap shorter than `OFFLINE_MIN_S` opens no modal and credits nothing, but the tank still gets its refill for that gap: the catch-up system command (01, issue #6) applies the `CatchUpTank` result directly, so quick app switches lose no refill time. The offline reward modal (`SCR_OFFLINE_REWARD`, ticks paused while it is open) shows "Magic Tank refilled: +N fish" from `TankFill`. Save-schema notes for 02: `Cells` and `DiscoveredLevels` serialise as int arrays (02 owns the encoding; the migration fixture must cover it if it ever changes), `TankReleaseLevel` and `AutoMergeEnabled` present from v1 with defaults so P1 needs no migration.

## 8. Throughput math

### 8.1 Fish per minute

The tank yields `60 / TankRefillS` fish per active minute while the player keeps it below capacity:

| `RES_TANK_REFILL` level | `TankRefillS` | Fish / min | Fish / 2 h session | Days of 2 h to the cap-10 ceiling (6,143 fish, section 4.3) |
|---|---|---|---|---|
| 0 | 6.0 | 10.0 | 1,200 | 5.1 |
| 2 | 5.2 | 11.5 | 1,385 | 4.4 |
| 5 | 4.0 | 15.0 | 1,800 | 3.4 |
| 8 | 2.8 | 21.4 | 2,571 | 2.4 |
| 10 | 2.0 | 30.0 | 3,600 | 1.7 |

`RES_TANK_CAP` does not change throughput for an attentive player; it changes how much a lazy player gets per visit (one tank) and how long the attentive player can look away without wasting refill time (`TankCap * TankRefillS` seconds: 60 s at Research 0, 120 s at cap 60 / 2 s).

### 8.2 Merges per minute and the two policies

Every level-1 fish eventually causes about one merge (a level-`n` fish is `2^(n-1)` fish and `2^(n-1) - 1` merges), so merges per minute equal fish per minute for a player who keeps up: 10 at Research 0, 30 at `RES_TANK_REFILL` 10. With `Merge all` one press does them all, so the attentive policy is "press `Release max` and `Merge all` whenever the tank is full", one pair of taps a minute at Research 0, every two minutes at cap 60.

| Policy | Definition | Fish per day (Research 0) | Fish Coins per day at cap 10 (merges only) | `fishMult` at day 7 |
|---|---|---|---|---|
| Attentive | `ReleaseMax` + `MergeAll` every active minute for 120 min; discards the lowest fish when jammed below the ceiling | 1,200 (until the ceiling on day 6) | about 2,160 | x20.2 |
| Lazy | `ReleaseMax` + `MergeAll` once per login, two logins a day | 20 | about 36 | x1.87 (140 fish by day 7, binary merged: 8, 4, 3 -> 65.8 + 13.0 + 8.7 = 87.4%, grows slowly) |
| Never | nothing | 0 | 0 | x1.0 |

The lazy player's `fishMult` is x1.19 after the first login (10 fish merged: 4, 2) and about x1.87 after a week; the attentive player's is x20.2. That x10.8 gap at day 7 (inside the band but near its top; issue #19, final numbers from `tools/fish_model.py`) is the active-play edge the design wants on this layer, and the tool checks it stays between x3 and x12 (bigger and the board is mandatory; smaller and it is decoration).

### 8.3 Fish Coins per active minute

Per level-1 fish fully merged up to level `cap`:

```
coinsPerFish(cap) = sum over n = 2..cap of FISH_MERGE_COINS_PCT * FISH_DISCOVERY_COINS[n] / 2^(n-1) = 0.02 * 10 * (cap - 1) = 0.2 * (cap - 1)
```

1.8 coins per fish at cap 10, 2.8 at cap 15, 3.8 at cap 20. At Research 0: 18 coins per active minute, about 2,160 per 2 h day; at `RES_TANK_REFILL` 10 and cap 15: 84 per minute, about 10,000 per day. Discovery adds a one-off `10 * (2^cap - 1)` = 10,230 coins for the climb to level 10 (almost all on day 1-2), then 10,240 for level 11, 20,480 for level 12, each when the first fish of that level is made. Discards add nothing in steady state. The Fish Coin Dungeon (06) is the other income; the 03 bot assumes 1,000 a day from it, so the board is about two thirds of Fish Coin income at Research 0 and more later.

### 8.4 DPS per active minute

Each active minute at Research 0 adds 10 fish to the board's merge tree. The board's passive after `F` released fish (all merged as far as they go) is `FISH_BOARD_DPS_FACTOR (0.77) * sum of FISH_MISSILE_PCT over the levels in the binary representation of F`, which for large `F` is dominated by `floor(F / 512)` cap-10 fish at 148% each. So the attentive player gains about 148 percentage points of passive every 51 minutes, roughly +2.9 points of hero DPS per active minute, until the board reaches its ceiling (section 4.3) on day 6; then nothing until `RES_FISH_CAP`. Missiles add a steady 2.3% of DPS while merging (section 5.1).

## 9. `tools/fish_model.py`

A per-minute throughput model, closed-form where possible, with a board simulation for the policy comparison. Reads `fish_levels`, `tank`, `research` and `constants` from `content/*.json` (09); `--override` as in the other tools.

Inputs: `--days 30`, `--active-min 120`, `--sessions 2`, `--policy attentive|lazy|never|auto` (auto = P1 Auto Merge), `--research "RES_TANK_CAP=3,RES_TANK_REFILL=5,RES_FISH_CAP=2"` or `--research-schedule tools/out/power_curve_7.json` to take the 03 bot's actual research levels per day, `--dungeon-coins 1000`, `--seed 7` (placement does not affect throughput, so the seed only matters for the jam test).

Model: tank stock and refill per minute; board as a count per level; the policy's release/merge/discard rules from sections 6.1 and 8.2; coins and missiles per merge from section 2.2; `FISH_BOARD_DPS_PCT` per section 5.2; offline fill per section 3.

Outputs (printed and `tools/out/fish_<policy>.json`): per day, fish released, merges, discards, `FISH_BOARD_DPS_PCT`, `fishMult`, Fish Coins from discovery / merges / discards, highest fish level, board occupancy, minutes jammed; a policy comparison table (attentive vs lazy vs never) at days 1, 3, 7, 14, 30; the section 8.1 and 8.3 tables regenerated from content; a `FISHLINE day7 attentive x20.16 lazy x1.87 ratio 10.8` summary line for 03's report. `S` constants it validates: `TANK_CAP0`, `TANK_REFILL_S0` (attentive fish per day between 1,000 and 1,500 at Research 0), `FISH_MAX_LVL0` (the attentive board reaches the reachable ceiling of section 4.3, computed from the cap and the cell count, between day 6 and day 12; at Research 0 that is 11 cap fish plus one each of levels 1-9, reached on day 6), `FISH_DISCOVERY_COINS` and `FISH_MERGE_COINS_PCT` (day-1 Fish Coins between 8,000 and 14,000 including discovery; steady-state merge coins between 1,500 and 3,000 a day at cap 10), `FISH_MISSILE_PCT` and `FISH_BOARD_DPS_FACTOR` (0.77; attentive `fishMult` at day 7 between x12 and x25, x20.16 measured; attentive/lazy ratio between x3 and x12; `missileDpsPct` under 10% at every research level), `FISH_DISCARD_COINS_PCT` (discard coins under 10% of merge coins for the attentive policy), `RES_TANK_*` effects (jointly with 03's tool: `RES_TANK_REFILL` 10 at most triples fish per day), `AUTO_MERGE_INTERVAL_S` (auto policy within 10% of attentive).

## 10. Events

```csharp
public sealed record FishReleased(long Tick, int Cell, int Level, int StockAfter) : SimEvent(Tick);
public sealed record FishMerged(long Tick, int Source, int Target, int NewLevel, double Coins, double MissilePct, bool Discovered) : SimEvent(Tick);
public sealed record FishDiscovered(long Tick, int Level, double Coins) : SimEvent(Tick);
public sealed record FishDiscarded(long Tick, int Cell, int Level, double Coins) : SimEvent(Tick);
public sealed record FishMoved(long Tick, int Source, int Target, int Level) : SimEvent(Tick);      // MVP, MoveFish (4.5)
public sealed record TankFishReady(long Tick, int TankCount) : SimEvent(Tick);      // opt-in, off by default
public sealed record FishMissileHit(long Tick, double HitFraction) : SimEvent(Tick); // published by 03 when the queue drains
```

All of these except `FishMissileHit` (03's) are in 00 section 5.1's fish group; this spec owns their fields. There is no `FishMissileFired`: the client animates the missile from `FishMerged.MissilePct` and shows damage from `FishMissileHit` (issue #11). `CurrencyChanged` with reasons `FISH_DISCOVERY` and `FISH_MERGE` is published by the Wallet as for every delta.

## 11. Edge cases

| Case | Behaviour |
|---|---|
| Board full, tank has fish | `ReleaseFish` rejected `BoardFull`; tank keeps filling to cap and stops; `Release max` disabled with subtitle "Board full"; if `Merge all` would also do nothing the tank widget shows the Research shortcut (MVP) and the long-press discard hint. |
| Tank empty | `ReleaseFish` rejected `TankEmpty`; tank shows a countdown `TankRefillTicks - TankRefillTick` ticks; `Release max` disabled. |
| Jammed board: 20 fish, no mergeable pair, tank full | A full board is jammed iff every level below `FishMaxLvl` appears at most once (cap fish cannot merge, section 4.3). At cap `c`, a full board with no fish at level `c` always has a pair (pigeonhole: 20 cells over `c - 1` levels); with cap fish it can jam at any cap, for example 11 level-10 fish plus one each of levels 1-9 at cap 10, the reachable ceiling of section 4.3 (issue #17, issue #247), or 20 cap fish from a save or a lowered cap. Resolution is discard or Research; the bot discards the lowest fish. |
| Merge during a boss fight | Allowed. The missile lands on the boss on the next tick; a merge is often the thing that breaks a wall by a hair, which is the intended drama. |
| Merge or release while `Spawning` | Allowed; the missile waits in the queue for the next enemy (03 section 2.5). |
| App backgrounded mid-drag | The drag is client state; no command has been sent. On resume the client cancels the drag (spring back) and the board is whatever the save says. |
| Save mid-`MergeAll` | Impossible: commands are atomic with respect to `Advance` and saves (01). |
| Offline catch-up | Tank fills per `CatchUpTank` at claim (section 7); nothing else changes; `fish_spawn` draws nothing. |
| `RES_FISH_CAP` bought with two cap fish on the board | They become mergeable immediately; `Merge all` picks them up on its next pass; no retroactive discovery. |
| Content lowers `FISH_MAX_LVL0` or a research cap below a saved fish's level | The fish stays, keeps its board DPS, cannot merge (`Capped`; `MergeAll` skips it); the content validator (09) warns when a cap decreases between versions. Load and content reconcile (02, 09) clamp `Cells` only to `FISH_MAX_LVL_HARD` (the `fish_levels` row count, which 09 never shrinks), never to the reachable cap; nothing is refunded. `TankCount` is still clamped to `TankCap` (issue #41). |
| `Cells` length differs from `FISH_BOARD_W * FISH_BOARD_H` after a content change | Board dimensions are not content-tunable after v1 without a migration (02); the validator rejects a change to `FISH_BOARD_W/H` unless the save schema version also changes. |
| Stage select to a lower stage (MVP) | Missiles and passive apply to whatever enemy is live; nothing board-side depends on stage. |
| Dungeon run (MVP) | The board is hidden and frozen while `Combat.Mode == Dungeon` (06, 07): every board command is rejected `Locked` (reason `BOARD_LOCKED`), no missiles fire, no board coins accrue, Auto Merge pauses. The tank keeps refilling and the passive `FISH_BOARD_DPS_PCT` applies to the dungeon boss, so 06's clearability preview is exact (issue #40). |

## 12. Determinism rules

1. `fish_spawn` is drawn only in a successful `ReleaseFish`, exactly once (`MoveFish`, `MergeFish`, `DiscardFish` and every rejection draw nothing), and `fish_spawn.Counter == Stats.FishReleased` is a `Validate()` invariant (02).
2. The k-th empty cell is found by a row-major scan; no hash sets, no platform-dependent iteration order.
3. `MergeAll` order is fixed by section 6.1; `ReleaseMax` is a loop of `ReleaseFish`.
4. Coins and missile values come from the `fish_levels` table rows, never recomputed with `Math.Pow`; `FISH_DISCOVERY_COINS` doubling is pre-evaluated by the exporter (09).
5. DPS is recomputed at most once per board command (the dirty flag is idempotent); `Query.FishBoardDpsPct` sums the table values over `Cells` in index order (floating-point sum order fixed).
6. Event order within a command: `FishMerged` (or `FishReleased`) then `FishDiscovered` then the `CurrencyChanged` from the Wallet; tests assert it.
7. The client never reads `Cells` to decide a gesture's validity ahead of the sim; it sends the command and animates from the result and events, so a stale view cannot produce a divergent board.

## 13. Analytics events

This section owns these events' fields (issue #245); every event also carries 02 section 14.1's envelope (which includes `dayIndex`), and 02 section 14.4 indexes them.

| Event | Fields | When |
|---|---|---|
| `fish_release` | `count`, `stockBefore`, `stockAfter`, `boardOccupancyAfter`, `viaButton` (bool) | per `ReleaseFish` or once per `ReleaseMax` with the count |
| `fish_merge` | `count`, `levelsCreated[]` (histogram of `NewLevel`), `coins`, `discoveries[]`, `viaButton` (bool), `duringBoss` (bool) | per `MergeFish` or once per `MergeAll` |
| `fish_discovered` | `level`, `coins` | per `FishDiscovered` |
| `fish_discard` | `level`, `coins`, `boardOccupancyBefore` | per `DiscardFish` |
| `fish_board_snapshot` | `cells[]`, `fishBoardDpsPct`, `fishMult`, `tankCap`, `tankRefillS`, `fishMaxLvl`, `autoMerge`, and session totals `releases`, `merges`, `discoveries`, `coinsEarned`, `activeSec` | Runtime, at session end (replaces the former `fish_session`; throughput for `tools/fish_model.py`; issue #11) |
| `currency_changed` | 06 section 13, reasons `FISH_DISCOVERY`, `FISH_MERGE` | per wallet delta |

`MoveFish` has no analytics event: it is a cosmetic reposition (section 4.5) and `fish_board_snapshot` already records where fish end up.

Questions the dashboard should answer (10): share of sessions with at least one merge, merges per active minute distribution (attentive vs lazy in the wild), day index of first level-10 fish, `fishMult` percentile bands at day 7 versus the tool's x12-x25, discard rate, jam rate.

## 14. Open questions

1. **Random placement versus first empty cell.** Default: random (section 4.1). If playtests find the drag distance annoying, switch to "nearest empty cell to the tank" with the same single draw used for tie-breaks, which keeps the counter invariant.
2. **Adjacency-free merging.** Default: any two equal fish anywhere. A 5 x 4 board with adjacency rules jams constantly; if the design wants spatial puzzle feel, that is a different (and bigger) board.
3. **Discard payout 10%.** Default: keep; the tool checks it stays under 10% of merge income for the attentive policy.
4. **Auto Merge unlock** (purchase versus Research node) and whether it should run during offline catch-up (this spec says no, to preserve the active edge). Owner: 06. Default: Research node, no offline.
5. **Board size is not content-tunable** after v1 without a migration. Owner: 02. Default: fixed at 5 x 4; `FISH_BOARD_W/H` stay in content for the validator's sake only.
6. **Missile animation timing versus sim application.** The sim applies on the next tick; the animation takes about 400 ms. Owner: 07. Default: the HP bar on the viewport lags the sim by one animation frame; damage numbers are shown when the missile lands, read from the event.
7. **Should cap fish auto-convert** (two cap fish merge into coins and a big missile) instead of refusing? Resolved: refuse (issue #17); a board full of cap fish is a visible "buy Research" prompt and the passive is the reward for getting there, and discard stays M0 as the escape hatch.

## 15. Acceptance criteria

1. `TickTank` from an empty tank at Research 0 reaches `TankCount == 1` at exactly tick 60 and `TankCount == 10` at tick 600, then stops advancing `TankRefillTick`; `CatchUpTank(36000)` from `TankCount 3, TankRefillTick 17` gives `added 7` (to `TankCap`) and refill tick 0; `CatchUpTank(125)` from `TankCount 0, TankRefillTick 0` gives `added 2`, refill tick 5.
2. `ReleaseFish` with `TankCount 0` is rejected `TankEmpty` and draws nothing; with a full board rejected `BoardFull` and draws nothing; otherwise draws exactly once and places a level-1 fish in the k-th empty cell for `u` in {0.0, 0.5, 1 - 1e-12} with 7 empty cells giving k = 0, 3, 6.
3. The first `ReleaseFish` on a fresh profile pays 10 Fish Coins with reason `FISH_DISCOVERY`, adds 1 to `DiscoveredLevels` and publishes `FishReleased` then `FishDiscovered(1, 10)`; the second pays nothing and publishes no `FishDiscovered`.
4. `MergeFish` of two level-3 fish creates level 4 in the target cell, empties the source, pays 80 coins (`FISH_DISCOVERY`) the first time and 1.6 coins (`FISH_MERGE`) thereafter, enqueues a missile of 0.169 hit fractions, publishes `FishMerged` then `FishDiscovered` (first time only); `MergeFish` of levels 3 and 4 is rejected `LevelMismatch`; of two cap fish `Capped` (reason `FISH_AT_LEVEL_CAP`); of an empty cell `InvalidTarget` (reason `EMPTY_CELL`); same cell twice or an out-of-range index `BadArgument` (reason `BAD_CELL`), without throwing; every rejection leaves `StateHash` unchanged and emits no events.
5. `MergeAll` on the board `[1,1,1,1,2,2,0,...]` merges cells (0->1) and (2->3) into level 2, then (1->3) and (4->5) into level 3, then (3->5) into level 4; the final board has a single level-4 fish in cell 5, `merges == 5`, and the event sequence is five `FishMerged` in that order; the result is byte-identical on x64, ARM64 and IL2CPP.
6. `ReleaseMax` with `TankCount 10` and 7 empty cells releases 7 and returns 7; with `TankCount 3` releases 3.
7. `DiscardFish` on a level-10 fish pays 512 coins with reason `FISH_MERGE`, empties the cell and publishes `FishDiscarded`; on an empty cell rejected `InvalidTarget` (reason `EMPTY_CELL`). `MoveFish` to an empty cell moves the fish, publishes `FishMoved`, draws nothing and leaves `FISH_BOARD_DPS_PCT` unchanged; to an occupied cell it is rejected `InvalidTarget` (reason `CELL_OCCUPIED`).
8. `Query.FishBoardDpsPct` for the board `[10,10,8,6,5,0...]` equals `FISH_BOARD_DPS_FACTOR * (192.2 + 192.2 + 85.4 + 38.0 + 25.3)` from the table within 1e-9 (`148.0 + 148.0 + 65.8 + 29.2 + 19.5 = 410.5` at 0.77), and `fishMult` with `missileDpsPct 0` equals 5.105.
9. A missile queued during `Spawning` applies on the first `Fighting` tick of the next enemy; one queued during a boss fight hits the boss; `crit.Counter` is unchanged by missiles.
10. `fish_spawn.Counter == Stats.FishReleased` after a 200,000-command fuzz run mixing all six board commands; `Validate()` never throws.
11. Offline: for a 3 h gap, `Catchup.Compute` changes nothing in `FishBoardState` and stores `Offline.Pending.TankFill`; `ClaimOfflineReward` changes only `TankCount` and `TankRefillTick`; `Cells`, `DiscoveredLevels`, the Wallet's Fish Coins and `fish_spawn.Counter` are unchanged by both. A gap under `OFFLINE_MIN_S` opens no modal and still advances the tank refill.
12. A property test over 10,000 random full boards with levels in `1..cap-1` finds `MergeAll` returns >= 1 (pigeonhole); a board of 20 cap fish gives `MergeAll == 0` and the bot discards (issue #17).
13. `tools/fish_model.py --policy attentive` reports between 1,000 and 1,500 fish on day 1 at Research 0, the board at its reachable ceiling (section 4.3: 11 cap fish plus one each of levels 1-9 at cap 10) between day 6 and day 12, `fishMult` at day 7 between x12 and x25; `--policy lazy` reports `fishMult` at day 7 between x1.5 and x3; the ratio is between x3 and x12.
14. `tools/fish_model.py` and the C# `HeroCat.Bots` fish policy agree on day-7 `fishMult` within 5% for the attentive policy (10's cross-check).
15. PlayMode (07): dragging a fish onto an equal fish sends exactly one `MergeFish(source, target)` with `target` the cell under the release point; dropping on a mismatched fish sends nothing and plays the spring-back; dropping on an empty cell sends one `MoveFish(source, target)`; a tap-tap on two equal fish sends one `MergeFish`; `Merge all` with nothing to merge is disabled and shows "Nothing to merge".
16. With `Combat.Mode == Dungeon`, each of `ReleaseFish`, `ReleaseMax`, `MergeFish`, `MergeAll`, `MoveFish` and `DiscardFish` is rejected `Locked` (reason `BOARD_LOCKED`) even when its arguments are also invalid, leaves `StateHash` unchanged and emits no events; `TickTank` still advances the tank, `Query.FishBoardDpsPct` is unchanged, and a missile queued before the run is still queued when `Combat.Mode = Stage` resumes (issue #40).
17. Loading a save with a level-12 fish under content whose `FishMaxLvl` is 10 keeps the fish at level 12 and its board DPS; `MergeFish` of two such fish is rejected `Capped` (reason `FISH_AT_LEVEL_CAP`) and `MergeAll` ignores them; a saved level above `FISH_MAX_LVL_HARD` is clamped to it (issue #41).

## 16. Constants introduced

| Name | Value | Kind | Section | Note |
|---|---|---|---|---|
| `TANK_REFILL_FLOOR_S` | 2.0 | S | 3 | Not a `constants` row: 09's `tank.refill_floor_s`, equal to `research[res_tank_refill].effect_floor` (issue #19). |
| `RES_TANK_CAP_PER_LVL` | 5 | S | 3 | Not a `constants` row: 09's `research[res_tank_cap].effect_per_level`; priced in 03 section 6. |
| `RES_TANK_REFILL_PER_LVL` | 0.4 s | S | 3 | Not a `constants` row: 09's `research[res_tank_refill].effect_per_level`; priced in 03 section 6. |
| `Fish.TankReleaseLevel` | 1 (MVP; P1 upgrades) | S | 3 | State field (02) holding the upgrade level bought; what each level releases is content, base `tank.release_level` = 1 (issue #7). |
| `FISH_DISCARD_COINS_PCT` | 0.10 | S | 4.4 | Of the level's discovery value. |
| `BoardDpsPct[n]` | `FISH_BOARD_DPS_FACTOR (0.77) * FISH_MISSILE_PCT[n]` | S | 5.2 | Derived `fish_levels` column (09); the factor is 00's `FISH_BOARD_DPS_FACTOR` (issue #19; 0.77 since issue #247). |
| `FISH_MAX_LVL_HARD` | 20 | S | 2.2 | `fish_levels` ships 20 rows; `RES_FISH_CAP` max 10. The only clamp applied to saved `Cells` (issue #41). |
| `AUTO_MERGE_INTERVAL_S` (P1) | 3 | S | 6.2 | |
| `UI_DRAG_START_PX`, `UI_LONG_PRESS_MS` | 8, 350 | - | 6 | Defined in 07's constants list (issue #18); `UI_LONG_PRESS_MS` is behaviour, so its value is this spec's. |
| `UI_MERGE_ALL_ANIM_MS`, `UI_RELEASE_MAX_ANIM_MS` | 80, 60 | - | 6 | Defined in 07's constants list (issue #18). |
| Haptic ids | `MERGE`, `DISCOVER`, `INVALID`, `RELEASE`, `SELECT` | - | 6 | For 07's haptics map. |
| `fish_levels` rows | 20 rows per section 2.2 | S | 2.2 | Table, generated from the 00 formulas by the exporter. |
