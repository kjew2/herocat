# 03 - Heroes and Growth

This spec defines what a hero is and how one grows: the hero sheet and its stats, classes and roles (section 1); former lives, traits, hidden talents and memories (section 2); statuses in battle (section 3); inner life, meaning morale, stress, loyalty and bonds (section 4); and how all of these combine into one stat pipeline (section 5). Growth systems (levels, synthesis, promotion, skills and class change, section 6) are still to be written under #344. Where this spec and `00-overview.md` disagree, 00 wins. HeroCat's spec 03 is archived at `specs/archive/03-combat-and-progression-herocat.md`, and HeroCat citations such as "03 section 8.6" in code, tools and older specs resolve there.

Tiers covered: M0 (grades, the four stats, the sheet, automatic growth, classes as data and roles, the former life and its life trait, the status engine with Fear), MVP (redistribution, summon-only classes, the full trait model, seal slots, hidden talents, memories, the other statuses, morale, stress, breakdown and stress death, bonds), P1 (loyalty, gifts, refusals and strikes, the inner-life side of duels, Surge, dual mind, claimed former lives, crafting trades), P2 (gift requests, moving traits between heroes).

Mechanics come from the in-story game catalogued in `docs/research/pick-me-up-infinite-gacha.md` (cited as "research §N"): mechanics only, never names, characters, story, art or text (decision 121, contract 15). Every deliberate difference is a `specs/11` row, cited here by id. Issues: #342 (parent) with #357 (section 1), #360 (section 2), #358 (section 3) and #359 (section 4).

