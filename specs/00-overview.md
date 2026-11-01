# 00 - Overview, Scope and Shared Contracts

Working title: **Whisker Hero**. Genre reference: *Cat Hero: Idle RPG* (Gameduo). Secondary references: Sword x Staff, Maple Idle.

This document is the contract every other spec in `specs/` builds on. If another spec disagrees with a name, number or rule here, this file wins and the other spec is wrong. Fix the other spec.

Sibling project: `idle-rng-game` (a separate repo) is an earlier, narrower prototype of the same genre. Its validated constants and its patterns for RNG streams, save migrations and bot harnesses are reused here where the structure matches. Names differ; the names in this file are the ones to use.

## 1. Product pitch and design pillars

A cat hero auto-fights its way through the Owner's Dreamland on the top half of a portrait phone screen. The bottom half is a fish-merge board the player works with their thumb. Everything the player earns feeds a dozen parallel upgrade systems, and the whole game is deciding where to spend next.

Pillars, in priority order. When two pillars conflict, the higher one wins.

1. **Always something to do, never something you must do.** The sim progresses without input. Active play (fish merging, tapping enemies, spending) accelerates it. A player who only logs in to claim offline rewards and spend still progresses.
2. **Many parallel sinks, each with its own RNG texture.** Training is smooth, summons are spiky, fish merging is tactile, dungeons are scheduled. No single system should be more than ~40% of the power curve so a bad-luck streak in one is offset by the others.
3. **Deterministic, data-driven core.** Every number lives in a content table. The simulation is a pure library that runs identically on device, on the server and in headless bots. Balance is tuned from a spreadsheet and verified by bots before any human sees it.
4. **Honest monetization.** Published odds, hard pity, no pay-to-skip-pity, no loot that is strictly required. Free players reach every system; paying players reach it sooner. Achievements and collection rewards pay out for a *count* of items owned, never for completing a named set, so no reward is a complete-gacha under Japan's rules (08 section on compliance). Every `OWN_*` achievement target is strictly below the current pool size of that kind, and 09 validates it (issue #44).
5. **Live service from day one.** Content, rates, prices and schedules are server-delivered data with version gates, so a balance patch never requires a store release.

## 2. Scope tiers

| Tag | Meaning |
|---|---|
| **M0** | Vertical slice. Proves the core loop on device. Local-only, no backend. |
| **MVP** | Soft-launch feature set. Everything a player needs for 30 days of play, plus backend, IAP and analytics. |
| **P1** | Designed in detail so MVP code leaves room for it. Built after soft launch. |
| **P2** | Mentioned so we do not paint ourselves into a corner. No detailed design. |

### 2.1 Feature inventory

Each row maps a reference-game feature to our system name, its tier, and the spec that owns it.

| Reference feature | Our system | Tier | Owning spec |
|---|---|---|---|
| Auto-combat, stage push, boss every N stages | **Combat**, **Stages** | M0 | 03 |
| Tap enemy to zap | **Tap Zap** | M0 | 03 |
| Hero level via EXP | **Hero Level** | M0 | 03 |
| Gold stat training | **Training** | M0 | 03 |
| Fish Coin research (MP pool, fish cap, misc) | **Research** | MVP | 03, 05 |
| Class / awakening milestones | **Awakening** | MVP | 03 |
| Weapon summon, weapon equip + passive collection bonus | **Weapons**, **Summon** | M0 | 04 |
| Skill summon, 6 auto-cast skill slots | **Skills**, **Summon** | M0 | 04 |
| Companion summon, companion slots, shared MP pool | **Companions**, **Summon** | MVP | 04 |
| Rune summon and rune slots | **Runes** | P1 | 04 |
| Summon level (banner EXP raises rates) | **Banner Level** | MVP | 04 |
| Release fish, merge fish, Magic Tank refill | **Fish Board**, **Magic Tank** | M0 | 05 |
| Fish Coins on new fish discovery | **Fish Discovery** | M0 | 05 |
| Fish missiles damage | **Fish Missiles** | M0 | 05 |
| Auto-merge | **Auto Merge** | P1 | 05 |
| Gold, Diamonds, Fish Coin, Rock Candy, tickets | **Wallet** | M0 | 06 |
| Offline earnings (24 h cap) | **Offline Catch-up** | M0 | 06, 02 |
| Daily missions, weekly missions, achievements | **Quests** | MVP | 06 |
| Fish Coin / Rock Candy / EXP daily dungeons | **Dungeons** | MVP | 06 |
| Tower (unlocks dungeons, ladder of fixed fights) | **Tower** | P1 | 06 |
| Shop: diamond packs, packages, daily free | **Shop**, **IAP** | MVP | 06, 08 |
| Watch-ad rewards | **Rewarded Ads** | P1 | 06, 08 |
| Mail / inbox grants | **Inbox** | MVP | 06, 08 |
| Guest + platform login, cloud save | **Account**, **Cloud Save** | MVP | 02, 08 |
| Guild, guild chat, guild boss | **Guild** | P1 | 08 |
| Guild siege (PvP) | **Siege** | P2 | 08 |
| Raid bosses (co-op) | **Raid** | P2 | 08 |
| Rankings | **Leaderboards** | P1 | 08 |
| Limited events, rate-up banners, season pass | **Events** | P1 | 06, 08 |
| Costumes, famous-cat collabs | **Costumes** | P2 | 04 |
| Redeem codes | **Coupons** | P1 | 08 |

## 3. Tech stack decision

