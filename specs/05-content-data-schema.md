# 05 - Content Data Schema

Scope: how every number in `specs/00` section 2 gets from a spreadsheet into an immutable `ContentDb` object that the sim, the Unity client and the bot harness all read. This is the implementation of `specs/00` section 5 contract 3 ("content is data"). Constants are referred to by their `specs/00` names; the values appear only inside the example JSON in sections 14 and 15. If a value here disagrees with `specs/00`, `specs/00` wins.

## 1. Scope and principles

1. **One source of truth.** A Google Sheet, one tab per table. Everything downstream (CSV, JSON, `ContentDb`, the Python models in `tools/`) is derived. Nobody edits JSON by hand.
2. **No literals in code.** The sim receives a `ContentDb` and reads `db.Constants.HP_G`-style properties or table rows. A reviewer should be able to grep `HeroCat.Sim` for numeric literals other than 0, 1 and array indices and find nothing balance-related.
3. **Fail at build time, not at runtime.** The converter rejects bad content with a listed, coded error (section 8). The C# loader re-checks the subset it depends on so a hand-edited or stale file cannot crash the sim deep inside a tick.
4. **Deterministic output.** Same CSV input produces byte-identical JSON. This keeps `Content/` diffs reviewable and lets the harness (`specs/09`) hash content as part of its determinism check.
5. **Immutable at runtime.** `ContentDb` and every `*Def` it exposes are read-only after `Load`. Hot reload (section 10) builds a new `ContentDb`; it never mutates the live one.

Tags: everything is P0 unless marked **P1** or **P2**.

## 2. Pipeline overview

```
Google Sheet (one tab per table)
   |  File > Download > CSV, one tab at a time        (P0, manual)
   |  tools/content_pull.py <sheet_id>                (P1, scripted export)
   v
content_src/<table>.csv                                committed to git
   |  python3 tools/content_build.py --src content_src --out Content
   v
Content/manifest.json + Content/<table>.json           committed to git
   |  copied/linked into the Unity project (section 10)
   v
ContentDb.Load(IContentSource)                         at startup, once
   |
   +--> Simulation (HeroCat.Sim)
   +--> Unity client view-models (specs/07)
   +--> Bot harness (specs/09)
   +--> tools/*.py (section 12, after the described change)
```

The CSVs are committed as well as the JSON so that CI can rebuild `Content/` without network access and fail the build if the committed JSON is stale (`content_build.py --check`, section 7).

## 3. Authoring conventions

### 3.1 Sheet and CSV layout

- Tab name = table name = CSV file name = JSON file name (`heroes` -> `content_src/heroes.csv` -> `Content/heroes.json`).
- Row 1 is the header. Column names are `snake_case`, must match the schema in section 5 exactly, and may appear in any order.
- Any column whose header starts with `#` is a designer note and is ignored by the converter (`#notes`, `#todo`).
- Blank rows (every cell empty) are skipped. A row with some cells empty is a data row and is validated.
- Cell text is trimmed. Decimal separator is `.` regardless of the sheet locale (set the sheet to en-US to avoid `1,07`).
- Row order in the CSV is preserved in the JSON and is meaningful only where a column named `sort_order` or an explicit ordering rule says so.

### 3.2 Column types

| Type | CSV form | JSON form | Notes |
|---|---|---|---|
| `int` | `50` | number | Must be an integer literal, no `50.0`. Range `[-2^31, 2^31-1]`. |
| `double` | `1.07`, `0.0325`, `10` | number | Parsed as IEEE double, emitted with Python `repr` (shortest round-trip). `10` is accepted and emitted as `10.0`. |
| `bool` | `true` / `false` | boolean | Lowercase only. |
| `string` | any text | string | Free text. Not used for anything the player sees (use `key`). |
| `id` | `hero_c_01` | string | `^[a-z][a-z0-9_]{0,63}$`. Primary key of its table; stable forever once shipped. |
| `ref(<table>)` | `hero_standard` | string | Must equal an `id` in the named table. |
| `enum(<Name>)` | `E` | string | One of the values in section 3.3. Case-sensitive. |
| `key` | `hero.c_01.name` | string | Localisation key, `^[a-z0-9_]+(\.[a-z0-9_]+)+$`. Section 11. |
| `color` | `#9B59B6` | string | `#RRGGBB`, uppercase hex. Kept as a string in `ContentDb`; the client parses it. |

Nullable columns are marked `?` in the schema tables (`icon_id?`). A nullable column accepts an empty cell and emits JSON `null`. Any other empty cell is error `E011`.

### 3.3 Enums

Enums are defined in C# (`HeroCat.Sim.Content.Enums`) and mirrored in the converter. Adding an enum value is a code change plus a converter change plus a MINOR content version bump; removing one is a MAJOR bump (section 6.3).

| Enum | Values (in order) | Used by |
|---|---|---|
| `Tier` | `C`, `R`, `E`, `L`, `M` | gear_rarity, hero_tiers, heroes, banners, boss_rules |
| `BannerKind` | `hero`, `pet` (P1) | banners |
| `RngStream` | `combat`, `gear_drop`, `gear_rarity`, `enhance`, `gacha_hero`, `gacha_pet` (P1) | banners. Matches `specs/00` section 5 contract 2. |
| `CurveId` | `enemy_hp`, `gold_per_kill`, `stat_cost` | curves |
| `QuestType` | `login`, `kill_mobs`, `clear_boss`, `do_pulls` | daily_quests |
| `GrantTrigger` | `profile_created`, `stage_reached`, `first_boss_cleared` | tutorial_grants |
| `GrantType` | `gems`, `free_pulls`, `gold`, `hero` | tutorial_grants |
| `ConstantType` | `int`, `double`, `bool` | constants |

`Tier` order is load-bearing: the gacha renormalisation in `specs/01` section 3.1 and "E+" / "L+" comparisons rely on `C < R < E < L < M`.

### 3.4 Ids and keys

- Ids are never reused and never renamed after they have shipped in a `content_version`; saves reference them (`specs/06`). To retire content, add an `enabled` column where the table has one (banners) or leave the row in place and stop referencing it.
- Hero ids follow `hero_<tier lowercase>_<nn>` in P0 (`hero_c_01` ... `hero_m_03`). The pattern is a convention, not a validation rule; the converter only enforces the `id` regex and uniqueness.
- Localisation keys follow `<table singular>.<id without table prefix>.<field>`: `hero.c_01.name`, `banner.hero_standard.name`, `quest.kill_mobs_500.name`.

## 4. Where every `specs/00` section 2 entry lives

Not every entry in `specs/00` section 2 is a scalar, so not every one is a row in `constants`. This table is the authoritative mapping and the list the converter checks against (rule `E040`/`E042`). The three names marked *(named here)* are literals that `specs/00` only gives inside a formula; this spec names them so they can live in data. `specs/00` should adopt the names (open decision 1).

