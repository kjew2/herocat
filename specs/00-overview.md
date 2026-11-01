# 00 - Overview, Scope and Shared Contracts

Working title: **Whisker Hero**. Genre reference: *Cat Hero: Idle RPG* (Gameduo). Secondary references: Sword x Staff, Maple Idle.

This document is the contract every other spec in `specs/` builds on. If another spec disagrees with a name, number or rule here, this file wins and the other spec is wrong. Fix the other spec.

Sibling project: `/local/home/kjew/idle-rng-game/specs/` is an earlier, narrower prototype of the same genre. Its validated constants and its patterns for RNG streams, save migrations and bot harnesses are reused here where the structure matches. Names differ; the names in this file are the ones to use.

## 1. Product pitch and design pillars

A cat hero auto-fights its way through the Owner's Dreamland on the top half of a portrait phone screen. The bottom half is a fish-merge board the player works with their thumb. Everything the player earns feeds a dozen parallel upgrade systems, and the whole game is deciding where to spend next.

Pillars, in priority order. When two pillars conflict, the higher one wins.

1. **Always something to do, never something you must do.** The sim progresses without input. Active play (fish merging, tapping enemies, spending) accelerates it. A player who only logs in to claim offline rewards and spend still progresses.
2. **Many parallel sinks, each with its own RNG texture.** Training is smooth, summons are spiky, fish merging is tactile, dungeons are scheduled. No single system should be more than ~40% of the power curve so a bad-luck streak in one is offset by the others.
3. **Deterministic, data-driven core.** Every number lives in a content table. The simulation is a pure library that runs identically on device, on the server and in headless bots. Balance is tuned from a spreadsheet and verified by bots before any human sees it.
4. **Honest monetization.** Published odds, hard pity, no pay-to-skip-pity, no loot that is strictly required. Free players reach every system; paying players reach it sooner. Achievements and collection rewards pay out for a *count* of items owned, never for completing a named set, so no reward is a complete-gacha under Japan's rules (08 section on compliance).
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
| Client engine | **Unity 2022 LTS**, C#, URP 2D, uGUI + TextMeshPro, Addressables, Unity 2D Animation (Spine if budget allows) | Reference game is Unity; 2D chibi animation, IAP, ads and analytics SDKs are all first-class. Godot 4 C# was considered: fine engine, but mobile IAP/ads SDK maturity is behind. Flutter + Flame rejected: weak for skeletal animation and particle-heavy combat. |
| Simulation core | **`Whisker.Sim`**, pure C# netstandard2.1, no `UnityEngine`, no IO, no clock, no JSON, no reflection | One implementation runs on device, in server validation and in bots. Determinism across x64/ARM64/IL2CPP requires no `Math.Pow/Exp/Log` on the hot path (repeated multiplication only), no floating-point reductions in unspecified order, no `DateTime.Now`. |
| Runtime glue | **`Whisker.Runtime`**, plain C#, references Sim + Newtonsoft.Json | Save serialization, migrations, clock, local store, HTTP clients, content loading. Shared by client and bots. No `UnityEngine`. |
| Backend | **ASP.NET Core 8** stateless API, **PostgreSQL** (accounts, saves, purchases, guilds, mail), **Redis** (leaderboards, rate limits, sessions), **S3-compatible object store** (save blobs, content bundles), CDN in front of content | C# so the server references `Whisker.Sim` for re-simulation and rate tables, no second implementation. Nakama/PlayFab were considered as a lighter path for a solo dev; rejected because server-authoritative summons with our own pity rules and save plausibility checks need custom code either way, and BaaS pricing scales badly with DAU. If the team is one person at M0, the backend is skipped entirely (local seams, see 01). |
| Content | Google Sheet -> JSON tables -> versioned content bundle served via CDN, fallback copy shipped in the app | Designers edit the sheet. Exporter validates and emits JSON. Spec 09. |
| Analytics | Event pipeline to a warehouse (Firebase Analytics for MVP, own events table on the backend for economy events) | Economy events need joins with purchases and saves, which third-party analytics cannot do. |
| CI | GitHub Actions: Sim + Runtime tests on every PR, bot acceptance table on every content change, Unity build via self-hosted runner or Unity Cloud Build | Spec 10. |

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
|  Whisker.Runtime   SessionRunner, SaveSerializer, Migrations,  |
|                    LocalSaveStore, CloudSaveClient, ServerClock,|
|                    SummonClient, ContentLoader, Analytics       |
+------------------------------+--------------------------------+
                               |
