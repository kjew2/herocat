## 05 - Fish Merge Minigame

This spec owns the bottom half of `SCR_MAIN`: the `FishBoard` grid and its cells, the `Fish` data per level, the `MagicTank` that produces level-1 fish on a tick-driven timer, the `ReleaseFish` / `MergeFish` / `DiscardFish` commands and their events, Fish Coin income from discovery and merges, the fish missiles that feed damage into 03's tick loop, the passive `FISH_BOARD_DPS_PCT`, the whole touch interaction model, the throughput math, `tools/fish_model.py`, persistence and offline behaviour, edge cases and analytics. Research nodes that upgrade the tank and the level cap are priced in 03 section 6; this spec says what they do.

Tiers covered: M0 (board, tank, release, merge, discard, discovery, missiles, passive DPS, merge-all and release-max buttons), MVP (Research upgrades, analytics, haptics), P1 (Auto Merge, tank level upgrades).

Depends on: 00 (section 6.4 constants), 02 (`FishBoardState` persistence), 03 (tick loop hooks `TickTank`, `MissileQueue`, `MarkDpsDirty`, Research effects), 06 (offline catch-up calls `CatchUpTank`), 07 (rendering and gesture recognition).

## 1. Design goals

1. The board is the thing the thumb does while the cat fights. Every merge should pay out in three ways at once: a coin number, a missile crossing the screen, and a bigger fish that raises the passive DPS line.
2. It is never mandatory. A player who only presses `Release max` and `Merge all` once per session still progresses; one who never touches it still gets the passive DPS of whatever is on the board.
3. It is deterministic. Cell placement is the only random element and it comes from the `fish_spawn` stream; merge results, coins and missiles are pure functions of levels.
4. It is tiny in the sim: 20 bytes of board, a stock count and a refill timer. Everything else is presentation.

## 2. Board and fish data

### 2.1 State

```csharp
namespace HeroCat.Sim.State
{
    public sealed class FishBoardState
    {
        public byte[] Cells;              // length FISH_BOARD_W * FISH_BOARD_H = 20; 0 = empty, n >= 1 = fish of level n; index = y * FISH_BOARD_W + x
        public MagicTankState Tank;
        public uint DiscoveredMask;       // bit (n-1) set once a level-n fish has ever been created; drives FishDiscovered and the fish book
        public long LifetimeReleases, LifetimeMerges, LifetimeDiscards;
        public double LifetimeFishCoins;  // discovery + merge + discard, for the server plausibility check (08)
    }

    public sealed class MagicTankState
    {
        public int Stock;                 // 0 .. TankCap
        public int RefillProgressTicks;   // 0 .. TankRefillTicks-1; only advances while Stock < TankCap
    }
}
```

`FISH_BOARD_W = 5`, `FISH_BOARD_H = 4` (00 section 6.4). The grid is drawn with (0,0) top-left; row-major order is also the order every "first empty cell" or "merge all" rule walks, so results do not depend on rendering.

### 2.2 Fish levels

One row per level in the `fish_levels` table (09). Values are the 00 formulas evaluated; the table exists so that the sheet can override a level (for example to make level 10 a visible milestone) without the code knowing.

| Level `n` | `FISH_DISCOVERY_COINS[n]` = `10 * 2^(n-1)` | `FISH_MERGE_COINS[n]` = 2% of discovery | `FISH_MISSILE_PCT[n]` = `5 * 1.5^(n-1)` (% of one hero hit) | Board DPS per fish = `0.5 * missile` (%) | Level-1 fish consumed |
|---|---|---|---|---|---|
| 1 | 10 | - | - (never fired: level 1 is released, not merged) | 2.5 | 1 |
| 2 | 20 | 0.4 | 7.5 | 3.75 | 2 |
| 3 | 40 | 0.8 | 11.25 | 5.6 | 4 |
| 4 | 80 | 1.6 | 16.9 | 8.4 | 8 |
| 5 | 160 | 3.2 | 25.3 | 12.7 | 16 |
| 6 | 320 | 6.4 | 38.0 | 19.0 | 32 |
| 7 | 640 | 12.8 | 57.0 | 28.5 | 64 |
| 8 | 1,280 | 25.6 | 85.4 | 42.7 | 128 |
| 9 | 2,560 | 51.2 | 128 | 64.1 | 256 |
| 10 | 5,120 | 102 | 192 | 96.1 | 512 |
| 11 | 10,240 | 205 | 288 | 144 | 1,024 |
| 12 | 20,480 | 410 | 432 | 216 | 2,048 |
| 13 | 40,960 | 819 | 648 | 324 | 4,096 |
| 14 | 81,920 | 1,638 | 973 | 486 | 8,192 |
| 15 | 163,840 | 3,277 | 1,459 | 730 | 16,384 |

Columns the table also carries: `name_key`, `sprite`, `splash_vfx`, `rarity_colour` (presentation), `missile_vfx`. Levels 1-10 exist at Research 0 (`FISH_MAX_LVL0 = 10`); each `RES_FISH_CAP` level opens one more, up to 20. The table ships all 20 rows; the cap decides which are reachable.