| `specs/00` entry | Lives in | Type |
|---|---|---|
| `TICK_HZ` | `constants` | int |
| `BASE_ATK` | `constants` | double |
| `ATTACK_INTERVAL_TICKS` | `constants` | int |
| `HP0`, `HP_G` | `curves[enemy_hp].base`, `.growth` | double, double |
| `KILLS_PER_STAGE` | `constants` | int |
| `SPAWN_DELAY_TICKS` | `constants` | int |
| `BOSS_EVERY` | `constants` | int |
| `BOSS_HP_MULT` | `boss_rules.hp_mult` | double |
| `BOSS_TIME_TICKS` | `boss_rules.time_ticks` | int |
| `BOSS_GOLD_MULT` | `boss_rules.gold_mult` | double |
| Boss gear drop 0.10, at least Rare | `boss_rules.gear_drop_p`, `.gear_min_rarity` | double, enum(Tier) |
| `GOLD0`, `GOLD_G` | `curves[gold_per_kill].base`, `.growth` | double, double |
| `STAT_PER_LVL` | `constants` | double |
| `STAT_COST0`, `STAT_COST_G` | `curves[stat_cost].base`, `.growth` | double, double |
| `GEMS_PER_DAY_F2P` | `constants` (read only by the harness; it is a target) | int |
| `PULL_COST_GEMS` | `banners.pull_cost_gems` | int |
| `FREE_PULLS_PER_DAY` | `banners.free_pulls_per_day` | int |
| `OFFLINE_RATE` | `constants` | double |
| `OFFLINE_CAP_H` | `constants` | double |
| `GEAR_SLOTS` | derived: `gear_slots` row count (`ContentDb.GearSlots.Count`) | - |
| `GEAR_DROP_P` | `constants` | double |
| `GEAR_TIER(s) = floor(s/10)*10` | `constants.TIER_WIDTH` *(named here)* | int |
| `GEAR_SLOT_G` | `constants` | double |
| Rarity weights | `gear_rarity.weight` | double |
| Rarity mult | `gear_rarity.mult` | double |
| `ENH_MAX` | `constants` | int |
| `ENH_PER_LVL` | `constants` | double |
| `ENH_SUCCESS(l) = max(0.30, 1 - 0.05*l)` | `constants.ENH_SUCCESS_FLOOR`, `constants.ENH_SUCCESS_STEP` *(named here)* | double, double |
| `ENH_COST(s) = 20 * GOLD(s)` | `constants.ENH_COST_GOLD_MULT` *(named here)* | double |
| Gacha rates | `banners.rates` (CSV columns `rate_c` ... `rate_m`) | double x5 |
| `PITY_SOFT_START`, `PITY_SOFT_STEP`, `PITY_HARD` | `banners.pity_soft_start`, `.pity_soft_step`, `.pity_hard` | int, double, int |
| `M_SHARE_OF_LPLUS` | `banners.mythic_share_of_lplus` | double |
| 10-pull guarantee (at least one E+) | `banners.guarantee_batch_size`, `.guarantee_min_tier` | int, enum(Tier) |
| `HERO_SLOTS` | `constants` | int |
| Base DPS% by tier | `heroes.base_pct` (per hero row) | double |
| `HERO_LVL_CUM` | `hero_level_thresholds.cum_copies` | int per row |
| `HERO_LVL_BONUS` | `constants` | double |
| Pool size by tier | derived: count of `heroes` rows per `(pool_id, tier)` | - |
| Shard value of excess copy (P1) | `hero_tiers.shard_value` | int |
| `CLASS_STAGES` | `class_advances.stage` | int per row |
| `CLASS_MULT` | `class_advances.mult` (every P0 row carries the same value) | double |
| Section 2.6 day/stage targets | `progress_targets` | int, int |

Things in `specs/01` section 5 that are numbers the game reads and therefore also live in content: daily login gems and the three daily quests (`daily_quests`), first-clear boss gems (`boss_rules.first_clear_gems`), the tutorial 1000 gems and 10 free pulls (`tutorial_grants`). The idle chest is P1 and is not in P0 content (open decision 4).

## 5. Tables

Every table below is P0 and required unless marked otherwise. "Key" is the primary key the converter enforces unique. Column order in this spec is the JSON key order the converter emits.

### 5.1 `constants`

Scalar named constants. Heterogeneous by design, so the type is a data column.

| Column | Type | Constraint |
|---|---|---|
| `key` | string | Key. `^[A-Z][A-Z0-9_]*$`. Must be exactly the set in the required list below (`E040`, `E042`). |
| `type` | enum(ConstantType) | Must equal the required type for `key` (`E041`). |
| `value` | per `type` | Parsed according to `type` (`E010`). Range rules in section 8.3. |

Required keys (20) and types. This list is duplicated in `tools/content_build.py` (`REQUIRED_CONSTANTS`) and in C# (`Constants` properties); loader test 2 in section 13 keeps them equal.

```
TICK_HZ int            BASE_ATK double          ATTACK_INTERVAL_TICKS int
KILLS_PER_STAGE int    SPAWN_DELAY_TICKS int    BOSS_EVERY int
STAT_PER_LVL double    GEMS_PER_DAY_F2P int     OFFLINE_RATE double
OFFLINE_CAP_H double   GEAR_DROP_P double       TIER_WIDTH int
GEAR_SLOT_G double     ENH_MAX int              ENH_PER_LVL double
ENH_SUCCESS_FLOOR double  ENH_SUCCESS_STEP double  ENH_COST_GOLD_MULT double
HERO_SLOTS int        HERO_LVL_BONUS double
```

JSON row shape: `{"key": "TICK_HZ", "type": "int", "value": 10}`. The loader maps rows onto the typed `Constants` class (section 9.2); it does not expose a dictionary, so a typo in C# is a compile error rather than a runtime miss.

### 5.2 `curves`

Formula-only approach, justified: `HP(s)`, `Gold(s)` and `StatCost(n)` are all `base * growth^x` over an unbounded domain (stage 550 is day 30; the sim must not break at 2000), and `specs/02` section 7 relies on the closed form for O(1) offline catch-up. A per-stage `stage_curve` table would be 550+ rows of derived numbers that drift from the formula the moment someone edits one cell, and it still could not cover stages past its last row. So P0 stores the two parameters per curve and code evaluates `growth^x` by repeated multiplication into a growable cache (never `Math.Pow`; see `specs/03` section 4 for the cross-machine determinism reason).

| Column | Type | Constraint |
|---|---|---|
| `id` | enum(CurveId) | Key. All three values must be present (`E044`). |
| `base` | double | `> 0`. |
| `growth` | double | `> 1` (`E043`). |

`ContentDb.Curves[CurveId.EnemyHp].Eval(s)` returns `base * growth^s`. Code never reads `base`/`growth` directly except in `Eval` and in tests.

**P2:** `stage_overrides` (stage, hp_mult, gold_mult) for hand-tuned individual stages. Not in P0; leaving a hook costs nothing because `Eval` is the only call site.

### 5.3 `boss_rules`

What a boss stage does. Which stages are boss stages is `constants.BOSS_EVERY`. Keyed by a starting stage so later bosses can differ (P1: boss types), with exactly one row in P0.

| Column | Type | Constraint |
|---|---|---|
| `id` | id | Key. |
| `from_stage` | int | `>= 1`; strictly increasing down the table (`E032`); the first row must have `from_stage = 1` (`E045`). The rule applies to boss stages `>= from_stage` until the next row's `from_stage`. |
| `hp_mult` | double | `> 0`. Boss HP = `hp_mult * HP(s)`. |
| `time_ticks` | int | `> 0`. Despawn after this many ticks. |
| `gold_mult` | double | `> 0`. Boss gold = `gold_mult * Gold(s)`. |
| `gear_drop_p` | double | `[0, 1]`. Drop chance on boss kill. |
| `gear_min_rarity` | enum(Tier) | Boss drops roll `gear_rarity` renormalised over rarities `>= gear_min_rarity`. |
| `first_clear_gems` | int | `>= 0`. Gems granted the first time this boss stage is cleared (`specs/01` section 5). |

P0 data: one row `default`, `from_stage` 1, with `specs/00` values `BOSS_HP_MULT`, `BOSS_TIME_TICKS`, `BOSS_GOLD_MULT`, the boss drop chance and minimum rarity from `specs/00` section 2.3, and 10 first-clear gems from `specs/01` section 5.

### 5.4 `gear_slots`

| Column | Type | Constraint |
|---|---|---|
| `id` | id | Key. P0 rows: `weapon`, `helm`, `chest`, `gloves`, `boots`, `ring`. |
| `sort_order` | int | Unique within table (`E046`); 0-based. This is the index the sim and the save use for the slot. |
| `name_key` | key | |
| `icon_id` | string | Placeholder sprite name in the client. |

The number of rows is `GEAR_SLOTS`. The loader exposes `GearSlots` as a list ordered by `sort_order`, so `sort_order` must be exactly `0..N-1` (`E047`). Saves store gear by `sort_order` index (`specs/06`), which is why the order is a validated contract and not a display hint.

### 5.5 `gear_rarity`

| Column | Type | Constraint |
|---|---|---|
| `rarity` | enum(Tier) | Key. All five values present (`E044`). |
| `weight` | double | `[0, 1]`. Weights sum to 1 within 1e-9 (`E030`). Drawn on the `gear_rarity` RNG stream. |
| `mult` | double | `> 0`. Multiplies the piece's ATK%. Non-decreasing in tier order (`E048`). |
| `display_color` | color | |
| `name_key` | key | |

### 5.6 `hero_tiers`

Per-tier hero facts that are not per-hero. Small, but it is where the P1 shard value and the tier colour belong, and it keeps `heroes` free of repeated columns.

| Column | Type | Constraint |
|---|---|---|
| `tier` | enum(Tier) | Key. All five values present (`E044`). |
| `display_color` | color | |
| `name_key` | key | "Common", "Rare", ... |
| `shard_value` | int | `>= 0`. **P1** consumer (shard shop). Present and populated in P0 data from `specs/00` section 2.4; P0 code tracks the shard counter and spends nothing. |