+------------------------------v--------------------------------+
|  Whisker.Sim       GameState, Simulation.Tick(), Commands,     |
|                    Events, RNG streams, Catch-up, ContentDb    |
+---------------------------------------------------------------+
            ^                                        ^
            | same assembly                          | same assembly
+-----------+-----------+              +-------------+-------------+
|  Whisker.Bots         |              |  Whisker.Server (ASP.NET) |
|  headless harness     |              |  Auth, Save, Summon, IAP, |
|                       |              |  Guild, Config, Mail      |
+-----------------------+              +---------------------------+
```

Authority split, which every spec must respect:

| Concern | Authority | Notes |
|---|---|---|
| Combat, stage progress, training, fish board, offline catch-up | **Client sim** | Deterministic; server can re-simulate from a prior save + command log for spot checks. |
| Summon results | **Server** (MVP+), local `ISummonService` in M0 | Server rolls, logs, returns results; client never holds a summon RNG seed in MVP. |
| Summon ticket balances | **Server** ledger (MVP+) | Needed so the summon endpoint can reject `PAYMENT_MISMATCH` (08 section 4.3). M0 holds them in the local `Wallet`. |
| Wall-clock time | **Server** | Client stores last trusted server time and monotonic offset; see 02 and 06. |
| Diamond grants from IAP, mail, coupons | **Server** | Receipt validated server-side; grant lands in Inbox; client claims. |
| Save of record | **Server** | Client is a cache. Conflict rules in 02. |
| Guild, leaderboards, events schedule, remote config | **Server** | |
| Dungeon rewards | **Client sim**, server plausibility-checked | Dungeon fights are deterministic sim runs seeded by server-issued seed (MVP). Tickets are debited on clear only; a failed or abandoned run costs nothing (06 section 6). |

Command/event discipline: the client never mutates `GameState`. It sends a `Command` into `Simulation`, which validates, mutates, and emits `Event`s. View-models subscribe to events. The same commands are what the bot harness and the server re-simulator feed in.

## 5. Canonical vocabulary and identifiers

Use these exact names in code, tables, specs and analytics. Singular for types, plural for collections.

### 5.1 Entities

| Name | Meaning |
|---|---|
| `Profile` | The account-level record: `playerId`, display name, created time, linked platforms. Not part of sim state. |
| `GameState` | Save root. Everything the sim reads and writes. |
| `Hero` | The player's single cat: level, EXP, awakening rank, equipped weapon id, equipped skill ids, equipped companion ids, equipped rune ids. |
| `Stat` | One of `ATK`, `HP`, `DEF`, `CRIT_RATE`, `CRIT_DMG`, `ATK_SPD`, `GOLD_GAIN`, `EXP_GAIN`. `Training` levels are per stat. |
| `Stage` | Integer `s >= 1`. The player farms exactly one stage at a time. Chapter = `floor((s-1)/CHAPTER_WIDTH)+1`, cosmetic only. |
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
| `Dungeon` | One of `FISH_COIN`, `ROCK_CANDY`, `EXP`. Ticketed, laddered, sweepable. |
| `Tower` (P1) | Fixed ladder of boss fights with one-time rewards. |
| `Quest` | A tracked objective with a reward. Kinds: `DAILY`, `WEEKLY`, `ACHIEVEMENT`. |
| `Wallet` | All currency balances. Every change goes through `Wallet.Apply(CurrencyDelta, Reason)`. |
| `Inbox` | Server-granted items awaiting claim (IAP, mail, coupons, compensation). |
| `Command` | A player or system intent fed to `Simulation`. Named `VerbNoun`. The full registry is the union of each owning spec's command table (01 defines the base type); this list is the canonical vocabulary. Combat/progression (03): `TrainStat(stat, count)`, `TrainStatMax`, `AttemptBoss`, `TapZap(count)`, `Awaken`, `Research(nodeId)`, `SelectStage`. Collections (04): `Summon(banner, count, payment, requestId)`, `EquipWeapon`, `EquipSkill`, `EquipCompanion`, `LimitBreak`, `AutoEquipBest*`, P1 `CastSkill`. Fish (05): `ReleaseFish`, `ReleaseMax`, `MergeFish`, `MergeAll`, `MoveFish`, `DiscardFish`, P1 `SetAutoMerge`. Economy (06): `ClaimOfflineReward`, `StartDungeon`, `SweepDungeon`, `ClaimQuest`, `ClaimAllQuests`, `ClaimDailyGift`, `ExchangeItem`, `ClaimInbox`. |
| `Event` | A fact emitted by the sim. Named `NounPastTense`. Combat: `HitLanded`, `EnemyKilled`, `StageCleared`, `StageFirstCleared`, `ChapterEntered`, `BossFailed`, `SkillCast`, `CompanionAbilityUsed`, `FishMissileHit`, `TapZapped`, `LevelUp`, `Awakened`, `ResearchLeveled`. Collections: `ItemAcquired`, `ItemEquipped`, `ItemLimitBroken`. Fish: `FishReleased`, `FishMerged`, `FishMoved`, `FishDiscarded`, `FishDiscovered`, `TankFishReady`. Economy: `CurrencyChanged`, `QuestCompleted`, `DayRolledOver`, `DungeonCleared`, `DungeonFailed`, `DungeonAbandoned`. |

### 5.2 Currencies

| Code | Display | Main source | Main sink | Soft/hard |
|---|---|---|---|---|
| `GOLD` | Gold | Kills, offline catch-up | Training | Soft |
| `DIAMOND` | Diamonds | Quests, first clears, IAP, login | Summons, shop | Hard |
| `FISH_COIN` | Fish Coins | Fish discovery, Fish Coin Dungeon | Research | Soft |
| `ROCK_CANDY` | Rock Candy | Rock Candy Dungeon, bosses, excess collectible copies past level 10 (04 section 5.3) | Awakening; limit breaks on weapons, skills and companions | Soft |
| `HERO_EXP` | EXP | Kills, EXP Dungeon | Hero Level (auto) | Soft, never spent manually |
| `TICKET_FISH_COIN`, `TICKET_ROCK_CANDY`, `TICKET_EXP` | Dungeon tickets | Daily reset, shop | Dungeon entry | Soft |
| `TICKET_SUMMON_WEAPON`, `TICKET_SUMMON_SKILL`, `TICKET_SUMMON_COMPANION` | Summon tickets | Quests, events, shop | One pull each | Soft |
| `GUILD_COIN` (P1) | Guild Coins | Guild activity | Guild shop | Soft |

Diamonds are the only currency purchasable with money. Nothing else is ever sold for money directly.

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
| `SCR_STAGE_SELECT` | Modal. Chapter list, jump back to a cleared stage, boss challenge | MVP |
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
| `SCR_TUTORIAL` | Overlay on `SCR_MAIN` | MVP |
| `SCR_CONFIRM` | Modal. Generic confirmation with cost and balance-after line (Awakening, IAP, delete account, exit) | M0 |
| `SCR_ERROR` | Modal or inline card driven by 01's error taxonomy (boot failure, maintenance, network loss, summon timeout) | M0 |

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
| `DIAMONDS_PER_DAY_F2P` | 400 | S | Target free diamond income from dailies + first clears + login. Tuning target. |
| `PULL_COST[WEAPON/SKILL/COMPANION]` | 100 / 200 / 300 | S | Per single pull. 10-pull = 10x, no discount; discounting is done through banner level instead. Matches the reference game's pricing. |
| `DAY_SECONDS` | 86400 | I | Day key = `floor(serverSeconds / DAY_SECONDS)`. Rollover 00:00 UTC. |
| `OFFLINE_RATE` | 0.5 | I | Offline gold/EXP per second as a fraction of in-app kill rate at the current stage. |
| `OFFLINE_CAP_H` | 24 | S | Reference game caps at 24 h. Sibling used 8 h; 24 h is friendlier and is what players expect in this sub-genre. |
| `OFFLINE_MIN_S` | 60 | S | Gaps shorter than this credit nothing and show no modal. |
| `TUTORIAL_DIAMONDS` | 1500 | S | One-time grant at profile creation. |
| `LOGIN_DIAMONDS` | 50 | I | Once per day key. |

### 6.3 Summons

| Name | Value | Kind | Notes |
|---|---|---|---|
| Base rates | C .60 / R .28 / E .09 / L .025 / M .005 | I | Banner level 1. Sum 1.0. Higher banner levels shift weight from C to R/E (04). |
| `PITY_SOFT_START` | 50 | I | Pulls 1-50 since last L+ use base rate. |
| `PITY_SOFT_STEP` | 0.0325 | I | L+ rate on pull `n` = `min(1, 0.03 + 0.0325*(n-50))`. |
| `PITY_HARD` | 80 | I | Forced L+. P99 is 62, so this is a published guarantee, not a design lever. |
| `M_SHARE_OF_LPLUS` | 1/6 | I | |
| 10-pull guarantee | at least one E+ | I | Slot 10 rerolls from the E+ sub-distribution if slots 1-9 are all below Epic. |
| `BANNER_EXP_PER_PULL` | 1 | S | Banner level thresholds in 04. |
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
| `TANK_CAP0` | 10 | S | Magic Tank capacity at Research 0. |
| `TANK_REFILL_S0` | 6 | S | Seconds per fish at Research 0. |
| `FISH_MAX_LVL0` | 10 | S | Highest fish level reachable before Research raises the cap. |
| `FISH_DISCOVERY_COINS[n]` | `10 * 2^(n-1)` | S | Fish Coins the first time a level-`n` fish is created. |
| `FISH_MERGE_COINS_PCT` | 0.02 | S | Each merge pays 2% of that level's discovery value. |
| `FISH_MISSILE_PCT[n]` | `5 * 1.5^(n-1)` | S | Damage of a fish missile fired when a level-`n` fish is created, as % of one hero hit. |
| `FISH_BOARD_DPS_FACTOR` | 0.5 | S | Named so 09 can hold it as a `constants` row. |
| `FISH_BOARD_DPS_PCT` | `sum over board of FISH_BOARD_DPS_FACTOR * FISH_MISSILE_PCT[level]` | S | Passive DPS% contributed by fish sitting on the board. Design lever so the board matters when idle. |

### 6.5 Awakening and Hero Level

| Name | Value | Kind |
|---|---|---|
| `AWAKEN_STAGES` | 50, 150, 300, 500, 800 | I (first four) / S |
| `AWAKEN_MULT` | 1.5 per rank | I |
| `AWAKEN_COST_ROCK_CANDY` | 100, 300, 800, 2000, 5000 | S |
| Awakening unlocks | rank 1: `SCR_RESEARCH` + companion slot 2; rank 2: companion slot 3; rank 3: companion slot 4; ranks 4-5: rune slots (P1) | S |
| `HERO_LVL_EXP(l)` | `100 * 1.08^l` | S |
| `HERO_LVL_MULT` | 1.02 | S | Hero layer = `HERO_LVL_MULT ^ heroLevel`. Replaces the earlier linear `HERO_LVL_ATK_PCT = 0.01`; without one compounding stage-fed layer the frontier stalls under any curve (03 section 8). 09's `hero_levels` table carries both columns; only `mult_per_level` is populated. |
| `RES_OFFLINE_CAP` | +2 h per level, max 6 levels | S | Research node extending `OFFLINE_CAP_H` from 24 h to at most 36 h. |
| `LIMIT_BREAK_RANKS` | 5 | S | Rock Candy sink on weapons, skills and companions: each rank adds one effective dupe level. Costs by rarity in 04 section 5.2. |

### 6.6 Session model and targets

Assumed F2P player: 2 h active per day (the bot harness splits it into two 60-minute sessions so weekly keys and the 00:00 UTC rollover are both exercised; 10 section 4), offline the rest. Targets the bot harness must land within +/-20%:

| Day | Stage |
|---|---|
| 1 | 240 |
| 3 | 330 |
| 7 | 430 |
| 14 | 480 |
| 30 | 550 |

Days 14 and 30 match the sibling's targets by construction; days 1-7 are higher because `HP_G` is flatter and there is no gear-tier layer to wait for. These numbers come from 03's scratch model and are provisional until `tools/power_curve_model.py` reproduces them. They are also a content table (`progress_targets`, 09) so the bots read them from `ContentDb`. Known open balance problem: the scratch model still shows a ~460-active-minute wall around day 20 when the summon layers saturate; 03 section 11 lists the candidate fixes.

Worked numbers in 06 and 07 that were written against the earlier day-1 = stage 90 target are stale by roughly 2.5x on early-day stage references; the arithmetic in those sections is otherwise unaffected.

## 7. The power formula

```
DPS(s) = BASE_ATK
       * (1 + TRAIN_PER_LVL[ATK] * trainLvl[ATK])          # Training, smooth gold sink
       * HERO_LVL_MULT ^ heroLevel                          # Hero Level, compounding, fed by EXP
       * (1 + equippedWeaponPct + sum_owned weaponPassivePct) # Weapons, summon-driven
       * (1 + sum_equipped skillDpsPct)                      # Skills, burst and auto-cast
       * (1 + mpSufficiency * sum_equipped companionDpsPct)  # Companions, MP-gated (03 section 3)
       * (1 + FISH_BOARD_DPS_PCT + missileUptime)            # Fish Board, active layer
       * (1 + RES_ATK_pct)                                   # Research, direct DPS node (03 section 6)
       * (1 + runePct)                                       # Runes (P1)
       * AWAKEN_MULT ^ awakenRank                            # milestone layer
       * critFactor(CRIT_RATE, CRIT_DMG) * speedFactor(ATK_SPD)
