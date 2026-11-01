## 03 - Combat and Progression

This spec owns the per-tick combat loop in `HeroCat.Sim`, the stage and boss rules, the eight `Stat`s and their Training curves, Hero Level, Awakening, Research, and the power formula that ties every layer together. It also specifies `tools/power_curve_model.py`, the greedy F2P bot that validates every `S` constant below and produces the day-stage table in 00 section 6.6. Where this spec and `00-overview.md` disagree, 00 wins.

Tiers covered: M0 (combat, stages, Tap Zap, fish missiles in the tick loop, Training, Hero Level), MVP (Awakening, Research, companions in the tick loop), P1 (Runes term, auto boss retry, Tower survival fights).

Depends on: 00 (all constants and names), 02 (`GameState` fields this spec reads and writes), 04 (weapon/skill/companion data the tick loop consumes), 05 (fish missile queue and `FISH_BOARD_DPS_PCT`), 06 (Rock Candy and Fish Coin income the bot assumes).

## 1. Design goals

1. One `Tick()` function, one `GameState`. Everything observable is a pure function of `(ContentDb, GameState)` plus the applied commands and ticks (00 section 8 item 1 and 5).
2. The hero never dies (section 4). Progress is monotone; the only "failure" is a boss timing out, which costs nothing but the timer.
3. Every layer of the power formula is worth buying at every point in the first 30 days: no layer above 40% of log-power at day 30, none below 5% (00 section 7). Section 8 checks this with the bot and section 8.5 reports where the current constants miss.
4. Fast enough for bots: `Advance(1_000_000)` under 2 s in Release (AC19, 10); the client tick allocates nothing when no event is masked in.

## 2. Tick loop

### 2.1 Entry points and per-tick order

```csharp
public sealed partial class Simulation
{
    public Simulation(IContentDb content, GameState state, IEventSink events);
    public void Tick();                 // exactly one tick, TICK_HZ per second of game time
    public void Advance(int ticks);     // Tick() repeated; commands cannot interleave
}
```

Order inside one tick. The order is a contract: tests in 10 assert it, and the server re-simulator (08) relies on it.

```
Tick():
  Time.Tick += 1
  Combat.Mp = min(MpPoolMax, Combat.Mp + MpRegenPerTick)                   # 1. MP regen (MVP)
  TickCooldowns()                                                          # 2. skill and companion cooldowns -= 1
  switch Combat.Phase:
    Spawning:
      Combat.SpawnTimer -= 1
      if Combat.SpawnTimer == 0: Spawn()
    Fighting:
      ApplyQueuedFishMissiles()                                            # 3. damage events queued by 05 since last tick
      if Combat.EnemyHp <= 0: Kill(); break
      AutoCastSkills()                                                     # 4. one cast per tick at most
      CastCompanionAbilities()                                             # 5. one cast per tick at most, MP-gated
      if Combat.EnemyHp <= 0: Kill(); break
      Combat.AttackProgress += ATTACK_PROGRESS_UNIT + AtkSpdBonusUnits     # 6. hero auto-attack with ATK_SPD
      if Combat.AttackProgress >= ATTACK_INTERVAL_TICKS * ATTACK_PROGRESS_UNIT:
        Combat.AttackProgress -= ATTACK_INTERVAL_TICKS * ATTACK_PROGRESS_UNIT
        HeroHit()
        if Combat.EnemyHp <= 0: Kill(); break
      ApplyTapZaps()                                                       # 7. taps received since last tick, capped
      if Combat.EnemyHp <= 0: Kill(); break
      if Combat.EnemyIsBoss:
        Combat.BossTicksLeft -= 1
        if Combat.BossTicksLeft == 0: BossFail()                               # 8. timer check last, so a hit on the final tick counts
  TickBuffs()                                                              # 9. ATK/gold buffs from companions and skills -= 1
```

State field names and shapes are 02's (section 3.1, issue #7): `Time.Tick`, `Combat.*`, `Progress.*`, `Loadout.*`, `Stats.*`. Everything this loop reads or writes lives in the in-memory `GameState`, so `DeepClone` and replay are exact. The save drops only the transient fight state 02 section 3.3 lists, which lives in `Combat.Fight` (02's `FightState`; the pseudocode below writes `Combat.EnemyHp`, `Combat.Phase`, `Combat.BossTicksLeft` and the like as shorthand for `Combat.Fight.EnemyHp` and so on): enemy HP, `Phase`, `SpawnTimer`, `AttackProgress`, `ActiveDots`, buffs, `PendingTaps`, `TapWindowTick`. It keeps `Combat.Boss`, `Combat.BossFailsOnStage`, `Combat.Selected`, `Combat.Mp`, the skill and companion cooldowns and `Combat.MissileQueue`. On load the player faces a fresh mob and a `Pending` boss respawns as the boss.