### 5.7 `heroes`

| Column | Type | Constraint |
|---|---|---|
| `id` | id | Key (`E020`). |
| `tier` | enum(Tier) | |
| `pool_id` | id | Groups heroes into a gacha pool. `banners.pool_id` references this column (`E022`). Not a `ref` to another table; the pool is the set of hero rows sharing the value. |
| `name_key` | key | |
| `desc_key` | key | |
| `icon_id` | string | Placeholder sprite name. P0 uses one per tier (`icon_hero_placeholder_c` ...). |
| `base_pct` | double | `> 0`. DPS% at level 1. P0 data uses the per-tier values from `specs/00` section 2.4 on every row; the column is per row so a future hero can deviate without a schema change. |

Pool ordering (load-bearing for RNG commitment, `specs/01` section 6): within a `(pool_id, tier)` the pool list is the hero ids sorted by ordinal string comparison. `u3 * pool.Count` indexes that list. Appending ids that sort after the existing ones leaves existing indices unchanged; inserting one that sorts earlier shifts them and changes what future pulls on an existing profile yield. That is not a correctness bug (past pulls are already in the save) but it is a MAJOR version bump by the rule in section 6.3 because it changes the meaning of a committed sequence.

### 5.8 `hero_level_thresholds`

| Column | Type | Constraint |
|---|---|---|
| `level` | int | Key. Must be exactly `1..N` contiguous in row order (`E060`). |
| `cum_copies` | int | Strictly increasing (`E031`). Row `level = 1` must have `cum_copies = 1` (`E061`). |

P0 data is `HERO_LVL_CUM` from `specs/00`, N = 10. `level(copies)` = number of rows with `cum_copies <= copies` (`specs/01` section 7). Loader exposes `IReadOnlyList<int> HeroLevelThresholds` with index 0 = level 1.

### 5.9 `banners`

One row per banner. P0 has one (`hero_standard`). Pity constants are per banner because `specs/01` section 11 leaves "shared vs per-banner pity" open; per-row columns support either answer.

| Column | Type | Constraint |
|---|---|---|
| `id` | id | Key. |
| `kind` | enum(BannerKind) | P0: `hero` only. |
| `name_key` | key | |
| `pool_id` | id | Must match at least one `heroes.pool_id` (`E022`); every `Tier` with a non-zero rate must have at least one hero in the pool (`E033`). |
| `rate_c`, `rate_r`, `rate_e`, `rate_l`, `rate_m` | double x5 | Each `[0, 1]`; sum to 1 within 1e-9 (`E030`). Folded into a JSON object `rates` keyed by `Tier` (section 6.1). |
| `pity_soft_start` | int | `>= 1`. |
| `pity_soft_step` | double | `> 0`. |
| `pity_hard` | int | `> pity_soft_start` (`E051`). |
| `mythic_share_of_lplus` | double | `[0, 1]`. |
| `guarantee_batch_size` | int | `>= 2`. A batch of this many pulls guarantees one result `>= guarantee_min_tier`. |
| `guarantee_min_tier` | enum(Tier) | Rate of that tier or higher must be `> 0` (`E052`). |
| `pull_cost_gems` | int | `>= 0`. |
| `free_pulls_per_day` | int | `>= 0`. |
| `rng_stream` | enum(RngStream) | Unique across enabled banners (`E053`): two banners sharing a stream would interleave draws and break the `3 * pulls` counter arithmetic in `specs/01` section 6. |
| `enabled` | bool | Disabled banners load but the client hides them and `IGachaService` refuses pulls on them. |

Derived in the loader, not stored: `BaseLplus = rates[L] + rates[M]`; `Pool[tier]` lists (section 5.7). Storing `base_lplus` as well would be a second copy of the same fact.

### 5.10 `class_advances`

| Column | Type | Constraint |
|---|---|---|
| `id` | id | Key. |
| `index` | int | Exactly `1..N` contiguous in row order (`E060`). |
| `stage` | int | `>= 1`; strictly increasing (`E032`). Advance happens when the player first reaches this stage. |
| `mult` | double | `> 0`. DPS multiplier granted by this advance. P0 rows all carry `CLASS_MULT`. |
| `name_key` | key | |
| `icon_id` | string | |

### 5.11 `daily_quests`

| Column | Type | Constraint |
|---|---|---|
| `id` | id | Key. |
| `type` | enum(QuestType) | |
| `target` | int | `>= 1`. Count the player must reach within the daily window. `login` uses 1. |
| `reward_gems` | int | `>= 0`. |
| `sort_order` | int | Unique (`E046`). Display order. |
| `name_key` | key | |

P0 rows, from `specs/01` section 5: `login` (1, 50 gems), `kill_mobs` (500, 30), `clear_boss` (1, 30), `do_pulls` (3, 30; free pulls count). Daily reset is at the server-time rollover (`specs/06`).

### 5.12 `tutorial_grants`

One-time grants keyed on a trigger. P0 has the two from `specs/01` section 5.

| Column | Type | Constraint |
|---|---|---|
| `id` | id | Key. Saves record which grant ids have fired (`specs/06`), so ids are permanent. |
| `trigger` | enum(GrantTrigger) | |
| `trigger_value` | int? | Required when `trigger = stage_reached` (the stage); must be empty otherwise (`E054`). |
| `grant_type` | enum(GrantType) | |
| `amount` | int | `>= 1`. |
| `grant_ref` | ref(heroes)? | Required when `grant_type = hero`; must be empty otherwise (`E054`). |
| `sort_order` | int | Unique. Order in which grants with the same trigger are applied. |

P0 rows: `tutorial_gems` (`profile_created`, `gems`, 1000) and `tutorial_pulls` (`profile_created`, `free_pulls`, 10).

### 5.13 `progress_targets`

The `specs/00` section 2.6 table. Read by the harness only (`specs/09`); included so that the acceptance targets are versioned with the content they judge.

| Column | Type | Constraint |
|---|---|---|
| `day` | int | Key. Strictly increasing (`E032`). |
| `stage` | int | `>= 1`; non-decreasing (`E048`). |

### 5.14 `strings` (optional in P0, required in **P1**)

| Column | Type | Constraint |
|---|---|---|
| `key` | key | Key. |
| `en` | string | English text. **P1** adds one column per locale. |

Emitted as `Content/strings.json` with shape `{"table": "strings", "locale": "en", "rows": [{"key": ..., "text": ...}]}`. P0 behaviour when the tab is absent or a key is missing is defined in section 11.

### 5.15 `meta`

Key/value tab the converter reads for build inputs. Not exposed as a table in `ContentDb`; its values land in `manifest.json`.

| Key | Type | Meaning |
|---|---|---|
| `content_version` | string | `MAJOR.MINOR`, section 6.3. Required. |
| `source_revision` | string | Free text the author bumps on export (sheet revision, date, initials). Required so two exports with different sheets never produce an identical manifest by accident. |

## 6. JSON layout and `content_version`

### 6.1 One file per table

`Content/` holds `manifest.json` plus one file per table. One file per table rather than one bundle because:

- Git diffs stay readable (a rate change touches one 20-line file, not a 2000-line blob).
- Addressables (**P1**) can ship a single changed table.
- Loader errors name a file, which is what a designer needs to find the offending tab.

The cost is one manifest to keep the set consistent, which the hash check in 6.2 covers.

Table file shape, identical for every table:

```json
{
  "table": "gear_slots",
  "rows": [
    { "id": "weapon", "sort_order": 0, "name_key": "gear_slot.weapon.name", "icon_id": "icon_slot_weapon" }
  ]
}
```

Formatting rules (so output is byte-identical for identical input): UTF-8 without BOM, 2-space indent, `\n` line endings, trailing newline, keys in schema column order, rows in CSV order, ints as integer literals, doubles via Python `repr`, `null` for empty nullable cells. Folded columns: a schema entry may declare `fold: rates <- rate_{tier}`, which turns `rate_c ... rate_m` into `"rates": {"C": ..., "R": ..., "E": ..., "L": ..., "M": ...}` in `Tier` order. `banners` is the only P0 table using a fold.

### 6.2 `manifest.json`

