## 03 - Combat and Progression

This spec owns the per-tick combat loop in `HeroCat.Sim`, the stage and boss rules, the eight `Stat`s and their Training curves, Party Level, Awakening, Research, and the power formula that ties every layer together. It also specifies `tools/power_curve_model.py`, the greedy F2P bot that validates every `S` constant below and produces the day-stage table in 00 section 6.6. Where this spec and `00-overview.md` disagree, 00 wins.

Tiers covered: M0 (the five-slot party in the tick loop (00 decisions 107, 109), combat, stages, Tap Zap, fish missiles, Training, Party Level), MVP (Awakening, Research), P1 (Runes term, auto boss retry, Tower survival fights).

The party model (00 decision 109): the party is `PARTY_SLOTS = 5` slots, all open from the start at every tier. Each occupied slot's companion auto-attacks with that slot's weapon, auto-casts that slot's skill and casts its own ability from that slot's MP pool; Training and Party Level are party-wide; Awakening is per slot. There is no hero in combat. This spec describes the party model; the sim, content, bots, golden files and tools still implement the single-hero model until issue #309 lands, so every "As built" note below describes the hero-model code unless it says otherwise.

Depends on: 00 (all constants and names), 02 (`GameState` fields this spec reads and writes), 04 (weapon/skill/companion data the tick loop consumes), 05 (fish missile queue and `FISH_BOARD_DPS_PCT`), 06 (Rock Candy and Fish Coin income the bot assumes).

## 1. Design goals

1. One `Tick()` function, one `GameState`. Everything observable is a pure function of `(ContentDb, GameState)` plus the applied commands and ticks (00 section 8 item 1 and 5).
2. The party never dies (section 4). Progress is monotone; the only "failure" is a boss timing out, which costs nothing but the timer.
3. Every layer of the power formula is worth buying at every point in the first 30 days: no layer above 40% of log-power at day 30, none below 5% (00 section 7). Section 8 checks this with the bot and section 8.5 reports where the current constants miss.
4. Fast enough for bots: `Advance(1_000_000)` under 2 s in Release (AC19, 10); the client tick allocates nothing when no event is masked in. As built (issue #160): `Simulation.CombatEvents` (a `CombatEventMask`) says which per-tick combat events the loop emits, and the loop checks it before it allocates one. `Default` is `EnemySpawned | FishMissileHit | TapZapped` (one per spawn or player input); `HitLanded`, `SkillCast` and `CompanionAbilityUsed`, which the loop raises several times per kill on its own, are opt-in, and the client sets `All` for its damage numbers and cast VFX. `None` is the null sink of AC19. The mask changes no state (the hash, quests and the ledger never read events); kill, stage, boss and currency events are not maskable.

## 2. Tick loop

### 2.1 Entry points and per-tick order

```csharp
public sealed partial class Simulation
{
    public Simulation(GameState state, ContentDb db);
    public ApplyResult Apply(Command cmd);          // validate, mutate, emit; a reject changes nothing (01 section 2.2)
    public void Advance(int ticks);                 // runs `ticks` ticks (1 / TICK_HZ s each); commands cannot interleave
    public IReadOnlyList<Event> DrainEvents();      // moves the events buffered since the last drain out
    public Snapshot GetSnapshot();                  // taken at the end of the last Advance or Apply
    public CombatEventMask CombatEvents { get; set; } = CombatEventMask.Default;   // which per-tick combat events are emitted
}
```

There is no public `Tick()`: one tick is the private body of `Advance`, written `Tick()` below. Events go into the sim's own buffer (capacity `EVENT_BUFFER_MAX`, kills coalesced past `EVENT_COALESCE_AT`, 01 section 2.3) and the caller drains it; there is no event sink. `CombatEventMask` (section 1 goal 4, criterion 19) gates the per-tick cosmetic events before they are allocated: `Default` is `EnemySpawned | FishMissileHit | TapZapped`; `HitLanded`, `SkillCast` and `CompanionAbilityUsed` are opt-in (the client sets `All`, 07 section 4.3), and `None` is the null sink. Milestone events (kills, stage, boss, currency) are never masked, and masking changes no state.

Order inside one tick. The order is a contract: tests in 10 assert it, and the server re-simulator (08) relies on it.

```
Tick():
  Time.Tick += 1
  for slot in 0 .. PARTY_SLOTS-1:                                          # 1. MP regen, every pool (2.4)
    Combat.Mp[slot] = min(MpPoolMax, Combat.Mp[slot] + MpRegenPerTick)
  TickCooldowns()                                                          # 2. every slot's skill and companion cooldown -= 1
  switch Combat.Phase:
    Spawning:
      Combat.SpawnTimer -= 1
      if Combat.SpawnTimer == 0: Spawn()
    Fighting:
      ApplyQueuedFishMissiles()                                            # 3. damage events queued by 05 since last tick
      ApplyDots()                                                          # 4. every active DOT, any slot's (2.3)
      if Combat.EnemyHp <= 0: Kill(); break
      for slot in 0 .. PARTY_SLOTS-1 in order:                             # 5. slot order is the party's action order
        if Party.Slots[slot].Companion == null: continue                   #    an empty slot does nothing
        SlotAttack(slot)                                                   # 5a. auto-attack with ATK_SPD (2.2)
        if Combat.EnemyHp <= 0: Kill(); break
        AutoCastSkill(slot)                                                # 5b. the slot's skill, at most one cast (2.3)
        if Combat.EnemyHp <= 0: Kill(); break
        CastCompanionAbility(slot)                                         # 5c. the slot's ability, from its own pool (2.4)
        if Combat.EnemyHp <= 0: Kill(); break
      ApplyTapZaps()                                                       # 6. taps received since last tick, capped
      if Combat.EnemyHp <= 0: Kill(); break
      if Combat.EnemyIsBoss:
        Combat.BossTicksLeft -= 1
        if Combat.BossTicksLeft == 0: BossFail()                           # 7. timer check last, so a hit on the final tick counts
  TickBuffs()                                                              # 8. party ATK, gold and slot ATK buffs -= 1
```

`Kill(); break` ends the `Fighting` branch for this tick wherever it occurs, inside the slot loop included: once an attack, skill or ability kills the enemy, the later slots neither attack, cast nor draw `crit` that tick, and steps 6-7 are skipped (step 8 still runs). Their attack progress is reset by the next `Spawn()`; their cooldowns already ran down in step 2. Each slot casts at most one skill and one ability per tick; there is no party-wide one-cast-per-tick limit (00 decision 109).

State field names and shapes are 02's (section 3.1, issue #7): `Time.Tick`, `Combat.*`, `Progress.*`, `Party.*`, `Stats.*`. The per-slot combat arrays (`AttackProgress`, `SkillCooldowns`, `CompanionCooldowns`, `Mp`, `SlotAtkBuffPct`, `SlotAtkBuffTicksLeft`) are sized `PARTY_SLOTS` and indexed by party slot. Everything this loop reads or writes lives in the in-memory `GameState`, so `DeepClone` and replay are exact. The save drops only the transient fight state 02 section 3.3 lists, which lives in `Combat.Fight` (02's `FightState`; the pseudocode below writes `Combat.EnemyHp`, `Combat.Phase`, `Combat.BossTicksLeft` and the like as shorthand for `Combat.Fight.EnemyHp` and so on): enemy HP, `Phase`, `SpawnTimer`, `AttackProgress[]`, `ActiveDots`, the party and slot buffs, `PendingTaps`, `TapWindowTick`. It keeps `Combat.Boss`, `Combat.BossFailsOnStage`, `Combat.Selected`, `Combat.Mp[]`, the skill and companion cooldowns and `Combat.MissileQueue`. On load the party faces a fresh mob and a `Pending` boss respawns as the boss.