Two reading aids. Missile damage grows x1.5 per level while the fish it takes to make one grows x2, so per level-1 fish the missile value of merging higher is `(0.75)^(n-1)`: merging is for the board DPS and the discovery payout, not for the missiles. Discovery coins double per level and the fish needed double too, so discovery pays a constant 10 coins per level-1 fish on the first climb to any level, which is why the first session feels rich and why `RES_FISH_CAP` opening level 11 is a 10,240-coin event.

## 3. Magic Tank

```
TankCap          = TANK_CAP0 (10) + RES_TANK_CAP_PER_LVL (5) * research[RES_TANK_CAP]              # 10 .. 60
TankRefillS      = max(TANK_REFILL_FLOOR_S (2.0), TANK_REFILL_S0 (6.0) - RES_TANK_REFILL_PER_LVL (0.4) * research[RES_TANK_REFILL])
TankRefillTicks  = round(TankRefillS * TICK_HZ)                                                      # 60 .. 20 ticks, integer

TickTank():                                   # called by 03's Tick() each tick, before combat
  if Tank.Stock >= TankCap: Tank.RefillProgressTicks = 0; return
  Tank.RefillProgressTicks += 1
  if Tank.RefillProgressTicks >= TankRefillTicks:
    Tank.RefillProgressTicks -= TankRefillTicks; Tank.Stock += 1
    publish TankFishReady(Tank.Stock)         # opt-in event; the HUD reads Stock from the snapshot instead

CatchUpTank(elapsedTicks):                    # called once by 06's offline catch-up, closed form, no per-tick loop
  if Tank.Stock >= TankCap: Tank.RefillProgressTicks = 0; return
  total = Tank.RefillProgressTicks + elapsedTicks
  gained = total / TankRefillTicks                                   # integer division
  Tank.Stock = min(TankCap, Tank.Stock + gained)
  Tank.RefillProgressTicks = Tank.Stock >= TankCap ? 0 : total % TankRefillTicks
```

The timer runs on sim ticks, not wall time, so it advances exactly as fast as combat does and offline catch-up can fill it with the same arithmetic 06 uses for gold (`elapsedTicks = creditedSeconds * TICK_HZ`). `OFFLINE_MIN_S` and the 24 h cap apply to the tank too: a player back after a weekend finds a full tank, not three days of fish. At Research 0 the tank is full one minute after it empties; with `RES_TANK_CAP` 10 and `RES_TANK_REFILL` 10 it holds 60 fish and refills in two minutes. Progress while the tank is full is discarded, which is the whole reason to release fish before closing the app.

P1: tank level upgrades (released fish start at level 2, then 3) would halve the taps per coin; reserved as `TankReleaseLevel` in `MagicTankState` with default 1, not in MVP content.

## 4. Commands

### 4.1 `ReleaseFish`

```
ReleaseFish():                                # one fish per command; the Release max button loops it
  if Tank.Stock == 0:                                    return Rejected(TankEmpty)
  empties = count of Cells == 0
  if empties == 0:                                       return Rejected(BoardFull)
  u = fish_spawn.NextDouble()                            # exactly one draw per successful release
  k = (int)(u * empties)                                 # 0 .. empties-1
  cell = index of the k-th empty cell in row-major order
  Cells[cell] = TankReleaseLevel (1 in MVP); Tank.Stock -= 1; Lifetime.Releases += 1
  Discover(1, viaRelease: true)                          # pays FISH_DISCOVERY_COINS[1] = 10 the very first time, then nothing
  MarkDpsDirty()                                         # a level-1 fish adds 2.5% board DPS
  publish FishReleased(cell, level: 1, stockAfter: Tank.Stock)
  Quests.OnFishReleased()                                # 06
  return Ok(cell)
```

Rejections consume no draw. The draw happens only on success so `fish_spawn.Counter == LifetimeReleases`, which 02 and 08 check. Random placement (rather than first empty cell) is deliberate: it spreads fish so that merges need a drag, which is the tactile part; with first-empty placement every release would land next to the last one and the board would read as a queue.

### 4.2 `MergeFish`

```
MergeFish(source, target):                    # cell indices 0..19
  if source == target or either index out of range:      throw ArgumentOutOfRange (client bug)
  a = Cells[source]; b = Cells[target]
  if a == 0 or b == 0:                                   return Rejected(EmptyCell)
  if a != b:                                             return Rejected(LevelMismatch)
  if a >= FishMaxLvl:                                    return Rejected(FishAtLevelCap)
  n = a + 1
  Cells[target] = n; Cells[source] = 0; Lifetime.Merges += 1
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
  bit = 1u << (n - 1)
  if (DiscoveredMask & bit) != 0: return false
  DiscoveredMask |= bit; return true
```