```json
{
  "schema_version": 1,
  "content_version": "1.0",
  "source_revision": "2026-10-30 sheet rev 41 kj",
  "tables": {
    "banners":                { "file": "banners.json",                "sha256": "..." },
    "boss_rules":             { "file": "boss_rules.json",             "sha256": "..." },
    "class_advances":         { "file": "class_advances.json",         "sha256": "..." },
    "constants":              { "file": "constants.json",              "sha256": "..." },
    "curves":                 { "file": "curves.json",                 "sha256": "..." },
    "daily_quests":           { "file": "daily_quests.json",           "sha256": "..." },
    "gear_rarity":            { "file": "gear_rarity.json",            "sha256": "..." },
    "gear_slots":             { "file": "gear_slots.json",             "sha256": "..." },
    "progress_targets":       { "file": "progress_targets.json",       "sha256": "..." },
    "hero_level_thresholds": { "file": "hero_level_thresholds.json", "sha256": "..." },
    "hero_tiers":            { "file": "hero_tiers.json",            "sha256": "..." },
    "heroes":                 { "file": "heroes.json",                 "sha256": "..." },
    "strings":                { "file": "strings.json",                "sha256": "..." },
    "tutorial_grants":        { "file": "tutorial_grants.json",        "sha256": "..." }
  },
  "content_hash": "sha256 over the table sha256 values concatenated in the order above"
}
```

- `schema_version` is the version of this document's table layout. The loader refuses any value it does not know (`E901`). It changes only when a converter and loader change together.
- No timestamp. A rebuild from the same CSVs must produce the same bytes; `source_revision` is the human-maintained provenance.
- Each table's `sha256` is over the file bytes. The loader verifies every file against the manifest (`E900`) and refuses to load a table that is not listed, so a stray hand edit or a half-copied directory fails fast.
- `content_hash` is what the save records alongside `content_version` (6.3) and what the harness prints at the top of its table.

### 6.3 `content_version` and save compatibility

Format `MAJOR.MINOR`, both non-negative integers, no leading zeros. Rules for bumping, enforced by the converter where it can see the previous build (`--previous <dir>`, default the existing `--out` directory):

| Change | Bump | Converter check |
|---|---|---|
| Any value change (a rate, a threshold value, a colour) | MINOR | `E091` if the content differs and the version string is unchanged. |
| New row, new enum value, new nullable column, new table | MINOR | same |
| Removed or renamed id in any keyed table | MAJOR | `E090` if an id present in the previous build is missing and MAJOR did not increase. |
| Changed number of rows in `hero_level_thresholds`, `gear_slots`, `class_advances` | MAJOR | `E090` |
| Changed pool ordering of existing heroes (section 5.7) | MAJOR | `E090` (detected as an index change in the derived pool list) |
| Removed enum value, removed table, removed required column | MAJOR | `E090` |

Coordination with `specs/06`: a save stores `content_version` and `content_hash` of the content it was last played on. On load, `specs/06` calls `db.IsCompatibleWith(save.ContentVersion)`, which is true when MAJOR matches. When it is false, `specs/06` owns what happens next; the P0 behaviour it should implement is `ContentReconcile.Run(save, db)`: drop owned-hero entries whose id is not in `db.Heroes` (shards are not refunded in P0), truncate gear to `db.GearSlots.Count`, clamp hero levels to the threshold count, and log each change. Content migrations as data (a `content_migrations` table mapping old ids to new) are **P1**. A save from a *newer* content MAJOR than the running build is refused with an "update the app" message; the client never downgrades content.

## 7. The converter: `tools/content_build.py`

### 7.1 Language choice: Python

Python, not C#, for three reasons that outweigh the one cost:

1. `tools/` is already Python 3.7+ with no dependencies, and section 12 has those models reading the converter's output. One language for the whole authoring side means one `SCHEMA` definition shared by the converter and the models.
2. CSV, JSON, SHA-256 and argparse are all stdlib. The converter stays a single file a designer can run with `python3` and no SDK, and CI runs it without a Unity licence or a dotnet install.
3. The thing that must stay in C# is the loader, and the loader needs its own validation anyway (section 1 principle 3). A C# converter would not remove that duplication; it would only move the sheet-side logic away from the models that need it.

