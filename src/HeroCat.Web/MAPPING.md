# Bridge mapping: web/wasm/BRIDGE.md names -> existing C# members

Host code: `Engine.cs` (the `[JSExport]` surface), `Host.cs` (all mapping), `PackSource.cs` (in-memory content), `JsonOut.cs` (JSON writer).
"derived" = computed in the adapter from public members using the core's formulas, read-only, so it can move into the core later.

## Exports
| bridge | C# |
|---|---|
| `Init(contentJson, saveJson, seedHex, nowMs, profileId)` | `ContentDb.Load(new PackSource(pack), verifyHashes: true)`; save: `SaveSystem.DecodeFull(bytes, db)` (header, crc, migrations, validate) then `new Simulation(db, state, sink)`; new profile: `GameState.NewProfile(db, guid, seed, createdSec)`, `ApplyTutorialGrants()`, `EnsureDay(nowSec)`. `Init` throws on a bad save or content on purpose (engine.js falls back); every other export returns `{"error":...}` |
| `Advance(n)` | `Simulation.Advance(n)`; an exception becomes an `engineError` event |
| `State()` | see UiState table |
| `Drain()` | events converted at publication time into a queue (cap 100000, oldest dropped) |
| `Query/Cmd` | tables below; unknown name: `null` / `{"error":"unknown"}` |
| `Export()` | `SaveSystem.Stamp` + `SaveSerializer.Encode` (no file IO; Newtonsoft; one ASCII header line + JSON body) |
| `SetEmitHits(on)` | event mask `Default | EnemySpawned [| HitLanded]` |
| `Version()` | constant text + `content <ContentDb.Version>` |