The merged fish lands in the **target** cell (where the finger let go), and the source empties. Adjacency is not required: any two equal fish on the board can be merged, which keeps the 20-cell board from jamming and keeps `Merge all` simple. A merge pays the full discovery value the first time a level is created and 2% of it on every repeat; both go through the Wallet with their own reason so analytics (06) can tell the one-off income from the recurring one.

### 4.3 At the level cap

Two fish at `FishMaxLvl` cannot merge (`FishAtLevelCap`). They stay on the board and contribute their full `0.5 * FISH_MISSILE_PCT[cap]` each. A board of 20 cap fish is the Fish Board's finished state at the current Research level: 1,920% passive at cap 10, x20.2 on the Fish Board layer. From there the player has three moves: buy `RES_FISH_CAP` (03 section 6) and start merging pairs of cap fish into the new level, discard fish to make room (4.4), or do nothing and keep the DPS. The UI shows the cap fish with a lock badge and the `Research` shortcut when `Merge all` finds nothing to merge and the tank is not empty (07).

### 4.4 `DiscardFish` (MVP)

```
DiscardFish(cell):
  if Cells[cell] == 0:                                   return Rejected(EmptyCell)
  n = Cells[cell]; Cells[cell] = 0; Lifetime.Discards += 1
  coins = FISH_DISCARD_COINS_PCT (0.10) * FISH_DISCOVERY_COINS[n]
  Wallet.Apply(+coins FISH_COIN, FISH_MERGE)             # same reason as a repeat merge: it is salvage, not discovery
  MarkDpsDirty()
  publish FishDiscarded(cell, n, coins)
  return Ok(coins)
```

Discard is the escape hatch for a jammed board (section 11) and is reached by long-press on a fish, then `Release back to sea`. It pays 10% of the level's discovery value: a cap-10 fish is worth 512 coins, less than the 922 coins the 511 merges that built it already paid, so discarding is never a better coin source than merging, but it is enough that a player is not punished for making room. It is M0 because M0 has no Research: an attentive player fills the board with 20 cap fish in about nine days and the M0 bot runs fourteen.

## 5. Missiles and passive board DPS

### 5.1 Missiles

Every merge that creates a level-`n` fish enqueues one missile worth `FISH_MISSILE_PCT[n]` percent of one hero hit (section 4.2). 03 section 2.5 drains the queue on the next `Fighting` tick: no crit, no RNG, never lost, surplus discarded on a kill. The client draws the missile from the merged fish's cell to the enemy when it sees `FishMerged`, timed so it arrives about when the sim applies it (07; the sim does not wait for the animation).

The rated contribution for the power formula is the trailing average `missileDpsPct`. Closed form for the bot and the tool, for a player merging at `f` level-1 fish per minute with every fish eventually merged up to level `cap`:

```
missileHitsPerFish(cap) = sum over n = 2..cap of FISH_MISSILE_PCT[n] / 100 * 2^-(n-1)      # each level-n merge is shared by 2^(n-1) released fish
                        = 0.05 * sum over n = 2..cap of 0.75^(n-1)
                        = 0.05 * 0.75 * (1 - 0.75^(cap-1)) / 0.25                          # 0.139 hits per fish at cap 10, 0.149 at cap 20 (limit 0.15)
missileDpsPct           = 100 * f * missileHitsPerFish(cap) / (60 * speedFactor)           # hits per minute from missiles over hero hits per minute
```

At Research 0 (`f = 10`, cap 10, speed 1): 1.39 hero hits per minute from missiles, `missileDpsPct = 2.3`. With `RES_TANK_REFILL` 10 (`f = 30`) and cap 15: 7.5%. Missiles are a visible reward, not a DPS layer; the board's passive is where the Fish Board earns its 15% share in 03 section 8.2.

### 5.2 Passive board DPS

```
FISH_BOARD_DPS_PCT = sum over occupied cells of FISH_BOARD_DPS_PER_FISH[level] = sum of 0.5 * FISH_MISSILE_PCT[level]
fishMult           = 1 + (FISH_BOARD_DPS_PCT + missileDpsPct) / 100
```

Recomputed on `MarkDpsDirty` after any board command (once per command, even `MergeAll`). Worked board states for the attentive player at Research 0 (section 8 explains the fish counts):

| When | Fish released so far | Board (levels) | `FISH_BOARD_DPS_PCT` | `fishMult` |
|---|---|---|---|---|
| End of first 10 minutes | 110 | 7, 5, 4, 3, 2, 1 (binary of 110, plus one unmerged) | 28.5 + 12.7 + 8.4 + 5.6 + 3.75 + 2.5 = 61 | x1.6 |
| End of day 1 (2 h) | 1,200 | 10, 10, 8, 6, 5 | 96 + 96 + 43 + 19 + 13 = 267 | x3.7 |
| End of day 3 | 3,600 | 10 x7, 5 | 673 + 13 = 686 | x7.9 |
| End of day 7 | 8,400 | 10 x16, 8, 7, 5 | 1,538 + 43 + 28 + 13 = 1,622 | x17.2 |
| Day 9 onward (full) | >= 10,240 | 10 x20 | 1,922 | x20.2 |
| After `RES_FISH_CAP` 1 and 10,240 more fish | | 11 x10, 10 x10 | 1,441 + 961 = 2,402 | x25 |
| Cap 12 board, 20 fish | 40,960 | 12 x20 | 4,322 | x44 |