```

Enemy HP grows `HP_G^s`. The budget for each layer's share of the residual is set in `03`. Rule of thumb from pillar 2: no layer above 40% of log-power at day 30, no layer below 5% so it still feels worth upgrading. 03's day-30 shares under the current constants: Hero 26%, Training 14%, Fish 15%, Weapons 10%, Skills 8%, Companions 7%, Awakening 6%, crit 6%, Research 4%, speed 3%. Research and speed miss the 5% floor; 03 section 8 lists the levers.

The hero cannot die. `HP` and `DEF` remain in the `Stat` enum but are not trainable in MVP; their P1 job is Tower survival (03 section 4).

## 8. Cross-spec contracts

1. **Sim is pure.** `Whisker.Sim` has no `UnityEngine`, IO, clock, JSON, reflection, `Math.Pow/Exp/Log`. Curves are evaluated by repeated multiplication from tables. Everything that needs IO lives in `Whisker.Runtime`.
2. **Named RNG streams.** Each consumer owns a stream `(seed, counter)` persisted in the save: `combat`, `crit` (one draw per hero hit), `fish_spawn` (one draw per successful release), `dungeon`, `loot` (one draw per boss kill), plus `summon_weapon`, `summon_skill`, `summon_companion` (three draws per pull). The summon streams are consumed only by `LocalSummonService` (M0 and bots); in Remote mode (MVP+) their counters are ignored, not removed, so schema v1 is one shape for every mode (02 section 3.4). Invariants: `loot.Counter == Stats.Kills`-derived, `fish_spawn.Counter == Stats.FishReleased`, Local-mode `summon_x.Counter == 3 * TotalPulls`. Enemy skins are chosen by hashing `(stage, killIndex)` and consume no stream (09 section 5.6).
3. **Content is data.** Every number in section 6 is a row in a table from spec 09. Code receives a `ContentDb` and never a literal. Content versions are `YYYY.MM.DD[.N]` with no major/minor semantics; breaking changes are expressed through `min_client_version` plus `schema_version` in the manifest. Content applies at the next cold boot, except the hot-apply allowlist `localization`, `shop_products`, `shop_packages`, `inbox_templates` (01 section on content hot update).
4. **Save schema is versioned from v1** with forward-only migrations and a fixture per version (02).
5. **Commands in, events out.** The client, bots and server re-simulator only talk to the sim through `Simulation.Apply(Command)` and `Simulation.Advance(ticks)`, and only observe it through `Event`s and read-only snapshots.
6. **Every currency change has a reason.** `Wallet.Apply(delta, reason)` where `reason` is from a closed enum: `KILL`, `BOSS`, `OFFLINE`, `LOGIN`, `TUTORIAL`, `FIRST_CLEAR`, `QUEST`, `ACHIEVEMENT`, `DAILY_GIFT`, `DAILY_RESET`, `SUMMON`, `TRAINING`, `RESEARCH`, `AWAKENING`, `LIMIT_BREAK`, `IAP`, `MAIL`, `DUNGEON`, `FISH_DISCOVERY`, `FISH_MERGE`, `SHOP_EXCHANGE`, `REFUND`, `ADMIN`. `ADMIN` is reserved for operator interventions; game grants always have their own code. The analytics event for every currency change carries it.
7. **Server time is the only trusted time.** `IClock` with `ServerClock` (MVP) and `FakeClock` (bots, tests). Day keys, offline gaps, dungeon resets and banner schedules all derive from it. `dayKey = floor(serverSeconds / DAY_SECONDS)`; `weekKey = floor((dayKey - 4) / 7)`, weeks start Monday 00:00 UTC. Clock going backwards grants nothing twice (02, 06). Offline catch-up is two-phase: `Catchup.Compute` stamps `LastTickServerTime` and stores `Offline.Pending` immediately, `ClaimOfflineReward` credits it; a second gap while a reward is pending sums into the same record, so the window is consumed at compute time, never at claim time.
8. **Screens bind to view-models, view-models subscribe to events.** No screen reads `GameState` directly and no screen polls per frame except the combat viewport and HUD counters, which read a per-tick snapshot.
9. **Curve-valued numbers are `double`** guarded by `STAGE_CAP`: `GOLD`, `FISH_COIN`, `Hero.Exp`, HP, damage. Counted currencies are `long`: `DIAMOND`, `ROCK_CANDY`, every ticket. `HERO_EXP` is `Hero.Exp`, not a `Wallet` balance, though it still flows through `Wallet.Apply` for the reason code. Display formatting uses the suffix ladder K, M, B, T, aa, ab, ac... (07). Never show more than 4 significant digits.
10. **All user-visible text is a localization key.** No literal strings in prefabs or code (07, 09).
11. **Server-sensitive operations go through an interface** with a local implementation for M0 and a documented remote contract: `IClock`, `ISummonService`, `ISaveStore`, `IInboxService`, `IReceiptValidator`, `IConfigService` (01, 08).
12. **The bot harness is the acceptance test** for any balance change (10). A content PR that moves the day-30 stage outside the tolerance band fails CI.
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
whisker-hero/
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
    power_curve_model.py               (to write) greedy F2P bot across all layers, prints the day table
    gacha_model.py                     (copy from idle-rng-game, rename streams) pity distribution
    fish_model.py                      (to write) tank/merge throughput, coin and DPS income per active minute
```

