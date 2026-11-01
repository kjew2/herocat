# 06 - Save, Migrations, Offline Catch-up and the Clock Seam

Scope: the on-disk save (schema v1, format, atomic write, autosave, load, validation, migrations), the offline catch-up that 02 section 7 defines, the in-app idle path, the `IClock` seam, what the save does and does not protect against, and the debug panel. Constants are canonical in `specs/00` section 2; state records that another spec already defines are reused by name and not redefined here.

Assemblies: the catch-up arithmetic (`SyncWallClock(nowMs)`, section 7) and the `GameState` tree live in `HeroCat.Sim` (pure C#, 00 section 5.1, takes `now` as a parameter and never reads a clock). Everything else in this spec (serializer, migrations, file store, `LocalClock`, `FakeClock`, `SessionRunner`) lives in `HeroCat.Runtime`: plain C# with no UnityEngine reference, referencing `HeroCat.Sim` and Newtonsoft.Json, shared by the Unity client (`specs/07`) and the bot harness (`specs/09`). See 00 section 7, decision 4. The Unity hooks are in section 10.3.

## 1. Design goals

1. A save survives a kill, a crash or a power loss at any instant: the player loses at most `AUTOSAVE_INTERVAL_SEC` of progress and never a whole profile.
2. Reloading a save changes no future RNG result (01 section 6, 04 section 12 items 3 and 4).
3. A save written by any shipped build loads in every later build, forever, through forward-only migrations.
4. Offline time is credited once, in closed form, against server time, capped, and never pushes stages (02 section 7).
5. The economy reads time from `IClock` only. The device clock is never trusted directly.
6. The debug panel exists in development builds and does not exist, as code, in release builds.

## 2. Constants

Constants used from 00 section 2 are referred to by name: `TICK_HZ`, `ATTACK_INTERVAL_TICKS`, `SPAWN_DELAY_TICKS`, `KILLS_PER_STAGE`, `BOSS_EVERY`, `GOLD0`, `GOLD_G`, `HP0`, `HP_G`, `OFFLINE_RATE`, `OFFLINE_CAP_H`, `GEAR_SLOTS`, `ENH_MAX`, `HERO_SLOTS`, `PITY_HARD`, `CLASS_STAGES`, `FREE_PULLS_PER_DAY`. `DAY_SECONDS` and `IDLE_CHEST_HOURS` come from 04 section 2.

Engineering constants introduced by this spec. They are not balance numbers, so they live in code (`SaveConfig`) and not in `ContentDb`; migrations in particular must not depend on content (section 8.2).

| Name | Value | Meaning |
|---|---|---|
| `SAVE_SCHEMA_VERSION` | 1 | Current JSON schema version. Bumps only after the v1 freeze (section 8.3). |
| `AUTOSAVE_INTERVAL_SEC` | 30 | Periodic autosave while the app is in the foreground. |
| `SAVE_KEEP_COUNT` | 2 | Good saves kept on disk: `slot0.sav` and `slot0.prev.sav`. |
| `SAVE_FAIL_WARN_COUNT` | 3 | Consecutive write failures before the client shows a warning. |
| `SAVE_REPAIR_MAX` | 20 | Repairs on one load above which the file is treated as corrupt. |
| `CORRUPT_KEEP_DAYS` | 7 | Quarantined corrupt files older than this are deleted at startup. |
| `OFFLINE_REPORT_MIN_SEC` | 60 | Below this elapsed time the welcome-back card is not shown; gold is still credited. |
| `IN_APP_GAP_SEC` | 30 | A frame gap longer than this is treated as an offline interval, not ticked. |
| `MAX_TICKS_PER_FRAME` | 100 | Upper bound on ticks run in one frame (10 s of sim at `TICK_HZ`). |

## 3. Save schema v1

### 3.1 Record tree

The root is the sim's state object. `Simulation` (`specs/03`) owns one `GameState` and mutates it in place; there is no separate DTO tree. Migrations (section 8) run on the JSON before it is deserialised, so the C# type is always the current schema and only the JSON has versions.

```csharp
namespace HeroCat.Sim.State;

public sealed record GameState
{
    public int    SchemaVersion  { get; set; } = SaveSchema.Current;   // SAVE_SCHEMA_VERSION
    public string ContentVersion { get; set; } = "";                   // 05 content_version active at last write
    public ProfileHeader     Profile     { get; init; } = new();
    public CombatState       Combat      { get; init; } = new();
    public Currencies        Currencies  { get; init; } = new();        // 04 section 3
    public ProgressionState  Progression { get; init; } = new();        // 04 section 10
    public GearState         Gear        { get; init; } = new();        // 04 section 5.1
    public HeroState        Heroes      { get; init; } = new();        // 04 section 10
    public Dictionary<string, BannerState>    Banners { get; init; } = new();   // key: BannerDef.Id (P0: one banner)
    public Dictionary<string, RngStreamState> Rng     { get; init; } = new();   // key: stream name, 00 section 5.2
    public DailyState        Daily       { get; init; } = new();        // 04 section 7.1
    public LifetimeStats     Stats       { get; init; } = new();
    public PendingPull?      PendingPull { get; set; }                  // 08 section 4; always null in local mode
    public PendingReceipt?   PendingReceipt { get; set; }               // 08 section 6; null until IAP (P1)
    public CertState?        Cert        { get; set; }                  // 10 section 4; null until the profile is certified
}

public sealed record ProfileHeader
{
    public Guid ProfileId             { get; init; }   // assigned once at creation; RNG seeds derive from it (3.2)
    public Guid DeviceInstallId       { get; set; }    // per install; cloud-save lineage (08 section 5)
    public long CreatedAtServerSec    { get; init; }   // IClock at creation
    public long LastSavedAtServerSec  { get; set; }    // IClock at last successful write; catch-up anchor (section 9)
    public long SaveSequence          { get; set; }    // +1 per write; cloud-save tiebreak (08 section 5)
    public bool DebugTainted          { get; set; }    // any debug command sets it (section 13)
    public string? AccountId          { get; set; }    // 10 section 10; null in P0
    public bool RankedLineage         { get; set; }    // 10 section 3; true only if created through a server genesis
}

public sealed record CombatState
{
    public int Stage        { get; set; } = 1;   // >= 1
    public int KillsInStage { get; set; }        // 0 .. KILLS_PER_STAGE - 1
}

// 01 section 8's BannerState is a runtime view over this record plus Heroes.Copies and
// Currencies.Shards (04 section 3 note). Only these two fields are persisted per banner.
public sealed record BannerState
{
    public int  PityCounter { get; set; }   // pulls since last L+, 0 .. PITY_HARD
    public long TotalPulls  { get; set; }   // lifetime; invariant Rng["gacha_hero"].Counter == 3 * TotalPulls
}

public sealed record RngStreamState
{
    public string Seed    { get; init; } = "";   // exactly 16 lowercase hex chars (a ulong)
    public long   Counter { get; set; }          // draws consumed so far
}

public sealed record LifetimeStats
{
    public long Kills            { get; set; }   // invariant Rng["gear_drop"].Counter == Kills
    public long BossKills        { get; set; }
    public long GearDrops        { get; set; }   // invariant Rng["gear_rarity"].Counter == 2 * GearDrops
    public long EnhanceAttempts  { get; set; }   // invariant Rng["enhance"].Counter == EnhanceAttempts
    public long ActiveSeconds    { get; set; }   // ticked time; analytics only
}

public sealed record CertState        // last server-certified checkpoint, 10 section 4
{
    public long   Seq                    { get; init; }
    public string StateHash              { get; init; } = "";   // lowercase hex SHA-256, 03 section 22
    public long   CertifiedAtServerSec   { get; init; }
    public string GenesisHash            { get; init; } = "";
}

public sealed record PendingReceipt     // IAP only, 08 section 6
{
    public string IdempotencyKey         { get; init; } = "";
    public string Platform               { get; init; } = "";
    public string ProductId              { get; init; } = "";
    public string TransactionId          { get; init; } = "";
    public string Payload                { get; init; } = "";
    public long   RequestedAtServerSec   { get; init; }
}

public sealed record PendingPull       // remote gacha only, 08 section 4.4
{
    public string IdempotencyKey        { get; init; } = "";
    public string BannerId              { get; init; } = "";
    public int    Count                 { get; init; }
    public string PaymentKind           { get; init; } = "";   // FreeDaily | FreeBanked | Gems (04 section 7.5)
    public long   RequestedAtServerSec  { get; init; }
}
```

Reused records, defined elsewhere and serialised as declared there:

| Record | Defined in | Fields in the save |
|---|---|---|
| `Currencies` | 04 section 3 | `Gold` (double), `Gems`, `FreePullsDaily`, `FreePullsBanked`, `Shards`, `EnhMaterials`, `DungeonTickets`. "Free pulls used today" is `FREE_PULLS_PER_DAY - FreePullsDaily`; the remaining count is what is stored. |
| `ProgressionState` | 04 section 10 | `StatLevel`, `ClassAdvances`, `HighestFirstClearBossStage`, `ChestTimerStart` (server seconds), `TutorialGranted`. |
| `GearState` / `GearPiece` | 04 section 5.1 | `Equipped`: array of exactly `GEAR_SLOTS` entries, each `null` or `{ Slot, Rarity, Tier, EnhLevel }`. `BasePct` is derived and never stored. |
| `HeroState` / `HeroId` | 04 section 10 | `Copies`: map from hero id to copy count. `Equipped`: array of exactly `HERO_SLOTS` entries, each `null` or a hero id. `AutoEquipBest`. |
| `DailyState` | 04 section 7.1 | `DayKey`, `Progress[]`, `Rewarded[]` (indexed like `ContentDb.Quests`), `DungeonTicketsUsed`. |

### 3.2 Field notes

- **Hero ids in JSON.** `HeroId(Rarity Tier, int Index)` serialises as the string `{TierLetter}{Index:D2}`, for example `"E07"` or `"M00"`. `Copies` is a JSON object keyed by that string; `Equipped` is an array of those strings or `null`. One Json.NET converter handles both directions.
- **Enums** (`GearSlot`, `Rarity`) serialise as their member names (`"Weapon"`, `"L"`), not integers. Rule: a saved enum member is never renamed in place; a rename is a migration step that rewrites the string.
- **Seeds.** `RngStreamState.Seed` is a hex string, not a JSON number, because numbers above 2^53 are not exact in every JSON parser the save may meet later (cloud save, tooling). `Counter` fits in a `long` (2^63 draws is unreachable).
- **Seed derivation (prototype).** `seed(name) = first 8 bytes of SHA-256(ProfileId.ToByteArray() ++ UTF8(name))` as a little-endian ulong. All five streams of 00 section 5.2 (`combat`, `gear_drop`, `gear_rarity`, `enhance`, `gacha_hero`) are created with `Counter = 0` at profile creation. 01 section 6 covers why the live `gacha_hero` seed moves server-side (`specs/08`).
- **Timestamps** are `long` unix seconds, UTC, read from `IClock` (section 11). Field names end in `ServerSec`.
- **Gold is a `double`** (02 section 2). Json.NET writes doubles in round-trip form, so a value survives save/load bit-exactly.
- **First clears** are the set `{BOSS_EVERY, 2 * BOSS_EVERY, ..., HighestFirstClearBossStage}` compressed to one integer because P0 stages are linear and monotone (04 section 7.3). If P1 stage select arrives, the migration replaces it with a bitset keyed by `stage / BOSS_EVERY`.
- **Idle chest timer** is `Progression.ChestTimerStart`; readiness is `ChestTimerStart + IDLE_CHEST_HOURS * 3600 <= now` (04 section 7.4). Nothing else about the chest is stored.
- **Quest progress** is positional (`Progress[i]` is quest `i` in `ContentDb.Quests`). Section 7.2 rule P9 pads or truncates on a count mismatch. See open decision 2.
- **Banners** is a map so a P1 pet or rate-up banner is a new key, not a schema change. Unknown keys are kept verbatim on load (rule P7).

### 3.3 What is deliberately not saved

- The current enemy's remaining HP, and an in-progress boss attempt (boss HP, timer). On load the player faces a fresh normal mob at `Combat.Stage`; an attempt that was in progress is abandoned at no cost, which matches 02 section 3 (a timed-out attempt costs nothing but the 20 s).
- The `SimEvent` queue (04 section 10): a save mid-queue loses toasts, never state.
- Derived values: `BasePct`, hero level, DPS, kill time, `ChestReadyAt`, class multiplier. All are recomputed from `ContentDb`, so retuning constants reflows every save with no migration.
- Content tables. The save stores ids and counters only; `ContentVersion` records which tables were active, for analytics and for the rules in 7.2 that handle removed ids.

### 3.4 Example (abbreviated)

```json
{
  "SchemaVersion": 1,
  "ContentVersion": "2026.10.3",
  "Profile": { "ProfileId": "5f1b...", "DeviceInstallId": "9c0e...", "CreatedAtServerSec": 1793404800,
               "LastSavedAtServerSec": 1793491200, "SaveSequence": 412, "DebugTainted": false },
  "Combat": { "Stage": 173, "KillsInStage": 4 },
  "Currencies": { "Gold": 184233.5, "Gems": 240, "FreePullsDaily": 1, "FreePullsBanked": 0,
                  "Shards": 0, "EnhMaterials": 0, "DungeonTickets": 0 },
  "Progression": { "StatLevel": 128, "ClassAdvances": 2, "HighestFirstClearBossStage": 170,
                   "ChestTimerStart": 1793488000, "TutorialGranted": true },
  "Gear": { "Equipped": [ { "Slot": "Weapon", "Rarity": "E", "Tier": 170, "EnhLevel": 6 }, null,
                          { "Slot": "Chest", "Rarity": "C", "Tier": 160, "EnhLevel": 15 }, null, null, null ] },
  "Heroes": { "Copies": { "C03": 7, "R11": 2, "E07": 1 }, "Equipped": [ "E07", "R11", "C03", null, null, null ],
              "AutoEquipBest": true },
  "Banners": { "hero_std": { "PityCounter": 23, "TotalPulls": 41 } },
  "Rng": { "combat":      { "Seed": "9f86d081884c7d65", "Counter": 2210334 },
           "gear_drop":   { "Seed": "3a1c0b8e55f2d7aa", "Counter": 18502 },
           "gear_rarity": { "Seed": "0c7e2b9d4411fa63", "Counter": 184 },
           "enhance":     { "Seed": "d41d8cd98f00b204", "Counter": 97 },
           "gacha_hero": { "Seed": "e3b0c44298fc1c14", "Counter": 123 } },
  "Daily": { "DayKey": 20757, "Progress": [1, 312, 0, 1], "Rewarded": [true, false, false, false],
             "DungeonTicketsUsed": 0 },
  "Stats": { "Kills": 18502, "BossKills": 17, "GearDrops": 92, "EnhanceAttempts": 97, "ActiveSeconds": 26140 },
  "PendingPull": null
}
```

A save of this shape is 2 to 4 KB. There is no size concern at any stage count.

## 4. Serialisation

### 4.1 Decision: JSON for the prototype

Use JSON through Newtonsoft.Json (Unity's `com.unity.nuget.newtonsoft-json` package; the same `Newtonsoft.Json` 13.x assembly from NuGet in the .NET test and bot projects). Settings: property names as declared (PascalCase), `NullValueHandling.Include` (empty gear and hero slots are explicit `null`s), `MissingMemberHandling.Ignore` on read, `Formatting.None` in release and `Indented` in development builds, UTF-8 without BOM.

Why JSON:
- Migrations operate on a JSON DOM (`JObject`) with no per-version C# types (section 8). With a binary schema every historical version would need its own generated classes.
- Human-readable saves are the main debugging tool in a prototype: paste into a bug report, hand-edit a fixture, diff two saves.
- No codegen step in the build. The bot harness and the Unity client share one assembly with no toolchain beyond the compiler.
- Size and speed are irrelevant at 2 to 4 KB.

Why not the alternatives:
- Unity `JsonUtility`: no dictionaries, no nullable, no polymorphism.
- `System.Text.Json`: not shipped with Unity's runtime; adding it with IL2CPP and its reflection-based serialiser is a known source of stripped-member failures, and the source-generator path does not fit Unity's compilation pipeline. Not worth the fight for a prototype.
- Protobuf now: codegen, opaque files, and the migration chain would have to be written against generated classes per version.

### 4.2 File layout

A save file is one ASCII header line followed by the JSON body:

```
HEROCAT-SAVE fmt=json crc32=1a2b3c4d len=3917\n
{ ...exactly len bytes of UTF-8 JSON... }
```

- `fmt` is the body encoding (`json` now; `pb` after the protobuf cut-over in 4.3).
- `crc32` is CRC-32 (IEEE, the zip polynomial) of the body bytes as 8 lowercase hex chars.
- `len` is the body byte count.
- The schema version is **not** in the header; `SchemaVersion` inside the body is authoritative and the header is a container, not a schema.

The loader reads the header line, then exactly `len` bytes, and rejects the file if any of these fail: header malformed, fewer bytes than `len` remain, more bytes than `len` remain, CRC mismatch, JSON parse failure. A truncated write therefore always fails fast instead of loading a half document.

```csharp
public static class SaveSerializer
{
    public static byte[]  Encode(GameState s);                        // header + body
    public static (int schemaVersion, JObject body) Decode(byte[] file);   // throws SaveCorruptException
    public static GameState Materialize(JObject bodyAtCurrentVersion);     // Json.NET deserialise with the converters in 3.2
}
```

### 4.3 Migration to protobuf (when, and how it stays additive)

Trigger: a server component that reads saves is written in a language where a shared `.proto` is worth more than readable files, or save size starts to matter for cloud sync. Neither is expected before P1 cloud save, and possibly not then.

Mechanics, so that nothing in this spec has to change shape:
1. Pick the cut-over schema version `N`. Generate `GameState.proto` from the C# record tree at version `N`.
2. The header `fmt=pb` selects the decoder. A `pb` body is always at version `>= N`.
3. JSON migration steps `1 -> ... -> N-1` stay exactly as they are. Step `N-1 -> N` is the last JSON step and ends by materialising `GameState` and re-encoding as protobuf.
4. From `N` on, schema evolution uses protobuf field numbers (new fields default, retired fields reserved). Changes that protobuf cannot express as defaults (a split, a unit change) still get a `Migrate` step, operating on `GameState` instead of `JObject`.
5. The loader's version check, the validation rules in 7.2 and everything after them are unchanged.

## 5. Writing a save

### 5.1 Stamp

Every write first stamps the state. The bot harness calls the same function instead of writing a file, so the catch-up anchor behaves identically in both (section 9.3).

```csharp
public static class SaveStamp
{
    public static void Apply(GameState s, long nowServerSec, string contentVersion)
    {
        s.Profile.LastSavedAtServerSec = nowServerSec;
        s.Profile.SaveSequence += 1;
        s.ContentVersion = contentVersion;
    }
}
```

### 5.2 Atomic write with rotation

Directory: `Application.persistentDataPath/saves/` on device; a caller-supplied directory in tests and the bot. Files: `slot0.sav` (newest good), `slot0.prev.sav` (previous good), `slot0.tmp` (in flight). P0 has one slot.

```
Write(state):
  SaveStamp.Apply(state, clock.NowServerSeconds, db.Version)
  bytes = SaveSerializer.Encode(state)
  using fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
      fs.Write(bytes); fs.Flush(flushToDisk: true)          // fsync the data
  if File.Exists(sav): File.Move(sav, prev, overwrite: true) // rename(2): atomic on APFS, ext4, f2fs
  File.Move(tmp, sav, overwrite: true)                       // rename(2)
```

Properties:
- At every instant at least one of `slot0.sav`, `slot0.prev.sav` is a complete, CRC-valid file. The only window with no `slot0.sav` is between the two renames, and the loader covers it by falling back to `slot0.prev.sav` (section 7.1).
- A crash during the `tmp` write leaves a partial `tmp` and untouched `sav`/`prev`. Startup deletes any stray `slot0.tmp`.
- The directory entry is not fsynced (not reachable from C#). On a power loss within the filesystem's journal commit window the renames may be lost, which leaves the previous `slot0.sav` intact. Accepted.
- Failure anywhere throws `SaveWriteException`; the caller keeps the in-memory state and retries at the next autosave (5.3). Note that `SaveStamp.Apply` has already run, so memory carries a newer `LastSavedAtServerSec` than disk; section 9.4 covers the consequence.

**Command log sidecar (ranked profiles, P1).** Between server certifications the command log (`specs/03` section 22) is held in memory and written to `slot0.log` on the same cadence as the save, inside the same rotation, so a save and its log are replaced together or the previous pair is kept (`specs/10` section 12). Unranked profiles write no sidecar.

### 5.3 Autosave cadence

Owner: `SessionRunner` (section 10). Writes are synchronous on the main thread in P0; a 4 KB encode plus write is well under a frame on any supported phone.

Periodic: when `dirty` and at least `AUTOSAVE_INTERVAL_SEC` of monotonic time has passed since the last successful write. `dirty` is set by any `SimEvent` and by any tick that killed an enemy; in combat it is always set, so in practice a write every 30 s.

Immediate (bypass the interval, no debounce):
- App pause (`OnApplicationPause(true)`) and app quit. Mobile OSes may kill a paused app within seconds, so the write completes before the callback returns.
- After a pull batch is applied (local or remote), so a result the player saw is on disk.
- After offline catch-up is applied at session open or resume (section 9.2).
- After any debug command (section 13).
- After a `ClassAdvanced` event.

Failure policy: count consecutive failures; at `SAVE_FAIL_WARN_COUNT` the client shows a persistent, non-blocking "Progress could not be saved. Is storage full?" banner and emits `save_write_failed`. Gameplay never blocks on a save.

If profiling ever shows the write on the frame, keep the encode on the main thread (it is the snapshot) and move the file I/O to a single-writer worker so rotation order is preserved. Not needed for P0.

## 6. Creating a profile

```
NewProfile(now):
  id = Guid.NewGuid()
  s = new GameState {
        Profile = { ProfileId = id, DeviceInstallId = InstallId(), CreatedAtServerSec = now, LastSavedAtServerSec = now },
        Combat  = { Stage = 1, KillsInStage = 0 },
        Rng     = one RngStreamState per name in 00 section 5.2 with Seed = seed(name), Counter = 0 }
  Economy.OnProfileCreated(now)     // 04 section 8: tutorial gems, banked pulls, chest timer, TutorialGranted
  return s
```

`InstallId()` reads or creates `persistentDataPath/install_id` (a GUID). It identifies the install, not the player, and only matters for cloud-save lineage (08 section 5).

## 7. Loading

### 7.1 Load path

```
Load(now, db):
  delete stray slot0.tmp; delete slot0.corrupt-* older than CORRUPT_KEEP_DAYS
  for file in [slot0.sav, slot0.prev.sav]:
      if not exists: continue
      try:
          (v, json) = SaveSerializer.Decode(read(file))
          if v > SaveSchema.Current: throw SaveTooNewException     // propagates; see below
          json  = SaveMigrations.Migrate(v, json)                   // section 8
          state = SaveSerializer.Materialize(json)
          repairs = SaveValidator.Run(state, now, db)               // section 7.2; throws SaveInvalidException
          if file is prev: analytics save_fallback
          return (state, repairs)
      catch SaveCorruptException | SaveInvalidException | JsonException: continue
  if either file existed: rename each to slot0.corrupt-{unixNow}.sav; analytics save_corrupt
  return (NewProfile(now), [])
```

Then, in this order, before the first tick: `Economy.OnTick(now)` (runs `EnsureDay`, 04 section 7.1), `OfflineCatchUp.Apply` (section 9), emit `OfflineCatchUpApplied`, `Write`.

`SaveTooNewException` aborts the load and the client shows "This save was made by a newer version of the game. Please update." It must not fall through to `prev` (which might be older and loadable, silently discarding the newer progress) and must not write anything. This only happens on a downgrade, which TestFlight and internal tracks do allow.

Quarantine instead of delete so a bug in the loader does not destroy a player's save; the corrupt files are removed after `CORRUPT_KEEP_DAYS`. P1 cloud save restores from the server instead of starting a new profile.

### 7.2 Validation and repair rules

Runs on a materialised `GameState` against the loaded `ContentDb`. Reject rules throw `SaveInvalidException` and the loader moves to the next file. Repair rules fix the field, record the rule id, and emit `save_repaired{rule}`. Content-related drops count as one repair per rule, not per item. More than `SAVE_REPAIR_MAX` repairs on one file is a reject.

Reject:

| Id | Condition |
|---|---|
| R1 | Header, length or CRC mismatch, or JSON parse failure (from `Decode`). |
| R2 | `SchemaVersion` missing or `< 1`. |
| R3 | `Profile.ProfileId == Guid.Empty`. |
| R4 | `Rng` lacks any of the five names in 00 section 5.2, or any `Seed` is not 16 hex chars, or any `Counter < 0`. Unknown extra stream names are dropped with a log, not rejected. |
| R5 | Any root section is `null` after migration (`Profile`, `Combat`, `Currencies`, `Progression`, `Gear`, `Heroes`, `Banners`, `Rng`, `Daily`, `Stats`). |

Repair:

| Id | Condition | Repair |
|---|---|---|
| P1 | `Combat.Stage < 1`; `KillsInStage` outside `[0, KILLS_PER_STAGE - 1]` | Set to 1; clamp. |
| P2 | Any currency negative; `Gold` is NaN or infinite | Set to 0. |
| P3 | `StatLevel < 0` | Set to 0. |
| P4 | `ClassAdvances != count of CLASS_STAGES entries <= Stage` | Recompute (04 section 6 asserts equality in P0). |
| P5 | `HighestFirstClearBossStage` not a multiple of `BOSS_EVERY` or negative | Floor to a multiple; min 0. |
| P6 | `ChestTimerStart > now` (clock went backwards) | Set to `now`. A full timer from now is the most a player can lose; it cannot be exploited because it only ever shortens the wait to one timer. |
| P7a | `Gear.Equipped.Length != GEAR_SLOTS` | Pad with `null` / truncate. |
| P7b | Piece whose `Slot` is not its index | Move to its own slot if empty, else drop. |
| P7c | `EnhLevel` outside `[0, ENH_MAX]`; `Tier < 0` or not a multiple of 10; `Rarity` not a defined member | Clamp; floor to a multiple of 10 (min 0); drop the piece. |
| P8a | `Heroes.Equipped.Length != HERO_SLOTS` | Pad / truncate. |
| P8b | Equipped id with no entry in `Copies`, unknown to `ContentDb`, or duplicated | Set that slot to `null` (keep the first of duplicates). |
| P8c | `Copies` key unknown to `ContentDb`, or value `<= 0` | Remove the entry. Shard refund is open decision 5. |
| P9a | `PityCounter` outside `[0, PITY_HARD]`; `TotalPulls < 0` | Clamp; set to 0. |
| P9b | The P0 banner id missing from `Banners` | Add a default `BannerState`. Unknown banner ids are kept verbatim. |
| P10 | Stream counters disagree with the facts: `gacha_hero.Counter != 3 * sum(TotalPulls)`, `gear_drop.Counter != Stats.Kills`, `gear_rarity.Counter != 2 * Stats.GearDrops`, `enhance.Counter != Stats.EnhanceAttempts` | Set the counter from the stat. The player-visible counts are the source of truth for "which draw are we on" (01 section 6: pull `k` is `Derive(seed, k)`). `combat` has no invariant. |
| P11 | `Daily.Progress.Length` or `Rewarded.Length != db.Quests.Count` | Pad with 0 / `false`, or truncate. |
| P12 | `ContentVersion` empty | Set to `db.Version`. |

Left alone on purpose: `Daily.DayKey > DayKey(now)` and `LastSavedAtServerSec > now` (clock went backwards; sections 9 and 11 handle both without touching the save), `CreatedAtServerSec > now` (cosmetic), `HighestFirstClearBossStage > Stage` (possible after a debug `SetStage` downward; harmless), `PendingPull` non-null (owned by 08 section 4.4).

## 8. Migrations

### 8.1 API

```csharp
public static class SaveSchema
{
    public const int  Current  = 1;      // SAVE_SCHEMA_VERSION
    public const bool V1Frozen = false;  // set true the day a build ships (8.3); never set back
}

public static class SaveMigrations
{
    // Steps[i] migrates version i+1 to version i+2. Invariant: Steps.Length == SaveSchema.Current - 1.
    static readonly Func<JObject, JObject>[] Steps = { };

    public static JObject Migrate(int v, JObject json)
    {
        if (v > SaveSchema.Current) throw new SaveTooNewException(v, SaveSchema.Current);
        for (; v < SaveSchema.Current; v++)
        {
            json = Steps[v - 1](json);
            json["SchemaVersion"] = v + 1;
            Analytics.Track(new MigrationApplied(v, v + 1));
        }
        return json;
    }
}
```

### 8.2 Rules for a step

1. A step is a pure function `JObject -> JObject`. It reads no `ContentDb`, no `IClock`, no file, no current C# type. Any value it needs is a literal frozen inside the step, because content tables and code will keep changing after the step is written and the step must produce the same output forever.
2. A step tolerates absent optional fields (saves written by a dev build mid-version). It never throws on a shape it can plausibly see; it fills defaults.
3. A step does not set `SchemaVersion`; the driver does.
4. Forward only. There is no down-migration. A newer save is refused (7.1), never degraded.
5. Each step ships with two fixtures and a test (section 14, tests M1 to M4): `Fixtures/Saves/v{n}.json` (a real save at version `n`, taken from a device or the bot at the time) and `v{n}.to.v{n+1}.expected.json`.
6. Renames are expressed as a rename, not drop-and-add, so a field keeps its history. A field may be dropped only when nothing in the current code can use it.
7. Adding a new RNG stream (04 section 11.1's `gear_substat`, for instance) is a migration step that inserts `{ Seed: seed(name), Counter: 0 }`. The step cannot call the SHA-256 derivation (rule 1 forbids reaching into current code), so it writes a placeholder `Seed = ""` and the validator's R4 is relaxed for that one name to re-derive from `ProfileId` on first load. Record the exemption in the step's comment.

What counts as a schema change (requires a bump and a step once frozen): adding, removing, renaming or retyping any serialised property; renaming an enum member; changing a dictionary key format; changing the set of required stream names; changing a field's meaning (seconds to milliseconds, remaining to used). What does not: content changes (`ContentVersion` is data), new keys in `Banners`, new hero or gear ids, new values in existing fields.

Illustrative future step (P1 substats and the `gear_substat` stream), to fix the style:

```csharp
// v1 -> v2: gear substats (04 section 11.1) and the gear_substat stream.
static JObject V1ToV2(JObject j)
{
    foreach (var piece in j["Gear"]!["Equipped"]!.Children().Where(p => p.Type == JTokenType.Object))
    {
        piece["Substats"] ??= new JArray();
        piece["Locked"]   ??= false;
    }
    ((JObject)j["Gear"]!)["Pending"] ??= new JArray(Enumerable.Repeat<object?>(null, 6));   // literal 6, not GEAR_SLOTS (rule 1)
    ((JObject)j["Rng"]!)["gear_substat"] ??= new JObject { ["Seed"] = "", ["Counter"] = 0 };  // rule 7 exemption
    return j;
}
```

### 8.3 Freeze rule

**v1 is frozen the day a build ships.** "Ships" means the first build installed on a device whose save the team is not willing to delete: an external TestFlight or Play test track build, or any build handed to a playtester outside the dev team. On that day:

1. Set `SaveSchema.V1Frozen = true`.
2. Commit `Fixtures/Saves/v1.json` (a real save from that build) and the SHA-256 of that file as a constant in the test project.
3. From then on every schema change bumps `Current`, adds a step, and adds the fixture pair.

Before the freeze, `GameState` changes freely with no bump. A dev save that no longer fits fails validation, is quarantined, and the dev gets a new profile. That is the intended cost of a pre-freeze change.

## 9. Offline catch-up

### 9.1 Algorithm (02 section 7, verbatim in code)

```csharp
public static class OfflineCatchUp
{
    public sealed record Result(long ElapsedSec, long CreditedSec, bool Capped, double Gold, int Stage, double KillTimeSec);

    // Call exactly once per session open or app resume, after Economy.OnTick(now) and before the first Tick.
    // Mutates Currencies.Gold only. Streams, kills, KillsInStage, quests, chest timer and Stage are untouched.
    public static Result Apply(GameState s, long nowServerSec, ContentDb db)
    {
        long   elapsed  = Math.Max(0, nowServerSec - s.Profile.LastSavedAtServerSec);   // clock backwards => 0
        long   cap      = db.OFFLINE_CAP_H * 3600;
        long   credited = Math.Min(elapsed, cap);
        int    stage    = s.Combat.Stage;
        double k        = Simulation.KillTimeSeconds(s, db);                             // specs/03; formula below
        double gold     = credited * db.OFFLINE_RATE * Economy.GoldPerKill(stage, db) / k;
        s.Currencies.Gold += gold;
        return new Result(elapsed, credited, elapsed > cap, gold, stage, k);
    }
}
```

Where, from 00 section 3 with the player's DPS computed from the loaded state:

```
hit         = DPS(stage) * ATTACK_INTERVAL_TICKS / TICK_HZ
killTicks   = max(ATTACK_INTERVAL_TICKS, ceil(HP(stage) / hit) * ATTACK_INTERVAL_TICKS) + SPAWN_DELAY_TICKS
k           = killTicks / TICK_HZ                       // seconds; minimum 1.5 s
GoldPerKill = GOLD0 * GOLD_G^stage                      // normal mob; no BOSS_GOLD_MULT
```

Properties that follow, each with a test in section 14:
- **Closed form, O(1).** No ticks are run; a 30-day absence costs the same as 30 seconds.
- **No stage push.** `Stage`, `KillsInStage`, every stream counter, `Stats.Kills`, quest progress and `HighestFirstClearBossStage` are unchanged. 02 section 7 explains why (drops and boss attempts need the RNG streams).
- **Cap.** `credited <= OFFLINE_CAP_H * 3600`. `Capped` drives the "earnings capped at 8 h" line in the welcome-back card.
- **Walled player.** A player parked on a boss stage farms that stage's normal mobs, so `k` uses normal `HP(stage)` and gold uses `GoldPerKill(stage)` without the boss multiplier.
- **Rate.** The real sim in the same state earns `GoldPerKill / k` per second; catch-up earns `OFFLINE_RATE` of that.

### 9.2 Applied once, against last-saved server time

The anchor is `Profile.LastSavedAtServerSec`, which `SaveStamp.Apply` sets on every write. The sequence at session open and at app resume is:

```
Economy.OnTick(now)                 // EnsureDay
r = OfflineCatchUp.Apply(state, now, db)
Emit OfflineCatchUpApplied(r)        // client shows the card if r.ElapsedSec >= OFFLINE_REPORT_MIN_SEC
Write(state)                         // stamps LastSavedAtServerSec = now, so the window cannot be credited twice
```

Because every pause writes a save (5.3), the anchor at resume is the pause time and the credited window is exactly the time the app was suspended. If the app was killed mid-session, the anchor is the last autosave, at most `AUTOSAVE_INTERVAL_SEC` before the kill; the player is credited offline gold for up to 30 s they were actually playing, and lost those 30 s of real progress. A wash, and not worth tracking separately.

### 9.3 Bot harness

The bot (`specs/09`) alternates `Simulation.Advance(ticks)` for active minutes with `OfflineCatchUp.Apply(state, lastSaved + offlineSec, db)` followed by `SaveStamp.Apply(state, lastSaved + offlineSec, db.Version)` for offline hours. It writes no files. This is the same code the client runs, so the bot's offline income is the client's offline income by construction.

### 9.4 Known double-grant window

If the `Write` after catch-up fails (disk full) and the app then dies, disk still holds the old anchor and the next load credits the same window again, bounded by one `OFFLINE_CAP_H` of gold. P0 accepts this (open decision 6). P1 cloud save removes it, because the server-side copy carries the newer anchor.

## 10. In-app idle (real ticks)

### 10.1 Rule

With the app in the foreground the real sim ticks at `TICK_HZ`, pushes stages, drops gear, and earns gold at the full rate. This is the "leave it on the desk" mode and is deliberately better than offline (02 section 7). The client keeps the screen awake on the combat screen (`specs/07`).

### 10.2 `SessionRunner`

Pure C#, in `HeroCat.Sim`. The Unity side (10.3) only forwards callbacks.

```csharp
public sealed class SessionRunner
{
    readonly Simulation sim; readonly ISaveStore store; readonly IClock clock; readonly ContentDb db;
    readonly double tickDt;
    double acc; long lastMono; long lastSaveMono; bool dirty; int failures;

    public SessionRunner(Simulation sim, ISaveStore store, IClock clock, ContentDb db)
    { this.sim = sim; this.store = store; this.clock = clock; this.db = db; tickDt = 1.0 / db.TICK_HZ; lastMono = lastSaveMono = Stopwatch.GetTimestamp(); }

    // dt comes from a monotonic clock the runner reads itself (Stopwatch.GetTimestamp()).
    // Unity's Time.unscaledDeltaTime is clamped by Time.maximumDeltaTime and cannot report a long freeze.
    public void OnFrame(long nowServerSec)
    {
        long   mono = Stopwatch.GetTimestamp();
        double dt   = (mono - lastMono) / (double)Stopwatch.Frequency;
        lastMono    = mono;

        if (dt > SaveConfig.IN_APP_GAP_SEC) { Resume(nowServerSec); acc = 0; return; }   // frozen without a pause callback

        acc = Math.Min(acc + dt, SaveConfig.IN_APP_GAP_SEC);
        int n = 0;
        while (acc >= tickDt && n < SaveConfig.MAX_TICKS_PER_FRAME) { sim.Tick(); acc -= tickDt; n++; }
        if (n > 0) dirty = true;                                   // a remainder above the cap is caught up over the next frames
        sim.Economy.OnTick(nowServerSec);                          // EnsureDay only (04 section 7.1)
        if (dirty && (mono - lastSaveMono) / (double)Stopwatch.Frequency >= SaveConfig.AUTOSAVE_INTERVAL_SEC) SaveNow();
    }

    public void OnPause(long nowServerSec)  => SaveNow();
    public void OnResume(long nowServerSec) => Resume(nowServerSec);

    void Resume(long now)
    {
        sim.Economy.OnTick(now);
        var r = OfflineCatchUp.Apply(sim.State, now, db);
        sim.Events.Emit(new OfflineCatchUpApplied(r));
        SaveNow();
        lastMono = Stopwatch.GetTimestamp();
    }

    void SaveNow()
    {
        try { store.Save(sim.State); dirty = false; failures = 0; lastSaveMono = Stopwatch.GetTimestamp(); }
        catch (SaveWriteException) { failures++; if (failures == SaveConfig.SAVE_FAIL_WARN_COUNT) sim.Events.Emit(new SaveWriteFailed(failures)); }
    }
}
```

Hitch behaviour: a hitch under `IN_APP_GAP_SEC` is ticked out at up to `MAX_TICKS_PER_FRAME` per frame (10 s of sim per frame), so a 25 s stall is caught up within three frames and no gold is lost. A gap above `IN_APP_GAP_SEC` (the OS suspended the process without a pause callback, or the debugger was attached) is credited through the offline formula at `OFFLINE_RATE`, which is the conservative choice and matches what a pause would have done.

### 10.3 Unity hooks (`HeroCat.Client`, specs/07)

`Update()` calls `runner.OnFrame(clock.NowServerSeconds)`. `OnApplicationPause(true)` calls `OnPause`; `OnApplicationPause(false)` calls `OnResume`; `OnApplicationQuit` calls `OnPause`. Nothing else in the client touches the save or the clock.

## 11. The clock seam

### 11.1 Interface and implementations

```csharp
public interface IClock
{
    long NowServerSeconds { get; }   // unix seconds, UTC, anchored to server time
    bool IsAuthoritative  { get; }   // true only when anchored to a server sync in this process (08 section 3)
}

// Prototype client. Wall clock plus a test offset. Deliberately NOT authoritative.
public sealed class LocalClock : IClock
{
    public long OffsetSeconds { get; internal set; }          // debug panel and tests only (InternalsVisibleTo)
    public long NowServerSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + OffsetSeconds;
    public bool IsAuthoritative  => false;
}

// Unit tests and the bot harness.
public sealed class FakeClock : IClock
{
    public long NowServerSeconds { get; private set; }
    public bool IsAuthoritative  => true;
    public void Set(long t)       => NowServerSeconds = t;
    public void Advance(long dt)  => NowServerSeconds += dt;
}
```

`ServerClock` (P1) is specified in 08 section 3.

### 11.2 Rules

1. **Nothing in `HeroCat.Sim` reads `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `Environment.TickCount` or `Stopwatch`, with no exceptions.** `LocalClock` and `SessionRunner` live in `HeroCat.Runtime`, and inside that assembly only `LocalClock.cs` and `SessionRunner.cs` may touch those members. A CI check in `specs/09` greps both assemblies' sources and fails the build on a hit.
2. Economy consumers of `IClock`: `EnsureDay` and the day key, the idle chest timer, the catch-up anchor and `OfflineCatchUp.Apply`, `CreatedAtServerSec`, `LastSavedAtServerSec`, `PendingPull.RequestedAtServerSec`, analytics timestamps. Combat ticks never read the clock; they are driven by the monotonic accumulator in 10.2.
3. **The device clock is never trusted for the economy.** In the prototype `LocalClock` is the device clock by definition; that is the accepted gap of 00's client-authoritative decision, bounded in 11.3, and closed by `ServerClock` in P1. The Unity client never passes `DateTime` into the sim (04 section 12 item 4).
4. **Clock goes backwards** (`now` smaller than a stored timestamp):
   - Catch-up: `elapsed = max(0, ...)`, so 0 gold, no exception (9.1).
   - Daily: `EnsureDay` rolls forward only. The rule is `if key <= Daily.DayKey: return`. (04 section 7.1 writes the comparison as `==`, which on a backwards clock would reset the day and re-pay the login bonus; this spec owns clock semantics and the 04 pseudo-code should read `<=`. Open decision 3.)
   - Chest: `ChestTimerStart > now` is repaired to `now` at load (P6) and the same clamp runs in `Economy.OnTick`.
   - `LastSavedAtServerSec` is left alone; the next write stamps the smaller `now`.
   - Emit `clock_anomaly{kind: backwards, seconds}` once per session.
5. **Clock jumps forward** (device clock set ahead): the player gains at most `OFFLINE_CAP_H` of offline gold, one lazy daily reset (04 section 7.1), and one idle chest per jump. Bounded and accepted in the prototype; irrelevant once `ServerClock` is authoritative.
6. **No server time available** (P1, launch while offline): `ServerClock` reports `IsAuthoritative = false` and serves the device clock floored at `LastSavedAtServerSec`, so time cannot run backwards past the last save. All economy proceeds under the offline-tolerance table in 08 section 9; the loss is bounded as in rule 5. Remote gacha is blocked without a server anyway (08 section 4).

## 12. Anti-save-scum reasoning

### 12.1 What the save guarantees

Every RNG consumer is a `(seed, counter)` stream and the counter is in the save (00 section 5.2). Draw `n` of a stream is a pure function of `(seed, n)`, so:

- Pull `k` on a banner is always `Derive(seed, 3k .. 3k+2)` (01 section 6). Reloading an older save restores an older counter and the player re-receives the same results they already had.
- Gear drop on lifetime kill `n` is draw `n` of `gear_drop`; its rarity and slot are draws `2d, 2d+1` of `gear_rarity` for drop `d`; enhancement attempt `m` is draw `m` of `enhance` (04 section 5.2 and 5.6). Reloading before a failed enhancement fails it again.
- Killing the app mid-pull is harmless. The debit, the roll, the copy increment and the counter advance are one in-memory transaction; the write is atomic (5.2). Either the whole pull is on disk, or none of it is and the gems are still there and the identical result rolls next time.
- A restored device backup (iCloud, Android auto backup) is just an older save. It rewinds currencies and progress; it cannot change any future outcome, and because gear auto-equip and sell are deterministic (04 section 5.4), replaying the same kills reproduces the same gear.

Rule P10 in 7.2 keeps the counters tied to the visible counts, so even a hand-edited counter is pulled back to "the next draw after the pulls you have".

### 12.2 What it does not protect against, and why that is accepted

| Attack | Effect | Why accepted in P0 |
|---|---|---|
| Editing the JSON (rooted device, desktop copy). The CRC is recomputable. | Arbitrary gold, gems, stage, copies. | 00 section 1 and 5: P0 is client-authoritative and single-player. (For ranked profiles in P1 the save is not trusted at all: the server replays the command log and compares `StateHash`, `specs/10` section 4, so an edited save simply fails certification.) There is no leaderboard, PvP or guild until P2, so a cheater only cheats their own progression. The money-adjacent assets (gems, pulls, receipts) move server-side in P1 (08 sections 4 and 6). An HMAC would need a key in the client, which is extractable: obfuscation, not protection, and not worth the engineering before P1. |
| Reading `gacha_hero.Seed` and computing future pulls with the public generator. | Decide whether to pull now or wait. | 01 section 6: live, the server owns the seed and returns results; the client never has it. In the prototype the seed is local and this is accepted. |
| Device clock jumps. | At most `OFFLINE_CAP_H` gold, one daily reset, one chest per jump (11.2 rule 5). | Bounded; `ServerClock` in P1 makes the device clock irrelevant. |
| Speed hacks on the process clock or memory editing. | Faster ticks, edited values. | Same class as file editing; same reasoning. |
| Modified builds (debug define forced on). | Debug panel in a release-shaped build. | Same class. Section 13's release assertion protects the team's own release pipeline, not the build from a determined attacker. |

What the prototype does anyway, because the real harm of cheating in P0 is polluted tuning data, not unfairness: `Profile.DebugTainted` is set by any debug command and flows into every analytics event so tainted profiles are excluded from the `specs/09` tables; the analytics sink flags a profile whose stage exceeds twice the 02 section 6 bot curve for its day count (`specs/08` section 8), which also catches edited saves.

## 13. Debug and cheat panel (development builds only)

### 13.1 Contract

```csharp
#if HEROCAT_DEBUG
namespace HeroCat.Sim.Debug;

public sealed class DebugCommands
{
    public DebugCommands(Simulation sim, LocalClock clock, ISaveStore store, ContentDb db, SessionRunner runner);

    public void AddGold(double amount);                 // Currencies.Gold += amount (may be negative; clamps at 0)
    public void AddGems(long amount);                   // Currencies.Gems += amount (clamps at 0)
    public void SetStage(int stage);                    // Combat.Stage = max(1, stage); KillsInStage = 0; ClassAdvances recomputed
                                                        // (04 section 6 while-loop); HighestFirstClearBossStage untouched
    public void AdvanceTime(long seconds, bool simulateAppClosed = true);
                                                        // clock.OffsetSeconds += seconds. With simulateAppClosed: runner.OnPause() first,
                                                        // then runner.OnResume(), so the real catch-up and EnsureDay paths run.
                                                        // Without: just the offset; the next OnFrame's EnsureDay sees the new day.
    public void SetPity(string bannerId, int n);        // PityCounter = clamp(n, 0, PITY_HARD)
    public void GrantHeroCopies(HeroId id, int copies);   // through the same dupe-to-level path as a pull, no stream draw, no quest progress
    public void GrantGear(GearSlot slot, Rarity rarity, int tier, int enh);   // through Economy.Receive (04 section 5.4)
    public void CompleteDailyQuests();                  // Progress[i] = Target for every quest; rewards through the normal path
    public void ResetDaily();                           // Daily.DayKey = -1; next EnsureDay rolls
    public void ForceSave();
    public void WipeSave();                             // deletes slot0.sav, slot0.prev.sav; new profile; restarts the runner
    public void CorruptPrimarySave();                   // truncates slot0.sav to half its length, to exercise the prev fallback
    public string ExportSaveJson();                     // the body of Encode(state), indented
    public void   ImportSaveJson(string body);          // Decode-less path: Migrate + Materialize + Validate, then replaces state and saves
}
#endif
```

Every command: sets `Profile.DebugTainted = true`, emits `debug_used{command}`, and ends with `ForceSave()`.

### 13.2 Compiled out of release

- The whole `HeroCat.Sim.Debug` namespace and the Unity panel (`specs/07`, its own asmdef with a define constraint) compile only under `HEROCAT_DEBUG`.
- `HEROCAT_DEBUG` is set in the Development build profile and absent from the Release profile. The release build script asserts the symbol is absent before building.
- `specs/09` CI loads the release `HeroCat.Sim.dll` and asserts by reflection that no type named `DebugCommands` exists (test D1).
- `LocalClock.OffsetSeconds` has an `internal` setter visible to the test and debug assemblies only. In release nothing can call it.

## 14. Unit tests

Files: `HeroCat.Runtime.Tests/SaveSerializerTests.cs`, `SaveStoreTests.cs`, `SaveValidatorTests.cs`, `MigrationTests.cs`, `OfflineCatchUpTests.cs`, `ClockTests.cs`, `SessionRunnerTests.cs`. All use a test `ContentDb` from the P0 tables, `FakeClock`, and a temp directory per test.

Serialisation (S):
1. **S1 round trip.** A `GameState` with every field non-default (including `null` gear and hero slots, an empty `Copies`, a non-null `PendingPull`) encodes and decodes to a value-equal record.
2. **S2 determinism after round trip.** For each of the five streams, the next draw after save/load equals the next draw of the un-restored copy (generalises 01 test 7).
3. **S3 seeds.** `Seed` values `0`, `ulong.MaxValue` and a random value round-trip as 16 hex chars.
4. **S4 header.** Each of: wrong CRC, `len` larger than the body, `len` smaller than the body, missing newline, body truncated by one byte, raises `SaveCorruptException`.
5. **S5 enums as strings.** The written body contains `"Rarity": "L"` and `"Slot": "Ring"`, never integers. On read, integer enum values are still accepted (Json.NET default) so a hand-edited fixture loads.

Store (T):
6. **T1 rotation.** After two writes: `slot0.tmp` absent, `slot0.sav` is write 2, `slot0.prev.sav` is write 1, `SaveSequence` is 2.
7. **T2 fallback.** Corrupt `slot0.sav` (flip a body byte): load returns the `prev` state and emits `save_fallback`.
8. **T3 both corrupt.** Two quarantined `slot0.corrupt-*.sav` files exist, a new profile is returned, `save_corrupt` emitted. Quarantined files older than `CORRUPT_KEEP_DAYS` are deleted on the next load.
9. **T4 crash between renames.** A store with a fault injected after the first `Move`: `slot0.sav` absent, `slot0.prev.sav` present; load succeeds from `prev`.
10. **T5 too new.** `SchemaVersion = Current + 1` raises `SaveTooNewException`; no file is modified or renamed; `prev` is not consulted.
11. **T6 write failure.** A directory made read-only: `Save` throws `SaveWriteException`, state in memory is intact, `SaveStamp` ran (sequence advanced), and the next successful write lands.

Validation (V):
12. **V1 to V12.** One table-driven case per repair rule P1 to P12 asserting the repaired value and the emitted rule id; one case per reject rule R2 to R5 asserting `SaveInvalidException`. P10 covers all four stream invariants separately.
13. **V13 repair cap.** A save violating `SAVE_REPAIR_MAX + 1` rules is rejected.
14. **V14 unknown banner kept.** A `Banners` key not in content survives load and the next write.

Migrations (M):
15. **M1 step count.** `Steps.Length == SaveSchema.Current - 1`.
16. **M2 per step.** For each `n < Current`: `Migrate(n, v{n}.json)` equals `v{n}.to.v{n+1}.expected.json` ignoring whitespace, and `Materialize` + `Validate` of the result produces zero repairs.
17. **M3 chain.** `Migrate(1, v1.json)` through `Current` loads with zero repairs.
18. **M4 purity.** Each step runs successfully with the `ContentDb` and `IClock` statics replaced by throwing stubs.
19. **M5 freeze.** When `SaveSchema.V1Frozen`, SHA-256 of `Fixtures/Saves/v1.json` equals the committed constant.

Offline catch-up (O):
20. **O1 exact value.** Stage 100, DPS chosen so `k = 2.0 s`, elapsed 3600: gold equals `3600 * OFFLINE_RATE * GOLD0 * GOLD_G^100 / 2.0` within 1e-9 relative.
21. **O2 cap.** Elapsed `OFFLINE_CAP_H * 3600 + 1`: `CreditedSec == OFFLINE_CAP_H * 3600`, `Capped == true`, gold equals the cap value.
22. **O3 nothing else moves.** Snapshot the state, apply 10 h of catch-up, and assert `Stage`, `KillsInStage`, all five counters, `Stats.*`, `Daily.*`, `Progression.*` and `Heroes.*` are value-equal to the snapshot; only `Currencies.Gold` changed.
23. **O4 once.** Apply, `Write`, `Load`, apply again with the same `now`: second result credits 0.
24. **O5 backwards clock.** `now < LastSavedAtServerSec`: `ElapsedSec == 0`, gold unchanged, no exception.
25. **O6 walled boss stage.** Stage 200 with DPS below the wall: `k` uses normal `HP(200)` and gold uses `GoldPerKill(200)` with no `BOSS_GOLD_MULT`.
26. **O7 rate against the real sim.** A walled state (boss unclearable, so no stage push) ticked for 1 h via `Advance(3600 * TICK_HZ)` with the `gear_drop` stream stubbed to never drop: catch-up for 3600 s yields `OFFLINE_RATE` times the ticked gold within 2% (tick quantisation).
27. **O8 cap over multiple sessions.** Three resumes of 3 h each credit 3 h, 3 h, 3 h (the cap is per window, not per day).

Clock (C):
28. **C1.** `LocalClock.OffsetSeconds = 86400` shifts `NowServerSeconds` by exactly 86400.
29. **C2.** `EnsureDay` with a day key one less than stored does not roll, does not reset `FreePullsDaily`, and emits `clock_anomaly`.
30. **C3.** `ChestTimerStart > now` is clamped to `now` by `OnTick`.
31. **C4 no wall clock in the sim.** The source scan from 11.2 rule 1 passes (this is the CI check, listed here so it is not forgotten).

Runner (N):
32. **N1 periodic.** Fake monotonic time: 29 s of frames produce no write; 30 s produce exactly one.
33. **N2 pause.** `OnPause` writes immediately regardless of the interval; two pauses 1 s apart produce two writes.
34. **N3 hitch.** A 25 s frame gap runs `MAX_TICKS_PER_FRAME` ticks that frame and the remaining ticks over following frames; total ticks equal `25 * TICK_HZ`.
35. **N4 freeze.** A 31 s frame gap runs zero ticks and applies catch-up for 31 s.
36. **N5 resume order.** `OnResume` runs `EnsureDay` before `OfflineCatchUp.Apply` and writes once afterwards.

Debug (D):
37. **D1.** The release-built `HeroCat.Sim.dll` contains no type named `DebugCommands` (CI, `specs/09`).
38. **D2.** `AdvanceTime(4 * 3600, simulateAppClosed: true)` yields exactly the gold `OfflineCatchUp.Apply` gives for 4 h and sets `DebugTainted`.
39. **D3.** `SetStage(160)` sets `ClassAdvances` to 2 and leaves `HighestFirstClearBossStage` unchanged.

## 15. Open decisions

1. **OS backup inclusion.** Whether to exclude `saves/` from iCloud and Android auto backup. A restored backup is harmless for RNG (12.1) and P1 cloud save supersedes it; leaning include, for the player who loses a phone before cloud save exists.
2. **Positional `Daily.Progress`.** 04 indexes quest progress by content order, which breaks silently if quests are reordered in content. Recommend keying by quest id before the v1 freeze; after the freeze it is a migration step.
3. **`EnsureDay` comparison.** 04 section 7.1 should read `if key <= Daily.DayKey: return` (11.2 rule 4). Needs the 04 author to confirm and amend.
4. **Idle-chest gems in catch-up.** 02 section 7 mentions "P1, idle-chest gems" in offline earnings; 04 section 7.4 already makes the chest timer run on server time whether or not the app is open, so nothing extra is needed. Confirm the two specs mean the same thing and strike the 02 parenthetical.
5. **Shard refund on removed heroes.** When rule P8c drops copies of a hero removed from content, whether to credit `Currencies.Shards` at 01 section 7 values. P0 shards are inert, so leaning yes once P1 spends them.
6. **Double-grant window** (9.4). Accepted for P0; closed by cloud save. Alternatively write a tiny anchor-only file before the full save; rejected for now as a second write path.
7. **Multiple profiles or slots.** P2 at the earliest. The `slot0` prefix leaves room.
8. **Protobuf cut-over trigger** (4.3). Revisit when the P1 backend is chosen (08 section 10).
9. **`AdvanceTime(simulateAppClosed: false)`.** Keep only if the Unity panel ends up using it; otherwise remove the parameter.