The idle contribution is the whole passive. A player who never merges still gets `fishMult = 1.5` from 20 level-1 fish released by `Release max` once (20 x 2.5%), and nothing more until they merge. That is the floor the design accepts for pillar 1; the tool checks that the attentive and lazy policies in section 8 differ by at least x3 on this layer at day 7, so touching the board is worth it.

## 6. Interaction model (07 renders; this section is the contract)

Gestures on the board, in priority order when they could overlap:

| Gesture | Result | Command sent | Feedback |
|---|---|---|---|
| Drag a fish and release over a fish of the same level | merge into the target cell | `MergeFish(source, target)` on drop | source lifts and follows the finger at 1.1x scale; valid targets pulse while dragging; on drop the two sprites snap together, splash VFX, coin number floats up, missile launches; haptic `MERGE` (light), `DISCOVER` (medium) when `FishDiscovered` follows |
| Drag and release over a different-level fish or an empty cell | nothing | none | source springs back over 120 ms; the target cell flashes red once if it was a fish (level mismatch); haptic `INVALID` (soft) |
| Drag and release outside the board | nothing | none | spring back; no haptic |
| Tap a fish (no drag beyond 8 px) | select it; a second tap on an equal fish merges into the second | `MergeFish(first, second)` on the second tap | first fish shows a selection ring; tapping empty space or a different level clears the selection with the red flash |
| Tap the Magic Tank | release one fish | `ReleaseFish()` | fish pops out of the tank and arcs to its cell; tank counter decrements; haptic `RELEASE` (light); on `TankEmpty` the tank wobbles and shows the refill countdown; on `BoardFull` the board edge flashes and a toast says "Board full: merge or release a fish" |
| Long-press a fish (350 ms) | info popover: level, name, board DPS of this fish, merge count to next level, `Release back to sea (+N coins)` | `DiscardFish(cell)` only if the popover button is tapped | popover; haptic `SELECT` |
| `Release max` button | releases until the tank is empty or the board is full | `ReleaseMax()` (section 6.1) | fish pop out at 60 ms intervals in the order the sim chose; the HUD counts; haptic once |
| `Merge all` button | merges every mergeable pair, lowest level first | `MergeAll()` (section 6.1) | merges animate in the sim's order at 80 ms intervals; coins and missiles per merge; haptic once per `FishDiscovered`, otherwise once at the end |
| Auto Merge toggle (P1) | runs `MergeAll` every `AUTO_MERGE_INTERVAL_S` and `ReleaseMax` when the tank is full | `SetAutoMerge(bool)` | toggle state in the tank widget |

Rules: touch targets are the full cell (minimum 64 px on a 360 px wide layout, 07); drag starts after 8 px of movement so a tap never becomes a drag; while a drag is in progress the combat viewport keeps running and other board input is ignored; a merge during a boss fight is allowed and its missile hits the boss; `Merge all` and `Release max` are disabled (greyed, not hidden) when they would do nothing, with the reason as the button subtitle ("Tank empty", "Nothing to merge", "Board full").

### 6.1 Bulk commands