The cost: the validation rules exist twice (Python converter, C# loader). Section 8 fixes the error codes and messages so both emit the same text, and loader test 17 plus the fixture directory in section 13 keep them in step.

### 7.2 Command line

```
python3 tools/content_build.py --src content_src --out Content [--previous Content] [--check] [--copy-to <dir>]
```

| Flag | Meaning |
|---|---|
| `--src` | Directory of `<table>.csv` files. Default `content_src`. |
| `--out` | Output directory. Created if missing; existing `*.json` are overwritten, nothing else is touched. Default `Content`. |
| `--previous` | A prior build to run the version-bump rules (6.3) against. Default: `--out` if it already holds a `manifest.json`. `--no-previous` skips the rules (first build only). |
| `--check` | Build to memory and exit 1 if the result differs from what is in `--out`. CI uses this to catch a stale committed `Content/`. Writes nothing. |
| `--copy-to` | After a successful build, mirror `--out` into another directory (the Unity project's content folder, section 10). |

Exit codes: 0 success, 1 validation errors (all of them listed, not just the first), 2 usage or I/O error. Errors go to stderr, one per line, in the format of section 8.1. On success stdout prints `content_version`, `content_hash`, the row count per table and the derived pool sizes per tier, so the console output doubles as a sanity check against `specs/00` section 2.4.

### 7.3 Structure

About 350 lines, one file:

- `SCHEMA`: dict `table -> TableSchema(columns, key, folds, required)`; columns carry `(type, nullable)`. This is the single machine-readable form of section 5 and the thing `tools/content.py` (section 12) imports.
- `ENUMS`: dict mirroring section 3.3.
- `REQUIRED_CONSTANTS`: dict `key -> type` mirroring section 5.1.
- `read_table(path, schema) -> list[dict]`: CSV parsing, per-cell type checks (`E010`, `E011`), unknown/missing columns (`E002`, `E003`), key uniqueness (`E020`), enum values (`E021`), `#` column skipping.
- `validate(tables) -> list[Error]`: every cross-row and cross-table rule in section 8, each a small function appended to a rule list so adding a rule is one function.
- `emit(tables, meta, out)`: deterministic JSON per 6.1, then the manifest.
- `version_rules(current, previous)`: 6.3.

### 7.4 CSV export (P0 manual, P1 scripted)

P0: File > Download > Comma Separated Values on each tab, saved as `content_src/<tab>.csv`. Fourteen tabs; it is tedious but it is also the step where a human looks at what they are about to commit.

**P1** `tools/content_pull.py <spreadsheet_id>`: fetches every tab through the sheet's CSV export URL (`.../gviz/tq?tqx=out:csv&sheet=<tab>`, works for link-shared sheets without OAuth), writes `content_src/`, then invokes `content_build.py`. Not P0 because it needs the sheet to be link-shared, which is a decision about who can read live tuning data (open decision 3).

## 8. Validation rules and error messages

### 8.1 Format

One line per error on stderr, no wrapping:

```
CONTENT <code> <location>: <message>
```

`<location>` is `<table>`, `<table>[<row>]` or `<table>[<row>].<column>`, where `<row>` is the row's key value when the row has one that parsed, otherwise `row <n>` with `n` the 1-based data row number (header excluded, blank rows counted so it matches the sheet). Placeholders in the messages below are in `{braces}`; doubles are printed with `repr` so the designer sees what the converter saw.

Both the converter and the C# loader use these exact codes and messages. The loader throws one `ContentLoadException` carrying the full list.

### 8.2 Structural

| Code | Condition | Message |
|---|---|---|
| `E001` | Required table file missing | `missing required table '{table}' (expected content_src/{table}.csv)` (loader: `expected {table}.json listed in manifest.json`) |
| `E002` | Required column absent | `missing required column '{column}'` |
| `E003` | Column not in schema and not `#`-prefixed | `unknown column '{column}' (remove it or add it to SCHEMA in tools/content_build.py)` |
| `E010` | Cell does not parse as its type | `expected {type}, got '{raw}'` |
| `E011` | Empty cell in non-nullable column | `required cell is empty` |
| `E020` | Duplicate key | `duplicate id '{value}' (first seen at row {first_row})` |
| `E021` | Enum value not allowed | `'{value}' is not a valid {enum} (allowed: {values comma-separated})` |
| `E022` | Reference does not resolve | `'{value}' does not exist in {target_table}.{target_column}` |
| `E070` | Bad colour | `'{value}' is not a #RRGGBB color` |
| `E071` | Bad id or key format | `'{value}' does not match {pattern}` |

### 8.3 Constants

| Code | Condition | Message |
|---|---|---|
| `E040` | Required key absent | `missing required constant '{key}'` |
| `E041` | `type` column differs from required | `declared type '{declared}' does not match required type '{required}'` |
| `E042` | Key not in required list | `unknown constant '{key}' (add it to REQUIRED_CONSTANTS and to ContentDb.Constants first)` |
| `E043` | Range violated | `value {value} out of range ({rule})` |

Range rules (`{rule}` text in parentheses): `TICK_HZ`, `ATTACK_INTERVAL_TICKS`, `KILLS_PER_STAGE`, `BOSS_EVERY`, `TIER_WIDTH`, `ENH_MAX`, `HERO_SLOTS`, `GEMS_PER_DAY_F2P` are `> 0`; `SPAWN_DELAY_TICKS` is `>= 0`; `BASE_ATK`, `STAT_PER_LVL`, `ENH_PER_LVL`, `HERO_LVL_BONUS`, `ENH_COST_GOLD_MULT`, `OFFLINE_CAP_H` are `> 0`; `GEAR_DROP_P`, `OFFLINE_RATE`, `ENH_SUCCESS_FLOOR`, `ENH_SUCCESS_STEP` are `in [0, 1]`; `GEAR_SLOT_G` is `> 1`. `curves.growth` uses `E043` with rule `> 1`, `curves.base` with `> 0`; every other `> 0` / `>= 0` / `[0, 1]` constraint in section 5 also reports as `E043` with the column's rule text.

### 8.4 Table rules

| Code | Table(s) | Condition | Message |
|---|---|---|---|
| `E030` | banners, gear_rarity | Rates/weights do not sum to 1 within 1e-9 | `rates sum to {sum} (abs error {abs_error}), expected 1 within 1e-9` |
| `E031` | hero_level_thresholds | `cum_copies` not strictly increasing | `cum_copies must be strictly increasing; level {level} has {value} <= previous {prev}` |
| `E032` | boss_rules, class_advances, progress_targets | Ordered column not strictly increasing | `{column} must be strictly increasing; row {row} has {value} <= previous {prev}` |
| `E033` | banners | A tier with rate `> 0` has no hero in the pool | `pool '{pool_id}' has no heroes of tier {tier} but rate_{tier lowercase} is {rate}` |
| `E044` | curves, gear_rarity, hero_tiers | An enum-keyed table is missing a value | `missing row for {enum} value '{value}'` |
| `E045` | boss_rules | First row's `from_stage` is not 1 | `first rule must have from_stage = 1, got {value}` |
| `E046` | gear_slots, daily_quests, tutorial_grants | Duplicate `sort_order` | `duplicate sort_order {value} (also at row {other_row})` |
| `E047` | gear_slots | `sort_order` not exactly `0..N-1` | `sort_order values must be exactly 0..{n_minus_1}, got {sorted values}` |
| `E048` | gear_rarity, progress_targets | Non-decreasing violated | `{column} must be non-decreasing in {order}; {row} has {value} < previous {prev}` |
| `E051` | banners | `pity_hard <= pity_soft_start` | `pity_hard ({hard}) must be greater than pity_soft_start ({soft})` |
| `E052` | banners | Guarantee tier unreachable | `guarantee_min_tier '{tier}' has zero combined rate for tiers >= {tier}` |
| `E053` | banners | Two enabled banners share `rng_stream` | `rng_stream '{stream}' is also used by enabled banner '{other_id}'` |
| `E054` | tutorial_grants | Conditional column wrong | `{column} must be {'set' or 'empty'} when {cond_column} = {cond_value}` |
| `E060` | hero_level_thresholds, class_advances | Index column not `1..N` in row order | `{column} must be exactly 1..{n} in row order, got {values}` |
| `E061` | hero_level_thresholds | Level 1 threshold not 1 | `cum_copies for level 1 must be 1 (one copy = level 1), got {value}` |

### 8.5 Versioning and manifest

| Code | Where | Condition | Message |
|---|---|---|---|
| `E090` | converter | MAJOR-worthy change without MAJOR bump | `{table}: '{id}' existed in previous build ({prev_version}) and is missing or reordered; this needs a MAJOR bump (current {version})` |
| `E091` | converter | Content changed, version unchanged | `content differs from previous build but content_version is still {version}; bump MINOR` |
| `E092` | converter | Bad version string | `content_version '{value}' must be MAJOR.MINOR (digits, no leading zeros)` |
| `E093` | converter | Version went backwards | `content_version {version} is lower than previous build {prev_version}` |
| `E900` | loader | File hash mismatch | `{file}: sha256 {actual} does not match manifest {expected}; rebuild Content/ with tools/content_build.py` |
| `E901` | loader | Unknown `schema_version` | `manifest schema_version {value} is not supported by this build (supports {supported})` |
| `E902` | loader | Table in manifest missing on disk, or file present but not in manifest | `{file} is listed in manifest.json but missing` / `{file} is present but not listed in manifest.json` |

Loader scope: the C# loader implements every code in 8.2, 8.3, 8.4 and 8.5-`E9xx`. It does not implement `E09x` (it has no previous build). `--check` in CI plus the hash check at load time are what make a hand edit of `Content/` visible.

## 9. `ContentDb` public API (C#)

Namespace `HeroCat.Sim.Content`, inside the pure `HeroCat.Sim` assembly (no `UnityEngine`). JSON parsing uses Newtonsoft.Json, available to Unity as `com.unity.nuget.newtonsoft-json` and to the harness as the NuGet package of the same name; it is the assembly's only external dependency (open decision 2). All collections are exposed through read-only interfaces backed by arrays or `ReadOnlyDictionary`; every `*Def` is a `sealed class` with get-only properties set in its constructor. Nothing has a public setter.

### 9.1 Loading

```csharp
public interface IContentSource
{
    bool Exists(string fileName);          // "manifest.json", "heroes.json"
    string ReadText(string fileName);      // UTF-8 contents
}

public sealed class FileContentSource : IContentSource   // harness, tests, editor
{
    public FileContentSource(string directory) { ... }
}

public sealed class ContentLoadException : Exception
{
    public IReadOnlyList<ContentError> Errors { get; }   // every error, section 8 format
}

public readonly struct ContentError
{
    public string Code { get; }        // "E030"
    public string Location { get; }    // "banners[hero_standard]"
    public string Message { get; }
    public override string ToString() => $"CONTENT {Code} {Location}: {Message}";
}

public sealed partial class ContentDb
{
    public static ContentDb Load(IContentSource source);   // throws ContentLoadException
    public string Version { get; }        // manifest.content_version, "1.0"
    public string ContentHash { get; }    // manifest.content_hash
    public string SourceRevision { get; }
    public bool IsCompatibleWith(string saveContentVersion);  // same MAJOR; false on malformed input
}
```

`Load` reads the manifest, verifies every hash (`E900`/`E902`), parses each table, runs the section 8 checks, builds derived data (pools, slot order, threshold list), and only then returns. A `ContentDb` that exists is valid; code never has to check.

### 9.2 Constants and curves

```csharp
public sealed class Constants
{
    public int    TickHz { get; }                // TICK_HZ
    public double BaseAtk { get; }               // BASE_ATK
    public int    AttackIntervalTicks { get; }   // ATTACK_INTERVAL_TICKS
    public int    KillsPerStage { get; }         // KILLS_PER_STAGE
    public int    SpawnDelayTicks { get; }       // SPAWN_DELAY_TICKS
    public int    BossEvery { get; }             // BOSS_EVERY
    public double StatPerLvl { get; }            // STAT_PER_LVL
    public int    GemsPerDayF2p { get; }         // GEMS_PER_DAY_F2P
    public double OfflineRate { get; }           // OFFLINE_RATE
    public double OfflineCapH { get; }           // OFFLINE_CAP_H
    public double GearDropP { get; }             // GEAR_DROP_P
    public int    TierWidth { get; }             // TIER_WIDTH
    public double GearSlotG { get; }             // GEAR_SLOT_G
    public int    EnhMax { get; }                // ENH_MAX
    public double EnhPerLvl { get; }             // ENH_PER_LVL
    public double EnhSuccessFloor { get; }       // ENH_SUCCESS_FLOOR
    public double EnhSuccessStep { get; }        // ENH_SUCCESS_STEP
    public double EnhCostGoldMult { get; }       // ENH_COST_GOLD_MULT
    public int    HeroSlots { get; }            // HERO_SLOTS
    public double HeroLvlBonus { get; }         // HERO_LVL_BONUS

    // Convenience, not stored:
    public int  TierOf(int stage) => (stage / TierWidth) * TierWidth;
    public bool IsBossStage(int stage) => stage % BossEvery == 0;
    public double EnhSuccess(int level) => Math.Max(EnhSuccessFloor, 1.0 - EnhSuccessStep * level);
}

public enum CurveId { EnemyHp, GoldPerKill, StatCost }

public sealed class CurveDef
{
    public CurveId Id { get; }
    public double Base { get; }
    public double Growth { get; }
    // growth^x by repeated multiplication, cached and grown on demand. No Math.Pow:
    // specs/03 section 4 requires bit-exact results across x64/ARM64/IL2CPP.
    private readonly List<double> _pow = new() { 1.0 };
    public double Eval(int x)
    {
        while (_pow.Count <= x) _pow.Add(_pow[^1] * Growth);
        return Base * _pow[x];
    }
}

public sealed partial class ContentDb
{
    public Constants Constants { get; }
    public IReadOnlyDictionary<CurveId, CurveDef> Curves { get; }
    public double EnemyHp(int stage)    => Curves[CurveId.EnemyHp].Eval(stage);
    public double GoldPerKill(int stage)=> Curves[CurveId.GoldPerKill].Eval(stage);
    public double StatCost(int level)   => Curves[CurveId.StatCost].Eval(level);
}
```

Property names are the PascalCase form of the `specs/00` names; the comment on each line is the content key so a grep for either finds the other.

### 9.3 Row types

```csharp
public enum Tier { C = 0, R = 1, E = 2, L = 3, M = 4 }
public enum BannerKind { Hero, Pet }
public enum RngStream { Combat, GearDrop, GearRarity, Enhance, GachaHero, GachaPet }
public enum QuestType { Login, KillMobs, ClearBoss, DoPulls }
public enum GrantTrigger { ProfileCreated, StageReached, FirstBossCleared }
public enum GrantType { Gems, FreePulls, Gold, Hero }

public sealed class BossRuleDef
{
    public string Id { get; }
    public int FromStage { get; }
    public double HpMult { get; }
    public int TimeTicks { get; }
    public double GoldMult { get; }
    public double GearDropP { get; }
    public Tier GearMinRarity { get; }
    public int FirstClearGems { get; }
}

public sealed class GearSlotDef   { public string Id; public int SortOrder; public string NameKey; public string IconId; }
public sealed class GearRarityDef { public Tier Rarity; public double Weight; public double Mult; public string DisplayColor; public string NameKey; }
public sealed class HeroTierDef  { public Tier Tier; public string DisplayColor; public string NameKey; public int ShardValue; }

public sealed class HeroDef
{
    public string Id { get; }
    public Tier Tier { get; }
    public string PoolId { get; }
    public string NameKey { get; }
    public string DescKey { get; }
    public string IconId { get; }
    public double BasePct { get; }
}

public sealed class BannerDef
{
    public string Id { get; }
    public BannerKind Kind { get; }
    public string NameKey { get; }
    public string PoolId { get; }
    public IReadOnlyDictionary<Tier, double> Rates { get; }
    public double BaseLplus { get; }                       // Rates[L] + Rates[M], derived at load
    public int PitySoftStart { get; }
    public double PitySoftStep { get; }
    public int PityHard { get; }
    public double MythicShareOfLplus { get; }
    public int GuaranteeBatchSize { get; }
    public Tier GuaranteeMinTier { get; }
    public int PullCostGems { get; }
    public int FreePullsPerDay { get; }
    public RngStream RngStream { get; }
    public bool Enabled { get; }
    public IReadOnlyDictionary<Tier, IReadOnlyList<string>> Pool { get; }   // hero ids, ordinal-sorted, derived at load

    public double PityRate(int n) =>
        n >= PityHard ? 1.0 : Math.Min(1.0, BaseLplus + PitySoftStep * Math.Max(0, n - PitySoftStart));
}

public sealed class ClassAdvanceDef  { public string Id; public int Index; public int Stage; public double Mult; public string NameKey; public string IconId; }
public sealed class DailyQuestDef    { public string Id; public QuestType Type; public int Target; public int RewardGems; public int SortOrder; public string NameKey; }
public sealed class TutorialGrantDef { public string Id; public GrantTrigger Trigger; public int? TriggerValue; public GrantType GrantType; public int Amount; public string GrantRef; public int SortOrder; }
public sealed class ProgressTargetDef{ public int Day; public int Stage; }
```

(The one-line classes above are abbreviated; in code they follow the same get-only constructor pattern as `HeroDef`.)

### 9.4 Table accessors

```csharp
public sealed partial class ContentDb
{
    public IReadOnlyList<BossRuleDef> BossRules { get; }               // by FromStage ascending
    public BossRuleDef BossRuleFor(int stage);                          // last rule with FromStage <= stage

    public IReadOnlyList<GearSlotDef> GearSlots { get; }               // index == SortOrder
    public IReadOnlyDictionary<Tier, GearRarityDef> GearRarity { get; }
    public IReadOnlyDictionary<Tier, HeroTierDef> HeroTiers { get; }

    public IReadOnlyDictionary<string, HeroDef> Heroes { get; }
    public IReadOnlyList<int> HeroLevelThresholds { get; }            // [0] is level 1
    public int HeroLevel(int copies);                                  // count of thresholds <= copies, 0 if copies == 0
    public double HeroPct(HeroDef hero, int level) =>
        hero.BasePct * (1.0 + Constants.HeroLvlBonus * (level - 1));

    public IReadOnlyDictionary<string, BannerDef> Banners { get; }
    public IReadOnlyList<ClassAdvanceDef> ClassAdvances { get; }       // by Index
    public IReadOnlyList<DailyQuestDef> DailyQuests { get; }           // by SortOrder
    public IReadOnlyList<TutorialGrantDef> TutorialGrants { get; }     // by SortOrder
    public IReadOnlyList<ProgressTargetDef> ProgressTargets { get; }   // by Day

    public IReadOnlyDictionary<string, string> Strings { get; }        // empty when strings.json absent
    public string Text(string key) => Strings.TryGetValue(key, out var s) ? s : key;   // section 11
}
```

Sim code takes `ContentDb` through its constructor (`new Simulation(db, save, clock, rngs)`) and never through a static. The harness constructs one `ContentDb` and shares it across all bot instances; it is immutable, so that is safe.

## 10. Loading in Unity and the harness; hot reload

### 10.1 Where the files live

`Content/` at the repository root is the build output. The Unity project reads a copy under `Assets/Resources/Content/` (so each table is a `TextAsset` and `Resources.Load<TextAsset>("Content/heroes")` works on every platform without the StreamingAssets-on-Android detour). The copy is git-ignored and is never edited by hand. An editor pre-build step (`ContentPreBuild`, an `IPreprocessBuildWithReport` in the `HeroCat.Client.Editor` assembly) makes it before every player build: it loads `Content/` with hash checks, mirrors `*.json` into `Assets/Resources/Content`, loads the copy the same way, and fails the build if `manifest.json` is missing or any `sha256` does not match. `content_build.py --copy-to <dir>` still works for a manual copy. In the editor the client reads `Content/` directly, so no copy is needed for Play. Unity imports `.json` as `TextAsset` natively; no importer work.

```csharp
public sealed class ResourcesContentSource : IContentSource      // Unity assembly, not Sim
{
    public bool Exists(string f)   => Resources.Load<TextAsset>("Content/" + Path.GetFileNameWithoutExtension(f)) != null;
    public string ReadText(string f) => Resources.Load<TextAsset>("Content/" + Path.GetFileNameWithoutExtension(f)).text;
}
```

The harness and tests use `FileContentSource(repoRoot + "/Content")`.

### 10.2 Hot reload

**P0: reload on play.** `ContentBootstrap` (a `MonoBehaviour` on the first scene) calls `ContentDb.Load(new ResourcesContentSource())` in `Awake` and hands the result to the composition root. Entering play mode in the editor is the reload; a failed load shows the full error list in a modal and does not start the sim. An editor menu item `HeroCat > Content > Rebuild JSON` runs `python3 tools/content_build.py --copy-to Assets/Resources/Content` via `System.Diagnostics.Process`, streams its stderr to the console, and calls `AssetDatabase.Refresh()`. There is no in-play swap in P0: `Simulation` caches derived values from `ContentDb` and a mid-tick swap would need every cache invalidated; the editor loop "stop, rebuild, play" is seconds and good enough for tuning.

**P1: Addressables remote update.** Replace `ResourcesContentSource` with an Addressables-backed source; the content group is labelled with `content_version`; on startup the client fetches the catalog, downloads changed table files only (6.1), and loads them. `ContentDb.Load` and everything downstream do not change. The save compatibility rule (6.3) runs after the download, so a MAJOR bump the client cannot handle is caught before the sim starts. **P2:** live swap while the sim runs, which requires `Simulation.ReplaceContent(ContentDb)` and cache invalidation.

## 11. Localisation hook

Player-visible text is never an inline string in a content row. Every table that has a display name carries `name_key` (and `desc_key` where there is a description), type `key` (section 3.2). The client resolves keys through `ContentDb.Text(key)`.

- **P0:** the `strings` tab is optional. If present, `Content/strings.json` holds English only and `Text` returns the English string. If absent, or a key is missing, `Text` returns the key itself (`hero.c_01.name`), which is ugly, visible and greppable, which is the point for an ugly-UI prototype. The converter emits warning `W001 {table}[{row}].{column}: key '{key}' has no entry in strings` when the tab exists and a key is missing; warnings do not fail the build in P0.
- **P1:** `strings` is required, one column per locale (`en`, `ja`, ...), emitted as `strings.<locale>.json`; `W001` becomes `E072` for the default locale; `ContentDb.Text` takes the active locale from a `ILocale` seam; placeholders in strings use `{0}`-style indices and the content schema records nothing about them.
- **P2:** per-locale icon or layout overrides.

## 12. Making the Python models read the same JSON

Today `tools/gacha_model.py` and `tools/power_curve_model.py` each hold a block of module-level constants that duplicates `specs/00` section 2. After this change they read `Content/` so the sheet is the single source of truth and the models become validators of the shipped data. Describe only; do not implement in this spec.

1. Add `tools/content.py` (stdlib only): `load(content_dir="Content") -> Content`, a small object with attributes `constants` (dict key -> value), `curves` (dict id -> `(base, growth)`), `banners`, `heroes`, `hero_level_thresholds`, `gear_rarity`, `class_advances`, `boss_rules`, all as lists of dicts straight from the JSON `rows`. It imports `SCHEMA` from `content_build.py` for key names and verifies `manifest.json` hashes before returning, so a stale directory fails the same way the C# loader does (`E900` text).
2. `gacha_model.py`: delete `BASE`, `L_PLUS`, `M_SHARE_OF_LPLUS`, `SOFT_START`, `SOFT_STEP`, `HARD`. In `main()`, `c = content.load(args.content)`, `b = next(r for r in c.banners if r["id"] == args.banner)` (default `hero_standard`), then `BASE = b["rates"]`, `L_PLUS = BASE["L"] + BASE["M"]`, `M_SHARE_OF_LPLUS = b["mythic_share_of_lplus"]`, `SOFT_START = b["pity_soft_start"]`, `SOFT_STEP = b["pity_soft_step"]`, `HARD = b["pity_hard"]`, and `GUARANTEE_N = b["guarantee_batch_size"]` replaces the literal 9 in `(1 - e_plus) ** 9` as `** (GUARANTEE_N - 1)`. `p_lplus` takes those as parameters instead of globals.
3. `power_curve_model.py`: delete the constant block. Build it from content: `HP0, HP_G = curves["enemy_hp"]`; `GOLD0, GOLD_G = curves["gold_per_kill"]`; `STAT_COST0, STAT_COST_G = curves["stat_cost"]`; `BOSS_TIME_S = boss_rules[0]["time_ticks"] / constants["TICK_HZ"]`; `SPAWN_S = constants["SPAWN_DELAY_TICKS"] / constants["TICK_HZ"]`; `MIN_KILL_S = constants["ATTACK_INTERVAL_TICKS"] / constants["TICK_HZ"]`; `GEAR_RARITY = [(r["rarity"], r["weight"], r["mult"]) for r in gear_rarity]`; `HERO_BASE_PCT` = per tier, taken from the first hero of each tier in the pool (and assert all heroes of a tier agree, since the model has no per-hero notion); `HERO_POOL` = count of heroes per tier; `HERO_LVL_CUM = [r["cum_copies"] for r in hero_level_thresholds]`; `CLASS_STAGES = [r["stage"] for r in class_advances]`, `CLASS_MULT = class_advances[0]["mult"]` (assert all equal); `PULL_COST`, `FREE_PULLS_PER_DAY`, pity values from the banner row; `GEMS_PER_DAY`, `OFFLINE_RATE`, `OFFLINE_CAP_H`, `GEAR_SLOTS` (= `len(gear_slots)`), `GEAR_DROP_P`, `GEAR_SLOT_G`, `ENH_MAX`, `ENH_PER_LVL`, `STAT_PER_LVL`, `BASE_ATK`, `KILLS_PER_STAGE`, `BOSS_EVERY`, `BOSS_HP_MULT` from `constants` and `boss_rules[0]`. The two literals in `spend()` (`20 * gold(...)` and `max(0.3, 1 - 0.05 * ...)`) become `constants["ENH_COST_GOLD_MULT"]`, `constants["ENH_SUCCESS_FLOOR"]`, `constants["ENH_SUCCESS_STEP"]`. The `random.random() * 0.97` renormalisation becomes `1 - L_PLUS` from the banner.
4. Both scripts gain `--content <dir>` (default `Content`) and keep their output format byte-identical for the current data, which is the acceptance test for the change: run before and after, diff the output, expect nothing.
5. Update the docstrings and `specs/00` section 2's sentence "Change them there first" to "Change the sheet first, rebuild `Content/`, rerun the tools, then update this table" (open decision 1 covers the `specs/00` edit).

## 13. Loader unit tests

`HeroCat.Sim.Tests/ContentDbTests.cs`. Fixtures live in `HeroCat.Sim.Tests/Fixtures/content/`: `good/` is a copy of the shipped `Content/` built by CI (never hand-edited), and each `bad_<code>_<name>/` directory is a full content set with one defect plus an `expected.txt` holding the exact `CONTENT Exxx ...` line. The Python side has `tools/tests/test_content_build.py` running the same `bad_*` directories through the converter and asserting the same `expected.txt`, which is how the two validators are kept equal (test 17).

1. **Shipped content loads.** `Load(FileContentSource("Content"))` succeeds; `Version` equals `manifest.content_version`; `ContentHash` equals `manifest.content_hash`.
2. **Constants complete.** Every public property of `Constants` is non-default after load, and the set of property names (snake-cased) equals the set of `key` values in `constants.json`. Catches a key added on one side only.
3. **Missing table** (`bad_E001`): throws `ContentLoadException` whose single error matches `expected.txt`.
4. **Hash mismatch** (`bad_E900`): one byte changed in `heroes.json`; `E900` with the file name.
5. **Unlisted file** (`bad_E902`): extra `pets.json` present; `E902`.
6. **Duplicate id** (`bad_E020`): two `hero_c_01` rows; `E020` names the first row.
7. **Rates sum** (`bad_E030`): `rate_c` 0.599; `E030` with `abs error 0.001`.
8. **Thresholds** (`bad_E031`): `cum_copies` 16, 25, 25; `E031` at level 7.
9. **Pool reference** (`bad_E033`): banner `pool_id` with no Mythic heroes; `E033` for tier `M`.
10. **Enum** (`bad_E021`): hero tier `X`; `E021` lists `C, R, E, L, M`.
11. **Constant missing and mistyped** (`bad_E040`, `bad_E041`): `E040` for a removed `TIER_WIDTH`; `E041` when `TICK_HZ` is declared `double`.
12. **All errors reported.** A fixture with three independent defects yields exactly three errors in table order, not one.
13. **Pool ordering.** `Banners["hero_standard"].Pool[Tier.C]` equals the C-tier ids sorted with `StringComparer.Ordinal`; appending `hero_c_21` to a fixture leaves indices 0..19 unchanged.
14. **Immutable.** Casting any `IReadOnlyList`/`IReadOnlyDictionary` to its mutable interface and calling `Add` throws `NotSupportedException`; reflection finds no public setter on any type in `HeroCat.Sim.Content`.
15. **IsCompatibleWith.** `("1.0" db, "1.7" save)` true; `("2.0", "1.7")` false; `("1.0", "")`, `("1.0", "1")`, `("1.0", "a.b")` false.
16. **Pity rate from content.** `Banners["hero_standard"].PityRate(n)` for n in {1, 50, 51, 55, 60, 65, 70, 75, 79, 80, 81} equals the `specs/01` section 3.2 table. This is the bridge to `GachaTests`; it fails if the sheet drifts from `specs/01`.
17. **Converter parity.** For every `bad_*` fixture, the loader's first error line equals `expected.txt`. (The Python test asserts the same file, so both validators are pinned to one text.)
18. **BossRuleFor.** Shipped content: `BossRuleFor(10)` and `BossRuleFor(550)` return `default`. Two-rule fixture (`from_stage` 1 and 300): `BossRuleFor(290)` is the first, `BossRuleFor(300)` the second.
19. **Golden content.** Shipped content matches `specs/00`: heroes per tier 20/15/10/6/3; `ClassAdvances` stages 50/150/300/500; `HeroLevelThresholds` equals `HERO_LVL_CUM`; `GearSlots.Count` is 6; `GearRarity[Tier.M].Mult` is 3.0. Marked `[Category("Golden")]`; when `specs/00` changes this test is updated in the same commit as the sheet.
20. **Culture invariance.** Run `Load` with `CultureInfo.CurrentCulture = de-DE`; `Constants.HpG`-equivalent (`Curves[EnemyHp].Growth`) is still 1.07, not 107.
21. **Text fallback.** With `strings.json` absent, `Text("hero.c_01.name")` returns the key; with the fixture present it returns the English string.
22. **Load time.** Shipped content loads in under 50 ms on the CI runner (warm). The harness constructs one `ContentDb` per run, but the test suite constructs hundreds.
23. **Determinism of the build.** Running `content_build.py --check` against the committed `Content/` exits 0 (this is a CI step rather than an NUnit test, listed here because it is the test that proves the committed JSON is what the CSVs say).

## 14. Example: `Content/banners.json`

Real P0 values from `specs/00` section 2.4 and `specs/01` sections 2 to 5. `mythic_share_of_lplus` is `1/6` as the shortest round-trip double; Python `repr(1/6)` and C# `(1.0/6).ToString("R")` both produce this text and parse back to the same bits.

```json
{
  "table": "banners",
  "rows": [
    {
      "id": "hero_standard",
      "kind": "hero",
      "name_key": "banner.hero_standard.name",
      "pool_id": "hero_standard",
      "rates": {
        "C": 0.6,
        "R": 0.28,
        "E": 0.09,
        "L": 0.025,
        "M": 0.005
      },
      "pity_soft_start": 50,
      "pity_soft_step": 0.0325,
      "pity_hard": 80,
      "mythic_share_of_lplus": 0.16666666666666666,
      "guarantee_batch_size": 10,
      "guarantee_min_tier": "E",
      "pull_cost_gems": 100,
      "free_pulls_per_day": 3,
      "rng_stream": "gacha_hero",
      "enabled": true
    }
  ]
}
```

Loader-derived, not in the file: `BaseLplus = 0.03`; `Pool["C"]` = the 20 ids `hero_c_01` ... `hero_c_20` in ordinal order, and so on per tier.

## 15. Example: `Content/heroes.json` (first 5 of 54 rows)

The CSV is ordered C, R, E, L, M, so the first five rows are Common. Rows 6 to 54 have the same shape with `tier` R/E/L/M, `base_pct` 20.0/40.0/80.0/160.0 and 15/10/6/3 rows respectively, matching the pool sizes in `specs/00` section 2.4.

```json
{
  "table": "heroes",
  "rows": [
    {
      "id": "hero_c_01",
      "tier": "C",
      "pool_id": "hero_standard",
      "name_key": "hero.c_01.name",
      "desc_key": "hero.c_01.desc",
      "icon_id": "icon_hero_placeholder_c",
      "base_pct": 10.0
    },
    {
      "id": "hero_c_02",
      "tier": "C",
      "pool_id": "hero_standard",
      "name_key": "hero.c_02.name",
      "desc_key": "hero.c_02.desc",
      "icon_id": "icon_hero_placeholder_c",
      "base_pct": 10.0
    },
    {
      "id": "hero_c_03",
      "tier": "C",
      "pool_id": "hero_standard",
      "name_key": "hero.c_03.name",
      "desc_key": "hero.c_03.desc",
      "icon_id": "icon_hero_placeholder_c",
      "base_pct": 10.0
    },
    {
      "id": "hero_c_04",
      "tier": "C",
      "pool_id": "hero_standard",
      "name_key": "hero.c_04.name",
      "desc_key": "hero.c_04.desc",
      "icon_id": "icon_hero_placeholder_c",
      "base_pct": 10.0
    },
    {
      "id": "hero_c_05",
      "tier": "C",
      "pool_id": "hero_standard",
      "name_key": "hero.c_05.name",
      "desc_key": "hero.c_05.desc",
      "icon_id": "icon_hero_placeholder_c",
      "base_pct": 10.0
    }
  ]
}
```

## 16. P1 and P2 summary

| Item | Tag | Where in this spec |
|---|---|---|
| Scripted sheet export `content_pull.py` | P1 | 7.4 |
| `pet` banner kind, `gacha_pet` stream | P1 | 3.3, 5.9 (schema already admits them) |
| `hero_tiers.shard_value` consumer (shard shop) | P1 | 5.6 |
| Required `strings` table, multiple locales, `E072` | P1 | 5.14, 11 |
| `content_migrations` table (id remaps across MAJOR) | P1 | 6.3 |
| Addressables remote content, per-table download | P1 | 10.2 |
| Multiple `boss_rules` rows (boss types) | P1 | 5.3 (schema already keyed by `from_stage`) |
| Idle chest constants | P1 | open decision 4 |
| `stage_overrides` table | P2 | 5.2 |
| Live content swap during a running sim | P2 | 10.2 |
| Per-locale icon overrides | P2 | 11 |

## 17. Open decisions

1. **`specs/00` adopts the three names introduced here.** `TIER_WIDTH`, `ENH_SUCCESS_FLOOR` / `ENH_SUCCESS_STEP`, `ENH_COST_GOLD_MULT` exist only in this spec. Either `specs/00` section 2 adds them as rows (recommended; one-line edits) or this spec renames to whatever `specs/00` chooses. Also the sentence in `specs/00` section 2 about changing the Python tools first should flip to "change the sheet first" once section 12 is done.
2. **JSON library in `HeroCat.Sim`.** Newtonsoft.Json is assumed. If the Unity version in `specs/07` ships a usable `System.Text.Json`, switching is a loader-only change; the alternative of a hand-written reader (about 200 lines) removes the dependency entirely at the cost of maintaining a parser. Decide when `specs/07` pins the Unity version.
3. **Sheet access for scripted export (P1).** The CSV export URL needs the sheet to be readable by link. If live tuning data must stay private, `content_pull.py` needs a service account and the `google-api-python-client` dependency, which breaks the "stdlib only" line in `tools/`.
4. **Idle chest.** `specs/01` section 5 counts 100 gems/day from idle chests toward `GEMS_PER_DAY_F2P`, but `specs/00` lists no P0 feature for it and `specs/02` section 7 calls it P1. If it is P0 after all, it needs `IDLE_CHEST_INTERVAL_H` and `IDLE_CHEST_GEMS` in `constants`, and the P0 harness daily gem total should be checked against the `daily_quests` sum plus first-clear estimate.
5. **Pity scope.** `specs/01` section 11 leaves shared-vs-per-banner pity open. The schema stores pity per banner; if pity becomes shared across banners of a kind, add `pity_group` (id) to `banners` and key the save's pity counter by it. No P0 impact.
6. **Should `E042` (unknown constant) be an error or a warning?** As an error it forces the C# and Python sides to be updated before a designer can add a knob, which is deliberate but may annoy during fast tuning. A `--allow-extra-constants` flag for local builds, with CI strict, is the likely compromise.
7. **Golden test ownership (test 19).** It pins the shipped sheet to `specs/00`. When the design changes, someone must update the sheet, `specs/00` and the test together. Decide whether that test lives with the content (owned by design) or with the sim (owned by engineering) so that the failing test lands on the right desk.