Planned source layout:

```
src/Whisker.Sim/            GameState, Simulation, Commands, Events, Rng, Economy, FishBoard, Catchup, ContentDb
src/Whisker.Runtime/        SaveSerializer, Migrations, LocalSaveStore, CloudSaveClient, ServerClock, FakeClock,
                            SummonClient, LocalSummonService, SessionRunner, ContentLoader, Analytics
src/Whisker.Sim.Tests/
src/Whisker.Runtime.Tests/
src/Whisker.Bots/           headless harness (spec 10)
src/Whisker.Server/         ASP.NET Core API (spec 08)
src/Whisker.Server.Tests/
unity/                      Unity project; Assets/Whisker.Client references Sim + Runtime as local packages
content/                    JSON tables exported from the sheet (spec 09)
tools/                      Python models and the sheet exporter
```

## 11. Milestones

| Milestone | Contents | Exit criterion |
|---|---|---|
| **M0 vertical slice** | Sim: combat, stages, training, hero level, weapon + skill summon (local), fish board, offline catch-up, local save. Client: `SCR_MAIN`, `SCR_HERO`, `SCR_WEAPONS`, `SCR_SKILLS`, `SCR_SUMMON`, `SCR_OFFLINE_REWARD`, `SCR_ITEM_DETAIL` with placeholder art. Bots print the day table. | Two weeks of simulated play within tolerance; one real person plays 20 minutes on device without being told what to do. |
| **MVP** | Everything tagged MVP in section 2.1. Backend: auth, cloud save, server summons, IAP, inbox, config. Analytics. Tutorial. Localization EN + one more. | Closed beta of 50 players, D7 retention measured, no save-loss reports. |
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
18. **Diamond budget.** `DIAMONDS_PER_DAY_F2P = 400` is met as a steady-state floor by counting 8 of 11 dailies plus weekly missions amortised at 50/day; late-game income tapers to about 384-414/day, which is inside tolerance. The daily free gift adds one weapon ticket (~100 diamonds of pull value) on top.
19. **Packages are diamonds on a delivery schedule only** (starter 600; weekly 300 + 100/day; monthly 1000 + 100/day). No ticket or collectible bundles, so decision 7 stays strict. Reopen only with soft-launch revenue data.
20. **Reason enum completed** (contract 6). `TUTORIAL` replaces 01's and 06's earlier use of `ADMIN` for the tutorial grant; both were patched.
21. **Week key** is `floor((dayKey - 4) / 7)`, Monday 00:00 UTC. 06 was patched from an off-by-one form.
22. **`MoveFish` is accepted** (MVP): cosmetic reposition of a fish to an empty cell, consumes no stream, emits `FishMoved`. 05 should add the command and its rejection reasons.
23. **Content versioning** is `YYYY.MM.DD[.N]`; hot-apply allowlist as in contract 3; `hero_levels` and `tank` are one-row rule tables; `progress_targets` is a content table (09).
24. **Enemy skins hash `(stage, killIndex)`** rather than consuming a sixth stream (09 section 5.6).
25. **Bot harness epoch** is a Monday 09:00 UTC with two 60-minute sessions per day, so both weekly keys and the UTC rollover are exercised in a 30-day run; diamond income defaults to the sim's own quests, with a flat `DIAMONDS_PER_DAY_F2P` mode kept only for parity with `power_curve_model.py` (10).
26. **Plausibility checks use the Tap Zap upper bound** `DPS * (1 + TAP_ZAP_DMG_PCT * TAP_ZAP_MAX_PER_SEC)` for the stage-versus-time check, not the idle bot's DPS (08).
27. **Save files are excluded from OS backup**; cloud save (including guests) is the backup (02).
28. **`IDEMPOTENCY_TTL_H = 24`**; a pending summon older than that executes fresh rather than replaying (02, 08).
29. **Soft-launch markets**: Philippines and Canada, with a New Zealand technical launch (10). Belgium paid-summon handling is deferred to soft launch (08 open question 5).

Known stale spots left for the next pass rather than patched blind: early-day stage numbers in 06 and 07 worked examples (written against day 1 = stage 90); 03 section 10's summon-stream migration sentence (decision 14); 04's per-day ticket assumption (decision 15); 05 lacks `MoveFish` (decision 22); the Fish Coin Dungeon reward `50 * f` may overshoot the 25-50% income share band and `tools/fish_model.py` must decide between it and `50 * f^0.7` (06 open question).
