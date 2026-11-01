# Engine bridge contract (browser UI <-> C# core)

The browser UI in `web/` plays through the C# `HeroCat.Sim` compiled to WebAssembly. `src/HeroCat.Web` is an **adapter** over the existing `Simulation`, `ContentDb` and `HeroCat.Runtime` save code: it maps them onto the JSON shapes below and adds no game rules of its own beyond read-only derivations listed in `src/HeroCat.Web/MAPPING.md`. Changes to the core stay in the core; this file and `MAPPING.md` change only when the bridge does.

Build: `web/wasm/build.sh` (packs `Content/*.json` with `pack-content.js`, publishes `src/HeroCat.Web`, writes `web/wasm/dist/`). Test: `node web/wasm/test/run-wasm.js`.

## The bridge

One static class `HeroCat.Web.Engine`, all members `[JSExport]`, all arguments and returns `string`, `int` or `double` (JSON text for structured data). One engine instance per page.

```
Init(string contentJson, string saveJson, string profileSeedHex, double nowMs, string profileId) : void
    saveJson empty => new profile (applies tutorial grants and the first-day login).
    profileSeedHex: up to 16 hex chars, a ulong. Ignored when saveJson is non-empty.
Advance(int ticks) : void
State() : string                               // JSON, UiState (below)
Drain() : string                               // JSON array of events, oldest first; clears the queue
Query(string name, string argsJson) : string   // JSON value
Cmd(string name, string argsJson) : string     // JSON result object
Export() : string                              // save text from HeroCat.Runtime SaveSerializer
SetEmitHits(int on) : void
Version() : string                             // e.g. "HeroCat.Sim 0.1.0 netstandard2.1 ... content 1.0"
UiContent() : string                           // JSON, UI labels and display numbers from ContentDb (below)
```

`argsJson` is a JSON array of positional arguments, `"[]"` for none. Unknown names return `{"error":"unknown"}` for `Cmd` and `null` for `Query`; they never throw across the boundary. A thrown C# exception must be caught in `Engine` and returned as `{"error":"<message>"}`.

### Query names (name -> args -> result)

`hp(stage)`, `bossHp(stage)`, `gold(stage)`, `statCost(n)`, `statMult(n?)`, `gearTier(stage)`, `gearBasePct(piece)`, `gearEffPct(piece)`, `gearSlotMult(slot)`, `gearMult()`, `heroLevel(copies)`, `heroPct(id)`, `heroMult()`, `classMult(k?)`, `dps()`, `hitDamage()`, `hitsToKill(hp)`, `killTimeSeconds(stage)`, `isBossStage()`, `bossClearable()`, `needDps()`, `enhanceCost()`, `enhanceSuccess(level)`, `pityRate(n)`, `freePullsRemaining()`, `chestReadyAtMs()`, `chestReady()`, `bulkCost(n, m)`, `maxAffordable(gold?)`, `pullPrice(count)`, `bestParty()`, `autoEquipWouldChange()`, `nowMs()`, `validate()` (array of error strings), `consolidatedRates()`.
A `piece` argument is `{"tier":int,"rarity":int,"enh":int}`. Optional args may be omitted or `null`.

### Command names (name -> args -> result object)

`buyStatLevels(m)`, `buyStatLevelsMax()` (returns a bare JSON number), `enhance(slot)`, `attemptBoss()`, `pull(count)` (result `{ok,results:[{id,name,tier,isNew,levelBefore,levelAfter,shards,pity,forced}],paidWith}` or `{ok:false,reason}`), `equipHero(slot,id)`, `unequipHero(slot)`, `autoEquip()` (bare number), `claimChest()`, `syncWallClock(nowMs)`, `debugGrantGold(n)`, `debugGrantGems(n)`, `debugSetStage(stage)`.

### UiState (what `State()` returns)