| Layer | Choice | Why, and what was rejected |
|---|---|---|
| Client engine | **Unity 6 (`6000.6.7f1`, pinned in `unity/HeroCatClient/ProjectSettings/ProjectVersion.txt`)**, C#, URP 2D, uGUI + TextMeshPro, Addressables, Unity 2D Animation (Spine if budget allows) | Reference game is Unity; Unity 6 rather than 2022 LTS because 2022.3 is out of support and current Google Play requirements (target API 35, 16 KB pages) need Unity 6. 2D chibi animation, IAP, ads and analytics SDKs are all first-class. Godot 4 C# was considered: fine engine, but mobile IAP/ads SDK maturity is behind. Flutter + Flame rejected: weak for skeletal animation and particle-heavy combat. |
| Simulation core | **`HeroCat.Sim`**, pure C# netstandard2.1, no `UnityEngine`, no IO, no clock, no JSON, no reflection | One implementation runs on device, in server validation and in bots. Determinism across x64/ARM64/IL2CPP requires no `Math.Pow/Exp/Log` on the hot path (repeated multiplication only), no floating-point reductions in unspecified order, no `DateTime.Now`. |
| Runtime glue | **`HeroCat.Runtime`**, plain C#, references Sim + Newtonsoft.Json | Save serialization, migrations, clock, local store, HTTP clients, content loading. Shared by client and bots. No `UnityEngine`. |
| Backend | **ASP.NET Core 8** stateless API, **PostgreSQL** (accounts, saves, purchases, guilds, mail), **Redis** (leaderboards, rate limits, sessions), **S3-compatible object store** (save blobs, content bundles), CDN in front of content | C# so the server references `HeroCat.Sim` for re-simulation and rate tables, no second implementation. The schema (02 section 12, which owns every server table) ships as plain versioned SQL migration files applied by a small runner, not EF Core migrations; queries use Npgsql/Dapper (EF Core for queries only is allowed) (issue #33). Nakama/PlayFab were considered as a lighter path for a solo dev; rejected because server-authoritative summons with our own pity rules and save plausibility checks need custom code either way, and BaaS pricing scales badly with DAU. If the team is one person at M0, the backend is skipped entirely (local seams, see 01). |
| Content | Google Sheet -> JSON tables -> versioned content bundle served via CDN, fallback copy shipped in the app | Designers edit the sheet. Exporter validates and emits JSON. Spec 09. |
| Analytics | Event pipeline to a warehouse (Firebase Analytics for MVP, own events table on the backend for economy events) | Economy events need joins with purchases and saves, which third-party analytics cannot do. |
| CI | GitHub Actions: Sim + Runtime tests on every PR, bot acceptance table on every content change, Unity build via self-hosted runner or Unity Cloud Build | `./check.sh` is the single source of truth: it has modes (`--pr`, `--content`, `--nightly`) and every workflow is a thin wrapper that calls one, with path filters choosing which runs (issue #27). The job that applies the SQL migration set to an empty Postgres 16 and runs 02 AC 16 is wired into `check.sh` behind a "Postgres available" guard, skipped explicitly where there is no Docker (issue #33). Spec 10. |

Orientation: **portrait only**. Minimum targets: iOS 15, Android 8 (API 26), 2 GB RAM devices at 30 fps.

## 4. Architecture in one page

Full detail in `01-architecture-and-data-flow.md`. This section fixes the shape.

```
+---------------------------------------------------------------+
|  Unity client (presentation only)                              |
|  Screens <- ViewModels <- SimEvents ; Screens -> Commands      |
+------------------------------+--------------------------------+
                               |
+------------------------------v--------------------------------+
|  HeroCat.Runtime   SessionRunner, SaveSerializer, Migrations,  |
|                    LocalSaveStore, CloudSaveClient, ServerClock,|
|                    SummonClient, ContentLoader, Analytics       |
+------------------------------+--------------------------------+
                               |
+------------------------------v--------------------------------+
|  HeroCat.Sim       GameState, Simulation.Tick(), Commands,     |
|                    Events, RNG streams, Catch-up, ContentDb    |
+---------------------------------------------------------------+
            ^                                        ^
            | same assembly                          | same assembly
+-----------+-----------+              +-------------+-------------+
|  HeroCat.Bots         |              |  HeroCat.Server (ASP.NET) |
|  headless harness     |              |  Auth, Save, Summon, IAP, |
|                       |              |  Guild, Config, Mail      |
+-----------------------+              +---------------------------+
```

Authority split, which every spec must respect:

| Concern | Authority | Notes |
|---|---|---|
| Combat, stage progress, training, fish board, offline catch-up | **Client sim** | Deterministic; server can re-simulate from a prior save + command log for spot checks. |
| Summon results | **Server** (MVP+), local `ISummonService` in M0 | Server rolls, logs, returns results; client never holds a summon RNG seed in MVP. |
| Summon ticket balances | **Server** ledger (MVP+) | Needed so the summon endpoint can reject `PAYMENT_MISMATCH` (08 section 4.3). M0 holds them in the local `Wallet`; in Remote mode `Wallet.SummonTicket` is a mirror of the ledger (issue #7). |
| Client-earned diamonds and tickets (login, quests, first clears, achievements, tutorial, daily gift) | **Client sim**, credited to the server on upload | Each `PUT /save` reports cumulative per-reason earned counters (02 `Stats`); the server credits the delta since the last upload into `diamond_ledger` / `ticket_ledger` (`ref_type = save_reconcile`), clamped to the per-day allowance, before reconciliation. Summons spend only uploaded earnings, so the client uploads before a pull when it has unsynced earnings (issue #49). |
| Wall-clock time | **Server** | Client stores last trusted server time and monotonic offset; see 02 and 06. |
| Diamond grants from IAP, mail, coupons | **Server** | Receipt validated server-side; grant lands in Inbox; client claims. |
| Save of record | **Server** | Client is a cache. Conflict rules in 02. |
| Guild, leaderboards, events schedule, remote config | **Server** | |
| Dungeon rewards | **Client sim**, server plausibility-checked | Dungeon fights are deterministic sim runs seeded by server-issued seed (MVP), ticked in the main loop with `Combat.Mode = Dungeon` (issue #42). Tickets are debited on clear only; a failed or abandoned run costs nothing (06 section 6). In MVP the server only correlates issued seeds with the save's recent runs and the soft-currency bounds; replaying runs is a P1 follow-up (issue #52). |

Command/event discipline: the client never mutates `GameState`. It sends a `Command` into `Simulation`, which validates, mutates, and emits `Event`s. Runtime-originated changes (server time sync, offline catch-up, summon results) are system commands through the same door (issue #6). View-models subscribe to events. The same commands are what the bot harness and the server re-simulator feed in.

## 5. Canonical vocabulary and identifiers

Use these exact names in code, tables, specs and analytics. Singular for types, plural for collections.

### 5.1 Entities

| Name | Meaning |
|---|---|
| `Profile` | The account-level record: `playerId`, display name, created time, linked platforms. Not part of sim state. Client-only flags (NEW badges, coach marks, sort/filter/step prefs, toggles) are neither `Profile` nor `GameState`: they live in Runtime's `ClientPrefs`, a local JSON store next to the save that is not cloud-synced (issue #47). |
| `GameState` | Save root. Everything the sim reads and writes, including transient fight state; the save drops only what 02 section 3.3 lists. Field names and shapes are 02's (issue #7). Holds the immutable `RngRoot` (Guid) that every RNG seed derives from (issue #9), and the `ContentVersion` and `ContentHash` it was last reconciled against (issue #56). |
| `Hero` | The player's single cat: level, EXP, awakening rank. The equipped weapon, skills, companions and runes live in `LoadoutState` (`Loadout.Weapon`, `Loadout.Skills`, ...), 02 section 3.1 (issue #7). |
| `Stat` | One of `ATK`, `HP`, `DEF`, `CRIT_RATE`, `CRIT_DMG`, `ATK_SPD`, `GOLD_GAIN`, `EXP_GAIN`. `Training` levels are per stat. |
| `Stage` | Integer `s >= 1`. The player farms exactly one stage at a time. `Combat.Stage` is the frontier; `SelectStage(s)` farms a side stage `s <= HighestCleared` (any stage whose kills or boss were completed), paying that stage's gold and EXP without advancing anything, while the frontier's kills and `Walled` state are kept; `SelectStage(null)` returns to the frontier. First-clear diamonds key off `HighestFirstClearBoss` (02, 03; issue #35). Chapter = `floor((s-1)/CHAPTER_WIDTH)+1`, cosmetic only. |
| `Boss` | Enemy at stage `s` where `s % BOSS_EVERY == 0`. Has a time limit. |
| `Wall` | A boss stage the player cannot clear in time. The player keeps farming that stage's normal mobs. |
| `Weapon` | Collectible from the Weapon banner. One equipped; every owned weapon also gives a passive bonus. Dupes raise its level. |
| `Skill` | Collectible from the Skill banner. Up to `SKILL_SLOTS` equipped, auto-cast on cooldown. Dupes raise its level. |
| `Companion` | Collectible from the Companion banner. Up to `COMPANION_SLOTS` equipped; each has an ability that spends from the shared MP pool. Dupes raise its level. |
| `Rune` (P1) | Collectible from the Rune banner. Slot-based stat modifiers. |
| `Banner` | A summon pool: `WEAPON`, `SKILL`, `COMPANION`, `RUNE`. Each has its own pity counter and banner level. |
| `Rarity` | `COMMON`, `RARE`, `EPIC`, `LEGENDARY`, `MYTHIC`. Short codes C/R/E/L/M. Ordered. |
| `Fish` | A tile on the Fish Board with an integer `level >= 1`. Two fish of level `n` merge into one fish of level `n+1`. |
| `FishBoard` | Grid of `FISH_BOARD_W x FISH_BOARD_H` cells, each empty or holding one Fish. |
| `MagicTank` | Timer-filled stock of releasable level-1 fish. Capacity and refill rate are Research-upgradable. |
| `Dungeon` | One of `FISH_COIN`, `ROCK_CANDY`, `EXP`. Ticketed, laddered, sweepable. A run is ticked in the main loop (`Combat.Mode = Dungeon`, one boss per floor); `StartDungeon` is rejected `NotAvailable` with reason `BOSS_ATTEMPT_IN_PROGRESS` while a boss attempt is active, and a sweep is allowed (01 section 8.8, 06 section 1.3). During a run the Fish Board is hidden and frozen: passive board DPS only, the Magic Tank keeps refilling, and board commands are rejected `Locked` with reason `BOARD_LOCKED` (issues #40, #42). |
| `Tower` (P1) | Fixed ladder of boss fights with one-time rewards. |
| `Quest` | A tracked objective with a reward. Kinds: `DAILY`, `WEEKLY`, `ACHIEVEMENT`. |
| `Wallet` | All currency balances. Every change goes through `Wallet.Apply(CurrencyDelta, Reason)`; a debit that would go below zero is rejected and changes nothing (issue #7). |
| `Inbox` | Server-granted items awaiting claim (IAP, mail, coupons, compensation). |
| `Command` | A player or system intent fed to `Simulation`. Named `VerbNoun`. The full registry is the union of each owning spec's command table (01 defines the base type); this list is the canonical vocabulary. Combat/progression (03): `TrainStat(stat, count)`, `TrainStatMax(stat)`, `AttemptBoss`, `TapZap(count)`, `Awaken`, `Research(nodeId)`, `SelectStage(stage?)`. Collections (04): `EquipWeapon`, `EquipSkill`, `UnequipSkill(slot)`, `EquipCompanion`, `UnequipCompanion(slot)`, `LimitBreak`, `AutoEquipBest*`, P1 `CastSkill`. Fish (05): `ReleaseFish`, `ReleaseMax`, `MergeFish`, `MergeAll`, `MoveFish`, `DiscardFish`, P1 `SetAutoMerge`. Economy (06): `ClaimOfflineReward`, `StartDungeon`, `SweepDungeon`, `ClaimQuest`, `ClaimAllQuests`, `ClaimDailyGift`, `ExchangeItem`. Tutorial (07): `SetTutorialStep(step)`, which advances the saved `GameState.Tutorial.Step` (issue #46). System (01; issued by Runtime, never by a screen, and logged like any other command): `ApplyServerTime(nowServerSeconds)`, `ComputeOfflineCatchup(nowServerSeconds)`, `ApplySummonResult(payload)`, `ApplyInboxGrant(grant)` (issues #5, #6, #13). `Summon(banner, count, payment, requestId)` (04), `ClaimInbox(id)` and `ClaimAllInbox` (06) are Runtime flows, not sim commands: the screen calls Runtime, which asks the server (or the M0 local service) and ends the flow with `ApplySummonResult` or `ApplyInboxGrant` (01 sections 2.5 and 8). `RetreatFromBoss` is not in MVP. A refused command returns `ApplyResult.Reject(code, reason)`, mutates nothing and emits nothing: `code` is 01's small shared `RejectCode` enum, `ApplyResult.Reason` is an UPPER_SNAKE key from the owning spec's closed list (for example `STAT_AT_CAP`), localized as `reject.<reason_lower>` (the client lowercases the reason to build the key, issue #248). An insufficient balance is always reason `NOT_ENOUGH_<CURRENCY>`, tickets included (`NOT_ENOUGH_TICKET_SUMMON_WEAPON`, `NOT_ENOUGH_TICKET_EXP`); `NOT_ENOUGH_TICKETS` and `NO_TICKETS` are not used (issue #244). `Apply` never throws on bad input; a malformed argument is rejected with `BadArgument` (issue #11). |
| `Event` | A fact emitted by the sim. Named `NounPastTense`. Combat: `EnemySpawned`, `HitLanded`, `EnemyKilled`, `EnemyKilledBatch` (01's coalesced form), `StageChanged`, `StageCleared`, `StageFirstCleared`, `BossCleared`, `ChapterEntered`, `BossFailed`, `StageCapReached`, `SkillCast`, `CompanionAbilityUsed`, `FishMissileHit`, `TapZapped`, `StatTrained`, `LevelUp`, `Awakened`, `ResearchLeveled`. Collections: `ItemAcquired`, `ItemEquipped`, `ItemLimitBroken`, `BannerLeveledUp`. Fish: `FishReleased`, `FishMerged`, `FishMoved`, `FishDiscarded`, `FishDiscovered`, `TankFishReady`. Economy: `CurrencyChanged`, `QuestCompleted`, `QuestClaimed`, `QuestsBulkClaimed`, `DayRolledOver`, `WeekRolledOver`, `OfflineRewardComputed`, `OfflineRewardClaimed`, `DungeonStarted`, `DungeonSwept`, `DungeonCleared`, `DungeonFloorFirstCleared`, `DungeonFailed`, `DungeonAbandoned`, `ClockWentBackwards`. Tutorial: `TutorialStepChanged`. Currency gains (boss Rock Candy included) are reported by `CurrencyChanged`, never `ItemAcquired`; there is no `FishMissileFired` (the client animates from `FishMerged`). Screens bind to these names; UI-only event names are not added (issue #11). |

### 5.2 Currencies

| Code | Display | Main source | Main sink | Soft/hard |
|---|---|---|---|---|
| `GOLD` | Gold | Kills, offline catch-up | Training | Soft |
| `DIAMOND` | Diamonds | Quests, first clears, IAP, login | Summons, shop | Hard |
| `FISH_COIN` | Fish Coins | Fish discovery, repeat merges and discards (`FISH_MERGE`), Fish Coin Dungeon | Research | Soft |
| `ROCK_CANDY` | Rock Candy | Rock Candy Dungeon, bosses, excess collectible copies past level 10 (04 section 5.3) | Awakening; limit breaks on weapons, skills and companions | Soft |
| `HERO_EXP` | EXP | Kills, EXP Dungeon | Hero Level (auto) | Soft, never spent manually |
| `TICKET_FISH_COIN`, `TICKET_ROCK_CANDY`, `TICKET_EXP` | Dungeon tickets | Daily reset, shop | Dungeon entry | Soft |
| `TICKET_SUMMON_WEAPON`, `TICKET_SUMMON_SKILL`, `TICKET_SUMMON_COMPANION` | Summon tickets | Quests, events, shop | One pull each | Soft |
| `GUILD_COIN` (P1) | Guild Coins | Guild activity | Guild shop | Soft |
| `TICKET_SUMMON_RUNE` (P1) | Rune summon tickets | None until P1 | One rune pull | Soft |

Diamonds are the only currency purchasable with money. Nothing else is ever sold for money directly.

`TICKET_SUMMON_RUNE` is appended after `GUILD_COIN` in the `Currency` enum so earlier indices are unchanged; it exists in schema v1 so the disabled `RUNE` banner row has a valid ticket currency, and stays unused until P1 (issue #26).

### 5.3 Screen identifiers

The full inventory, layouts and states live in `07-screens-and-ux.md`. These IDs are canonical.

| ID | Type | Tier |
|---|---|---|
| `SCR_BOOT` | Full | M0 |
| `SCR_TITLE` | Full (login / account pick) | MVP |
| `SCR_MAIN` | Full. Combat viewport (top), Fish Board (bottom), HUD, bottom tab bar | M0 |
| `SCR_HERO` | Full. Training, hero level, awakening, equipped loadout | M0 |
| `SCR_WEAPONS` | Full. Collection grid, equip, level, passive bonus total | M0 |
| `SCR_SKILLS` | Full. Collection grid, 6 slots, auto-cast order | M0 |
| `SCR_COMPANIONS` | Full. Collection grid, slots, MP pool | MVP |
| `SCR_RUNES` | Full | P1 |
| `SCR_RESEARCH` | Full. Fish Coin sinks | MVP |
| `SCR_SUMMON` | Full. Tabs per banner, 1x/10x, rates, pity, banner level | M0 |
| `SCR_SUMMON_RESULT` | Overlay. Reveal animation, skip, summon again | M0 |
| `SCR_DUNGEONS` | Full. Three dungeon cards + Tower card | MVP |
| `SCR_DUNGEON_RUN` | Full. Replaces combat viewport during a run | MVP |
| `SCR_STAGE_SELECT` | Modal. Chapter list, farm a cleared stage, return to the frontier, frontier boss challenge (issue #35) | MVP |
| `SCR_QUESTS` | Full. Daily / weekly / achievements tabs | MVP |
| `SCR_SHOP` | Full. Diamonds, packages, daily free, exchange | MVP |
| `SCR_INBOX` | Modal | MVP |
| `SCR_OFFLINE_REWARD` | Modal, shown on return | M0 |
| `SCR_ITEM_DETAIL` | Modal. Any collectible's stats, level, next-level cost | M0 |
| `SCR_SETTINGS` | Full. Account link, notifications, language, restore purchases, delete account, support, licenses | MVP |
| `SCR_PROFILE` | Modal. Name, avatar, stats | MVP |
| `SCR_GUILD` | Full | P1 |
| `SCR_RANKING` | Full | P1 |
| `SCR_EVENTS` | Full | P1 |
| `SCR_TUTORIAL` | Overlay on `SCR_MAIN`. Steps are `tutorial_steps` rows that wait on events (`expect_event`, `expect_count`); ticks keep running except on step 1 (issue #46) | MVP |
| `SCR_CONFIRM` | Modal. Generic confirmation with cost and balance-after line (Awakening, IAP, delete account, exit) | M0 |
| `SCR_ERROR` | Modal or inline card driven by 01's error taxonomy (boot failure, maintenance, network loss, summon timeout). 01 classifies responses by `(status, code)` with 08's wire codes canonical and localized as `err.<code_lower>.title` and `.body` (the UPPER_SNAKE code lowercased in the key, issue #248); the non-blocking variant is a card in the action-strip position (issue #30) | M0 |

Locked states come from one content table, `feature_unlocks` (09), keyed by UPPER_SNAKE feature id (`RESEARCH`, `COMPANIONS`, `QUESTS`, `DUNGEONS`, `SHOP`) with a condition `ALWAYS`, `STAGE_HIGHEST >= n` (reads `Progress.HighestCleared`) or `AWAKEN_RANK >= n`, read by both the sim's rejections and the client's Locked states through `ContentDb.IsUnlocked`. Seed values: Research at Awakening rank 1 (decision 11), Companion banner open from the start, Quests at stage 10, Dungeons at stage 30 (the row replaces the former `DUNGEON_UNLOCK_STAGE` constant), Shop never locked (issue #45).

## 6. Canonical constants

Two kinds of constants appear here.

- **Inherited** (`I`): validated in the sibling project by `idle-rng-game/tools/gacha_model.py` and `power_curve_model.py` for the same loop structure. Trust them as a starting point.
- **Seed** (`S`): new to this design, not yet validated. `03` and `05` define the tool that validates them (`tools/power_curve_model.py` and `tools/fish_model.py` in this repo). Until those tools run, treat `S` values as placeholders and do not hard-code them anywhere.

Code must read every constant from content data (spec 09), never from a literal.

### 6.1 Combat and stages

| Name | Value | Kind | Notes |
|---|---|---|---|
| `TICK_HZ` | 10 | I | Fixed sim tick. 1 tick = 100 ms. |
| `BASE_ATK`, `BASE_HP` | 10, 100 | I/S | Hero at Training level 0, no multipliers. |
| `ATTACK_INTERVAL_TICKS` | 10 | I | One hit per second at `ATK_SPD` 0. |
| `HP0`, `HP_G` | 20, 1.04 | S | Enemy HP at stage `s` = `HP0 * HP_G^s`. Sibling used 1.07 with a gear-tier layer carrying ~75% of log-power; this game has no gear tiers, and 03's scratch bot stalled at stage 250 by day 30 under 1.07. Re-tagged S until `tools/power_curve_model.py` confirms. |
| `KILLS_PER_STAGE` | 10 | I | Kills to advance a normal stage. |
| `SPAWN_DELAY_TICKS` | 5 | I | |
| `BOSS_EVERY` | 10 | I | |
| `BOSS_HP_MULT` | 10 | I | |
| `BOSS_TIME_TICKS` | 300 | S | 30 s (reference game is more lenient than the sibling's 20 s because skills and companions add burst). |
| `BOSS_GOLD_MULT` | 3 | I | |
| `CHAPTER_WIDTH` | 50 | S | Cosmetic grouping, background and enemy skin change. |
| `STAGE_CAP` | 9000 | I | `double` range guard. Sim emits `StageCapReached`. |
| `TAP_ZAP_DMG_PCT` | 0.5 | S | One tap = 50% of one hero hit. |
| `TAP_ZAP_MAX_PER_SEC` | 6 | S | Taps beyond this are ignored (bot-tap guard). Caps active contribution at ~3 hits/s equivalent. |

### 6.2 Economy

| Name | Value | Kind | Notes |
|---|---|---|---|
| `GOLD0`, `GOLD_G` | 2, 1.03 | S | Gold per kill at stage `s`. Moved with `HP_G` (03 section 8). |
| `EXP0`, `EXP_G` | 5, 1.025 | S | Hero EXP per kill. Moved with `HP_G` (03 section 8). |
| `TRAIN_PER_LVL[ATK]` | 0.05 | I | ATK multiplier = `1 + 0.05 * n`. Other stats in 03. |
| `TRAIN_COST0`, `TRAIN_COST_G` | 50, 1.07 | I | Cost of level `n -> n+1` = `50 * 1.07^n` gold, per stat. |
| `TRAIN_BULK_STEPS` | 1, 10, 100, Max | I | |
| `DIAMONDS_PER_DAY_F2P` | 400 | S | Target free diamond income from dailies + first clears + login. Tuning target. Quest rows are the single source of quest diamond amounts (no duplicate quest-diamond or bonus constants); 09's `E039` recomputes 06's reference day from the rows, `REF_DAILIES_DONE` and `REF_NEW_BOSSES`, within 10% (issues #55, #244). |
| `REF_DAILIES_DONE` | 8 | S | Reference-day daily missions completed, read only by `E039`. |
| `REF_NEW_BOSSES` | 4 | S | Reference-day new boss first clears, read only by `E039`. |
| `PULL_COST[WEAPON/SKILL/COMPANION]` | 100 / 200 / 300 | S | Per single pull. 10-pull = 10x, no discount; discounting is done through banner level instead. Matches the reference game's pricing. |
| `DAY_SECONDS` | 86400 | I | Day key = `floor(serverSeconds / DAY_SECONDS)`. Rollover 00:00 UTC. |
| `OFFLINE_RATE` | 0.5 | I | Offline gold/EXP per second as a fraction of in-app kill rate at the current stage. |
| `OFFLINE_CAP_H` | 24 | S | Reference game caps at 24 h. Sibling used 8 h; 24 h is friendlier and is what players expect in this sub-genre. Catch-up and 08's `OFFLINE_WINDOW_OVERLAP` check use the effective cap `OFFLINE_CAP_H + 2 h * RES_OFFLINE_CAP level` (issue #21). |
| `OFFLINE_MIN_S` | 60 | S | Gaps shorter than this credit nothing and show no modal. |
| `TUTORIAL_DIAMONDS` | 1500 | S | One-time grant at profile creation, together with one starter weapon `TUTORIAL_WEAPON_ID` so collection screens are never empty; both come only from 09's `tut_welcome` step grant, guarded by `Tutorial.TutorialDiamondsGranted` (02; issue #46). |
| `LOGIN_DIAMONDS` | 50 | I | Once per day key, auto-granted by `EnsureDay` with reason `LOGIN`. A `constants` row; the `d_login` daily quest only counts toward the bonus thresholds and pays nothing (issue #20). |
| `FIRST_CLEAR_DIAMONDS` | 10 | I | Per boss stage cleared for the first time, reason `FIRST_CLEAR`, keyed off `HighestFirstClearBoss`; paid from M0 (03, 06 section 1.2). |
| `DUNGEON_TICKETS_PER_DAY` | 2 | S | Per dungeon; rollover tops each `TICKET_*` dungeon balance up to it, never down (06 section 4.2). |
| `DUNGEON_FLOOR_STAGE_STEP` | 20 | S | Dungeon floor `f` is as hard as the stage-`20f` boss (06 section 6.1). |
| `DUNGEON_FLOORS_MVP` | 40 | S | Floors per dungeon in MVP (06 section 6.4). |
| `EXP_DUNGEON_KILLS` | 300 | S | EXP Dungeon floor `f` pays this many kills' EXP at stage `20f` (06 section 6.3). |
| `DAILY_GIFT_WEAPON_TICKETS` | 1 | S | `TICKET_SUMMON_WEAPON` per day from `ClaimDailyGift`, reason `DAILY_GIFT` (06 section 8.3). |
| `INBOX_EXPIRY_DAYS` | 30 | S | Default inbox item lifetime from `availableAt`; `IAP` items never expire (06 section 9). |

### 6.3 Summons

| Name | Value | Kind | Notes |
|---|---|---|---|
| Base rates | C .60 / R .28 / E .09 / L .025 / M .005 | I | Banner level 1. Sum 1.0. Higher banner levels shift weight from C to R/E (04). |
| `PITY_SOFT_START` | 50 | I | Pulls 1-50 since last L+ use base rate. `pity` = pulls since the last L+ before this pull, and the pull number is `n = pity + 1` (issue #24). |
| `PITY_SOFT_STEP` | 0.0325 | I | L+ rate on pull `n` = `min(1, 0.03 + 0.0325*(n-50))`. |
| `PITY_HARD` | 80 | I | Forced L+. P99 is 62, so this is a published guarantee, not a design lever. |
| `M_SHARE_OF_LPLUS` | 1/6 | I | |
| 10-pull guarantee | at least one E+ | I | If slots 1-9 are all below Epic, slot 10 runs the normal L+ check first and is Epic if it fails; never a renormalised draw over E/L/M, so the published L+ rate is unchanged. 04 owns the one pull algorithm (banner level evaluated per pull) shared by the server, `LocalSummonService` and `gacha_model.py` (issue #24). |
| `BANNER_EXP_PER_PULL` | 1 | S | Banner level thresholds (`BANNER_LVL_EXP`) in 04. Per-banner F2P pull cadence is computed by the tool from 06's income and 03's bot policy, never hand-entered; `BANNER_LVL_EXP` is tuned from that output so weapon banner level 4 lands in days 20-35: 0, 10, 25, 55, 90, 145, 215, 305, 415, 540 (20-seed median day 27 in `power_curve_model.py --check`; issue #38). |
| `SKILL_SLOTS` | 6 | I | |
| `COMPANION_SLOTS` | 4 | S | |
| `RUNE_SLOTS` (P1) | 6 | S | |
| `DUPE_LVL_CUM` | 1,3,6,10,16,25,40,64,100,160 | I | Total copies for level 1..10, all collectible kinds. |
| `DUPE_LVL_BONUS` | 0.20 | I | Each level past 1 adds 20% of base. Level 10 = 2.8x base. |
| Pool sizes | Weapons 30, Skills 40, Companions 50 | S | Reference game ships 40+ skills, 50+ companions. |
| `WEAPON_PASSIVE_PCT[r]` | C 1 / R 2 / E 4 / L 8 / M 16 | S | ATK% per owned weapon of rarity `r`, scaled by its level. Unequipped weapons still count. |

### 6.4 Fish Board

| Name | Value | Kind | Notes |
|---|---|---|---|
| `FISH_BOARD_W`, `FISH_BOARD_H` | 5, 4 | S | 20 cells. |
| `TANK_CAP0` | 10 | S | Magic Tank capacity at Research 0. A new profile starts with a full tank (decision 99). |
| `TANK_REFILL_S0` | 6 | S | Seconds per fish at Research 0. |
| `FISH_MAX_LVL0` | 10 | S | Highest fish level reachable before Research raises the cap. A saved fish above a lowered cap stays, keeps its board DPS and cannot merge; loads clamp fish only to the `fish_levels` row count (issue #41). |
| `FISH_DISCOVERY_COINS[n]` | `10 * 2^(n-1)` | S | Fish Coins the first time a level-`n` fish is created. |
| `FISH_MERGE_COINS_PCT` | 0.02 | S | Each merge pays 2% of that level's discovery value, unrounded (`FISH_COIN` is a `double`; the UI floors the display) (issue #16). |
| `FISH_MISSILE_PCT[n]` | `5 * 1.5^(n-1)` | S | Damage of a fish missile fired when a level-`n` fish is created, as % of one hero hit. |
| `FISH_BOARD_DPS_FACTOR` | 0.77 | S | Named so 09 can hold it as a `constants` row. Set so the reachable cap-10 board (11 cap fish plus one each of levels 1-9, 05 section 4.3) gives `fishMult` ~x20 (decision 97, issue #247). |
| `FISH_BOARD_DPS_PCT` | `sum over board of FISH_BOARD_DPS_FACTOR * FISH_MISSILE_PCT[level]` | S | Passive DPS% contributed by fish sitting on the board. Design lever so the board matters when idle. A board of `W * H` cells at cap `c` holds at most `W * H - c + 1` cap fish plus one of each lower level (05 section 4.3). |

### 6.5 Awakening and Hero Level

| Name | Value | Kind |
|---|---|---|
| `AWAKEN_STAGES` | 50, 150, 300, 500, 800 | I (first four) / S |
| `AWAKEN_MULT` | 1.5 per rank | I |
| `AWAKEN_COST_ROCK_CANDY` | 100, 300, 800, 2000, 5000 | S |
| Awakening cadence target | each rank affordable within `max_days_after_gate` = 2 days of its `AWAKEN_STAGES` gate; content table `awakening_targets (rank, max_days_after_gate)`, read by the tools and the bots (issue #43) | S |
| Awakening unlocks | rank 1: `SCR_RESEARCH` (via `feature_unlocks`, section 5.3) + companion slot 2; rank 2: companion slot 3; rank 3: companion slot 4; ranks 4-5: rune slots (P1) | S |
| `HERO_LVL_EXP(l)` | `100 * 1.08^l` | S |
| `HERO_LVL_MULT` | 1.02 | S | Hero layer = `HERO_LVL_MULT ^ heroLevel`. Replaces the earlier linear `HERO_LVL_ATK_PCT = 0.01`; without one compounding stage-fed layer the frontier stalls under any curve (03 section 8). 09's `hero_levels` table carries both columns; only `mult_per_level` is populated. |
| `RES_OFFLINE_CAP` | +2 h per level, max 6 levels | S | Research node extending `OFFLINE_CAP_H` from 24 h to at most 36 h. |
| `LIMIT_BREAK_RANKS` | 5 | S | Rock Candy sink on weapons, skills and companions: each rank adds one effective dupe level. Costs by rarity in 04 section 5.2. In `REQUIRED_CONSTANTS`; the per-rarity values are the required `rarities.limit_break_cost` and `rarities.dupe_overflow_rock_candy` columns (09). Debits use reason `LIMIT_BREAK` (issue #37). |

### 6.6 Session model and targets

Assumed F2P player: 2 h active per day (the bot harness splits it into two 60-minute sessions so weekly keys and the 00:00 UTC rollover are both exercised, with the gap lengths alternating so that on alternate days a session is open across 00:00 UTC (issue #29); 10 section 3.3), offline the rest. Targets the bot harness must land within +/-20%:

| Day | Stage |
|---|---|
| 1 | 240 |
| 3 | 330 |
| 7 | 430 |
| 14 | 480 |
| 30 | 550 |

Days 14 and 30 match the sibling's targets by construction; days 1-7 are higher because `HP_G` is flatter and there is no gear-tier layer to wait for. These numbers come from 03's scratch model and are provisional until `tools/power_curve_model.py` reproduces them. They are also a content table (`progress_targets`, 09) so the bots read them from `ContentDb`. Known open balance problem: the scratch model still shows a ~460-active-minute wall around day 20 when the summon layers saturate. The fix is chosen from 03 section 9's levers (candidates `RES_ATK` 0.08 per level, limit-break repricing) using `tools/power_curve_model.py` output, and is pending that tool; 03 AC18 gains a day-14-to-30 wall bound (issue #36).

## 7. The power formula

```
DPS(s) = BASE_ATK
       * (1 + TRAIN_PER_LVL[ATK] * trainLvl[ATK])          # Training, smooth gold sink
       * HERO_LVL_MULT ^ heroLevel                          # Hero Level, compounding, fed by EXP
       * (1 + equippedWeaponPct + sum_owned weaponPassivePct) # Weapons, summon-driven
       * (1 + sum_equipped skillDpsPct)                      # Skills, burst and auto-cast
       * (1 + mpSufficiency * sum_equipped companionDpsPct)  # Companions, MP-gated (03 sections 2.4 and 7, 04 section 8.2); MP_REGEN companions count as regen
       * (1 + (FISH_BOARD_DPS_PCT + missileDpsPct) / 100)   # Fish Board; missileDpsPct is rated-only
       * (1 + RES_ATK_pct)                                   # Research, direct DPS node (03 section 6)
       * (1 + runePct)                                       # Runes (P1)
       * AWAKEN_MULT ^ awakenRank                            # milestone layer
       * critFactor(CRIT_RATE, CRIT_DMG) * speedFactor(ATK_SPD)
```

This is the **rated** DPS: the expected value used by `Query`, the wall check, layer shares, the bots, the tools and 08's plausibility bound. It excludes transient buffs. The tick loop deals damage differently: a hero hit carries only the layers that have no discrete event (Training, Hero, Weapons, passive board DPS, Research, Runes, Awakening, active buffs), and skills, companions, fish missiles and Tap Zap deal their own multiples of that hit, so no layer is counted twice. Skills deal multiples of `SkillUnit = HeroHit * critFactor * speedFactor` and companions multiples of `CompanionUnit = SkillUnit * (1 + skillDpsPct)`, so companion damage scales with the skill layer; this may be revisited once `tools/power_curve_model.py` reports layer shares (issue #243). `mpSufficiency = min(1, mpRegen / sum(mp_cost / cooldownSeconds))`, where the net MP restored by `MP_REGEN` companions counts as regen (issue #39). `missileDpsPct` never enters the live hit; a live display number, if any, is a runtime-only rolling average kept out of the save and `StateHash`. 03 section 7.1 owns the decomposition and tests that measured DPS over a long fight matches the rated value within 1% (issues #10, #15).

Enemy HP grows `HP_G^s`. The budget for each layer's share of the residual is set in `03`. Rule of thumb from pillar 2: no layer above 40% of log-power at day 30, no layer below 5% so it still feels worth upgrading. The bots check the 40% ceiling on days 1, 7 and 30 and the 5% floor on days 7 and 30 only; `Base` is not a layer for the floor (crit is); speed is permanently exempt from the floor at every tier because it cannot meet it once `ATK_SPD` is capped (issue #36), and per-tier floor exemptions (M0: Research, Companions) live in `acceptance-targets.json` and report `WARN` instead of failing (issue #12). 03's day-30 shares under the current constants: Hero 26%, Training 14%, Fish 15%, Weapons 10%, Skills 8%, Companions 7%, Awakening 6%, crit 6%, Research 4%, speed 3%. Research misses the 5% floor; the lever is chosen from 03 section 9 with `tools/power_curve_model.py` output, pending that tool (issue #36).

The hero cannot die. `HP` and `DEF` remain in the `Stat` enum but are not trainable in MVP; their P1 job is Tower survival (03 section 4).

## 8. Cross-spec contracts

1. **Sim is pure.** `HeroCat.Sim` has no `UnityEngine`, IO, clock, JSON, reflection, `Math.Pow/Exp/Log`. Curves are evaluated by repeated multiplication from tables. Everything that needs IO lives in `HeroCat.Runtime`.
2. **Named RNG streams.** Each consumer owns a stream `(seed, counter)` persisted in the save: `combat` (reserved, no draws in MVP), `crit` (one draw per stage-fight hero hit; dungeon-fight hits draw from the temporary per-run `dungeon_run` stream and are not counted in `Stats.HeroHits`), `fish_spawn` (one draw per successful release), `dungeon` (issues run seeds locally), `loot` (one draw per boss kill), plus `summon_weapon`, `summon_skill`, `summon_companion` (three draws per pull). 03 owns the combat draw order. Every seed is the first 8 bytes of SHA-256(`RngRoot` ++ UTF-8 stream name), derived in Runtime (the sim stays hash-free). `RngRoot` is an immutable Guid set once at profile creation (`PlayerId` if known, else a fresh random Guid) and never re-derived, so seeds do not change when an account is linked; a migration that adds a stream derives its seed from `RngRoot` inside the step (issue #9). The summon streams are drawn only by `LocalSummonService` (M0 and bots), which rolls on a clone and returns the counter after the pull in the payload; `ApplySummonResult` sets it (issue #6). In Remote mode (MVP+) their counters are ignored, not removed, so schema v1 is one shape for every mode (02 section 3.4). Invariants, checked by the validator and 08: `combat.Counter == 0` (MVP), `crit.Counter == Stats.HeroHits`, `loot.Counter == Stats.BossKills`, `fish_spawn.Counter == Stats.FishReleased`, Local-mode `summon_x.Counter == 3 * TotalPulls` (issue #8). Enemy skins are chosen by hashing `(stage, killIndex)` and consume no stream (09 section 5.6). A dungeon run draws from a temporary `dungeon_run` stream seeded by the run's seed, held in `Combat.DungeonRun` and dropped when the run ends (issue #42).
3. **Content is data.** Every number in section 6 is a row in a table from spec 09. Code receives a `ContentDb` and never a literal. Content versions are `YYYY.MM.DD[.N]` with no major/minor semantics; breaking changes are expressed through `min_client_version` plus `schema_version` in the manifest. Content applies at the next cold boot, except the hot-apply allowlist `localization`, `shop_products`, `shop_packages`, `inbox_templates` (01 section on content hot update). The exporter computes the manifest's `hot_apply` bool from this allowlist, which is the single source; 08's `hotApplyTables` in `GET /config` only echoes it, and a Sim test proves no sim code reads `shop_packages` or `inbox_templates` (issue #54). `GET /config` applies the rollout bucket server-side from the JWT (no JWT gets the non-rollout default); the client never hashes its own bucket (issue #31). Loading a save against other content runs 02's validator content rules, with no refund in MVP (issue #56).
4. **Save schema is versioned from v1** with forward-only migrations and a fixture per version (02).
5. **Commands in, events out.** The client, bots and server re-simulator only talk to the sim through `Simulation.Apply(Command)` and `Simulation.Advance(ticks)`, and only observe it through `Event`s and read-only snapshots. Nothing else mutates `GameState`: server time sync, offline catch-up and summon results enter as the logged system commands `ApplyServerTime`, `ComputeOfflineCatchup` and `ApplySummonResult`, so replaying the command log reproduces the state exactly. `ApplySummonResult(payload)` is the only summon mutator in both modes: it debits in Local mode and replaces balances with the server's in Remote mode (issues #6, #13).
6. **Every currency change has a reason.** `Wallet.Apply(delta, reason)` where `reason` is from a closed enum: `KILL`, `BOSS`, `OFFLINE`, `LOGIN`, `TUTORIAL`, `FIRST_CLEAR`, `QUEST`, `ACHIEVEMENT`, `DAILY_GIFT`, `DAILY_RESET`, `SUMMON`, `TRAINING`, `RESEARCH`, `AWAKENING`, `LIMIT_BREAK`, `IAP`, `MAIL`, `DUNGEON`, `FISH_DISCOVERY`, `FISH_MERGE`, `SHOP_EXCHANGE`, `REFUND`, `ADMIN`. `ADMIN` is reserved for operator interventions; game grants always have their own code. The analytics event for every currency change carries it.
7. **Server time is the only trusted time.** `IClock` with `ServerClock` (MVP) and `FakeClock` (bots, tests). Day keys, offline gaps, dungeon resets and banner schedules all derive from it. `dayKey = floor(serverSeconds / DAY_SECONDS)`; `weekKey = floor((dayKey - 4) / 7)`, weeks start Monday 00:00 UTC. Clock going backwards grants nothing twice (02, 06). Between syncs the sim advances a virtual server clock per tick and rolls the day on the tick that crosses midnight; Runtime issues `ApplyServerTime` only at boot, resume, gap recovery and every `WALL_CLOCK_SYNC_SEC = 60` s (a `RuntimeConfig` constant), which re-anchors the clock and handles any jump (issue #5). Offline catch-up is two-phase: `ComputeOfflineCatchup` runs `Catchup.Compute`, stamps `Time.LastTickServerTime` and stores `Offline.Pending` immediately, `ClaimOfflineReward` credits it; ticks are paused while `SCR_OFFLINE_REWARD` is open, so the numbers shown are exactly what is credited. A second gap while a reward is pending sums into the same record (keeping the latest stage for display), so the window is consumed at compute time, never at claim time. The Magic Tank fill is computed at compute time with 05's `CatchUpTank` over the credited ticks and applied at claim (issue #21).
8. **Screens bind to view-models, view-models subscribe to events.** No screen reads `GameState` directly and no screen polls per frame except the combat viewport and HUD counters, which read a per-tick snapshot.
9. **Curve-valued numbers are `double`** guarded by `STAGE_CAP`: `GOLD`, `FISH_COIN`, `Hero.Exp`, HP, damage. Counted currencies are `long`: `DIAMOND`, `ROCK_CANDY`, every ticket. `HERO_EXP` is `Hero.Exp`, not a `Wallet` balance, though it still flows through `Wallet.Apply` for the reason code. Display formatting uses the suffix ladder K, M, B, T, aa, ab, ac... (07): round to 3 significant digits first, then choose the suffix (`999.4 -> 999`, `999.5 -> 1.00K`); multipliers use the ladder too (`x1.50K`); NaN and infinity show `?`. The one exception is integer counts below 1e6, shown in full with locale grouping (issue #22).
10. **All user-visible text is a localization key.** No literal strings in prefabs or code (07, 09).
11. **Server-sensitive operations go through an interface** with a local implementation for M0 and a documented remote contract: `IClock`, `ISummonService`, `ISaveStore`, `IInboxService`, `IReceiptValidator`, `IConfigService`, `IDungeonSeedService` (01, 08). The seventh seam issues dungeon run seeds: locally one `dungeon` stream draw (M0), the server endpoint from MVP (issue #25).
12. **The bot harness is the acceptance test** for any balance change (10). A content PR that moves the day-30 stage outside the tolerance band fails CI. `StateHash` is SHA-256 over the canonical binary state encoding, and the golden command log is JSONL in the same format as 01's runtime command log, so one parser serves upload, server re-sim and golden replay (issue #28).
13. **Idempotency for every server mutation.** Each client-originated mutating request carries a client-generated `requestId`; the server stores results keyed by it for 24 h and replays them on retry (01, 08).

## 9. Spec writing conventions

Every spec file follows this skeleton so readers know where to look:

1. Title `## NN - Name`, one-paragraph purpose, scope tiers covered, and a `Depends on:` line naming the specs it builds on.
2. Body, with tables for data and C#-style pseudocode for logic. Mermaid for diagrams (GitHub renders it); ASCII when a mermaid diagram would be unreadable.
3. `## Open questions` with an owner guess and a default we will take if nobody answers.
4. `## Acceptance criteria`, numbered, each testable by a bot, a unit test or a checklist item.

Rules: use the names from section 5 exactly; mark every feature with its tier tag; when you introduce a constant, add it to your spec's own constants table **and** list it in your final section "Constants introduced" so it can be lifted into this file.

## 10. File map

```
herocat/
  README.md
  specs/
    00-overview.md                     this file
    01-architecture-and-data-flow.md   layers, commands/events, every end-to-end flow, threading, network
    02-domain-model-and-persistence.md entities, save schema v1, migrations, local + cloud save, server DB
    03-combat-and-progression.md       combat loop, stats, training, hero level, awakening, research, power curve
    04-summons-and-collections.md      banners, pity, banner level, weapons, skills, companions, runes
    05-fish-merge-minigame.md          board, tank, merge rules, discovery, missiles, auto-merge
    06-economy-and-daily-loop.md       currencies, daily sources/sinks, quests, dungeons, offline, shop, inbox
    07-screens-and-ux.md               navigation map, every screen, states, UX and design rules
    08-backend-and-live-ops.md         services, API contracts, auth, IAP, anti-cheat, config, compliance
    09-content-data-pipeline.md        table schemas, exporter, validation, ContentDb, localization
    10-testing-release-and-ops.md      test pyramid, bots, CI/CD, store release, KPIs, runbooks, roadmap
  tools/
    power_curve_model.py               (sibling copy; rewrite to 03 section 8.6) greedy F2P bot across all layers, prints the day table
    gacha_model.py                     (sibling copy; rename streams, 04 section 14) pity distribution; --vectors writes Fixtures/Summon/vectors.json
    fish_model.py                      tank/merge throughput, coin and DPS income per active minute
    export_content.py                  sheet exporter, verb CLI (pull/build/check/diff/...), spec 09
```

Planned source layout:

```
src/HeroCat.Sim/            GameState, Simulation, Commands, Events, Rng, Economy, FishBoard, Catchup, ContentDb
src/HeroCat.Runtime/        SaveSerializer, Migrations, LocalSaveStore, CloudSaveClient, ServerClock, FakeClock,
                            SummonClient, LocalSummonService, SessionRunner, ContentLoader, Analytics
src/HeroCat.Sim.Tests/
src/HeroCat.Runtime.Tests/
src/HeroCat.Bots/           headless harness (spec 10)
src/HeroCat.Server/         ASP.NET Core API (spec 08)
src/HeroCat.Server.Tests/
unity/HeroCatClient/        Unity project; Assets/HeroCat.Client references Sim + Runtime as local packages
content/                    JSON tables exported from the sheet (spec 09); lowercase, replaces today's Content/ (issue #1)
tools/                      Python models and the sheet exporter (export_content.py replaces content_build.py)
Fixtures/Summon/            summon vectors (Python-generated) and the C# stream-sequence determinism golden (issue #14)
```

## 11. Milestones

| Milestone | Contents | Exit criterion |
|---|---|---|
| **M0 vertical slice** | Sim: combat, stages, training, hero level, weapon + skill summon (local), fish board, offline catch-up, local save. Client: `SCR_MAIN`, `SCR_HERO`, `SCR_WEAPONS`, `SCR_SKILLS`, `SCR_SUMMON`, `SCR_OFFLINE_REWARD`, `SCR_ITEM_DETAIL` with placeholder art. Bots print the day table. | Two weeks of simulated play within tolerance; one real person plays 20 minutes on device without being told what to do. |
| **MVP** | Everything tagged MVP in section 2.1. Backend: auth, cloud save, server summons, IAP, inbox, config. Analytics. Tutorial. Localization: EN ships (`ship_locales = en` in shipped meta); `ja` is fully translated in the pipeline and CI fixtures use `en,ja` so the second-locale path is tested (issue #57). | Closed beta of 50 players, D7 retention measured, no save-loss reports. |
| **Soft launch** | Store compliance checklist (10) complete, staged rollout in two small markets. | D1 >= 40%, D7 >= 15%, crash-free sessions >= 99.5%, no P0 economy exploit. |
| **P1** | Runes, Research expansion, Tower, Guild, Rewarded Ads, Events, Leaderboards, Coupons, Auto Merge. | Scheduled by soft-launch data. |
| **P2** | Siege, Raid, Costumes. | Not scheduled. |

## 12. Decisions log

Decisions taken in this document that other specs must not reopen without updating this file:

1. Unity client, pure-C# sim, ASP.NET Core server sharing the sim assembly. (Section 3.)
2. Summons are server-authoritative from MVP. M0 uses a local implementation of the same algorithm. (Section 4.)
3. Offline cap is 24 h, not the sibling's 8 h. (Section 6.2.)
4. Boss timer is 30 s. (Section 6.1.)
5. Owned-but-unequipped weapons give a passive bonus. This is the reference game's key collection hook and the reason the weapon banner is the cheapest. (Section 6.3.)
6. Fish on the board contribute passive DPS, so the board is never purely cosmetic while idle. (Section 6.4.)
7. Diamonds are the only purchasable currency. (Section 5.2.)
8. Day rollover is 00:00 UTC for everyone in MVP. Per-region rollover is a P1 question for 06/08.

### 12.1 Decisions made while reconciling specs 01-10

These were open or contradictory across the writer-produced specs and are now settled. Each spec named was patched or is flagged; if you find a stale reference, this section wins.

9. **Curve constants re-tuned** (03 section 8): `HP_G` 1.07 -> 1.04, `GOLD_G` 1.05 -> 1.03, `EXP_G` 1.04 -> 1.025, Hero Level becomes compounding `HERO_LVL_MULT = 1.02`. Day-stage targets are now 240/330/430/480/550. All four re-tagged S until `tools/power_curve_model.py` reproduces the table. 09 and 10 read targets from the `progress_targets` content table, so adopting a different set is a content PR.
10. **The hero cannot die.** `HP`/`DEF` stay in the `Stat` enum, are not trainable in MVP, and get a Tower-survival job in P1 (03 section 4).
11. **Awakening gates slots.** Rank 1 unlocks `SCR_RESEARCH` and companion slot 2; ranks 2-3 unlock companion slots 3-4; ranks 4-5 unlock rune slots (03 section 5.3).
12. **Offline cap is 24 h base, extendable to 36 h** through `RES_OFFLINE_CAP` (6 levels, +2 h each). Decision 3 means the base, not a hard ceiling.
13. **Limit breaks** (5 ranks, Rock Candy, flat +1 effective dupe level) apply to weapons, skills **and** companions. Copies beyond `DUPE_LVL_CUM[10] = 160` convert to Rock Candy, not shards (04 sections 5.2-5.3).
14. **Summon RNG streams stay in schema v1 for every mode**; Remote mode ignores their counters. 03 section 10's note that they are "removed by the MVP migration" is superseded. No migration removes them.
15. **Summon tickets are a server ledger from MVP** (authority table, section 4). 06's per-day ticket income (1 weapon ticket from the daily gift; about 0.15 skill and 0.15 companion tickets per day from weeklies and achievements) is the figure 04's banner-level model must use; 04's bot assumed W 2 / S 1 / C 1 and needs a re-sync.
16. **Dungeon tickets are debited on clear only.** Failed and abandoned runs cost nothing; `SCR_DUNGEONS` previews clearability with the deterministic sim. 02's recovery rule P16 was patched to drop the refund.
17. **Quests are three-state** (`IN_PROGRESS` / `COMPLETED` / `CLAIMED`) with an explicit claim; unclaimed daily rewards are forfeited at rollover. Quest progress is keyed by string quest id, never positionally (02, 06, 09).
18. **Diamond budget.** `DIAMONDS_PER_DAY_F2P = 400` is met as a steady-state floor by counting 8 of 11 dailies plus weekly missions amortised at 50/day; late-game income tapers to about 384-407/day, which is inside tolerance. The daily free gift adds one weapon ticket (~100 diamonds of pull value) on top.
19. **Packages are diamonds on a delivery schedule only** (starter 600; weekly 300 + 100/day; monthly 1000 + 100/day). No ticket or collectible bundles, so decision 7 stays strict. Reopen only with soft-launch revenue data.
20. **Reason enum completed** (contract 6). `TUTORIAL` replaces 01's and 06's earlier use of `ADMIN` for the tutorial grant; both were patched.
21. **Week key** is `floor((dayKey - 4) / 7)`, Monday 00:00 UTC. 06 was patched from an off-by-one form.
22. **`MoveFish` is accepted** (MVP): cosmetic reposition of a fish to an empty cell, consumes no stream, emits `FishMoved`. 05 should add the command and its rejection reasons.
23. **Content versioning** is `YYYY.MM.DD[.N]`; hot-apply allowlist as in contract 3; `hero_levels` and `tank` are one-row rule tables; `progress_targets` is a content table (09).
24. **Enemy skins hash `(stage, killIndex)`** rather than consuming a sixth stream (09 section 5.6).
25. **Bot harness epoch** is a Monday 09:00 UTC with two 60-minute sessions per day, so both weekly keys and the UTC rollover are exercised in a 30-day run; gap lengths alternate so that on alternate days the evening session runs across 00:00 UTC and the in-session rollover is exercised too (amended by decision 61, issue #29); diamond income defaults to the sim's own quests, with a flat `DIAMONDS_PER_DAY_F2P` mode kept only for parity with `power_curve_model.py` (10).
26. **Plausibility checks use the Tap Zap upper bound** `DPS * (1 + TAP_ZAP_DMG_PCT * TAP_ZAP_MAX_PER_SEC)` for the stage-versus-time check, not the idle bot's DPS (08).
27. **Save files are excluded from OS backup**; cloud save (including guests) is the backup (02).
28. **`IDEMPOTENCY_TTL_H = 24`**; a pending summon older than that executes fresh rather than replaying (02, 08).
29. **Soft-launch markets**: Philippines and Canada, with a New Zealand technical launch (10). Belgium paid-summon handling is deferred to soft launch (08 open question 5).

Known stale spots left for the next pass rather than patched blind: 05 lacks `MoveFish` (decision 22); the Fish Coin Dungeon reward `50 * f` may overshoot the 25-50% income share band and `tools/fish_model.py` must decide between it and `50 * f^0.7` (06 open question).

### 12.2 Decisions made after the spec rewrite

30. **Code names stay `HeroCat.*`** (`HeroCat.Sim`, `HeroCat.Runtime`, `HeroCat.Bots`, `HeroCat.Server`, `HeroCat.Client`, `HeroCat.sln`). "Whisker Hero" is the working title of the product only. Issue #1.
31. **Unity 6 (`6000.6.7f1`)**, not 2022 LTS. Issue #2.
32. **No web prototype.** The WebAssembly playtest build (`web/`, `src/HeroCat.Web`, `play.sh`) is removed; the Unity client is the only client. Issue #3.
33. **Clean replacement of the old design.** The previous prototype's systems (hero party gacha, gear, class advances, gems, chest timer) are deleted, not migrated. Nothing has shipped, so the new `GameState` is schema v1 from scratch, old dev saves are not migrated, and golden files and acceptance targets are regenerated from the new bots. Infrastructure (RNG, clock, replay, state hasher, save store, bot harness, content build) is reused. Issue #4.
34. **Content directory and exporter names follow 09/10**: lowercase `content/` and `tools/export_content.py` with the verb CLI replace `Content/` and `tools/content_build.py`; the code rename lands with the exporter rewrite. Issue #1.
35. **UI stack is uGUI + TextMeshPro** on Unity 6 (decision 31). The existing UI Toolkit screens are old design and are rewritten. Issue #2.
36. **One spec 07.** The still-valid Unity setup from the old `07-client-unity-prototype.md` (version pin, Dev/Release build profiles, safe area, frame loop and sim bridge, Android/iOS build steps, device QA checklist) moved into `07-screens-and-ux.md`; the old file is deleted. Issue #3.
37. **Server time enters by tick-driven virtual clock.** The sim rolls the day on the tick that crosses midnight; Runtime issues a logged `ApplyServerTime` only at boot, resume, gap recovery and every `WALL_CLOCK_SYNC_SEC = 60` s (contract 7). Issue #5.
38. **No mutation outside `Apply`.** Offline catch-up is the system command `ComputeOfflineCatchup(nowServerSeconds)`; `ApplySummonResult` debits in both modes and, in Local mode, sets the summon stream counter carried in the payload, so `LocalSummonService` reads state but never writes it (contract 5). Issue #6.
39. **02 owns `GameState` names and shapes**; feature specs' newer semantics are adopted into it: `OwnedItem.LimitBreaks`, `Copies` capped at 160 with level derived, banner level derived from cumulative `BannerState.Exp`, three-state quest records whose progress counts past target, `ShopState`, `Stats.HeroHits`/`FishDiscards`/`FirstWallStage`, `BossPhase` (None/Pending/Walled) instead of `BossActive`, a persisted `Combat.MissileQueue`, `Wallet.Apply` rejecting overdrafts, `Wallet.SummonTicket` a server mirror in Remote mode, equipped items in `LoadoutState`, new-item badges outside sim state. Issue #7.
40. **RNG draw rules and invariants are 03's**: `combat` reserved (`Counter == 0`), `crit` one per hero hit (`== Stats.HeroHits`), `loot` one per boss kill (`== Stats.BossKills`) (contract 2). Issue #8.
41. **Seeds derive from an immutable `RngRoot`** in `GameState` (SHA-256 in Runtime), never from `PlayerId`/`DeviceId` at load time; migrations that add a stream derive its seed from `RngRoot` inside the step. Issue #9.
42. **Rated vs realised damage are split.** Section 7's `DPS` is the rated value; hero hits exclude the skill, companion and missile layers, which deal their own multiples of the hit (03 section 7.1). Issue #10.
43. **00's command and event names win, extended** with the names owner specs define (section 5.1); rejects use 01's `RejectCode` plus a closed per-spec reason string; malformed input is rejected with `BadArgument`, never thrown; ids `res_*`, constant `TRAIN_CAP`; `RetreatFromBoss` is out of MVP. Issue #11.
44. **Layer-share gate**: 40% ceiling on days 1, 7 and 30; 5% floor on days 7 and 30 with per-tier exemptions as data in `acceptance-targets.json` reported as `WARN` (section 7). Issue #12.
45. **01's `SummonResultPayload` is the summon contract**, extended per pull with `CopiesAfter` and `OverflowRockCandy`; `ApplySummonResult(payload)` is the only mutator; `LocalSummonService` rolls on cloned state and checks the wallet first; Remote mode takes the server's balances; event `BannerLeveledUp`; one merged `summon` analytics schema. Issue #13.
46. **Summon vectors**: `gacha_model.py --vectors` owns the scripted-`u` vectors in `Fixtures/Summon/`; the C# stream-sequence fixture stays as a determinism golden that Python's SplitMix64 must reproduce. Issue #14.
47. **`missileDpsPct` is rated-only** and never enters the live hit; any live display average is runtime-only, outside the save and `StateHash`. Issue #15.
48. **Fish Coin merge payouts are unrounded `double`s** (05's values); 09 stops rounding them. Issue #16.
49. **Jammed board**: a full board is jammed iff every level below the cap appears at most once; cap fish still do not merge (05 open question 7 default) and `DiscardFish` stays M0. Issue #17.
50. **Fish Board gestures**: 05 owns behaviour, commands and events (discard entry, `UI_LONG_PRESS_MS`); 07 owns presentation; the UI constants live in 07's constants list. Issue #18.
51. **Spec 05 errata applied** (and the 00 formula units and `FISH_COIN` sources). Issue #19.
52. **Login diamonds are auto-granted** by `EnsureDay` with reason `LOGIN`; `d_login` pays nothing and only counts toward bonus thresholds, keeping the 400/day budget. Issue #20.
53. **Offline catch-up details**: ticks pause behind `SCR_OFFLINE_REWARD`; the tank count uses 05's `CatchUpTank` over credited ticks at compute and applies at claim; a second gap sums into the pending record with the latest stage shown; the effective cap includes `RES_OFFLINE_CAP` (contract 7). Issue #21.
54. **Number formatting**: 3 significant digits on the suffix ladder, rounded before the suffix is chosen; integer counts below 1e6 in full (contract 9). Issue #22.
55. **07 acceptance criteria run engine-free first**: screen logic lives in the client's `Core/` and is tested in `HeroCat.Runtime.Tests`; only rect, prefab and profiler checks wait for a Unity CI job. 07 marks each AC engine-free or Unity-only. Issue #23.
56. **One summon pull algorithm, 04's**: forced 10th slot is Epic only after a failed normal L+ check; `pity` = pulls since last L+ before this pull, `n = pity + 1`; banner level evaluated per pull; one vectors file `Fixtures/Summon/vectors.json` (section 6.3). Issue #24.
57. **`IDungeonSeedService` is the seventh server-sensitive seam** (contract 11). Issue #25.
58. **`TICKET_SUMMON_RUNE` is in the `Currency` enum from schema v1**, appended after `GUILD_COIN` and unused until P1 (section 5.2); rune `ATK_PCT` maps to `Stat.ATK`. Issue #26.
59. **CI wraps `check.sh`**: `./check.sh` gains `--pr`, `--content` and `--nightly` modes (10 adds `--server` for the server-test job) and the workflows are thin path-filtered wrappers, so a green `./check.sh` still means green CI (section 3). Issue #27.
60. **`StateHash` stays SHA-256**; the golden command log is JSONL in 01's command-log format (contract 12). Issue #28.
61. **Bot sessions sometimes span the rollover**: gap lengths alternate so the evening session runs across 00:00 UTC on alternate days, keeping 120 active minutes in two sessions (amends decision 25; 10 section 3.3). Issue #29.
62. **Error classes are decided by `(status, code)`**: 08's wire codes are canonical, one table in 01 section 5.2 drives the classifier and the localization validator (keys `err.<CODE>`), an unknown code falls back to the status rule, and 01's class-to-presentation mapping holds with the non-blocking card in the action-strip position (section 5.3). Issue #30.
63. **Content rollout buckets are server-side**: `GET /config` buckets by JWT, no JWT gets the non-rollout default, the client re-fetches config once after boot auth and has no bucket hash (contract 3). Issue #31.
64. **GDPR hard delete tombstones financial rows**: the job inserts a tombstone `accounts` row (UUID from the first 16 bytes of `sha256(player_id || pepper)`, `status = 'deleted'`, PII blanked), re-points the purchaser's `purchases`/`summon_log`/`diamond_ledger` rows to it, then deletes the real account; the three financial FKs are `on delete restrict`; non-purchaser `summon_log` is deleted (02 section 13). Issue #32.
65. **Server schema is plain versioned SQL migrations**, applied by a small runner (no EF Core migrations; EF Core for queries only is allowed), checked by a Postgres-guarded `check.sh` job (section 3). Issue #33.
66. **08 owns the cloud-save wire**: `If-Match: *` for a first upload, metadata in `X-Save-Meta`, body the raw save, 08's routes, same-device lower `saveSeq` rejected `409 SAVE_STALE`; 02 section 12 adds `granted_items`, `ticket_ledger`, P1 `coupons`/`coupon_redemptions` and the `events` DDL. Issue #34.
67. **Stage select farms a side stage**: `Combat.Stage` stays the frontier, `SelectStage(s)` farms `s <= HighestCleared` (02's meaning: any stage completed) without advancing anything, `SelectStage(null)` returns, first clears key off `HighestFirstClearBoss`, and the list's boss challenge is the frontier boss only (section 5.1). Issue #35.
68. **Day-20 wall and floor misses are fixed with the tool**: levers come from 03 section 9 using `tools/power_curve_model.py` output (pending; candidates `RES_ATK` 0.08, limit-break repricing), 03 AC18 gains a day-14-to-30 wall bound, and speed is permanently exempt from the 5% floor (sections 6.6, 7). Issue #36.
69. **Limit-break names**: reason `LIMIT_BREAK`, constant `LIMIT_BREAK_RANKS` (in `REQUIRED_CONSTANTS`), per-rarity columns `rarities.limit_break_cost` and `rarities.dupe_overflow_rock_candy` (section 6.5). Issue #37.
70. **Banner-level cadence comes from the tool**: per-banner F2P pulls are computed from 06's income and 03's bot policy, and `BANNER_LVL_EXP` is retuned so weapon banner level 4 lands in days 20-35; hand-entered pulls-per-day figures are removed. Applied: the placeholder thresholds scaled by 0.18 (rounded to 5) give 0, 10, 25, 55, 90, 145, 215, 305, 415, 540 and a 20-seed median weapon level-4 day of 27 (section 6.3; supersedes decision 15's re-sync note). Issue #38.
71. **`mpSufficiency` counts `MP_REGEN` companions**: the net MP they restore counts as regen in the rated formula (03 adopts 04's form); 04 criterion 11 is corrected to about 15.6 +/- 1 casts per minute for the other three (section 7). Issue #39.
72. **The Fish Board is hidden and frozen during dungeon runs**: passive board DPS only, the tank keeps refilling, board commands are rejected `Locked` / `BOARD_LOCKED` while `Combat.Mode == Dungeon` (section 5.1). Issue #40.
73. **Fish above a lowered level cap are kept**: they keep their board DPS and cannot merge; loads clamp fish only to the `fish_levels` row count, and the tank stock clamp to `TankCap` stays (section 6.4). Issue #41.
74. **Dungeon runs are ticked in the main loop**: `StartDungeon` validates and sets `Combat.Mode = Dungeon`, the fight draws from a temporary `dungeon_run` stream, `DungeonCleared` debits one ticket and grants the reward, failed and abandoned runs cost nothing (decision 16), one boss per floor, `PreviewDungeon` runs the same fight headlessly on a clone, 06 section 6.2's reject reasons apply, and `StartDungeon` is rejected during a boss attempt (01). Issue #42.
75. **Awakening cadence is "days after gate"**: content table `awakening_targets (rank, max_days_after_gate)`, every rank `max_days_after_gate = 2`, read by the tools and the bots; 03 and 06 acceptance criteria read it (section 6.5). Issue #43.
76. **`OWN_*` achievement top rungs stay below the pool size** (for example 25/35/45), and 09 rejects any `OWN_*` target not strictly below the current pool size of that kind (pillar 4). Issue #44.
77. **One `feature_unlocks` content table** gates features for the sim and the client: Research at Awakening rank 1, Companion banner open, Quests at stage 10, Dungeons at stage 30, Shop never locked (section 5.3). Issue #45.
78. **Tutorial model**: the save holds `GameState.Tutorial.Step` (int) advanced by `SetTutorialStep`; `TUTORIAL_DONE` is the `tutorial_steps` row count; steps wait on `expect_event` x `expect_count`; ticks keep running except on step 1; the welcome step grants the starter weapon `TUTORIAL_WEAPON_ID` with `TUTORIAL_DIAMONDS`. Issue #46.
79. **Client prefs live in Runtime `ClientPrefs`**: coach marks, sort/filter/step prefs and toggles (and the NEW badges already moved client-side) are a local JSON store next to the save, not cloud-synced and not in `GameState` (section 5.1). Issue #47.
80. **Fish art is a fixed MVP set**: 12 sprites plus tinted variants and a crown overlay, the merge sound clamps at step 12, and the validator warns when content can exceed the sprite count (07 section 15). Issue #48.
81. **Client-earned diamonds and tickets are credited on upload**: `PUT /save` carries cumulative per-reason earned counters, the server credits the delta into `diamond_ledger` / `ticket_ledger` (`ref_type = save_reconcile`) clamped to the per-day allowance before reconciling, and the client uploads before a pull when it has unsynced earnings (section 4). Issue #49.
82. **First-purchase double is per SKU, diamond packs only**: packages are never doubled; "used" is derived from `purchases` rows with `status` in (`granted`, `refunded`, `chargeback`) for that SKU, so a refund does not re-arm the double, and `/shop/catalog` returns a per-SKU map. Issue #50.
83. **Package portions are materialised at purchase**: one inbox row per portion with `inbox_items.available_at`; `GET /inbox` hides future rows except the next future portion of each package, a refund expires undelivered ones, and the client shows that one future portion greyed (`INBOX_NOT_AVAILABLE` 409 if claimed early). Issue #51.
84. **Dungeon results are validated by correlation only in MVP**: the save keeps recent runs that the server matches to issued seeds, flagging clears without a seed, bounded by the soft-currency checks; `dungeon_replay` is a P1 follow-up if soft-launch data shows abuse (section 4). Issue #52.
85. **02 section 12 lists every server table and column 08 relies on**, so the GDPR delete job and 02 AC 16 cover them all. Issue #53.
86. **The hot-apply allowlist is the four tables of contract 3**; the exporter-computed manifest `hot_apply` bool is the single source, 08's `hotApplyTables` echoes it, and a Sim test proves no sim path reads `shop_packages` or `inbox_templates`. Issue #54.
87. **Quest rows are the single source of quest diamonds**: the duplicate quest-diamond constants and `QUEST_DAILY_BONUS_5`/`_8` are dropped, 09 checks the bonus rows exist, and `E039` recomputes 06's reference day from the rows plus `REF_DAILIES_DONE = 8` and `REF_NEW_BOSSES = 4` within 10% (section 6.2). Issues #55, #244.
88. **Content reconcile on load is 02's validator**: one pass with the content-dependent rules, no refund in MVP, and `ContentHash` saved next to `ContentVersion` (section 5.1, contract 3). Issue #56.
89. **Soft launch ships `en` only**; `ja` stays fully translated in the pipeline with `ship_locales = en,ja` in CI fixtures, and the exporter maps `zh-Hans` <-> `zh_hans` as a fixed rule (section 11). Issue #57.
90. **The light spender spends $20**: `pkg_monthly` on day 1 and `dia_3` (first-purchase double) on day 2, no `dia_2` buys (10 section 3.4). Issue #58.
91. **10 owns the alarm and runbook catalog** with 08's `RB-*` ids, 08's extra alarms merged in and a Wilson-interval odds rule per banner level replacing both fixed bands; 08 section 9.5 becomes a pointer. Issue #59.
92. **`STAGE_VS_TIME` reads a generated content table** `plausibility_stage_bound (day, stage)`, the p95 over 20 whale seeds for 60 days from the bot CLI's `stage-bound` verb, validated by the exporter, shipped in the bundle and regenerated with any sim-affecting content change. Issue #60.
93. **Netherlands is listed as default EU**; Belgium is not listed for soft launch and is decided for global launch (10 section 7). Issue #61.
94. **Companion damage scales with the skill layer**: companions deal multiples of `CompanionUnit = SkillUnit * (1 + skillDpsPct)`, keeping the rated formula multiplicative and the 1% rated-vs-measured test exact; may be revisited when `tools/power_curve_model.py` reports layer shares (section 7). Issue #243.
95. **Insufficient-balance rejects are `NOT_ENOUGH_<CURRENCY>` everywhere**, tickets included (`NOT_ENOUGH_TICKET_SUMMON_WEAPON`, `NOT_ENOUGH_TICKET_EXP`), replacing `NOT_ENOUGH_TICKETS` and `NO_TICKETS` (section 5.1); items 1 and 2 of the issue are decision 87. Issue #244.
96. **Analytics schemas are owned by the feature specs**: each event's fields are defined once, in its owner's analytics section (03 section 11 combat and progression, 04 section 13 summons and collection, 05 section 13 fish, 06 section 13 economy, quests, dungeons, offline, shop and IAP; 08 section 7.4 `plausibility_flag`); 02 section 14 keeps the common envelope, the offline queue, the session, save-health and clock events, and an index of every event name with its owner. `CURRENCY_AGG_SEC` moves to 06. Issue #245.
97. **Fish Board ceiling and factor**: a 20-cell board holds at most 20 - cap + 1 cap fish plus one of each lower level; FISH_BOARD_DPS_FACTOR is 0.77 (S) so that reachable board gives the intended ~x20 multiplier. Issue #247.
98. **`tutorial_steps.expect_event` may list several events** separated by `|`, any of which counts toward `expect_count`; the exporter checks every name against the event vocabulary (`E021`), and step 12 waits on `BossCleared|BossFailed` (09 section 5.29, 07 section 14). Issue #246 item 1.
99. **A new profile starts with a full Magic Tank** (`TankCount = TankCap`, `TANK_CAP0` at Research 0) so the first session can merge at once, and with MP 0 (02 section 3.1, 05 section 3). Issue #246 item 2.
100. **The welcome grant reaches analytics from `ProfileFactory`**: no `Simulation` exists when it is applied, so Runtime sends its `currency_changed` rows with `reason = TUTORIAL` itself (06 section 13). Issue #246 item 4.
101. **`CLOCK_BACKWARDS_TOLERANCE_S = 5` (S)**, a content constant: `ClockWentBackwards` and `clock_anomaly{backwards}` fire only when server time is behind the virtual clock (or behind `LastTickServerTime` at resume) by more than it; less is resync jitter (01 section 3.2, 06 section 4.2). Issue #246 item 5.
102. **Error, reject and notice localization keys are lowercase**: `err.<code_lower>.title` / `.body`, `reject.<reason_lower>` (a reasonless reject uses the snake-cased `RejectCode`), `notice.<code_lower>.title` / `.body`; wire codes and reasons stay UPPER_SNAKE and the client lowercases them when it builds the key, so 09's lowercase key pattern holds. This replaces decision 62's `err.<CODE>` spelling; English rows exist for every code and reason the client can show (01 section 7, AC 14). Issue #248 item 1.
103. **No readable save copy is a new profile, not a fatal error**: 02 section 8.3 wins; the copies are quarantined, a new profile starts, analytics gets `save_corrupt_all{supportId}` and `SCR_MAIN` shows a one-time notice with the support id; 01's Fatal `SAVE_CORRUPT_ALL` class and table row are removed. Issue #248 item 2.