Dungeon runs (MVP, 06) are ticked in this same loop, not resolved instantly (issue #42): `StartDungeon` switches `Combat.Mode = Dungeon` and the floor's single boss is fought by the whole party with steps 1-8 above under the dungeon's time limit, except steps 3 and 6: the missile queue is not drained in that mode (section 2.5), and there is no Tap Zap (`TapZap` is rejected `(NotAvailable, DUNGEON_RUN_IN_PROGRESS)`, as are `AttemptBoss` and `SelectStage`, so no input changes the run's outcome; issue #172). The boss is a separate transient fight (`Combat.DungeonRun.Fight`); `Combat.Fight` is not ticked during the run. In that mode the kill and timeout branches hand off to 06 (`DungeonCleared`, which debits the ticket and grants the reward, or `DungeonFailed`, which costs nothing; 00 decision 16) instead of `Kill()`/`BossFail()`, so no stage, loot, first-clear or wall state changes; the slot hits' crit draws come from the temporary `dungeon_run` stream (section 10), and the frontier fight resumes unchanged when the run ends.

`ATTACK_PROGRESS_UNIT = 100` (integer fixed point). Every slot gains the same party-wide amount per tick. At `ATK_SPD` level 0 a slot gains 100 units per tick and attacks every `ATTACK_INTERVAL_TICKS = 10` ticks, one attack per second. At `ATK_SPD` level `n` the gain is `100 * (1 + TRAIN_PER_LVL[ATK_SPD] * n)` rounded to an integer, so level 100 (cap, +100%) is 200 units per tick and one attack every 5 ticks. Integer arithmetic keeps the hit schedule identical on every platform; a `double` accumulator would not (00 section 3, determinism row). `Spawn()` zeroes every slot's progress, so all occupied slots attack on the same ticks unless a companion was equipped mid-fight; the slot hits of one tick are an **attack round**.

### 2.2 Slot hit and crit

A slot hit is one companion's auto-attack (00 decision 109).

```
SlotAttack(slot):
  Combat.AttackProgress[slot] += ATTACK_PROGRESS_UNIT + AtkSpdBonusUnits    # party-wide ATK_SPD
  if Combat.AttackProgress[slot] < ATTACK_INTERVAL_TICKS * ATTACK_PROGRESS_UNIT: return
  Combat.AttackProgress[slot] -= ATTACK_INTERVAL_TICKS * ATTACK_PROGRESS_UNIT
  dmg = Query.SlotHit(slot)                                 # cached, section 7.3
      * (1 + Combat.SlotAtkBuffPct[slot] / 100)             # the slot's own BUFF skill (2.3)
      * (1 + Combat.AtkBuffPct / 100)                       # the party ATK buff from BUFF_ATK companions (2.4); buffs apply here only
  u = crit.NextDouble()                                     # exactly one draw per slot hit that lands, always
  Stats.PartyHits += 1
  isCrit = u < CritRate
  if isCrit: dmg *= CritDmg
  Combat.EnemyHp -= dmg
  publish HitLanded(slot, dmg, isCrit, Combat.EnemyHp)      # opt-in event (CombatEventMask, section 1 goal 4)

CritRate = min(TRAIN_CAP[CRIT_RATE] * TRAIN_PER_LVL[CRIT_RATE], TRAIN_PER_LVL[CRIT_RATE] * trainLvl[CRIT_RATE])   # 0 .. 0.75, party-wide
CritDmg  = CRIT_DMG_BASE + TRAIN_PER_LVL[CRIT_DMG] * trainLvl[CRIT_DMG]                                       # 1.5 at level 0, party-wide
```

The `crit` stream is consumed once per slot hit that lands, including hits at `CritRate == 0`, in slot order within a tick, so `crit.Counter == Stats.PartyHits` holds for the validator and the server audit (02, 08; issue #8). A slot whose attack does not happen because an earlier slot killed the enemy that tick (section 2.1) draws nothing and does not count. Fish missiles, skills, companion abilities and Tap Zap do not crit and consume no `crit` draws. Slot hits in a dungeon fight draw from `dungeon_run` instead and do not count in `Stats.PartyHits` (section 2.1, issue #42).

### 2.3 Skills (auto-cast, one per slot)

Each party slot holds at most one skill (`Party.Slots[slot].Skill`; 00 decision 109), and a skill scales with its own slot's attack. Skill data comes from 04: `dpsPct` (rated average DPS as % of its slot's auto-attack DPS), `castType` (`INSTANT`, `DOT`, `BUFF`), `cooldownTicks`, and the per-cast numbers derived from them. The tick loop only needs:

```
AutoCastSkill(slot):                                        # step 5b; the slot holds a companion
  s = Skill(Party.Slots[slot].Skill); if s == null: return
  if Combat.SkillCooldowns[slot] > 0: return
  Cast(slot, s); Combat.SkillCooldowns[slot] = s.CooldownTicks
  publish SkillCast(slot, s.Id)                             # opt-in event (CombatEventMask, section 1 goal 4)
```

- `INSTANT`: `EnemyHp -= Query.SlotUnit(slot) * s.DamagePctPerCast / 100` (`SlotUnit`, section 7.1).
- `DOT`: adds a `DotInstance(slot, ticksLeft, damagePctPerTick)` to `Combat.ActiveDots` (max `PARTY_SLOTS` entries, oldest replaced); `ApplyDots()` (step 4) deals `Query.SlotUnit(dot.Slot) * damagePctPerTick / 100` per live instance. Since `duration_ticks <= cooldown_ticks` (04), each slot has at most one live DOT, so the cap never binds.
- `BUFF`: sets the slot's own buff, `Combat.SlotAtkBuffPct[slot] = max(current, s.BuffPct)` with `Combat.SlotAtkBuffTicksLeft[slot] = s.DurationTicks`; a weaker cast never shortens it. It multiplies that slot's auto-attack only, not other slots, casts, abilities, missiles or taps (section 7.1).

A skill in an empty slot is idle (00 decision 109). Casting requires a live enemy (`Phase == Fighting`). During `Spawning` cooldowns still tick down, so a skill that comes off cooldown between enemies fires on the first tick of the next fight. This is why skills feel like a burst at spawn and why the boss timer check is last in the tick.

### 2.4 Companions and the MP pools (M0, 00 decisions 107, 109)

Companions (04 section 8) have `abilityType` in `DAMAGE`, `BUFF_ATK`, `BUFF_GOLD`, `MP_REGEN`, an `mpCost`, a `cooldownTicks` and a magnitude. Each slot has its own MP pool, `Combat.Mp[slot]`, and its companion pays only from it:

```
CastCompanionAbility(slot):                                 # step 5c
  c = Companion(Party.Slots[slot].Companion)
  if Combat.CompanionCooldowns[slot] > 0: return
  if Combat.Mp[slot] < c.MpCost: return                     # MP-gated: the ability waits, cooldown does not restart
  Combat.Mp[slot] -= c.MpCost
  Apply(slot, c); Combat.CompanionCooldowns[slot] = c.CooldownTicks
  publish CompanionAbilityUsed(slot, c.Id, Combat.Mp[slot]) # opt-in event; the slot's own pool after the cast

MpPoolMax      = MP_POOL0 + RES_MP_POOL_PER_LVL * research[res_mp_pool]                  # the same for every pool
MpRegenPerTick = (MP_REGEN0 + RES_MP_REGEN_PER_LVL * research[res_mp_regen]) / TICK_HZ   # the same for every pool
```

`Apply(slot, c)`, every magnitude scaled by the companion's `LevelMult` (04 section 5.1):

- `DAMAGE` deals `Query.CompanionUnit(slot) * c.DamagePctPerCast / 100` (section 7.1).
- `BUFF_ATK` buffs the whole party: `Combat.AtkBuffPct = max(current, buffPct)` with `Combat.AtkBuffTicksLeft = durationTicks`, strongest active wins, a weaker cast never shortens it. It multiplies every slot's auto-attack (section 2.2), not casts, abilities, missiles or taps.
- `BUFF_GOLD` has its own party slot (`Combat.Fight.GoldBuffPct` / `GoldBuffTicksLeft`, strongest active wins) and multiplies the kill's gold by `1 + GoldBuffPct / 100` in `Kill()`.
- `MP_REGEN` restores `mp_restored * LevelMult` to every one of the five pools, its own included (after its own cost is paid), each never past `MpPoolMax`. `CompanionAbilityUsed` carries the caster's pool after the cost and the restore.

`TickBuffs` (step 8) runs the party ATK buff, the gold buff and every slot's ATK buff down. Empty slots and unowned ids are skipped (an empty slot's pool still regenerates and still receives `MP_REGEN` restores). The pool belongs to the slot: swapping the slot's companion keeps `Combat.Mp[slot]`. `MpPoolMax` and `MpRegenPerTick` read Research and are cached with the power breakdown (section 7.3; `ResearchLeveled` dirties it). Every tier has companions (the starter party, 00 decision 109), so step 1 runs in every tier; before Research (M0) the pools regenerate at `MP_REGEN0`.

With the defaults (`MP_POOL0 = 100`, `MP_REGEN0 = 0.5` per second per pool, every companion ability costing 10 MP on a 10 s cooldown) each companion wants 1 MP/s from a pool that regenerates 0.5 MP/s, so it fires at half its rated uptime until Research's MP nodes or an `MP_REGEN` companion close the gap (00 decision 110). That gap is why Research and `MP_REGEN` companions matter; section 6 prices it. The pools are `Combat.Mp[]` and are saved; they are not refilled by offline catch-up (06).

### 2.5 Fish missiles and Tap Zap

