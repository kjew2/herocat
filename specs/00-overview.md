# 00 - Overview, Scope and Shared Contracts

Working title: ***Hundred Floor Summoner*** (decision 127). Code names stay `HeroCat.*` until the rename in #351 (decision 30).

This document is the contract every other spec in `specs/` builds on. If another spec disagrees with a name, number or rule here, this file wins and the other spec is wrong. Fix the other spec.

> **Rewrite status (epic #353).** This file describes the new game. The project is being rewritten in place (decision 122), so specs 01-10 and the code still describe HeroCat until each one is replaced under its own issue (section 10). Until then:
>
> - For **what to build**, this file and `specs/11-source-divergences.md` win. A HeroCat rule in 01-10 that contradicts them is stale. Don't build on it; follow the rewrite issue instead.
> - For **how today's code behaves**, specs 01-10 still describe it. HeroCat citations such as "00 section 6.1" or "00 contract 2" point to the archived HeroCat overview, `specs/archive/00-overview-herocat.md`, and HeroCat citations of spec 03 ("03 section 8.6") point to `specs/archive/03-combat-and-progression-herocat.md`; `specs/03-heroes-and-growth.md` is the new game's spec. Decision numbers are shared by both files, and section 12 here gives each one's status.
> - Mechanics come from the in-story game of the *Pick Me Up!* webtoon, catalogued in `docs/research/pick-me-up-infinite-gacha.md` (cited here as "research §N"). We take mechanics only, never names, characters, story, art or text (decision 121). Every deliberate difference from that catalog is a row in `specs/11` (decision 129).

## 1. Product pitch and design pillars

The player is a newly licensed summoner. A tower of a hundred floors has appeared over the city, and the summoner calls heroes from former ordinary lives (farmers, smiths, soldiers, merchants) to climb it (decision 124). Heroes arrive at a star grade from 1★ to 7★, each with a former life that shapes what they are good at. The summoner builds parties of five, assigns them to floors and lets the fights run on their own. Back in the lobby, the summoner levels, promotes, equips and feeds heroes to each other, builds facilities and keeps the roster from breaking down. Heroes who die stay dead until a revive item brings them back.

Genre: idle gacha RPG with roster management. Mechanics reference: the in-story game of *Pick Me Up!* (research §2). No other game is a reference.

Pillars, in priority order. When two pillars conflict, the higher one wins.

1. **The tower is the spine.** Clearing floor 100 is the goal. Everything else unlocks by floor and feeds the climb: side content, facilities, account level and rankings (decision 123, research §11). A system that doesn't help the player climb, or doesn't unlock on the way up, needs a reason to exist.
2. **Fights run themselves; the game is in the lobby.** Combat is an idle auto-battle with no live input (DIV-06). The player's skill is in the choices made before a fight: who to summon, keep, feed, promote and equip, which five go together, and which floor they take on. Progress continues while the player is away. While online, the player changes the lobby (decision 123).
3. **Heroes are people, and losses are real.** Heroes have former lives, moods, stress, loyalty and relationships, and they can refuse orders. Death is semi-permanent: a dead hero is out of the roster until a revive item brings them back, never automatically (DIV-04). The source's dark rules stay in: stress can kill, synthesis and conversion consume the heroes fed in, and the manager can execute a hero who disobeys (decision 128). The content rating is 17+ / mature (decision 105).
4. **Deterministic, data-driven core.** Every number lives in a content table. The simulation is a pure library that runs identically on device, on the server and in headless bots. Balance is tuned from data and checked by bots before a person sees it. Floor variants are seeded and fair, and nothing is rigged per account (DIV-01).
5. **Honest monetization.** Every summon banner publishes its odds and has hard pity (DIV-01, DIV-02). No hero grade is paid-only: free players can reach every grade, and paying gets them there sooner (DIV-03). Gems buy advanced summons, revive items at a listed price and skipped waits. Research hours, annexes, skill books and replay recording cost gold or materials, never gems (DIV-05, decision 126). Achievements and collection rewards pay out for a *count* of things owned, never for completing a named set, so no reward is a complete-gacha under Japan's rules (decision 76).
6. **Live service from day one.** Content, rates, prices and schedules are server-delivered data with version gates, so a balance patch never needs a store release.

There is no "underdog" pillar. Whether a low grade can reach the top is a balance detail, not a design goal (decision 123).

## 2. Scope tiers

| Tag | Meaning |
|---|---|
| **M0** | Vertical slice. Proves the core loop on device. Local only, no backend (#352). |
| **MVP** | Soft-launch feature set. Everything a player needs for 30 days of play, plus backend, IAP and analytics. |
| **P1** | Designed in detail so MVP code leaves room for it. Built after soft launch. |
| **P2** | Mentioned so we don't paint ourselves into a corner. No detailed design. |

### 2.1 Feature inventory

Each row maps a mechanic from the research catalog to our system name, its tier, the spec that owns it (section 10's planned file map) and the issue that writes it. The tiers are agent defaults set in #341. Each owning issue may move a row by updating this table in the same change.

| Source mechanic (research §) | Our system | Tier | Spec | Issue |
|---|---|---|---|---|
| Star grades 1-7, stat window, stats and growth per level (§3) | **Heroes**, **Hero Sheet** | M0 | 03 | #357 |
| Classes as data and formation roles (§3, §9.5) | **Classes**, **Roles** | M0 | 03 | #357 |
| Summon-only classes; class change at 3★ and Lv20 (§3, §9.3) | **Classes**, **Class Change** | MVP | 03 | #357, #367 |
| Stat redistribution by focus preset (§3) | **Redistribution** | MVP | 03 | #357 |
| Status effects: Fear, Panic, Despair, Berserk, Composure, Exhaustion, poison, bleeding, frenzy, near death, hidden power (§3, §9.1) | **Statuses** | Fear M0, rest MVP, Surge and dual mind P1 | 03 | #358 |
| Morale, stress, breakdown and stress death, intimacy and pair synergy (§3, §8) | **Inner Life** | MVP | 03 | #359 |
| Loyalty, refusals and strikes (§3, §8) | **Inner Life** | P1 | 03 | #359 |
| Former lives, hidden talents, seal-slot traits, memory restoration (§3, §9.1) | **Former Lives**, **Traits** | Former life M0, rest MVP, crafting trades and claimed lives P1 | 03 | #360 |
| Levels, EXP, level cap per grade (§5) | **Levels** | M0 | 03 | #363 |
| Synthesis and the conversion chamber (§5, §9.3) | **Synthesis**, **Conversion** | MVP | 03 | #364 |
| Promotion by stones and recipes up to 7★ (§5, §9.3) | **Promotion** | M0 (low steps), MVP (all) | 03 | #365 |
| Skills: training, levels, tiers, merging, evolution, awakening, skill books (§5, §9.3) | **Skills** | MVP | 03 | #366 |
| Normal and advanced hero summons, 1x and 10x, affinity pulls (§4, §9.2) | **Hero Summon** | M0 (local), MVP (server) | 04 | #361 |
| Equipment summon for gold (§4) | **Gear Summon** | MVP | 04 | #362 |
| Equipment grades, slots, storage, auto-equip, durability, potions (§6) | **Gear**, **Storage**, **Consumables** | MVP | 04 | #368 |
| Hero artisans, workshop, crafting minigames (§6, §9.4) | **Crafting** | P1 | 04 | #369 |
| 100-floor tower, a step every 5 floors, regions every 10, floor unlocks (§7, §9.5) | **Tower**, **Floors** | M0 (floors 1-10), MVP (1-100) | 05 | #370 |
| Auto-battle on hero AI, roles and formation (§8, §9.5) | **Battle** | M0 | 05 | #371 |
| Mission types: subjugation, survival, defend, escort, maze, multi-party raids (§7, §9.5) | **Missions** | Subjugation M0, rest MVP | 05 | #372 |
| Permadeath, death notices, memorials, mission failure (§7, §9.5) | **Death**, **Revival**, **Memorials** | Death and revival M0, memorials P1 | 05 | #373 |
| Replays, weekday dungeon, 48-hour exploration (§7) | **Replays**, **Weekday Dungeon**, **Exploration** | MVP | 05 | #374 |
| Adventure dungeon (§7) | **Adventure Dungeon** | P1 | 05 | #374 |
| Lobby facilities, levels, annexes, lodging tiers (§8) | **Lobby**, **Facilities** | Core facilities MVP, annex fusion and lodging tiers P1 | 06 | #375 |
| The manager NPC: orders, tips, intervention points, onboarding (§8, §9.6) | **Manager**, **Tutorial** | MVP | 06 | #376 |
| Research lab with a researcher hero (§8) | **Research** | MVP | 06 | #377 |
| Parties of five, leaders, hero-proposed lineups, slot duels (§8, §9.1) | **Parties** | One party M0, several MVP, proposals and duels P1 | 06 | #378 |
| Tactical posts: a pre-battle positioning screen (§8) | **Formation Board** | P1 | 05, 06 | #371, #375 |
| Gifts and favourites (§8) | **Gifts**, **Favourites** | Favourites MVP, gifts P1 | 06 | #359, #378 |
| Account level, rankings by highest floor (§8, §9.6) | **Account Level**, **Rankings** | Account level MVP, rankings P1 | 06 | #379 |
| Mailbox, tutorial rewards, paid packages (§4, §8) | **Inbox**, **Shop**, **IAP** | MVP | 06, 08 | #379 |
| Idle time: lobby clock, offline progress (§8) | **Idle Time** | M0 | 06 | #379 |
| Not in the source: daily and weekly missions, achievements | **Quests** | MVP | 06 | #379 |
| Not in the source: limited and rate-up banners, events | **Events** | P1 | 04, 06, 08 | #361, #379 |
| Guest and platform login, cloud save | **Account**, **Cloud Save** | MVP | 02, 08 | #384, #386 |
| Not in the source: rewarded ads, redeem codes | **Rewarded Ads**, **Coupons** | P1 | 06, 08 | #386 |
| Multi-master layer: community, dispatch, training services, invasion, flying ship (§9.6) | **Multi-Master** | P2 | 06 | #380 |
| Not in the source: costumes and alternate art | **Costumes** | P2 | 03 | #349 |

Rows marked "not in the source" are systems we add (kind *Added* in `specs/11`). They need a register row, approved by the owner where they touch monetization, before their spec is written (decision 129). HeroCat had all of them, so the infrastructure exists; the design does not.

## 3. Tech stack decision

Carried over from HeroCat unchanged (decisions 1, 31, 32, 35, 65). The new game changes the game systems, not the stack.

| Layer | Choice | Notes |
|---|---|---|
| Client engine | **Unity 6 (`6000.6.7f1`, pinned in `unity/HeroCatClient/ProjectSettings/ProjectVersion.txt`)**, C#, URP 2D, uGUI + TextMeshPro, Addressables | iOS and Android only. The Unity client is the only client; there is no web build (decision 32). |
| Simulation core | **`HeroCat.Sim`**, pure C# netstandard2.1, C# 9, no `UnityEngine`, no IO, no clock, no JSON, no reflection | One implementation runs on device, in server validation and in bots. No `Math.Pow/Exp/Log` on the hot path, no floating-point reductions in unspecified order, no `DateTime.Now`. |
| Runtime glue | **`HeroCat.Runtime`**, plain C#, references Sim + Newtonsoft.Json | Save serialization, migrations, clock, local store, HTTP clients, content loading. Shared by client and bots. |
| Backend | **ASP.NET Core 8** stateless API, **PostgreSQL**, **Redis**, **S3-compatible object store**, CDN for content | C#, so the server references `HeroCat.Sim` for re-simulation and rate tables. The schema is plain versioned SQL migrations (decision 65). |
| Content | `content_src/*.csv` -> validated JSON tables -> versioned content bundle served by CDN, with a fallback copy in the app | People and agents edit the committed CSVs directly (#278). Spec 09. |
| Analytics | Firebase Analytics for MVP, plus our own events table on the backend for economy events | |
| CI | GitHub Actions as thin wrappers around `./check.sh` modes (decision 59) | Spec 10. |

Orientation: **portrait only**. Minimum targets: iOS 15, Android 8 (API 26), 2 GB RAM devices at 30 fps.

## 4. Architecture in one page

Full detail in 01. The layering is unchanged from HeroCat:

```
Unity client (presentation only)   Screens <- ViewModels <- SimEvents ; Screens -> Commands
        |
HeroCat.Runtime   SessionRunner, SaveSerializer, Migrations, LocalSaveStore, CloudSaveClient,
                  ServerClock, SummonClient, ContentLoader, Analytics
        |
HeroCat.Sim       GameState, Simulation.Tick(), Commands, Events, RNG streams, Catch-up, ContentDb
        ^                                   ^
HeroCat.Bots (headless harness)    HeroCat.Server (ASP.NET: Auth, Save, Summon, IAP, Config, Mail)
```

Authority split, which every spec must respect:

| Concern | Authority | Notes |
|---|---|---|
| Battles, floor progress, hero levels, statuses, deaths, inner life, lobby timers, offline catch-up | **Client sim** | Deterministic. The server can re-simulate from an earlier save and a command log for spot checks (08, #386). |
| Synthesis, conversion, promotion and crafting rolls | **Client sim** | Each roll draws from a named stream in the save (contract 2), so results replay exactly. |
| Hero and gear summon results | **Server** (MVP+), local `ISummonService` in M0 | The server rolls, logs and returns results. The client never holds a summon seed in MVP (decision 2). |
| Gem balance and summon tickets | **Server** ledger (MVP+) | M0 holds them in the local `Wallet`. In Remote mode the client's balance mirrors the ledger (decision 15). |
| Client-earned gems and tickets (quests, first clears, tutorial, login) | **Client sim**, credited to the server on upload | As decision 81, with gems in place of diamonds. |
| Revive items bought for gems | **Server** | A gem purchase, granted through the inbox like any other paid grant (DIV-04, #386). |
| Wall-clock time | **Server** | |
| Gem grants from IAP, mail, coupons | **Server** | Receipts are validated server-side; grants land in the inbox. |
| Save of record | **Server** | The client is a cache. Conflict rules in 02. |
| Rankings, event schedule, remote config | **Server** | |

Command and event discipline is unchanged: the client never mutates `GameState`. It sends a `Command` into `Simulation`, which validates it, mutates the state and emits `Event`s. Runtime-originated changes (server time, offline catch-up, summon results, inbox grants) are system commands through the same door (decision 38).

## 5. Canonical vocabulary and identifiers

Use these names in code, tables, specs and analytics. Singular for types, plural for collections. The owning specs add fields and members. They don't rename what is listed here without updating this section.

### 5.1 Entities

| Name | Meaning |
|---|---|
| `Profile` | The account-level record: `playerId`, display name, created time, linked platforms. Not part of sim state. Client-only flags live in Runtime's `ClientPrefs` (decision 79). |
| `GameState` | The save root: everything the sim reads and writes. Holds the immutable `RngRoot` and the `ContentVersion` and `ContentHash` it was last reconciled against. Field names and shapes are 02's (#384). It is a fresh schema v1; HeroCat dev saves are not migrated (decision 33). |
| `Summoner` | The player's in-world role. Not an entity in `GameState`: it is the player. Account-wide progress (account level, lobby, wallet) is the summoner's. |
| `HeroDef` | A content row: one character concept from the reused cast (decision 127), with its base grade, class, former life, base stats, growth rate, traits, hidden talents, seal traits and memories (03 sections 1-2, #391). |
| `Hero` | One owned hero: an individual instance with a `HeroUid` that never changes and a `HeroDefId`. It has its own grade, level, EXP, stats, class, skills, traits, statuses, inner-life values, gear, favourite flag and life state. Heroes are individuals, not stacked copies. Whether two heroes of the same `HeroDef` can coexist, and what a duplicate pull gives, is #344's call (#361, #364). |
| `HeroUid` | Stable id of an owned `Hero`, assigned when the hero enters the roster. Never reused, even after death, synthesis or conversion. |
| `Roster` | All living and dead heroes the summoner owns, up to a lodging cap (06, #375). |
| `Grade` | A hero's star grade, an integer `GRADE_MIN..GRADE_MAX` (1★ to 7★). Ordered. Shown as stars. Raised only by `Promotion` (03, #365). |
| `Stat` | A hero attribute: the fixed set `STR`, `INT`, `VIT` (shown as Stamina) and `AGI`, integer valued, built in core, max and current layers (03 section 1.2). Shown as current/max (research §3). |
| `Class` | A hero's combat class. 1★ and 2★ heroes start as `NOVICE` (`NOVICE_MAX_GRADE`); some classes are summon-only (03 section 1.4). Class change is 03's (#367). |
| `Skill` | A hero's ability, with a level and a tier. Learned, levelled, merged and evolved per 03 (#366). |
| `Trait` | A graded passive with a level, from a hero's former life, a seal slot, a revealed talent or a memory (03 section 2, #360). |
| `Role` | A hero's formation role, `SHORT`, `MID`, `LONG` or `SUPPORT`, set by its class (03 section 1.5). 05's formation reads it. |
| `StatFocus` | Growth weights that place a hero's stat points automatically at level-up (03 section 1.3). |
| `Status` | A timed or conditional combat state on a combatant, such as `FEAR`: a content row in `statuses`, applied by deterministic triggers that draw no RNG (03 section 3, #358). |
| `LifeState` | `ALIVE` or `DEAD`. A `DEAD` hero keeps its row, can't be assigned to a party and returns only through `Revive` (05, #373). Heroes consumed by synthesis or conversion leave the roster and are not `DEAD`: they can't be revived (decision 128). Whether a dead hero's death record shows in a memorial is P1 (05, #373). |
| `Party` | Up to `PARTY_SIZE` heroes with an optional leader, identified by `PartyId`. A hero is in at most one party (06, #378). |
| `Assignment` | A party's current job: a tower floor, a replay, the weekday dungeon or an exploration. While assigned, the party fights on its own, online or away (05, 06). |
| `Tower` | The 100 floors, `TOWER_FLOORS` (05, #370). |
| `Floor` | Integer `f` in `1..TOWER_FLOORS`. A content row with a mission type, objectives, the number of parties needed, an enemy roster and rewards (05, #370, #372, #390). |
| `Region` | A group of `REGION_EVERY` floors with its own setting and enemies (05, #370). |
| `Mission` | The objective set of one floor attempt: subjugation, survival, defend, escort and the others of 05 (#372). |
| `Battle` | One deterministic auto-battle between assigned heroes and a floor's enemies, ticked in the main loop (05, #371). |
| `Gear` | One owned equipment item with a `GearUid`, a `GearDefId` and a letter `GearGrade` (04, #368). |
| `GearGrade` | Equipment grade, ordered from `F` through letter grades with sub-grades to `S` and above (04, #368; research §6, §9.4). |
| `Item` | A stackable owned thing: materials (elemental stones, promotion materials), consumables (potions, revive items) and tickets that aren't currencies. Ids and kinds are content (04, #368; 09). |
| `ReviveItem` | An `Item` that brings one `DEAD` hero back (DIV-04). Earned in play and sold for gems at a listed price (decision 126). |
| `Facility` | A lobby building with a level, such as the summoning hall or the training hall (06, #375). |
| `Manager` | The lobby's manager NPC: the tutorial and UI voice, who relays orders, gives tips and can execute a hero who disobeys (decision 128); its other powers are 06's (#376). Our own character, not the source's. |
| `Banner` | A summon pool with published rates and its own pity counter: `HERO_NORMAL`, `HERO_ADVANCED` and `GEAR` (04, #361, #362). |
| `Quest` | A tracked objective with a reward. Kinds `DAILY`, `WEEKLY`, `ACHIEVEMENT`. Three-state with an explicit claim (decision 17). |
| `Wallet` | All currency balances. Every change goes through `Wallet.Apply(CurrencyDelta, Reason)`. A debit that would go below zero is rejected and changes nothing. |
| `Inbox` | Server-granted items awaiting a claim (IAP, mail, coupons, compensation). |
| `Command` | A player or system intent fed to `Simulation`, named `VerbNoun`. The registry is the union of each owning spec's command table; 01 defines the base type (#388). Provisional vocabulary: Heroes (03) `LevelUpHero`, `Promote`, `Synthesize`, `Convert`, `ChangeClass`, `TrainSkill`, `ToggleFavourite`, `RedistributeStats`, `OrderRest`, `CancelRest`, `GiveGift` (P1). Gear (04) `EquipGear`, `UnequipGear`, `AutoEquip`. Tower (05) `AssignFloor`, `AssignReplay`, `Revive`. Lobby (06) `SetParty`, `SetPartyLeader`, `UpgradeFacility`, `SetResearch`, `StartExploration`, `ClaimOfflineReward`, `ClaimQuest`, `ClaimAllQuests`. Tutorial (07) `SetTutorialStep`. System commands (01; issued by Runtime, never by a screen, and logged like any other command): `ApplyServerTime`, `ComputeOfflineCatchup`, `ApplySummonResult`, `ApplyInboxGrant`. `Summon` and `ClaimInbox` stay Runtime flows that end in a system command. Rejects follow decision 43: 01's `RejectCode` plus a closed per-spec UPPER_SNAKE reason, with `NOT_ENOUGH_<CURRENCY>` for every short balance (decision 95). `Apply` never throws on bad input. |
| `Event` | A fact emitted by the sim, named `NounPastTense`. Provisional vocabulary: `HeroAcquired`, `HeroLeveledUp`, `HeroPromoted`, `HeroesSynthesized`, `HeroConverted`, `ClassChanged`, `SkillLeveled`, `StatusApplied`, `StatusEnded`, `StatsRedistributed`, `TraitGained`, `TraitLeveled`, `SealSlotOpened`, `TalentRevealed`, `MemoryRestored`, `MoraleBandChanged`, `StressBandChanged`, `HeroBrokeDown`, `HeroRecovered`, `HeroRestOrdered`, `HeroRested`, `BondFormed`, `BondTierChanged`, `GiftGiven`, `PartyInoperable`, `HeroDied` (with a cause, `STRESS` among them), `HeroRevived`, `HeroExecuted`, `HeroRefused`, `BattleStarted`, `FloorCleared`, `FloorFirstCleared`, `MissionFailed`, `PartyWiped`, `GearAcquired`, `GearEquipped`, `FacilityUpgraded`, `ResearchLeveled`, `ExplorationReturned`, `CurrencyChanged`, `ItemChanged`, `QuestCompleted`, `QuestClaimed`, `DayRolledOver`, `WeekRolledOver`, `OfflineRewardComputed`, `OfflineRewardClaimed`, `ClockWentBackwards`, `TutorialStepChanged`. Events about one hero carry its `HeroUid`; events about a party carry its `PartyId`. |

### 5.2 Currencies

| Code | Display | Main source | Main sink | Soft/hard |
|---|---|---|---|---|
| `GOLD` | Gold | Floor clears, replays, exploration | Normal hero summon, gear summon, facilities, research, skill books, recording | Soft |
| `GEM` | Gems | IAP; free amounts earned in play, sources and sizes set in #379 | Advanced hero summon, revive items at a listed price, skipping waits | Hard |
| `TICKET_SUMMON_HERO`, `TICKET_SUMMON_GEAR` | Summon tickets | Tutorial, quests, events | One pull each | Soft |

`GEM` is the only currency bought with money (decision 7, renamed). The source gives free players almost no gems (research §4: missions give none). Free gem income is what lets a free player reach every grade (DIV-03), so #379 sets it and records the change from the source in `specs/11`. Gems never pay for research hours, annexes, skill books or replay recording; those cost gold or materials, and gems only skip waiting (DIV-05). Materials, potions and revive items are `Item`s, not currencies. Hero EXP belongs to the hero, not the wallet. Further currencies are added by their owning spec, here first.

### 5.3 Screen identifiers

07 owns the full inventory, layouts and states (#385). These IDs are canonical; 07 may add more.

| ID | Purpose | Tier |
|---|---|---|
| `SCR_BOOT`, `SCR_TITLE` | Boot, login and account pick (carried) | M0, MVP |
| `SCR_LOBBY` | Home screen: the lobby, its facilities, the manager and status alerts. Replaces HeroCat's `SCR_MAIN`. | M0 |
| `SCR_ROSTER` | Hero list with sort, filter and favourites | M0 |
| `SCR_HERO` | One hero's stat window: grade, level, stats, class, skills, traits, statuses, gear, history | M0 |
| `SCR_SUMMON`, `SCR_SUMMON_RESULT` | Hero and gear banners with published odds and pity; the reveal | M0 |
| `SCR_PARTY` | Party builder and floor assignment | M0 |
| `SCR_TOWER` | Floor list and floor detail: mission type, hints, enemy roster, parties needed | M0 |
| `SCR_BATTLE` | Watching an assigned fight: status alerts and death notices | M0 |
| `SCR_MISSION_RESULT` | Clear or failure, MVP, level-ups, drops, deaths | M0 |
| `SCR_PROMOTION`, `SCR_SYNTHESIS` | Promotion, synthesis and conversion | M0 (promotion), MVP |
| `SCR_GEAR` | Storage, gear detail and equip | MVP |
| `SCR_FACILITY`, `SCR_RESEARCH` | One facility's levels and effects; the research lab | MVP |
| `SCR_OFFLINE_REWARD` | What happened while away, including deaths (carried) | M0 |
| `SCR_QUESTS`, `SCR_SHOP`, `SCR_INBOX`, `SCR_SETTINGS`, `SCR_PROFILE` | Carried | MVP |
| `SCR_TUTORIAL` | The manager's overlay of tutorial steps (decision 78) | MVP |
| `SCR_CONFIRM`, `SCR_ERROR` | Carried | M0 |
| `SCR_RANKING`, `SCR_EVENTS`, `SCR_MEMORIAL` | Rankings by highest floor; events; memorials of the dead | P1 |

Retired with HeroCat: `SCR_MAIN`, `SCR_WEAPONS`, `SCR_SKILLS`, `SCR_COMPANIONS`, `SCR_RUNES`, `SCR_DUNGEONS`, `SCR_DUNGEON_RUN`, `SCR_STAGE_SELECT`, `SCR_ITEM_DETAIL`, `SCR_GUILD`.

Locked states keep the single `feature_unlocks` content table (decision 77), read by both the sim and the client. The new main condition is `FLOOR_HIGHEST >= n`, the highest floor cleared. The seed unlocks follow the source (research §7, §8): the weekday dungeon after floor 5; exploration, lobby naming and the adventure dungeon (P1) at floor 10; a new region every `REGION_EVERY` floors.

## 6. Canonical constants

Every constant here is a row in a content table (09). Code reads it from `ContentDb`, never from a literal. Kinds:

- **Infra** (`I`): carried from HeroCat's infrastructure, not balance. Trust it.
- **Source** (`Src`): taken from the research catalog (cited by §). It is a starting point, and the owning spec may retune it. If the spec changes the rule rather than the number, add a `specs/11` row.
- **Seed** (`S`): new to this design and not yet validated. The balance model (#348) validates it. Don't hard-code it anywhere.

Values marked "set in #N" don't exist yet. The owning spec adds them, here and in its own constants table.

### 6.1 Time and engine

| Name | Value | Kind | Notes |
|---|---|---|---|
| `TICK_HZ` | 10 | I | Fixed sim tick, 100 ms. |
| `DAY_SECONDS` | 86400 | I | `dayKey = floor(serverSeconds / DAY_SECONDS)`. Rollover at 00:00 UTC for everyone (decision 8). |
| `WALL_CLOCK_SYNC_SEC` | 60 | I | A `RuntimeConfig` constant (decision 37). |
| `CLOCK_BACKWARDS_TOLERANCE_S` | 5 | I | Decision 101. |
| `IDEMPOTENCY_TTL_H` | 24 | I | Decision 28. |
| `OFFLINE_CAP_H` | 24 | S | How long assigned parties keep fighting while the player is away. Carried from HeroCat as a default; 06 sets the idle time model (#379). |
| `OFFLINE_MIN_S` | 60 | S | A gap shorter than this shows no offline report. |
| `INBOX_EXPIRY_DAYS` | 30 | S | `IAP` items never expire. |

### 6.2 Heroes and growth

| Name | Value | Kind | Notes |
|---|---|---|---|
| `GRADE_MIN`, `GRADE_MAX` | 1, 7 | Src | Star grades 1★-7★ (research §3). |
| `SUMMON_GRADE_MAX` | 5 | Src | The highest grade a summon can give; 6★ and 7★ come only from promotion (research §3). No grade is paid-only (DIV-03). 04 confirms it (#361). |
| `LEVEL_CAP[g]` | 1★ 10 / 2★ 20 / 3★ 40 / 4★ 60 / 5★ 80 / 6★ 99 / 7★ above 99 | Src | 1★ is clear in the source, the rest is wiki-grade (research §5, §9.6). 7★'s cap is set in #363. |
| `CLASS_CHANGE_GRADE`, `CLASS_CHANGE_LEVEL` | 3, 20 | Src | Research §9.3. 03 owns it (#367). |
| `STATUS_FEAR_PCT`, `STATUS_PANIC_PCT`, `STATUS_DESPAIR_PCT`, `STATUS_EXHAUSTION_PCT` | -30 / -50 / -80 / -90 | Src | All stats, while the status lasts (research §3, §9.1). 03 owns it (#358). |
| `NOVICE_MAX_GRADE`, `STAT_COUNT`, `GROWTH_DEFAULT[g]`, `GROWTH_BONUS[g]`, `STAT_REDISTRIBUTE_GOLD_PER_LEVEL`, `HERO_AWARENESS_MAX` and the other hero-sheet constants | 03 "Constants introduced" | Src/S | Growth per level is `HeroDef.growth_rate` + `GROWTH_BONUS[g]`, seeded 4-5 a level from research §3 (#357). |
| Trait, talent, seal-slot and memory constants (`FORMER_LIFE_START_POINTS`, `SEAL_SLOTS[g]`, `TRAIT_*`, `TALENT_*`, `BASIS_POINTS`) | 03 "Constants introduced" | Src/S | #360. |
| The other `STATUS_*` constants (thresholds, shock, durations, clamps) | 03 "Constants introduced" | S | #358. |
| Inner-life constants (`INNER_*`, `MORALE_*`, `STRESS_*`, `BOND_*`, `LOYALTY_*`, `GIFT_*`) | 03 "Constants introduced" | S | #359. Rule: `STRESS_GRACE_H >= OFFLINE_CAP_H`, checked by the content validator. |
| EXP curve, promotion recipes and success rates, synthesis transfer | set in #363, #364, #365 | Src/S | Source anchors: EXP to next level 10, 20, 50, 70, 120 at Lv1, 2, 5, 9, 15; promotion success rates of 87% and 91% (research §10). |

### 6.3 Tower and battle

| Name | Value | Kind | Notes |
|---|---|---|---|
| `TOWER_FLOORS` | 100 | Src | Decision 123. |
| `FLOOR_STEP_EVERY` | 5 | Src | Difficulty steps every 5 floors, with a boss-like spike (decision 123, research §7). Difficulty between steps need not rise every floor. |
| `REGION_EVERY` | 10 | Src | A new region every 10 floors (research §7). |
| `PARTY_SIZE` | 5 | Src | Heroes per party (research §2). |
| `PARTIES_MAX` | set in #378 | S | The source's floor 20 needs 3 parties (research §9.5). |
| `GATE_COUNTDOWN_S` | 10 | Src | Presentation only. The sim starts the battle on the assignment tick (research §7). |
| Enemy and boss scaling, mission timers, floor rewards | set in #370, #372 | S | Source anchors: a survival floor runs 10 minutes; floor 80's enemies are Lv108-121 (research §10). |

### 6.4 Summons and economy

| Name | Value | Kind | Notes |
|---|---|---|---|
| Rates per banner and grade, hard pity, 10-pull guarantee | set in #361, #362 | S | Published per banner and the same for every account (DIV-01, DIV-02). Source anchors: the advanced summon's 4★ rate is 1%; normal summons give 1-3★, advanced give 3-5★ (wiki) (research §4, §9.2). |
| Pull prices | set in #361, #362 | S | Source anchors: an advanced pull costs 300-500 gems; a gear 10x costs 50,000 gold (research §10). |
| Free gem income, login gems, first-clear gems, revive item price, packages | set in #379 | S | Replaces HeroCat's diamond budget (decision 18). Free income must let a free player reach every grade (DIV-03). |

### 6.5 Session model and targets

The bot harness keeps HeroCat's session shape until #348 replaces it: a free player active 2 hours a day in two 60-minute sessions, offline the rest, with sessions crossing the 00:00 UTC rollover on alternate days (decisions 25, 61). Progress targets are floors reached by day, set by the balance model (#348) and kept as content (`progress_targets`, 09). Until then, the acceptance sweep is report-only (decision 125).

Source anchors for the target curve (research §2, §9.5, §10): floor 5 is the first wall many players fail; fewer than half reach floor 20; the best player reached floor 88 in two years. Ours should be faster than the source's: it's a story beat, not a tuned game.

## 7. Hero power

HeroCat's power formula and its layer-share rule (decision 44) are retired with HeroCat's systems. 03 defines how a hero's combat stats come from grade, level, growth, class, skills, traits, gear, inner life and statuses (03 section 5) and the stat term of hero power (03 section 1.2.2), and 05 (#371) defines how a battle turns stats into damage. Both follow contract 1: curves evaluated by repeated multiplication from tables, no `Math.Pow/Exp/Log`. Both expose a rated power value that the bots, the tools, the plausibility checks (08) and the UI's party-strength hint read.

## 8. Cross-spec contracts

1. **Sim is pure.** `HeroCat.Sim` has no `UnityEngine`, IO, clock, JSON, reflection or `Math.Pow/Exp/Log`. Curves are evaluated by repeated multiplication from tables. Everything that needs IO lives in `HeroCat.Runtime`.
2. **Named RNG streams.** Each consumer owns a stream `(seed, counter)` persisted in the save. Every seed is the first 8 bytes of SHA-256(`RngRoot` ++ UTF-8 stream name), derived in Runtime. `RngRoot` is an immutable Guid set once at profile creation (decision 41). The new stream list is 01's and 05's (#388, #371). It needs at least battle, loot, promotion, synthesis, floor-variant, `inner_life` (hostility on summon, 03 section 4.5) and the two summon streams. Stats, growth, traits, talents, memories and statuses draw no RNG (03 section 5). The summon streams are drawn only by `LocalSummonService` (M0 and bots); in Remote mode their counters are ignored, not removed (decision 14). Floor variants are seeded and the same rules apply to every account (DIV-01). 05 owns the battle draw order and its invariants (decision 40, owner moved from 03).
3. **Content is data.** Every number in section 6 is a row in a table from 09. Code receives a `ContentDb` and never a literal. Versions are `YYYY.MM.DD[.N]`; breaking changes go through `min_client_version` plus `schema_version`. Content applies at the next cold boot, except the hot-apply allowlist (decision 86). Loading a save against other content runs 02's validator (decision 88).
4. **Save schema is versioned from v1** with forward-only migrations and a fixture per version (02). The new game starts a fresh v1 (decision 33, #384).
5. **Commands in, events out.** The client, bots and server re-simulator only talk to the sim through `Simulation.Apply(Command)` and `Simulation.Advance(ticks)`, and only observe it through `Event`s and read-only snapshots. Server time, offline catch-up, summon results and inbox grants enter as logged system commands, so replaying the command log reproduces the state exactly.
6. **Every currency and item change has a reason.** `Wallet.Apply(delta, reason)`, and the item equivalent, take a reason from one closed enum that 02 owns. The seed list: `FLOOR_CLEAR`, `FIRST_CLEAR`, `REPLAY`, `DUNGEON`, `EXPLORATION`, `OFFLINE`, `LOGIN`, `TUTORIAL`, `QUEST`, `ACHIEVEMENT`, `SUMMON`, `PROMOTION`, `SYNTHESIS`, `CONVERSION`, `CLASS_CHANGE`, `STAT_REDISTRIBUTE`, `SKILL`, `FACILITY`, `RESEARCH`, `REVIVE`, `CRAFT`, `SHOP_EXCHANGE`, `IAP`, `MAIL`, `REFUND`, `ADMIN`. `ADMIN` is only for operator interventions. The analytics event for every change carries it.
7. **Server time is the only trusted time.** `IClock` with `ServerClock` (MVP) and `FakeClock` (bots, tests). Day and week keys (decision 21), offline gaps, the weekday dungeon's rotation, exploration timers and banner schedules all derive from it. A clock going backwards grants nothing twice. Offline catch-up is two-phase, as in decision 53: `ComputeOfflineCatchup` computes and stores the result, `ClaimOfflineReward` credits it, and ticks pause while `SCR_OFFLINE_REWARD` is open.
8. **Away is the same game.** Assigned parties keep fighting while the player is away, under the same battle rules, including statuses and death (decision 123, pillar 3). Catch-up must give the same result as the equivalent online ticks, or 05 and 06 must define a deterministic catch-up that the bots prove equivalent within a stated tolerance (#371, #379). Every death that happened while away shows in the offline report.
9. **Screens bind to view-models; view-models subscribe to events.** No screen reads `GameState` directly (decision 120).
10. **Numbers.** Curve-valued numbers (gold, EXP, HP, damage) are `double`. Counted values (gems, tickets, item counts, levels, grades, hero stats, percentages, basis points and inner-life values) are integers. Display formatting follows decision 54.
11. **All user-visible text is a localization key** (07, 09). Keys for errors, rejects and notices follow decision 102.
12. **Server-sensitive operations go through an interface** with a local implementation for M0 and a documented remote contract: `IClock`, `ISummonService`, `ISaveStore`, `IInboxService`, `IReceiptValidator`, `IConfigService` (01, 08). Whether floors or explorations need server-issued seeds, as HeroCat's dungeons did (decision 57), is #386's call.
13. **The bot harness is the acceptance test** for any balance change (10). `StateHash` is SHA-256 over the canonical binary state encoding. The golden command log is JSONL in 01's command-log format (decision 60).
14. **Idempotency for every server mutation.** Each client-originated mutating request carries a client-generated `requestId`; the server keeps results for `IDEMPOTENCY_TTL_H` and replays them on retry.
15. **Mechanics only, divergences on record.** No name, character, place, story beat, item name, art or text from the source enters the specs, content, code or store copy (decision 121). When a spec knowingly departs from the research catalog, it adds or updates a `specs/11` row in the same change and cites the `DIV-` id (decision 129).

## 9. Spec writing conventions

Every spec file follows this skeleton:

1. Title `# NN - Name`, a one-paragraph purpose, the scope tiers covered and a `Depends on:` line.
2. Body, with tables for data and C#-style pseudocode for logic. Mermaid for diagrams.
3. `## Divergences from the source`: the `DIV-` ids the spec relies on, or "none".
4. `## Open questions`, each with an owner guess and the default we take if nobody answers.
5. `## Acceptance criteria`, numbered, each testable by a bot, a unit test or a checklist item.
6. `## Constants introduced`, so they can be lifted into section 6 here.

Rules: use the names from section 5 exactly; tag every feature with its tier; cite the research catalog by section for any borrowed mechanic; use only our own names and fiction (contract 15).

## 10. File map

Specs 01, 02 and 07-10 keep their numbers and are rewritten in place. Specs 03-06 are repurposed for the new game's systems; each file is renamed in the change that rewrites it. Until then, each file still holds its HeroCat text.

| File (planned name) | Covers | Today | Issue |
|---|---|---|---|
| `00-overview.md` | This file | New game | #341 |
| `01-architecture-and-data-flow.md` | Layers, commands and events, the idle tick and offline flow, server flows | HeroCat | #388 |
| `02-domain-model-and-persistence.md` | `GameState` v1, save store, cloud save, server DB | HeroCat | #384 |
| `03-heroes-and-growth.md` | Hero sheet, stats, classes, statuses, inner life, former lives and traits; levels, synthesis, conversion, promotion, skills, class change | New game (hero model); growth pending #344 | #342, #344 |
| `04-summons-and-gear.md` | Hero and gear banners, rates, pity, odds disclosure; gear grades, slots, storage, consumables; crafting (P1) | HeroCat summons and collections | #343, #345 |
| `05-tower-and-battle.md` | Floors, regions, unlocks, the auto-battle sim, mission types, death and revival, side content | HeroCat fish board (retired) | #346 |
| `06-lobby-and-meta.md` | Lobby and facilities, the manager, research, parties, account level, rankings, quests, shop, inbox, idle time; the P2 multi-master layer | HeroCat economy and daily loop | #347 |
| `07-screens-and-ux.md` | Navigation, every screen, accessibility, localization and performance rules, Unity client setup | HeroCat | #385 |
| `08-backend-and-live-ops.md` | Services, API, auth, IAP, anti-cheat, config, compliance | HeroCat (mostly carries over) | #386 |
| `09-content-data-pipeline.md` | Table schemas, exporter, validation, `ContentDb`, localization | HeroCat | #387 |
| `10-testing-release-and-ops.md` | Test pyramid, bots, CI/CD, store release, KPIs, runbooks | HeroCat | #388 |
| `11-source-divergences.md` | Every deliberate difference from the research catalog | New game | #382 |
| `archive/00-overview-herocat.md` | HeroCat's overview, kept for citations; not a spec | Archived | #341 |

Tools: `tools/power_curve_model.py` and `tools/gacha_model.py` are HeroCat models, rewritten or replaced in #348. `tools/fish_model.py` goes with the fish board (#389). `tools/export_content.py` and `tools/content.py` carry over. The source layout of the archived file's section 10 carries over unchanged until the rename (#351).

## 11. Milestones

| Milestone | Contents | Exit criterion |
|---|---|---|
| **M0 vertical slice** (#352) | Local only. Hero summon (local), hero sheet with grade, stats and former life; one party of five; tower floors 1-10 with the floor-5 step and a floor-10 boss; subjugation missions; the auto-battle with Fear; levels and EXP; promotion of a low-grade hero; death and a revive item end to end; offline catch-up; local save. Client: the M0 screens of section 5.3 with placeholder art. Bots run the loop. | Sim tests, bots and `./check.sh` green; Unity screens type-checked (Unity can't run in cloud sessions); one person plays 20 minutes on device without being told what to do. |
| **MVP** | Everything tagged MVP in section 2.1. Backend: auth, cloud save, server summons, IAP, inbox, config. Analytics. Tutorial. EN ships, `ja` translated in the pipeline (decision 89). | Closed beta of 50 players, D7 retention measured, no save-loss reports. |
| **Soft launch** | Store compliance checklist complete (10), with gacha odds disclosure and the 17+ rating; staged rollout in the soft-launch markets (decision 29). | D1 >= 40%, D7 >= 15%, crash-free sessions >= 99.5%, no P0 economy exploit. |
| **P1** | Crafting, adventure dungeon, formation board, annex fusion, lodging tiers, gifts, memorials, hero-proposed lineups and duels, rankings, events, rewarded ads, coupons. | Scheduled by soft-launch data. |
| **P2** | Multi-master layer, costumes. | Not scheduled. |

## 12. Decisions log

Decisions are numbered once and never renumbered: code comments and other specs cite them by number. Decisions 1-120 were made for HeroCat; their full text is in `specs/archive/00-overview-herocat.md` section 12. Each has one status here:

- **Kept**: still holds for the new game, as written or with HeroCat names swapped for the new ones (diamonds -> gems, stages -> floors, companions -> heroes).
- **Changed**: the rule carries over in a new form, given here.
- **Retired**: it described a HeroCat system that the new game doesn't have. Don't apply it.
- **Review (#N)**: it may carry over; the named issue decides. Until then, don't build new work on it.

### 12.1 HeroCat decisions 1-120

| # | Status | Note |
|---|---|---|
| 1 | Kept | Unity client, pure-C# sim, ASP.NET Core server sharing the sim. |
| 2 | Kept | Summons are server-authoritative from MVP; M0 uses a local copy of the same algorithm. Covers the gear banner too. |
| 3 | Changed | 24 h offline is a seed default for how long parties keep fighting away (`OFFLINE_CAP_H`), set in #379. |
| 4, 5, 6 | Retired | Boss timer, weapon passive bonus, fish board DPS. |
| 7 | Changed | Gems are the only purchasable currency. |
| 8 | Kept | Day rollover at 00:00 UTC for everyone in MVP. |
| 9, 10, 11, 12, 13 | Retired | Curve constants, "the party cannot die" (reversed by decision 123), Awakening gates, offline research node, limit breaks. |
| 14 | Kept | Summon streams stay in schema v1 for every mode. |
| 15 | Kept | Summon tickets are a server ledger from MVP. |
| 16 | Retired | Dungeon tickets. Side-content costs are 05's (#374). |
| 17 | Kept | Quests are three-state with an explicit claim. |
| 18 | Retired | Diamond budget. The gem budget is set in #379 with #348. |
| 19 | Review (#379) | Packages as hard currency on a delivery schedule. |
| 20 | Changed | The reason enum is reseeded by contract 6 and owned by 02. |
| 21 | Kept | Week key, weeks start Monday 00:00 UTC. |
| 22 | Retired | `MoveFish`. |
| 23 | Kept | Content versioning and one-row rule tables. HeroCat table names go with their tables (#387). |
| 24 | Retired | Enemy skins by stage hash. |
| 25 | Kept | Bot harness epoch and two daily sessions, until #348. |
| 26 | Retired | Tap Zap plausibility bound. |
| 27, 28 | Kept | Saves excluded from OS backup; `IDEMPOTENCY_TTL_H = 24`. |
| 29 | Kept | Soft-launch markets. |
| 30 | Kept | Code names stay `HeroCat.*` until #351. The working title is now *Hundred Floor Summoner* (decision 127). |
| 31, 32 | Kept | Unity 6; no web build. |
| 33 | Kept | Clean replacement of the old design, applied again: the new `GameState` is a fresh v1. |
| 34, 35, 36, 37 | Kept | Content directory and exporter names; uGUI + TextMeshPro; one spec 07; tick-driven virtual clock. |
| 38 | Kept | No mutation outside `Apply`; offline catch-up and summon results are system commands. |
| 39 | Kept | 02 owns `GameState` names and shapes. HeroCat fields go (#384). |
| 40 | Changed | RNG draw rules belong to the battle spec, now 05 (contract 2). |
| 41 | Kept | Seeds derive from the immutable `RngRoot`. |
| 42 | Retired | Rated vs realised damage split of the HeroCat formula. 05 defines its own (section 7). |
| 43 | Kept | Command and event names: this file's, extended by owners; rejects use `RejectCode` plus a closed reason. |
| 44 | Retired | Layer-share gate. |
| 45, 46 | Kept | `SummonResultPayload` is the summon contract (fields change in #361); summon vectors come from the gacha model (#348). |
| 47, 48, 49, 50, 51 | Retired | Missiles and the fish board. |
| 52 | Kept | Login rewards are auto-granted on the day roll; the amounts are #379's. |
| 53 | Changed | Two-phase offline catch-up with ticks paused behind the report (contract 7); the Magic Tank parts are retired. |
| 54, 55 | Kept | Number formatting; 07 acceptance criteria run engine-free first. |
| 56 | Changed | One summon algorithm, shared by the server, `LocalSummonService` and the gacha model. The HeroCat algorithm itself is replaced in #361. |
| 57 | Review (#386) | `IDungeonSeedService`. Contract 12. |
| 58 | Retired | Rune tickets. |
| 59, 60, 61, 62, 63, 64, 65, 66 | Kept | CI wraps `check.sh`; `StateHash`; rollover-crossing sessions; error classes; rollout buckets; GDPR delete; SQL migrations; the cloud-save wire. |
| 67, 68, 69, 70, 71, 72, 73, 74, 75 | Retired | Stage select, the day-20 wall, limit breaks, banner level, MP sufficiency, dungeons and the fish board, Awakening cadence. |
| 76 | Kept | `OWN_*` achievement targets stay below the pool size (pillar 5). |
| 77 | Changed | One `feature_unlocks` table, keyed on the highest floor cleared (section 5.3). |
| 78, 79 | Kept | Tutorial model; `ClientPrefs`. |
| 80 | Retired | Fish art. |
| 81, 82, 83 | Kept | Client-earned gems credited on upload; first-purchase double; package portions in the inbox. |
| 84 | Review (#386) | Dungeon result validation becomes floor-progress plausibility. |
| 85, 86, 87, 88, 89 | Kept | 02 lists every server table; hot-apply allowlist; quest rows are the single source of quest gems; reconcile on load; EN ships first. |
| 90 | Retired | The $20 light-spender script. New spender policies come in #348. |
| 91 | Kept | 10 owns the alarm and runbook catalog. |
| 92 | Review (#386) | `STAGE_VS_TIME` becomes a floor-versus-time bound. |
| 93 | Kept | Netherlands as default EU. |
| 94 | Retired | Companion damage scaling. |
| 95, 96 | Kept | `NOT_ENOUGH_<CURRENCY>` rejects; analytics schemas are owned by the feature specs. |
| 97 | Retired | Fish board ceiling. |
| 98 | Kept | `tutorial_steps.expect_event` lists. |
| 99 | Retired | The new profile's full Magic Tank. |
| 100, 101, 102, 103 | Kept | Welcome grant analytics; clock tolerance; lowercase localization keys; an unreadable save is a new profile. |
| 104 | Retired | The squire avatar. The player is the summoner; whether the summoner is drawn is #349's. |
| 105 | Kept | Mature content rating, reinforced by decision 128. Store art uses covered variants. |
| 106 | Review (#349) | Pixel-art battle sprites. |
| 107 | Retired | Companions in M0. Heroes are M0. |
| 108 | Review (#349) | Splash art for the top grades only. |
| 109, 110, 111, 112, 113 | Retired | The five-companion party model, per-slot mana, its first balance, banner-level retune, companions' drawn weapons. #368 and #349 decide how gear shows in battle. |
| 114 | Review (#386) | Dungeon seed daily cap. |
| 115, 116, 117, 118, 119, 120 | Kept | Error localization coverage; inbox item grants reuse the summon item path; logout rules; where the conflict modal shows; one client asmdef. |

### 12.2 Pivot decisions

121. **Pivot to an original world built on Pick Me Up! mechanics.** Owner decision on issue #337 (option B): the project pivots from HeroCat to a gacha game that borrows the in-story game's mechanics from *Pick Me Up!* (summoned heroes with star grades, a master who raises them, a 100-floor tower). The world, names, characters, story and art are our own. Nothing from the work's IP (names, characters, story, art, text) goes into the game, specs, content or store copy. A license is not being pursued.
122. **The pivot rewrites this repo in place.** Owner decision on issue #338 (option A): the new game is built here, keeping git and issue history, CI, `check.sh` and the infrastructure that fits (deterministic sim pattern, content pipeline, server, bots, Unity client shell). Game systems, content and specs are replaced spec by spec. `HeroCat.*` code names (decision 30) stay until the new name is chosen, then they are renamed in one change (issue #351). HeroCat is superseded rather than run in parallel. Open HeroCat issues are sorted in #350.
123. **The pivot's core loop is idle.** Owner decision on issue #339: combat is an idle auto-battle. The player picks heroes and assigns them to floors, and fights then run with no player input. Progress continues while the player is away; while online, the player changes the lobby (roster, parties, facilities). A dead hero is out of the roster until a revive item brings them back: semi-permanent, never automatic. There is no "underdog" pillar: whether low grades can reach the top is a balance detail, not a design goal. The tower is the spine (100 floors, a difficulty step every 5). Nothing from HeroCat's loop carries over (no fish board, no Tap Zap, no manual skill cast; #229 closed). Owning specs: #370-#374, #378, #379.
124. **The pivot's premise is "The Summoner's Tower".** Owner decision on issue #354, question 1 (option B): the player is a newly licensed summoner who calls heroes from former ordinary lives (farmers, smiths, soldiers) to climb a tower that appeared over the city. There is no "trapped inside a game" frame (DIV-08).
125. **HeroCat's open balance decisions are closed by the pivot.** In the backlog triage (#350, approved by the owner), #249, #266, #276 and #283 were closed as not planned. The acceptance sweep and the report-only model checks stay report-only (`REWRITE_REPORT_ONLY=1`) until the pivot's balance model (#348) replaces them. The `#249` references left in `check.sh`, `tools/` and the bots go with the HeroCat code they describe.
126. **Monetization and fairness for the pivot.** Owner decisions on issue #340: every summon banner publishes its odds and has hard pity (DIV-01, DIV-02). No hero grade is paid-only: free players can reach every grade, and paying gets there sooner (DIV-03). Revive items are earned in play (tower, events) and can also be bought for gems at a listed price (DIV-04). Research hours, facility annexes, skill books and replay recording cost gold or materials; gems only skip waiting (DIV-05). Pity size, rates and prices are set in the summon and shop specs (#361, #379).
127. **The pivot's cast and working title.** Owner decisions on issue #354, questions 2 and 3: the existing character concepts (`art/concepts/companions.md`) become the summonable heroes, re-graded onto star grades and each given a former life (#391, #357, #360); the splash house style carries over (#321, #291, #292). The working title is ***Hundred Floor Summoner***. The code name, the replacement for `HeroCat.*`, and the bundle id are chosen in #351; until then decision 30 stands.
128. **The pivot keeps the source's dark rules.** Owner decisions on issue #381: stress can kill a hero; synthesis and conversion consume (kill) the heroes fed into them; the manager NPC can execute a hero who disobeys orders; training services (P2, #380) carry a high death rate. Odds are never rigged per account (DIV-01), and accounts are not deleted for inactivity (DIV-07). The content rating target is 17+ / mature, and the age gate stays as built (#302 option 1). Owning specs: #359, #364, #376, #380.
129. **Divergences from the source are tracked in `specs/11-source-divergences.md`.** Owner decision on issue #382 (option B): a register with ids (`DIV-01`...) that the specs cite, a pointer at the top of the research doc, and a `CLAUDE.md` rule to update it in the same change.
Owner decisions on the hero model (#342), numbered after the specs/00 rewrite's agent defaults (section 12.3):

136. **Stress death is warned and stoppable.** Owner decision on #342: stress death ships in MVP. A hero dies of stress only after two warnings the player has seen (the `CRITICAL` alert, then the breakdown) and a `STRESS_GRACE_H` (24 h) countdown that starts only once the breakdown has been seen; one free `OrderRest` always saves the hero (03 section 4.4, DIV-20).
137. **Hidden talents are fixed per hero definition.** Owner decision on #342: every copy of a `HeroDef` has the same talents. The sheet shows how many there are, each reveal condition and its progress, and hides only the reward until it is revealed (03 section 2.5, DIV-10, DIV-11).
138. **Stat redistribution by focus preset.** Owner decision on #342: in MVP the player can change a hero's growth focus for gold, which re-places only the points earned from levels (03 section 1.3.2, DIV-09).
139. **Refusals follow fixed visible rules.** Owner decision on #342: refusals and strikes (P1) follow deterministic rules shown before assignment, never a chance roll (03 section 4.8, DIV-20).

### 12.3 Decisions made in the specs/00 rewrite (#341)

Agent defaults, open to the owner's veto in review (`CLAUDE.md` rule 1). Each is easy to change before the spec it touches is written.

130. **Pillar order.** Tower spine, then idle fights with lobby decisions, then heroes as people with real losses, then the deterministic core, honest monetization and live service (section 1). Derived from decisions 123, 126 and 128. HeroCat's pillars 1 and 2 ("always something to do", "many parallel sinks") are retired.
131. **Spec file map.** Specs 01, 02 and 07-10 keep their numbers. 03-06 are repurposed as heroes and growth, summons and gear, tower and battle, and lobby and meta, each renamed when it is rewritten (section 10). HeroCat's overview moves to `specs/archive/00-overview-herocat.md`, so existing citations still resolve.
132. **Scope tiers** as in section 2.1, with M0 matching #352. Owning issues may move rows.
133. **Heroes are individual instances** with a permanent `HeroUid`, not stacked copies, because each hero carries its own grade, level, statuses, inner life and death (section 5.1). Duplicate handling stays with #344.
134. **Away is the same game** (contract 8): parties keep fighting under the same rules while the player is away, deaths included, and the offline report shows every death. 05 and 06 may define a faster, deterministic catch-up that the bots prove equivalent.
135. **`GEM` replaces `DIAMOND`** as the hard currency's name, matching decision 126. Materials, potions and revive items are items, not currencies (section 5.2).

### 12.4 Decisions made in the hero model (#342)

Agent defaults from specs/03 sections 1-5, open to the owner's veto in review (`CLAUDE.md` rule 1).

140. **Frenzy is bounded.** A frenzied hero attacks only its hostile partner, for `STATUS_FRENZY_TICKS` (03 section 3.4, DIV-17).
141. **The opening-status forecast shows a band**, not the raw pressure number (03 section 3.3, DIV-15).
142. **No auto-rest setting in MVP.** Revisit with soft-launch data, possibly as a manager upgrade (#376).
143. **A memory never lowers a stat.** Its behaviour and inner-life effects may go either way (03 section 2.6, DIV-14).
144. **Claimed former lives are P1**, off in MVP content (03 section 2.6.3).
145. **The cast's rarity bands map to 1★-5★**, Common 1★ up to Mythic 5★; #391 has the final say (03 section 1.1).
146. **Low-grade mage concepts take a `CASTER` calling.** They arrive as `NOVICE`; `MAGE` stays summon-only for 3★ and up (03 section 1.4).