Dungeon runs (MVP, 06) are ticked in this same loop, not resolved instantly (issue #42): `StartDungeon` switches `Combat.Mode = Dungeon` and the floor's single boss is fought with steps 1-9 above under the dungeon's time limit, except step 3: the missile queue is not drained in that mode (section 2.5). In that mode the kill and timeout branches hand off to 06 (`DungeonCleared`, which debits the ticket and grants the reward, or `DungeonFailed`, which costs nothing; 00 decision 16) instead of `Kill()`/`BossFail()`, so no stage, loot, first-clear or wall state changes; the hero's crit draw comes from the temporary `dungeon_run` stream (section 10), and the frontier fight resumes unchanged when the run ends.

`ATTACK_PROGRESS_UNIT = 100` (integer fixed point). At `ATK_SPD` level 0 the hero gains 100 units per tick and hits every `ATTACK_INTERVAL_TICKS = 10` ticks, one hit per second. At `ATK_SPD` level `n` the gain is `100 * (1 + TRAIN_PER_LVL[ATK_SPD] * n)` rounded to an integer, so level 100 (cap, +100%) is 200 units per tick and one hit every 5 ticks. Integer arithmetic keeps the hit schedule identical on every platform; a `double` accumulator would not (00 section 3, determinism row).

### 2.2 Hero hit and crit

```
HeroHit():
  dmg = Query.HeroHit * (1 + Combat.AtkBuffPct / 100)       # HeroHit cached, section 7.3; the ATK buff applies here only
  u = crit.NextDouble()                                     # exactly one draw per hero hit, always
  Stats.HeroHits += 1
  isCrit = u < CritRate
  if isCrit: dmg *= CritDmg
  Combat.EnemyHp -= dmg
  publish HitLanded(dmg, isCrit, Combat.EnemyHp)            # opt-in event, see 01

CritRate = min(TRAIN_CAP[CRIT_RATE] * TRAIN_PER_LVL[CRIT_RATE], TRAIN_PER_LVL[CRIT_RATE] * trainLvl[CRIT_RATE])   # 0 .. 0.75
CritDmg  = CRIT_DMG_BASE + TRAIN_PER_LVL[CRIT_DMG] * trainLvl[CRIT_DMG]                                       # 1.5 at level 0
```

The `crit` stream is consumed once per hero hit, including hits at `CritRate == 0`, so `crit.Counter == Stats.HeroHits` holds for the validator and the server audit (02, 08; issue #8). Fish missiles, skills, companion abilities and Tap Zap do not crit and consume no `crit` draws. Hero hits in a dungeon fight draw from `dungeon_run` instead and do not count in `Stats.HeroHits` (section 2.1, issue #42).

### 2.3 Skills (auto-cast)

Skill data comes from 04: `dpsPct` (rated average DPS as % of hero DPS), `castType` (`INSTANT`, `DOT`, `BUFF`), `cooldownTicks`, and the per-cast numbers derived from them. The tick loop only needs:

```
AutoCastSkills():
  for slot in 0 .. SKILL_SLOTS-1 in order:                  # slot order is the priority order (04 section 7.2)
    s = Skill(Loadout.Skills[slot]); if s == null: continue
    if Combat.SkillCooldowns[slot] > 0: continue
    Cast(s); Combat.SkillCooldowns[slot] = s.CooldownTicks
    publish SkillCast(slot, s.Id)
    return                                                  # at most one cast per tick
```

- `INSTANT`: `EnemyHp -= Query.SkillUnit * s.DamagePctPerCast / 100` (`SkillUnit`, section 7.1).
- `DOT`: adds a `DotInstance(ticksLeft, damagePerTick)` (`damagePerTick` in `SkillUnit`s) to `Combat.ActiveDots` (max 6 entries, oldest replaced); `ApplyDots()` runs inside step 4 before casting.
- `BUFF`: sets `Combat.AtkBuffPct = max(current, s.BuffPct)` with `Combat.AtkBuffTicksLeft = s.DurationTicks`. Buffs do not stack; the strongest active one applies. The buff multiplies the hero's auto-attack only, not casts, missiles or taps (section 7.1).

Casting requires a live enemy (`Phase == Fighting`). During `Spawning` cooldowns still tick down, so a skill that comes off cooldown between enemies fires on the first tick of the next fight. This is why skills feel like a burst at spawn and why the boss timer check is last in the tick.

### 2.4 Companions and the MP pool (MVP)

Companions (04 section 8) have `abilityType` in `DAMAGE`, `BUFF_ATK`, `BUFF_GOLD`, `MP_REGEN`, an `mpCost`, a `cooldownTicks` and a magnitude. All four equipped companions draw from one pool:

```
CastCompanionAbilities():
  for slot in 0 .. COMPANION_SLOTS-1 in order:
    c = Companion(Loadout.Companions[slot]); if c == null: continue
    if Combat.CompanionCooldowns[slot] > 0: continue
    if Combat.Mp < c.MpCost: continue                       # MP-gated: the ability waits, cooldown does not restart
    Combat.Mp -= c.MpCost
    Apply(c); Combat.CompanionCooldowns[slot] = c.CooldownTicks
    publish CompanionAbilityUsed(slot, c.Id, Combat.Mp)
    return                                                  # at most one cast per tick

MpPoolMax     = MP_POOL0 + RES_MP_POOL_PER_LVL * research[res_mp_pool]
MpRegenPerTick = (MP_REGEN0 + RES_MP_REGEN_PER_LVL * research[res_mp_regen]) / TICK_HZ
```

`Apply(c)`: `DAMAGE` deals `Query.CompanionUnit * c.DamagePctPerCast / 100` (section 7.1); `BUFF_ATK` sets the shared ATK buff exactly as a `BUFF` skill does; `BUFF_GOLD` and `MP_REGEN` as 04 section 8.

With the defaults (`MP_POOL0 = 100`, `MP_REGEN0 = 2.0` per second, every companion ability costing 10 MP on a 10 s cooldown) four companions want 4 MP/s and get 2, so they fire at half their rated uptime until Research raises regen. That gap is the reason Research matters to a player who never touches the Fish Board; section 6 prices it. The MP pool is `Combat.Mp` and is saved; it is not refilled by offline catch-up (06).

### 2.5 Fish missiles and Tap Zap

Fish missiles arrive as damage events queued by the Fish Board (05 section 5): `FishMissileQueue.Enqueue(hitFraction)` where `hitFraction = FISH_MISSILE_PCT[level] / 100`. The queue is `Combat.MissileQueue` and is persisted in the save (02), so a save taken while `Spawning` with missiles queued resumes and re-simulates identically (issue #7).

```
ApplyQueuedFishMissiles():
  while Combat.MissileQueue.Count > 0:
    f = Combat.MissileQueue.Dequeue()
    Combat.EnemyHp -= Query.HeroHit * f                     # no crit, no draw, no buff
    publish FishMissileHit(f)
```

Missiles queued while `Spawning` wait for the next enemy. While `Combat.Mode == Dungeon` the queue is not drained (the board fires nothing during a run, issue #40); anything still queued waits for the stage fight (05 section 5.1). A missile is never lost and never overkills into the next enemy; the surplus is discarded with the kill.

Tap Zap (M0): the client sends `TapZap(count)` once per frame with the number of taps since the last frame. The sim keeps `Combat.TapsThisSecond` and a `TapWindowTick`:

```
TapZap(count):                                              # command, validated like any other
  if Time.Tick - Combat.TapWindowTick >= TICK_HZ: Combat.TapWindowTick = Time.Tick; Combat.TapsThisSecond = 0
  allowed = max(0, TAP_ZAP_MAX_PER_SEC - Combat.TapsThisSecond)
  used = min(count, allowed); Combat.TapsThisSecond += used
  Combat.PendingTaps += used
  return Ok(used, ignored: count - used)

ApplyTapZaps():
  if Combat.PendingTaps == 0: return
  Combat.EnemyHp -= Query.HeroHit * TAP_ZAP_DMG_PCT * Combat.PendingTaps
  publish TapZapped(Combat.PendingTaps); Combat.PendingTaps = 0
```

At the cap, taps add `6 * 0.5 = 3` hero hits per second on top of the hero's 1 to 2, so a tapping player is at most 2.5x to 4x an idle one during a boss. That is the intended size of the active edge; the bot never taps, so the targets in section 8 are idle-combat targets.

### 2.6 Spawn, kill, loot, stage advance

```
FoughtStage = Combat.Selected ?? Combat.Stage                 # side-stage farming (section 2.7, issue #35)

Spawn():
  isBoss = Combat.Selected == null and Combat.Boss == Pending   # a selected stage never spawns a boss
  Combat.EnemyIsBoss = isBoss
  Combat.EnemyMaxHp = Combat.EnemyHp = isBoss ? BOSS_HP_MULT * Hp(Combat.Stage) : Hp(FoughtStage)
  Combat.AttackProgress = 0
  Combat.BossTicksLeft = isBoss ? BOSS_TIME_TICKS : 0
  Combat.Phase = Fighting
  publish EnemySpawned(FoughtStage, isBoss, Combat.EnemyMaxHp)

Kill():
  s = FoughtStage; isBoss = Combat.EnemyIsBoss
  gold = Gold(s) * GoldMult * (isBoss ? BOSS_GOLD_MULT : 1)
  exp  = Exp(s)  * ExpMult
  Wallet.Apply(+gold GOLD, isBoss ? BOSS : KILL)
  Wallet.Apply(+exp HERO_EXP, isBoss ? BOSS : KILL)        # the only EXP path: writes Hero.Exp, runs CheckHeroLevel (5.2), may publish LevelUp
  Stats.Kills += 1; if isBoss: Stats.BossKills += 1
  publish EnemyKilled(s, isBoss, gold, exp)
  Quests.OnKill(s, isBoss)                                  # 06
  if isBoss:
    if loot.NextDouble() < BOSS_ROCK_CANDY_P:               # exactly one `loot` draw per boss kill
      Wallet.Apply(+1 ROCK_CANDY, BOSS)                     # CurrencyChanged is the only event (issue #11)
    publish BossCleared(s)
    if s > Progress.HighestFirstClearBoss:
      Progress.HighestFirstClearBoss = s; Wallet.Apply(+FIRST_CLEAR_DIAMONDS DIAMOND, FIRST_CLEAR)   # value owned by 06
      publish StageFirstCleared(s, FIRST_CLEAR_DIAMONDS)
    AdvanceStage(viaBoss: true)
  else if Combat.Selected != null:
    pass                                                    # farming a selected stage: pays, never advances (issue #35)
  else if Combat.Boss == Walled:
    pass                                                    # farming a walled stage: no credit toward advance
  else:
    Combat.KillsInStage += 1
    if Combat.KillsInStage == KILLS_PER_STAGE - 1 and s % BOSS_EVERY == 0:
      Combat.Boss = Pending                                 # the 10th enemy of a boss stage is the boss
    else if Combat.KillsInStage >= KILLS_PER_STAGE:
      AdvanceStage(viaBoss: false)
  Combat.Phase = Spawning; Combat.SpawnTimer = SPAWN_DELAY_TICKS; Combat.ActiveDots.Clear()

AdvanceStage(viaBoss):
  from = Combat.Stage
  Progress.HighestCleared = max(Progress.HighestCleared, from)
  if from >= STAGE_CAP:
    if Progress.StageCapReached == 0: Progress.StageCapReached = Time.Tick
    publish StageCapReached(from); Combat.Boss = None; return
  Combat.Stage = from + 1; Combat.KillsInStage = 0; Combat.Boss = None; Combat.BossFailsOnStage = 0
  publish StageCleared(from, from + 1, viaBoss)
  if (from + 1 - 1) % CHAPTER_WIDTH == 0: publish ChapterEntered(Chapter(from + 1))
```

Normal kills on a non-boss stage never pay Rock Candy; `loot` is consumed only on boss kills, so `loot.Counter == Stats.BossKills` in MVP (issue #8; P1 adds drop tables and a second invariant).

### 2.7 Boss fail, the wall, and `AttemptBoss`

```
BossFail():
  s = Combat.Stage
  Combat.Boss = Walled; Combat.BossFailsOnStage += 1; Stats.BossFails += 1
  if Stats.FirstWallStage == 0: Stats.FirstWallStage = s
  publish BossFailed(s, Combat.BossFailsOnStage, Combat.EnemyHp / Combat.EnemyMaxHp)   # remaining fraction drives the "so close" UI
  Combat.Phase = Spawning; Combat.SpawnTimer = SPAWN_DELAY_TICKS; Combat.ActiveDots.Clear()

AttemptBoss():                                              # command; always the frontier boss (issue #35)
  if Combat.Boss == Pending and Combat.Selected == null:      return Reject(NotAvailable, "BOSS_ALREADY_PENDING")
  if Combat.Stage % BOSS_EVERY != 0 or Combat.Boss == None:   return Reject(NotAvailable, "NO_BOSS_TO_ATTEMPT")
  if Combat.Selected != null:
    Combat.Selected = null; publish StageChanged(Combat.Stage)  # the boss challenge returns to the frontier
  Combat.Boss = Pending
  Combat.Phase = Spawning; Combat.SpawnTimer = SPAWN_DELAY_TICKS   # current mob despawns: no gold, no EXP, no kill credit
  return Ok

SelectStage(stage?):                                        # command, MVP (SCR_STAGE_SELECT); issue #35
  if stage != null and (stage < 1 or stage > STAGE_CAP):      return Reject(BadArgument, "STAGE_OUT_OF_RANGE")
  if stage != null and stage > max(Progress.HighestCleared, Combat.Stage):
                                                              return Reject(Locked, "STAGE_NOT_CLEARED")
  if Combat.Mode == Dungeon:                                  return Reject(NotAvailable, "DUNGEON_RUN_IN_PROGRESS")   # stage fields frozen during a run (02 section 3.3); issue #159
  target = (stage == null or stage == Combat.Stage) ? null : stage   # null = the frontier
  if target == Combat.Selected: return Ok                     # no-op, no event
  Combat.Selected = target                                    # Combat.Stage, KillsInStage, Boss and BossFailsOnStage are untouched
  Combat.Phase = Spawning; Combat.SpawnTimer = SPAWN_DELAY_TICKS; Combat.ActiveDots.Clear()   # current enemy despawns, no rewards
  publish StageChanged(FoughtStage)
  return Ok
```

While `Walled`, mobs of stage `s` spawn and pay `Gold(s)` and `Exp(s)`; `KillsInStage` stays at `KILLS_PER_STAGE - 1`. The first boss attempt on a boss stage is automatic. Retries are manual in MVP (`AttemptBoss` from `SCR_MAIN`); P1 adds `BOSS_AUTO_RETRY_S = 60` as a content constant and a timer in `CombatState`. `SCR_STAGE_SELECT` (MVP, 07) sends `SelectStage(stage?)` to farm any stage `<= Progress.HighestCleared` as a side stage (issue #35): `Combat.Stage` stays the frontier, and the frontier's `KillsInStage`, `Boss` (including `Walled` and `Pending`) and `BossFailsOnStage` are kept unchanged until the player returns with `SelectStage(null)`. Kills on a selected stage pay that stage's `Gold(s)` and `Exp(s)`, count for `Stats.Kills` and quests, and never advance anything or spawn a boss, so farming a lower stage is always worse than the frontier. The only boss challenge is the frontier boss: `AttemptBoss` (from `SCR_MAIN` or the list) returns to the frontier first. First-clear diamonds therefore only ever pay through `Progress.HighestFirstClearBoss` on the frontier.

### 2.8 Wall condition

A boss is cleared iff the damage dealt inside `BOSS_TIME_TICKS` reaches `BOSS_HP_MULT * Hp(s)`. Ignoring skills, companions, missiles and crit variance, the hero lands `BOSS_TIME_TICKS / ATTACK_INTERVAL_TICKS * speedFactor = 30 * speedFactor` hits in 30 s, so:

```
clearable(s)  <=>  BOSS_HP_MULT * Hp(s) <= 30 * speedFactor * HeroHit * critFactor
              <=>  Hp(s) / (HeroHit * critFactor * speedFactor) <= 3 s
```

In words: **if a normal mob on this stage takes more than three seconds of hero DPS, the boss cannot be cleared by the hero alone.** Skills (rated `+sum skillDpsPct`), companions and missiles are already inside the rated `DPS(s)` in section 7, so the full condition is `Hp(s) / DPS(s) <= 3 s` with `DPS` from the power formula. The sibling project's condition was 2 s with a 20 s timer; the 30 s timer (00 decision 4) buys one extra hit per mob, which is what lets the burst layers matter.

## 3. Stages and chapters

| Field | Type | Meaning |
|---|---|---|
| `Combat.Stage` | `int` | Frontier stage `s >= 1`; only `AdvanceStage` moves it. |
| `Combat.KillsInStage` | `int` | `0 .. KILLS_PER_STAGE-1`. |
| `Combat.Boss` | `BossPhase` | `None`, `Pending` (next spawn is the boss), `Walled` (boss failed, farming mobs). 02's type; a bool could not represent `Walled` (issue #7). |
| `Combat.BossFailsOnStage` | `int` | Reset on advance. |
| `Combat.Selected` | `int?` | MVP: side stage being farmed, chosen with `SelectStage` in `SCR_STAGE_SELECT`; null = frontier (issue #35). |
| `Progress.HighestCleared` | `int` | Highest stage completed on the frontier (all kills, or the boss), any stage, 02's meaning (issue #35); bounds `SelectStage` and the Awakening gate reads it. |
| `Progress.HighestFirstClearBoss` | `int` | Highest boss stage that paid first-clear diamonds (06); the only first-clear key (issue #35). |

All of these are saved (02 section 3.1; issue #7).

Enemy data per stage is derived, not stored: `Hp(s) = HP0 * HP_G^s`, `Gold(s) = GOLD0 * GOLD_G^s`, `Exp(s) = EXP0 * EXP_G^s`, all evaluated by repeated multiplication from `GeometricTable`s (00 section 8 item 1). Chapter `= floor((s-1) / CHAPTER_WIDTH) + 1` and is cosmetic: the `chapters` table (09) carries background, enemy skin set and chapter name per chapter; `ChapterEntered` lets 07 play the transition. Bosses use the chapter's boss skin.

Worked enemy numbers with the constants this spec recommends in section 8.1 (`HP_G = 1.04`, `GOLD_G = 1.03`, `EXP_G = 1.025`):

| Stage | Chapter | Mob HP | Boss HP | Gold/kill | EXP/kill | DPS needed for a 3 s kill |
|---|---|---|---|---|---|---|
| 1 | 1 | 20.8 | - | 2.06 | 5.1 | 6.9 |
| 10 | 1 | 29.6 | 296 | 2.69 | 6.4 | 9.9 |
| 50 | 1 | 142 | 1.42e3 | 8.77 | 17.2 | 47 |
| 100 | 2 | 1.01e3 | 1.01e4 | 38.4 | 59.2 | 337 |
| 240 | 5 | 2.46e5 | 2.46e6 | 2.40e3 | 1.86e3 | 8.2e4 |
| 430 | 9 | 4.30e8 | 4.30e9 | 6.52e5 | 2.05e5 | 1.43e8 |
| 550 | 11 | 4.67e10 | 4.67e11 | 2.26e7 | 3.95e6 | 1.56e10 |
| 1000 | 20 | 2.14e18 | 2.14e19 | 1.37e13 | 2.70e11 | 7.1e17 |

With the inherited `HP_G = 1.07` the stage-550 mob has `2.9e17` HP; section 8.4 explains why that curve cannot be reached in 30 days by this game's layers.

## 4. Can the hero die? No.

Decision: the hero has no death state in Combat, Dungeons or Tower. Enemies deal no damage to the hero in MVP. The only failure is a boss timer running out, which keeps the player farming the same stage (section 2.7).

Why: the reference game has no death; pillar 1 (always something to do, never something you must do) rules out a loop where an unattended phone sits on a death screen; and a death mechanic would need HP, DEF, enemy ATK curves and a revive timer, each of which is another `S` constant for the tool to validate before M0 can ship.

What that does to `HP` and `DEF`:

| Stat | MVP | P1 job |
|---|---|---|
| `HP` | Exists in the `Stat` enum and the `stats` table with `trainable = false`; not shown in `SCR_HERO`; `BASE_HP` is content but unused. | Tower (06) fights are survival fights: the Tower boss deals `towerBossDps(floor)` and the hero retreats (same outcome as a timeout) when `heroHp` reaches 0. `HP` Training then buys seconds against Tower bosses only. |
| `DEF` | Same as `HP`. | Reduces `towerBossDps` by `1 - DEF / (DEF + DEF_K)`. |

Keeping the two rows in the enum costs nothing and keeps 00 section 5.1, the save schema (02) and the `stats` table (09) stable. Cutting them later is a content change; adding them later would be a schema change. Six stats are trainable in M0 and MVP.

## 5. Stats, Training, Hero Level, Awakening

### 5.1 The eight stats and their Training curves

Training level `n` for a stat costs `TRAIN_COST0 * TRAIN_COST_G^n = 50 * 1.07^n` gold for `n -> n+1` (00 section 6.2, same curve for every stat). Effects:

| Stat | Per level (`TRAIN_PER_LVL`) | Effect | Cap (`TRAIN_CAP`, levels) | Tier | Kind |
|---|---|---|---|---|---|
| `ATK` | 0.05 | `atkMult = 1 + 0.05 n` | none | M0 | I |
| `HP` | - | not trainable in MVP (section 4) | - | P1 | - |
| `DEF` | - | not trainable in MVP (section 4) | - | P1 | - |
| `CRIT_RATE` | 0.0025 | `critRate = 0.0025 n` | 300 (75%) | M0 | S |
| `CRIT_DMG` | 0.01 | `critDmg = 1.5 + 0.01 n` | none | M0 | S |
| `ATK_SPD` | 0.01 | attack progress per tick `= 100 * (1 + 0.01 n)` | 100 (2 hits/s) | M0 | S |
| `GOLD_GAIN` | 0.02 | `goldMult *= 1 + 0.02 n` | none | M0 | S |
| `EXP_GAIN` | 0.02 | `expMult *= 1 + 0.02 n` | none | M0 | S |

```
critFactor  = 1 + critRate * (critDmg - 1)              # expected damage multiplier per hit
speedFactor = 1 + TRAIN_PER_LVL[ATK_SPD] * min(n, TRAIN_CAP[ATK_SPD])
```

Why these shapes:

- `ATK` is the smooth layer and keeps the inherited 5%. Total gold to reach level `n` is `50 * (1.07^n - 1) / 0.07`, so with gold income growing `GOLD_G^s` the level grows linearly in stage: `n ~ s * ln(GOLD_G) / ln(1.07)`, which is `0.43 s` with the recommended `GOLD_G = 1.03`.
- `CRIT_RATE` and `CRIT_DMG` only matter together, which is the first real spending decision the player makes. `CRIT_RATE` is capped so crit cannot become a certainty; `CRIT_DMG` is uncapped and grows like `ATK` once the rate is high. At the cap with `CRIT_DMG` level 250, `critFactor = 1 + 0.75 * 3.0 = 3.25`.
- `ATK_SPD` is capped at exactly 2x so the integer attack schedule stays simple and the boss math in section 2.8 stays readable. It is deliberately the cheapest early doubling: level 100 costs `50 * (1.07^100 - 1) / 0.07 = 6.1e5` gold in total, affordable late on day 1.
- `GOLD_GAIN` and `EXP_GAIN` are investment stats. A level of `GOLD_GAIN` is worth `ln(1 + 0.02/(1 + 0.02 n)) / ln(1.07)` future `ATK` levels; the bot (section 8.3) values it at half that to account for the delay.

`TrainStat(stat, count)` is all-or-nothing for `count` in `TRAIN_BULK_STEPS` (1, 10, 100) and `TrainStatMax(stat)` buys while affordable. Both use 01's `RejectCode` with the specific reason in `ApplyResult.Reason` (issue #11): `Locked`/`STAT_NOT_TRAINABLE` for `HP`/`DEF` in MVP, `Capped`/`STAT_AT_CAP` at the cap, `CannotAfford`/`NOT_ENOUGH_GOLD` otherwise. On success they publish `StatTrained(stat, newLevel)` and mark DPS dirty. Bulk cost is the closed form `50 * 1.07^n * (1.07^m - 1) / 0.07`, quoted and charged identically; `MaxAffordable` is computed by walking the `GeometricTable` (no logarithm, 00 section 8 item 1).

### 5.2 Hero Level

Hero EXP (`HERO_EXP`) is `Hero.Exp`, not a Wallet balance, though it flows through `Wallet.Apply` for the reason code (never spent manually, 00 section 5.2). Level thresholds follow 00: `HERO_LVL_EXP(l) = 100 * 1.08^l` EXP to go from level `l` to `l+1`.

Every EXP source (kills, offline catch-up, the EXP Dungeon) goes through `Wallet.Apply(+exp HERO_EXP, reason)`, which writes `Hero.Exp` and then calls `CheckHeroLevel()` in the same `Apply` or tick (00 section 8 item 9); there is no second grant path.

```
CheckHeroLevel():                                # called by Wallet.Apply after any HERO_EXP delta
  while Hero.Exp >= HeroLvlExp(Hero.Level):
    Hero.Exp -= HeroLvlExp(Hero.Level); Hero.Level += 1; MarkDpsDirty()
    publish LevelUp(Hero.Level)
```

Effect: the Hero Level term in 00 section 7 is the compounding (adopted by 00 decision 9, replacing the linear `1 + HERO_LVL_ATK_PCT * heroLevel`)

```
heroMult = HERO_LVL_MULT ^ heroLevel          # HERO_LVL_MULT = 1.02 (S), evaluated from a GeometricTable
```

Reason (section 8.4 has the numbers): Whisker Hero has no gear-tier layer, and gear tiers were the only layer in the sibling project whose growth compounded with stage. Without one compounding stage-fed layer the frontier stalls for a week at a time from day 7 on, under any enemy curve. Hero Level is the right place for it: it is fed by EXP, which grows with stage (`EXP_G`), it is automatic (no decision, no cost), and `HERO_LVL_MULT` is an `S` constant this spec sets. With `EXP_G = 1.025` and threshold growth `1.08`, hero level grows `~0.32 s + offline EXP`, so `heroMult ~ 1.02^(0.32 s) = e^(0.0063 s)`, which is 16% of the per-stage HP growth `ln 1.04 = 0.0392`. Combined with Training's declining linear term and Awakening, the stage-fed layers return roughly 40% of each stage's cost, and the time-fed layers (summons, Fish Board, Research) must supply the other 60%. That split is what keeps the 40% cap in reach (section 8.5). If `HERO_LVL_MULT` is ever raised above 1.025 the Hero Level share passes 40% by day 7.

### 5.3 Awakening (MVP)

| Rank | Stage gate (`AWAKEN_STAGES`) | Rock Candy cost (`AWAKEN_COST_ROCK_CANDY`) | Multiplier after | Unlocks |
|---|---|---|---|---|
| 1 | 50 | 100 | x1.5 | `SCR_RESEARCH` tab, companion slot 2 |
| 2 | 150 | 300 | x2.25 | companion slot 3 |
| 3 | 300 | 800 | x3.375 | companion slot 4 |
| 4 | 500 | 2000 | x5.06 | Rune slots 1-3 (P1) |
| 5 | 800 | 5000 | x7.59 | Rune slots 4-6 (P1) |

```
Awaken():                                                   # command, from SCR_HERO
  r = Hero.AwakenRank
  if r >= AWAKEN_STAGES.Length:                        return Reject(Capped, "AWAKEN_MAX_RANK")
  if Progress.HighestCleared < AWAKEN_STAGES[r]:       return Reject(Locked, "AWAKEN_STAGE_NOT_REACHED")
  if Wallet.RockCandy < AWAKEN_COST_ROCK_CANDY[r]:     return Reject(CannotAfford, "NOT_ENOUGH_ROCK_CANDY")
  Wallet.Apply(-AWAKEN_COST_ROCK_CANDY[r] ROCK_CANDY, AWAKENING)                   # reason per 00 contract 6 (issue #11)
  Hero.AwakenRank = r + 1; MarkDpsDirty()
  publish Awakened(Hero.AwakenRank)
```

Awakening is a button, not automatic, because it costs a currency. The gate reads `Progress.HighestCleared`; farming a side stage in `SCR_STAGE_SELECT` never lowers it. Companion slot unlocks are the reference game's class-change feel without a second mechanic; slot 1 is open from the start so the companion banner is never useless. The bot (section 8.3) pays each rank as soon as both gates pass. Cadence target (issue #43): every rank is affordable within `max_days_after_gate` days of the day the bot first reaches its stage gate, read from the `awakening_targets` content table (09; columns `rank`, `max_days_after_gate`, value 2 for every rank). That table is the one source both this spec's tool check (section 8.6, acceptance 21) and 06's acceptance read: the stage gate is the pacing lever, Rock Candy is a check. Rock Candy income is 06's (the Rock Candy Dungeon, 06 section 6.5), which the bot reads from content (section 8.3). Boss drops alone (`BOSS_ROCK_CANDY_P = 0.5`, one candy) give about 12 per day early and cannot carry it.

As built (issue #157): the rows are the `awakening` table (09), rank `r` is row `r`, and a tier without the rows (M0) has no ranks, so `Awaken` is rejected `Capped`/`AWAKEN_MAX_RANK` there. A rank above the table counts as the last rank (02 rule P3 clamps it on load). `Query` exposes `AwakenRank`, `AwakenMult` (the product of the rows' `mult` up to the rank, exactly 2.25 at rank 2), `NextAwakenStageGate` and `NextAwakenCost` (null and 0 at the last rank), `CanAwaken`, `CompanionSlotsUnlocked` (1 at rank 0, then `companion_slots_after`, at most `COMPANION_SLOTS`), `RuneSlotsUnlocked` (0 at rank 0, then `rune_slots_after`) and `ResearchUnlocked` (`feature_unlocks` `RESEARCH`, which the `Research` command reads); the pure forms are `AwakeningRules` for the bots and 04's slot checks. The bot's step-3 helpers (`TryAwaken`, `NextAwakenWithin`) exist but are not yet called by `greedy-f2p` (issue #214; the cadence is decision #249 items 3 and 5).

## 6. Research (MVP, Fish Coin sink)

Research is a flat list of nodes, each with independent levels, costs and caps. No tree edges in MVP: the reference game's tree is mostly presentation, and edges are one more table to validate. `SCR_RESEARCH` shows the nodes as a tree drawn from a `layoutRow`/`layoutCol` column in the `research` table (09); the sim ignores layout.

Cost of level `k -> k+1` of node `x` is `RES_COST0[x] * RES_COST_G[x]^k` Fish Coins, evaluated from a per-node `GeometricTable` and charged unrounded (Fish Coins are a `double`, like Training's gold; the UI floors the display). The last column is `RES_COST0 * RES_COST_G^(max - 1)` to three significant figures.

| Node id | Effect per level | Max level | `RES_COST0` | `RES_COST_G` | Cost of last level | Why it exists |
|---|---|---|---|---|---|---|
| `res_atk` | `researchPct += 0.05` | 50 | 150 | 1.20 | 1.14e6 | Direct DPS; makes Fish Coins matter to a player who ignores companions. |
| `res_gold_gain` | `goldMult *= 1 + 0.05 k` (one factor, not per level) | 20 | 250 | 1.40 | 1.49e5 | Second gold-gain source so gold income is not only a Training decision. |
| `res_mp_pool` | `MpPoolMax += 20` | 10 | 200 | 1.50 | 7.69e3 | Burst at spawn: more abilities fire before regen matters. |
| `res_mp_regen` | `MpRegen += 0.5 /s` | 10 | 300 | 1.50 | 1.15e4 | Companion uptime; the node every companion player wants first. |
| `res_tank_cap` | `TankCap += 5` | 10 | 200 | 1.50 | 7.69e3 | Bigger buffer between sessions (05). |
| `res_tank_refill` | `TankRefillS -= 0.4` (floor 2.0 s) | 10 | 300 | 1.60 | 2.06e4 | More fish per active minute (05). |
| `res_fish_cap` | `FishMaxLvl += 1` | 10 | 2500 | 2.00 | 1.28e6 | Opens the next fish level and its discovery payout; the Fish Board's growth engine (05 section 8). |
| `res_offline_cap` | `OfflineCapH += 2` | 6 | 500 | 1.50 | 3.80e3 | Only matters to players who skip a day; cheap on purpose. |

Research effects are read by the sim through `Query` (`MpPoolMax`, `MpRegenPerTick`, `TankCap`, `TankRefillTicks`, `FishMaxLvl`, `OfflineCapH`, `GoldMult`, `ResearchPct`). Node ids are the `research` table's lowercase `res_*` ids (09; issue #11). `Research(nodeId)` buys one level: rejects, in this order, `InvalidTarget`/`UNKNOWN_RESEARCH` (no such node in the content tier), `Locked`/`RESEARCH_LOCKED` (before Research's `feature_unlocks` gate (09), seeded at Awakening rank 1 per 00 decision 11, or before the node's `unlock_awaken_rank`; issue #45), `Capped`/`RESEARCH_MAXED`, `CannotAfford`/`NOT_ENOUGH_FISH_COIN`; charges with reason `RESEARCH`; publishes `ResearchLeveled(nodeId, newLevel, cost)`; marks DPS dirty when the node affects it.

As built (issue #158): `Locked` is checked before `Capped` so every call before rank 1 is `Locked` (acceptance 13), even for a node an odd save holds at its max. `ResearchRules` holds the pure rules and effect formulas for the command, `Query` and the bots. `Query` exposes `ResearchLevelOf(nodeId)`, `NextResearchCost(nodeId)` (0 when unknown or maxed), `CanResearch(nodeId)`, `ResearchEffectTotal(effect)`, `MpPoolMax`, `MpRegenPerTick`, `OfflineCapH` (read by catch-up), `GoldMult` (Training's factor times `1 + 0.05 k`) and `ResearchPct`. The three Fish Board effects are exposed as `ResearchTankCap`, `ResearchTankRefillTicks` (floor 2.0 s) and `ResearchFishMaxLvl`; the board itself keeps the Research-0 values until 05's issue #167 switches `TankCap`, `TankRefillTicks` and `FishMaxLvl` to them. A tier without the rows (M0) has no nodes, so every `Research` call is `InvalidTarget` there and every effect is its Research-0 value. No quest goal reads Research. `greedy-f2p` does not buy Research yet (issue #214; the cadence is decision #249).

`res_offline_cap` extends the 24 h base cap from 00 decision 3; it does not change the base. Catch-up (06) and 08's `OFFLINE_WINDOW_OVERLAP` check use the effective cap `OFFLINE_CAP_H + 2 h * res_offline_cap level` (issue #21).

Fish Coin income that pays for this is roughly `0.2 * (FishMaxLvl - 1)` coins per level-1 fish fully merged (05 section 8), about 1,800 to 2,500 per 2 h active day at Research 0, plus the Fish Coin Dungeon (06 section 6.3 reward table, which the bot reads from content). Against that income the costs above mean: all MP nodes to level 4 by day 3, `res_fish_cap` level 1 around day 3, level 2 around day 7, level 3 around day 14, level 4 around day 28, `res_atk` to about level 15 by day 30. The doubling cost of `res_fish_cap` is what turns linear coin income into a log-in-time Fish Board layer, which is the intended shape: fast at first, never finished.

## 7. The power formula

### 7.1 Formula

Same shape as 00 section 7, with the Hero Level term compounding (section 5.2) and the crit and speed factors made explicit. This is the **rated** DPS, `Query.RatedDps`: the expected steady-state value used by `SCR_HERO`, the wall check (section 2.8), the layer shares, the bots, the tools and 08's plausibility bound. It excludes transient buffs.

```
DPS = BASE_ATK
    * (1 + TRAIN_PER_LVL[ATK] * trainLvl[ATK])                       # Training        (gold)
    * HERO_LVL_MULT ^ heroLevel                                      # Hero Level      (EXP)
    * (1 + (equippedWeaponPct + sum_owned weaponPassivePct) / 100)   # Weapons         (summons)
    * (1 + sum_equipped skillDpsPct / 100)                           # Skills          (summons)
    * (1 + mpSufficiency * sum_equipped companionDpsPct / 100)       # Companions      (summons + Research)
    * (1 + (FISH_BOARD_DPS_PCT + missileDpsPct) / 100)               # Fish Board      (active play + Research); missileDpsPct is rated-only
    * (1 + researchPct)                                              # Research        (Fish Coins)
    * (1 + runePct)                                                  # Runes           (P1, 1.0 until then)
    * AWAKEN_MULT ^ awakenRank                                       # Awakening       (stage gates + Rock Candy)
    * critFactor * speedFactor                                       # Training, crit and speed stats
```

`mpSufficiency = min(1, (mpRegenPerSecond + sum_equipped MP_REGEN (mpRestored - mpCost) / cooldownSeconds) / sum_equipped other (mpCost / cooldownSeconds))` is the fraction of rated companion uptime the MP pool can sustain (section 2.4). The net MP an `MP_REGEN` companion restores counts as regen, and its own cost is not in the demand (04 section 8.2; issue #39); with no other companion equipped it is 1. `missileDpsPct` is 05's closed form (05 section 5.1) and is **rated-only** (issue #15): it never enters the tick loop, which deals missile damage as discrete hits. The sim keeps no trailing average; if `SCR_HERO` wants a live number, the runtime/view-model keeps a 60 s rolling sum of `FishMissileHit`, outside the save and `StateHash`. Skills' `skillDpsPct` are rated averages over their cooldown (04 section 7), so the formula is the expected steady-state DPS.

The tick loop realises that value without counting any layer twice (issue #10). A hero hit carries only the layers that have no discrete event; every discrete source deals a multiple of a unit built from it:

```
HeroHit       = BASE_ATK * Training * HeroLevel * Weapons
              * (1 + FISH_BOARD_DPS_PCT / 100)                       # passive board only (05 fishMult_live)
              * Research * Runes * Awakening                         # one non-crit hero hit, no buff
hero attack   = HeroHit * (1 + atkBuffPct / 100), then crit           # section 2.2; ATK buffs from skills and companions apply here only
SkillUnit     = HeroHit * critFactor * speedFactor                   # one second of expected hero attack damage; INSTANT/DOT per-cast numbers (04) are in % of it
CompanionUnit = SkillUnit * (1 + sum_equipped skillDpsPct / 100)     # companion DAMAGE per-cast numbers (04) are in % of it
missile       = HeroHit * f                                          # 05; rated through missileDpsPct
tap           = HeroHit * TAP_ZAP_DMG_PCT                            # not in the rated DPS; the bot never taps
```

At speed 1 with no crit `SkillUnit == HeroHit`, which is the unit 04's per-cast derivation states. `CompanionUnit` carries the skill factor so that hero + skills + companions multiply as the rated formula does instead of adding. Rule (issue #243): companion damage scales with the equipped skills. It may be revisited (an additive skills + companions term) once `tools/power_curve_model.py` reports layer shares. A `BUFF` skill or `BUFF_ATK` companion is rated through its informational `dps_pct` (04) and realised as the hero-attack buff; buff uptime overlap, MP-gating and missiles are the only parts of the rated value that are approximations. Test (acceptance 20): measured DPS over a long fight equals `RatedDps` within 1%.

`Query.KillTimeSeconds(stage)` is the rated time to kill one normal enemy, used by offline catch-up (06 section 7.1) and the bots: whole attacks at the rated per-attack damage plus the spawn delay, `attackSec = ATTACK_INTERVAL_TICKS / TICK_HZ / speedFactor`, `killTime = max(1, ceil(Hp(stage) / (RatedDps * attackSec))) * attackSec + SPAWN_DELAY_TICKS / TICK_HZ` (06 section 7.2's "3-hit kill = 3.5 s" at speed 1). Skills, companions and crits enter through `RatedDps`, their expected share per attack (issue #108).

### 7.2 Units

Percent fields (`weaponPct`, `skillDpsPct`, `companionDpsPct`, `FISH_BOARD_DPS_PCT`, `missileDpsPct`, `atkBuffPct`, per-cast `DamagePctPerCast`) are in percentage points (`40` means +40%) because that is how 04 and 05 tables and the UI state them. `researchPct`, `runePct`, `TRAIN_PER_LVL[*]` and `critRate` are fractions. `Query` exposes every factor separately for `SCR_HERO`'s breakdown panel and for the bot's layer-share report.

### 7.3 Caching

`RatedDps`, `HeroHit`, `SkillUnit`, `CompanionUnit` and the ten factors are cached in `Simulation` and recomputed lazily after `MarkDpsDirty()`, which is called by: any Training purchase, `LevelUp`, `Awakened`, `ResearchLeveled`, any weapon/skill/companion equip or copy gain or limit break (04), any Fish Board change (05: DPS recomputed at most once per command). Buffs are applied at hit time and do not dirty the cache. `Tick()` reads the cache only; a stat bought mid-fight applies to the next hit.

## 8. Layer-share budget, targets and the bot

### 8.1 The problem with the inherited curve

00 inherits `HP_G = 1.07` and `GOLD_G = 1.05` from the sibling project along with the day-stage targets 90/240/380/480/550. In the sibling, a gear-tier layer whose multiplier grew `1.045^stage` carried 73-77% of log-power from day 7 on; the other layers covered a residual of `ln(1.07/1.045) = 0.024` nats per stage. Whisker Hero has no gear tiers. Its layers are:

| Layer | Fed by | Growth shape over 30 days |
|---|---|---|
| Training (`ATK`, crit, speed, gold, EXP) | gold, which grows `GOLD_G^s` | level `~0.43 s`, multiplier linear in level, so **logarithmic in stage** |
| Hero Level | EXP, `EXP_G^s` | compounding (section 5.2): **exponential in stage**, `e^(0.0063 s)` |
| Awakening | stage gates | five steps of x1.5 |
| Weapons, Skills, Companions | pulls, a fixed number per day | saturating: each new rarity tier is a step, dupes are a slow linear climb |
| Fish Board | active minutes, Fish Coins | x1.5 per `res_fish_cap` level, costs doubling: **logarithmic in time** |
| Research | Fish Coins | linear in level, costs geometric: logarithmic in time |

Under `HP_G = 1.07` every stage costs `0.0677` nats and the time-fed layers together add about 0.05 to 0.1 nats per day after day 10. The scratch model (section 8.6) with the 00 constants gives stage 110 on day 1, 210 on day 7, 250 on day 30, with a wall from day 14 to day 30 of 960 active minutes (eight days at two hours). That is not a tuning miss; it is the shape of a stage curve built for a layer this game does not have.

Two things must change, and this spec recommends both:

1. **Gentler enemy curve.** `HP_G 1.07 -> 1.04`, `GOLD_G 1.05 -> 1.03`, `EXP_G 1.04 -> 1.025`. The gold gap to HP stays at 0.01 per stage so Training stays a minority layer (the sibling's gap was 0.02 and gave Training 10%; here the gap is smaller because gold has six stats to feed and no enhancement competitor). `HP0`, `GOLD0`, `EXP0`, `BASE_ATK` stay. Stage numbers get larger across the board; chapter and awakening gates still land on sensible days (section 5.3).
2. **One compounding stage-fed layer**: Hero Level at `HERO_LVL_MULT = 1.02` (section 5.2).

These constants are re-tagged `S` in section 14 and the tool in section 8.6 is their validator. Everything else inherited from the sibling stays as 00 states it.

### 8.2 Worked numbers (scratch model, seed 7, recommended constants)

Day-stage output and the share of `ln(DPS / BASE_ATK)` carried by each factor. The model is the Python sketch section 8.6 specifies, run with the recommended constants and hand-entered ticket and Rock Candy income figures that predate 06's tables; the tool replaces them with 06's content income (section 8.3, issue #38). Treat the stage numbers as +/-15% until `tools/power_curve_model.py` in this repo replaces them.

| Day | Stage | DPS | Train | Hero | Weapons | Skills | Companions | Fish | Research | Awaken | Crit | Speed |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 240 | 1.1e5 | 20% | 28% | 9% | 4% | 3% | 15% | 5% | 4% | 3% | 7% |
| 3 | 330 | 3.4e6 | 19% | 28% | 7% | 5% | 5% | 18% | 4% | 3% | 6% | 5% |
| 7 | 430 | 1.5e8 | 16% | 27% | 9% | 8% | 6% | 16% | 3% | 5% | 6% | 4% |
| 14 | 480 | 9.0e8 | 15% | 27% | 8% | 8% | 7% | 14% | 3% | 7% | 7% | 4% |
| 30 | 550 | 1.6e10 | 14% | 26% | 10% | 8% | 7% | 15% | 4% | 6% | 6% | 3% |

Five other seeds land at 530-550 on day 30. Multipliers at day 30 (seed 7): Training x14, Hero x261, Weapons x8.3, Skills x5.5, Companions x4.4, Fish x24, Research x2.3, Awakening x3.4, crit x3.6, speed x2.0. Hero level 281, `ATK` Training level 262, `CRIT_RATE` at its cap, `ATK_SPD` at its cap since day 2, fish cap 12, Awakening rank 3.

Reading the table:

- **The 40% cap holds.** Hero Level is the largest layer at 26-28% throughout, by design (section 5.2). Nothing else passes 20%.
- **The 5% floor is missed by Research (3-5%), Companions on days 1-3, Skills on day 1, and speed on day 30.** Research needs `res_atk` to be roughly twice as strong or half as expensive; Companions are MVP and the bot gets slot 2 only at Awakening rank 1, so days 1-3 are expected to be low; `ATK_SPD` is capped at x2 and its share can only fall as the others grow, so speed is permanently exempt from the floor (issue #36). The MVP fix is chosen from section 9's levers using `tools/power_curve_model.py` output, run first (issue #36); candidates are `res_atk` at 0.08 per level and limit-break repricing (04 section 5), and the lever choice and any new values are pending the tool. The day-1 misses are unlock timing and are not checked (the floor applies on days 7 and 30 only); Research and Companions are M0 floor exemptions that report `WARN` (section 8.5, issue #12).
- **Pace.** Day 1 ends at stage 240 with the first wall at stage 70 in the first hour and Awakening rank 1 bought in the first session. Days 2-7 average 27 stages a day; days 7-14 average 7; days 14-30 average 4.4. The longest wall inside 30 days is 460 active minutes (about four sessions) starting around day 20. The sibling's criterion of no wall over 48 h wall-clock in the first 14 days holds; the day-20 wall does not, and MVP acceptance 18 now bounds walls on days 14-30 too (issue #36; fix pending the tool, section 12 question 2).

### 8.3 The greedy F2P bot policy

The bot is the reference player for every balance number. It plays `ACTIVE_MIN = 120` minutes a day in two 60-minute sessions (00 decisions 25 and 61; 10 section 3.3), is offline the rest, taps nothing, and spends everything as soon as it can on whatever gives the most log-DPS per unit of currency. In prose:

1. **Gold**: every active minute, buy Training levels while any stat is affordable, always the stat with the highest marginal `ln(DPS)` per gold. `GOLD_GAIN` and `EXP_GAIN` are valued at half the `ATK` levels (or hero levels) their income increase will eventually buy.
2. **Diamonds and tickets**: at the start of each day, spend every ticket on its banner, then spend diamonds on the banner with the highest expected `ln(DPS)` gain per diamond, where the expectation is over the banner's current published rate table and the gain of each pool item is evaluated against the current collection. Use a 10-pull when the chosen banner's 10x price is affordable, else singles. Re-evaluate after every pull.
3. **Rock Candy**: awaken as soon as stage gate and cost are both met. Otherwise, if the next gate is more than 50 stages away, spend on limit breaks (04 section 5) on whichever item gives the most `ln(DPS)` per candy, keeping nothing in reserve.
4. **Fish Coins**: every active minute, if companions are MP-starved buy `res_mp_regen`; else buy the node with the best `ln(DPS)` per coin among `res_atk`, `res_gold_gain` (payback proxy as for gold), `res_tank_*` (valued at 0.08 nats per level, the measured board growth per 10% more fish) and `res_fish_cap` (valued at `0.5 * ln 1.5 * fraction of board already at the cap`).
5. **Fish Board**: the attentive policy from 05 section 8 every active minute: release until the board is full or the tank is empty, merge every equal pair lowest level first, repeat; if the board is full with no merge and the tank is not empty, discard the lowest-level fish.
6. **Boss**: the first attempt is automatic; when walled, retry at the start of every active minute (the real player presses the button; `AttemptBoss` is free).
7. **Offline**: two offline gaps a day totalling 22 h (10 section 3.3), each caught up at `OFFLINE_RATE` for gold and EXP at the current stage, capped by the effective `OfflineCapH` (`OFFLINE_CAP_H` plus `res_offline_cap`); the tank fills by 05's `CatchUpTank` over the credited ticks, which reaches capacity; nothing else moves (06, issue #21).

Inputs the bot takes from other specs: `DIAMONDS_PER_DAY_F2P = 400` and `TUTORIAL_DIAMONDS = 1500` (00); summon ticket income per banner from 06's ticket sources and Rock Candy and Fish Coin income from 06 section 6.3's dungeon reward tables, all read from content, never hand-entered (issues #38, #43). Per-banner pull cadence (pulls per day per banner) is an output of this policy applied to that income, not an input; 04 retunes `BANNER_LVL_EXP` from it so the weapon banner reaches level 4 between day 20 and day 35 (issue #38; tuned in 04 section 4 to a 20-seed median of day 27). If 06's content changes the bot reruns and this section is regenerated.

Pseudocode, as `HeroCat.Bots` will implement it on the real sim (10):

```csharp
void EndOfActiveMinute(Simulation sim)
{
    // 1. gold -> training
    while (true)
    {
        Stat? best = null; double bestValue = 0;
        foreach (var stat in TrainableStats)
        {
            double cost = sim.Query.TrainCost(stat);
            if (cost > sim.State.Wallet.Gold) continue;
            double v = MarginalLnDps(sim, stat) / cost;      // closed forms, section 5.1
            if (v > bestValue) { best = stat; bestValue = v; }
        }
        if (best == null) break;
        sim.TrainStat(best.Value, 1);
    }
    // 3. rock candy
    if (sim.Awaken().Ok) { }
    else if (!NextAwakenWithin(sim, 50)) while (BestLimitBreak(sim) is var lb && lb != null && sim.LimitBreak(lb).Ok) { }
    // 4. fish coins
    SpendFishCoins(sim);
    // 6. boss
    if (sim.State.Combat.Boss == BossPhase.Walled) sim.AttemptBoss();
}
```

### 8.4 Why the targets moved

00 section 6.6 says the inherited targets may move if this section's redistribution requires it. It does. With the recommended constants the bot's table is:

| Day | 00 inherited | This spec (scratch model) |
|---|---|---|
| 1 | 90 | 240 |
| 3 | 240 | 330 |
| 7 | 380 | 430 |
| 14 | 480 | 480 |
| 30 | 550 | 550 |

Days 14 and 30 coincide by construction: `HP_G = 1.04` was chosen so that the layers this game actually has reach the inherited day-30 stage, and `HERO_LVL_MULT = 1.02` so that the day-14 to day-30 pace is not a dead stop. Days 1-7 are higher because a gentler curve makes the kill-rate-limited opening longer: with one-hit kills a stage takes 15 s, so 2 h can hold 480 stages, and the gentler curve keeps the hero near one-hit kills for longer. If the design wants day 1 back near 90, the lever is `HP0` (x6 moves day 1 down by about 45 stages) and it moves every later day by the same amount; the tool reports it, the decision is 00's.

The two alternatives considered and rejected: keeping `HP_G = 1.07` and accepting 110/210/250 with an eight-day wall (fails pillar 1 in practice); or making Training compound instead of Hero Level (Training is `I` in 00 and would exceed the 40% cap at any compounding rate that fixes the stall, because gold also feeds five other stats).

### 8.5 Layer-share rule as a test

The harness (10) computes `share[layer] = ln(factor[layer]) / ln(DPS / BASE_ATK)` from `Query.RatedDps`'s factors and asserts `share <= 0.40` for every layer on days 1, 7 and 30, and `share >= 0.05` on days 7 and 30 only (issue #12): day 1 is where a runaway layer shows first, but a day-1 floor mostly measures unlock timing. `Base` (`BASE_ATK`) is not a layer for the floor; crit is. Speed is permanently exempt from the floor in every tier, because `ATK_SPD` is capped at x2 and its share can only fall (issue #36); the ceiling still applies to it. Runes are not checked before P1. Per-tier floor exemptions live in `src/HeroCat.Bots/Acceptance/acceptance-targets.json` and report `WARN` instead of failing; the ceiling always gates. M0 exempts Research and Companions (the M0 harness runs without Companions and Research). The tier exemptions are removed when this section's retune lands.

### 8.6 `tools/power_curve_model.py` for this repo

A per-minute aggregate model, not the real sim, so it runs 30 days in under ten seconds and can sweep constants. It is the copy of `idle-rng-game/tools/power_curve_model.py` restructured as follows.

Inputs: `content/*.json` via `tools/content.py` (09 section 11), so a constant exists in exactly one place; CLI flags `--content DIR`, `--seed N` (default 7), `--days 30`, `--active-min 120`, `--sessions 1|2` (default 2), `--override NAME=VALUE` (repeatable, for sweeps), `--tickets W,S,C`, `--rock-candy-per-day`, `--fish-coin-per-day` (flat income instead of 06's dungeon tables), `--policy greedy|lazy|spender20|whale` (10 defines the last three; they need the shop tables), `--m0` (drops Companions, Research, dungeons, tickets, limit breaks and every content row tagged above M0, which removes Awakening), `--check [--seeds 1-20]` (the 20-seed sweep below; exits 1 on any `FAIL`). Until the MVP tables ship (`research`, `companions`, `dungeons`, quests), the tool uses their rows as this spec, 04 and 06 state them and prints a `fallback` note for each.

Model, per active minute: compute `dps()` from the section 7 formula using the closed-form `missileDpsPct` from 05; run kills at `killTime = ceil(hp / hitDamage) / speedFactor + SPAWN_S` with the boss check from section 2.8; apply the section 8.3 policy; once per day apply income grants, pulls (section 8.3 item 2, with the exact 04 algorithm including banner levels and the 10-pull guarantee), offline catch-up and tank fill. The Fish Board is simulated as a count per level with the attentive policy from 05 section 8 (not cell by cell; cell placement does not change throughput). Enemy and cost curves use `**` in Python; bit-exactness is the C# harness's job, not this tool's.

Outputs, printed and written as `tools/out/power_curve_<seed>.json`:

1. The day table for days 1, 2, 3, 5, 7, 10, 14, 21, 30: stage, DPS, each layer's multiplier, Training levels per stat, hero level, awaken rank, fish cap, research levels, pulls and L+ per banner, limit breaks.
2. The layer-share table (section 8.2 format) and a PASS/FAIL/WARN line per layer against the section 8.5 rule.
3. Wall report: first wall stage, longest wall in active minutes and the day it started, total walled minutes.
4. Economy report: cumulative gold earned and spent, diamonds spent per banner, Fish Coins earned (discovery, merges, dungeon) and spent per node, Rock Candy earned and spent.
5. A compact line `DAYTABLE 1:240 3:330 7:430 14:480 30:550` for 00 section 6.6 to paste.
6. Per-banner pull cadence: pulls per day on each banner (tickets and diamonds) and the day each banner level is reached, the input 04's `BANNER_LVL_EXP` retune and `gacha_model.py` read (issue #38).

`S` constants it validates and what it checks: `HP_G`, `GOLD_G`, `EXP_G`, `HERO_LVL_MULT` (day table within tolerance, 40%/5% rule, longest wall under 14 days <= 240 active minutes, longest wall opened on days 15-30 within `lateWallMaxActiveMinutes`, acceptance 18); `TRAIN_PER_LVL[CRIT_RATE/CRIT_DMG/ATK_SPD/GOLD_GAIN/EXP_GAIN]` and `TRAIN_CAP[*]` (each stat receives between 5% and 50% of Training gold over 30 days; none is bought to zero); `BOSS_TIME_TICKS` (first wall between stage 60 and 200); `TAP_ZAP_*` (a tapping variant clears a wall at most 2.5x faster than the idle bot); `EXP0`/`HERO_LVL_EXP` (hero level between 0.25 s and 0.45 s at every reported day); `AWAKEN_STAGES`, `AWAKEN_COST_ROCK_CANDY` (every rank affordable within `awakening_targets.max_days_after_gate` days of reaching its gate; issue #43); `RES_*` costs (every node has at least one level bought by day 7 and `res_fish_cap` reaches 2 by day 10); `BOSS_ROCK_CANDY_P` (boss drops are under 20% of Rock Candy income). The Fish Board constants are `fish_model.py`'s (05); the summon constants are `gacha_model.py`'s (04); this tool consumes both tools' outputs as inputs where it needs them.

Acceptance in CI (10): the median of 20 seeds within +/-20% of 00 section 6.6 on days 1, 7 and 30; the share rule; the wall rule.

## 9. Tuning levers

| Symptom | Lever | Side effect |
|---|---|---|
| Day 1 too fast | `HP0` up (x6 is about -45 stages on every day) | None on shares. |
| Late walls too long | `HERO_LVL_MULT` 1.02 -> 1.025 | Hero share rises toward 40%; check it. |
| Hero Level over 40% | `HERO_LVL_MULT` down, or raise `HERO_LVL_EXP` growth 1.08 -> 1.09 | Fewer stages/day late. |
| Research under 5% | `res_atk` to 0.08 per level, or `RES_COST_G[res_atk]` 1.20 -> 1.15 | Fish Coins drift away from `res_fish_cap`; rerun `fish_model.py`. |
| Companions under 5% | `MP_REGEN0` 2 -> 3, or lower `mpCost` (04) | Reduces the pull of `res_mp_regen`. |
| Training over 25% | `GOLD_G` 1.03 -> 1.025 | Fewer Training levels; `CRIT_RATE` cap reached later. |
| Gold feels useless late | `GOLD_G` up toward 1.035 | Training share rises. |
| Walls too frequent | `BOSS_TIME_TICKS` 300 -> 400, or `BOSS_HP_MULT` 10 -> 8 | Flatter feel; prefer this to touching `HP_G`. |
| Everything scales but feels samey | add Awakening ranks at 50-stage spacing with x1.25 | Cheap, high perceived value; shifts the Awakening share up. |

Never change `HP_G` to fix a local problem; it moves every stage at once and every other spec's worked numbers with it. Change the layer that misbehaves.

## 10. RNG streams used by combat

| Stream | Draws | When | Counter invariant | Consumer rule |
|---|---|---|---|---|
| `combat` | 0 in MVP | reserved: P1 enemy variance, skill proc chances | `== 0` | Only `HeroCat.Sim.Combat` may draw. |
| `crit` | 1 per stage hero hit | section 2.2, including hits at `critRate == 0` | `== Stats.HeroHits` | Only `HeroHit()`. Fish missiles, skills, companions and Tap Zap never draw. |
| `loot` | 1 per boss kill | `BOSS_ROCK_CANDY_P` roll | `== Stats.BossKills` | Only `Kill()` on a boss. P1 drop tables add draws and a new invariant. |
| `fish_spawn` | per 05 | release cell choice | 05 owns | Fish Board only. |
| `dungeon` | 1 per M0 run (06) | dungeon run seed, drawn by the M0 local `IDungeonSeedService` (00 contract 11, issue #25) | 06 owns | Dungeon seed service only. |
| `dungeon_run` | per dungeon fight (06) | temporary, reseeded from the run seed at `StartDungeon`; the hero's crit draws while `Combat.Mode == Dungeon` (issue #42) | none; discarded when the run ends | Dungeon fights only; `crit` and `Stats.HeroHits` are untouched by dungeon hits. |

The summon streams are not combat's: they stay in the save in every mode and are drawn only by `LocalSummonService` (00 decision 14). Draws are consumed even when the outcome is predetermined so the invariants above hold and the server can audit a save (08). These invariants are the validator's and 08's (02 section 3.4, issue #8). Stream seeds are derived in Runtime, not the sim: the first 8 bytes of SHA-256(`RngRoot` ++ UTF-8 stream name), where `RngRoot` is the immutable Guid set at profile creation (00 contract 2, issue #9); 02 persists `(seed, counter)` per name.

## 11. Analytics events

This section owns these events' fields (issue #245); every event also carries 02 section 14.1's envelope, and 02 section 14.4 indexes them. The sim publishes the event named in the last column and the Runtime sink maps it.

| Event | Fields | When |
|---|---|---|
| `stage_cleared` | `stage`, `isBoss`, `firstClear`, `diamondsGranted`, `attempts` (boss stages only), `ticksOnStage`, `dps` | `StageCleared`; feeds the 00 section 6.6 day table |
| `boss_failed` | `stage`, `bossHpLeftPct`, `attempt` | `BossFailed` (2.7); the first one on a stage is the wall hit (2.8) |
| `wall_broken` | `stage`, `wallSec`, `activeSecDuringWall`, `attempts`, `layersChangedDuringWall[]` | `StageCleared` of a boss stage that was `Walled` |
| `training` | `stat`, `levelFrom`, `levelTo`, `goldCost` | `StatTrained`; one per command, not per level (01) |
| `level_up` | `heroLevel` | `LevelUp` (5.2) |
| `awakened` | `rank`, `stage`, `rockCandyCost`, `rockCandyAfter` | `Awakened` (5.3) |
| `research_bought` | `researchId`, `level`, `fishCoinCost` | `ResearchLeveled` (6) |
| `currency_changed` | 06 section 13, reasons `KILL`, `BOSS`, `FIRST_CLEAR`, `TRAINING`, `RESEARCH`, `AWAKENING` | per wallet delta |

## 12. Open questions

1. **Adopt the recommended curve?** Resolved: adopted (00 decision 9); 03, 05, 06 and 10 write their worked numbers against it, and 00 section 6.6 is replaced by the tool's `DAYTABLE` line once `tools/power_curve_model.py` exists.
2. **Day-20 wall.** Resolved (issue #36): ship M0 as is; for MVP, run `tools/power_curve_model.py` first and pick the levers from section 9 that clear both the day-20 wall and the 5% floor misses (candidates: `res_atk` at 0.08 per level, limit breaks priced so the bot buys one every day from day 10, 04 section 5). The lever choice and values are pending the tool. Acceptance 18 bounds walls on days 14-30; speed is permanently exempt from the floor.
3. **Day 1 at 240 stages.** Fine for the genre; wrong if 07's tutorial or 06's first-clear diamond budget assumed ~90. Owner: 00. Default: keep; if 06 needs fewer first clears on day 1, raise `HP0` rather than `HP_G`.
4. **`HP`/`DEF` trainable only in P1.** Resolved: as section 4; the enum keeps all eight (00 decision 10).
5. **`res_offline_cap` versus decision 3.** Resolved: the node extends the cap; catch-up uses the effective cap `OFFLINE_CAP_H + 2 h * level` (issue #21).
6. **Companion slot unlocks tied to Awakening.** Resolved: as section 5.3 (00 decision 11).
7. **Auto boss retry** (`BOSS_AUTO_RETRY_S = 60`) is P1 here; 07 may want it in MVP for the idle-on-desk player. Default: P1.
8. **Tap Zap and the server plausibility check.** Resolved: a tapping player's stage-vs-time bound is up to 4x the idle bound during bosses (section 2.5), so 08 uses `DPS * (1 + TAP_ZAP_DMG_PCT * TAP_ZAP_MAX_PER_SEC)` as the upper bound (00 decision 26).

## 13. Acceptance criteria

1. From a fresh profile, the first `EnemySpawned` is at tick `SPAWN_DELAY_TICKS` and the first `HitLanded` at `SPAWN_DELAY_TICKS + ATTACK_INTERVAL_TICKS`; with `ATK_SPD` level 100 the hit interval is exactly 5 ticks for 1,000 consecutive hits.
2. Ten normal kills on stage 1 advance to stage 2 with one `StageCleared(1, 2, false)`; on stage 10 the 10th enemy spawns as a boss with `Hp == BOSS_HP_MULT * Hp(10)` and `BossTicksLeft == BOSS_TIME_TICKS`.
3. A boss that needs exactly `30 * speedFactor` non-crit hits is cleared; one needing one more hit fails at tick `BOSS_TIME_TICKS` after spawn and publishes `BossFailed(10, 1, remainingFraction)`; a hit landing on the final tick counts.
4. While `Walled`, mobs of the same stage spawn, pay `Gold(s) * GoldMult` and `Exp(s) * ExpMult`, and `KillsInStage` stays at `KILLS_PER_STAGE - 1`; `AttemptBoss` despawns the mob with no gold or EXP and the next spawn is the boss; `AttemptBoss` is rejected with `NotAvailable`/`NO_BOSS_TO_ATTEMPT` on a normal stage and `NotAvailable`/`BOSS_ALREADY_PENDING` while pending.
5. `crit.Counter == Stats.HeroHits` and `loot.Counter == Stats.BossKills` after a 1,000,000-tick fuzz run; fish missiles, skills, companions and Tap Zap consume no `crit` draws.
6. Over 100,000 hero hits with `CRIT_RATE` level 100 and `CRIT_DMG` level 50 (seed 7), the crit frequency is within 3 standard deviations of 0.25 and mean damage within 1% of `HeroHit * (1 + 0.25 * 1.0)`.
7. `TapZap(20)` in one second applies exactly `TAP_ZAP_MAX_PER_SEC` taps and reports 14 ignored; the next second accepts 6 again; each applied tap removes `HeroHit * TAP_ZAP_DMG_PCT`.
8. Queued fish missiles apply on the first `Fighting` tick after queueing, in order, without crit; a missile queued during `Spawning` is not lost, including across a save round trip; surplus damage beyond the kill is discarded.
9. `TrainStat(ATK, 10)` with insufficient gold changes nothing; with sufficient gold charges exactly the closed-form bulk cost and raises `atkMult` by `0.5`; `TrainStat(CRIT_RATE, 1)` at level 300 is rejected with `Capped`/`STAT_AT_CAP`; `TrainStat(HP, 1)` is rejected with `Locked`/`STAT_NOT_TRAINABLE` in MVP; a successful purchase publishes one `StatTrained`.
10. `critFactor`, `speedFactor`, `heroMult`, `awakenMult` and `researchPct` from `Query` equal hand-computed values for a fixture with `CRIT_RATE` 120, `CRIT_DMG` 80, `ATK_SPD` 100, hero level 50, Awakening rank 2, `res_atk` 7, within relative 1e-12.
11. A `Wallet.Apply(HERO_EXP)` crossing two thresholds in one grant raises the level by 2 and publishes two `LevelUp` events; `heroMult` equals `HERO_LVL_MULT^level` from the `GeometricTable` (bit-identical on x64, ARM64, IL2CPP per 10's golden-file test).
12. `Awaken()` is rejected with `Locked`/`AWAKEN_STAGE_NOT_REACHED` at `Progress.HighestCleared == 49`, with `CannotAfford`/`NOT_ENOUGH_ROCK_CANDY` at stage 50 with 99 candy, succeeds at 100 candy charging exactly 100 with reason `AWAKENING`, and opens companion slot 2.
13. `Research(res_fish_cap)` at level 0 charges 2,500 Fish Coins, at level 9 charges 1,280,000, at level 10 is rejected with `Capped`/`RESEARCH_MAXED`; before Awakening rank 1 every `Research` call is rejected with `Locked`/`RESEARCH_LOCKED`.
14. With four companions equipped, `MP_REGEN0 = 2`, costs 10 and cooldowns 100 ticks, the sim fires 12 +/- 1 companion abilities per minute (not 24); after `res_mp_regen` level 4 it fires 24 per minute. Replacing one with an `MP_REGEN` companion that restores 16 MP per cast (same cost and cooldown), the other three fire 15.6 +/- 1 abilities per minute and `mpSufficiency` equals `2.6 / 3` (issue #39).
15. Deterministic: two fresh profiles with seed 7, the same scripted commands and `Advance(1_000_000)` produce field-equal `GameState` graphs and identical event sequences; a `DeepClone` taken mid-boss continues identically.
16. Bot: the median of 20 seeds from `tools/power_curve_model.py` lands within +/-20% of the section 8.4 table on days 1, 7 and 30; the C# bot (10) on the real sim lands within +/-15% of the Python model on the same days.
17. Shares: on days 1, 7 and 30 no layer exceeds 40% of `ln(DPS / BASE_ATK)`; on days 7 and 30 every layer except `Base` and speed is at or above 5% (speed is permanently exempt, issue #36), except the tier's exemptions in `acceptance-targets.json`, which report `WARN` (M0: Research, Companions; issue #12).
18. Walls: first wall between stage 60 and 200 in every seed; no wall opened in the first 14 days exceeds 240 active minutes; no wall opened on days 15-30 exceeds the late-wall bound `lateWallMaxActiveMinutes` in `acceptance-targets.json` (issue #36; the bound's value is set from the tool's output and pending).
19. Perf: `Advance(1_000_000)` with `NullEventSink` from a stage-300 profile with six skills and four companions equipped completes in under 2 s in Release; `Advance(100_000)` with the default event mask allocates under 128 bytes per kill.
20. Rated vs realised (issue #10): for a fixture with two `INSTANT` skills, one `DOT` skill, two `DAMAGE` companions at full MP, a static board (no merges, so no missiles), `ATK_SPD` 50 and `CRIT_RATE` 120 / `CRIT_DMG` 80 with hero-attack damage counted at its expected crit value, total damage dealt over a 100,000-tick fight against an unkillable enemy, divided by the fight's seconds, equals `Query.RatedDps` within 1%.
21. Awakening cadence (issue #43): in the 20-seed sweep, every Awakening rank the bot's stage reaches within 30 days is bought no more than `awakening_targets.max_days_after_gate` days after the day its `AWAKEN_STAGES` gate is first reached; the tool and the bot read the same `awakening_targets` rows.
22. Stage select (issue #35): at frontier stage 57 with `KillsInStage == 4`, `SelectStage(56)` succeeds and `SelectStage(58)` is rejected with `Locked`/`STAGE_NOT_CLEARED`; `SelectStage(0)`, a negative stage and `SelectStage(STAGE_CAP + 1)` are rejected with `BadArgument`/`STAGE_OUT_OF_RANGE`, and any `SelectStage` while `Combat.Mode == Dungeon` with `NotAvailable`/`DUNGEON_RUN_IN_PROGRESS` (issue #159); kills on stage 56 pay `Gold(56) * GoldMult` and `Exp(56) * ExpMult`, spawn no boss and leave `Combat.Stage`, `KillsInStage`, `Combat.Boss` and `Progress.HighestCleared` unchanged; `SelectStage(null)` resumes stage 57 at 4 kills; on a walled boss stage the `Walled` phase survives a select and return, and `AttemptBoss` while a side stage is selected clears `Combat.Selected` and spawns the frontier boss.

## 14. Constants introduced

Every constant this spec adds or changes, for lifting into 00 section 6. Tag `S` means `tools/power_curve_model.py` validates it as described in section 8.6.

| Name | Value | Kind | Section | Note |
|---|---|---|---|---|
| `HP_G` | **1.04** (was 1.07, I) | **S** | 8.1 | Re-tag and change proposed. |
| `GOLD_G` | **1.03** (was 1.05, I) | **S** | 8.1 | Re-tag and change proposed. |
| `EXP_G` | **1.025** (was 1.04) | S | 8.1 | Change proposed. |
| `HERO_LVL_MULT` | 1.02 | S | 5.2 | Replaces `HERO_LVL_ATK_PCT = 0.01` (linear) with a compounding term. |
| `ATTACK_PROGRESS_UNIT` | 100 | I | 2.1 | Integer fixed point for attack speed. |
| `TRAIN_PER_LVL[CRIT_RATE]` | 0.0025 | S | 5.1 | |
| `TRAIN_PER_LVL[CRIT_DMG]` | 0.01 | S | 5.1 | |
| `TRAIN_PER_LVL[ATK_SPD]` | 0.01 | S | 5.1 | |
| `TRAIN_PER_LVL[GOLD_GAIN]` | 0.02 | S | 5.1 | |
| `TRAIN_PER_LVL[EXP_GAIN]` | 0.02 | S | 5.1 | |
| `TRAIN_CAP[CRIT_RATE]` | 300 | S | 5.1 | 75% crit rate. |
| `TRAIN_CAP[ATK_SPD]` | 100 | S | 5.1 | 2 hits/s. |
| `CRIT_DMG_BASE` | 1.5 | S | 5.1 | |
| `HP`, `DEF` trainable | false (MVP) | - | 4 | P1 Tower job. |
| `DEF_K` | 100 (P1) | S | 4 | `DEF / (DEF + DEF_K)` damage reduction in Tower. |
| `MP_POOL0` | 100 | S | 2.4 | |
| `MP_REGEN0` | 2.0 per second | S | 2.4 | |
| `BOSS_ROCK_CANDY_P` | 0.5 | S | 2.6 | One Rock Candy per boss kill at this chance, `loot` stream. |
| `BOSS_AUTO_RETRY_S` | 60 (P1) | S | 2.7 | |
| `AWAKEN_UNLOCKS` | companion slots 2/3/4 at ranks 1/2/3, Rune slots at 4/5 | - | 5.3 | |
| `research` table (`res_*` nodes) | section 6 (8 nodes: cost0, growth, max, effect) | S | 6 | `res_fish_cap` validated jointly with 05's tool. |
| `awakening_targets` table (`rank`, `max_days_after_gate`) | 2 for every rank | S | 5.3 | Awakening cadence target read by the tool, 06 and the bots (issue #43). Ticket and Rock Candy income are read from 06's content, not assumed (issues #38, #43). |
| Day-stage targets | 240 / 330 / 430 / 480 / 550 | S | 8.4 | Replaces 00 section 6.6 once the tool confirms. |