Content pack: `{ "manifest.json": "<raw text>", "<table>.json": "<raw text>", ... }` from `web/wasm/pack-content.js`; raw text is kept byte for byte so the manifest sha256 checks pass in memory. If `contentJson` is not a pack, the pack embedded in the assembly (`build/content-pack.json`) is used.
Time: `Init(nowMs)` and `syncWallClock(nowMs)` floor to whole seconds (the core's clock unit). Nothing reads a clock.
Hero ids: `hero_c_01` <-> `C01` (tier letter upper-case + number), mapped both ways in every query, command, state and event.

## Queries
| bridge | C# member | note |
|---|---|---|
| hp, bossHp, gold, statCost | `SimQuery.Hp/BossHp/Gold/StatCost` | bossHp uses `BossRuleFor(stage)` (intro rule) |
| statMult(n?) | `SimQuery.StatMult`; with n: `1 + StatPerLvl*n` | derived (arg form) |
| gearTier | `Constants.TierOf` | |
| gearBasePct / gearEffPct (piece) | `SimQuery.GearBasePct/GearEffPct(new GearPiece(0, rarity, tier, enh))` | null piece -> 0 |
| gearSlotMult, gearMult, heroMult, dps, hitDamage | `SimQuery.*` | |
| heroLevel, heroPct(id) | `SimQuery.HeroLevel/HeroPct` | id mapped |
| classMult(k?) | `SimQuery.ClassMult`; with k: `ContentDb.ClassMult(k)` (clamps at 4) | |
| hitsToKill, killTimeSeconds, isBossStage, bossClearable | `SimQuery.*` | hitsToKill saturates at int.MaxValue |
| needDps | derived | `BossHp / (rule.TimeTicks / AttackIntervalTicks) * TickHz / AttackIntervalTicks`, using the core's boss rule for the stage |
| enhanceCost, enhanceSuccess, pityRate, freePullsRemaining | `SimQuery.*` | |
| chestReadyAtMs / chestReady | `ChestReadyAtServerSec * 1000` / `SimQuery.IsChestReady` | whole-second resolution |
| bulkCost(n, m) | derived | sum of `ContentDb.StatCost(n+i)`; the core's `BulkStatCost` only starts at the current level |
| maxAffordable(gold?) | `SimQuery.MaxAffordableStatLevels`; with gold: derived loop (cap 100000) | |
| pullPrice(count) | derived | same payment rule as `Simulation.PullBatch` (daily, banked, gems; x10: banked >= 10 or 10 x cost) |
| bestParty | derived | the order `AutoEquipInternal` uses (HeroPct desc, id ordinal, take HeroSlots) |
| autoEquipWouldChange | derived | best sum > current sum + 1e-9  |
| nowMs | derived | `Clock.LastSyncServerMs + (Tick - TickAtLastSync) * 1000 / TickHz` (the core's `NowServerMs` is internal) |
| validate | `Simulation.Validate()` | `[message]` on `InvariantViolationException`, else `[]` |
| consolidatedRates | derived | the specs/01 section 3.3 loop over `BannerDef.PityRate` |

## Commands
| bridge | C# member | result mapping |
|---|---|---|
| buyStatLevels(m) | `Simulation.BuyStatLevels(m)` | `{ok,cost}` (cost from `Query.BulkStatCost`); `invalid` for m <= 0, else `notEnoughGold` |
| buyStatLevelsMax | `BuyStatLevelsMax()` | count |
| enhance(slot) | `Enhance(slot)` | `{ok,success,level,cost}`; reasons `emptySlot`, `maxed`, `notEnoughGold`; bad slot -> `{"error"}` |
| attemptBoss | `AttemptBoss()` | `bossAlreadyPending`, `noBossToAttempt` |
| pull(count) | `Pull()` / `Pull10()` (count 10, else 1) | `{ok,results,paidWith}`; `NotEnoughGems` -> `cannotAfford`; result fields: see below |
| equipHero(slot, id) | `EquipHero` | `heroNotOwned` (also for unknown ids), `heroAlreadyEquipped` |
| unequipHero(slot) | `UnequipHero` | `{ok:true}` |
| autoEquip | `AutoEquipBestHeroes()` | slots changed; positional, turns AutoEquipBest on |
| claimChest | `ClaimIdleChest()` | `{ok}` / `{ok:false,reason:"notReady"}` |
| syncWallClock(nowMs) | `SyncWallClock(floor(nowMs/1000))` | `null` |
| debugGrantGold/Gems, debugSetStage | `Debug.GrantGold/GrantGems/SetStage` | `null` |

Pull result fields: `id` mapped; `name` = `ContentDb.Text(NameKey)` ; `tier` = `(int)PullResult.Tier`; `isNew, levelBefore, levelAfter` direct; `shards` = `ShardsGained`; **`pity` derived** (pity counter before the batch + position, reset after an L+; the core's `PullResult` has no pity); **`forced` derived** (last slot of a x10, no earlier tier >= GuaranteeMinTier, result below L).

## UiState
| UiState field | C# member |
|---|---|
| schema, contentVersion | `GameState.SchemaVersion/ContentVersion` |
| profileId | `ProfileId` (Guid text) |
| createdUtcMs | `CreatedAtServerSec * 1000` |
| tick | `Tick` |
| stage.* | `Stage.Stage/KillsInStage/Boss (BossPhase -> none,pending,walled)/BossFailsOnStage` |
| combat.* | `Combat.Phase (Spawning,Fighting -> lower case)/SpawnTimer/EnemyHp/EnemyMaxHp/EnemyIsBoss/AttackTimer/BossTimer` |
| cur.* | `Currencies.Gold/Gems/Shards/FreePullsDaily/FreePullsBanked` |
| statLevel, classAdvances | `Progression.StatLevel/ClassAdvances` |
| gear | `Gear.Slots` -> `{tier, rarity:(int)Rarity, enh:Enhance}` or null |
| heroes.owned / party | `Heroes.Copies` (copies > 0, sorted by bridge id) / `Heroes.Equipped`; ids mapped |
| banner.pity / totalPulls | `HeroBanner.PityCounter/TotalPulls` |
| daily.* | `Daily.DayKey/Progress/Rewarded` |
| prog.highestFirstClearBoss | `Progression.HighestFirstClearBossStage` |
| prog.chestTimerStartMs | `Progression.ChestTimerStart * 1000` |
| prog.tutorialGranted | `Progression.TutorialGranted` |
| clock.lastSyncUtcMs / tickAtLastSync | `Clock.LastSyncServerMs/TickAtLastSync` |
| life.kills, bossKills, bossFails, gearDrops, goldEarned, firstWallStage | `Lifetime.*` |
| life.enhAttempts / enhSuccesses / offlineGold | `Lifetime.EnhanceAttempts/EnhanceSuccesses/OfflineGoldEarned` |
| life.pulls | `HeroBanner.TotalPulls` |
| **life.lplus** | **no C# equivalent**: counted by the adapter from pull results this session; restarts at 0 after a reload |

Not exposed (core only): `Currencies.EnhMaterials/DungeonTickets`, `Lifetime.GemsEarned`, `HeroState.AutoEquipBest`, `Rng` (omitted by contract), `SaveMeta`.

## Events
| bridge `type` | C# event | fields |
|---|---|---|
| hit | `HitLanded` | `dmg` = Damage (remaining HP dropped) |
| spawn | `EnemySpawned` | `boss`, `stage` (Hp dropped) |
| kill | `EnemyKilled` | `stage, boss, gold` |
| bossCleared, firstClear | `BossCleared`, `FirstClear` | adapter reorders: the core publishes FirstClear first |
| bossFailed | `BossFailed` | `stage`, `attempt` = FailsOnStage |
| stageAdvanced | `StageAdvanced` | `from, to, viaBoss` |
| stageCap | `StageCapReached` | no fields |
| classAdvanced | `ClassAdvanced` | `advances, stage`; **`dpsAfter` derived** (`Query.Dps` at publication), **`dpsBefore` derived** (`dpsAfter / (ClassMult(k)/ClassMult(k-1))`) |
| gearDropped | `GearDropped` | `slot, piece, equipped, replaced, current` (current = the slot's piece when not equipped), plus **`soldFor`** (core-only) |
| questCompleted | `QuestCompleted` | `id, gems`; **`label` derived** from `ContentDb.Text(quest.NameKey)` |
| chestClaimed | `IdleChestClaimed` | `gems` |
| dayRolledOver | `DayRolledOver` | `dayKey` |
| offlineGold | `OfflineGoldCredited` | `elapsedMs = ElapsedSec*1000`, `creditedS`, `gold` |
| clockWentBackwards | `ClockWentBackwards` | `byMs` |
| statLeveled | `StatLeveled` | `from = NewLevel - Count`, `to`, `cost` |
| enhanced | `EnhanceAttempted` | `slot, success, level = NewLevel, cost` |
| pulled | `HeroPulled` x n | **synthesised** by the pull command from the `PullOutcome` (one event with all results), after the quest event |
| engineError | (adapter) | `message`; only when `Advance` threw |
| (none) | `HeroEquipped` | dropped, the UI has no use for it |

## Missing from the core (bridge cannot expose)
Command log (`ICommandLog`), `Simulation.StateHash()`, `Simulation.Replay(...)`: not present in `HeroCat.Sim`.

## Additive changes to existing projects
None. `src/HeroCat.Web/` is new; `HeroCat.sln` is not modified (the wasm project is built by `web/wasm/build.sh`).