Depends on: 00 (names, constants, contracts), 02 (the `Hero` save fields, #384), 04 (gear and consumables, #368; summons, #361), 05 (the battle loop, formation, death and revival: #370, #371, #373), 06 (facilities, the manager, research, parties and the offline report: #375-#379), 07 (screens, #385) and 09 (content tables, #387).

## 1. Hero sheet, stats, growth rate and classes

What a hero *is* in numbers: its star grade, its four stats and how they grow, its class and the formation role that class gives it, and the stat window (`SCR_HERO`) that shows all of it. Source: research §3 and §9.1. Issue #357, parent #342.

Tiers: grades, stats, the sheet, automatic growth, classes as data and roles are **M0**. Summon-only classes and the hero-awareness flag are **MVP**. Stat redistribution is **MVP**. Class *change* is #367 (MVP), levels and EXP are #363, promotion is #365, synthesis is #364, skills are #366, statuses are section 3, inner life is section 4, former lives and traits are section 2. Battle formulas are 05's (#371).

Calls made here as agent defaults (`CLAUDE.md` rule 1, vetoable in review): four stats with AGI covering the source's "AGI or DEX"; the stamina stat is called `VIT` so it can't be confused with battle HP; growth points are allocated automatically and monotonically; no per-instance random stats; growth rate shown after the first level-up.

### 1.1 Star grades (M0)

`Grade` is an integer `GRADE_MIN..GRADE_MAX` (1★-7★), stored on the `Hero` (section 5.1 of 00).

| Grade | Can be pulled | Arrives with | Notes |
|---|---|---|---|
| 1★, 2★ | Yes | `NOVICE` class | Lowest stats. Up to `NOVICE_MAX_GRADE` a `HeroDef` must arrive as `NOVICE` (research §3: "low grades start as Novice"). |
| 3★-5★ | Yes | A non-Novice class | `SUMMON_GRADE_MAX = 5` (research §3). Which banner gives which grades is #361's. |
| 6★, 7★ | **No** | - | Reached only by promotion (#365). No `HeroDef` has `base_grade > SUMMON_GRADE_MAX`; the content validator rejects it. |

- A `HeroDef` has exactly one `base_grade`: the grade every copy of it is summoned at. A `Hero`'s `grade` starts there and only `Promotion` raises it (#365). Nothing lowers it.
- Per-grade numbers live in one table, `grades` (section 1.7). This section adds `growth_bonus`; #363 adds `level_cap`, #366 `skill_level_cap`, #365 the promotion columns.
- Seed for #391 (decision 127): the reused cast's five rarity bands map onto the five pullable grades, Common 1★, Rare 2★, Epic 3★, Legendary 4★, Mythic 5★. It keeps "the most glamorous designs at the top" and gives the 1★ band the most heroes. #391 owns the final mapping.

### 1.2 Stats (M0)

Four stats. The set is a fixed `StatId` enum in this order; the `stats` table has exactly one row per value. Adding or removing a stat is a save-schema change (02, #384), not a content change.

```csharp
public enum StatId : byte { STR = 0, INT = 1, VIT = 2, AGI = 3 }   // order is part of the save schema
```

| Stat | Display | What it feeds in battle (05 owns the formulas, #371) |
|---|---|---|
| `STR` | Strength | Physical attack power (swords, spears, bows), the physical part of skills that scale on it. |
| `INT` | Intelligence | Magic attack and healing power, mana pool and cast speed for classes whose `attack_stat` is `INT`. |
| `VIT` | Stamina | Max battle HP (`MaxHp = HP_BASE + VIT * HP_PER_VIT` in 05) and physical toughness. Called `VIT` in code so the stat is never mistaken for the HP pool. |
| `AGI` | Agility | Action speed (attack and cast interval), evasion and accuracy. Covers the source's "Agility or Dexterity" (research §3). |

Rules for 05 (contracts, not formulas):

- **A stat is not a damage number.** Damage is a stat times a skill coefficient (research §9.1: "technique scales it"). Skills (#366) carry the coefficients; a class's basic attack uses its `attack_stat`.
- Each class names its `attack_stat` (`STR` or `INT`). 05 reads it; no class logic is hard-coded.
- Statuses, traits and inner-life bonuses act on stats, never on damage directly, unless their own spec says otherwise (section 3, section 4, section 2).

**Numbers.** Stat values are counted integers (contract 10). Percent modifiers are integers with 100 = no change. All arithmetic uses `long` intermediates and truncating division on non-negative values, clamped at 0. Derived battle values (HP, damage) become `double` in 05.

#### 1.2.1 Layers, current/max and total stat points

Each stat is built in three layers. The sheet shows **current/max** (research §3).

| Layer | Made of | Where it lives |
|---|---|---|
| **Core** (permanent) | `HeroDef` base + promotion gains (#365) + growth points allocated (1.3) + permanent flat bonuses (synthesis #364, events) | Base and promotion gains derived from content; allocation and bonuses saved on the `Hero` |
| **Max** (the hero as equipped) | Core + gear flat (#368) + trait flat (section 2), times class % plus trait % | Derived, never saved |
| **Current** (right now, in battle) | Max times the inner-life multiplier (section 4), then the status multiplier (section 3) | Derived, never saved |

The full formula, with number formats and clamps, is section 5. The functions below are the lobby view.

```csharp
int Core(Hero h, StatId s, ContentDb db) {
    var def = db.HeroDefs[h.HeroDefId];
    return def.Base[s]
         + db.PromotionStatGain(def, h.Grade, s)   // #365; 0 until #365 defines it
         + h.GrowthAlloc[s]                         // 1.3
         + h.StatBonus[s];                          // #364 and other permanent grants
}

int Max(Hero h, StatId s, ContentDb db) {
    var t = Traits.Aggregate(h, db);                                          // section 2.3.4
    long flat  = Core(h, s, db) + GearFlat(h, s, db) + t.Flat[s];             // #368, section 2
    long pctBp = db.Classes[h.ClassId].Pct[s] * (BASIS_POINTS / STAT_PCT_BASE) + t.PctBp[s];
    return (int)Math.Max(0, flat * Math.Max(0, pctBp) / BASIS_POINTS);
}

// Out of battle there is no inner-life or status layer, so current == max on the lobby sheet.
// In battle, current is section 5's CurrentInBattle.

int TotalStatPoints(Hero h, ContentDb db) => Sum over s of Core(h, s, db);
```

- Out of battle, current equals max: every status ends with its battle (section 3.8) and the lobby heals at once (research §8). In battle, buffs (Berserk, Surge) can push current **above** max; the battle view shows it highlighted (07).
- Inner-life bonuses, pair bonuses included (section 4.6), apply only inside a battle's party context, so they appear in current during a fight, not on the lobby sheet.
- **Total stat points** = the sum of the four core stats (research §9.1). Gear, class %, traits and statuses are excluded, so it measures the hero, not the loadout. #364 (synthesis transfer) and 08's plausibility checks read it.

#### 1.2.2 Hero power (M0)

00 section 7 asks 03 for a rated power value. This section defines the stat term; #366 and #368 add skill and gear terms to the same sum, and 05 may retune the weights.

```csharp
long HeroPower(Hero h, ContentDb db) =>
    Sum over s of (long)Max(h, s, db) * db.Stats[s].PowerWeight     // seed weight 1 each
    + SkillPowerTerm(h, db)                                           // #366, 0 until defined
    ;                                                                 // gear is already inside Max
```

Statuses are not in power: it is the planning value shown in the party builder, not a live reading.

### 1.3 Growth rate (M0; redistribution MVP)

Each level-up gives a hero a fixed number of growth points (research §3: about 4 for an ordinary 1★, 5 for a normal 4★).

```csharp
int GrowthPerLevel(Hero h, ContentDb db) =>
    db.HeroDefs[h.HeroDefId].GrowthRate + db.Grades[h.Grade].GrowthBonus;   // read at the moment of the level-up
```

- `HeroDef.growth_rate` is per hero concept. The seed default by base grade is in `grades.growth_default` (1★ 4, 2★ 4, 3★ 5, 4★ 5, 5★ 6); #391 starts from it, and a hero with hidden potential gets a higher value (research §3, section 2).
- `grades.growth_bonus` lets promotion raise growth. Seed 0 at every grade; a tuning lever for #348 and #365.
- The level-up itself (EXP, caps) is #363's. On each level gained it calls `GrantGrowthPoints`, which adds to `Hero.GrowthEarned` and allocates the new points.

#### 1.3.1 Automatic allocation (M0)

The player does not place points by hand. New points go to stats by the hero's **focus**, a row of four integer weights in `stat_focus`. A hero's focus is `HeroDef.focus_id` if set, else its class's `default_focus`.

Allocation is incremental, one point at a time, to the stat furthest below its weighted share. It is deterministic, uses no RNG, and never lowers a stat (it is house-monotone, so it avoids the quota paradox where a stat drops as the total rises).

```csharp
void AllocatePoints(int[] alloc, int points, StatFocus f) {          // f.W[s] >= 0, Sum(f.W) > 0 (validator)
    long wSum = Sum(f.W);
    for (int p = 0; p < points; p++) {
        long total = Sum(alloc) + 1;
        StatId best = STR; long bestGap = long.MinValue;
        foreach (StatId s in StatOrder) {                           // STR, INT, VIT, AGI: ties go to the earlier stat
            if (f.W[s] == 0) continue;
            long gap = f.W[s] * total - (long)alloc[s] * wSum;      // how far s is below its share
            if (gap > bestGap) { bestGap = gap; best = s; }
        }
        alloc[best]++;
    }
}
```

On a class change (#367) the hero's focus becomes the new class's default; points already allocated stay where they are and only future points follow the new focus.

#### 1.3.2 Redistribution (MVP)

The source lets points be redistributed, so a stat can fall (research §3). We keep redistribution but make it a lobby decision instead of something that happens on its own at level-up (DIV-09).

- `RedistributeStats(HeroUid, FocusId)` clears `GrowthAlloc` and re-runs `AllocatePoints(alloc, GrowthEarned, focus)` from zero. Only growth points move. Base, promotion gains and `StatBonus` never do. A stat can fall.
- The chosen focus must be in the class's `allowed_focus` list. The hero keeps it as `Hero.FocusId` for future level-ups until the next redistribution or class change.
- Cost: `STAT_REDISTRIBUTE_GOLD_PER_LEVEL * level` gold, reason `STAT_REDISTRIBUTE` (contract 6; see the notes).
- Rejects (closed list): `HERO_NOT_FOUND`, `HERO_DEAD`, `HERO_ON_ASSIGNMENT` (its party is fighting), `FOCUS_NOT_ALLOWED`, `NO_CHANGE` (same focus and the same resulting allocation), `NOT_ENOUGH_GOLD`.
- Emits `StatsRedistributed { HeroUid, FocusId, Before[4], After[4] }`.

#### 1.3.3 Growth rate visibility (M0)

The sheet shows the growth rate ("+N per level") once the hero has gained at least one level (`GrowthEarned > 0`) and "?" before that. The number is fixed per `HeroDef` and visible in the first level-up anyway, so this is a deterministic reveal by use (research §3: try a 1★ before synthesizing it; DIV-10). Other hidden talents follow section 2's reveal rules.

### 1.4 Classes (M0 as data; class change MVP in #367)

A hero always has exactly one class (`Hero.ClassId`). The class sets the hero's formation **role** (1.5), its `attack_stat`, a percent modifier per stat and its default growth focus. Class trees and the change itself are #367's.

**How a class is obtained** (`classes.obtain`):

| Value | Meaning |
|---|---|
| `DEFAULT` | `NOVICE` only. Every `HeroDef` with `base_grade <= NOVICE_MAX_GRADE` arrives with it. |
| `ANY` | Can arrive with a summon (on a `HeroDef` at 3★+) or be reached by class change. |
| `SUMMON_ONLY` | Only a `HeroDef` can carry it; class change can never reach it (research §3: the source's mage is pull-only). MVP. |
| `CHANGE_ONLY` | Reached only by class change (advanced classes); no `HeroDef` may start with it. |

Validator rules: `base_grade <= NOVICE_MAX_GRADE` requires `class_id = NOVICE`; `base_grade > NOVICE_MAX_GRADE` forbids `NOVICE`; a `HeroDef` may not start in a `CHANGE_ONLY` class; #367 must reject a change into `SUMMON_ONLY`.

**Seed class list** (our names; #367 builds the tree, #391 assigns heroes). Percents are seeds for #348.

| `id` | `role` | `attack_stat` | `obtain` | `default_focus` | STR / INT / VIT / AGI % |
|---|---|---|---|---|---|
| `NOVICE` | `SHORT` | `STR` | `DEFAULT` | `BALANCED` | 100 / 100 / 100 / 100 |
| `WARRIOR` | `SHORT` | `STR` | `ANY` | `POWER` | 110 / 100 / 105 / 100 |
| `GUARDIAN` | `SHORT` | `STR` | `ANY` | `STAMINA` | 100 / 100 / 115 / 95 |
| `SCOUT` | `SHORT` | `STR` | `ANY` | `SPEED` | 100 / 100 / 95 / 115 |
| `LANCER` | `MID` | `STR` | `CHANGE_ONLY` | `POWER` | 110 / 100 / 100 / 105 |
| `RANGER` | `LONG` | `STR` | `ANY` | `SPEED` | 105 / 100 / 95 / 110 |
| `CASTER` | `LONG` | `INT` | `ANY` | `MIND` | 95 / 110 / 95 / 100 |
| `MAGE` | `LONG` | `INT` | `SUMMON_ONLY` | `MIND` | 90 / 125 / 90 / 100 |
| `CLERIC` | `SUPPORT` | `INT` | `ANY` | `MIND` | 95 / 110 / 105 / 95 |
| `BARD` | `SUPPORT` | `INT` | `ANY` | `BALANCED` | 100 / 105 / 100 / 105 |

The cast's concept roles (`art/concepts/companions.md`) map onto these as follows: Warrior to `WARRIOR`, Tank to `GUARDIAN`, Mage to `MAGE` or `CASTER`, Caster to `CASTER`, Cleric to `CLERIC`, Bard to `BARD`. Paladin, Death Knight and Sage become `CHANGE_ONLY` or `ANY` rows that #367 adds; Merchant becomes a former life (section 2) rather than a class. For 1★ and 2★ heroes the concept role is a *calling* (former life and class-change path), not the starting class, because they arrive as `NOVICE`.

### 1.5 Roles and ranges (M0)

The formation axis 05 uses (research §9.5: short, mid and long range plus support; a swordsman can't fill a spear slot). Role comes only from the class, so a class change can change it.

| `Role` | Meaning | Typical classes |
|---|---|---|
| `SHORT` | Front line, melee reach | `NOVICE`, `WARRIOR`, `GUARDIAN`, `SCOUT` |
| `MID` | Second line, reach weapons and short casts | `LANCER` |
| `LONG` | Back line, bows and spells | `RANGER`, `CASTER`, `MAGE` |
| `SUPPORT` | Back line, heals and buffs first, attacks second | `CLERIC`, `BARD` |

05 owns the slot layout, the range distances (`roles.range`, set in #371), whether a slot accepts only its own role, and the cost of a mismatch. This section only guarantees that every hero has exactly one role, readable as `Role RoleOf(Hero h) => db.Classes[h.ClassId].Role`.

### 1.6 The stat window (`SCR_HERO`, M0)

The player, the summoner, always sees the whole sheet. Nothing on it is gated by research; the source's research gate applies to the heroes' own view (section 1.6.1). Layout and states are 07's (#385).

| Field | Shown as | Source of the value | Tier |
|---|---|---|---|
| Portrait, name, stars | Name key, `grade` stars | `HeroDef`, `Hero.Grade` | M0 |
| Level, EXP | `Lv n`, `exp / expToNext`, a "promote to continue" note at the cap | #363 | M0 |
| Class and role | Class name and role icon | 1.4, 1.5 | M0 |
| Stats | `current / max` for each of STR, INT, VIT, AGI | 1.2.1 | M0 |
| Total stat points | One number | 1.2.1 | M0 |
| Growth rate | `+N per level` or `?` | 1.3.3 | M0 |
| Power | One number | 1.2.2 | M0 |
| Former life | Name and aptitude line | section 2 | M0 |
| Skills | Each with its level and tier | #366 | MVP (M0 shows starting skills only) |
| Traits and seal slots | Graded traits, `used / slots` | section 2 | MVP |
| Statuses | Active statuses with time left | section 3 | Fear M0 |
| Inner life | Morale, stress and (P1) loyalty as 0-100 with bands, bonds, any breakdown countdown (DIV-19) | Section 4.10 | MVP |
| Gear | Equipped items | #368 | MVP |
| Life state, favourite | `DEAD` greyed out; favourite toggle | 05 #373; `ToggleFavourite` | M0 |

#### 1.6.1 Heroes' own view (MVP)

In the source, heroes can't see their own windows or the mission objective until research unlocks it, and a party died for not knowing its objective (research §3). We keep that as a **hero** rule, not a player rule:

- `HeroAwareness` is an account-wide integer level held by the research state (#377), `0..HERO_AWARENESS_MAX`. Level 0 means heroes don't know the objective or their own numbers. Level 1, the first research step, means they see the objective and part of their own windows.
- 05 and #372 define what the level changes in hero AI (for example, whether heroes play to the objective, use potions at a low-HP threshold, or know a timer). In M0 the level is 0 and has no battle effect until 05 gives it one.
- It never hides anything from the player.

### 1.7 Content tables

All names below are 09's to register (#387). They replace HeroCat's `companions`, `rarities` and `stats` tables. Presentation columns (`name_key`, `desc_key`, `icon`, art ids) follow 09's conventions and are listed only where new.

**`hero_defs`**: one row per hero concept (decision 127).

| Column | Type | Notes |
|---|---|---|
| `id` | string | `HeroDefId`, stable. |
| `base_grade` | int | `1..SUMMON_GRADE_MAX`. |
| `class_id` | enum | 1.4 validator rules. |
| `focus_id` | enum, optional | Blank means the class's `default_focus`. |
| `base_str`, `base_int`, `base_vit`, `base_agi` | int >= 0 | Core stats at `base_grade` and Lv1. |
| `growth_rate` | int >= 1 | Points per level before `growth_bonus`. |
| `summonable` | bool | False keeps a row out of every banner pool (reward and story heroes). Pools themselves are #361's. |
| `former_life_id`, trait and talent columns | | section 2. |
| `start_skills` | list | #366. |
| `tier`, `name_key`, `desc_key`, `icon`, `portrait`, `sprite`, `splash` | | Art per #349. |

**`grades`**: one row per grade `1..7`. Columns: `grade`, `growth_default` (validator and authoring hint only, never read by the sim), `growth_bonus`, `tier`; #363, #365 and #366 add their own columns.

**`stats`**: one row per `StatId`. Columns: `id`, `power_weight` (int), `power_tag` (bool; true for `STR` and `INT`, the stats Berserk raises, section 3.1), `tier`, `name_key`, `desc_key`, `icon`.

**`classes`**: columns `id`, `role`, `attack_stat`, `obtain`, `default_focus`, `allowed_focus` (list), `pct_str`, `pct_int`, `pct_vit`, `pct_agi` (int, 100 = none), `tier`, `name_key`, `desc_key`, `icon`; #367 adds tree and requirement columns.

**`roles`**: columns `id` (`SHORT`, `MID`, `LONG`, `SUPPORT`), `range` (set in #371), `tier`, `name_key`, `icon`.

**`stat_focus`**: columns `id`, `w_str`, `w_int`, `w_vit`, `w_agi` (int >= 0, sum > 0), `tier`, `name_key`. Seed rows:

| `id` | STR / INT / VIT / AGI |
|---|---|
| `BALANCED` | 1 / 1 / 1 / 1 |
| `POWER` | 3 / 0 / 2 / 1 |
| `STAMINA` | 2 / 0 / 3 / 1 |
| `SPEED` | 2 / 0 / 1 / 3 |
| `MIND` | 0 / 3 / 1 / 2 |

**Seed stat lines** for #391, from the source's sample sheets (research §3). Our archetype names; cells marked `*` are not given by the source and are seeds.

| Archetype | Grade | Class | STR | INT | VIT | AGI | Total | Growth |
|---|---|---|---|---|---|---|---|---|
| Fresh recruit | 1★ | `NOVICE` | 14 | 10 | 12 | 10* | 46 | 4 |
| Nimble striker | 3★ | `SCOUT` | 13 | 10 | 12* | 18* | 53 | 5 |
| Born caster | 3★ | `MAGE` | 7 | 36 | 9* | 11* | 63 | 5 |
| Shield veteran | 4★ | `GUARDIAN` | 19 | 10 | 18 | 22 | 69 | 5 |

**Constants**: rows in the `constants` table (section "Constants introduced").

### 1.8 `Hero` save fields (shape only; 02 owns the schema, #384)

| Field | Type | Owner | Notes |
|---|---|---|---|
| `Uid` | `HeroUid` | 00 | Never reused. |
| `HeroDefId` | string | 00 | |
| `Grade` | byte | this section, #365 | |
| `Level`, `Exp` | int, double | #363 | |
| `ClassId` | enum | this section, #367 | |
| `FocusId` | enum, nullable | this section | Null means the derived default (`HeroDef.focus_id`, else the class's). Set by `RedistributeStats`, cleared by class change. |
| `GrowthEarned` | int | this section | All growth points ever granted. Kept so content retunes never rewrite history. |
| `GrowthAlloc` | int[4] | this section | Indexed by `StatId`. `Sum(GrowthAlloc) == GrowthEarned` always. |
| `StatBonus` | int[4] | #364 and others | Permanent flat grants. Never touched by redistribution. |
| `Skills`, `Traits`, `Statuses`, inner-life block, `Gear`, `Favourite`, `LifeState` | | #366, section 2, section 3, section 4, #368, 06, #373 | |

Current, max, total stat points, power and role are derived, never saved. Base stats and promotion gains are read from content, so a balance patch to a `HeroDef` reaches existing heroes. Growth already earned does not change.

Duplicates: every copy of a `HeroDef` is created with the same stats. There is no per-instance stat roll at summon time, so a pull is worth what the banner shows. Whether duplicates coexist is #344's call.

### 1.9 Commands and events

| Command | Tier | Effect | Event |
|---|---|---|---|
| `RedistributeStats(heroUid, focusId)` | MVP | 1.3.2 | `StatsRedistributed` |
| (internal, called by #363's level-up) `GrantGrowthPoints(hero, n)` | M0 | Adds `n` to `GrowthEarned` and allocates them | Folded into `HeroLeveledUp`, which carries `PointsGained[4]` |

## 2. Former lives, traits, hidden talents and memories

What makes two heroes of the same grade different. Every hero was called from a former ordinary life (decision 124, DIV-08). That life gives them starting aptitudes and trades. They can carry graded **traits** in **seal slots**. Low grades can hide **talents** that only show up through use. Each promotion can **restore a memory** that changes their traits or behaviour. Mechanics come from research §3 (former lives, hidden talents), §5 (promotion restores memories), §9.1 (seal slots and graded traits) and §11 (#354 evidence). The names, occupations and fiction here are our own (decision 121).

Tiers:

| Feature | Tier |
|---|---|
| `former_life_id` on every `HeroDef`, shown on `SCR_HERO` and in the summon reveal; the life trait's `STAT_FLAT` and `STAT_PCT` effects apply | **M0** |
| The full `Trait` model (every effect kind in 2.3), seal slots, hidden talents, memory restoration | **MVP** |
| `TRAINING_RATE` effects (read by Skills, #366) and `LOBBY_WORK` effects (read by Facilities, #375) | **MVP** |
| `CRAFT_TRADE` effects (read by Crafting, #369) | **P1** |
| A claimed former life that differs from the true one and is exposed by a memory (2.6.3) | **P1** |
| Moving traits between heroes (synthesis or seal items) | **P2**, not designed |

**This section rolls no dice.** Former lives, talents, seal traits and memories are all authored per `HeroDef` and applied by fixed rules. No RNG stream is drawn, so a hero's sheet is fully determined by its `HeroDefId`, its grade and its recorded history. That is the honesty rule (pillar 5): a banner's published odds pick the hero, and nothing hidden is rolled on the copy the player receives.

Readers outside this spec: promotion (#365), skills and training (#366), battle and hero AI (05, #371), facilities (06, #375), crafting (04, #369) and the roster content (#391).

### 2.1 Data model overview

```
HeroDef ──former_life_id──> FormerLife ──life_trait_id──> TraitDef ──> trait_effects (per level)
   │                                                           ^
   ├──> hero_talents[0..TALENTS_MAX_PER_HERO)  ── reward ──────┘ (or a SkillDef, #366)
   ├──> hero_seal_traits[slot] ── trait_id ────────────────────┘
   └──> memory_scenes[to_grade] ──> memory_effects

Hero (owned) ── traits: List<HeroTrait>, talentProgress[], talentRevealedMask,
                memoryRestoredMask, behaviourFlags
```

Each source of advantage is a trait with a `Source` tag, so one aggregation path (2.3.4) turns all of them into stat modifiers. Only the rules for *how a hero gets* a trait differ by source.

| `TraitSource` | Comes from | Slot | Tier |
|---|---|---|---|
| `LIFE` | The hero's former life. Exactly one, always held from acquisition. | None (not in a seal slot) | M0 |
| `SEAL` | `hero_seal_traits`, opened by grade | One seal slot each | MVP |
| `TALENT` | A revealed hidden talent | None | MVP |
| `MEMORY` | A `GRANT_TRAIT` memory effect | None | MVP |

### 2.2 Former lives (M0)

Every `HeroDef` names exactly one former life. A former life is a content row, not a code enum, so #391 can add occupations without a code change. A former life sets three things, all through its **life trait**:

1. **Starting aptitudes:** a fixed budget of flat stat points (`STAT_FLAT`), the same total for every former life (`FORMER_LIFE_START_POINTS`), spread differently. A farmer leans to VIT and a scribe to INT, but neither gets more in total. This keeps former life a matter of flavour and fit, not a ranking. Applies from M0.
2. **Skill aptitudes:** `TRAINING_RATE` per skill tag. The source's hunter is good with a bow (research §3, §11). Here that means bow skills train faster and the hero can start with a bow skill (the `HeroDef`'s skills, section 1/#366). MVP.
3. **Trades and lobby work:** `CRAFT_TRADE` (P1, #369) and `LOBBY_WORK` (MVP, #375). A smith can work the forge, and a cook raises the canteen's output. The research catalog's artisans (research §6) use these.

The former life shows on `SCR_HERO` (icon, name and one line of description) and on the summon reveal card. It is never hidden. A *claimed* former life is P1 (2.6.3).

#### 2.2.1 `former_lives` table

| Column | Type | Notes |
|---|---|---|
| `former_life_id` | string, UPPER_SNAKE | Primary key, e.g. `FARMER` |
| `name_key`, `desc_key` | loc key | Contract 11 |
| `icon_id` | string | Client asset id (#349) |
| `life_trait_id` | `traits.trait_id` | Must have `source = LIFE`. Its level-1 `STAT_FLAT` rows must sum to `FORMER_LIFE_START_POINTS`. |
| `sort_order` | int | Roster filter order |
| `bond_tags` | list | Tags for section 4.5's `bond_affinity` (two heroes from the same trade start warm) |

#### 2.2.2 Seed former lives (our own, generic)

The stats are section 1's: STR, INT, VIT, AGI. `hero_defs.base_*` exclude these points (section 5). Points are level-1 `STAT_FLAT` on the life trait (`FORMER_LIFE_START_POINTS = 4`). Skill tags are #366's and trades are #369's. The names below are proposals for those specs.

| `former_life_id` | STR | INT | VIT | AGI | `TRAINING_RATE` tags (+bp) | `CRAFT_TRADE` (P1) | `LOBBY_WORK` (MVP) |
|---|---|---|---|---|---|---|---|
| `FARMER` | 1 | 0 | 3 | 0 | `POLEARM` +2000 | — | `CANTEEN` +1000 |
| `HUNTER` | 0 | 0 | 1 | 3 | `BOW` +3000, `SCOUTING` +1500 | `LEATHER` 1 | — |
| `SMITH` | 3 | 0 | 1 | 0 | `HAMMER` +2000 | `SMITHING` 2 | `FORGE` +1500 |
| `CARPENTER` | 2 | 1 | 1 | 0 | `HAMMER` +1000 | `WOODWORK` 2 | `WORKSHOP` +1500 |
| `COOK` | 0 | 1 | 2 | 1 | `BLADE` +1000 | `COOKING` 2 | `CANTEEN` +2000 |
| `MERCHANT` | 0 | 3 | 0 | 1 | — | — | `SHOP` +2000 |
| `TOWN_GUARD` | 2 | 0 | 2 | 0 | `SHIELD` +2500, `POLEARM` +1000 | — | `GATE` +1000 |
| `SOLDIER` | 2 | 0 | 1 | 1 | `BLADE` +2000, `SHIELD` +1000 | — | `TRAINING_HALL` +1000 |
| `FISHER` | 1 | 0 | 2 | 1 | `POLEARM` +1500 | `COOKING` 1 | `CANTEEN` +1000 |
| `MINER` | 3 | 0 | 1 | 0 | `HAMMER` +1500 | `SMITHING` 1 | `STOREROOM` +1000 |
| `HERBALIST` | 0 | 2 | 1 | 1 | `HEALING` +2500 | `ALCHEMY` 2 | `INFIRMARY` +1500 |
| `SCRIBE` | 0 | 4 | 0 | 0 | `CASTING` +1500 | — | `RESEARCH_LAB` +2000 |
| `COURIER` | 0 | 1 | 0 | 3 | `SCOUTING` +2500 | — | — |
| `NURSE` | 0 | 2 | 2 | 0 | `HEALING` +3000 | — | `INFIRMARY` +2000 |
| `STREET_PERFORMER` | 0 | 1 | 0 | 3 | `BLADE` +1000 | — | `LODGING` +1000 (morale, section 4) |

Fifteen rows give every lobby facility (#375) at least one fitting occupation. #391 assigns them to the reused cast (decision 127) and may add more.

### 2.3 The `Trait` model (life-trait stats M0, the rest MVP)

A trait is a passive with a **grade** (how rare and strong it is, shown as a letter), a **level** (`1..maxLevel`) and a list of **effects** per level. In the source, a trait shows as "B+, Lv3" (research §9.1), and ours displays the same way.

#### 2.3.1 Trait grades

Grades are an ordered content table, so the letter scale can change without code. A trait's grade is a label, a sort key and a level cap. It does not compute effects: each level's effects are authored (2.3.3). The cap is the grade's `max_level`.

| `trait_grade_id` | `rank` | `label_key` | `max_level` |
|---|---|---|---|
| `D` | 0 | `trait_grade.d` | 2 |
| `C` | 1 | `trait_grade.c` | 3 |
| `C_PLUS` | 2 | `trait_grade.c_plus` | 3 |
| `B` | 3 | `trait_grade.b` | 4 |
| `B_PLUS` | 4 | `trait_grade.b_plus` | 4 |
| `A` | 5 | `trait_grade.a` | 5 |
| `A_PLUS` | 6 | `trait_grade.a_plus` | 5 |
| `S` | 7 | `trait_grade.s` | 5 |

`TRAIT_LEVEL_MAX` (5) is the largest `max_level` the validator accepts. Life traits are always grade `D` and level 1 at M0. Whether they can level is an open question (default no).

#### 2.3.2 `traits` table

| Column | Type | Notes |
|---|---|---|
| `trait_id` | string | Primary key |
| `name_key`, `desc_key` | loc key | |
| `icon_id` | string | |
| `trait_grade_id` | `trait_grades` | |
| `source` | `LIFE` / `SEAL` / `TALENT` / `MEMORY` | Where this trait may appear (validator) |
| `max_level` | int | `<= trait_grades.max_level` |
| `tags` | list | Tags other sections read: section 3's triggers (`TEMPER_BERSERK`, `TEMPER_COMPOSED`, `DUAL_MIND`, `HIDDEN_POWER`) and section 4.5's `bond_affinity` |

#### 2.3.3 `trait_effects` table and effect kinds

There is one row per effect per level. Each level lists its **full** effect set, not a delta from the level below, so a level is read on its own. The validator requires rows for every level `1..max_level`.

| Column | Type | Notes |
|---|---|---|
| `trait_id`, `level` | key | |
| `effect_kind` | enum below | |
| `target` | string | Meaning depends on the kind |
| `value` | int | Points, or basis points (bp, 10000 = 100%) |
| `condition_kind`, `condition_value` | enum, int | Only for `COND_STAT_PCT` |
| `duration_s` | int | Only for `COND_STAT_PCT`. Converted to ticks with `TICK_HZ`. |

Effect kinds form a closed enum. Each kind names the spec that reads it, and a kind with no reader yet is still loaded and shown, but has no effect.

| `effect_kind` | `target` | `value` | Read by | Tier |
|---|---|---|---|---|
| `STAT_FLAT` | stat id (section 1) | points | Stat pipeline, section 1 | M0 |
| `STAT_PCT` | stat id, or `ALL` | bp | Stat pipeline, section 1 | M0 |
| `GROWTH_POINTS` | stat id | extra points per level-up, applied from the level the trait is gained | Levels, #363 | MVP |
| `STATUS_RESIST` | status id or status group (section 3.1, e.g. `DREAD` holding `FEAR`, `PANIC`, `DESPAIR`) | bp; how it applies is section 5 | Statuses, section 3 | MVP |
| `COND_STAT_PCT` | stat id, or `ALL` | bp | Battle, #371 | MVP |
| `TRAINING_RATE` | skill tag (#366) | bp on skill EXP from training | Skills, #366 | MVP |
| `LOBBY_WORK` | facility role (#375) | bp on that facility's output while the hero works there | Facilities, #375 | MVP |
| `INNER_LIFE` | `MORALE_GAIN`, `STRESS_GAIN`, … (section 4) | bp | Inner life, section 4 | MVP |
| `CRAFT_TRADE` | trade id (#369) | trade level | Crafting, #369 | P1 |

`COND_STAT_PCT` conditions, all evaluated by 05 at the end of a battle tick, after damage:

| `condition_kind` | `condition_value` | Fires |
|---|---|---|
| `HP_BELOW_BP` | HP threshold in bp of max HP | When current HP is `> 0` and `<= threshold`. Once per battle. A hit that kills outright does not fire it. |
| `BATTLE_START` | — | On the battle's first tick |
| `ALLY_DIED` | — | The first time an ally in the same party dies, once per battle |

The source's example graded trait (research §9.1) maps onto these kinds: one `STATUS_RESIST` on the mental group, one `STATUS_RESIST` on `FEAR`, and a `COND_STAT_PCT ALL +2000` with `HP_BELOW_BP` and `duration_s = 60`. #391 writes our own traits with our own names. This is only a check that the model can express the source's.

#### 2.3.4 How traits apply to stats

Traits add one layer to the stat pipeline, whose order is section 5's. This section supplies the layer:

```csharp
// HeroCat.Sim. Integer maths only; BP = 10000 (content: BASIS_POINTS).
public readonly struct TraitModifiers {
    public readonly int[] Flat;      // per StatId, points
    public readonly int[] PctBp;     // per StatId, basis points, ALL already spread
    public readonly int[] GrowthPts; // per StatId, extra points per level
}

public static TraitModifiers Aggregate(Hero h, ContentDb db) {
    var m = TraitModifiers.Empty(db.StatCount);
    foreach (var t in h.Traits)                       // stored order: by Source, then TraitId (ordinal)
        foreach (var e in db.TraitEffects(t.TraitId, t.Level))
            switch (e.Kind) {
                case STAT_FLAT:     m.Flat[e.Stat]      += e.Value; break;
                case STAT_PCT:      AddPct(m, e.Target, e.Value, db); break; // ALL -> every stat
                case GROWTH_POINTS: m.GrowthPts[e.Stat] += e.Value; break;
                default: break;                       // other kinds are read by their own spec
            }
    for (int s = 0; s < db.StatCount; s++)
        m.PctBp[s] = Clamp(m.PctBp[s], -db.Const.TRAIT_STAT_PCT_CAP_BP, db.Const.TRAIT_STAT_PCT_CAP_BP);
    return m;
}

// Read by section 1.2.1's Max and section 5: Flat[s] is added to the flat sum, PctBp[s] to the
// class percentage (in bp). Integer division, rounding toward zero. GrowthPts[s] are added to
// StatBonus[s] at each level-up (section 5).
```

Stacking rules:

- A hero holds each `trait_id` at most once. A second grant of a trait the hero already holds keeps the higher level and emits `TraitLeveled` if the level rose.
- Effects of the same kind on the same target add up across traits. The sum of `STAT_PCT` is capped per stat at `±TRAIT_STAT_PCT_CAP_BP`. section 3 caps the sum of `STATUS_RESIST`, because tolerance is not immunity (research §5).
- A trait's `COND_STAT_PCT` buffs are a status-like timed modifier held by the battle (05). They stack with section 3's status multipliers the way 05 and section 3 define, not inside `Aggregate`.
- Traits stay with a hero through death and revival (#373). A hero consumed by synthesis or conversion takes its traits with it, unless #364 defines a transfer (P2; see Open questions).

### 2.4 Seal slots (MVP)

A hero has seal slots, shown as `open/capacity` (the source shows "1/1", research §9.1). Capacity is set by grade. Each slot's content is authored per `HeroDef`. A slot holds a `SEAL` trait from the hero's former life: something they were born with, trained into or carried away from that life. Examples include a lineage, a craft drilled since childhood or an old soldier's nerve. In our fiction these come from the hero's own ordinary life, never another world (DIV-08, DIV-12).

- **Capacity:** `SEAL_SLOTS[grade]`. Slot `i` is **open** when `i < SEAL_SLOTS[grade]` and the hero's `HeroDef` has a `hero_seal_traits` row for slot `i`. Opening a slot grants its trait at the row's `start_level` and emits `SealSlotOpened` and `TraitGained`.
- **Sealed:** a slot with a row but `i >= SEAL_SLOTS[grade]` shows as sealed, with the grade that opens it. Its trait is not shown (it reads "???") until the slot opens. A slot index with no row is not drawn at all, so the displayed capacity is `min(SEAL_SLOTS[grade], rows authored)`.
- **When slots open:** at acquisition, for every slot the starting grade already allows, and after each `Promote` that raises capacity. The open happens before memory effects, so a memory can level a trait that just opened (2.5).
- **No swapping in MVP.** The player cannot move, remove or replace seal traits. Their levels rise only through memory effects (2.5).

| Column of `hero_seal_traits` | Type | Notes |
|---|---|---|
| `hero_def_id`, `slot_index` | key | `slot_index < SEAL_SLOTS[GRADE_MAX]` |
| `trait_id` | `traits` | `source = SEAL` |
| `start_level` | int | `1..max_level` |

Seed `SEAL_SLOTS[g]`, for 1★ to 7★: `0, 1, 1, 1, 2, 2, 3`. A 1★ has none, so its first slot is a reason to promote it. Many heroes will have fewer rows than the 7★ capacity, and #391 decides per hero.

### 2.5 Hidden talents (MVP)

A hero can have up to `TALENTS_MAX_PER_HERO` talents, authored per `HeroDef` in `hero_talents`. On a hero whose `HeroDef` base grade is `<= TALENT_HIDDEN_MAX_GRADE`, talents start **hidden** and are revealed by use (research §3: "try a 1★ before synthesizing it"). On higher base grades, every talent is revealed at acquisition, because the source's higher grades arrive with their skills.

**Decision: talents are fixed per `HeroDef`, not rolled per copy.** Two heroes of the same `HeroDef` have the same talents with the same reveal conditions. This keeps the published odds the whole story: what a banner gives is the published hero, with nothing hidden rolled on top. The owner confirmed this (decision 137).

**Disclosure rule.** `SCR_HERO` shows the talent count and, for each hidden talent, its reveal condition with progress. It does not show the reward until the talent is revealed. For example:

```
Talents  1 / 3 revealed
  [Shield Instinct]  B  Lv1
  ???   Fight 5 battles          3 / 5
  ???   Reach 2★                 —
```

The banner's hero list and the summon reveal show only the count ("2 hidden talents"). No reward is shown in either place before it is revealed.

**A hidden talent has no effect.** Its reward applies only from the tick it is revealed. Revealing is the unlock. This makes it worth using a low grade before feeding it away, and the player sees exactly what still has to be done.

#### 2.5.1 `hero_talents` table

| Column | Type | Notes |
|---|---|---|
| `hero_def_id`, `ordinal` | key | `ordinal < TALENTS_MAX_PER_HERO`. Display order. |
| `reveal_kind` | enum below | |
| `reveal_target` | string | Optional. Depends on `reveal_kind`. |
| `reveal_count` | int | `>= 1` |
| `reward_kind` | `TRAIT` / `SKILL` | |
| `reward_id` | `traits.trait_id` (`source = TALENT`) or a skill id (#366) | |
| `reward_level` | int | Trait level or skill level |

| `reveal_kind` | Counter rises by 1 when… | `reveal_target` |
|---|---|---|
| `BATTLES_FOUGHT` | The hero is alive at the end of a battle it started in (05) | — |
| `FLOORS_CLEARED` | The hero's party clears a floor with the hero alive | — |
| `STATUS_ENDURED` | The hero is alive at the end of a battle in which it had the status | status id (section 3) |
| `TRAINING_SESSIONS` | The hero completes a training session (#366) | Optional skill tag |
| `LEVEL_REACHED` | Set to the hero's level (not incremented) | — |
| `GRADE_REACHED` | Set to the hero's grade | — |

#### 2.5.2 Reveal logic

```csharp
// Runs inside Apply/Advance at the event points above. Pure, no RNG.
void OnTalentCounter(Hero h, RevealKind kind, string target, int newValueOrDelta, bool isSet) {
    var defs = db.HeroTalents(h.HeroDefId);
    for (int i = 0; i < defs.Length; i++) {               // ordinal order
        var d = defs[i];
        if (h.TalentRevealed(i) || d.RevealKind != kind) continue;
        if (d.RevealTarget != null && d.RevealTarget != target) continue;
        h.TalentProgress[i] = isSet ? Max(h.TalentProgress[i], newValueOrDelta)
                                    : SatAdd(h.TalentProgress[i], newValueOrDelta, d.RevealCount);
        if (h.TalentProgress[i] >= d.RevealCount) Reveal(h, i, d);
    }
}

void Reveal(Hero h, int i, TalentDef d) {
    h.SetTalentRevealed(i);
    if (d.RewardKind == TRAIT) GrantTrait(h, d.RewardId, d.RewardLevel, TraitSource.TALENT);
    else                       Skills.Grant(h, d.RewardId, d.RewardLevel);   // #366
    Emit(new TalentRevealed(h.Uid, i, d.RewardKind, d.RewardId));
}
```

- Counters run while the player is away, under the same rules (contract 8). The offline report lists talents revealed while away.
- A talent revealed mid-battle applies its stat effects from the next battle. A battle reads `TraitModifiers` once, when it starts (05). Revealing never changes a fight in progress.
- A dead hero's counters freeze. They resume after revival.

### 2.6 Memory restoration (MVP)

Each successful promotion can restore a memory of the hero's former life (research §5). In our design this is a mechanic hook: an authored row per `HeroDef` per target grade, holding a scene id for the client and a list of effects for the sim. The scene text and art are #391's and #349's content, written in our own fiction. This spec writes none of it.

#### 2.6.1 Tables

| `memory_scenes` column | Type | Notes |
|---|---|---|
| `hero_def_id`, `to_grade` | key | `to_grade` in `2..GRADE_MAX`. At most one row per pair. |
| `cutscene_id` | string | Client presentation id (07, #349). Its loc keys hang off it. |
| `title_key` | loc key | The memory's name on the hero's history tab |

| `memory_effects` column | Type | Notes |
|---|---|---|
| `hero_def_id`, `to_grade`, `ordinal` | key | Applied in `ordinal` order |
| `effect_kind` | `TRAIT_LEVEL` / `GRANT_TRAIT` / `SET_BEHAVIOUR` / `CLEAR_BEHAVIOUR` / `INNER_LIFE` / `REVEAL_TRUE_LIFE` (P1) | |
| `target` | string | trait id, behaviour id or inner-life value (section 4) |
| `value` | int | Levels added, trait level, or an inner-life delta |

`behaviours` is a content table (`behaviour_id`, `desc_key`, `read_by`). A behaviour is a flag on the hero that another spec reads. Examples: a hero-AI tendency (05, #371), such as guarding casters or keeping to the back line, or an inner-life modifier (section 4), such as making a refusal less likely. This spec only stores the flags and sets or clears them. It defines no behaviours.

#### 2.6.2 Rules

```csharp
// Called by #365's Promote handler after a successful promotion to grade g, and at acquisition.
void RestoreMemories(Hero h, int fromGradeExclusive, int toGradeInclusive, bool atAcquisition) {
    OpenSealSlots(h);                                    // 2.4, for the new grade
    for (int g = fromGradeExclusive + 1; g <= toGradeInclusive; g++) {
        if (h.MemoryRestored(g)) continue;              // once per hero per grade
        var scene = db.MemoryScene(h.HeroDefId, g);
        if (scene == null) continue;
        foreach (var e in db.MemoryEffects(h.HeroDefId, g)) ApplyMemoryEffect(h, e);
        h.SetMemoryRestored(g);
        Emit(new MemoryRestored(h.Uid, g, scene.CutsceneId, atAcquisition));
    }
    OnTalentCounter(h, GRADE_REACHED, null, h.Grade, isSet: true);
}
```

- **Once per hero per grade.** `memoryRestoredMask` has one bit per grade, so a memory never applies twice. No mechanic lowers a grade.
- **At acquisition**, memories for grades up to the hero's starting grade apply silently (`atAcquisition = true`). The client adds them to the history tab without playing them. A pulled 4★ arrives already holding its 2★-4★ memories.
- **On promotion**, the client plays `cutscene_id` after `SCR_PROMOTION`'s result. It can be skipped and replayed from the history tab. Whether a scene has been seen is presentation state in `ClientPrefs`, not sim state.
- **Disclosure.** `SCR_PROMOTION` says "restores a memory" when a scene row exists for the target grade. The sheet shows the effects once they apply. Memory effects never lower a stat. `INNER_LIFE` and behaviour effects may be negative (decision 143).
- A failed promotion (#365) restores nothing.

#### 2.6.3 Claimed former lives (P1)

In the source, heroes sometimes lie about their past (research §11). P1 adds an optional `claimed_former_life_id` on `HeroDef`. While it is set, the sheet shows and applies the claimed life's trait. A `REVEAL_TRUE_LIFE` memory effect swaps in the true life and its trait. The sheet marks the swap ("former life revealed"). MVP content leaves the column empty.

### 2.7 State, commands and events

The hero fields below are listed for 02's `GameState` (#384), which owns names and shapes.

| Field on `Hero` | Type | Notes |
|---|---|---|
| `traits` | `List<HeroTrait { TraitId, Level, Source, SlotIndex? }>` | Stored order: `Source`, then `TraitId` ordinal. The life trait is always present. |
| `talentProgress` | `int[TALENTS_MAX_PER_HERO]` | Saturates at `reveal_count` |
| `talentRevealedMask` | byte | |
| `memoryRestoredMask` | byte | Bit `g` set when grade `g`'s memory has applied |
| `behaviourFlags` | `SortedSet<string>` | Behaviour ids |

Seal slot open state is not stored. It is derived from `grade` and `hero_seal_traits` (2.4).

There are **no new commands**: reveals, slot openings and memories all follow from other commands (`Promote`, training, battles) and from acquisition. New events, to add to 00 section 5.1:

| Event | Payload |
|---|---|
| `TraitGained` | `HeroUid`, `TraitId`, `Level`, `Source` |
| `TraitLeveled` | `HeroUid`, `TraitId`, `FromLevel`, `ToLevel` |
| `SealSlotOpened` | `HeroUid`, `SlotIndex`, `TraitId` |
| `TalentRevealed` | `HeroUid`, `Ordinal`, `RewardKind`, `RewardId` |
| `MemoryRestored` | `HeroUid`, `Grade`, `CutsceneId`, `AtAcquisition` |

### 2.8 Content validation (09)

- Every `HeroDef` has a valid `former_life_id`. Every life trait has `source = LIFE`, grade `D` and `max_level = 1` in MVP. The sum of its level-1 `STAT_FLAT` rows equals `FORMER_LIFE_START_POINTS`, and it has no negative `STAT_FLAT`.
- Every `traits.max_level <= trait_grades[grade].max_level <= TRAIT_LEVEL_MAX`. `trait_effects` has at least one row for each level `1..max_level`.
- `hero_talents`: ordinals are `0..n-1` with no gaps, and `n <= TALENTS_MAX_PER_HERO`. `reveal_count >= 1`. A `TRAIT` reward points at a `source = TALENT` trait. `GRADE_REACHED` and `LEVEL_REACHED` counts are reachable (`<= GRADE_MAX`, `<= LEVEL_CAP[GRADE_MAX]`).
- `hero_seal_traits`: `slot_index < SEAL_SLOTS[GRADE_MAX]`. Slots are contiguous from 0. Each slot points at a `source = SEAL` trait.
- `memory_scenes` and `memory_effects`: `to_grade` in `2..GRADE_MAX`. Every `memory_effects` key has a scene row. `TRAIT_LEVEL` never lifts a trait past its `max_level`. A trait referenced by `TRAIT_LEVEL` is held by then (life, a seal slot open at that grade, or an earlier grant).
- `SEAL_SLOTS[g]` is non-decreasing in `g`.
- Contract 15 is a manual review item for #391's rows: no source names, occupations used as character names, story or text.

## 3. Statuses

A `Status` is a timed or conditional combat state on one combatant (section 5.1 of 00). Statuses are content rows; the sim owns one generic engine that applies, stacks, ticks, ends and reports them. Every trigger is a deterministic threshold on values the player can see, and **the status engine draws no RNG**. The battle loop that calls the engine is 05's (#371); death and revival are #373's.

Tiers: the engine, the pressure model and `FEAR` are **M0**. `PANIC`, `DESPAIR`, `BERSERK`, `EXHAUSTION`, `COMPOSURE`, `POISON`, `BLEEDING`, `FRENZY`, `NEAR_DEATH`, contagion and the offline summary are **MVP**. `SURGE` (hidden-power awakening), dual-mind heroes and leader-side alert hooks are **P1**.

Sources: research §3 (Fear, Panic, Despair, Frenzy, Poison, Bleeding, near death, Berserk vs Composure, status alerts), §9.1 (Exhaustion, hidden power, Composure + Berserk, the near-death trait), §9.5 (the death cascade, a boss that berserks at low health), §10 (the numbers).

### 3.1 The status set

| Id | Group | Rank | Effect | Ends | Tier |
|---|---|---|---|---|---|
| `FEAR` | `DREAD` | 1 | All stats `STATUS_FEAR_PCT` (-30) | Pressure falls below its band (3.3) | M0 |
| `PANIC` | `DREAD` | 2 | All stats `STATUS_PANIC_PCT` (-50) | Same | MVP |
| `DESPAIR` | `DREAD` | 3 | All stats `STATUS_DESPAIR_PCT` (-80) | Same | MVP |
| `BERSERK` | `MIND` | 1 | Power stats `STATUS_BERSERK_PCT`, skill effect `STATUS_BERSERK_SKILL_PCT`, AI flag `RECKLESS`; suppresses `DREAD` | `STATUS_BERSERK_TICKS`; then `EXHAUSTION` | MVP |
| `COMPOSURE` | `MIND` | 1 | Nerve `+STATUS_COMPOSURE_NERVE`; blocks `BERSERK` and `FRENZY` | Its source's duration (0 = whole battle) | MVP |
| `EXHAUSTION` | `FATIGUE` | 1 | All stats `STATUS_EXHAUSTION_PCT` (-90); blocks `BERSERK` | `STATUS_EXHAUSTION_TICKS` | MVP |
| `FRENZY` | `RAGE` | 1 | AI flag `TARGET_HOSTILE_PARTNER` (3.4) | `STATUS_FRENZY_TICKS`, or the partner dies | MVP |
| `POISON` | `TOXIN` | 1 | Lose `STATUS_POISON_PERMILLE` of max HP every `STATUS_DOT_INTERVAL_TICKS` | `STATUS_POISON_TICKS`, or an antidote (cure tag `ANTIDOTE`) | MVP |
| `BLEEDING` | `WOUND` | 1 | Lose `STATUS_BLEED_PERMILLE` of max HP per stack every `STATUS_DOT_INTERVAL_TICKS`; up to `STATUS_BLEED_MAX_STACKS` | `STATUS_BLEED_TICKS`, or any healing potion (cure tag `HEAL`) | MVP |
| `NEAR_DEATH` | `VITAL` | 1 | Marker, no stat change; read by traits (section 2) and alerts | HP back above `STATUS_NEAR_DEATH_EXIT_PCT` | MVP |
| `SURGE` | `SURGE` | 1 | All stats `STATUS_SURGE_PCT` (+1000) | `STATUS_SURGE_TICKS`; once per battle | P1 |

"All stats" means every combat stat in section 1's set **except the HP pool**: current and max HP are never scaled by a status, so a -80% status can't kill by itself (DIV-16). Power stats are those with `stats.power_tag` (section 1.7): `STR` and `INT`.

Statuses apply to any combatant. Heroes run the pressure model (3.3); enemies get statuses only from explicit triggers on their own rows, such as a boss that takes `BERSERK` at low health (research §9.5). Enemies have no pressure.

### 3.2 Content tables

Three tables (09, #387). Ids and ranks are content; adding a status is a content change plus, only for a new *kind* of effect, code.

**`statuses`**: one row per status.

| Column | Type | Meaning |
|---|---|---|
| `id` | string | UPPER_SNAKE id, as in 3.1 |
| `group` | string | Exclusion group. One active status per group per combatant (3.5) |
| `rank` | int | Precedence inside the group; higher wins |
| `stat_pct` | int | Added to every combat stat's percentage (3.6); 0 for none |
| `stat_scope` | enum | `ALL` or `POWER` |
| `skill_pct` | int | Added to skill effect percentage (#366) |
| `nerve_bonus` | int | Added to the hero's nerve (3.3) |
| `ai_flags` | flags | Read by 05's AI: `RECKLESS`, `TARGET_HOSTILE_PARTNER` |
| `duration_ticks` | int | 0 = lasts until its end condition |
| `min_ticks` | int | The status can't end by condition before this many ticks (anti-flicker) |
| `stack_rule` | enum | `REFRESH` (reset the timer), `STACK` (add a stack and reset the timer), `IGNORE` |
| `max_stacks` | int | 1 unless `STACK` |
| `dot_permille` | int | Max-HP permille lost per stack per DoT interval; 0 for none |
| `dot_cause` | enum | Death cause given to #373 if the DoT kills (`POISON`, `BLEEDING`) |
| `cure_tags` | list | Consumable tags that remove it (#368) |
| `blocks` | list | Status ids that can't be applied while this one is active |
| `suppresses_groups` | list | Groups whose active status ends (`SUPPRESSED`) while this one is active |
| `on_end_status` | string | Applied when this ends by `EXPIRED` (Berserk -> Exhaustion) |
| `max_per_battle` | int | 0 = unlimited |
| `contagion_shock` | int | Shock added to each living party ally when this is applied (3.7) |
| `alert` | enum | `NONE`, `FEED`, `ALERT` (3.9) |
| `stress_on_apply` | int | Stress added to the hero when this is applied (section 4.3); seed Fear 100, Panic 200, Despair 400, others 0 |
| `name_key`, `desc_key`, `icon` | string | Localization keys and icon id (contract 11) |
| `tier` | enum | `M0`, `MVP`, `P1` |

**`status_triggers`**: the non-pressure ways a status starts.

| Column | Type | Meaning |
|---|---|---|
| `id` | string | Trigger id |
| `status_id` | string | What it applies |
| `on` | enum | `BATTLE_START`, `PRESSURE_BAND`, `HP_AT_OR_BELOW_PCT`, `STATUS_APPLIED`, `PAIR_HOSTILE` |
| `param` | int | Threshold for the `on` kind (band rank, HP percent) |
| `requires_tag` | string | Tag the combatant must carry (trait section 2, skill #366, class #367, enemy row 05); empty = anyone |
| `replaces_status` | string | For `PRESSURE_BAND`: the dread status this one takes the place of |
| `priority` | int | Order when several triggers fire on one tick |

Seed rows:

| `id` | `status_id` | `on` | `param` | `requires_tag` | `replaces_status` | Tier |
|---|---|---|---|---|---|---|
| `T_COMPOSED_START` | `COMPOSURE` | `BATTLE_START` | 0 | `TEMPER_COMPOSED` | | MVP |
| `T_BERSERK_PANIC` | `BERSERK` | `PRESSURE_BAND` | 2 | `TEMPER_BERSERK` | `PANIC` | MVP |
| `T_BERSERK_LOW_HP` | `BERSERK` | `HP_AT_OR_BELOW_PCT` | `STATUS_BERSERK_HP_PCT` | `TEMPER_BERSERK` | | MVP |
| `T_BOSS_ENRAGE` | `BERSERK` | `HP_AT_OR_BELOW_PCT` | `STATUS_BERSERK_HP_PCT` | `ENEMY_ENRAGES` | | MVP |
| `T_FRENZY` | `FRENZY` | `PAIR_HOSTILE` | 2 | | | MVP |
| `T_SURGE` | `SURGE` | `STATUS_APPLIED` | 0 (`NEAR_DEATH`) | `HIDDEN_POWER` | | P1 |

Attack-applied statuses (poison, bleeding) are not triggers here: an attack or skill row in 05 or #366 carries `applies_status` and `apply_every_n_hits`. The attacker keeps a hit counter per attack, so the Nth, 2Nth... hit applies the status. No roll.

**`constants`**: every scalar in "Constants introduced" is a row in the existing key/value `constants` table.

### 3.3 Pressure: Fear, Panic and Despair (M0 Fear, MVP the rest)

The dread statuses are not timed. They are bands of one integer per hero, **pressure**, computed every tick from visible inputs:

```csharp
int Pressure(Hero h, BattleCtx b) =>
    Threat(h, b) - Nerve(h, b) + h.Battle.Shock;

int Threat(Hero h, BattleCtx b) =>
    b.Floor.Threat                                            // floor row column, 05 (#370)
  + Max(0, b.Floor.EnemyLevel - h.Level) * C.STATUS_THREAT_PER_LEVEL_GAP;

int Nerve(Hero h, BattleCtx b) =>
    C.STATUS_NERVE_BY_GRADE[h.Grade]
  + Min(h.BattlesSurvived, C.STATUS_NERVE_BATTLES_CAP) * C.STATUS_NERVE_PER_BATTLE
  + InnerLife.NerveModifier(h)            // morale and stress bands: section 5; 0 in M0
  + Traits.NerveModifier(h)               // STATUS_RESIST on DREAD: section 5
  + ActiveStatuses(h).Sum(s => s.Def.NerveBonus);   // COMPOSURE

DreadRank Band(int p, DreadRank current) {
    // Enter at the threshold; leave only below threshold - hysteresis.
    int R(DreadRank r) => r == current ? C.STATUS_BAND_HYSTERESIS : 0;
    if (p >= C.STATUS_DESPAIR_AT - R(Despair)) return Despair;
    if (p >= C.STATUS_PANIC_AT   - R(Panic))   return Panic;
    if (p >= C.STATUS_FEAR_AT    - R(Fear))    return Fear;
    return None;
}
```

- **Battle start (M0).** Shock starts at 0, so the opening band depends only on the floor, the hero's level and grade, battles survived, inner life and traits. "Unprepared heroes start afraid" (research §3) becomes: under-levelled, low-grade or green heroes start in `FEAR` or worse.
- **Shock (M0).** Battle events add to `Shock` (3.7), clamped to `0..STATUS_SHOCK_MAX`. Shock decays by 1 every `STATUS_SHOCK_DECAY_TICKS`, and each enemy the hero's party kills subtracts `STATUS_SHOCK_RELIEF_KILL`. This is how Fear wears off mid-fight (research §3).
- **Band changes.** A higher band replaces the lower status at once (`StatusEnded` reason `REPLACED`, then `StatusApplied`). A lower band only takes over after the current status has lasted `min_ticks`. With M0 content, only the `FEAR` row exists, so any band at or above Fear maps to `FEAR`.
- **Forecast (M0).** Because the opening band is a pure function of state, the sim exposes `ForecastOpeningStatus(heroUid, floor)`. `SCR_PARTY` and `SCR_TOWER` show it per hero ("will start afraid"), before the player commits a party (DIV-15).
- `BattlesSurvived` is a counter on `Hero`, +1 for each battle the hero ends alive. 02 owns its field (#384).

### 3.4 Berserk, Composure, Exhaustion, Frenzy and Surge

- **Berserk (MVP).** A hero tagged `TEMPER_BERSERK` (from a former life or trait, section 2, or a skill, #366) who reaches the Panic band takes `BERSERK` in place of `PANIC`, or takes it at `STATUS_BERSERK_HP_PCT` HP. Berserk ends any active `DREAD` status (`SUPPRESSED`) and keeps it from applying; pressure is still tracked. Its `RECKLESS` flag tells 05's AI to ignore its role and formation and attack the nearest enemy. `max_per_battle` is 1.
- **Exhaustion (MVP).** When Berserk expires, `EXHAUSTION` follows for `STATUS_EXHAUSTION_TICKS` (research §9.1). It does not follow if Berserk ended by death, cure or battle end. Exhaustion and dread sum (3.6), clamped.
- **Composure (MVP).** Granted at battle start by `TEMPER_COMPOSED`, or by a skill for the skill's duration. It adds nerve and blocks `BERSERK` and `FRENZY`. Berserk and Composure share the `MIND` group, so normally only one holds (research §3).
- **Dual mind (P1).** A hero tagged `DUAL_MIND` (rare trait, section 2) is exempt from the `MIND` exclusion: it can hold both, and its Berserk drops `RECKLESS` (power without the control loss, research §9.1).
- **Frenzy (MVP).** Two heroes in the same battle whose bond is in tier `HOSTILE` (section 4.5) both take `FRENZY` when either reaches the band given by `param` (2 = Panic). A frenzied hero's AI targets its hostile partner, and only that partner, while the partner lives; it attacks enemies otherwise. Frenzy ends when its timer ends or the partner dies. The wipe risk comes from the deaths and the shock that follow (DIV-17). Pair hostility is shown on the party screen by section 4, so the risk is visible before assignment.
- **Surge (P1).** A hero tagged `HIDDEN_POWER` (a hidden talent, section 2) takes `SURGE` the first time `NEAR_DEATH` is applied in a battle. The trait is hidden until first used (research §3 hidden talents); the trigger rule itself is public in the hero help text. The source does not say what triggers it (research §12); this fills that gap.

### 3.5 Stacking and precedence

1. **One status per group.** Applying status *S* in group *G* where *A* is active:
   - `S.rank > A.rank`: *A* ends `REPLACED`, *S* applies.
   - `S.rank < A.rank`: ignored, no event.
   - `S == A`: by `stack_rule`. `REFRESH` resets the timer; `STACK` adds a stack (up to `max_stacks`) and resets the timer; `IGNORE` does nothing. A refresh or stack emits `StatusApplied` with the new stack count.
   - Same rank, different id (Berserk vs Composure in `MIND`): the first one holds; the second is ignored, unless the combatant is `DUAL_MIND`.
2. **Blocks** are checked before group rules: a status in an active status's `blocks` list is not applied (no event).
3. **Suppression:** applying a status with `suppresses_groups` ends the active status of each listed group (`SUPPRESSED`) and blocks that group until it ends.
4. **Across groups** effects combine (3.6). Fear and Panic can never both be active: they share `DREAD`.

### 3.6 Effect on stats

Status modifiers are applied **last**, after section 1's pipeline (grade, level, growth, class, skills, traits, gear):

```csharp
int Effective(int stat, StatTag tag, Combatant c) {
    int pct = 0;
    foreach (var s in c.Statuses)            // canonical order: status id
        if (s.Def.StatScope == ALL || (s.Def.StatScope == POWER && tag == POWER))
            pct += s.Def.StatPct;
    pct = Clamp(pct, C.STATUS_STAT_PCT_MIN, C.STATUS_STAT_PCT_MAX);
    return (int)((long)stat * (100 + pct) / 100);   // integer, truncates toward zero
}
```

Percentages add across groups and are clamped, so Despair plus Exhaustion (-170) holds at `STATUS_STAT_PCT_MIN` (-95) (DIV-16). `skill_pct` sums and clamps the same way into #366's skill-effect value. Effective stats are recomputed only on the tick a status is applied or ends.

DoT damage is `maxHp * dot_permille * stacks / 1000`, taken through 05's damage path with source `STATUS`, so armour and shields follow 05's rules and a DoT death reaches #373 with `dot_cause`. HP arithmetic follows contract 10 and 05; the status engine's own numbers (ticks, pressure, percentages, permille, stacks) are all integers.

### 3.7 Spread through a party (the death cascade)

Research §9.5 shows fear and panic spreading before a wipe. Spread works only through shock, inside one party, and is bounded:

| Event | Shock to each living hero in the same party | Tier |
|---|---|---|
| An ally dies | `STATUS_SHOCK_ALLY_DEATH` | M0 |
| The party leader dies | `STATUS_SHOCK_LEADER_DEATH` extra | MVP |
| An ally takes `PANIC` or `DESPAIR` | That row's `contagion_shock` | MVP |
| Another party in the same battle is wiped | `STATUS_SHOCK_PARTY_WIPED` (to every party still fighting) | MVP |
| The party kills an enemy | `-STATUS_SHOCK_RELIEF_KILL` | M0 |

Contagion fires on **entry** to a band, at most once per hero per status per battle, so a cascade always terminates. `COMPOSURE` gives nerve, not immunity: a composed hero feels shock but needs more of it.

### 3.8 Tick order

05 owns the battle tick (#371). The status engine exposes two hooks that 05 calls on every battle tick, in this place:

```csharp
// 05's battle tick, status parts only
void BattleTick(BattleCtx b) {
    Statuses.PreAction(b);   // 1. timers, DoT, expiry chains, shock decay, bands, HP markers
    Ai.Act(b);               // 2. 05: moves and attacks; they may queue status applications
    Damage.Resolve(b);       // 3. 05: hits, deaths (deaths queue shock)
    Statuses.PostAction(b);  // 4. queued applications, shock, bands, spread, effective stats
}

void PreAction(BattleCtx b) {
    foreach (var c in b.CanonicalOrder) {        // parties by entry order, slot 0..4; then enemies by spawn index
        foreach (var s in c.Statuses.OrderBy(id)) {
            s.ElapsedTicks++;
            if (s.Def.DotPermille > 0 && s.ElapsedTicks % C.STATUS_DOT_INTERVAL_TICKS == 0)
                Damage.Queue(c, DotAmount(c, s), source: STATUS, cause: s.Def.DotCause);
            if (s.Def.DurationTicks > 0 && s.ElapsedTicks >= s.Def.DurationTicks)
                End(c, s, EXPIRED);              // applies on_end_status (Berserk -> Exhaustion)
        }
        if (c is Hero h) { DecayShock(h); ApplyBand(h, b); }
        CheckHpMarkers(c);                       // NEAR_DEATH enter/exit, HP_AT_OR_BELOW_PCT triggers
    }
}

void PostAction(BattleCtx b) {
    // Deterministic order: target canonical index, then trigger priority, then status id, then queue order.
    foreach (var app in b.StatusQueue.OrderBy(a => (a.Target.Index, a.Priority, a.StatusId, a.Seq)))
        TryApply(app);                           // 3.5 rules; may emit StatusApplied / StatusEnded
    ApplyShockFromDeaths(b);                     // 3.7
    foreach (var h in b.HeroesInCanonicalOrder) ApplyBand(h, b);   // may chain contagion; bounded by 3.7
    RecomputeDirtyStats(b);
}
```

- `BATTLE_START` triggers and the opening bands are applied once, before the first `PreAction`, in canonical order.
- A hero who dies ends every status with `DIED`. When the battle ends, every remaining status ends with `BATTLE_ENDED` and no `on_end_status` applies. Statuses never leave a battle: the lobby heals everything (research §9.1), and the next battle starts clean.
- **No RNG.** `StatusSystem` takes no RNG stream and draws nothing, so statuses never shift 05's battle stream (contract 2). Any future chance-based status must draw from 05's battle stream and show its chance in the UI; that needs a change to this section.
- **Away is the same game** (contract 8). Offline catch-up runs these same hooks. If 05 or 06 define a faster catch-up, it must reproduce the same status events or be proven equivalent by the bots.

### 3.9 Events and alerts

Both events are in 00's vocabulary (section 5.1).

```csharp
record StatusApplied(BattleId Battle, long Tick, CombatantRef Target,   // HeroUid or enemy spawn index
                     PartyId? Party, StatusId Status, int Stacks, int RemainingTicks,
                     StatusCause Cause, CombatantRef? Source);         // PRESSURE, TRIGGER, ATTACK, SKILL, CHAIN, CONTAGION
record StatusEnded  (BattleId Battle, long Tick, CombatantRef Target, PartyId? Party,
                     StatusId Status, StatusEndReason Reason);
enum StatusEndReason { EXPIRED, CONDITION_CLEARED, REPLACED, SUPPRESSED, CURED, DIED, BATTLE_ENDED }
```

A refresh or a new stack emits `StatusApplied` again; going from one dread band to another emits `StatusEnded(REPLACED)` then `StatusApplied`.

The source sends status alerts to the master and the party leader, even across split parties (research §3). Ours surface by the row's `alert` column:

| Where | `FEED` | `ALERT` | Tier |
|---|---|---|---|
| `SCR_BATTLE`, while watching | Icon on the hero and a line in the battle log | Same, plus a banner toast naming hero and party | M0 (Fear is `FEED`) |
| `SCR_LOBBY`, a fight running elsewhere | Nothing | An alert chip on that party until the player looks at it | MVP |
| `SCR_OFFLINE_REWARD` and `SCR_MISSION_RESULT` | Counted | Listed per hero | MVP |
| Push notification | Never | Never | — |

Seed alerts: `FEAR`, `POISON`, `BLEEDING`, `COMPOSURE`, `EXHAUSTION` are `FEED`; `PANIC`, `DESPAIR`, `BERSERK`, `FRENZY`, `NEAR_DEATH`, `SURGE` are `ALERT`. Push notifications are 06's and 07's; statuses never push on their own (DIV-18). Death notices are #373's.

**Offline summary (MVP).** For each assignment that ran while away, the sim keeps a `StatusSummary`: per hero, the worst dread status reached, how many times `NEAR_DEATH` applied, and any `BERSERK`, `FRENZY` or `SURGE`. 06 (#379) lays it out in the report next to the deaths.

**Leader hook (P1).** The party leader's AI can read the party's active alerts (for example, guard a hero in `NEAR_DEATH`). 05 decides whether to use it.

### 3.10 Cures and consumables

Each status lists `cure_tags`. When a hero uses a consumable with a matching tag, the status ends with `CURED`. Healing potions restore HP but do not end `POISON` (research §3: potions slow poison, they don't cure it); they do end `BLEEDING`. Antidotes end `POISON`. When the AI uses a carried consumable is #368's auto-use rule; this section provides `bool CanCure(ItemDef, Status)`.

## 4. Inner life

A hero's inner life is four values and a web of pair bonds that the lobby shapes and the climb wears down: **morale**, **stress**, **loyalty** and **bonds** between heroes (research §3, §8, §9.6). They turn into small stat bonuses or penalties in battle, they decide whether a hero breaks down, and from P1 they decide whether a hero refuses a floor. Every value is shown to the player as a number and a band. Nothing about a hero's mind is hidden (owner, #359 comment; DIV-19). Stress can kill (decision 128), but only after a warning the player has seen and a countdown a single tap can stop (section 4.4).

Design budget for an idle game: the player never has to feed, pet or check in on heroes to keep them alive. Recovery is automatic while a hero is off duty. The player's decisions are the same ones the lobby already asks for: who fights, who rests, who is fed to synthesis, who goes in a party with whom. The only active care is one command, `OrderRest`, and the optional P1 gifts.

| Feature | Tier |
|---|---|
| Morale, its bands and stat effect | MVP |
| Stress, its bands and stat effect, breakdown and stress death | MVP |
| Bonds: intimacy, pair synergy and anti-synergy, hostility on summon | MVP |
| `OrderRest` | MVP |
| Loyalty | P1 |
| Gifts, tastes, jealousy | P1 (matches section 2.1's Gifts row) |
| Refusals and strikes | P1 |
| Duels, inner-life side only (slot duels and stakes are 06's, #378) | P1 |
| Hero gift requests ("asks for a specific gift", research §8) | P2 |

M0 has no inner life: M0 heroes carry no inner-life fields and battles apply no inner-life modifier. The fields arrive in MVP (02, #384 decides whether v1 reserves them).

### 4.1 Scale, storage and timing

All inner-life values are integers. Morale, stress and loyalty run `0..INNER_MAX` (10,000 points); a bond runs `-INNER_MAX..INNER_MAX`. The UI shows `floor(value / 100)` as 0-100 (bonds as -100..100), plus the band. Percentages in rules are integer percents; every product is computed as `a * b / 100` in `long`, truncating toward zero, clamped after each change.

```csharp
// On Hero (02 owns the final names, #384)
struct InnerLife {
    int Morale;            // 0..INNER_MAX
    int Stress;            // 0..INNER_MAX
    int Loyalty;           // 0..INNER_MAX, P1 (held at LOYALTY_START until then)
    bool RestOrdered;      // set by OrderRest
    long BreakdownTick;    // -1 = not in breakdown
    long GraceStartTick;   // -1 = grace not started (breakdown not yet seen)
    int GiftsToday;        // P1, reset on DayRolledOver
}
// On GameState: bonds, sparse, sorted by (A, B) with A < B. A missing pair is 0.
struct Bond { HeroUid A; HeroUid B; int Value; bool SharedPartyToday; }
```

When values change:

| Trigger | When | What it changes |
|---|---|---|
| **Pulse** | Every `INNER_PULSE_S` of sim time (`tick % (INNER_PULSE_S * TICK_HZ) == 0`), online and in offline catch-up alike | Battle stress, rest recovery, bond growth (4.3, 4.5) |
| Battle events | When 05 emits them: `StatusApplied`, `HeroDied` | Stress and morale spikes (4.2, 4.3) |
| Lobby events | When the owning command applies: `HeroesSynthesized`, `HeroConverted`, `HeroExecuted`, `FacilityUpgraded`, `FloorFirstCleared`, `HeroAcquired`, `HeroRevived` | Spikes, bond creation |
| `DayRolledOver` | Day rollover (decision 8) | Morale drift, food relief, bond drift, jealousy (P1) |
| Battle boundary | When a battle ends and before the party's next battle starts | Benching for breakdown or rest orders; refusal check (P1) |

The pulse is the only per-time update, so catch-up stays cheap and exact: catch-up must apply the same pulses in the same order as online ticks (contract 8). If 05 or 06 defines a faster analytic catch-up (#371, #379), it must reproduce pulse results exactly, since every rule here is integer arithmetic on counts of pulses.

Per-hero modifiers come from data, never from code: traits and former lives (section 2) may carry `stress_gain_pct`, `stress_rest_pct`, `morale_loss_pct` and `bond_tags` columns. A modifier of `+20` means 120% of the base amount.

### 4.2 Morale (MVP)

Morale is how much hope a hero has. The lobby moves it mostly as a whole; parties and gifts move it per hero (research §3: synthesis lowers it, gifts and facility upgrades raise it; a lobby run on fear loses hope).

| Trigger | Who | Change |
|---|---|---|
| `HeroAcquired` | The new hero | Set to `MORALE_START` |
| `HeroesSynthesized` | Every other living hero in the roster | `-MORALE_SYNTH_WITNESS` per hero consumed, at most `-MORALE_SYNTH_CAP` per command |
| `HeroConverted` | Every other living hero | `-MORALE_CONVERT_WITNESS` per hero consumed, at most `-MORALE_CONVERT_CAP` per command |
| `HeroExecuted` (P1, #376) | Every other living hero | `-MORALE_EXECUTION_WITNESS` |
| `HeroDied` | Its party-mates / everyone else living | `-MORALE_ALLY_DEATH` / `-MORALE_LOBBY_DEATH` |
| `FacilityUpgraded` | Every living hero | `+MORALE_FACILITY_UPGRADE` |
| `FloorFirstCleared` | Living heroes of the clearing party | `+MORALE_FIRST_CLEAR` |
| Gift (P1) | The receiver | 4.7 |
| `DayRolledOver` | Every living hero | Moves `MORALE_DRIFT_PER_DAY` toward `MORALE_BASELINE`, never past it |

Bands (`inner_bands` table, stat `MORALE`):

| Band | Range | Stat % | Notes |
|---|---|---|---|
| `INSPIRED` | 8000-10000 | +5 | |
| `STEADY` | 4000-7999 | 0 | |
| `LOW` | 2000-3999 | -5 | |
| `DESPONDENT` | 0-1999 | -10 | Refusal condition R2 from P1 |

The witness penalties are what makes synthesis a choice with a cost (research §5: typical masters synthesize 3-4 times a day). The seed values let a player synthesize a few heroes a day and recover by the next day's drift plus an occasional facility upgrade; running the lobby on mass culls sinks it to `LOW`. #348 tunes this.

### 4.3 Stress (MVP)

Stress is wear from fighting. It rises while a hero fights, fastest against human-shaped enemies (research §7: from floor 12), and spikes on frightening events. It falls on its own while the hero is off duty, faster with good lodging and varied food (research §3, §8).

```csharp
// Each pulse, for each living hero h:
if (h.BreakdownTick < 0 && Fighting(h)) {               // its party has an active Battle (05)
    int pct = 100 + STRESS_HUMAN_BONUS_PCT * battle.HumanSharePct / 100;   // HumanSharePct from 05, 0..100
    pct = pct * (100 + h.Mods.StressGainPct) / 100;
    h.Stress += STRESS_BATTLE_PER_PULSE * pct / 100;
} else if (!Fighting(h)) {                                // unassigned, or assigned between battles
    int pct = 100 + lobby.RestBonusPct + h.Mods.StressRestPct;  // RestBonusPct from 06's lodging, #375
    if (h.RestOrdered) pct = pct * STRESS_REST_ORDER_PCT / 100;
    if (h.BreakdownTick >= 0 && !h.RestOrdered) pct = 0;      // a broken-down hero can't calm down alone
    h.Stress -= STRESS_REST_PER_PULSE * pct / 100;
}
h.Stress = Clamp(h.Stress, 0, INNER_MAX);
```

| Spike | Who | Change |
|---|---|---|
| `HeroAcquired` | The new hero | Set to `STRESS_START` |
| `StatusApplied` | The hero | `+statuses.stress_on_apply` for that status (a column on section 3's table; seed Fear 100, Panic 200, Despair 400, others 0) |
| `HeroDied` | Party-mates | `+STRESS_ALLY_DEATH`, or `+STRESS_ALLY_DEATH_BONDED` if their bond with the dead hero is `CLOSE` or better |
| `HeroesSynthesized` | The target / every other living hero | `+STRESS_SYNTH_TARGET` / `+STRESS_SYNTH_WITNESS`, per hero consumed |
| `HeroConverted` | Every other living hero | `+STRESS_CONVERT_WITNESS` per hero consumed |
| `HeroExecuted` (P1) | Every other living hero | `+STRESS_EXECUTION_WITNESS` |
| `DayRolledOver` | Every living hero | `-STRESS_FOOD_PER_DISH * min(lobby.FoodVariety, STRESS_FOOD_DISH_CAP)`; `FoodVariety` is the count of distinct dishes the restaurant served that day (06, #375) |
| `HeroRevived` | The revived hero | Set to `STRESS_REVIVE`; breakdown and rest order cleared |

Bands (`inner_bands`, stat `STRESS`):

| Band | Range | Stat % | Notes |
|---|---|---|---|
| `CALM` | 0-3999 | 0 | |
| `STRAINED` | 4000-7999 | -5 | |
| `CRITICAL` | 8000-9999 | -10 | Warning W1 (4.4); refusal condition R1 from P1 |
| `BREAKDOWN` | 10000 | n/a | The hero is benched and the death countdown runs (4.4) |

Seed budget, for #348 to check: a hero fighting all day (24 h, the offline cap) on floors with no human enemies gains about 2,900 points; on fully human floors about 8,600. A day of passive rest removes about 8,600; a full-variety restaurant removes 750 a day. So a party on human floors needs about a day off for each day on, and a party below floor 12 can fight for several days before it is `CRITICAL`. Rotating rested heroes into the climb is the intended lobby skill, not a chore: it happens through the party screen the player already uses.

### 4.4 Breakdown, stress death and the warning rule (MVP)

Stress death is real (decision 128) and never a surprise. A hero dies of stress only after **two** signals the player has seen and a countdown they ignored.

**W1, critical warning.** When a hero enters `CRITICAL`, the sim emits `StressBandChanged(band = CRITICAL)`. The lobby shows a standing alert until the hero leaves the band, the hero's roster card turns red, and the offline report lists it. `SCR_PARTY` shows, for each assigned hero, a projection: `pulses = ceil((INNER_MAX - stress) / gainPerPulse(floor))`, shown as "breaks down in about N h on this floor".

**W2, breakdown.** When stress reaches `INNER_MAX`:

1. The sim sets `BreakdownTick` and emits `HeroBrokeDown`. Stress stays at `INNER_MAX`; battle stress stops accruing.
2. The hero finishes the battle in progress (no mid-fight removal; 05's battle loop is untouched) and is removed from its party at the battle boundary, leaving the slot empty. The party keeps its assignment if any hero remains (06, #378). The hero can't be put in a party until it recovers (`SetParty` rejects with `HERO_BROKEN_DOWN`).
3. **The grace countdown starts only when the player has seen the breakdown.** If the breakdown happened in online ticks, `GraceStartTick = BreakdownTick`. If it happened during offline catch-up, `GraceStartTick` is set on `ClaimOfflineReward`, so the offline report always shows it with the full countdown left.
4. The hero dies when `tick - GraceStartTick >= STRESS_GRACE_H * 3600 * TICK_HZ` and it has not recovered. The sim emits `HeroDied(cause = STRESS)` and the hero becomes `DEAD` (05, #373). A revive item brings it back (DIV-04), at `STRESS_REVIVE`.
5. **The fix always works.** While in breakdown, passive rest does nothing, so neglect has a cost, but one `OrderRest` is always enough: the seeds satisfy `STRESS_REST_PER_PULSE * STRESS_REST_ORDER_PCT / 100 * pulsesIn(STRESS_GRACE_H / 2) >= INNER_MAX - STRESS_RECOVER` with no lodging bonus (about 3.7 h needed against a 24 h countdown). A content-validator rule (09) rejects tables that break this inequality.
6. When stress falls below `STRESS_RECOVER`, the breakdown ends: `HeroRecovered` is emitted and the countdown is cleared.

Since `STRESS_GRACE_H` is at least `OFFLINE_CAP_H`, a player who opens the game once a day never loses a hero to stress without having had a full countdown in front of them. Because sim time is capped by `OFFLINE_CAP_H` per absence, a long absence pauses the countdown rather than running it out.

**`OrderRest(heroUid)` (MVP).** Sets `RestOrdered`. The hero leaves its party at the next battle boundary (at once if not fighting) and rests at `STRESS_REST_ORDER_PCT`. The order clears itself when stress falls below `STRESS_REST_DONE` (`HeroRested`) or on `CancelRest(heroUid)`. A resting hero can't join a party (`HERO_RESTING`). Rejects: `HERO_NOT_FOUND`, `HERO_DEAD`, `ALREADY_RESTING`. Free, no cooldown. The manager's tips (#376) point at it on every W1 and W2 alert.

### 4.5 Bonds: intimacy, pair synergy and hostility (MVP)

A bond is how two heroes feel about each other (research §3: goodwill gives a pair stat bonuses in one party, some pairs lower each other's stats, hostility can form the moment a hero is summoned). Positive values are intimacy; negative values are hostility. Bonds are symmetric in MVP.

**Forming on summon.** On `HeroAcquired`, for each other living hero `o` in the roster:

```csharp
int v = AffinityFromTags(newHero, o);                  // bond_affinity table: tag pair -> value, summed
if (Rng("inner_life").NextPct() < BOND_SUMMON_HOSTILE_PCT)
    v += BOND_SUMMON_HOSTILE_VALUE;                     // hostility at first sight
if (sameAffinityPull) v += BOND_AFFINITY_PULL_VALUE;    // heroes pulled together who know each other (#361)
if (v != 0) { SetBond(newHero, o, v); Emit(BondFormed(a, b, v)); }
```

Tags come from former lives and traits (section 2), e.g. two heroes from the same trade start warm, and content may list pairs of trades that start cold. The draw uses a new named stream, `inner_life` (contract 2), one draw per existing living hero in ascending `HeroUid` order, so the result replays exactly. The summon reveal (`SCR_SUMMON_RESULT`) shows every bond formed.

**Growth.** Each pulse, for each pair of living heroes fighting in the same active battle: a bond `>= 0` gains `BOND_TOGETHER_PER_PULSE`; a bond `< 0` loses it (hostility festers side by side). On `DayRolledOver`, a negative bond whose pair did not share a party that day moves `BOND_HOSTILE_DRIFT_PER_DAY` toward 0. Positive bonds don't decay. When a hero leaves the roster (synthesis, conversion), its bonds are deleted; a `DEAD` hero keeps its bonds for revival.

**Tiers and party effect** (`bond_tiers` table):

| Tier | Range | Stat % per party-mate in this tier |
|---|---|---|
| `SWORN` | 7000 and up | +6 |
| `CLOSE` | 3000-6999 | +3 |
| `NEUTRAL` | -2999-2999 | 0 |
| `TENSE` | -6999 to -3000 | -3 |
| `HOSTILE` | -7000 and below | -6 |

A hero's pair % is the sum over its living party-mates, clamped to `±BOND_PARTY_PCT_CAP`. A `HOSTILE` pair in one party is the trigger for Frenzy (research §3); the status and its rules are section 3's. `BondTierChanged` is emitted when a pair crosses a tier boundary. `SCR_PARTY` shows the party's pair grid and each hero's total before the player assigns it.

### 4.6 How inner life reaches stats (MVP)

```csharp
int InnerLifePct(Hero h, Party p) =>
    Clamp(Band(MORALE, h.Morale).StatPct + Band(STRESS, h.Stress).StatPct + PairPct(h, p),
          INNER_STAT_PCT_MIN, INNER_STAT_PCT_MAX);   // -20..+15
```

The result is one all-stats percentage that the stat pipeline applies as a single multiplier, after max and before statuses (section 5). It never scales the HP pool. It is read at battle start and at each pulse, so a mid-fight band change takes effect at the next pulse. Outside a party, `PairPct` is 0.

### 4.7 Loyalty and gifts (P1)

Loyalty is how the hero sees the summoner. It merges the source's loyalty and favourability into one value (research §3, §8; DIV-22). It does nothing in MVP and is held at `LOYALTY_START`. In P1 it gates refusals (4.8).

| Trigger | Change |
|---|---|
| `HeroAcquired` | Set to `LOYALTY_START` |
| `FloorFirstCleared` | `+LOYALTY_FIRST_CLEAR` to the clearing party |
| Being party leader for a `FloorFirstCleared` | `+LOYALTY_LEADER_CLEAR` extra |
| `HeroExecuted`, `HeroesSynthesized` | `-LOYALTY_CULL_WITNESS` to every other living hero, once per command |
| Gift | Below |

**`GiveGift(heroUid, itemId)`.** Consumes one gift `Item` (an item kind with `gift_tags`; sources are #368 and #379, and gifts must be obtainable without gems, pillar 5). Taste is looked up in the `gift_tastes` table by `HeroDef` and tag: liked `GIFT_LIKED_PCT`, neutral 100, disliked `GIFT_DISLIKED_PCT` (negative). The hero gains `GIFT_MORALE * taste / 100` morale and `GIFT_LOYALTY * taste / 100` loyalty. Only the first `GIFT_DAILY_CAP` gifts per hero per day have an effect; later ones are rejected (`GIFT_CAP_REACHED`). Tastes are shown on `SCR_HERO` from the start. Event: `GiftGiven`.

**Jealousy.** On `DayRolledOver`, for each party in which at least one hero received a gift that day, every party-mate who received none loses `JEALOUSY_MORALE` per gifted party-mate, at most `JEALOUSY_MORALE_CAP`. Jealousy is scoped to parties, so the rule the player has to learn is "gift the whole party or none" (DIV-21).

### 4.8 Refusals and strikes (P1)

A refusal is deterministic and visible in advance (DIV-20). A hero refuses an assignment while any of these hold:

| Id | Condition | Shown as |
|---|---|---|
| R1 | Stress `CRITICAL`, the floor has `HumanSharePct > 0`, and loyalty `< LOYALTY_HOLD` | "Too stressed to fight people" |
| R2 | Morale `DESPONDENT` and loyalty `< LOYALTY_REFUSE_BELOW` | "Has given up" |
| R3 | A `HOSTILE` bond with a party-mate, and loyalty `< LOYALTY_HOLD` | "Won't fight beside *name*" |

**Strikes.** A refusal spreads one hop: any living hero with a `CLOSE` or better bond to a refuser, and loyalty `< LOYALTY_HOLD`, also refuses, in any party. The refuser whose own condition holds is the ringleader; heroes refusing by contagion name their ringleader. If a ringleader stops refusing or leaves the roster, its followers re-check at once.

**Effect.** The check runs when a party is assigned (`AssignFloor`, `AssignReplay` reject with `HERO_REFUSES` and list the refusers) and at every battle boundary. A party with a refuser is inoperable: it finishes the current battle, then stops, and the sim emits `HeroRefused` per refuser and `PartyInoperable(partyId)`. The party screen shows refusers before assignment, so a refusal is never a surprise.

**Ways out**, as in the source (research §8: meet their wishes or punish the instigator): fix the condition (rest, gifts, split a hostile pair), drop the refuser from the party, or punish the ringleader with the manager's execution (#376, decision 128) or synthesis (#364). Both punishments carry the witness penalties of 4.2 and 4.7 lobby-wide.

### 4.9 Duels, inner-life side (P1)

Duels and their stakes are #378's (research §8, §9.6). Inner life only reacts: when a duel between two heroes ends, their bond is set to 0 (the conflict is settled), the winner gains `DUEL_WIN_MORALE` and the loser loses `DUEL_LOSE_MORALE`. A duel is the one way to clear a `HOSTILE` bond quickly.

### 4.10 Visibility (MVP)

| Where | What |
|---|---|
| `SCR_HERO` | Morale, stress and (P1) loyalty as 0-100 with bands; bonds list; breakdown countdown; (P1) gift tastes and refusal reasons |
| `SCR_ROSTER` | Band icons; sort and filter by stress and morale |
| `SCR_PARTY` | Pair grid, each hero's inner-life %, breakdown projection on the chosen floor, (P1) refusers |
| `SCR_LOBBY` | Standing alerts for `CRITICAL` and `BREAKDOWN` heroes with the countdown |
| `SCR_OFFLINE_REWARD` | Band changes into `CRITICAL`, breakdowns, recoveries and stress deaths while away |
| `SCR_SUMMON_RESULT` | Bonds formed by the new hero |

### 4.11 Commands, events and content tables

| Command | Tier | Rejects |
|---|---|---|
| `OrderRest(heroUid)` | MVP | `HERO_NOT_FOUND`, `HERO_DEAD`, `ALREADY_RESTING` |
| `CancelRest(heroUid)` | MVP | `HERO_NOT_FOUND`, `NOT_RESTING` |
| `GiveGift(heroUid, itemId)` | P1 | `HERO_NOT_FOUND`, `HERO_DEAD`, `NOT_A_GIFT`, `NOT_ENOUGH_ITEM`, `GIFT_CAP_REACHED` |

New reject reasons on other specs' commands: `HERO_BROKEN_DOWN`, `HERO_RESTING` (on `SetParty`, 06); `HERO_REFUSES` (on `AssignFloor`, `AssignReplay`, 05, P1).

| Event | Tier | Payload |
|---|---|---|
| `MoraleBandChanged` | MVP | `HeroUid`, from, to |
| `StressBandChanged` | MVP | `HeroUid`, from, to |
| `HeroBrokeDown` | MVP | `HeroUid`, `PartyId` |
| `HeroRecovered` | MVP | `HeroUid` |
| `HeroRestOrdered`, `HeroRested` | MVP | `HeroUid` |
| `BondFormed` | MVP | two `HeroUid`s, value |
| `BondTierChanged` | MVP | two `HeroUid`s, from, to |
| `HeroDied` (existing) | MVP | adds `cause = STRESS` |
| `GiftGiven` | P1 | `HeroUid`, item, taste |
| `HeroRefused` (existing) | P1 | `HeroUid`, `PartyId`, reason `R1`/`R2`/`R3`/`STRIKE`, ringleader `HeroUid` |
| `PartyInoperable` | P1 | `PartyId`, refuser list |

Raw value changes emit no event (they happen every pulse); view-models read them from snapshots and react to band events (contract 9).

| Table | Key | Columns |
|---|---|---|
| `constants` | key | every inner-life scalar in "Constants introduced" (section 5) |
| `inner_bands` | `stat`, `band` | `min`, `max`, `stat_pct`, `nerve` (section 5), `name` (loc key) |
| `bond_tiers` | `tier` | `min`, `max`, `stat_pct`, `name` |
| `bond_affinity` | `tag_a`, `tag_b` | `value` |
| `gift_tastes` (P1) | `hero_def_id`, `tag` | `taste` (`LIKED`, `DISLIKED`) |
| Columns on others | | `statuses.stress_on_apply` (section 3); trait and former-life modifier columns and `bond_tags` (section 2); `HumanSharePct` from enemy rows (#370, #371); `RestBonusPct`, `FoodVariety` (#375) |

## 5. The stat pipeline, end to end

Sections 1-4 each own one layer. This section fixes their order, their number formats and the hand-offs between them.

```csharp
// Out of battle (the lobby sheet, power, the party builder): layers 1-2 only.
// In battle: all four layers. Integer maths throughout, long intermediates, clamped at 0.
int CurrentInBattle(Hero h, StatId s, BattleCtx b, ContentDb db) {
    long flat  = Core(h, s, db)                       // 1. section 1.2.1: base + promotion + growth + StatBonus
               + GearFlat(h, s, db)                   //    gear, #368
               + Traits.Flat[s];                      //    section 2.3.4
    long pctBp = db.Classes[h.ClassId].Pct[s] * (BASIS_POINTS / STAT_PCT_BASE)
               + Traits.PctBp[s];                     //    trait sum already clamped to ±TRAIT_STAT_PCT_CAP_BP
    long max   = Math.Max(0, flat * Math.Max(0, pctBp) / BASIS_POINTS);   // 2. Max: shown on the sheet

    int inner  = InnerLifePct(h, b.Party);            // 3. section 4.6, clamped INNER_STAT_PCT_MIN..MAX; 0 in M0
    int status = StatusSum(h, s, b);                  // 4. section 3.6, clamped STATUS_STAT_PCT_MIN..MAX
    long cur   = max * (STAT_PCT_BASE + inner) / STAT_PCT_BASE;
    return (int)Math.Max(0, cur * (STAT_PCT_BASE + status) / STAT_PCT_BASE);
}
```

- **Order:** core, then gear, traits and class (max), then inner life, then statuses. A trait's timed `COND_STAT_PCT` buff (section 2.3.3) is held by the battle as a status-like modifier and adds to the status sum, under the same clamp.
- **The HP pool is never scaled by layers 3 and 4.** Battle max HP reads **max** `VIT`, not current `VIT` (05, #371), so neither inner life nor a status can kill by itself (DIV-16). Current `VIT` still feeds 05's toughness terms.
- **Power** (section 1.2.2) reads max, never current: it is a planning value.
- **Numbers.** Stats, percentages, basis points, ticks, pressure and inner-life values are integers (contract 10 counts them as counted values). Battle HP and damage are curve values and stay `double` in 05 per contract 10; the status engine's damage-over-time amount is handed to 05 as a permille of max HP.
- **Former life and base stats.** `hero_defs.base_*` exclude the life trait. The seed stat lines in section 1.7 are what the sheet shows at Lv1, so a hero's base is the line minus its former life's spread (section 2.2.2).
- **Hidden potential.** A high growth rate on some low grades (research §3) is either the `HeroDef`'s visible `growth_rate` (DIV-10) or a hidden `GROWTH_POINTS` talent (section 2.5). `GROWTH_POINTS` go into `StatBonus` at each level-up, so redistribution never moves them.

**Hand-offs between sections.**

| From | To | What |
|---|---|---|
| Section 2 traits (`traits.tags`) and behaviours (section 2.6) | Section 3 triggers | Tags `TEMPER_BERSERK`, `TEMPER_COMPOSED`, `DUAL_MIND`, `HIDDEN_POWER`. `status_triggers.requires_tag` matches a held trait's tag, a behaviour flag, a class, skill or enemy tag. |
| Section 2 `STATUS_RESIST` effects | Section 3 | On the `DREAD` group or one of its members, the summed resistance becomes nerve: `nerve += sumBp / STATUS_RESIST_BP_PER_NERVE`. On any other status, it shortens that status's duration by `sumBp`, with the sum capped at `STATUS_RESIST_CAP_BP` (tolerance, not immunity; research §5). Resistance never rolls a chance. |
| Section 4 bands (`inner_bands.nerve`) | Section 3 nerve | `InnerLife.NerveModifier(h)` = the morale band's `nerve` + the stress band's `nerve` (seeds: `INSPIRED` +5, `LOW` -3, `DESPONDENT` -6; `STRAINED` -3, `CRITICAL` -6; others 0). 0 in M0. |
| Section 3 `StatusApplied` | Section 4 stress | `statuses.stress_on_apply` (seed Fear 100, Panic 200, Despair 400, others 0). |
| Section 4 bonds | Section 3 Frenzy | "Hostile" means bond tier `HOSTILE`. |
| Section 2 `INNER_LIFE` effects | Section 4 modifiers | Targets `STRESS_GAIN`, `STRESS_REST`, `MORALE_LOSS`, in basis points; section 4 reads them as `StressGainPct = sumBp / 100` and so on. |
| Section 2 `former_lives.bond_tags` and `traits.tags` | Section 4.5 `bond_affinity` | Tag pairs that start warm or cold. |

**RNG.** Only one rule in this spec draws a random number: hostility at first sight on summon (section 4.5), from the named stream `inner_life` (contract 2), at the published chance `BOND_SUMMON_HOSTILE_PCT`, shown in the hero help text and on the summon reveal. Stats, growth, traits, talents, memories and statuses draw nothing.

**Scalars.** Every scalar constant in this spec is a row in the `constants` table (09). Bands and tiers have their own tables (`inner_bands`, `bond_tiers`, `trait_grades`).

## 6. Growth: levels, synthesis, promotion, skills and class change

Not yet written. Owned by #344 and its sub-issues: levels, EXP and level caps (#363), synthesis and conversion (#364), promotion (#365), skills (#366) and class change (#367). Sections 1-5 name the hooks those specs fill: `GrantGrowthPoints` and `HeroLeveledUp.PointsGained` (#363), `StatBonus` and total stat points as the transfer basis (#364), `PromotionStatGain` and `RestoreMemories` (#365), skill tags, `skill_pct` and `Skills.Grant` (#366), and the class rules of section 1.4 (#367).

## Divergences from the source

Rows in `specs/11-source-divergences.md`:

| Id | Section | Summary |
|---|---|---|
| DIV-09 | 1.3.2 | Level-ups only add points; a stat falls only on a paid-in-gold redistribution |
| DIV-10 | 1.3.3 | Growth rate fixed per `HeroDef`, shown after the first level-up |
| DIV-11 | 2.5 | Talents fixed per `HeroDef`; count, condition and progress shown |
| DIV-12 | 2.4 | Seal traits come from the hero's own former life, not another world |
| DIV-13 | 2.2 | Every former life gets the same starting stat budget |
| DIV-14 | 2.6 | Memories apply once per grade and never lower a stat |
| DIV-15 | 3.3 | Fear, Panic and Despair are bands of a visible pressure value, forecast before assignment |
| DIV-16 | 3.1, 3.6, 5 | Statuses never scale the HP pool; summed status percentages clamp at -95% / +1000% |
| DIV-17 | 3.4 | Frenzy attacks only the hostile partner, for a fixed time |
| DIV-18 | 3.9 | Status alerts in the battle view, lobby chips and reports, never pushed |
| DIV-19 | 4 | Morale, stress, loyalty and bonds are visible numbers and bands |
| DIV-20 | 4.4, 4.8 | Stress death only after two seen warnings and a countdown; refusals follow fixed visible rules |
| DIV-21 | 4.7 | Jealousy only among party-mates |
| DIV-22 | 4.7 | Loyalty and favourability are one value |

## Decisions

Owner decisions on #342 (2026-11-07), recorded as 00 decisions 136-139:

- **Stress death (decision 136).** MVP. A hero dies of stress only after two warnings the player has seen (the `CRITICAL` alert, then the breakdown) and a `STRESS_GRACE_H` (24 h) countdown that starts only once the breakdown has been seen. One free `OrderRest` always saves the hero (section 4.4).
- **Talents (decision 137).** Fixed per `HeroDef`, not rolled per copy. The sheet shows the count, each reveal condition and its progress; the reward stays hidden until revealed (section 2.5).
- **Redistribution (decision 138).** MVP, as focus presets for gold (section 1.3.2).
- **Refusals (decision 139).** P1, by fixed visible rules shown before assignment, never by chance (section 4.8).

Agent defaults, open to the owner's veto in review (`CLAUDE.md` rule 1), recorded as 00 decisions 140-146:

- **140.** Frenzy attacks only the hostile partner, for `STATUS_FRENZY_TICKS` (DIV-17).
- **141.** The opening-status forecast is shown as a band per hero, not as the raw pressure number.
- **142.** No auto-rest party setting in MVP. Revisit with soft-launch data, possibly as a manager upgrade (#376).
- **143.** A memory never lowers a stat. Behaviour and inner-life effects may go either way (DIV-14).
- **144.** Claimed former lives are P1 and off in MVP content (section 2.6.3).
- **145.** The reused cast's rarity bands map to 1★-5★ (Common 1★ to Mythic 5★); #391 has the final say (section 1.1).
- **146.** The cast's Common and Rare mage concepts arrive as `NOVICE` with a `CASTER` calling; `MAGE` stays summon-only for 3★ and up (section 1.4).

Other calls made in the drafts, also vetoable: four stats with `VIT` for stamina, monotone automatic allocation, the trait letter scale and `SEAL_SLOTS`, an equal former-life budget, inner-life values on a 0-10,000 scale with a 60 s pulse, symmetric bonds, `SURGE` for hidden-power awakening at P1, and every status ending at battle end.

## Open questions

Each has the default we take if nobody answers.

1. Split `AGI` into `AGI` and `DEX`? **Default: no.** A fifth stat is a save-schema change, cheaper to add later than to remove. (Section 1.)
2. Should promotion raise the growth rate (`grades.growth_bonus`)? **Default: 0 at every grade** until #365 and #348 tune it.
3. What does `HeroAwareness` change in battle? **05 and #372 decide.** Default: no effect in M0.
4. The source's "Lv20 with stats totalling 100" (research §9.1) doesn't fit a 46-point 1★ start plus 4 a level (122 at Lv20). **Default: keep 4 a level** and let #348 fit the curve.
5. Is a talent active before it is revealed? **Default: no.** The reveal is the unlock.
6. Can traits move between heroes through synthesis, conversion or seal items? **Default: no in MVP, P2.** #364 may revisit.
7. Do life traits level? **Default: no in MVP.** P1 could tie it to crafting (#369).
8. Are revealed talents remembered across copies in a hero catalog? **Default: no**; a catalog is #379's call.
9. Surge at the source's +1000% can carry a floor. **Default: keep it at P1** and let #348 tune it.
10. Do enemies use the pressure model? **Default: no**; enemies get statuses only from explicit trigger rows.
11. Do any statuses outlast a battle? **Default: no**; revisit if #374's 48-hour exploration wants injuries.
12. Seed values for pressure, threat and nerve are guesses until #348 and 05's floor `threat` column (#370). **Default: the seeds below**, with the bots checking that 1★ heroes start afraid on floor 1 and 3★ heroes don't.
13. Synthesis witness penalty size. **Default: morale -100 per consumed hero, capped at -400 per command** (#348 tunes).
14. Push notifications for the stress warnings while the app is closed. **Default: P1 local notifications** (07, #385); fairness doesn't need them because the countdown waits for the player.
15. Asymmetric bonds. **Default: symmetric in MVP and P1.**
16. The bond table grows as `n(n-1)/2` pairs. **Default: fine up to the lodging cap** (#375); revisit storage above about 300 heroes.

## Acceptance criteria

**Hero sheet (section 1)**

1. The content validator rejects a `HeroDef` with `base_grade` outside `1..SUMMON_GRADE_MAX`, a `NOVICE` class above `NOVICE_MAX_GRADE`, a non-`NOVICE` class at or below it, or a `CHANGE_ONLY` starting class.
2. The validator rejects a `stat_focus` row with a negative weight or a zero sum, a `classes` row whose `default_focus` is not in its `allowed_focus`, and a `stats` table that doesn't have exactly one row per `StatId`.
3. Unit test: `AllocatePoints` with each seed focus, from 0 to 400 points one at a time, never lowers any stat, keeps `Sum(alloc) == points`, and gives a zero-weight stat no points.
4. Unit test: allocating N points in one call gives the same array as N calls of one point. `RedistributeStats` to the hero's current focus from a clean history returns `NO_CHANGE`.
5. Unit test: for a hero with no gear, traits or statuses, `Max == Core * classPct / 100` (truncated) and `Current == Max`. With a -30% status, `Current == Max * 70 / 100`. No value is ever negative.
6. Unit test: `TotalStatPoints` changes on level-up by exactly `GrowthPerLevel` and does not change on equip, unequip, status changes or redistribution.
7. Unit test: `RedistributeStats` keeps `Sum(GrowthAlloc) == GrowthEarned`, leaves `StatBonus` and base unchanged, debits `STAT_REDISTRIBUTE_GOLD_PER_LEVEL * level` gold with reason `STAT_REDISTRIBUTE`, and rejects with each listed reason when it applies, changing nothing.
8. Determinism: two sims given the same content and commands produce identical `GrowthAlloc` arrays and the same `StateHash`. No stat code draws from any RNG stream.
9. Two heroes of the same `HeroDef` summoned in one session have identical core stats.
10. Changing `base_str` of a `HeroDef` in content changes the core STR of existing heroes of that def by the same amount after reload. `GrowthEarned` and `GrowthAlloc` don't change.
11. Every hero's role is defined, and `RoleOf` returns its class's role. Searching the sim for a `StatId` or class literal outside the enum and content reads finds nothing (code review checklist).
12. `SCR_HERO`'s view-model exposes every M0 field in 1.6. The growth rate reads `?` until `GrowthEarned > 0`. (Engine-free view-model test; the Unity screen is only type-checked.)

**Former lives and traits (section 2)**

13. **M0:** every `HeroDef` in content has a former life. `SCR_HERO`'s view-model and the summon reveal expose its name, icon and description key.
14. **M0:** a newly acquired hero holds exactly one `LIFE` trait. Its stats before any level-up equal the `HeroDef` base stats plus the life trait's `STAT_FLAT` values, after section 1's pipeline (unit test, per seed former life).
15. The content validator rejects a former life whose life-trait `STAT_FLAT` sum differs from `FORMER_LIFE_START_POINTS`, and every other 2.8 violation (one failing fixture per rule).
16. Acquiring, revealing talents, opening slots and restoring memories draw no RNG. A test runs a 30-day bot session and asserts that no stream counter moves on these paths.
17. Two heroes of the same `HeroDef` that receive the same command history end with identical `traits`, `talentProgress`, `talentRevealedMask`, `memoryRestoredMask` and `behaviourFlags`, and the same `StateHash` contribution.
18. A hidden talent contributes nothing to `TraitModifiers` before it is revealed and its full effect after (unit test). A reveal during a battle does not change that battle's stats (05 test).
19. A hidden talent's reward id and name are absent from `SCR_HERO`'s view-model until `TalentRevealed`. Its condition kind, `reveal_count` and progress are present.
20. A hero whose base grade is above `TALENT_HIDDEN_MAX_GRADE` has every talent revealed on acquisition.
21. Talent counters advance the same online and through offline catch-up for the same battles (contract 8 equivalence test). The offline report lists talents revealed while away.
22. Seal slots: after promoting to grade `g`, the hero holds a `SEAL` trait for exactly the slots `i < SEAL_SLOTS[g]` that have rows. Each new one emits `SealSlotOpened` and `TraitGained` exactly once.
23. Memories: a promotion to `g` with a `memory_scenes` row emits exactly one `MemoryRestored` and applies its effects in `ordinal` order. A second pass over the same grade does nothing. A failed promotion emits none.
24. A hero acquired at grade `g` has `memoryRestoredMask` bits set for every authored grade `<= g`, with `AtAcquisition = true`, and the client plays no cutscene for them.
25. No memory effect lowers a stat: the validator rejects any memory-granted trait with a negative `STAT_FLAT` or `STAT_PCT`.
26. `STAT_PCT` sums are clamped to `±TRAIT_STAT_PCT_CAP_BP` per stat (unit test with stacked traits).
27. `HP_BELOW_BP` fires once per battle, only while the hero's HP is above 0, and lasts `duration_s * TICK_HZ` ticks (05 battle test).
28. Every number in section 2 is read from `ContentDb`. A grep test finds no literal for section 2's constants in `HeroCat.Sim`.

**Statuses (section 3)**

29. Unit: a hero whose opening pressure is at or above `STATUS_FEAR_AT` gets `FEAR` before the first `PreAction`, and `ForecastOpeningStatus` returns the same status for the same state. (M0)
30. Unit: with `FEAR` at -30, every combat stat except max and current HP equals `stat * 70 / 100` truncated; removing the status restores the exact original values. (M0)
31. Unit: shock decays one point per `STATUS_SHOCK_DECAY_TICKS`; once pressure is below `STATUS_FEAR_AT - STATUS_BAND_HYSTERESIS` and `min_ticks` have passed, `FEAR` ends with `CONDITION_CLEARED`. (M0)
32. Unit: an ally's death adds `STATUS_SHOCK_ALLY_DEATH` to every living hero in that party and none to other parties. (M0)
33. Unit: with Fear active, entering the Panic band emits `StatusEnded(FEAR, REPLACED)` then `StatusApplied(PANIC)`; `FEAR` and `PANIC` are never both active on any tick of a 1,000-battle bot run. (MVP)
34. Unit: Despair (-80) plus Exhaustion (-90) gives an effective modifier of exactly `STATUS_STAT_PCT_MIN`. (MVP)
35. Unit: Berserk that expires is followed by `EXHAUSTION` on the same tick; Berserk ended by death or battle end is not. A `TEMPER_BERSERK` hero never holds `PANIC` or `DESPAIR` while Berserk is active. (MVP)
36. Unit: a non-`DUAL_MIND` hero never holds `BERSERK` and `COMPOSURE` together; a `DUAL_MIND` hero can, and its Berserk has no `RECKLESS` flag. (MVP, dual mind P1)
37. Unit: an attack with `apply_every_n_hits = 3` applies its status on hits 3, 6, 9 and no others. (MVP)
38. Unit: `BLEEDING` stacks up to `STATUS_BLEED_MAX_STACKS`; DoT per interval equals `maxHp * permille * stacks / 1000`; a healing potion ends Bleeding but not Poison; an antidote ends Poison. A DoT death reaches #373 with cause `POISON` or `BLEEDING`. (MVP)
39. Unit: in a party of five where every death causes contagion, the cascade ends within one `PostAction` and each hero's contagion fires at most once per status per battle. (MVP)
40. Unit: at battle end every active status emits `StatusEnded(BATTLE_ENDED)`; the next battle starts with no statuses and zero shock. (M0)
41. API/test: `StatusSystem` takes no RNG stream; the battle stream counter after a golden battle is the same as in a variant of that battle whose only difference is a status row's `stat_pct`. (M0)
42. Determinism: the same save and command log give byte-identical status event sequences on two runs, and online ticks and offline catch-up give the same sequence for the same span (contract 8). (M0)
43. Content validation (09): every `status_triggers.status_id`, `replaces_status`, `on_end_status` and `blocks` entry names an existing status; group ranks are unique per group; no status row is read with a hard-coded number in code (grep check for literals in `StatusSystem`). (M0)
44. Client checklist: `SCR_PARTY` shows each hero's forecast opening status; `SCR_BATTLE` shows an icon and log line for every `FEED` status and a toast for every `ALERT` status; `SCR_OFFLINE_REWARD` lists each hero's worst dread status and near-death count. Type-checked only in cloud sessions (Unity can't run there). (M0 battle view and forecast, MVP report)
45. Bot: on floor 1 with M0 content, every 1★ hero starts in `FEAR` and no 3★ hero at the floor's enemy level does. (M0)

**Inner life (section 4)**

46. With the same save and command log, two runs give identical inner-life values, bonds and events, including through offline catch-up (unit test plus `StateHash` golden).
47. Online ticks and offline catch-up over the same span give identical inner-life values (bot test, contract 8).
48. A hero fighting a 100% human floor gains `STRESS_BATTLE_PER_PULSE * (100 + STRESS_HUMAN_BONUS_PCT) / 100` per pulse; a hero with no assignment loses `STRESS_REST_PER_PULSE` per pulse at 0% lodging bonus (unit test).
49. No hero dies of stress unless a `StressBandChanged` to `CRITICAL` and a `HeroBrokeDown` were emitted for it, and at least `STRESS_GRACE_H` of sim time passed after `GraceStartTick` (property test over the 20-seed bot sweep).
50. A breakdown that starts during offline catch-up has `GraceStartTick` equal to the tick of the `ClaimOfflineReward` that showed it (unit test).
51. Issuing `OrderRest` at any point in a breakdown's first `STRESS_GRACE_H / 2` prevents the death, with zero lodging bonus and no food (unit test); the content validator rejects tables that break the inequality in 4.4 step 5.
52. A broken-down or resting hero is rejected by `SetParty` with `HERO_BROKEN_DOWN` or `HERO_RESTING`, and a hero that breaks down mid-battle stays in that battle until it ends (unit test).
53. Synthesizing k heroes changes each other living hero's morale by `-min(k * MORALE_SYNTH_WITNESS, MORALE_SYNTH_CAP)` (unit test).
54. A hero's inner-life % equals the clamped sum of its morale band, stress band and bond tiers with its party-mates, and section 1's pipeline applies it once (unit test).
55. Bonds formed on summon depend only on the `inner_life` stream and roster order: the same summon on the same save forms the same bonds (unit test).
56. Changing any inner-life value in `constants`, `inner_bands` or `bond_tiers` changes behaviour with no code change; no inner-life number appears as a literal in `HeroCat.Sim` (code review checklist and a content-override test).
57. (P1) A party containing a hero that meets R1, R2 or R3 is rejected by `AssignFloor` with `HERO_REFUSES`; one that becomes so mid-assignment stops at the next battle boundary with `PartyInoperable` (unit test).
58. (P1) Removing a strike's ringleader from the roster makes its contagion followers stop refusing in the same command (unit test).
59. (P1) Gifting one hero of a five-hero party leaves the other four `JEALOUSY_MORALE` lower after the next rollover, and gifting all five leaves none jealous (unit test).
**Stat pipeline (section 5)**

60. Unit: for a hero in battle with gear, traits, an inner-life modifier and a status, current equals the section 5 formula step by step, and the layers apply in the stated order; removing each layer restores the previous value exactly.
61. Unit: no inner-life value or status changes a hero's battle max HP; only max `VIT` does.
62. Unit: a `STATUS_RESIST` of `STATUS_RESIST_BP_PER_NERVE * k` on the `DREAD` group adds exactly `k` nerve; a resistance on `POISON` shortens its duration by that many basis points, capped at `STATUS_RESIST_CAP_BP`; neither draws RNG.
63. Determinism: over a 30-day bot run, the only stream counter that this spec's code advances is `inner_life`, and only on `HeroAcquired`.

## Constants introduced

These are lifted into 00 section 6.2 as one pointer row per section; this table is the source of truth for the values.

| Name | Value | Kind | Notes |
|---|---|---|---|
| **Hero sheet (section 1)** | | | |
| `NOVICE_MAX_GRADE` | 2 | Src | `HeroDef`s at or below this grade arrive as `NOVICE`; 3★ and up arrive with a class (research §3). |
| `STAT_COUNT` | 4 | I | `STR`, `INT`, `VIT`, `AGI`. A schema constant, not tuning: changing it is a save migration (02). |
| `STAT_PCT_BASE` | 100 | I | Percent modifiers are integers with 100 = no change. |
| `GROWTH_DEFAULT[g]` | 1★ 4 / 2★ 4 / 3★ 5 / 4★ 5 / 5★ 6 | Src/S | `grades.growth_default`, an authoring default for `HeroDef.growth_rate`. 1★ and 4★ from research §3; the rest are seeds. |
| `GROWTH_BONUS[g]` | 0 at every grade | S | `grades.growth_bonus`, added per level on top of `growth_rate`. A lever for #365 and #348. |
| `CLASS_STAT_PCT` | per `classes` row (1.4) | S | 90-125. Validated by #348. |
| `STAT_POWER_WEIGHT[s]` | 1 each | S | `stats.power_weight`. 05 may retune it (#371). |
| `STAT_REDISTRIBUTE_GOLD_PER_LEVEL` | 200 | S | MVP. Gold only (DIV-05). Sized against gold income in #379 and #348. |
| `HERO_AWARENESS_MAX` | 1 | Src | The first step of the research tree that lets heroes see themselves (research §3, §8). Higher levels are P1 (#377). |
| **Former lives and traits (section 2)** | | | |
| `FORMER_LIFE_START_POINTS` | 4 | S | Flat stat points every life trait gives at acquisition. Equal for all former lives (DIV-13). |
| `TALENTS_MAX_PER_HERO` | 3 | S | Hidden or revealed talents per `HeroDef` |
| `TALENT_HIDDEN_MAX_GRADE` | 2 | Src | Talents start hidden on base grades up to this one. Higher grades arrive with theirs revealed (research §3). |
| `SEAL_SLOTS[g]` | 1★ 0 / 2★ 1 / 3★ 1 / 4★ 1 / 5★ 2 / 6★ 2 / 7★ 3 | S | The source shows "1/1" and leaves the count unexplained (research §9.1, §12) |
| `TRAIT_LEVEL_MAX` | 5 | S | Highest trait level. The source shows Lv3 (research §9.1). |
| `trait_grades` | D, C, C+, B, B+, A, A+, S, with `max_level` 2/3/3/4/4/5/5/5 | S | Letter scale for traits (the source shows B+, research §9.1). A table, not a single constant. |
| `TRAIT_STAT_PCT_CAP_BP` | 10000 | S | Cap on summed `STAT_PCT` from traits, per stat (±100%) |
| `BASIS_POINTS` | 10000 | I | 1 bp = 0.01%. Shared with any spec that uses bp. |
| **Statuses (section 3)** | | | |
| `STATUS_STAT_PCT_MIN`, `STATUS_STAT_PCT_MAX` | -95, +1000 | S | Clamp on summed status percentages (DIV-16). |
| `STATUS_FEAR_AT`, `STATUS_PANIC_AT`, `STATUS_DESPAIR_AT` | 10, 25, 45 | S | Pressure thresholds for the dread bands (3.3). M0 uses only `STATUS_FEAR_AT`. |
| `STATUS_BAND_HYSTERESIS` | 5 | S | A band is left only below its threshold minus this. |
| `STATUS_DREAD_MIN_TICKS` | 30 | S | `min_ticks` of the dread rows: 3 s before a band can drop. |
| `STATUS_THREAT_PER_LEVEL_GAP` | 2 | S | Threat per level the floor's enemies are above the hero. |
| `STATUS_NERVE_BY_GRADE[g]` | 1★ 0 / 2★ 4 / 3★ 10 / 4★ 14 / 5★ 18 / 6★ 22 / 7★ 26 | S | 1★ heroes panic, 3★ fight from day one (research §3). |
| `STATUS_NERVE_PER_BATTLE`, `STATUS_NERVE_BATTLES_CAP` | 1, 10 | S | Nerve from battles survived. |
| `STATUS_SHOCK_MAX` | 60 | S | Shock clamp. |
| `STATUS_SHOCK_DECAY_TICKS` | 10 | S | One shock point lost per second. |
| `STATUS_SHOCK_ALLY_DEATH` | 15 | S | M0. |
| `STATUS_SHOCK_LEADER_DEATH` | 10 | S | Extra, on top of the ally death. |
| `STATUS_SHOCK_PARTY_WIPED` | 10 | S | To other parties in the same battle. |
| `STATUS_SHOCK_RELIEF_KILL` | 2 | S | Shock removed per enemy the party kills. |
| `STATUS_CONTAGION_PANIC`, `STATUS_CONTAGION_DESPAIR` | 5, 10 | S | `contagion_shock` of the two rows. |
| `STATUS_BERSERK_PCT`, `STATUS_BERSERK_SKILL_PCT` | +50, -50 | S | Power stats up, skill effect down (research §3). |
| `STATUS_BERSERK_TICKS`, `STATUS_BERSERK_HP_PCT` | 300, 30 | S | 30 s; low-HP trigger. |
| `STATUS_EXHAUSTION_TICKS` | 200 | S | 20 s after Berserk. |
| `STATUS_COMPOSURE_NERVE` | 15 | S | |
| `STATUS_FRENZY_TICKS` | 200 | S | |
| `STATUS_DOT_INTERVAL_TICKS` | 10 | S | DoT once a second. |
| `STATUS_POISON_PERMILLE`, `STATUS_POISON_TICKS` | 2, 6000 | Src/S | About 10 minutes of useful movement left (research §3): 2‰ a second empties HP in ~8 min. |
| `STATUS_BLEED_PERMILLE`, `STATUS_BLEED_MAX_STACKS`, `STATUS_BLEED_TICKS` | 5, 3, 150 | S | |
| `STATUS_NEAR_DEATH_HP_PCT`, `STATUS_NEAR_DEATH_EXIT_PCT` | 20, 25 | S | |
| `STATUS_SURGE_PCT`, `STATUS_SURGE_TICKS` | +1000, 300 | Src/S | Research §9.1, §10. P1. |
| **Inner life (section 4)** | | | |
| `INNER_MAX` | 10000 | S | Scale of morale, stress, loyalty; bonds run ±. Displayed / 100. |
| `INNER_PULSE_S` | 60 | S | Inner-life update period in sim seconds. |
| `INNER_STAT_PCT_MIN`, `INNER_STAT_PCT_MAX` | -20, +15 | S | Clamp on the combined inner-life stat %. |
| `MORALE_START`, `MORALE_BASELINE`, `MORALE_DRIFT_PER_DAY` | 6000, 5000, 300 | S | |
| `MORALE_SYNTH_WITNESS`, `MORALE_SYNTH_CAP` | 100, 400 | S | Per consumed hero; cap per command. Research §3 direction. |
| `MORALE_CONVERT_WITNESS`, `MORALE_CONVERT_CAP` | 200, 600 | S | Conversion is feared more (research §9.3). |
| `MORALE_EXECUTION_WITNESS` | 800 | S | P1. |
| `MORALE_ALLY_DEATH`, `MORALE_LOBBY_DEATH` | 500, 100 | S | |
| `MORALE_FACILITY_UPGRADE`, `MORALE_FIRST_CLEAR` | 300, 200 | S | Research §3 direction. |
| Morale bands | 8000 / 4000 / 2000 at +5 / 0 / -5 / -10 % | S | `inner_bands`. |
| `STRESS_START`, `STRESS_REVIVE` | 1000, 5000 | S | |
| `STRESS_BATTLE_PER_PULSE`, `STRESS_HUMAN_BONUS_PCT` | 2, 200 | S | Human-shaped enemies raise stress faster (research §7). |
| `STRESS_REST_PER_PULSE`, `STRESS_REST_ORDER_PCT`, `STRESS_REST_DONE` | 6, 300, 4000 | S | |
| `STRESS_FOOD_PER_DISH`, `STRESS_FOOD_DISH_CAP` | 150, 5 | S | Food variety lowers stress (research §3, §8). |
| `STRESS_ALLY_DEATH`, `STRESS_ALLY_DEATH_BONDED` | 800, 1500 | S | |
| `STRESS_SYNTH_TARGET`, `STRESS_SYNTH_WITNESS`, `STRESS_CONVERT_WITNESS`, `STRESS_EXECUTION_WITNESS` | 200, 50, 100, 300 | S | Execution P1. |
| Stress bands | 4000 / 8000 / 10000 at 0 / -5 / -10 % | S | `inner_bands`. |
| `STRESS_RECOVER` | 6000 | S | Breakdown ends below this. |
| `STRESS_GRACE_H` | 24 | S | Must be ≥ `OFFLINE_CAP_H`; validator-checked with 4.4 step 5. |
| `BOND_SUMMON_HOSTILE_PCT`, `BOND_SUMMON_HOSTILE_VALUE` | 3, -5000 | S | Hostility on summon (research §3). |
| `BOND_AFFINITY_PULL_VALUE` | 5000 | S | Affinity pulls (research §4, #361). |
| `BOND_TOGETHER_PER_PULSE`, `BOND_HOSTILE_DRIFT_PER_DAY` | 1, 500 | S | |
| Bond tiers | ±3000 / ±7000 at ±3 / ±6 % | S | `bond_tiers`; intimacy bonus (research §3). |
| `BOND_PARTY_PCT_CAP` | 12 | S | |
| `LOYALTY_START`, `LOYALTY_FIRST_CLEAR`, `LOYALTY_LEADER_CLEAR`, `LOYALTY_CULL_WITNESS` | 3000, 200, 200, 100 | S | P1. |
| `LOYALTY_REFUSE_BELOW`, `LOYALTY_HOLD` | 2000, 7000 | S | P1. |
| `GIFT_MORALE`, `GIFT_LOYALTY`, `GIFT_DAILY_CAP` | 300, 300, 3 | S | P1. |
| `GIFT_LIKED_PCT`, `GIFT_DISLIKED_PCT` | 200, -50 | S | P1. |
| `JEALOUSY_MORALE`, `JEALOUSY_MORALE_CAP` | 200, 600 | S | P1. |
| `DUEL_WIN_MORALE`, `DUEL_LOSE_MORALE` | 300, 300 | S | P1. |
| **Stat pipeline (section 5)** | | | |
| `STATUS_RESIST_BP_PER_NERVE` | 100 | S | Trait resistance on the `DREAD` group becomes nerve at this rate: 1% = 1 nerve. |
| `STATUS_RESIST_CAP_BP` | 7500 | S | Cap on summed resistance to one status: at most 75% shorter. Tolerance, not immunity (research §5). |