camelCase, RNG internals and seeds omitted:
`{ schema, contentVersion, profileId, createdUtcMs, tick, stage:{stage,killsInStage,boss,bossFailsOnStage}, combat:{phase,spawnTimer,enemyHp,enemyMaxHp,enemyIsBoss,attackTimer,bossTimer}, cur:{gold,gems,shards,freePullsDaily,freePullsBanked}, statLevel, gear:[null|{tier,rarity,enh}] (6), heroes:{owned:{id:copies},party:[null|id] (6)}, banner:{pity,totalPulls}, classAdvances, daily:{dayKey,progress:[],rewarded:[]}, prog:{highestFirstClearBoss,chestTimerStartMs,tutorialGranted}, clock:{lastSyncUtcMs,tickAtLastSync}, life:{kills,bossKills,bossFails,gearDrops,enhAttempts,enhSuccesses,goldEarned,offlineGold,firstWallStage,pulls,lplus} }`.
`boss` is `"none"|"pending"|"walled"`; `phase` is `"spawning"|"fighting"`. Numbers are JSON numbers; doubles are written in .NET/JS shortest round-trip form so they parse back to the identical double. `owned` and `heroes.owned` key order is sorted by id.

### Events (what `Drain()` returns)

Objects with `type`, `tick` and per-type fields (see `src/HeroCat.Web/MAPPING.md`): `hit`, `spawn`, `kill`, `bossCleared`, `bossFailed`, `firstClear`, `stageAdvanced`, `classAdvanced`, `gearDropped`, `questCompleted`, `chestClaimed`, `dayRolledOver`, `offlineGold`, `clockWentBackwards`, `statLeveled`, `enhanced`, `pulled`, `stageCap`. Same order within a tick. `hit` events are only emitted after `SetEmitHits(1)`.

### UiContent (what `UiContent()` returns)

Labels and display numbers for the UI, from the same `ContentDb` (UPPER_SNAKE keys, grouped like `specs/00` section 2): `version`, `rarities`, `rarityNames`, `combat{TICK_HZ, ATTACK_INTERVAL_TICKS, KILLS_PER_STAGE, BOSS_EVERY, BOSS_TIME_TICKS}`, `economy{STAT_PER_LVL, STAT_COST_G, PULL_COST_GEMS, FREE_PULLS_PER_DAY, IDLE_CHEST_GEMS, IDLE_CHEST_HOURS, OFFLINE_RATE, OFFLINE_CAP_H, DAY_SECONDS, STAT_BULK_STEPS}`, `quests[{id, kind, target, gems, label}]`, `gear{SLOTS, DROP_P, BOSS_DROP_P, ENH_MAX}`, `banner{PITY_HARD, PITY_SOFT_START, PITY_SOFT_STEP, BASE_LPLUS, M_SHARE_OF_LPLUS, BASE_RATES}`, `heroes{SLOTS, LVL_CUM, pool[[{id, tier, name, short}]]}`, `class{MULT, STAGES, NAMES}` (`NAMES[0]` is the base class). Hero ids use the short form `C01`..`M03`.

### JS facade (`web/engine.js`)

`IdleEngine.create(opts)` loads `wasm/dist/_framework/dotnet.js`, calls `Init` with `wasm/dist/content-pack.json`, and returns an object with `state`, `content` (from `UiContent()`), `heroById`, every query and command above as a method, `advance`, `drain`, `emitHits`, `reload`, `exportSave`, `version`. `state` is refreshed from `State()` at most once per `advance`/command batch; queries are memoised (content-only results forever, build-dependent ones until the build changes, the rest per state version). If loading fails, `create` rejects with a message the UI shows (bundle not built, page opened as a file, or the load error). Saves live in `localStorage['herocat-wasm-save-v1']` as `{v, backend:'wasm', clockOffsetMs, state}`.

## Not exposed yet

The core's command log, `StateHash()` and `Replay` (`specs/03` section 22, `specs/10` section 6) are not exposed through the bridge yet; they become relevant once the web client talks to a certifier (`specs/10` section 4).