```
ReleaseMax():
  n = 0
  while ReleaseFish().Ok: n += 1                         # each release draws from fish_spawn as in 4.1
  return Ok(n)                                            # 0 is not a rejection; the client disables the button on 0

MergeAll():
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

`MergeAll` is fully determined by the board contents: lowest level first, leftmost pair first, result in the second cell. 09's content validator does not care, but 10's golden-file test does: a saved board plus `MergeAll` must give byte-identical boards on every platform. `MergeAll` calls `MarkDpsDirty` once at the end, not per merge.

### 6.2 Auto Merge (P1)

`AutoMergeEnabled` in `FishBoardState`, default false, unlocked by a one-time purchase or a P1 Research node (06 decides). While enabled and the app is in the foreground, the sim runs `MergeAll()` every `AUTO_MERGE_INTERVAL_S = 3` seconds of ticks and `ReleaseMax()` whenever `Tank.Stock == TankCap`. It never discards. It does not run during offline catch-up (the tank fills; the board does not change), so the active player keeps an edge: a 2 h session with Auto Merge equals the attentive policy; a login-only player with Auto Merge gets one tank of fish merged per session. Events are emitted exactly as for manual commands so the HUD animates them.

## 7. Persistence and offline

`FishBoardState` is part of `GameState` (02) and is saved on every autosave trigger; no board command is lost on a crash because commands are applied synchronously and the save is taken after `Advance`. Offline catch-up (06) calls `CatchUpTank(elapsedTicks)` and nothing else on the board: fish do not merge, no missiles fire, no coins accrue, `DiscoveredMask` does not change. The offline reward modal (`SCR_OFFLINE_REWARD`) shows "Magic Tank refilled: +N fish" from the `Stock` delta. Save-schema notes for 02: `Cells` serialises as a 20-byte base64 string or a 20-element int array (02 picks; the migration fixture must cover both if it ever changes), `DiscoveredMask` as an unsigned int, `TankReleaseLevel` and `AutoMergeEnabled` present from v1 with defaults so P1 needs no migration.

## 8. Throughput math

### 8.1 Fish per minute

The tank yields `60 / TankRefillS` fish per active minute while the player keeps it below capacity:

| `RES_TANK_REFILL` level | `TankRefillS` | Fish / min | Fish / 2 h session | Days of 2 h to a full cap-10 board (10,240 fish) |
|---|---|---|---|---|
| 0 | 6.0 | 10.0 | 1,200 | 8.5 |
| 2 | 5.2 | 11.5 | 1,385 | 7.4 |
| 5 | 4.0 | 15.0 | 1,800 | 5.7 |
| 8 | 2.8 | 21.4 | 2,571 | 4.0 |
| 10 | 2.0 | 30.0 | 3,600 | 2.8 |

`RES_TANK_CAP` does not change throughput for an attentive player; it changes how much a lazy player gets per visit (one tank) and how long the attentive player can look away without wasting refill time (`TankCap * TankRefillS` seconds: 60 s at Research 0, 120 s at cap 60 / 2 s).

### 8.2 Merges per minute and the two policies

Every level-1 fish eventually causes about one merge (a level-`n` fish is `2^(n-1)` fish and `2^(n-1) - 1` merges), so merges per minute equal fish per minute for a player who keeps up: 10 at Research 0, 30 at `RES_TANK_REFILL` 10. With `Merge all` one press does them all, so the attentive policy is "press `Release max` and `Merge all` whenever the tank is full", one pair of taps a minute at Research 0, every two minutes at cap 60.

| Policy | Definition | Fish per day (Research 0) | Fish Coins per day at cap 10 (merges only) | `fishMult` at day 7 |
|---|---|---|---|---|
| Attentive | `ReleaseMax` + `MergeAll` every active minute for 120 min; discards the lowest fish when jammed | 1,200 | about 2,160 | x17 |
| Lazy | `ReleaseMax` + `MergeAll` once per login, two logins a day | 20 | about 36 | x1.9 (20 fish, binary merged: 4, 2, 1 -> 8.4 + 3.75 + 2.5 = 15%, grows slowly) |
| Never | nothing | 0 | 0 | x1.0 |

The lazy player's `fishMult` is x1.5 after the first login and x1.9 after a week; the attentive player's is x17. That x9 gap at day 7 is the active-play edge the design wants on this layer, and the tool checks it stays between x3 and x12 (bigger and the board is mandatory; smaller and it is decoration).

### 8.3 Fish Coins per active minute

Per level-1 fish fully merged up to level `cap`:

```
coinsPerFish(cap) = sum over n = 2..cap of FISH_MERGE_COINS_PCT * FISH_DISCOVERY_COINS[n] / 2^(n-1) = 0.02 * 10 * (cap - 1) = 0.2 * (cap - 1)
```

1.8 coins per fish at cap 10, 2.8 at cap 15, 3.8 at cap 20. At Research 0: 18 coins per active minute, about 2,160 per 2 h day; at `RES_TANK_REFILL` 10 and cap 15: 84 per minute, about 10,000 per day. Discovery adds a one-off `10 * (2^cap - 1)` = 10,230 coins for the climb to level 10 (almost all on day 1-2), then 10,240 for level 11, 20,480 for level 12, each when the first fish of that level is made. Discards add nothing in steady state. The Fish Coin Dungeon (06) is the other income; the 03 bot assumes 1,000 a day from it, so the board is about two thirds of Fish Coin income at Research 0 and more later.

### 8.4 DPS per active minute

Each active minute at Research 0 adds 10 fish to the board's merge tree. The board's passive after `F` released fish (all merged as far as they go) is `0.5 * sum of FISH_MISSILE_PCT over the levels in the binary representation of F`, which for large `F` is dominated by `floor(F / 512)` cap-10 fish at 96% each. So the attentive player gains about 96 percentage points of passive every 51 minutes, roughly +1.9 points of hero DPS per active minute, until the board fills around day 9; then nothing until `RES_FISH_CAP`. Missiles add a steady 2.3% of DPS while merging (section 5.1).

## 9. `tools/fish_model.py`

A per-minute throughput model, closed-form where possible, with a board simulation for the policy comparison. Reads `fish_levels`, `tank`, `research` and `constants` from `content/*.json` (09); `--override` as in the other tools.

Inputs: `--days 30`, `--active-min 120`, `--sessions 2`, `--policy attentive|lazy|never|auto` (auto = P1 Auto Merge), `--research "RES_TANK_CAP=3,RES_TANK_REFILL=5,RES_FISH_CAP=2"` or `--research-schedule tools/out/power_curve_7.json` to take the 03 bot's actual research levels per day, `--dungeon-coins 1000`, `--seed 7` (placement does not affect throughput, so the seed only matters for the jam test).

Model: tank stock and refill per minute; board as a count per level; the policy's release/merge/discard rules from sections 6.1 and 8.2; coins and missiles per merge from section 2.2; `FISH_BOARD_DPS_PCT` per section 5.2; offline fill per section 3.

Outputs (printed and `tools/out/fish_<policy>.json`): per day, fish released, merges, discards, `FISH_BOARD_DPS_PCT`, `fishMult`, Fish Coins from discovery / merges / discards, highest fish level, board occupancy, minutes jammed; a policy comparison table (attentive vs lazy vs never) at days 1, 3, 7, 14, 30; the section 8.1 and 8.3 tables regenerated from content; a `FISHLINE day7 attentive x17.2 lazy x1.9 ratio 9.1` summary line for 03's report. `S` constants it validates: `TANK_CAP0`, `TANK_REFILL_S0` (attentive fish per day between 1,000 and 1,500 at Research 0), `FISH_MAX_LVL0` (board fills between day 6 and day 12 for the attentive policy), `FISH_DISCOVERY_COINS` and `FISH_MERGE_COINS_PCT` (day-1 Fish Coins between 8,000 and 14,000 including discovery; steady-state merge coins between 1,500 and 3,000 a day at cap 10), `FISH_MISSILE_PCT` and the 0.5 passive factor (attentive `fishMult` at day 7 between x12 and x25; attentive/lazy ratio between x3 and x12; `missileDpsPct` under 10% at every research level), `FISH_DISCARD_COINS_PCT` (discard coins under 10% of merge coins for the attentive policy), `RES_TANK_*` effects (jointly with 03's tool: `RES_TANK_REFILL` 10 at most triples fish per day), `AUTO_MERGE_INTERVAL_S` (auto policy within 10% of attentive).

## 10. Events

```csharp
public sealed record FishReleased(long Tick, int Cell, int Level, int StockAfter) : SimEvent(Tick);
public sealed record FishMerged(long Tick, int Source, int Target, int NewLevel, double Coins, double MissilePct, bool Discovered) : SimEvent(Tick);
public sealed record FishDiscovered(long Tick, int Level, double Coins) : SimEvent(Tick);
public sealed record FishDiscarded(long Tick, int Cell, int Level, double Coins) : SimEvent(Tick);
public sealed record TankFishReady(long Tick, int Stock) : SimEvent(Tick);          // opt-in, off by default
public sealed record FishMissileHit(long Tick, double HitFraction) : SimEvent(Tick); // published by 03 when the queue drains
```

`FishMerged` and `FishDiscovered` are the two in 00 section 5.1; the rest are this spec's. `CurrencyChanged` with reasons `FISH_DISCOVERY` and `FISH_MERGE` is published by the Wallet as for every delta.

## 11. Edge cases

| Case | Behaviour |
|---|---|
| Board full, tank has fish | `ReleaseFish` rejected `BoardFull`; tank keeps filling to cap and stops; `Release max` disabled with subtitle "Board full"; if `Merge all` would also do nothing the tank widget shows the Research shortcut (MVP) and the long-press discard hint. |
| Tank empty | `ReleaseFish` rejected `TankEmpty`; tank shows a countdown `TankRefillTicks - RefillProgressTicks` ticks; `Release max` disabled. |
| Jammed board: 20 fish, no two equal, tank full | Possible only with at least 11 distinct levels, so only at cap 11+ (with cap 10 there are at most 10 distinct levels and 20 cells, so a pair always exists). Resolution is discard or Research; the bot discards the lowest fish. |
| Merge during a boss fight | Allowed. The missile lands on the boss on the next tick; a merge is often the thing that breaks a wall by a hair, which is the intended drama. |
| Merge or release while `Spawning` | Allowed; the missile waits in the queue for the next enemy (03 section 2.5). |
| App backgrounded mid-drag | The drag is client state; no command has been sent. On resume the client cancels the drag (spring back) and the board is whatever the save says. |
| Save mid-`MergeAll` | Impossible: commands are atomic with respect to `Advance` and saves (01). |
| Offline catch-up | Tank fills per `CatchUpTank`; nothing else changes; `fish_spawn` draws nothing. |
| `RES_FISH_CAP` bought with two cap fish on the board | They become mergeable immediately; `Merge all` picks them up on its next pass; no retroactive discovery. |
| Content lowers `FISH_MAX_LVL0` or a research cap below a saved fish's level | The fish stays, keeps its board DPS, cannot merge; the content validator (09) warns when a cap decreases between versions. |
| `Cells` length differs from `FISH_BOARD_W * FISH_BOARD_H` after a content change | Board dimensions are not content-tunable after v1 without a migration (02); the validator rejects a change to `FISH_BOARD_W/H` unless the save schema version also changes. |
| Stage select to a lower stage (MVP) | Missiles and passive apply to whatever enemy is live; nothing board-side depends on stage. |
| Dungeon run (MVP) | The board stays interactive during a dungeon run; missiles hit the dungeon enemy; coins accrue normally. 06 may disable the board for Tower fights. |

## 12. Determinism rules

1. `fish_spawn` is drawn only in a successful `ReleaseFish`, exactly once, and `fish_spawn.Counter == LifetimeReleases` is a `Validate()` invariant (02).
2. The k-th empty cell is found by a row-major scan; no hash sets, no platform-dependent iteration order.
3. `MergeAll` order is fixed by section 6.1; `ReleaseMax` is a loop of `ReleaseFish`.
4. Coins and missile values come from the `fish_levels` table rows, never recomputed with `Math.Pow`; `FISH_DISCOVERY_COINS` doubling is pre-evaluated by the exporter (09).
5. The board marks DPS dirty once per command; `Query.FishBoardDpsPct` sums the table values over `Cells` in index order (floating-point sum order fixed).
6. Event order within a command: `FishMerged` then `FishDiscovered` then the `CurrencyChanged` from the Wallet; tests assert it.
7. The client never reads `Cells` to decide a gesture's validity ahead of the sim; it sends the command and animates from the result and events, so a stale view cannot produce a divergent board.

## 13. Analytics events

| Event | Fields | When |
|---|---|---|
| `fish_release` | `count`, `stockBefore`, `stockAfter`, `boardOccupancyAfter`, `viaButton` (bool) | per `ReleaseFish` or once per `ReleaseMax` with the count |
| `fish_merge` | `count`, `levelsCreated[]` (histogram of `NewLevel`), `coins`, `discoveries[]`, `viaButton` (bool), `duringBoss` (bool) | per `MergeFish` or once per `MergeAll` |
| `fish_discovered` | `level`, `coins`, `dayIndex` (days since profile creation) | per `FishDiscovered` |
| `fish_discard` | `level`, `coins`, `boardOccupancyBefore` | per `DiscardFish` |
| `fish_board_snapshot` | `cells[]`, `fishBoardDpsPct`, `fishMult`, `tankCap`, `tankRefillS`, `fishMaxLvl`, `autoMerge` | at session end |
| `currency_changed` | 02 schema with reasons `FISH_DISCOVERY`, `FISH_MERGE` | per wallet delta |

Questions the dashboard should answer (10): share of sessions with at least one merge, merges per active minute distribution (attentive vs lazy in the wild), day index of first level-10 fish, `fishMult` percentile bands at day 7 versus the tool's x12-x25, discard rate, jam rate.

## 14. Open questions

1. **Random placement versus first empty cell.** Default: random (section 4.1). If playtests find the drag distance annoying, switch to "nearest empty cell to the tank" with the same single draw used for tie-breaks, which keeps the counter invariant.
2. **Adjacency-free merging.** Default: any two equal fish anywhere. A 5 x 4 board with adjacency rules jams constantly; if the design wants spatial puzzle feel, that is a different (and bigger) board.
3. **Discard payout 10%.** Default: keep; the tool checks it stays under 10% of merge income for the attentive policy.
4. **Auto Merge unlock** (purchase versus Research node) and whether it should run during offline catch-up (this spec says no, to preserve the active edge). Owner: 06. Default: Research node, no offline.
5. **Board size is not content-tunable** after v1 without a migration. Owner: 02. Default: fixed at 5 x 4; `FISH_BOARD_W/H` stay in content for the validator's sake only.
6. **Missile animation timing versus sim application.** The sim applies on the next tick; the animation takes about 400 ms. Owner: 07. Default: the HP bar on the viewport lags the sim by one animation frame; damage numbers are shown when the missile lands, read from the event.
7. **Should cap fish auto-convert** (two cap fish merge into coins and a big missile) instead of refusing? Default: refuse; a board full of cap fish is a visible "buy Research" prompt and the passive is the reward for getting there.

## 15. Acceptance criteria

1. `TickTank` from an empty tank at Research 0 reaches `Stock == 1` at exactly tick 60 and `Stock == 10` at tick 600, then stops advancing `RefillProgressTicks`; `CatchUpTank(36000)` from `Stock 3, progress 17` gives `Stock == TankCap` and `RefillProgressTicks == 0`; `CatchUpTank(125)` from `Stock 0, progress 0` gives `Stock 2, progress 5`.
2. `ReleaseFish` with `Stock 0` is rejected `TankEmpty` and draws nothing; with a full board rejected `BoardFull` and draws nothing; otherwise draws exactly once and places a level-1 fish in the k-th empty cell for `u` in {0.0, 0.5, 1 - 1e-12} with 7 empty cells giving k = 0, 3, 6.
3. The first `ReleaseFish` on a fresh profile pays 10 Fish Coins with reason `FISH_DISCOVERY` and sets bit 0 of `DiscoveredMask`; the second pays nothing.
4. `MergeFish` of two level-3 fish creates level 4 in the target cell, empties the source, pays 80 coins (`FISH_DISCOVERY`) the first time and 1.6 coins (`FISH_MERGE`) thereafter, enqueues a missile of 0.169 hit fractions, publishes `FishMerged` then `FishDiscovered` (first time only); `MergeFish` of levels 3 and 4 is rejected `LevelMismatch`; of two cap fish `FishAtLevelCap`; of an empty cell `EmptyCell`; same cell twice throws.
5. `MergeAll` on the board `[1,1,1,1,2,2,0,...]` merges cells (0->1) and (2->3) into level 2, then (1->3) and (4->5) into level 3, then (3->5) into level 4; the final board has a single level-4 fish in cell 5, `merges == 5`, and the event sequence is five `FishMerged` in that order; the result is byte-identical on x64, ARM64 and IL2CPP.
6. `ReleaseMax` with `Stock 10` and 7 empty cells releases 7 and returns 7; with `Stock 3` releases 3.
7. `DiscardFish` on a level-10 fish pays 512 coins with reason `FISH_MERGE`, empties the cell and publishes `FishDiscarded`; on an empty cell rejected `EmptyCell`.
8. `Query.FishBoardDpsPct` for the board `[10,10,8,6,5,0...]` equals `96.1 + 96.1 + 42.7 + 19.0 + 12.7 = 266.6` from the table within 1e-9, and `fishMult` with `missileDpsPct 0` equals 3.666.
9. A missile queued during `Spawning` applies on the first `Fighting` tick of the next enemy; one queued during a boss fight hits the boss; `crit.Counter` is unchanged by missiles.
10. `fish_spawn.Counter == LifetimeReleases` after a 200,000-command fuzz run mixing all five board commands; `Validate()` never throws.
11. Offline: a 3 h catch-up changes only `Tank.Stock` and `Tank.RefillProgressTicks` in `FishBoardState`; `Cells`, `DiscoveredMask`, the Wallet's Fish Coins and `fish_spawn.Counter` are unchanged.
12. With cap 10 and 20 occupied cells there is always at least one mergeable pair (pigeonhole); a property test over 10,000 random full boards finds `MergeAll` returns >= 1.
13. `tools/fish_model.py --policy attentive` reports between 1,000 and 1,500 fish on day 1 at Research 0, a full cap-10 board between day 6 and day 12, `fishMult` at day 7 between x12 and x25; `--policy lazy` reports `fishMult` at day 7 between x1.5 and x3; the ratio is between x3 and x12.
14. `tools/fish_model.py` and the C# `HeroCat.Bots` fish policy agree on day-7 `fishMult` within 5% for the attentive policy (10's cross-check).
15. PlayMode (07): dragging a fish onto an equal fish sends exactly one `MergeFish(source, target)` with `target` the cell under the release point; dropping on a mismatched fish sends nothing and plays the spring-back; a tap-tap on two equal fish sends one `MergeFish`; `Merge all` with nothing to merge is disabled and shows "Nothing to merge".

## 16. Constants introduced

| Name | Value | Kind | Section | Note |
|---|---|---|---|---|
| `TANK_REFILL_FLOOR_S` | 2.0 | S | 3 | Lower bound after `RES_TANK_REFILL`. |
| `RES_TANK_CAP_PER_LVL` | 5 | S | 3 | Priced in 03 section 6. |
| `RES_TANK_REFILL_PER_LVL` | 0.4 s | S | 3 | Priced in 03 section 6. |
| `TankReleaseLevel` | 1 (MVP; P1 upgrades) | S | 3 | State field with default, not a constant row. |
| `FISH_DISCARD_COINS_PCT` | 0.10 | S | 4.4 | Of the level's discovery value. |
| `FISH_BOARD_DPS_PER_FISH[n]` | `0.5 * FISH_MISSILE_PCT[n]` | S | 5.2 | Names the 0.5 factor from 00 section 6.4 so the table can carry it. |
| `FISH_MAX_LVL_HARD` | 20 | S | 2.2 | `fish_levels` ships 20 rows; `RES_FISH_CAP` max 10. |
| `AUTO_MERGE_INTERVAL_S` (P1) | 3 | S | 6.2 | |
| `DRAG_START_PX`, `LONG_PRESS_MS` | 8, 350 | - | 6 | UI constants for 07. |
| `MERGE_ALL_ANIM_MS`, `RELEASE_MAX_ANIM_MS` | 80, 60 | - | 6 | UI constants for 07. |
| Haptic ids | `MERGE`, `DISCOVER`, `INVALID`, `RELEASE`, `SELECT` | - | 6 | For 07's haptics map. |
| `fish_levels` rows | 20 rows per section 2.2 | S | 2.2 | Table, generated from the 00 formulas by the exporter. |