Fish missiles arrive as damage events queued by the Fish Board (05 section 5): `FishMissileQueue.Enqueue(hitFraction)` where `hitFraction = FISH_MISSILE_PCT[level] / 100`, a fraction of one `PartyHit` (section 7.1; 00 decision 109). The queue is `Combat.MissileQueue` and is persisted in the save (02), so a save taken while `Spawning` with missiles queued resumes and re-simulates identically (issue #7).

```
ApplyQueuedFishMissiles():
  while Combat.MissileQueue.Count > 0:
    f = Combat.MissileQueue.Dequeue()
    Combat.EnemyHp -= Query.PartyHit * f                    # no crit, no draw, no buff
    publish FishMissileHit(f)
```

Missiles queued while `Spawning` wait for the next enemy. While `Combat.Mode == Dungeon` the queue is not drained (the board fires nothing during a run, issue #40); anything still queued waits for the stage fight (05 section 5.1). A missile is never lost and never overkills into the next enemy; the surplus is discarded with the kill.

Tap Zap (M0): the client sends `TapZap(count)` once per frame with the number of taps since the last frame. The sim keeps `Combat.TapsThisSecond` and a `TapWindowTick`:

```
TapZap(count):                                              # command, validated like any other
  if Combat.Mode == Dungeon: return Reject(NotAvailable, "DUNGEON_RUN_IN_PROGRESS")   # no taps in a run (06 section 6.1)
  if Time.Tick - Combat.TapWindowTick >= TICK_HZ: Combat.TapWindowTick = Time.Tick; Combat.TapsThisSecond = 0
  allowed = max(0, TAP_ZAP_MAX_PER_SEC - Combat.TapsThisSecond)
  used = min(count, allowed); Combat.TapsThisSecond += used
  Combat.PendingTaps += used
  return Ok(used, ignored: count - used)

ApplyTapZaps():
  if Combat.PendingTaps == 0: return
  Combat.EnemyHp -= Query.PartyHit * TAP_ZAP_DMG_PCT * Combat.PendingTaps
  publish TapZapped(Combat.PendingTaps); Combat.PendingTaps = 0
```

At the cap, taps add `6 * 0.5 = 3` `PartyHit`s per second on top of the party's 1 to 2 attack rounds, so a tapping player is at most 2.5x to 4x an idle one during a boss. That is the intended size of the active edge; the bot never taps, so the targets in section 8 are idle-combat targets.

### 2.6 Spawn, kill, loot, stage advance

```
FoughtStage = Combat.Selected ?? Combat.Stage                 # side-stage farming (section 2.7, issue #35)

Spawn():
  isBoss = Combat.Selected == null and Combat.Boss == Pending   # a selected stage never spawns a boss
  Combat.EnemyIsBoss = isBoss
  Combat.EnemyMaxHp = Combat.EnemyHp = isBoss ? BOSS_HP_MULT * Hp(Combat.Stage) : Hp(FoughtStage)
  Combat.AttackProgress[*] = 0                                # every slot starts a fresh attack interval
  Combat.BossTicksLeft = isBoss ? BOSS_TIME_TICKS : 0
  Combat.Phase = Fighting
  publish EnemySpawned(FoughtStage, isBoss, Combat.EnemyMaxHp)

Kill():
  s = FoughtStage; isBoss = Combat.EnemyIsBoss
  gold = Gold(s) * GoldMult * (isBoss ? BOSS_GOLD_MULT : 1)
  exp  = Exp(s)  * ExpMult
  Wallet.Apply(+gold GOLD, isBoss ? BOSS : KILL)
  Wallet.Apply(+exp PARTY_EXP, isBoss ? BOSS : KILL)       # the only EXP path: writes Party.Exp, runs CheckPartyLevel (5.2), may publish LevelUp
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
  if Combat.Mode == Dungeon:                                  return Reject(NotAvailable, "DUNGEON_RUN_IN_PROGRESS")   # stage fields frozen (02 section 3.3)
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

A boss is cleared iff the damage dealt inside `BOSS_TIME_TICKS` reaches `BOSS_HP_MULT * Hp(s)`. Ignoring skills, companion abilities, missiles and crit variance, the party lands `BOSS_TIME_TICKS / ATTACK_INTERVAL_TICKS * speedFactor = 30 * speedFactor` attack rounds in 30 s, each worth one `PartyHit` (section 7.1), so:

```
clearable(s)  <=>  BOSS_HP_MULT * Hp(s) <= 30 * speedFactor * PartyHit * critFactor
              <=>  Hp(s) / (PartyHit * critFactor * speedFactor) <= 3 s
```

In words: **if a normal mob on this stage takes more than three seconds of the party's auto-attack DPS, the boss cannot be cleared by auto-attacks alone.** Skills, companion abilities and missiles are already inside the rated `DPS(s)` in section 7, so the full condition is `Hp(s) / DPS(s) <= 3 s` with `DPS` from the power formula. The sibling project's condition was 2 s with a 20 s timer; the 30 s timer (00 decision 4) buys one extra hit per mob, which is what lets the burst layers matter.

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

## 4. Can the party die? No.

Decision: the party has no death state in Combat, Dungeons or Tower (00 decisions 10, 109); no companion dies or is knocked out either. Enemies deal no damage to the party in MVP. The only failure is a boss timer running out, which keeps the player farming the same stage (section 2.7).

Why: the reference game has no death; pillar 1 (always something to do, never something you must do) rules out a loop where an unattended phone sits on a death screen; and a death mechanic would need HP, DEF, enemy ATK curves and a revive timer, each of which is another `S` constant for the tool to validate before M0 can ship.

What that does to `HP` and `DEF`:

| Stat | MVP | P1 job |
|---|---|---|
| `HP` | Exists in the `Stat` enum and the `stats` table with `trainable = false`; not shown in `SCR_PARTY`; `BASE_HP` is content but unused. | Tower (06) fights are survival fights: the Tower boss deals `towerBossDps(floor)` to one party-wide HP pool and the party retreats (same outcome as a timeout) when `partyHp` reaches 0. `HP` Training (party-wide) then buys seconds against Tower bosses only. |
| `DEF` | Same as `HP`. | Reduces `towerBossDps` by `1 - DEF / (DEF + DEF_K)`. |

Keeping the two rows in the enum costs nothing and keeps 00 section 5.1, the save schema (02) and the `stats` table (09) stable. Cutting them later is a content change; adding them later would be a schema change. Six stats are trainable in M0 and MVP.

## 5. Stats, Training, Party Level, Awakening

### 5.1 The eight stats and their Training curves

Training is party-wide (00 decision 109): one set of eight stat levels, and every level buffs all five slots. Training level `n` for a stat costs `TRAIN_COST0 * TRAIN_COST_G^n = 50 * 1.07^n` gold for `n -> n+1` (00 section 6.2, same curve for every stat). Effects:

| Stat | Per level (`TRAIN_PER_LVL`) | Effect | Cap (`TRAIN_CAP`, levels) | Tier | Kind |
|---|---|---|---|---|---|
| `ATK` | 0.05 | `atkMult = 1 + 0.05 n` | none | M0 | I |
| `HP` | - | not trainable in MVP (section 4) | - | P1 | - |
| `DEF` | - | not trainable in MVP (section 4) | - | P1 | - |
| `CRIT_RATE` | 0.0025 | `critRate = 0.0025 n` | 300 (75%) | M0 | S |
| `CRIT_DMG` | 0.01 | `critDmg = 1.5 + 0.01 n` | none | M0 | S |
| `ATK_SPD` | 0.01 | attack progress per tick `= 100 * (1 + 0.01 n)` | 100 (2 attacks/s per slot) | M0 | S |
| `GOLD_GAIN` | 0.02 | `goldMult *= 1 + 0.02 n` | none | M0 | S |
| `EXP_GAIN` | 0.02 | `expMult *= 1 + 0.02 n` | none | M0 | S |

```
critFactor  = 1 + critRate * (critDmg - 1)              # expected damage multiplier per slot hit
speedFactor = 1 + TRAIN_PER_LVL[ATK_SPD] * min(n, TRAIN_CAP[ATK_SPD])
```

Why these shapes:

- `ATK` is the smooth layer and keeps the inherited 5%. Total gold to reach level `n` is `50 * (1.07^n - 1) / 0.07`, so with gold income growing `GOLD_G^s` the level grows linearly in stage: `n ~ s * ln(GOLD_G) / ln(1.07)`, which is `0.43 s` with the recommended `GOLD_G = 1.03`.
- `CRIT_RATE` and `CRIT_DMG` only matter together, which is the first real spending decision the player makes. `CRIT_RATE` is capped so crit cannot become a certainty; `CRIT_DMG` is uncapped and grows like `ATK` once the rate is high. At the cap with `CRIT_DMG` level 250, `critFactor = 1 + 0.75 * 3.0 = 3.25`.
- `ATK_SPD` is capped at exactly 2x so the integer attack schedule stays simple and the boss math in section 2.8 stays readable. It is deliberately the cheapest early doubling: level 100 costs `50 * (1.07^100 - 1) / 0.07 = 6.1e5` gold in total, affordable late on day 1.
- `GOLD_GAIN` and `EXP_GAIN` are investment stats. A level of `GOLD_GAIN` is worth `ln(1 + 0.02/(1 + 0.02 n)) / ln(1.07)` future `ATK` levels; the bot (section 8.3) values it at half that to account for the delay.
- Every Training factor multiplies every slot equally, so its share of `ln(DPS)` is the same as under the hero model; the numbers above (gold totals, levels per stage) do not depend on party size.

`TrainStat(stat, count)` is all-or-nothing for `count` in `TRAIN_BULK_STEPS` (1, 10, 100) and `TrainStatMax(stat)` buys while affordable. Both use 01's `RejectCode` with the specific reason in `ApplyResult.Reason` (issue #11): `Locked`/`STAT_NOT_TRAINABLE` for `HP`/`DEF` in MVP, `Capped`/`STAT_AT_CAP` at the cap, `CannotAfford`/`NOT_ENOUGH_GOLD` otherwise. On success they publish `StatTrained(stat, newLevel)` and mark DPS dirty. Bulk cost is the closed form `50 * 1.07^n * (1.07^m - 1) / 0.07`, quoted and charged identically; `MaxAffordable` is computed by walking the `GeometricTable` (no logarithm, 00 section 8 item 1).

### 5.2 Party Level

The party has one level, party-wide (00 decision 109): it is fed by kill EXP, rises automatically and is never spent. Party EXP (`PARTY_EXP`) is `Party.Exp`, not a Wallet balance, though it flows through `Wallet.Apply` for the reason code (never spent manually, 00 section 5.2). Level thresholds follow 00: `PARTY_LVL_EXP(l) = 100 * 1.08^l` EXP to go from level `l` to `l+1`, read from the `party_levels` table (09).

Every EXP source (kills, offline catch-up, the EXP Dungeon) goes through `Wallet.Apply(+exp PARTY_EXP, reason)`, which writes `Party.Exp` and then calls `CheckPartyLevel()` in the same `Apply` or tick (00 section 8 item 9); there is no second grant path.

```
CheckPartyLevel():                               # called by Wallet.Apply after any PARTY_EXP delta
  while Party.Exp >= PartyLvlExp(Party.Level):
    Party.Exp -= PartyLvlExp(Party.Level); Party.Level += 1; MarkDpsDirty()
    publish LevelUp(Party.Level)
```

Effect: the Party Level term in 00 section 7 is the compounding (adopted by 00 decision 9, replacing the linear `1 + HERO_LVL_ATK_PCT * heroLevel`), applied to every slot

```
partyMult = PARTY_LVL_MULT ^ partyLevel       # PARTY_LVL_MULT = 1.02 (S), evaluated from a GeometricTable
```

Reason (section 8.4 has the hero-model numbers): Whisker Hero has no gear-tier layer, and gear tiers were the only layer in the sibling project whose growth compounded with stage. Without one compounding stage-fed layer the frontier stalls for a week at a time from day 7 on, under any enemy curve. Party Level is the right place for it: it is fed by EXP, which grows with stage (`EXP_G`), it is automatic (no decision, no cost), and `PARTY_LVL_MULT` is an `S` constant this spec sets. With `EXP_G = 1.025` and threshold growth `1.08`, party level grows `~0.32 s + offline EXP`, so `partyMult ~ 1.02^(0.32 s) = e^(0.0063 s)`, which is 16% of the per-stage HP growth `ln 1.04 = 0.0392`. The rest of this paragraph is hero-model balance and stale until #309 and #249 rerun it: combined with Training's declining linear term and Awakening, the stage-fed layers returned roughly 40% of each stage's cost, and the time-fed layers (summons, Fish Board, Research) had to supply the other 60%. That split is what kept the 40% cap in reach (section 8.5). If `PARTY_LVL_MULT` is ever raised above 1.025 the Party Level share passed 40% by day 7 under that model.

### 5.3 Awakening (MVP, per slot)

Awakening is per party slot (00 decision 109): each slot has its own `Party.Slots[slot].AwakenRank`, passes the same stage gates and pays the same Rock Candy cost per rank, and its multiplier applies to that slot's damage only (section 7.1). The rank belongs to the slot, not the companion: swapping the slot's companion keeps it. An empty slot can be awakened; its rank waits idle until a companion is equipped.

| Rank | Stage gate (`AWAKEN_STAGES`) | Rock Candy cost per slot (`AWAKEN_COST_ROCK_CANDY`) | Slot multiplier after | Unlocks (highest rank of any slot) |
|---|---|---|---|---|
| 1 | 50 | 20 | x1.5 | `SCR_RESEARCH` tab |
| 2 | 150 | 60 | x2.25 | - |
| 3 | 300 | 160 | x3.375 | - |
| 4 | 500 | 400 | x5.06 | Rune slots 1-3 (P1) |
| 5 | 800 | 1000 | x7.59 | Rune slots 4-6 (P1) |

```
Awaken(slot):                                               # command, from the slot card in SCR_PARTY
  if slot < 0 or slot >= PARTY_SLOTS:                  return Reject(BadArgument, "BAD_SLOT")
  r = Party.Slots[slot].AwakenRank
  if r >= AWAKEN_STAGES.Length:                        return Reject(Capped, "AWAKEN_MAX_RANK")
  if Progress.HighestCleared < AWAKEN_STAGES[r]:       return Reject(Locked, "AWAKEN_STAGE_NOT_REACHED")
  if Wallet.RockCandy < AWAKEN_COST_ROCK_CANDY[r]:     return Reject(CannotAfford, "NOT_ENOUGH_ROCK_CANDY")
  Wallet.Apply(-AWAKEN_COST_ROCK_CANDY[r] ROCK_CANDY, AWAKENING)                   # reason per 00 contract 6 (issue #11)
  Party.Slots[slot].AwakenRank = r + 1; MarkDpsDirty()
  publish Awakened(slot, r + 1)
```

Awakening is a button, not automatic, because it costs a currency. The gate reads `Progress.HighestCleared`; farming a side stage in `SCR_STAGE_SELECT` never lowers it. Party slots are never gated by Awakening: all five are open from the start at every tier (00 decision 109, superseding the slot unlocks of decision 11). The `feature_unlocks` condition `AWAKEN_RANK >= n` reads the highest rank of any slot, so Research opens when the first slot reaches rank 1 and the P1 rune slots open at highest rank 4 and 5. `AWAKEN_MULT` (1.5), `AWAKEN_STAGES` and `AWAKEN_COST_ROCK_CANDY` keep their hero-model values per slot until the balance pass (#249): one slot's rank now multiplies roughly a fifth of party DPS, and five times as many ranks exist, each paid separately (section 12 question 10).

Cadence target (issue #43): every rank is affordable within `max_days_after_gate` days of the day the bot first reaches its stage gate, read from the `awakening_targets` content table (09; columns `rank`, `max_days_after_gate`, value 2 for every rank). Until #249 sets a per-slot target, the check reads the first slot to reach each rank (the highest slot rank); the other four slots' ranks are reported, not checked (section 12 question 10). That table is the one source both this spec's tool check (section 8.6, acceptance 21) and 06's acceptance read: the stage gate is the pacing lever, Rock Candy is a check. Rock Candy income is 06's (the Rock Candy Dungeon, 06 section 6.5), which the bot reads from content (section 8.3). Boss drops alone (`BOSS_ROCK_CANDY_P = 0.5`, one candy) give about 12 per day early and cannot carry it.

As built (issue #157, hero model): the rows are the `awakening` table (09), rank `r` is row `r`, and a tier without the rows (M0) has no ranks, so `Awaken` is rejected `Capped`/`AWAKEN_MAX_RANK` there. A rank above the table counts as the last rank (02 rule P3 clamps it on load). To build (#309): `Query` exposes per slot `AwakenRank(slot)`, `AwakenMult(slot)` (the product of the rows' `mult` up to the slot's rank, exactly 2.25 at rank 2), `NextAwakenStageGate(slot)` and `NextAwakenCost(slot)` (null and 0 at the last rank) and `CanAwaken(slot)`, and party-wide `HighestAwakenRank`, `RuneSlotsUnlocked` (0 at highest rank 0, then `rune_slots_after` of the highest rank) and `ResearchUnlocked` (`feature_unlocks` `RESEARCH`, which the `Research` command reads); `CompanionSlotsUnlocked` and the `companion_slots_after` column are removed. The pure forms are `AwakeningRules` for the bots. The bot's step-3 helpers (`TryAwaken`, `NextAwakenWithin`) exist but are not yet called by `greedy-f2p` (issue #214; the cadence is decision #249 items 3 and 5).

## 6. Research (MVP, Fish Coin sink)

Research is a flat list of nodes, each with independent levels, costs and caps. No tree edges in MVP: the reference game's tree is mostly presentation, and edges are one more table to validate. `SCR_RESEARCH` shows the nodes as a tree drawn from a `layoutRow`/`layoutCol` column in the `research` table (09); the sim ignores layout.

Cost of level `k -> k+1` of node `x` is `RES_COST0[x] * RES_COST_G[x]^k` Fish Coins, evaluated from a per-node `GeometricTable` and charged unrounded (Fish Coins are a `double`, like Training's gold; the UI floors the display). The last column is `RES_COST0 * RES_COST_G^(max - 1)` to three significant figures.

| Node id | Effect per level | Max level | `RES_COST0` | `RES_COST_G` | Cost of last level | Why it exists |
|---|---|---|---|---|---|---|
| `res_atk` | `researchPct += 0.05` | 50 | 150 | 1.20 | 1.14e6 | Direct DPS for every slot; makes Fish Coins matter to a party that is not MP-limited. |
| `res_gold_gain` | `goldMult *= 1 + 0.05 k` (one factor, not per level) | 20 | 250 | 1.40 | 1.49e5 | Second gold-gain source so gold income is not only a Training decision. |
| `res_mp_pool` | `MpPoolMax += 20` (every pool) | 10 | 200 | 1.50 | 7.69e3 | Burst at spawn: more abilities fire before regen matters (section 12 question 9). |
| `res_mp_regen` | `MpRegen += 0.5 /s` (every pool) | 10 | 300 | 1.50 | 1.15e4 | Companion uptime in every slot's pool (section 12 question 9). |
| `res_tank_cap` | `TankCap += 5` | 10 | 200 | 1.50 | 7.69e3 | Bigger buffer between sessions (05). |
| `res_tank_refill` | `TankRefillS -= 0.4` (floor 2.0 s) | 10 | 300 | 1.60 | 2.06e4 | More fish per active minute (05). |
| `res_fish_cap` | `FishMaxLvl += 1` | 10 | 2500 | 2.00 | 1.28e6 | Opens the next fish level and its discovery payout; the Fish Board's growth engine (05 section 8). |
| `res_offline_cap` | `OfflineCapH += 2` | 6 | 500 | 1.50 | 3.80e3 | Only matters to players who skip a day; cheap on purpose. |

The MP nodes raise all five pools equally (00 decision 109); there is no per-slot Research. Research effects are read by the sim through `Query` (`MpPoolMax`, `MpRegenPerTick`, `TankCap`, `TankRefillTicks`, `FishMaxLvl`, `OfflineCapH`, `GoldMult`, `ResearchPct`). Node ids are the `research` table's lowercase `res_*` ids (09; issue #11). `Research(nodeId)` buys one level: rejects, in this order, `InvalidTarget`/`UNKNOWN_RESEARCH` (no such node in the content tier), `Locked`/`RESEARCH_LOCKED` (before Research's `feature_unlocks` gate (09), seeded at Awakening rank 1 per 00 decision 11, or before the node's `unlock_awaken_rank`; issue #45), `Capped`/`RESEARCH_MAXED`, `CannotAfford`/`NOT_ENOUGH_FISH_COIN`; charges with reason `RESEARCH`; publishes `ResearchLeveled(nodeId, newLevel, cost)`; marks DPS dirty when the node affects it.

As built (issue #158): `Locked` is checked before `Capped` so every call before rank 1 is `Locked` (acceptance 13), even for a node an odd save holds at its max. `ResearchRules` holds the pure rules and effect formulas for the command, `Query` and the bots. `Query` exposes `ResearchLevelOf(nodeId)`, `NextResearchCost(nodeId)` (0 when unknown or maxed), `CanResearch(nodeId)`, `ResearchEffectTotal(effect)`, `MpPoolMax`, `MpRegenPerTick`, `OfflineCapH` (read by catch-up), `GoldMult` (Training's factor times `1 + 0.05 k`) and `ResearchPct`. The three Fish Board effects are exposed as `ResearchTankCap`, `ResearchTankRefillTicks` (floor 2.0 s) and `ResearchFishMaxLvl`, and the board's `TankCap`, `TankRefillTicks` and `FishMaxLvl` read them (issue #167; 05 section 3). The DPS cache is marked dirty after every accepted command (section 7.3), so a node that does not affect DPS (`res_fish_cap`, `res_tank_*`) still marks it; the recompute gives the same value. A tier without the rows (M0) has no nodes, so every `Research` call is `InvalidTarget` there and every effect is its Research-0 value. No quest goal reads Research. `greedy-f2p` does not buy Research yet (issue #214; the cadence is decision #249).

`res_offline_cap` extends the 24 h base cap from 00 decision 3; it does not change the base. Catch-up (06) and 08's `OFFLINE_WINDOW_OVERLAP` check use the effective cap `OFFLINE_CAP_H + 2 h * res_offline_cap level` (issue #21).

Fish Coin income that pays for this is roughly `0.2 * (FishMaxLvl - 1)` coins per level-1 fish fully merged (05 section 8), about 1,800 to 2,500 per 2 h active day at Research 0, plus the Fish Coin Dungeon (06 section 6.3 reward table, which the bot reads from content). Against that income the costs above meant, under the hero model's bot (stale until #309 and #249): all MP nodes to level 4 by day 3, `res_fish_cap` level 1 around day 3, level 2 around day 7, level 3 around day 14, level 4 around day 28, `res_atk` to about level 15 by day 30. The doubling cost of `res_fish_cap` is what turns linear coin income into a log-in-time Fish Board layer, which is the intended shape: fast at first, never finished.

## 7. The power formula

### 7.1 Formula

Same shape as 00 section 7 (00 decision 109): the party's damage is the sum of its occupied slots. Party-wide layers (Training, Party Level, the weapon passive, the passive board, Research, Runes, crit, speed) multiply every slot; a slot's equipped weapon, skill, companion `DAMAGE` ability and Awakening multiply that slot only; `BUFF_ATK` companions and missiles act on the whole party. This is the **rated** DPS, `Query.RatedDps`: the expected steady-state value used by `SCR_PARTY`, the wall check (section 2.8), the layer shares, the bots, the tools and 08's plausibility bound. It excludes transient buffs.

For each occupied slot `i` (companion `c_i`, weapon `w_i`, skill `k_i`, Awakening rank `a_i`; an empty slot contributes nothing and is left out of every sum):

```
SlotHit_i  = BASE_ATK                                                         # one companion's auto-attack (00 decision 109)
           * COMPANION_ATK_MULT[rarity(c_i)]                                  # the companion's rarity, not level (00 decision 111)
           * (1 + TRAIN_PER_LVL[ATK] * trainLvl[ATK])                         # Training        (gold, party-wide)
           * PARTY_LVL_MULT ^ partyLevel                                      # Party Level     (EXP, party-wide)
           * (1 + (equippedWeaponPct(w_i) + sum_owned weaponPassivePct) / 100) # Weapons        (summons; slot's own + party passive)
           * (1 + FISH_BOARD_DPS_PCT / 100)                                   # Fish Board, passive (05 fishMult_live)
           * (1 + researchPct)                                                # Research        (Fish Coins)
           * (1 + runePct)                                                    # Runes           (P1, 1.0 until then)
           * AWAKEN_MULT ^ a_i                                                # Awakening       (per slot)
                                                                              # = one non-crit, unbuffed slot hit
SlotUnit_i      = SlotHit_i * critFactor * speedFactor                        # one second of slot i's expected auto-attack
CompanionUnit_i = SlotUnit_i * (1 + skillDpsPct(k_i) / 100)                   # unit of c_i's DAMAGE per-cast numbers (decision 94, per slot)
SlotDps_i       = CompanionUnit_i * (1 + mpSufficiency_i * damagePct(c_i) / 100)   # Skills, then Companions (own DAMAGE ability)
PartyHit        = sum_i SlotHit_i                                             # unit of fish missiles and Tap Zap
AutoDps         = (sum_i SlotDps_i) * (1 + sum_i mpSufficiency_i * buffAtkPct(c_i) / 100)   # Companions (party BUFF_ATK)
MissileDps      = PartyHit * speedFactor * missileDpsPct / 100                # Fish Board, missiles (05; rated only)
RatedDps        = AutoDps + MissileDps
```

`damagePct(c)` is `c`'s level-scaled `dps_pct` when `c` is a `DAMAGE` companion and 0 otherwise; `buffAtkPct(c)` the same for `BUFF_ATK`; `BUFF_GOLD` and `MP_REGEN` count 0 damage (04 section 8.1). `skillDpsPct(k)` is 0 for an empty skill slot and `equippedWeaponPct(w)` is 0 for a companion with no weapon (it attacks unarmed). `missileDpsPct` is 05's closed form (05 section 5.1), missile hits per minute over the party's attack rounds per minute, so `MissileDps` is the missiles' expected damage in `PartyHit`s (a missile deals `PartyHit * f`, no crit). It is **rated-only** (issue #15): it never enters the tick loop, which deals missile damage as discrete hits. The sim keeps no trailing average; if `SCR_PARTY` wants a live number, the runtime/view-model keeps a 60 s rolling sum of `FishMissileHit`, outside the save and `StateHash`. Skills' `skillDpsPct` are rated averages over their cooldown (04 section 7), so the formula is the expected steady-state DPS.

MP sufficiency, one value per occupied slot `i`, for that slot's own pool (section 2.4; issue #39):

```
regenPerSec      = MpRegenPerTick * TICK_HZ                                   # the same for every pool
demand_i         = mp_cost(c_i) / cooldownSeconds(c_i)                        # every ability type, BUFF_GOLD and MP_REGEN included
restored(j)      = mp_restored(c_j) * LevelMult(c_j)                          # MP an MP_REGEN companion restores to EVERY pool per cast
uptime_j         = 1                                                          if restored(j) >= mp_cost(c_j)
                 = min(1, regenPerSec / ((mp_cost(c_j) - restored(j)) / cooldownSeconds(c_j)))   otherwise
restorePerSec    = sum over occupied j with c_j MP_REGEN of uptime_j * restored(j) / cooldownSeconds(c_j)
mpSufficiency_i  = min(1, (regenPerSec + restorePerSec) / demand_i)
```

Every pool receives every restorer's MP, its own included, and pays only its own companion's cost, so the same expression holds for an `MP_REGEN` companion's own pool. `uptime_j` counts a restorer whose restore is below its cost at what its own regen alone sustains, ignoring other restorers' help; that is the one conservative approximation here (a net-draining restorer is a Common or Rare at low level, 04 section 8.1). Overflow at `MpPoolMax` only wastes MP in a pool whose supply already meets its demand, so it does not change `mpSufficiency_i`. With no `MP_REGEN` companion equipped `restorePerSec` is 0.

The tick loop realises that value without counting any layer twice (issue #10). A slot hit carries only the layers that have no discrete event; every discrete source deals a multiple of a unit built from it:

```
slot attack  = SlotHit_i * (1 + slotAtkBuffPct_i / 100) * (1 + atkBuffPct / 100), then crit   # section 2.2; buffs apply here only
INSTANT/DOT  = SlotUnit_i * DamagePctPerCast / 100        # slot i's skill (04 per-cast numbers are % of SlotUnit_i)
DAMAGE       = CompanionUnit_i * DamagePctPerCast / 100   # slot i's companion (04 per-cast numbers are % of CompanionUnit_i)
missile      = PartyHit * f                               # 05; rated through missileDpsPct
tap          = PartyHit * TAP_ZAP_DMG_PCT                 # not in the rated DPS; the bot never taps
```

At speed 1 with no crit `SlotUnit_i == SlotHit_i`, which is the unit 04's per-cast derivation states. `CompanionUnit_i` carries its own slot's skill factor so that a slot's attack, skill and ability multiply as the rated formula does instead of adding. Rule (issue #243, per slot since 00 decision 109): companion damage scales with its own slot's skill. It may be revisited (an additive skill + companion term) once `tools/power_curve_model.py` reports layer shares.

Exact and approximate parts. For a party whose skills are `INSTANT` or `DOT`, whose companions are `DAMAGE`, `BUFF_GOLD` or `MP_REGEN`, with no missiles, against an enemy that does not die, the formula is exact in expectation: each slot attacks `speedFactor` times a second at `SlotHit_i * critFactor` on average, each skill delivers `skillDpsPct(k_i)` of `SlotUnit_i` per second because it fires exactly once per cooldown (one cast per slot per tick, no party-wide limit, so slots never delay each other), and each `DAMAGE` ability delivers `damagePct(c_i)` of `CompanionUnit_i` per second scaled by its pool's long-run cast rate `mpSufficiency_i`. Measured minus rated is then only crit sampling noise. The approximations are: (1) `BUFF` skills and `BUFF_ATK` companions, rated through their informational `dps_pct` (04) as a factor on the slot's or party's whole DPS but realised as a buff on auto-attacks only, with buff overlap (strongest wins) ignored; (2) a net-draining `MP_REGEN` restorer's `uptime_j` (above); (3) `missileDpsPct`, which is 05's closed form for the merge rate; (4) kills: the attack, skill and ability damage past a kill, and the later slots' hits dropped in the killing tick (section 2.1), are lost to overkill, which `KillTimeSeconds` approximates by whole attack rounds. Test (acceptance 20): measured DPS over a long fight equals `RatedDps` within 1%, on a fixture made of the exact parts only.

`Query.KillTimeSeconds(stage)` is the rated time to kill one normal enemy, used by offline catch-up (06 section 7.1) and the bots: whole attack rounds at the rated per-round party damage plus the spawn delay, `attackSec = ATTACK_INTERVAL_TICKS / TICK_HZ / speedFactor`, `killTime = max(1, ceil(Hp(stage) / (RatedDps * attackSec))) * attackSec + SPAWN_DELAY_TICKS / TICK_HZ` (06 section 7.2's "3-hit kill = 3.5 s" at speed 1). It stays on the rated party DPS: skills, companion abilities, crits and every slot enter through `RatedDps`, their expected share per round (issue #108).

### 7.2 Units

Percent fields (`equippedWeaponPct`, `weaponPassivePct`, `skillDpsPct`, `damagePct`, `buffAtkPct`, `FISH_BOARD_DPS_PCT`, `missileDpsPct`, `atkBuffPct`, `slotAtkBuffPct`, per-cast `DamagePctPerCast`) are in percentage points (`40` means +40%) because that is how 04 and 05 tables and the UI state them. `researchPct`, `runePct`, `TRAIN_PER_LVL[*]`, `critRate` and `mpSufficiency_i` are fractions. Damage units:

| Unit | Scope | Meaning | Used by |
|---|---|---|---|
| `SlotHit_i` | slot | one non-crit, unbuffed auto-attack of slot `i` | slot attacks (2.2) |
| `SlotUnit_i` | slot | one second of slot `i`'s expected auto-attack (`SlotHit_i * critFactor * speedFactor`) | slot `i`'s `INSTANT`/`DOT` skill (2.3) |
| `CompanionUnit_i` | slot | `SlotUnit_i * (1 + skillDpsPct(k_i) / 100)` | slot `i`'s `DAMAGE` ability (2.4) |
| `PartyHit` | party | `sum_i SlotHit_i` over occupied slots | fish missiles, Tap Zap (2.5) |

`Query` exposes `SlotHit(slot)`, `SlotUnit(slot)`, `CompanionUnit(slot)`, `MpSufficiency(slot)`, `PartyHit` and every factor separately, per slot where the factor is per slot, for `SCR_PARTY`'s breakdown panel and for the bot's layer-share report (section 8.5).

### 7.3 Caching

`RatedDps`, `PartyHit`, the per-slot `SlotHit`, `SlotUnit`, `CompanionUnit` and `MpSufficiency` arrays, and the party and per-slot factors are cached in `Simulation` and recomputed lazily after `MarkDpsDirty()`, which is called by: any Training purchase, `LevelUp`, `Awakened`, `ResearchLeveled`, any weapon/skill/companion equip or unequip in any slot, any copy gain or limit break (04), any Fish Board change (05: DPS recomputed at most once per command). Buffs are applied at hit time and do not dirty the cache. `Tick()` reads the cache only; a stat bought mid-fight applies to the next hit.

### 7.4 Layer factors (proposal, section 12 question 11)

A sum over slots is not a product of layer factors, so section 8.5's `share = ln(factor) / ln(DPS / BASE_ATK)` needs a definition for the per-slot layers. Proposed, exact by construction (the logs add up to `ln RatedDps`):

```
w_i               = SlotDps_i / sum_j SlotDps_j                                # slot i's weight in the party
factor[L]         = exp(sum_i w_i * ln f_L,i)                                  # per-slot layer L (Weapons, Skills, Companions' own DAMAGE, Awakening)
factor[L]         = f_L                                                        # party-wide layer (Training, PartyLevel, Research, Runes, Crit, Speed, passive Fish)
factor[Companions] *= 1 + sum_i mpSufficiency_i * buffAtkPct(c_i) / 100        # the party BUFF_ATK term joins the companion layer
factor[Fish]      *= RatedDps / AutoDps                                        # missiles join the Fish layer
Base              = BASE_ATK * exp(H(w)),  H(w) = -sum_i w_i ln w_i            # party size: 5 x BASE_ATK when the slots are equal
```

`Base` stays outside the floor and ceiling as now. The weighted mean credits a layer by where the party's damage is, so a strong main slot dominates; the bot policy (section 8.3) is what concentrates it there.

## 8. Layer-share budget, targets and the bot

**Stale under the party model (00 decision 109).** Every number in sections 8.1, 8.2 and 8.4 (stage per day, DPS, layer shares, multipliers, wall lengths, cadences) was produced by the single-hero scratch model. They are kept as historical context only, are not targets for the party model, and are replaced when the bots and `tools/power_curve_model.py` are rerun on the party model (#309) and the balance pass (#249) retunes; no party-model number is given here until then. The reasoning (why the curve moved, why one compounding layer) still holds, because the party-wide layers multiply every slot as they multiplied the hero. Section 8.3's party policy is a proposal for #309.

### 8.1 The problem with the inherited curve

00 inherits `HP_G = 1.07` and `GOLD_G = 1.05` from the sibling project along with the day-stage targets 90/240/380/480/550. In the sibling, a gear-tier layer whose multiplier grew `1.045^stage` carried 73-77% of log-power from day 7 on; the other layers covered a residual of `ln(1.07/1.045) = 0.024` nats per stage. Whisker Hero has no gear tiers. Its layers are:

| Layer | Fed by | Growth shape over 30 days |
|---|---|---|
| Training (`ATK`, crit, speed, gold, EXP) | gold, which grows `GOLD_G^s` | level `~0.43 s`, multiplier linear in level, so **logarithmic in stage** |
| Party Level | EXP, `EXP_G^s` | compounding (section 5.2): **exponential in stage**, `e^(0.0063 s)` |
| Awakening | stage gates + Rock Candy, per slot | five steps of x1.5 on each slot |
| Weapons, Skills, Companions (per slot) | pulls, a fixed number per day | saturating: each new rarity tier is a step, dupes are a slow linear climb |
| Fish Board | active minutes, Fish Coins | x1.5 per `res_fish_cap` level, costs doubling: **logarithmic in time** |
| Research | Fish Coins | linear in level, costs geometric: logarithmic in time |

Under `HP_G = 1.07` every stage costs `0.0677` nats and the time-fed layers together add about 0.05 to 0.1 nats per day after day 10. The scratch model (section 8.6) with the 00 constants gives stage 110 on day 1, 210 on day 7, 250 on day 30, with a wall from day 14 to day 30 of 960 active minutes (eight days at two hours). That is not a tuning miss; it is the shape of a stage curve built for a layer this game does not have.

Two things must change, and this spec recommends both:

1. **Gentler enemy curve.** `HP_G 1.07 -> 1.04`, `GOLD_G 1.05 -> 1.03`, `EXP_G 1.04 -> 1.025`. The gold gap to HP stays at 0.01 per stage so Training stays a minority layer (the sibling's gap was 0.02 and gave Training 10%; here the gap is smaller because gold has six stats to feed and no enhancement competitor). `HP0`, `GOLD0`, `EXP0` stay; `BASE_ATK` is now 2 per companion, so the five-slot starter party matches the hero's 10 at day 0 (00 decision 109). Stage numbers get larger across the board; chapter and awakening gates still land on sensible days (section 5.3).
2. **One compounding stage-fed layer**: Party Level (formerly Hero Level) at `PARTY_LVL_MULT = 1.02` (section 5.2).

These constants are re-tagged `S` in section 14 and the tool in section 8.6 is their validator. Everything else inherited from the sibling stays as 00 states it.

### 8.2 Worked numbers (hero model, stale; scratch model, seed 7, recommended constants)

Historical hero-model output (section 8 note; the `Hero` column is today's Party Level layer, `BASE_ATK` was 10). Day-stage output and the share of `ln(DPS / BASE_ATK)` carried by each factor. The model is the Python sketch section 8.6 specifies, run with the recommended constants and hand-entered ticket and Rock Candy income figures that predate 06's tables; the tool replaces them with 06's content income (section 8.3, issue #38). Treat the stage numbers as +/-15% until `tools/power_curve_model.py` in this repo replaces them.

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

1. **Gold**: every active minute, buy Training levels while any stat is affordable, always the stat with the highest marginal `ln(DPS)` per gold. `GOLD_GAIN` and `EXP_GAIN` are valued at half the `ATK` levels (or party levels) their income increase will eventually buy.
2. **Diamonds and tickets**: at the start of each day, spend every ticket on its banner, then spend diamonds on the banner with the highest expected `ln(DPS)` gain per diamond, where the expectation is over the banner's current published rate table and the gain of each pool item is evaluated against the current collection. Use a 10-pull when the chosen banner's 10x price is affordable, else singles. Re-evaluate after every pull. A pull's gain is evaluated with the item placed by step 8, so a weapon or skill is worth what it adds in its best slot.
3. **Rock Candy** (proposal, #309): awaken, among the slots whose stage gate and cost are both met, the one with the largest `SlotDps_i` (a rank multiplies `SlotDps_i` by `AWAKEN_MULT`; ties: lowest slot index). Empty slots are never awakened. Rock Candy is reserved only for the strongest slot's next rank (00 decision 111): the occupied slot with the largest `SlotDps_i` that still has a next rank, gate and cost aside, which is the slot this rule awakens next. While that slot's next gate is at most 50 stages away the bot saves; otherwise it spends on limit breaks (04 section 5) on whichever item gives the most `ln(DPS)` per candy. The other slots' ranks reserve nothing. The C# bot (`GreedyF2PBot.StrongestAwakenSlot`, `NextAwakenWithin`) and `tools/power_curve_model.py` use this same rule.
4. **Fish Coins**: every active minute, if any equipped companion's pool is MP-starved (`mpSufficiency_i < 1`) buy `res_mp_regen`; else buy the node with the best `ln(DPS)` per coin among `res_atk`, `res_gold_gain` (payback proxy as for gold), `res_tank_*` (valued at 0.08 nats per level, the measured board growth per 10% more fish) and `res_fish_cap` (valued at `0.5 * ln 1.5 * fraction of board already at the cap`).
5. **Fish Board**: the attentive policy from 05 section 8 every active minute: release until the board is full or the tank is empty, merge every equal pair lowest level first, repeat; if the board is full with no merge and the tank is not empty, discard the lowest-level fish.
6. **Boss**: the first attempt is automatic; when walled, retry at the start of every active minute (the real player presses the button; `AttemptBoss` is free).
7. **Offline**: two offline gaps a day totalling 22 h (10 section 3.3), each caught up at `OFFLINE_RATE` for gold and EXP at the current stage, capped by the effective `OfflineCapH` (`OFFLINE_CAP_H` plus `res_offline_cap`); the tank fills by 05's `CatchUpTank` over the credited ticks, which reaches capacity; nothing else moves (06, issue #21).
8. **Equip** (proposal, #309): after every pull, limit break or awakening, run `AutoEquipBestCompanions`, `AutoEquipBestWeapons` and `AutoEquipBestSkills` (04) over the five slots, in that order. Those commands rank slots by 04's per-layer coefficients, the slot's factor on everything but the layer being placed (weapons: `Awakening * skillFactor * ownFactor`; skills: `Awakening * weaponFactor * ownFactor`; companions: `Awakening * weaponFactor * skillFactor`; 04 sections 6.3, 7.2 and 8.2), and give the best item to the heaviest slot, the next to the next, ties by slot index, no item in two slots; the bot uses the same commands, so bots and tools agree. Because every per-slot layer multiplies the others in the same slot, this concentrates the best weapon, skill and `DAMAGE` companion on the most-awakened slot. 04 owns the exact ranking and tie-breaks.

Inputs the bot takes from other specs: diamond income by 06's rules (`TUTORIAL_DIAMONDS = 1500`, `LOGIN_DIAMONDS`, the daily and weekly quest rows its day completes, `FIRST_CLEAR_DIAMONDS` per new boss stage and the achievement rungs it crosses; 06 sections 2 and 5, issue #178), with `--diamonds flat` granting `DIAMONDS_PER_DAY_F2P = 400` a day instead for parity runs; summon ticket income per banner from 06's ticket sources and Rock Candy and Fish Coin income from 06 section 6.3's dungeon reward tables, all read from content, never hand-entered (issues #38, #43). Per-banner pull cadence (pulls per day per banner) is an output of this policy applied to that income, not an input; 04 retunes `BANNER_LVL_EXP` from it so the weapon banner reaches level 4 between day 20 and day 35 (issue #38; tuned in 04 section 4, where the hero-model 20-seed median was day 23.5 under 06's diamond rules, stale until #309). If 06's content changes the bot reruns and this section is regenerated.

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
    // 3. rock candy (proposal): the strongest slot that can awaken
    if (BestAwakenSlot(sim) is int slot && sim.Awaken(slot).Ok) Reequip(sim);   // step 8
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

Days 14 and 30 coincide by construction: `HP_G = 1.04` was chosen so that the layers this game actually has reach the inherited day-30 stage, and `HERO_LVL_MULT = 1.02` (now `PARTY_LVL_MULT`) so that the day-14 to day-30 pace is not a dead stop. Days 1-7 are higher because a gentler curve makes the kill-rate-limited opening longer: with one-hit kills a stage takes 15 s, so 2 h can hold 480 stages, and the gentler curve keeps the hero near one-hit kills for longer. If the design wants day 1 back near 90, the lever is `HP0` (x6 moves day 1 down by about 45 stages) and it moves every later day by the same amount; the tool reports it, the decision is 00's.

The two alternatives considered and rejected: keeping `HP_G = 1.07` and accepting 110/210/250 with an eight-day wall (fails pillar 1 in practice); or making Training compound instead of Hero Level, now Party Level (Training is `I` in 00 and would exceed the 40% cap at any compounding rate that fixes the stall, because gold also feeds five other stats).

### 8.5 Layer-share rule as a test

The harness (10) computes `share[layer] = ln(factor[layer]) / ln(DPS / BASE_ATK)` from `Query.RatedDps`'s factors, with the per-slot layers folded into party factors as section 7.4 proposes (section 12 question 11), and asserts `share <= 0.40` for every layer on days 1, 7 and 30, and `share >= 0.05` on days 7 and 30 only (issue #12): day 1 is where a runaway layer shows first, but a day-1 floor mostly measures unlock timing. `Base` (`BASE_ATK`) is not a layer for the floor; crit is. Speed is permanently exempt from the floor in every tier, because `ATK_SPD` is capped at x2 and its share can only fall (issue #36); the ceiling still applies to it. Runes are not checked before P1. Per-tier floor exemptions live in `src/HeroCat.Bots/Acceptance/acceptance-targets.json` and report `WARN` instead of failing; the ceiling always gates. M0 exempts Research and Companions; the reason given under the hero model (the M0 harness runs without Companions) no longer holds, since the party is companions at every tier, so the exemption list is reviewed in #309 and #249. The tier exemptions are removed when this section's retune lands.

### 8.6 `tools/power_curve_model.py` for this repo

A per-minute aggregate model, not the real sim, so it runs 30 days in under ten seconds and can sweep constants. It is the copy of `idle-rng-game/tools/power_curve_model.py` restructured as follows.

Inputs: `content/*.json` via `tools/content.py` (09 section 11), so a constant exists in exactly one place; CLI flags `--content DIR`, `--seed N` (default 7), `--days 30`, `--active-min 120`, `--sessions 1|2` (default 2), `--override NAME=VALUE` (repeatable, for sweeps), `--tickets W,S,C`, `--rock-candy-per-day`, `--fish-coin-per-day` (flat income instead of 06's dungeon tables), `--policy greedy|lazy|spender20|whale` (10 defines the last three; they need the shop tables), `--diamonds content|flat` (06's diamond sources, default, or a flat `DIAMONDS_PER_DAY_F2P`), `--m0` (drops Research, dungeons, tickets, limit breaks and every content row tagged above M0, which removes Awakening; the five-slot party and its companions stay, they are M0), `--check [--seeds 1-20]` (the 20-seed sweep below; exits 1 on any `FAIL`). Until the MVP tables ship (`research`, `companions`, `dungeons`, quests), the tool uses their rows as this spec, 04 and 06 state them and prints a `fallback` note for each.

Model, per active minute: compute `dps()` from the section 7 formula (sum over the five slots, per-slot `mpSufficiency_i`) using the closed-form `missileDpsPct` from 05; run kills at `killTime = ceil(hp / roundDamage) / speedFactor + SPAWN_S` (`roundDamage` the rated damage of one attack round) with the boss check from section 2.8; apply the section 8.3 policy; once per day apply income grants, pulls (section 8.3 item 2, with the exact 04 algorithm including banner levels and the 10-pull guarantee), offline catch-up and tank fill. The Fish Board is simulated as a count per level with the attentive policy from 05 section 8 (not cell by cell; cell placement does not change throughput). Enemy and cost curves use `**` in Python; bit-exactness is the C# harness's job, not this tool's.

Outputs, printed and written as `tools/out/power_curve_<seed>.json`:

1. The day table for days 1, 2, 3, 5, 7, 10, 14, 21, 30: stage, DPS, each layer's multiplier, Training levels per stat, party level, awaken rank per slot, fish cap, research levels, pulls and L+ per banner, limit breaks.
2. The layer-share table (section 8.2 format) and a PASS/FAIL/WARN line per layer against the section 8.5 rule.
3. Wall report: first wall stage, longest wall in active minutes and the day it started, total walled minutes.
4. Economy report: cumulative gold earned and spent, diamonds spent per banner, Fish Coins earned (discovery, merges, dungeon) and spent per node, Rock Candy earned and spent.
5. A compact line `DAYTABLE 1:240 3:330 7:430 14:480 30:550` for 00 section 6.6 to paste (the example values are the stale hero-model table).
6. Per-banner pull cadence: pulls per day on each banner (tickets and diamonds) and the day each banner level is reached, the input 04's `BANNER_LVL_EXP` retune and `gacha_model.py` read (issue #38).
7. 06's economy tables (issue #178): diamond income by source for days 1, 3, 7, 14 and 30 (06 section 2) and the sinks-by-day table for days 1, 7 and 30 (06 section 11), as 20-seed medians under `--check`, with a PASS/FAIL line per 06 economy check (diamond budget, first clears, 10-pull affordability and pity per banner, diamond and gold spending, Fish Coin and EXP Dungeon shares, best floor on day 30).

`S` constants it validates and what it checks: `HP_G`, `GOLD_G`, `EXP_G`, `PARTY_LVL_MULT` (day table within tolerance, 40%/5% rule, longest wall under 14 days <= 240 active minutes, longest wall opened on days 15-30 within `lateWallMaxActiveMinutes`, acceptance 18); `TRAIN_PER_LVL[CRIT_RATE/CRIT_DMG/ATK_SPD/GOLD_GAIN/EXP_GAIN]` and `TRAIN_CAP[*]` (each stat receives between 5% and 50% of Training gold over 30 days; none is bought to zero); `BOSS_TIME_TICKS` (first wall between stage 60 and 200); `TAP_ZAP_*` (a tapping variant clears a wall at most 2.5x faster than the idle bot); `EXP0`/`PARTY_LVL_EXP` (party level between 0.25 s and 0.45 s at every reported day); `AWAKEN_STAGES`, `AWAKEN_COST_ROCK_CANDY` (every rank affordable on the first slot within `awakening_targets.max_days_after_gate` days of reaching its gate, the other slots reported; issue #43, section 12 question 10); `RES_*` costs (every node has at least one level bought by day 7 and `res_fish_cap` reaches 2 by day 10); `BOSS_ROCK_CANDY_P` (boss drops are under 20% of Rock Candy income). The Fish Board constants are `fish_model.py`'s (05); the summon constants are `gacha_model.py`'s (04); this tool consumes both tools' outputs as inputs where it needs them.

Acceptance in CI (10): the median of 20 seeds within +/-20% of 00 section 6.6 on days 1, 7 and 30; the share rule; the wall rule.

## 9. Tuning levers

| Symptom | Lever | Side effect |
|---|---|---|
| Day 1 too fast | `HP0` up (x6 is about -45 stages on every day) | None on shares. |
| Late walls too long | `PARTY_LVL_MULT` 1.02 -> 1.025 | Party Level share rises toward 40%; check it. |
| Party Level over 40% | `PARTY_LVL_MULT` down, or raise `PARTY_LVL_EXP` growth 1.08 -> 1.09 | Fewer stages/day late. |
| Research under 5% | `res_atk` to 0.08 per level, or `RES_COST_G[res_atk]` 1.20 -> 1.15 | Fish Coins drift away from `res_fish_cap`; rerun `fish_model.py`. |
| Companions under 5% | companion `dps_pct` (04); `MP_REGEN0` or `mpCost` (MP binds per pool, 00 decision 110) | Reduces the pull of `res_mp_regen`. |
| Training over 25% | `GOLD_G` 1.03 -> 1.025 | Fewer Training levels; `CRIT_RATE` cap reached later. |
| Gold feels useless late | `GOLD_G` up toward 1.035 | Training share rises. |
| Walls too frequent | `BOSS_TIME_TICKS` 300 -> 400, or `BOSS_HP_MULT` 10 -> 8 | Flatter feel; prefer this to touching `HP_G`. |
| Everything scales but feels samey | add Awakening ranks at 50-stage spacing with x1.25 | Cheap, high perceived value; shifts the Awakening share up. |

Never change `HP_G` to fix a local problem; it moves every stage at once and every other spec's worked numbers with it. Change the layer that misbehaves.

## 10. RNG streams used by combat

| Stream | Draws | When | Counter invariant | Consumer rule |
|---|---|---|---|---|
| `combat` | 0 in MVP | reserved: P1 enemy variance, skill proc chances | `== 0` | Only `HeroCat.Sim.Combat` may draw. |
| `crit` | 1 per stage slot hit that lands | section 2.2, in slot order, including hits at `critRate == 0`; none for a slot whose attack is dropped after a kill | `== Stats.PartyHits` | Only `SlotAttack()`. Fish missiles, skills, companion abilities and Tap Zap never draw. |
| `loot` | 1 per boss kill | `BOSS_ROCK_CANDY_P` roll | `== Stats.BossKills` | Only `Kill()` on a boss. P1 drop tables add draws and a new invariant. |
| `fish_spawn` | per 05 | release cell choice | 05 owns | Fish Board only. |
| `dungeon` | 1 per M0 run (06) | dungeon run seed, drawn by the M0 local `IDungeonSeedService` (00 contract 11, issue #25) | 06 owns | Dungeon seed service only. |
| `dungeon_run` | per dungeon fight (06) | temporary, reseeded from the run seed at `StartDungeon`; every slot hit's crit draw while `Combat.Mode == Dungeon` (issue #42) | none; discarded when the run ends | Dungeon fights only; `crit` and `Stats.PartyHits` are untouched by dungeon hits. |

The summon streams are not combat's: they stay in the save in every mode and are drawn only by `LocalSummonService` (00 decision 14). Draws are consumed even when the outcome is predetermined so the invariants above hold and the server can audit a save (08). These invariants are the validator's and 08's (02 section 3.4, issue #8). Stream seeds are derived in Runtime, not the sim: the first 8 bytes of SHA-256(`RngRoot` ++ UTF-8 stream name), where `RngRoot` is the immutable Guid set at profile creation (00 contract 2, issue #9); 02 persists `(seed, counter)` per name.

## 11. Analytics events

This section owns these events' fields (issue #245); every event also carries 02 section 14.1's envelope, and 02 section 14.4 indexes them. The sim publishes the event named in the last column and the Runtime sink maps it. The envelope's `stage` is the current frontier, so an event about another stage names it `clearedStage`, `bossStage` or `wallStage` (an owner field may not reuse an envelope name, 02 section 14.1; issue #274).

| Event | Fields | When |
|---|---|---|
| `stage_cleared` | `clearedStage`, `isBoss`, `firstClear`, `diamondsGranted`, `attempts` (boss stages only), `ticksOnStage`, `dps` | `StageCleared`; feeds the 00 section 6.6 day table |
| `boss_failed` | `bossStage`, `bossHpLeftPct`, `attempt` | `BossFailed` (2.7); the first one on a stage is the wall hit (2.8) |
| `wall_broken` | `wallStage`, `wallSec`, `activeSecDuringWall`, `attempts`, `layersChangedDuringWall[]` | `StageCleared` of a boss stage that was `Walled` |
| `training` | `stat`, `levelFrom`, `levelTo`, `goldCost` | `StatTrained`; one per command, not per level (01) |
| `level_up` | `partyLevel` | `LevelUp` (5.2); renamed from `heroLevel` (00 decision 109) |
| `awakened` | `slot`, `rank` (the slot's new rank), `rockCandyCost`, `rockCandyAfter` (the stage is the envelope's) | `Awakened(slot, rank)` (5.3) |
| `research_bought` | `researchId`, `level`, `fishCoinCost` | `ResearchLeveled` (6) |
| `currency_changed` | 06 section 13, reasons `KILL`, `BOSS`, `FIRST_CLEAR`, `TRAINING`, `RESEARCH`, `AWAKENING` | per wallet delta |

As built (`HeroCat.Runtime.Session.FeatureAnalytics`, issue #274): the sim's events carry no stage-entry tick, wall start or per-stage fight count, so the Runtime tracks them from the events and seeds them from the state when it attaches; nothing is saved and the sim is unchanged.

- `attempts` counts the boss fights on the stage including the clearing one (`BossFailsOnStage + 1`); `attempt` is `BossFailed.Attempt`. `bossHpLeftPct` is the remaining fraction in percentage points (0-100).
- `firstClear`: `clearedStage` is above the highest stage cleared so far; `diamondsGranted` is the `StageFirstCleared` payment for that stage, else 0.
- `ticksOnStage` counts sim ticks from the previous frontier advance (tick 0 for a new profile); `dps` is `Query.RatedDps` when the row is sent.
- A wall opens on the first `BossFailed` of a stage. `wallSec` is virtual server seconds (offline gaps included), `activeSecDuringWall` ticked seconds, and `layersChangedDuringWall` the `PowerLayer` names other than `Base` whose party factor (section 7.4) differs between the wall's opening and its break; the hero-model layer `Hero` is renamed `PartyLevel` (00 decision 109).
- A stage or wall entered before this process attached (a cold boot mid-stage or mid-wall) has no known start: the row omits `ticksOnStage`, or `wallSec`, `activeSecDuringWall` and `layersChangedDuringWall`, rather than send a partial value.

## 12. Open questions

1. **Adopt the recommended curve?** Resolved: adopted (00 decision 9); 03, 05, 06 and 10 write their worked numbers against it, and 00 section 6.6 is replaced by the tool's `DAYTABLE` line once `tools/power_curve_model.py` exists.
2. **Day-20 wall.** Resolved (issue #36): ship M0 as is; for MVP, run `tools/power_curve_model.py` first and pick the levers from section 9 that clear both the day-20 wall and the 5% floor misses (candidates: `res_atk` at 0.08 per level, limit breaks priced so the bot buys one every day from day 10, 04 section 5). The lever choice and values are pending the tool. Acceptance 18 bounds walls on days 14-30; speed is permanently exempt from the floor.
3. **Day 1 at 240 stages.** Fine for the genre; wrong if 07's tutorial or 06's first-clear diamond budget assumed ~90. Owner: 00. Default: keep; if 06 needs fewer first clears on day 1, raise `HP0` rather than `HP_G`.
4. **`HP`/`DEF` trainable only in P1.** Resolved: as section 4; the enum keeps all eight (00 decision 10).
5. **`res_offline_cap` versus decision 3.** Resolved: the node extends the cap; catch-up uses the effective cap `OFFLINE_CAP_H + 2 h * level` (issue #21).
6. **Companion slot unlocks tied to Awakening.** Superseded (00 decision 109): all five party slots are open from the start at every tier; Awakening is per slot and only Research and the P1 rune slots read its highest rank (section 5.3).
7. **Auto boss retry** (`BOSS_AUTO_RETRY_S = 60`) is P1 here; 07 may want it in MVP for the idle-on-desk player. Default: P1.
8. **Tap Zap and the server plausibility check.** Resolved: a tapping player's stage-vs-time bound is up to 4x the idle bound during bosses (section 2.5), so 08 uses `DPS * (1 + TAP_ZAP_DMG_PCT * TAP_ZAP_MAX_PER_SEC)` as the upper bound (00 decision 26). Still an upper bound under the party model: a tap is a fraction of `PartyHit`, and `PartyHit` is at most one second of `RatedDps`.
9. **MP does not bind with per-slot pools.** Resolved by 00 decision 110: `MP_REGEN0 = 0.5` per pool (option (a)); the balance pass may still tune the value. Original question: Every content companion costs 10 MP per 10 s (1 MP/s) and each pool regenerates `MP_REGEN0 = 2` MP/s, so `mpSufficiency` is 1 in every slot at Research 0: `res_mp_regen`, `res_mp_pool` and `MP_REGEN` companions add no DPS, and section 6's MP nodes lose their job (section 2.4). Options: (a) lower `MP_REGEN0` per pool (0.5 MP/s would make a default companion run at half uptime, as the shared pool did with four companions); (b) raise `mp_cost` or vary it by rarity (04); (c) accept that MP is cosmetic and repurpose the MP nodes. Recommendation: (a), set by the balance pass. Owner: the user, via #249. Until then the values are kept per pool and nothing else depends on the outcome.
10. **Per-slot Awakening budget.** Resolved by 00 decision 111: `AWAKEN_COST_ROCK_CANDY` = 20, 60, 160, 400, 1000 per slot (a fifth), `AWAKEN_MULT` stays 1.5, the cadence check reads the first slot to reach each rank. Original question: Awakening is now five times the ranks at the hero-model cost each (25 ranks, 5 x 8,200 = 41,000 Rock Candy to max), and each rank is worth about a fifth of what it was. Undecided: whether `AWAKEN_MULT`, `AWAKEN_COST_ROCK_CANDY` or 06's Rock Candy income change, and whether the `awakening_targets` cadence applies to every slot or only the first. Default until #249: values unchanged, the cadence check reads the first slot to reach each rank (section 5.3).
11. **Layer shares with a sum over slots.** Section 8.5's share rule assumed a product of layer factors. Section 7.4 proposes a DPS-weighted geometric mean of the per-slot factors with the slot-spread entropy in `Base`, which keeps the shares exact; the alternative is a leave-one-out ratio per layer, which does not add up. Default: section 7.4, implemented in #309; the 40%/5% thresholds themselves are #249's.
12. **Companion auto-attack by rarity.** Resolved by 00 decision 111: hybrid, `COMPANION_ATK_MULT[r]` (C 1.0 / R 1.1 / E 1.25 / L 1.5 / M 2.0) on the auto-attack and `COMPANION_DPS_PCT` raised 25% on the ability. Original question: Every companion's auto-attack is `BASE_ATK` (00 decision 109); rarity and dupes drive only its ability. Open for the balance pass (#249): whether auto-attack should scale with companion rarity or level.

## 13. Acceptance criteria

1. From a fresh profile (the starter party in slots 0-4), the first `EnemySpawned` is at tick `SPAWN_DELAY_TICKS` and the first `HitLanded` events at `SPAWN_DELAY_TICKS + ATTACK_INTERVAL_TICKS`, one per occupied slot in slot order 0-4 on that tick; with `ATK_SPD` level 100 every slot's hit interval is exactly 5 ticks for 1,000 consecutive hits.
2. Ten normal kills on stage 1 advance to stage 2 with one `StageCleared(1, 2, false)`; on stage 10 the 10th enemy spawns as a boss with `Hp == BOSS_HP_MULT * Hp(10)` and `BossTicksLeft == BOSS_TIME_TICKS`.
3. With no skills and only `BUFF_GOLD` companions, a boss whose HP is exactly `30 * speedFactor` non-crit attack rounds (`30 * speedFactor * PartyHit`) is cleared; one with one more `SlotHit(0)` of HP fails at tick `BOSS_TIME_TICKS` after spawn and publishes `BossFailed(10, 1, remainingFraction)`; a hit landing on the final tick counts.
4. While `Walled`, mobs of the same stage spawn, pay `Gold(s) * GoldMult` and `Exp(s) * ExpMult`, and `KillsInStage` stays at `KILLS_PER_STAGE - 1`; `AttemptBoss` despawns the mob with no gold or EXP and the next spawn is the boss; `AttemptBoss` is rejected with `NotAvailable`/`NO_BOSS_TO_ATTEMPT` on a normal stage and `NotAvailable`/`BOSS_ALREADY_PENDING` while pending.
5. `crit.Counter == Stats.PartyHits` and `loot.Counter == Stats.BossKills` after a 1,000,000-tick fuzz run with five occupied slots; fish missiles, skills, companion abilities and Tap Zap consume no `crit` draws.
6. Over 100,000 slot hits with `CRIT_RATE` level 100 and `CRIT_DMG` level 50 (seed 7), the crit frequency is within 3 standard deviations of 0.25 and, for every occupied slot `i`, the mean damage of slot `i`'s hits is within 1% of `SlotHit(i) * (1 + 0.25 * 1.0)`.
7. `TapZap(20)` in one second applies exactly `TAP_ZAP_MAX_PER_SEC` taps and reports 14 ignored; the next second accepts 6 again; each applied tap removes `PartyHit * TAP_ZAP_DMG_PCT`.
8. Queued fish missiles apply on the first `Fighting` tick after queueing, in order, without crit, each removing `PartyHit * f`; a missile queued during `Spawning` is not lost, including across a save round trip; surplus damage beyond the kill is discarded.
9. `TrainStat(ATK, 10)` with insufficient gold changes nothing; with sufficient gold charges exactly the closed-form bulk cost and raises `atkMult` by `0.5`; `TrainStat(CRIT_RATE, 1)` at level 300 is rejected with `Capped`/`STAT_AT_CAP`; `TrainStat(HP, 1)` is rejected with `Locked`/`STAT_NOT_TRAINABLE` in MVP; a successful purchase publishes one `StatTrained`.
10. `critFactor`, `speedFactor`, `partyMult`, `AwakenMult(slot)` for every slot, `researchPct`, `SlotHit(slot)` and `PartyHit` from `Query` equal hand-computed values for a fixture with `CRIT_RATE` 120, `CRIT_DMG` 80, `ATK_SPD` 100, party level 50, slot Awakening ranks 2, 0, 1, 0, 0, a different weapon in slots 0-2, slot 3 unarmed, slot 4 empty, and `res_atk` 7, within relative 1e-12.
11. A `Wallet.Apply(PARTY_EXP)` crossing two thresholds in one grant raises `Party.Level` by 2 and publishes two `LevelUp` events; `partyMult` equals `PARTY_LVL_MULT^level` from the `GeometricTable` (bit-identical on x64, ARM64, IL2CPP per 10's golden-file test).
12. `Awaken(0)` is rejected with `Locked`/`AWAKEN_STAGE_NOT_REACHED` at `Progress.HighestCleared == 49`, with `CannotAfford`/`NOT_ENOUGH_ROCK_CANDY` at stage 50 with 99 candy, succeeds at 100 candy charging exactly 100 with reason `AWAKENING`, publishes `Awakened(0, 1)`, opens Research and multiplies `SlotHit(0)` by 1.5 while slots 1-4 keep rank 0 and their `SlotHit`; `Awaken(1)` then charges another 100; `Awaken(-1)` and `Awaken(PARTY_SLOTS)` are rejected with `BadArgument`/`BAD_SLOT` before any other check; swapping slot 0's companion keeps its rank 1.
13. `Research(res_fish_cap)` at level 0 charges 2,500 Fish Coins, at level 9 charges 1,280,000, at level 10 is rejected with `Capped`/`RESEARCH_MAXED`; before any slot reaches Awakening rank 1 every `Research` call is rejected with `Locked`/`RESEARCH_LOCKED`.
14. MP per pool (issue #39): with fixture `DAMAGE` companions costing 30 MP on a 100-tick cooldown in slots 0-3, slot 4 empty and `MP_REGEN0 = 2`, each of slots 0-3 fires 4 +/- 0.3 abilities per minute (not 6) in steady state (from minute 2: the first minute also spends each starting 100-MP pool) and `MpSufficiency(i) == 2/3`; after `res_mp_regen` level 4 each fires 6 per minute. Putting an `MP_REGEN` companion that restores 6 MP per cast (cost 10, cooldown 100) in slot 4 raises each of slots 0-3 to 5.2 +/- 0.3 per minute with `MpSufficiency(i) == 2.6 / 3`, the restorer fires 6 per minute, and each restore reaches all five pools, the restorer's own included. Each `CompanionAbilityUsed` carries the caster's own pool.
15. Deterministic: two fresh profiles with seed 7, the same scripted commands and `Advance(1_000_000)` produce field-equal `GameState` graphs and identical event sequences; a `DeepClone` taken mid-boss continues identically.
16. Bot: the median of 20 seeds from `tools/power_curve_model.py` lands within +/-20% of the day-stage targets on days 1, 7 and 30; the C# bot (10) on the real sim lands within +/-15% of the Python model on the same days. The section 8.4 targets are hero-model and stale; party-model targets come from #309 and #249, and the sweep stays report-only until then.
17. Shares (factors as section 7.4): on days 1, 7 and 30 no layer exceeds 40% of `ln(DPS / BASE_ATK)`; on days 7 and 30 every layer except `Base` and speed is at or above 5% (speed is permanently exempt, issue #36), except the tier's exemptions in `acceptance-targets.json`, which report `WARN` (M0: Research, Companions, under review in #309; issue #12).
18. Walls: first wall between stage 60 and 200 in every seed; no wall opened in the first 14 days exceeds 240 active minutes; no wall opened on days 15-30 exceeds the late-wall bound `lateWallMaxActiveMinutes` in `acceptance-targets.json` (issue #36; the bound's value is set from the tool's output and pending).
19. Perf: `Advance(1_000_000)` with the null sink (`CombatEventMask.None`, kills coalesced, the buffer drained) from a stage-300 profile with five companions, five weapons and five skills equipped completes in under 2 s in Release; with `CombatEventMask.None` a fighting tick of that profile allocates nothing; `Advance(100_000)` with the default event mask allocates under 128 bytes per kill. The profile is `PerfFixture` (`src/HeroCat.Bots/Measure/PerfFixture.cs`, MVP content, Awakening rank 3 on every slot, every cast and ability type equipped across the five slots; issue #160, party version in #309), shared by the Perf tests and the `bench` verb.
20. Rated vs realised (issue #10): for a fixture of only the exact parts of section 7.1 (five occupied slots: `DAMAGE` companions in slots 0-1 at full MP, an `MP_REGEN` companion whose restore covers its cost in slot 2, `BUFF_GOLD` companions in slots 3-4; `INSTANT` skills in slots 0 and 2, a `DOT` skill in slot 1; different weapons in slots 0-2, slot 3 unarmed; slot Awakening ranks 2, 1, 0, 0, 0), a static board (no merges, so no missiles), `ATK_SPD` 50 and `CRIT_RATE` 120 / `CRIT_DMG` 80 with slot-hit damage counted at its expected crit value, total damage dealt over a 100,000-tick fight against an unkillable enemy, divided by the fight's seconds, equals `Query.RatedDps` within 1%.
21. Awakening cadence (issue #43): in the 20-seed sweep, every Awakening rank the bot's highest slot reaches within 30 days is bought on the first slot no more than `awakening_targets.max_days_after_gate` days after the day its `AWAKEN_STAGES` gate is first reached (section 12 question 10); the tool and the bot read the same `awakening_targets` rows.
22. Stage select (issue #35): at frontier stage 57 with `KillsInStage == 4`, `SelectStage(56)` succeeds and `SelectStage(58)` is rejected with `Locked`/`STAGE_NOT_CLEARED`; `SelectStage(0)`, a negative stage and `SelectStage(STAGE_CAP + 1)` are rejected with `BadArgument`/`STAGE_OUT_OF_RANGE`, and any `SelectStage` while `Combat.Mode == Dungeon` with `NotAvailable`/`DUNGEON_RUN_IN_PROGRESS` (issue #159); kills on stage 56 pay `Gold(56) * GoldMult` and `Exp(56) * ExpMult`, spawn no boss and leave `Combat.Stage`, `KillsInStage`, `Combat.Boss` and `Progress.HighestCleared` unchanged; `SelectStage(null)` resumes stage 57 at 4 kills; on a walled boss stage the `Walled` phase survives a select and return, and `AttemptBoss` while a side stage is selected clears `Combat.Selected` and spawns the frontier boss.
23. Kill within an attack round: with five occupied slots each dealing exactly 10 per hit (no crit, no skills, no damaging abilities) against a 25-HP enemy, the first round kills on slot 2: `HitLanded` is published for slots 0-2 only, `crit.Counter` and `Stats.PartyHits` rise by 3, and slots 2-4 cast no skill or ability on that tick.
24. Buff scope: a `BUFF` skill in slot 1 multiplies slot 1's hits only; a `BUFF_ATK` companion multiplies every slot's hits; with both active, slot 1's non-crit hit equals `SlotHit(1) * (1 + slotAtkBuffPct / 100) * (1 + atkBuffPct / 100)`; neither changes skill, ability, missile or tap damage.

## 14. Constants introduced

Every constant this spec adds or changes, for lifting into 00 section 6. Tag `S` means `tools/power_curve_model.py` validates it as described in section 8.6.

| Name | Value | Kind | Section | Note |
|---|---|---|---|---|
| `HP_G` | **1.04** (was 1.07, I) | **S** | 8.1 | Re-tag and change proposed. |
| `GOLD_G` | **1.03** (was 1.05, I) | **S** | 8.1 | Re-tag and change proposed. |
| `EXP_G` | **1.025** (was 1.04) | S | 8.1 | Change proposed. |
| `PARTY_LVL_MULT` | 1.02 | S | 5.2 | Renamed from `HERO_LVL_MULT` (00 decision 109). Replaces `HERO_LVL_ATK_PCT = 0.01` (linear) with a compounding term. |
| `PARTY_LVL_EXP(l)` | `100 * 1.08^l` | S | 5.2 | Renamed from `HERO_LVL_EXP`; `party_levels` table (was `hero_levels`). |
| `PARTY_SLOTS` | 5 | I | 2.1 | 00's; replaces `SKILL_SLOTS` and `COMPANION_SLOTS`. Sizes every per-slot combat array (00 decision 109). |
| `BASE_ATK` | 2 per companion (was 10 for the hero) | I | 7.1 | 00's (decision 109); retuned in #249. |
| `ATTACK_PROGRESS_UNIT` | 100 | I | 2.1 | Integer fixed point for attack speed. |
| `TRAIN_PER_LVL[CRIT_RATE]` | 0.0025 | S | 5.1 | |
| `TRAIN_PER_LVL[CRIT_DMG]` | 0.01 | S | 5.1 | |
| `TRAIN_PER_LVL[ATK_SPD]` | 0.01 | S | 5.1 | |
| `TRAIN_PER_LVL[GOLD_GAIN]` | 0.02 | S | 5.1 | |
| `TRAIN_PER_LVL[EXP_GAIN]` | 0.02 | S | 5.1 | |
| `TRAIN_CAP[CRIT_RATE]` | 300 | S | 5.1 | 75% crit rate. |
| `TRAIN_CAP[ATK_SPD]` | 100 | S | 5.1 | 2 attacks/s per slot. |
| `CRIT_DMG_BASE` | 1.5 | S | 5.1 | |
| `HP`, `DEF` trainable | false (MVP) | - | 4 | P1 Tower job. |
| `DEF_K` | 100 (P1) | S | 4 | `DEF / (DEF + DEF_K)` damage reduction in Tower. |
| `MP_POOL0` | 100 | S | 2.4 | Per slot pool (00 decision 109); see section 12 question 9. |
| `MP_REGEN0` | 0.5 per second | S | 2.4 | Per slot pool; a default companion (1 MP/s) runs at half uptime at Research 0 (00 decision 110). |
| `BOSS_ROCK_CANDY_P` | 0.5 | S | 2.6 | One Rock Candy per boss kill at this chance, `loot` stream. |
| `BOSS_AUTO_RETRY_S` | 60 (P1) | S | 2.7 | |
| `AWAKEN_UNLOCKS` | Research at highest slot rank 1, Rune slots at highest slot rank 4/5 | - | 5.3 | Companion-slot unlocks removed (00 decision 109). |
| `AWAKEN_STAGES`, `AWAKEN_COST_ROCK_CANDY`, `AWAKEN_MULT` | 00's values, now per slot; costs 20, 60, 160, 400, 1000 | I/S | 5.3 | Costs cut to a fifth by 00 decision 111. |
| `COMPANION_ATK_MULT[r]` | C 1.0 / R 1.1 / E 1.25 / L 1.5 / M 2.0 | S | 7.1 | 00 decision 111; a `rarities` column (09). |
| `research` table (`res_*` nodes) | section 6 (8 nodes: cost0, growth, max, effect) | S | 6 | `res_fish_cap` validated jointly with 05's tool. |
| `awakening_targets` table (`rank`, `max_days_after_gate`) | 2 for every rank | S | 5.3 | Awakening cadence target read by the tool, 06 and the bots (issue #43). Ticket and Rock Candy income are read from 06's content, not assumed (issues #38, #43). |
| Day-stage targets | 240 / 330 / 430 / 480 / 550 | S | 8.4 | Hero-model; stale until the tool reruns on the party model (#309, #249). |
