## 02 - Domain Model and Persistence

Defines every field the game remembers: the `GameState` tree the sim owns, the account-level `Profile` it does not, the save schema v1 and its serializer, forward-only migrations with fixtures, the local save store, the cloud save client and its conflict rules, the integrity envelope, the clock seam, the server-side relational schema, data retention and deletion, and the analytics schema for economy events. Feature specs (03-06) say what each field means in play; this spec says its type, its default, its invariants, and where it lives.

Tiers covered: M0 (GameState, local save, FakeClock, local-only Profile), MVP (cloud save, ServerClock, integrity envelope, server schema, GDPR, analytics), P1 (guild and leaderboard tables as stubs).

Depends on: 00 (sections 4, 5, 6, 8), 01 (SessionRunner, flows, command payloads), 03 (Stat and Training semantics), 04 (collection semantics), 05 (board semantics), 06 (quests, dungeons, offline), 08 (API contracts the save client calls).

## 1. Placement and design goals

| Thing | Assembly | Why |
|---|---|---|
| `GameState` and every record under it, `RngStreamState`, `Catchup.Compute` (called only from the `ComputeOfflineCatchup` system command, issue #6) | `HeroCat.Sim` | The sim reads and writes them; pure data, no serializer attributes. |
| `Profile`, `SaveSerializer`, `SaveMigrations`, `SaveValidator`, `LocalSaveStore`, `CloudSaveClient`, `ServerClock`, `FakeClock`, `IntegrityEnvelope` | `HeroCat.Runtime` | Need Newtonsoft, IO, HTTP, clocks. |
| Relational schema, retention jobs, GDPR flow | `HeroCat.Server` | |

Goals, in priority order:

1. A save survives a kill, crash or power loss at any instant; the player loses at most `AUTOSAVE_INTERVAL_SEC` (01) of progress and never a profile.
2. Loading a save changes no future RNG result: every stream is `(seed, counter)` in the save and draw `k` is a pure function of both.
3. A save written by any shipped build loads in every later build through forward-only migrations with a fixture per version.
4. The server copy is the save of record (00 section 4); the device is a cache. Conflicts resolve by rule, with the player asked only when the rule would discard visible progress.
5. Server time is the only trusted time. A backwards clock grants nothing twice.
6. Anything a cheater can gain by editing the save is bounded and detectable by the plausibility checks in 08; money-adjacent values (diamonds from IAP, summon results) are server-authoritative so editing them is pointless.

## 2. Constants

Engineering constants in `HeroCat.Runtime.SaveConfig` (code, not `ContentDb`; migrations must not depend on content). All are `S` unless marked `I` (same role in the sibling project). None is a balance number; 10's fault-injection and perf tests are the check.

| Name | Value | Tag | Meaning |
|---|---|---|---|
| `SAVE_SCHEMA_VERSION` | 1 | I | Current JSON schema version. |
| `SAVE_KEEP_COUNT` | 2 | I | `slot0.sav` and `slot0.prev.sav`. |
| `SAVE_REPAIR_MAX` | 20 | I | Repairs on one load above which the file is treated as corrupt. |
| `CORRUPT_KEEP_DAYS` | 7 | I | Quarantined files older than this are deleted at startup. |
| `CONFLICT_KEEP_DAYS` | 30 | S | Rejected conflict candidates kept as `slot0.conflict-*.sav`. |
| `CLOUD_UPLOAD_MIN_SEC` | 60 | I | Upload coalescing window (01 section 8.10). |
| `CLOUD_FETCH_TIMEOUT_S` | 5 | I | Boot-time cloud meta fetch. |
| `CONFLICT_PROGRESS_MARGIN` | 0.10 | S | Loser must have `progressScore` more than 10% above the winner to trigger the conflict modal. |
| `CLOCK_RESYNC_SEC` | 900 | I | Foreground resync cadence when no API call has happened; any observation or sync attempt restarts it (11.1). |
| `CLOCK_JUMP_WARN_SEC` | 300 | I | Resync delta above which `clock_anomaly` is emitted. |
| `CLOCK_MAX_SKEW_ACCEPT_S` | 120 | S | Server flags `CLOCK_AHEAD` on a save whose `lastSavedServerTime` is more than this ahead of server now (accepted, not rejected: 08 section 3.1 step 5). |
| `HMAC_KEY_BYTES` | 32 | S | Device key length. |
| `SAVE_BLOB_MAX_BYTES` | 262144 | S | 256 KiB; server rejects larger uploads (a legitimate save is 20-60 KB). |
| `CMDLOG_UPLOAD_MAX_BYTES` | 1 MiB | S | Matches `CMDLOG_MAX_BYTES` (01). |
| `GDPR_DELETE_GRACE_DAYS` | 14 | S | Account recoverable for this long after a delete request. |
| `RETENTION_INACTIVE_DAYS` | 730 | S | Inactive accounts purged after 2 years. |
| `ANALYTICS_BATCH_MAX` | 100 | I | Events per upload batch. |
| `ANALYTICS_QUEUE_MAX` | 10000 | I | Offline queue cap, oldest dropped. |
| `ANALYTICS_UPLOAD_SEC` | 60 | S | Foreground seconds between interval uploads of the analytics queue (14.2); pause and quit also upload. |
| `PENDING_STALE_DAYS` | 7 | S | `Pending.Summon` / `Pending.Dungeon` older than this is cleared on load (6.4 P17). |
| `SAVE_HISTORY_KEEP` | 10 | S | Server-side blobs kept per player for support rollback (9.2, 13). |
| `DUNGEON_RECENT_RUNS_MAX` | 30 | S | `Dungeons.RecentRuns` length (issue #52). Covers a day of seeds (`DUNGEON_SEEDS_PER_DAY` 10 x 3 kinds, 08) between uploads. A sim constant (`HeroCat.Sim`), since the run-end code trims the list. |

## 3. `GameState`

### 3.1 Tree

Records are `sealed`, mutable via `set` where the sim writes them, `init` where only profile creation does. Collections default to empty. Every `double` is finite and non-negative by invariant; every id is a content id string (09). Comments give unit, range and owner spec.

This tree is canonical for names and shapes (issue #7): feature specs 03-06 own what a field means in play, and their pseudocode uses the names below. All sim state lives in `GameState`, including transient fight state (`Combat.Fight`), so `DeepClone` and replay are exact; the save drops only what 3.3 lists.

```csharp
namespace HeroCat.Sim.State;

public sealed class GameState
{
    public const int CurrentSchemaVersion = 1;                           // SAVE_SCHEMA_VERSION; Runtime's SaveSchema.Current is this constant (the Sim cannot see Runtime)
    public int    SchemaVersion   { get; set; } = CurrentSchemaVersion;  // the only version field in the body
    public string ContentVersion  { get; set; } = "";                   // ContentDb.Version active at last write (09)
    public string ContentHash     { get; set; } = "";                   // ContentDb.Hash (09 manifest sha256, lowercase hex) the save was last reconciled against; stamped with ContentVersion (issue #56)
    public Guid   PlayerId        { get; init; }                        // server playerId; Guid.Empty for an unlinked local guest (01 open question 7)
    public Guid   DeviceId        { get; set; }                         // install id of the device that wrote this save; cloud-save lineage only (9.3), never a seed input
    public Guid   RngRoot         { get; init; }                        // set once by ProfileFactory: PlayerId if known, else a fresh random Guid; the only seed input (3.4, issue #9)
    public long   SaveSeq         { get; set; }                         // +1 per write; same-device tiebreak

    public TimeState        Time        { get; init; } = new();
    public PartyState       Party       { get; init; } = new();          // the five companion slots and the party level (00 decision 109)
    public TrainingState    Training    { get; init; } = new();
    public Wallet           Wallet      { get; init; } = new();
    public CollectionState  Weapons     { get; init; } = new();
    public CollectionState  Skills      { get; init; } = new();
    public CollectionState  Companions  { get; init; } = new();
    public CollectionState  Runes       { get; init; } = new();          // P1; empty in MVP, present so no schema bump
    public LoadoutState     Loadout     { get; init; } = new();          // P1 runes only; weapons, skills and companions are equipped per slot in Party.Slots (00 decision 109)
    public FishBoardState   Fish        { get; init; } = new();
    public Dictionary<Banner, BannerState> Banners { get; init; } = new();   // one entry per Banner the player has ever seen
    public CombatState      Combat      { get; init; } = new();
    public ProgressState    Progress    { get; init; } = new();
    public QuestState       Quests      { get; init; } = new();
    public DungeonState     Dungeons    { get; init; } = new();
    public ShopState        Shop        { get; init; } = new();          // daily gift and exchange limits (06)
    public ResearchState    Research    { get; init; } = new();
    public InboxState       Inbox       { get; init; } = new();
    public OfflineState     Offline     { get; init; } = new();
    public PendingState     Pending     { get; init; } = new();
    public TutorialState    Tutorial    { get; init; } = new();
    public Dictionary<string, RngStreamState> Rng { get; init; } = new();   // key: stream name (3.4)
    public LifetimeStats    Stats       { get; init; } = new();
    public bool             DebugTainted { get; set; }                  // any debug command sets it; excluded from tuning data
}

public sealed class TimeState
{
    public long Tick                 { get; set; }        // sim ticks since profile creation; monotone
    public long LastSavedServerTime  { get; set; }        // unix s; stamped by SaveStamp on every write
    public long LastTickServerTime   { get; set; }        // unix s; catch-up anchor; every tick raises it to the virtual server now (never back), so ticked time is never offline and a replay reproduces it; also stamped when ComputeOfflineCatchup runs (01 section 8.7; issue #108)
    public long AnchorServerTime     { get; set; }        // unix s; set by each ApplyServerTime. Virtual server now = AnchorServerTime + (Tick - AnchorTick) / TICK_HZ (01 section 3.1; issue #5)
    public long AnchorTick           { get; set; }        // Tick at that ApplyServerTime
    public long CreatedServerTime    { get; init; }       // unix s
    public int  DayKey               { get; set; } = -1;  // floor(serverSeconds / DAY_SECONDS) at last rollover (06; -1 on a new profile, 06 section 4.2 rule 4); the only daily key: quests, dungeon tickets and shop all reset off it (issue #7)
    public int  WeekKey              { get; set; } = -1;  // floor((dayKey - 4) / 7) at last weekly rollover (00 contract 7; equal to floor((serverSeconds - WEEK_EPOCH_OFFSET) / (7 * DAY_SECONDS)) with WEEK_EPOCH_OFFSET = 4 * DAY_SECONDS); the only weekly key
    public int  LoginDayKey          { get; set; } = -1;  // DayKey on which LOGIN_DIAMONDS was last granted (reason LOGIN, issue #20)
}

public sealed class PartyState                             // 00 decision 109: a party of PARTY_SLOTS companions; the squire is the player's identity only (section 4) and never fights
{
    public int    Level        { get; set; } = 1;         // >= 1; the one Party Level, 03 PARTY_LVL_EXP curve
    public double Exp          { get; set; }              // PARTY_EXP toward next level; reset on level up
    public PartySlot[] Slots   { get; set; } = NewSlots(); // exactly PARTY_SLOTS (5) entries, never null elements; index = slot = action order in a tick (03)
    public static PartySlot[] NewSlots() { var a = new PartySlot[5]; for (int i = 0; i < a.Length; i++) a[i] = new(); return a; }   // literal 5 = PARTY_SLOTS
}

public sealed class PartySlot                              // progression belongs to the slot, not the companion: swapping Companion keeps Weapon, Skill and AwakenRank (00 decision 109)
{
    public string? Companion   { get; set; }              // companion id; null = empty slot (does nothing; its weapon, skill and rank stay but are idle)
    public string? Weapon      { get; set; }              // weapon id; null = unarmed (equipped pct 0). Unique across slots
    public string? Skill       { get; set; }              // skill id; null = no skill. Unique across slots; scales with this slot's attack (04)
    public int     AwakenRank  { get; set; }              // 0..AWAKEN_STAGES.Length; this slot's awakening (03)
}

public sealed class TrainingState
{
    // Index by Stat enum ordinal: ATK, HP, DEF, CRIT_RATE, CRIT_DMG, ATK_SPD, GOLD_GAIN, EXP_GAIN. Length always 8.
    public int[] Level { get; set; } = new int[8];        // each 0..TRAIN_CAP[stat] (03)
}

public sealed class Wallet
{
    public double Gold          { get; set; }             // GOLD; double because it passes 2^53 by stage ~700
    public long   Diamond       { get; set; }             // DIAMOND; long, server-reconciled in MVP
    public double FishCoin      { get; set; }             // FISH_COIN
    public long   RockCandy     { get; set; }             // ROCK_CANDY
    public double PartyExp      => 0;                     // PARTY_EXP is not a balance; it is Party.Exp. Present in CurrencyDelta only.
    public int[]  DungeonTicket { get; set; } = new int[3];      // index by Dungeon ordinal: FISH_COIN, ROCK_CANDY, EXP
    public int[]  SummonTicket  { get; set; } = new int[4];      // index by Banner ordinal: WEAPON, SKILL, COMPANION, RUNE (= TICKET_SUMMON_RUNE, unused until P1; issue #26). Remote mode: mirror of the server ledger (3.2)
    public long   GuildCoin     { get; set; }             // P1
    // Every change goes through Wallet.Apply(CurrencyDelta, CurrencyReason) (00 section 8.6), which rejects a debit below 0 (nothing changes, returns false; 06), rejects NaN/inf, and emits CurrencyChanged.
}

public sealed class CollectionState
{
    // key: content id (e.g. "wpn_017"). Absent key == not owned.
    public Dictionary<string, OwnedItem> Items { get; init; } = new();
    // "New" badges are not sim state: they live in Runtime's ClientPrefs (8.4), so viewing a screen never changes StateHash (issues #7, #47).
}

public sealed class OwnedItem
{
    public int Copies      { get; set; }   // 1..DUPE_LVL_CUM[10] (160); a copy past 160 converts to Rock Candy at pull time and is not stored (04 section 5.3). Level is derived from Copies at read time, never stored (04)
    public int LimitBreaks { get; set; }   // 0..LIMIT_BREAK_RANKS (5); 04 section 5.2
    public long FirstAcquiredServerTime { get; init; }
}

public sealed class LoadoutState                           // P1 only; weapons, skills and companions moved to Party.Slots (00 decision 109)
{
    public string?[] Runes     { get; set; } = new string?[6];        // RUNE_SLOTS, P1
}

public sealed class FishBoardState
{
    public int[]  Cells          { get; set; } = new int[20];     // FISH_BOARD_W * FISH_BOARD_H, row-major; 0 = empty, n >= 1 = fish level
    public int    TankCount      { get; set; }                   // fish in the Magic Tank, 0..TankCap; a new profile starts full (= TankCap via 05's query, TANK_CAP0 at Research 0; issue #246)
    public int    TankRefillTick { get; set; }                   // ticks accumulated toward the next tank fish; 0..refillTicks-1
    public int    TankReleaseLevel { get; set; } = 1;            // P1 upgrade level bought; content says what each level releases (05, 09 tank.release_level is the base)
    public HashSet<int> DiscoveredLevels { get; init; } = new(); // levels ever created; drives FISH_DISCOVERY_COINS
    public int    HighestLevel   { get; set; }                   // max of DiscoveredLevels, cached for the HUD
    public long   TotalMerges    { get; set; }
    public bool   AutoMergeEnabled { get; set; }                 // P1
}

public sealed class BannerState
{
    public int  Pity          { get; set; }      // pulls since last L+ before the next pull (n = Pity + 1, issue #24), 0..PITY_HARD-1. MVP: mirror of the server value, overwritten on every result.
    public long Exp           { get; set; }      // cumulative banner EXP ever earned; banner level is derived via BANNER_LVL_EXP, never stored (04)
    public long TotalPulls    { get; set; }
}

public enum CombatMode { Stage, Dungeon }
public enum BossPhase  { None, Pending, Walled }           // Pending: next spawn is the boss; Walled: boss failed, farming mobs (03 section 2.7)
public enum FightPhase { Spawning, Fighting }

public sealed class CombatState
{
    public CombatMode Mode          { get; set; } = CombatMode.Stage;
    public int    Stage             { get; set; } = 1;     // current farming stage, >= 1, <= STAGE_CAP
    public int    KillsInStage      { get; set; }          // 0..KILLS_PER_STAGE-1
    public BossPhase Boss           { get; set; }          // None/Pending/Walled, so a wall survives a reload (issue #7)
    public int    BossFailsOnStage  { get; set; }          // reset on advance (03)
    public int?   Selected          { get; set; }          // MVP SCR_STAGE_SELECT side-stage farm target <= HighestCleared; null = frontier. Stage, KillsInStage and Boss keep the frontier meanwhile (03; issue #35)
    public double[] Mp              { get; set; } = new double[5];  // one MP pool per party slot (PARTY_SLOTS), 0..MpPoolMax each (04); regen per tick from Research, equal for every pool; 0 in every pool on a new profile (issue #246)
    public int[]  SkillCooldowns    { get; set; } = new int[5];   // ticks left per party slot's skill (PARTY_SLOTS)
    public int[]  CompanionCooldowns { get; set; } = new int[5];  // ticks left per party slot's companion ability (PARTY_SLOTS; 03 section 2.4)
    public List<double> MissileQueue { get; init; } = new();      // queued fish-missile hit fractions, FIFO; persisted so a save mid-queue loses nothing (03, 05; issue #7)
    public DungeonRunState? DungeonRun { get; set; }       // non-null only while Mode == Dungeon
    public FightState? Fight        { get; set; }          // transient fight state; never saved (3.3)
    public bool ShouldSerializeFight() => false;           // Json.NET naming convention, no attribute: the serializer skips Fight
}

public sealed class FightState                              // in memory only; null after load or profile creation, and the next tick creates it as Spawning with SpawnTimer = SPAWN_DELAY_TICKS (03), so the player faces a fresh mob and a Pending boss respawns as the boss
{
    public FightPhase Phase         { get; set; }
    public int    SpawnTimer        { get; set; }          // ticks until Spawn()
    public double EnemyHp           { get; set; }
    public double EnemyMaxHp        { get; set; }
    public bool   EnemyIsBoss       { get; set; }
    public int    BossTicksLeft     { get; set; }          // 0..BOSS_TIME_TICKS (03 boss timer)
    public int[]  AttackProgress    { get; set; } = new int[5];   // per party slot (PARTY_SLOTS), fixed point, ATTACK_PROGRESS_UNIT, all at the party-wide speed (03 section 2.1)
    public List<DotInstance> ActiveDots { get; init; } = new();   // max 6 (03 section 2.3)
    public double[] SlotAtkBuffPct  { get; set; } = new double[5]; // per party slot: strongest active BUFF skill of that slot, percentage points; buffs only that slot's auto-attack (03)
    public int[]  SlotAtkBuffTicksLeft { get; set; } = new int[5];
    public double AtkBuffPct        { get; set; }          // party-wide: strongest active BUFF_ATK companion, percentage points; buffs every slot's auto-attack (04 section 8.1)
    public int    AtkBuffTicksLeft  { get; set; }
    public double GoldBuffPct       { get; set; }          // strongest active BUFF_GOLD companion, percentage points (04 section 8.1; issue #156)
    public int    GoldBuffTicksLeft { get; set; }
    public int    PendingTaps       { get; set; }          // taps accepted since the last tick
    public int    TapsThisSecond    { get; set; }          // TAP_ZAP_MAX_PER_SEC guard
    public long   TapWindowTick     { get; set; }          // Time.Tick at which the current tap window opened
}

public sealed record DotInstance(int TicksLeft, double DamagePerTick);

public sealed class DungeonRunState                        // a ticked run in the main loop (06 section 6.2; issue #42)
{
    public Dungeon Kind        { get; init; }
    public int     Floor       { get; init; }
    public ulong   Seed        { get; init; }              // server-issued (MVP) or from the `dungeon` stream (M0); seeds the temporary `dungeon_run` stream (3.4)
    public long    StreamCounter { get; set; }             // draws consumed from `dungeon_run`; the stream is Derive(Seed, StreamCounter) and is discarded with the run
    public string  GrantId     { get; init; } = "";        // server grant id (MVP) for plausibility correlation; "" in M0
    public int     EnemyIndex  { get; set; }               // position in the floor's enemy sequence; always 0 in MVP (one boss per floor)
    public int     TicksLeft   { get; set; }
    public FightState Fight    { get; set; }               // transient (3.3): the floor's boss fight; never saved, a reload abandons the run (P16). Combat.Fight stays the frozen frontier fight (issue #172)
}

public sealed class ProgressState
{
    public int  HighestCleared     { get; set; }           // highest stage completed at the frontier (KILLS_PER_STAGE, or its boss); any stage, not only bosses (issue #35)
    public int  HighestFirstClearBoss { get; set; }        // highest boss stage that paid first-clear diamonds (03); first-clear set = multiples of BOSS_EVERY <= this
    public int  StageCapReached    { get; set; }           // 0 or the tick at which STAGE_CAP was hit
}

public sealed class QuestState                                          // reset off Time.DayKey / Time.WeekKey (06 EnsureDay); no keys of its own
{
    public Dictionary<string, QuestProgress> Daily       { get; init; } = new();   // key: quest id from quests_daily (09), bonus rows (d_bonus_5, d_bonus_8) included; reset on rollover
    public Dictionary<string, QuestProgress> Weekly      { get; init; } = new();   // key: quest id from quests_weekly (09), bonus rows (w_bonus_all_skill, w_bonus_all_companion) included; reset on weekly rollover
    public Dictionary<string, QuestProgress> Achievement { get; init; } = new();   // key: rung id (`ach_<ladder>_<rung>`, 09), one entry per rung; never reset
}

public enum QuestStatus { IN_PROGRESS, COMPLETED, CLAIMED }              // 00 decision 17

public sealed class QuestProgress
{
    public long        Progress { get; set; }   // counter; keeps counting past target (06)
    public QuestStatus State    { get; set; }   // IN_PROGRESS -> COMPLETED when Progress >= target -> CLAIMED by ClaimQuest
}

public sealed class DungeonState                             // daily counters reset off Time.DayKey (06)
{
    public int[] BestFloor     { get; set; } = new int[3];   // per Dungeon ordinal; 0 = never cleared
    public int[] EntriesToday  { get; set; } = new int[3];   // cleared runs today, informational for the HUD; tickets are the real limiter; reset at rollover
    public int[] SweepsToday   { get; set; } = new int[3];   // sweeps today; reset at rollover
    public List<DungeonRunRecord> RecentRuns { get; init; } = new();   // newest last, at most DUNGEON_RECENT_RUNS_MAX; never reset by rollover (issue #52)
}

public sealed record DungeonRunRecord(string GrantId, Dungeon Kind, int Floor, bool Cleared);

public sealed class ShopState                                 // 06; both reset by the daily rollover
{
    public bool DailyGiftClaimed { get; set; }
    public Dictionary<string, int> ExchangeCounts { get; init; } = new();   // key: exchange offer id (06 section 8.4), value: purchases today
}

public sealed class ResearchState
{
    public Dictionary<string, int> Level { get; init; } = new();   // key: research id (09), value: level >= 1; absent = 0
}

public sealed class InboxState
{
    public HashSet<string> ClaimedIds { get; init; } = new();   // server inbox item ids claimed by this save; the server is authoritative, this prevents a double-apply on replay
    public long LastFetchedServerTime { get; set; }
}

public sealed class OfflineState
{
    public CatchupResult? Pending { get; set; }   // non-null between ComputeOfflineCatchup and ClaimOfflineReward (01 section 8.7); a second gap while pending sums into it (amounts and seconds added, latest Stage/KillTimeSec kept; issue #21)
    public long LifetimeOfflineSecCredited { get; set; }
}

public sealed record CatchupResult(long ElapsedSec, long CreditedSec, bool Capped, double Gold, double Exp, int TankFill, int Stage, double KillTimeSec, long ComputedServerTime);
// TankFill: fish from 05's CatchUpTank(CreditedSec * TICK_HZ) at compute; applied at claim as TankCount = min(TankCap, TankCount + TankFill), refill progress per 05 (issue #21).

public sealed class PendingState
{
    public PendingSummon?        Summon   { get; set; }                  // one in flight (01 section 8.5)
    public PendingDungeonSeed?   Dungeon  { get; set; }
    public List<PendingReceipt>  Receipts { get; init; } = new();        // zero or more (01 section 8.9)
}

public sealed record PendingSummon(string RequestId, Banner Banner, int Count, string Payment, long RequestedServerTime);
public sealed record PendingDungeonSeed(string RequestId, Dungeon Kind, int Floor, long RequestedServerTime);
public sealed record PendingReceipt(string RequestId, string Platform, string Sku, string TransactionId, string Payload, long RequestedServerTime);

public sealed class TutorialState                          // issue #46
{
    public int  Step      { get; set; }        // 0..TUTORIAL_DONE, where TUTORIAL_DONE = row count of tutorial_steps (09; 12 in MVP); advanced only by SetTutorialStep (01, 07)
    public bool Skipped   { get; set; }
    public bool TutorialDiamondsGranted { get; set; }   // set when 09's tut_welcome grant (TUTORIAL_DIAMONDS with reason TUTORIAL, the five starter weapons TUTORIAL_WEAPON_IDS and the five starter companions TUTORIAL_COMPANION_IDS; 3.5) is applied at profile creation; guards it to exactly once
}

public sealed class RngStreamState
{
    public string Seed    { get; init; } = "";   // exactly 16 lowercase hex chars (a ulong); string so JSON tooling never rounds it
    public long   Counter { get; set; }          // draws consumed so far, >= 0
}

public sealed class LifetimeStats
{
    public long Kills, BossKills, BossFails, PartyHits, Taps, Pulls, Merges, FishReleased, FishDiscards, DungeonClears, TrainingLevelsBought;
    public int  FirstWallStage;                   // stage of the first BossFail ever; 0 = never walled (03)
    public long ActiveSeconds;                    // ticked time; analytics only
    public double GoldEarned, GoldSpent, FishCoinEarned;
    public long DiamondsEarned, DiamondsSpent, DiamondsPurchased;
    // Client-side ledger feed (issue #49): cumulative amounts the sim itself credited or debited, per reason, never reset.
    // Only reasons applied locally (LOGIN, TUTORIAL, FIRST_CLEAR, QUEST, ACHIEVEMENT, DAILY_GIFT, SHOP_EXCHANGE, ...);
    // never IAP, MAIL, ADMIN, REFUND (inbox grants) or SUMMON (server debit). Reported on every upload (9.2).
    public Dictionary<CurrencyReason, long> ClientDiamondsEarned { get; init; } = new();   // positive amounts
    public Dictionary<CurrencyReason, long> ClientDiamondsSpent  { get; init; } = new();   // positive amounts, e.g. SHOP_EXCHANGE
    public Dictionary<Banner, Dictionary<CurrencyReason, long>> ClientTicketsEarned { get; init; } = new();   // summon tickets by ticket kind (Banner ordinal = SummonTicket index)
}
```

### 3.2 Field notes

- **`Training.Level` is an array indexed by `Stat` ordinal**, not a dictionary, so the hot path (DPS evaluation every tick) has no hashing. The `Stat` enum order is fixed in 00 section 5.1 and a reorder is a schema change.
- **`Wallet` uses `long` for `DIAMOND` and `ROCK_CANDY`** (server-reconciled integers) and `double` for `GOLD` and `FISH_COIN` (exponential soft currencies). `PARTY_EXP` is `Party.Exp`, not a wallet balance; `CurrencyDelta` still names it so `CurrencyChanged` events and analytics are uniform. A debit larger than the balance is rejected, not clamped (06; issue #7).
- **`Wallet.SummonTicket` in Remote mode is a mirror** of the server ticket ledger (00 decision 15), overwritten from each `SummonResultPayload` and inbox grant, like `Banners[b].Pity`. In Local mode (M0, bots) it is the balance.
- **Client-earned diamonds and tickets reach the server ledgers on upload** (issue #49). `Stats.ClientDiamondsEarned`, `ClientDiamondsSpent` and `ClientTicketsEarned` are cumulative per reason; each `PUT /save` reports them and the server credits the positive delta over what it already credited (rows with `ref_type = 'save_reconcile'` in `diamond_ledger` / `ticket_ledger`, section 12), clamped to the per-day allowance (08 section 3.1). A summon can spend only what has been credited, so the client uploads before a pull when these counters changed since the last acknowledged upload (9.2; 01).
- **Item and banner levels are derived, never stored** (issue #7). `OwnedItem` level comes from `Copies` via `DUPE_LVL_CUM` and banner level from `BannerState.Exp` via `BANNER_LVL_EXP` at read time, so retuning either table reflows every save without a migration (04).
- **The party is five slots, and progression belongs to the slot** (00 decision 109; implementation tracked in issue #309). `Party.Slots[i]` holds slot `i`'s companion, weapon, skill and `AwakenRank`; every slot is open from the start at every tier (no slot is gated by Awakening or anything else). An owned weapon, skill or companion is in at most one slot. Swapping the companion in a slot keeps that slot's weapon, skill and rank. Every per-slot array (`Party.Slots`, `Combat.Mp`, `Combat.SkillCooldowns`, `Combat.CompanionCooldowns`, `Fight.AttackProgress`, `Fight.SlotAtkBuffPct`, `Fight.SlotAtkBuffTicksLeft`) is exactly `PARTY_SLOTS` long and indexed by the same slot. `Party.Level` and `Party.Exp` are party-wide, as are `Training`, `Fight.AtkBuffPct` (`BUFF_ATK` companions) and `Fight.GoldBuffPct` (`BUFF_GOLD`).
- **`Stats.PartyHits`** counts slot hits: one per slot auto-attack that lands in a stage fight (03), so a tick can add up to `PARTY_SLOTS`. It feeds the `crit` invariant (3.4).
- **`Fish.Cells` is a flat `int[]`** of levels; the board is 20 cells and a cell is fully described by its level. Row-major: `index = y * FISH_BOARD_W + x`.
- **`Banners[b].Pity` in MVP is a mirror.** The server holds the authoritative pity per banner (08 section 4). The client value is overwritten by every `SummonResultPayload` and exists for display and for the M0/bot path.
- **`Combat.DungeonRun` is persisted** so a kill mid-run is detected at load and abandoned (P16; 01 section 8.8 step 6), not so the run can resume. Abandoning costs nothing: the ticket is debited only by `DungeonCleared` (00 decision 16; issue #42). While `Mode == Dungeon` the stage fields (`Stage`, `KillsInStage`, `Boss`, `Selected`) are untouched, so the frontier resumes unchanged when the run ends.
- **`Dungeons.RecentRuns`** is the correlation record for 08 (issue #52): every ended run (cleared or failed) appends `{GrantId, Kind, Floor, Cleared}`, keeping only the newest `DUNGEON_RECENT_RUNS_MAX`; the server matches `GrantId` to issued seeds. Sweeps use no seed and are not recorded.
- **`Combat` persists the stage loop, not the fight** (issue #7). `Boss`, `BossFailsOnStage`, `Selected`, the five `Mp` pools, the per-slot skill and companion cooldowns and `MissileQueue` are saved; `Combat.Fight` (enemy HP, spawn timer, boss timer, per-slot attack progress, DOTs, slot and party buffs, taps) is in memory only, so `DeepClone` mid-boss continues identically (03 AC15) while a reload starts a fresh mob.
- **Quest maps are keyed by id**, not positional (sibling open decision 2 resolved here): a content reorder is harmless; a removed quest id is dropped by the validator. Bonus rewards and achievement rungs are ordinary quest rows keyed by their own ids (06).
- **Stage select is side-stage farming** (issue #35). `SelectStage(s)` sets `Combat.Selected`; kills there pay that stage's rewards and never advance, a selected stage never spawns a boss, and `Combat.Stage`, `KillsInStage`, `Boss` (including `Walled`) and `BossFailsOnStage` keep the frontier until `SelectStage(null)`. Bosses are only ever cleared at the frontier, in order, so the first-clear set is always `{BOSS_EVERY, 2*BOSS_EVERY, ..., HighestFirstClearBoss}` and no out-of-order set is stored.
- **Timestamps** are `long` unix seconds UTC from `IClock`, named `*ServerTime`.
- **`PlayerId == Guid.Empty`** marks a local guest that has never reached the server (01 open question 7). The first successful guest auth sets it and uploads; `RngRoot` and every seed stay as they were (issue #9).

### 3.3 Deliberately not saved

- `Combat.Fight`: current enemy HP, spawn timer, boss timer, per-slot attack progress, DOTs, slot and party buffs and pending tap zaps. On load the player faces a fresh mob at `Combat.Stage`; a boss attempt in progress restarts (`Boss == Pending` respawns the boss with a full timer), and a `Walled` stage stays walled. Queued fish missiles are saved (`Combat.MissileQueue`).
- `Combat.DungeonRun.Fight`: the dungeon boss's fight state. A run in a loaded save is abandoned anyway (P16), so nothing needs it.
- The `Event` buffer and the `Snapshot`.
- Derived values: DPS, kill time, board passive DPS%, weapon passive total, next training cost, `FISH_BOARD_DPS_PCT`, item and banner levels, and any live missile-DPS rolling average (view-model only, issue #15). All recomputed from `ContentDb`, so a retune reflows every save without a migration.
- Client-only flags: collection NEW badges, coach marks, sort/filter/step prefs and settings toggles live in Runtime's `ClientPrefs` (8.4), not in `GameState` or `Profile` (issue #47).
- Content tables; the save stores ids and counters plus `ContentVersion` and `ContentHash`.
- Auth tokens, device key, config cache: Runtime files outside the save (section 8.4).

### 3.4 RNG streams

| Name | Consumer | Draws per event | Tier | Invariant checked by validator |
|---|---|---|---|---|
| `combat` | reserved (03) | 0 in MVP | M0 | `Counter == 0` in MVP |
| `crit` | crit roll per stage-fight slot hit (one slot's auto-attack that lands) | 1 per stage-fight slot hit; a slot whose attack does not happen because an earlier slot killed the enemy that tick draws nothing (dungeon hits use `dungeon_run` and are not in `Stats.PartyHits`) | M0 | `Counter == Stats.PartyHits` |
| `fish_spawn` | `ReleaseFish` cell pick | 1 per release | M0 | `Counter == Stats.FishReleased` |
| `dungeon` | M0 dungeon seed derivation (`IDungeonSeedService` local implementation); MVP unused for new runs | 1 per M0 run | M0 | none |
| `dungeon_run` | dungeon fight draws (slot crits) while `Combat.Mode == Dungeon` (03, 06; issue #42) | per 03 | MVP | Temporary: not in `Rng`; seed `DungeonRun.Seed`, counter `DungeonRun.StreamCounter`; discarded when the run ends |
| `loot` | boss Rock Candy drop | 1 per boss kill | M0 | `Counter == Stats.BossKills` (P1 drop tables add their own invariant) |
| `summon_weapon`, `summon_skill`, `summon_companion` | `LocalSummonService` (M0 and bots) draws on a clone; the `ApplySummonResult` payload carries the counter after the pull and `Apply` sets it (issue #6) | 3 per pull (L+ check, tier, item index; 04 section 3.2) | M0 | `Counter == 3 * Banners[b].TotalPulls` in Local mode; ignored in Remote mode |

Seed derivation at profile creation: `seed(name) = first 8 bytes of SHA-256(RngRoot.ToByteArray() ++ UTF8(name))` little-endian, computed in `HeroCat.Runtime.ProfileFactory` (the sim has no hashing). `RngRoot` is `PlayerId` when the profile is created with a known player, else a fresh random `Guid`; it never changes, so seeds are independent of account linking and of which device writes the save (issue #9). Generator: SplitMix64 stateless derive, `Derive(seed, k)` (03 owns the code; same as the sibling).

Summon streams exist in every save, in every mode (00 decision 14), so M0, bots and the server-side re-simulator (which runs in Local mode against a save) share one schema. In Remote mode their counters stay at whatever M0 left (or 0) and the validator does not check them.

### 3.5 New profile

`HeroCat.Runtime.ProfileFactory.Create(playerId, now, db)` returns the defaults of 3.1 plus the following, and every new profile passes 6.4 with zero repairs (00 decision 109; issue #46):

- `Tutorial.TutorialDiamondsGranted = true` with 09's `tut_welcome` grant applied once: `TUTORIAL_DIAMONDS` (reason `TUTORIAL`), one copy of each of the five distinct Common weapons `TUTORIAL_WEAPON_IDS` (00 decision 111) and of the five distinct Common companions `TUTORIAL_COMPANION_IDS` (09).
- `Party.Level = 1`, `Party.Exp = 0`. `Party.Slots[i].Companion = TUTORIAL_COMPANION_IDS[i]` for `i = 0..4`; `Party.Slots[i].Weapon = TUTORIAL_WEAPON_IDS[i]` for `i = 0..4` (00 decision 111); no skills; every `AwakenRank = 0`.
- `Combat.Mp[i] = 0` in every pool (issue #246); every cooldown 0.
- `Fish.TankCount` = TankCap (full tank, `TANK_CAP0` at Research 0; issue #246); `Fish.TankRefillTick = 0`.
- The eight streams of 3.4 seeded from `RngRoot`, every counter 0.

## 4. `Profile` (account level, not in sim)

```csharp
namespace HeroCat.Runtime.Account;

public sealed record Profile(
    Guid     PlayerId,
    string   DisplayName,              // 2..16 chars, server-filtered; default "Squire#" + last 4 of PlayerId (00 decisions 104, 109)
    string   AvatarId,                 // content id; default "avatar_default" (a squire portrait)
    long     CreatedServerTime,
    LinkedPlatform[] Links,            // zero or more
    bool     IsGuest,                  // Links.Length == 0
    string   Region,                   // ISO 3166-1 alpha-2 from the store account or IP at creation; drives loot-box disclosure rules (08)
    int      AgeGate,                  // 0 unknown, 1 under-13 (COPPA mode), 2 adult; set once at first launch (08 compliance)
    long     LastLoginServerTime,
    string   SupportCode);             // 08 section 2.1: base32(hmac(playerId, serverKey))[:12], computed by the server, never stored (issue #195)

public sealed record LinkedPlatform(string Provider /* "apple" | "google_play" */, string ProviderUserIdHash, long LinkedServerTime);
```

Stored server-side in `accounts` (section 12) and cached in `persistentDataPath/profile.json`. The sim never reads it; `SCR_PROFILE` and `SCR_SETTINGS` bind to it through `AccountViewModel`. Display name and avatar are not in `GameState` so a cloud-save conflict cannot rename the player. The squire is the player's identity only, its avatar portrait and default name; it never fights and has no `GameState` of its own (the party of 3.1 does the fighting; 00 decision 109).

## 5. Save schema v1

Schema v1 is redefined from scratch for the party model (00 decision 109): nothing has shipped and `V1Frozen` is false (00 decision 33, 7.4), so `SAVE_SCHEMA_VERSION` stays 1, there is no migration step, and a pre-party dev save (root `Hero`, no `Party`) is rejected by R3 and quarantined, and the dev gets a new profile.

### 5.1 File layout

```
HEROCAT-SAVE v=1 fmt=json crc32=7f3a19c2 len=38127 hmac=<64 hex>\n
{ ...exactly len bytes of UTF-8 JSON... }
```

- `v` is the container version (not the schema version; `SchemaVersion` in the body is authoritative).
- `crc32` (IEEE) of the body bytes, for corruption detection.
- `hmac` is HMAC-SHA256 of the body with the device key (section 10). Absent in Local mode builds that have no key (`hmac=-`).
- The loader reads the header, exactly `len` bytes, and rejects on: malformed header, byte count mismatch, CRC mismatch, JSON parse failure. HMAC mismatch is **not** a local reject (a restored OS backup from another device legitimately fails it); it is recorded as `hmacValid = false` and the server decides (08).

### 5.2 Full example

A real day-9 save, abbreviated only in repeated collection entries. 41 KB formatted, 23 KB minified.

```json
{
  "SchemaVersion": 1,
  "ContentVersion": "2026.11.03",
  "ContentHash": "5d41402abc4b2a76b9719d911017c592ae7f1e5c3b8a4d6e9f0a1b2c3d4e5f60",
  "PlayerId": "6b1f2c40-9d1e-4a7b-8f3e-2c1a9b7d5e01",
  "DeviceId": "0f9e8d7c-6b5a-4433-9211-aabbccddeeff",
  "RngRoot": "6b1f2c40-9d1e-4a7b-8f3e-2c1a9b7d5e01",
  "SaveSeq": 1874,
  "Time": {
    "Tick": 7842110, "LastSavedServerTime": 1793667200, "LastTickServerTime": 1793667200,
    "AnchorServerTime": 1793667140, "AnchorTick": 7841510,
    "CreatedServerTime": 1792890000, "DayKey": 20760, "WeekKey": 2965, "LoginDayKey": 20760
  },
  "Party": {
    "Level": 143, "Exp": 48211.5,
    "Slots": [
      { "Companion": "cmp_012", "Weapon": "wpn_028", "Skill": "skl_001", "AwakenRank": 2 },
      { "Companion": "cmp_002", "Weapon": "wpn_003", "Skill": "skl_014", "AwakenRank": 1 },
      { "Companion": "cmp_003", "Weapon": "wpn_017", "Skill": "skl_022", "AwakenRank": 1 },
      { "Companion": "cmp_004", "Weapon": null,      "Skill": null,      "AwakenRank": 0 },
      { "Companion": "cmp_005", "Weapon": null,      "Skill": null,      "AwakenRank": 0 }
    ]
  },
  "Training": { "Level": [312, 140, 95, 61, 58, 44, 120, 77] },
  "Wallet": {
    "Gold": 1.8423371e12, "Diamond": 1240, "FishCoin": 38400.0, "RockCandy": 412,
    "DungeonTicket": [1, 0, 2], "SummonTicket": [3, 0, 1, 0], "GuildCoin": 0
  },
  "Weapons": {
    "Items": {
      "wpn_003": { "Copies": 41, "LimitBreaks": 1, "FirstAcquiredServerTime": 1792890400 },
      "wpn_017": { "Copies": 2,  "LimitBreaks": 0, "FirstAcquiredServerTime": 1793001200 },
      "wpn_028": { "Copies": 1,  "LimitBreaks": 0, "FirstAcquiredServerTime": 1793402210 }
    }
  },
  "Skills": { "Items": { "skl_001": { "Copies": 18, "LimitBreaks": 0, "FirstAcquiredServerTime": 1792890500 } } },
  "Companions": {
    "Items": {
      "cmp_002": { "Copies": 3, "LimitBreaks": 0, "FirstAcquiredServerTime": 1792890000 },
      "cmp_012": { "Copies": 4, "LimitBreaks": 0, "FirstAcquiredServerTime": 1793100000 }
    }
  },
  "Runes": { "Items": {} },
  "Loadout": { "Runes": [null, null, null, null, null, null] },
  "Fish": {
    "Cells": [3, 0, 1, 1, 0,  5, 2, 0, 0, 1,  0, 0, 7, 0, 0,  1, 0, 0, 2, 0],
    "TankCount": 4, "TankRefillTick": 23, "TankReleaseLevel": 1,
    "DiscoveredLevels": [1, 2, 3, 4, 5, 6, 7, 8], "HighestLevel": 8, "TotalMerges": 2214, "AutoMergeEnabled": false
  },
  "Banners": {
    "WEAPON":    { "Pity": 23, "Exp": 412, "TotalPulls": 412 },
    "SKILL":     { "Pity": 61, "Exp": 180, "TotalPulls": 180 },
    "COMPANION": { "Pity": 4,  "Exp": 40,  "TotalPulls": 40 }
  },
  "Combat": {
    "Mode": "Stage", "Stage": 412, "KillsInStage": 6, "Boss": "None", "BossFailsOnStage": 0, "Selected": null,
    "Mp": [84.0, 40.5, 12.0, 60.0, 0.0], "SkillCooldowns": [0, 31, 0, 0, 0], "CompanionCooldowns": [12, 0, 0, 3, 0], "MissileQueue": [],
    "DungeonRun": null
  },
  "Progress": { "HighestCleared": 411, "HighestFirstClearBoss": 410, "StageCapReached": 0 },
  "Quests": {
    "Daily": {
      "dq_kill_500":   { "Progress": 731, "State": "CLAIMED" },
      "dq_clear_10":   { "Progress": 7,   "State": "IN_PROGRESS" },
      "dq_summon_wpn": { "Progress": 10,  "State": "CLAIMED" },
      "dq_merge_30":   { "Progress": 42,  "State": "COMPLETED" },
      "d_bonus_5":     { "Progress": 2,   "State": "IN_PROGRESS" }
    },
    "Weekly": { "wq_kill_5000": { "Progress": 3120, "State": "IN_PROGRESS" } },
    "Achievement": {
      "ach_stage_05": { "Progress": 412,  "State": "CLAIMED" },
      "ach_stage_06": { "Progress": 412,  "State": "IN_PROGRESS" },
      "ach_merges_03": { "Progress": 2214, "State": "COMPLETED" }
    }
  },
  "Dungeons": { "BestFloor": [9, 6, 8], "EntriesToday": [1, 1, 0], "SweepsToday": [0, 1, 0],
                "RecentRuns": [ { "GrantId": "5b1e2c7a-9d40-4f3e-8a61-2c0d7e9f4b13", "Kind": "FISH_COIN", "Floor": 9, "Cleared": true },
                                { "GrantId": "dgr_01HZXA", "Kind": "ROCK_CANDY", "Floor": 7, "Cleared": false } ] },
  "Shop": { "DailyGiftClaimed": true, "ExchangeCounts": { "ex_ticket_fish": 1 } },
  "Research": { "Level": { "res_mp_pool": 3, "res_mp_regen": 2, "res_tank_cap": 4, "res_tank_refill": 2, "res_fish_cap": 1 } },
  "Inbox": { "ClaimedIds": ["ibx_01HZX3K9Q8", "ibx_01HZY0A2M1"], "LastFetchedServerTime": 1793667100 },
  "Offline": { "Pending": null, "LifetimeOfflineSecCredited": 391200 },
  "Pending": { "Summon": null, "Dungeon": null, "Receipts": [] },
  "Tutorial": { "Step": 12, "Skipped": false, "TutorialDiamondsGranted": true },
  "Rng": {
    "combat":           { "Seed": "9f86d081884c7d65", "Counter": 0 },
    "crit":             { "Seed": "3a1c0b8e55f2d7aa", "Counter": 1203944 },
    "fish_spawn":       { "Seed": "0c7e2b9d4411fa63", "Counter": 4460 },
    "dungeon":          { "Seed": "d41d8cd98f00b204", "Counter": 0 },
    "loot":             { "Seed": "e3b0c44298fc1c14", "Counter": 41 },
    "summon_weapon":    { "Seed": "7b2e4f1a9c3d5e60", "Counter": 0 },
    "summon_skill":     { "Seed": "1d9c8b7a6f5e4d30", "Counter": 0 },
    "summon_companion": { "Seed": "a5b4c3d2e1f00918", "Counter": 0 }
  },
  "Stats": {
    "Kills": 184502, "BossKills": 41, "BossFails": 9, "PartyHits": 1203944, "Taps": 8120, "Pulls": 632, "Merges": 2214,
    "FishReleased": 4460, "FishDiscards": 12, "FirstWallStage": 120, "DungeonClears": 23, "TrainingLevelsBought": 907, "ActiveSeconds": 61420,
    "GoldEarned": 9.12e12, "GoldSpent": 7.28e12, "FishCoinEarned": 61200.0,
    "DiamondsEarned": 5020, "DiamondsSpent": 4980, "DiamondsPurchased": 1200,
    "ClientDiamondsEarned": { "ACHIEVEMENT": 300, "FIRST_CLEAR": 820, "LOGIN": 1150, "QUEST": 1250, "TUTORIAL": 1500 },
    "ClientDiamondsSpent": { "SHOP_EXCHANGE": 200 },
    "ClientTicketsEarned": { "WEAPON": { "DAILY_GIFT": 23 }, "SKILL": { "QUEST": 3 }, "COMPANION": { "QUEST": 3 } }
  },
  "DebugTainted": false
}
```

Size grows with collection count (max 30 + 40 + 50 + runes) and quest count; it is bounded at roughly 60 KB with every item owned. `SAVE_BLOB_MAX_BYTES` leaves 4x headroom.

## 6. Serializer

### 6.1 Settings

Newtonsoft.Json 13.x (`com.unity.nuget.newtonsoft-json` in Unity; the NuGet package elsewhere). One `JsonSerializerSettings` instance in `SaveSerializer`:

| Setting | Value | Why |
|---|---|---|
| Property naming | As declared (PascalCase) | Matches the C# tree; no contract resolver reflection surprises under IL2CPP. |
| `NullValueHandling` | `Include` | Empty party-slot fields and rune slots are explicit `null`s; `Pending.Summon: null` is meaningful. |
| `DefaultValueHandling` | `Include` | A `0` is written so a fixture is complete and diffable. |
| `MissingMemberHandling` | `Ignore` | Unknown fields (newer dev build, removed field) are skipped. See 6.3. |
| `FloatParseHandling` | `Double` | Never `Decimal`. |
| `FloatFormatHandling` | `String` for NaN/Infinity on write (then rejected by the validator); default otherwise | Writing `NaN` as a bare token would make the file unparseable by other tools. |
| Double formatting | Round-trip exact (`R` formatting; Json.NET default) | A `Gold` value survives bit-exactly. The text is whatever Json.NET's round-trip form writes (`1842337100000.0`, not necessarily the shortest form `1.8423371e12`); only the parsed value is the contract (issue #246). |
| Enums | `StringEnumConverter` | `"Mode": "Stage"`, `"WEAPON"` keys. Integers accepted on read. A saved member is never renamed in place; a rename is a migration. |
| `Guid` | Default 36-char lowercase string | |
| `Dictionary<Banner, BannerState>` | Keys as enum names via the converter | |
| Every dictionary (`Banners`, `Rng`, `Items`, quest maps, `Research.Level`, `Shop.ExchangeCounts`, `Stats.ClientDiamondsEarned`, `ClientDiamondsSpent`, `ClientTicketsEarned`) | Written sorted by the key as written (ordinal string order: enum names, ids); read in any order | Byte stability (AC 3): two equal states built in different insertion orders encode to the same bytes; issue #49. The 5.2 example shows `Banners` in enum order for readability; the serializer writes `COMPANION`, `SKILL`, `WEAPON`. |
| `HashSet<int>` / `HashSet<string>` | JSON arrays, written sorted | Sorted so two saves with equal state are byte-equal (determinism test, 10). |
| `Formatting` | `None` release, `Indented` development | |
| Encoding | UTF-8 without BOM | |
| `TypeNameHandling` | `None` | Never emit `$type`. |
| `ReferenceLoopHandling` | `Error` | The tree has no cycles; a cycle is a bug. |
| `Combat.Fight`, `Combat.DungeonRun.Fight` | Skipped (`ShouldSerializeFight() => false`, Json.NET's convention; no attribute) | Transient fight state (3.3); absent on read, so it loads as `null`. |

IL2CPP: every record in section 3 is referenced from a `[Preserve]`d `SaveTypes.Touch()` method and `link.xml` preserves `HeroCat.Sim.State` so stripping cannot remove a setter.

### 6.2 API

```csharp
public static class SaveSerializer
{
    public static byte[] Encode(GameState s, byte[]? deviceKey);                  // header + body; computes crc32 and hmac
    public static SaveFile Decode(byte[] file, byte[]? deviceKey);                // throws SaveCorruptException on header/len/crc/parse failure
    public static GameState Materialize(JObject bodyAtCurrentVersion);            // Json.NET deserialize with the settings above
}

public sealed record SaveFile(int SchemaVersion, JObject Body, bool HmacPresent, bool HmacValid, string Sha256);
```

### 6.3 Unknown-field tolerance

- Unknown properties on any object are ignored on read and therefore **dropped on the next write**. This is correct for a downgrade-then-upgrade within a dev cycle and for a removed field; it is wrong for a field a newer schema version added, which is why a newer `SchemaVersion` is refused outright (7.1) rather than loaded leniently.
- Unknown keys in `Banners` (a P1 `RUNE` banner, an event banner) and `Rng` (a new stream) are preserved: both are dictionaries, so Json.NET keeps them. The validator drops unknown `Rng` names with a log (they cost nothing to keep but a stale stream could be mistaken for a live one) and keeps unknown `Banners`.
- Unknown item ids in `Weapons/Skills/Companions/Runes.Items` and unknown quest or research ids are a **content** question, handled by the validator (rules P10, P12, P13), not by the serializer.

### 6.4 Validation and repair

Runs on the materialized `GameState` against the loaded `ContentDb` and `now`. This is the only content reconcile on load (issue #56): the content-dependent rules (P4, P8-P14, P21) are what 09's former `ContentReconcile` did, and nothing is refunded in MVP; 09 section 6.3 points here. Reject rules throw `SaveInvalidException` and the loader tries the next file. Repair rules fix the field, record the rule id, and emit `save_repaired{rule}`. More than `SAVE_REPAIR_MAX` distinct rule hits is a reject.

Reject:

| Id | Condition |
|---|---|
| R1 | Header, length or CRC mismatch, or JSON parse failure (from `Decode`). |
| R2 | `SchemaVersion` missing or `< 1`. |
| R3 | Any root section `null` or absent after migration (checked on the body before `Materialize`, so a missing section is not silently defaulted); a `Party.Slots` element `null` is P7, not R3. A pre-party dev save (root `Hero`, no `Party`; 5 and 00 decision 109) fails here. |
| R4 | `Rng` lacks any of the eight streams in 3.4 (`combat`, `crit`, `fish_spawn`, `dungeon`, `loot`, `summon_weapon`, `summon_skill`, `summon_companion`; 00 decision 14, every mode), or any `Seed` is not 16 hex chars, or any `Counter < 0`; or `RngRoot == Guid.Empty`. No stream is exempt: a migration step that adds a stream writes its seed (7.2 rule 6; issue #9). |
| R5 | `Training.Level.Length != 8`, `Fish.Cells.Length != FISH_BOARD_W * FISH_BOARD_H`, `Wallet.DungeonTicket.Length != 3`, `Wallet.SummonTicket.Length != 4`. These are structural, not content-driven, so a mismatch means a corrupted or foreign file. |

Repair:

| Id | Condition | Repair |
|---|---|---|
| P1 | `Combat.Stage < 1` or `> STAGE_CAP`; `KillsInStage` outside `[0, KILLS_PER_STAGE-1]`; `Boss != None` on a stage that is not a multiple of `BOSS_EVERY`; `BossFailsOnStage < 0`; `Selected` outside `[1, HighestCleared]`; any cooldown negative; `MissileQueue` entry not positive or non-finite (05: every queued missile is a positive hit fraction); any `Combat.Mp[i]` negative or non-finite | Clamp; `Boss = None`; `Selected = null`; drop the bad missile entries; that pool's `Mp[i] = 0`. |
| P2 | Any wallet balance negative, NaN or infinite; `Party.Exp` likewise | Set to 0. |
| P3 | `Party.Level < 1`; any `Party.Slots[i].AwakenRank` outside `[0, AWAKEN_STAGES.Length]` | Clamp, per slot. |
| P4 | `Training.Level[i]` outside `[0, TRAIN_CAP[i]]` | Clamp. |
| P5 | `Progress.HighestCleared` outside `[Combat.Stage - 1, Combat.Stage]` | Set to `Stage - 1` (`Stage` is the frontier, issue #35; it equals `Stage` only once `STAGE_CAP` is cleared). |
| P6 | `HighestFirstClearBoss` not a multiple of `BOSS_EVERY`, negative, or `> HighestCleared` | Floor to a multiple; clamp. |
| P7 | `Party.Slots.Length != PARTY_SLOTS`, or a `null` element in it; `Combat.Mp`, `SkillCooldowns` or `CompanionCooldowns` not `PARTY_SLOTS` long; `Loadout.Runes.Length != RUNE_SLOTS` | Pad slots with an empty `PartySlot` (all ids `null`, rank 0) and replace a `null` element with one; pad pools and cooldowns with 0 and runes with `null`; truncate past the count. |
| P8 | A `Party.Slots[i]` `Companion`, `Weapon` or `Skill` (or a `Loadout.Runes` entry) not owned, unknown to content, or of the wrong kind; the same weapon, skill or companion in two slots (00 decision 109: each is unique across slots) | Set that field to `null`, keeping the lowest-index slot of a duplicate. Runs after P10, so an id content removed is cleared here too. Nothing is auto-equipped: an empty or unarmed slot is a valid state (3.2). The slot's other fields and its `AwakenRank` are kept. No slot is locked, so there is no slot-gate rule. |
| P9 | `Fish.Cells[i] < 0` or `> FISH_MAX_LVL_HARD` (the `fish_levels` row count, which 09 never shrinks) | Clamp to that range, never to the reachable cap (`FISH_MAX_LVL` incl. Research): a fish above a lowered cap stays and cannot merge (05), with no refund (issue #41). `DiscoveredLevels` drops levels outside `[1, FISH_MAX_LVL_HARD]` and gains any level present on the board. `HighestLevel` recomputed. `TotalMerges` clamped to `[0, Stats.Merges]`. |
| P9b | `TankCount` outside `[0, TankCap]`; `TankRefillTick` outside `[0, refillTicks-1]`, or non-zero on a full tank; `TankReleaseLevel < 1` or above its content max | Clamp (a full tank's refill progress to 0, 05 section 3). A repaired board passes `FishBoard.FirstViolation`. |
| P10 | `Items` key unknown to content, or `Copies <= 0` | Remove the entry. No refund in MVP (issue #56); ids are never deleted (09 section 3.4), so this path is rare. |
| P11 | `Banners[b].Pity` outside `[0, PITY_HARD-1]`; `Exp < 0` | Clamp. Missing banner for a banner the content defines -> add default. |
| P12 | Quest id unknown to content (`Daily`, `Weekly` and `Achievement` maps, each checked against its own table); `Progress < 0` | Drop the entry; clamp to 0. A quest with no entry reads as `IN_PROGRESS` with progress 0. The sim raises the achievement ladders back to the state's values when it is constructed (06 section 5.4, issue #171). |
| P13 | `Research.Level` key unknown or value `< 1` or `> maxLevel` | Remove (unknown or `< 1`: absent reads as level 0) / clamp. |
| P14 | `OwnedItem.Copies > DUPE_LVL_CUM[10]` (160); `LimitBreaks` outside `[0, LIMIT_BREAK_RANKS]` | Clamp (no Rock Candy is granted on load; conversion happens only at pull time, 04). |
| P15 | Stream counter invariants (3.4): `combat.Counter != 0` (MVP), `crit.Counter != Stats.PartyHits`, `loot.Counter != Stats.BossKills`, `fish_spawn.Counter != Stats.FishReleased`, Local mode `summon_x.Counter != 3 * TotalPulls` | Set the counter from the stat. The player-visible count is the source of truth for "which draw is next". |
| P16 | `Combat.DungeonRun != null` on load | `Mode = Stage`, `DungeonRun = null`, emit `DungeonAbandoned`. No refund: tickets are debited on clear only (06 section 6), so an abandoned run never spent one. |
| P17 | `Pending.Summon` or `Pending.Dungeon` older than `PENDING_STALE_DAYS = 7` | Clear it (the server's idempotency record is gone after 24 h anyway; a 7-day-old intent is dead). Receipts are never auto-cleared. |
| P18 | `Offline.Pending != null` and `ComputedServerTime > now` | Clear it (clock went backwards after compute; nothing was credited). |
| P19 | `ContentVersion` or `ContentHash` empty | Set to `db.Version` / `db.Hash`. |
| P20 | `SaveSeq < 0`; `DeviceId == Guid.Empty` | Set to 0; set to this install's id. |
| P21 | `Tutorial.Step` outside `[0, TUTORIAL_DONE]` (a content change to `tutorial_steps`); `Dungeons.RecentRuns.Count > DUNGEON_RECENT_RUNS_MAX` | Clamp the step; keep the newest `DUNGEON_RECENT_RUNS_MAX` runs. |

Left alone on purpose: `Time.DayKey > dayKey(now)` and `LastSavedServerTime > now` (backwards clock; section 11 handles them), `CreatedServerTime > now` (cosmetic).

A shape no rule expects (a `null` nested collection such as `Combat.MissileQueue`) is a reject, not a crash: the validator wraps any failure of its own in `SaveInvalidException`, so the loader quarantines the file (8.3). An unknown `Rng` name (6.3) is dropped with a log line and is not a repair hit.

## 7. Migrations

### 7.1 Driver

```csharp
public static class SaveSchema
{
    public const int  Current  = GameState.CurrentSchemaVersion;   // SAVE_SCHEMA_VERSION
    public const bool V1Frozen = false;   // set true the day a build ships to anyone outside the team; never set back
}

public static class SaveMigrations
{
    // Steps[i] migrates version i+1 to i+2. Invariant: Steps.Length == SaveSchema.Current - 1.
    static readonly Func<JObject, JObject>[] Steps = { };

    public static JObject Migrate(int v, JObject body, IMigrationLog log)
    {
        if (v > SaveSchema.Current) throw new SaveTooNewException(v, SaveSchema.Current);
        for (; v < SaveSchema.Current; v++)
        {
            body = Steps[v - 1](body);
            body["SchemaVersion"] = v + 1;
            log.Applied(v, v + 1);
        }
        return body;
    }
}
```

`SaveTooNewException` aborts the load: the client shows `SAVE_TOO_NEW` ("This save was made by a newer version. Please update.") and must not fall through to `slot0.prev.sav` (which might be older and loadable, silently discarding progress) and must not write anything. Only happens on a downgrade (TestFlight / internal track).

### 7.2 Rules for a step

1. A step is a pure function `JObject -> JObject`. No `ContentDb`, no `IClock`, no file, no current C# type. Any value it needs is a literal frozen inside the step, because content and code keep changing after the step is written and the step must produce the same output forever.
2. A step tolerates absent optional fields (dev builds mid-version) and never throws on a shape it can plausibly meet; it fills defaults.
3. A step does not set `SchemaVersion`; the driver does.
4. Forward only. No down-migration; a newer save is refused.
5. Renames are expressed as a rename, never drop-and-add. A field may be dropped only when nothing in current code can use it.
6. A new RNG stream is inserted with its seed already derived inside the step from the body's `RngRoot` (`seed(name)` of 3.4, with the SHA-256 formula and the stream name as literals in the step) and `Counter: 0`. This is pure (rule 1) because `RngRoot` is in the body and never changes, so the same save gets the same seed on every device and R4 needs no exemption (issue #9).
7. What is a schema change: adding, removing, renaming or retyping a serialized property; renaming an enum member; changing a dictionary key format; changing the required stream set; changing a field's meaning (seconds -> ticks, remaining -> used). What is not: content changes, new keys in `Banners`/`Rng`/`Items`, new values in existing fields.

### 7.3 Fixtures and the test

Directory `src/HeroCat.Runtime.Tests/Fixtures/Saves/`:

```
v1.json                      a real save at v1 taken from a device or the bot the day v1 froze
v1.sha256                    hash of v1.json, asserted when V1Frozen
v1.to.v2.expected.json       (when v2 exists) exact output of Steps[0](v1.json)
v2.json                      a real v2 save
...
```

Tests (`MigrationTests.cs`), each required to pass in CI:

| Id | Assertion |
|---|---|
| M1 | `Steps.Length == SaveSchema.Current - 1`. |
| M2 | For each `n < Current`: `Migrate(n, v{n}.json)` applied one step equals `v{n}.to.v{n+1}.expected.json` ignoring whitespace. |
| M3 | For each fixture `v{n}.json`, `n = 1..Current`: `Migrate` to `Current`, `Materialize`, `Validate` produces zero repairs and zero rejects. **Every fixture migrates to current.** |
| M4 | Each step runs with `ContentDb` and `IClock` replaced by throwing stubs (purity). |
| M5 | When `V1Frozen`, SHA-256 of `v1.json` equals `v1.sha256`. |
| M6 | `Materialize(Migrate(...))` then `Encode` then `Decode` then `Materialize` yields a value-equal state (idempotent after migration). |
| M7 | A body with `SchemaVersion = Current + 1` raises `SaveTooNewException` and the store does not consult `prev`. |

Illustrative future step, to fix the style (hypothetical v1 -> v2 adding Rune sockets and a `rune_roll` stream):

```csharp
// v1 -> v2: rune sockets on weapons (04 P1) and the rune_roll stream.
static JObject V1ToV2(JObject j)
{
    foreach (var item in j["Weapons"]!["Items"]!.Children<JProperty>())
        ((JObject)item.Value)["Sockets"] ??= new JArray(null, null);           // literal 2, not RUNE_SOCKETS (rule 1)
    var root = Guid.Parse((string)j["RngRoot"]!);
    ((JObject)j["Rng"]!)["rune_roll"] ??= new JObject { ["Seed"] = V1ToV2Seed(root, "rune_roll"), ["Counter"] = 0 };   // rule 6: frozen copy of seed(name)
    return j;
}
```

### 7.4 Freeze rule

v1 is frozen the day a build ships to anyone whose save the team is not willing to delete (external TestFlight / Play test track, or any playtester outside the dev team). On that day: set `V1Frozen = true`, commit `v1.json` and `v1.sha256`. Before the freeze, `GameState` changes freely and a dev save that no longer fits is quarantined and the dev gets a new profile. The party model (00 decision 109) is such a change: v1 was redefined in place, with no step (5).

## 8. `LocalSaveStore`

### 8.1 Interface

```csharp
public enum SaveTrigger { Periodic, Pause, Quit, Resume, Summon, Inbox, Dungeon, Debug, Conflict }
public enum LoadStatus  { Loaded, LoadedFromPrev, NewProfile, Conflict, TooNew }

public sealed record LoadResult(LoadStatus Status, GameState? State, GameState? Other, IReadOnlyList<string> Repairs, SaveMeta? LocalMeta, SaveMeta? RemoteMeta);

public interface ISaveStore
{
    Task<LoadResult> LoadAsync(long nowServerSec, ContentDb db, CancellationToken ct);
    void Save(GameState state, SaveTrigger trigger);             // synchronous; must complete inside OnApplicationPause
    Task ResolveConflictAsync(GameState chosen, GameState rejected, CancellationToken ct);   // local: archives rejected
}
```

### 8.2 Stamp and write

```
Save(state, trigger):
  SaveStamp.Apply(state, clock.NowServerSeconds, db.Version, installId)
      // LastSavedServerTime = now; SaveSeq += 1; ContentVersion = db.Version; ContentHash = db.Hash; DeviceId = installId
      // never Time.LastTickServerTime: the sim owns it inside Apply (every tick raises it to the virtual server now,
      // ComputeOfflineCatchup stamps it; 00 contract 5), so a command-log replay reproduces it. Every field the stamp
      // writes is bookkeeping that StateHash leaves out.
  bytes = SaveSerializer.Encode(state, deviceKey)
  using fs = new FileStream(slot0.tmp, Create, Write, FileShare.None)
      fs.Write(bytes); fs.Flush(flushToDisk: true)
  if exists(slot0.sav): File.Move(slot0.sav, slot0.prev.sav, overwrite: true)     // rename(2), atomic on APFS/ext4/f2fs
  File.Move(slot0.tmp, slot0.sav, overwrite: true)
```

Directory: `Application.persistentDataPath/saves/`. Files: `slot0.sav`, `slot0.prev.sav`, `slot0.tmp`, plus quarantine `slot0.corrupt-<unix>.sav` and conflict archives `slot0.conflict-<unix>.sav`; a name already taken (both files quarantined in the same second) gets `-1`, `-2`, ... before `.sav`. Retention ages are judged by the `<unix>` in the name (server seconds from `IClock`), not by file mtimes. One slot in MVP; the prefix leaves room.

Code shape (issue #77): `LocalSaveStore` implements `ISaveStore` on top of a raw-bytes `ISaveSlotStore` (`FileSaveStore` on device, `MemorySaveStore` for tests and bots, the cloud wrapper in MVP), which only reads, rotates, quarantines, archives and purges. After `TooNew` the store refuses every `Save` (`SaveWriteException`). A successful load floors a Local-mode clock at the loaded `LastSavedServerTime` (11.2 rule 4).

Properties: at every instant at least one of `slot0.sav`/`slot0.prev.sav` is complete and CRC-valid; a crash during the `tmp` write leaves `sav`/`prev` untouched; startup deletes stray `slot0.tmp`. Failure throws `SaveWriteException`; the caller keeps memory state and retries at the next trigger (01 section 3.1). `SaveStamp` has already run, so memory carries a newer `LastSavedServerTime` than disk; the consequence is one bounded double-credit window (8.5).

### 8.3 Load and corruption recovery order

```
LoadAsync(now, db):
  delete slot0.tmp if present; delete slot0.corrupt-* older than CORRUPT_KEEP_DAYS; delete slot0.conflict-* older than CONFLICT_KEEP_DAYS
  for file in [slot0.sav, slot0.prev.sav]:
      if not exists: continue
      try:
          sf    = SaveSerializer.Decode(read(file), deviceKey)
          if sf.SchemaVersion > SaveSchema.Current: return TooNew               // never falls to prev, renames nothing; later Saves are refused
          body  = SaveMigrations.Migrate(sf.SchemaVersion, sf.Body, log)
          state = SaveSerializer.Materialize(body)
          reps  = SaveValidator.Run(state, now, db)                               // throws SaveInvalidException; analytics save_repaired{rule} per id
          if file is prev: analytics save_fallback
          return Loaded / LoadedFromPrev
      catch SaveCorruptException | SaveInvalidException | JsonException: analytics save_reject{file, reason}; continue
  if either file existed: rename each to slot0.corrupt-{now}.sav; analytics save_corrupt
  return NewProfile(ProfileFactory.Create(playerId, now, db))                     // flagged ReplacedUnreadableSave when a file existed
```

No readable copy anywhere (both local files bad and, in MVP, no usable cloud copy) is not fatal (issue #248; 01 section 7 has no `SAVE_CORRUPT_ALL` class): the files are quarantined as above, the new profile starts, Runtime's boot sends `save_corrupt_all{supportId}` and `SCR_MAIN` shows a one-time acknowledge-only notice ("your save could not be read; a new game was started; contact support with id X", keys `notice.save_corrupt_all.title` / `.body`, 07 section 4.27). The next boot loads the new profile's save, so the notice is shown once. `save_corrupt` stays the local-quarantine signal 10's alarm watches; in M0, with no cloud, both events fire together.

Recovery order, from best to worst outcome: (1) `slot0.sav`, (2) `slot0.prev.sav` (at most one autosave interval older), (3) cloud copy if `CloudSaveClient` is active (9.2 covers the merge), (4) new profile with the corrupt files quarantined for `CORRUPT_KEEP_DAYS` so support can recover them. A bug in the loader therefore never destroys a player's data; it quarantines it.

### 8.4 Other local files (not the save)

| File | Content | Backed up by OS? |
|---|---|---|
| `saves/slot0*.sav` | The save | Excluded from iCloud/Android auto-backup (`NSURLIsExcludedFromBackupKey`, `android:allowBackup` with exclusion rules). Cloud save is the backup; an OS-restored save on a new device would carry the old device's key and lineage and confuse conflict resolution. |
| `install_id` | `DeviceId` GUID, created at first launch and reused; a missing or unreadable file means a new id (`InstallId.LoadOrCreate`) | Excluded. A restored install is a new device. |
| `device_key` | 32 random bytes for HMAC (section 10) | Secure storage (Keychain / Android Keystore-wrapped), excluded from backup. |
| `tokens.json` | JWTs, the account's `playerId`, a pending guest `requestId` (`SecureTokenStore`, issue #148) and the `registeredKeyId` of 10.1 (issue #150) | Secure storage. Until the platform stores land (#151), editor and desktop builds use `FileSecureStore` under `{dataDir}/secure/`, which is not secure. |
| `profile.json` | `Profile` cache | Included; harmless. |
| `client_prefs.json` | `ClientPrefs` (fields are 07's): new-item badges, `SeenCoachMarks`, sort/filter prefs, `TrainStep`, `Notify*`, `Locale`, `Sound`, `Music`, `Haptics`, `ReducedMotion`, `LargeText`, `TargetFps` (issue #47). Never cloud-synced, never in `GameState`; written atomically like the save (tmp + rename), and a missing or unreadable file means defaults | Included; harmless (losing it only resets NEW dots and coach marks). |
| `config_cache.json` | last remote config | Included. |
| `content/<version>/` | downloaded bundles | Excluded (re-downloadable). |
| `saves/cmdlog.jsonl` | command log (01 section 2.6) | Excluded. |
| `analytics/queue.ndjson` | pending events | Excluded. |

### 8.5 Known double-credit window

If the `Save` after `ComputeOfflineCatchup` fails (disk full) and the app then dies, disk holds the old `LastTickServerTime` and the next load computes the same window again, bounded by the effective offline cap (`OFFLINE_CAP_H` plus the `RES_OFFLINE_CAP` extension, 06; issue #21) of gold and EXP at `OFFLINE_RATE`. Accepted in M0; in MVP the server copy carries the newer anchor and the plausibility check `OFFLINE_WINDOW_OVERLAP` (08) flags a save whose credited windows overlap.

## 9. `CloudSaveClient` (MVP)

### 9.1 Metadata

Sent with every upload in the `X-Save-Meta` header and returned by `GET /save/meta` (08 section 3.1 owns the wire; paths are relative to 08's `/v1` base URL; issue #34):

```json
{
  "playerId": "6b1f2c40-...", "deviceId": "0f9e8d7c-...", "saveSeq": 1874,
  "lastSavedServerTime": 1793667200, "schemaVersion": 1, "contentVersion": "2026.11.03",
  "clientVersion": "1.4.2+318", "sizeBytes": 23811, "sha256": "ab12...",
  "hmac": "9c7e...", "clockAuthoritative": true,
  "progressScore": 41200.5,
  "summary": { "stage": 412, "partyLevel": 143, "diamonds": 1240, "totalPulls": 632, "highestFish": 8, "awakenRanks": [2, 1, 1, 0, 0] }
}
```

`progressScore` is a single monotone-ish number the conflict rule compares:

```
progressScore = HighestCleared * 100
              + Party.Level * 10
              + sum(Training.Level) * 1
              + sum over collections of Copies * 5
              + Fish.HighestLevel * 50
              + sum over Party.Slots of AwakenRank * 2000
              + Stats.DungeonClears * 20
```

It deliberately excludes currency balances (spending lowers them) and includes only things that only go up (slot ranks do: a companion swap keeps its slot's rank, 3.2). `summary.awakenRanks` is `Party.Slots[i].AwakenRank` in slot order. Weights are engineering constants in `SaveConfig`; they only need to make "clearly more progress" visible.

### 9.2 Upload

| Trigger | Behaviour |
|---|---|
| `Periodic` | Coalesced: at most one upload per `CLOUD_UPLOAD_MIN_SEC`; the queue holds one item (the latest bytes). |
| `Pause`, `Quit`, `Summon`, `Inbox`, `Dungeon`, `Conflict` | Immediate. `Quit` and `Pause` get a 3 s budget then abandon (the local write already succeeded; the upload retries at next launch). `Summon` uploads **before** `POST /summon/pull` when the client-earnings counters (3.2) changed since the last acknowledged upload, and the pull waits for it (issue #49). |
| Offline | Latest bytes stay queued; retried on reconnect, resume, and next trigger. |

Request: `PUT /save` with `If-Match: <etag>` of the last server version this device observed (`If-Match: *` on first upload and on guest adoption), the 9.1 metadata as JSON in `X-Save-Meta`, `Content-Type: application/octet-stream`, and the raw file bytes (header + body) as the body (08 section 3.1; issue #34). Server validates header, CRC, HMAC against the device's registered key, `sizeBytes <= SAVE_BLOB_MAX_BYTES`, rejects a `saveSeq` not above the stored one for the same `deviceId` with `409 SAVE_STALE`, checks `lastSavedServerTime <= serverNow + CLOCK_MAX_SKEW_ACCEPT_S`, credits the client-earnings delta to the ledgers (3.2; issue #49), runs plausibility checks (08), stores the blob in S3 keyed `saves/{playerId}/{saveSeq}.sav`, updates `saves` row, returns `{etag, saveSeq, serverTime, flags[], diamondReconcile}`.

Keep the last `SAVE_HISTORY_KEEP = 10` blobs per player server-side for support rollback.

### 9.3 Conflict rules

Inputs: `L` local candidate (8.3), `R` remote candidate (`GET /save` blob, fetched when `GET /save/meta` differs from the last etag seen or at boot). Applied at boot, at resume (meta only, blob on demand), and on a `409` from `PUT` (`SAVE_CONFLICT` or `SAVE_STALE`).

```
Resolve(L, R):
  1. only one exists                 -> use it. R only: write locally. Status Loaded.
  2. L.sha256 == R.sha256            -> use L.
  3. L.deviceId == R.deviceId        -> higher saveSeq wins, silently (same lineage; the other is just older).
  4. different devices:
       winner = higher lastSavedServerTime
       if !L.clockAuthoritative && R.clockAuthoritative: winner = R   (a non-authoritative stamp never beats an authoritative one)
       if !R.clockAuthoritative && L.clockAuthoritative: winner = L
       tie: higher saveSeq, then R
       loser = the other
       if loser.progressScore > winner.progressScore * (1 + CONFLICT_PROGRESS_MARGIN)
          or loser.summary.totalPulls > winner.summary.totalPulls
          or loser.summary.diamonds >= winner.summary.diamonds + PULL_COST[WEAPON]:
            Status Conflict, both states returned; the client shows the modal with `winner` preselected
       else: use winner silently; analytics cloud_autoresolved{winnerSide}
```

Why last-writer-wins with a progress override and not a merge: the save is a single RNG lineage and a single pity history. Two devices that both made pull `k` may have gotten different server results; merging copy counts would double-count and merging counters is undefined. One lineage is kept, the other discarded, and the player is asked only when the discard would be felt.

### 9.4 Conflict modal rules (owned here, rendered by 07)

- Two cards, "This device" and "Other device" (device model name from `devices`, 08), each showing: stage, party level, diamonds, total pulls, highest fish, last played (relative time). The rule's winner is preselected and labelled "Recommended".
- Shown only on `SCR_MAIN` idle: never during a boss, a reveal, a dungeon run or over another modal. Until shown, the game runs on `L` and **does not upload** (uploads are suspended while a conflict is unresolved so the loser is not overwritten by a periodic save).
- `ResolveConflictAsync(chosen, rejected)`: write `chosen` locally, archive `rejected` as `slot0.conflict-<now>.sav` for `CONFLICT_KEEP_DAYS`, upload `chosen` with `If-Match: R.etag`. If that PUT also 409s (a third device), resolve again from the top.
- Choosing the other device's save replaces the running `Simulation` (the runner is rebuilt; the current session's unsaved progress since `L` was loaded is the price, and the modal says so: "Progress on this device since <time> will be lost").
- Analytics `cloud_conflict{winnerSide, chosenSide, scoreL, scoreR, secondsApart}`.

## 10. Integrity envelope

### 10.1 Device key and HMAC

At first launch `HeroCat.Runtime.Account.DeviceKey.LoadOrCreate(ISecureStore)` generates `HMAC_KEY_BYTES` random bytes from a CSPRNG (`DeviceKey.Create()`) and stores them in platform secure storage as `device_key` (8.4); later launches reuse them, and a stored value of the wrong length is replaced. The key is symmetric: the client and the server hold the same bytes. The server stores it in `devices.hmac_key_enc` (envelope-encrypted, section 12). Every save header carries `hmac = HMAC-SHA256(deviceKey, body)`.

Registration (as built, issue #150). The key travels only over TLS and only in the bodies of 08 section 2.2's registering calls: `POST /auth/guest` registers it with the account it creates (`POST /auth/link-login` with the account it signs into, issue #196), and `POST /devices {deviceId, deviceKey, model, os, clientVersion}` registers a key the server does not hold yet. The key's id, `keyId = hex(SHA-256(key))[:16]` (`DeviceKey.KeyId`), is client-local: `tokens.json` records `registeredKeyId` for the session's player (set by the guest call and by a `/devices` 204, kept across refreshes, gone with the tokens on logout), and the AuthClient sends `POST /devices` only when the current key's id differs (a key replaced after the guest call, or tokens from a build without the record). The server never sees `keyId`; it compares the key bytes, so the same `deviceId` and key again is a 204 that only updates the metadata, and the same request id is replayed by the idempotency store. The client reuses one request id per (player, key) until the call lands. Uploads wait for it: `CloudSaveStore` awaits the registration before each `PUT /save`; a transient failure (offline, timeout, 5xx, 429, 401) keeps the upload queued and the next upload retries, while a final refusal (`403 KEY_ROTATION_NOT_ALLOWED`, `409 DEVICE_ALREADY_BOUND`, ...) is not retried in that process and lets uploads go ahead, so the server records `hmacValid = false` (5.1: never a rejection). The key never appears in logs, analytics, headers or any other request; `keyId` is what may be stored or shown outside secure storage.

What it is for: the server can tell a save written by this client build on this registered device from a hand-edited blob. What it is not: protection against a rooted device, which can extract the key. The key makes casual editing fail and makes a cheater's edits attributable to a device; the plausibility checks make them detectable.

Local mode (M0) has no key; `hmac=-`. Bots write no files; the bot harness passes no key, so any save it encodes is `hmac=-`.

### 10.2 Server plausibility checks (named here, specified in 08)

The server runs these on every upload and attaches `flags[]` to the response. The client only logs flags; the server acts (08 action matrix).

| Check name | What it tests |
|---|---|
| `HMAC_INVALID` | Envelope does not verify against the device's key. |
| `SEQ_REGRESSION` | `saveSeq` below the save of record of another device of the same player; accepted and flagged (`info`). A `saveSeq` not above the last one stored for the same `deviceId` is not flagged but rejected `409 SAVE_STALE` (08 section 3.1; issues #34, #198). |
| `CLOCK_AHEAD` | `lastSavedServerTime > serverNow + CLOCK_MAX_SKEW_ACCEPT_S`. |
| `STAGE_VS_TIME` | `HighestCleared` exceeds the bot-curve stage for `(now - CreatedServerTime)` by more than the 08 bound. |
| `DPS_VS_STAGE` | DPS re-derived via `HeroCat.Sim` from the uploaded state cannot clear `HighestCleared`'s boss in `BOSS_TIME_TICKS`. |
| `GOLD_DELTA_BOUND` | Gold gained since the previous upload exceeds the maximum the sim could produce in the elapsed time at `HighestCleared`. |
| `DIAMOND_LEDGER_MISMATCH` | `Wallet.Diamond` exceeds the server ledger (grants - spends, after this upload's client-earnings credit; issue #49) by more than the per-day F2P cap times days. |
| `ITEM_WITHOUT_PULL` | A collection copy count exceeds what `summon_log` plus `granted_items` account for (plus one copy of each `TUTORIAL_WEAPON_IDS` weapon and each `TUTORIAL_COMPANION_IDS` companion, granted at profile creation; 3.5, issue #46). |
| `PITY_MISMATCH` | Client `Banners[b].Pity` differs from server pity (informational; the server value is used regardless). |
| `DUPLICATE_REQUEST_ID` | A `requestId` seen on a different player. |
| `OFFLINE_WINDOW_OVERLAP` | Lifetime offline credited exceeds wall-clock gaps observable from upload timestamps. |
| `STREAM_COUNTER_MISMATCH` | Any P15 invariant (`crit.Counter != Stats.PartyHits`, `loot.Counter != Stats.BossKills`, ...) fails after the client's own validator should have repaired them. |
| `TAINTED` | `DebugTainted == true` on a release build. |

## 11. The clock seam

### 11.1 Interface and implementations

```csharp
namespace HeroCat.Runtime.Services;   // with the other Runtime seams (decided in issue #78)

public interface IClock
{
    long NowServerSeconds { get; }     // unix seconds UTC, anchored to server time
    bool IsAuthoritative  { get; }     // true once anchored to a server observation in this process
}

public interface ITimeApi { Task<long> GetServerTimeAsync(CancellationToken ct); }   // GET /time (08 section 2.2)

public sealed class ServerClock : IClock, IClockFloor, IClockResync    // thread-safe
{
    readonly ITimeApi? api;            // null in Local mode: never syncs
    readonly IClock device;            // SystemClock (device time, itself floored) until the first observation
    readonly IMonoClock mono;          // Stopwatch by default; injectable, so tests never sleep
    long?  offsetSec;                  // serverNow - monoNow at last observation
    long   floorSec;                   // Time.LastSavedServerTime at load; time never reads below this
    long   lastSyncMono;               // last observation or sync attempt: the CLOCK_RESYNC_SEC cadence
    long Mono() => mono.Ticks / mono.Frequency;

    public ServerClock(ITimeApi? api = null, IClock? device = null, IMonoClock? mono = null);

    public bool IsAuthoritative => offsetSec.HasValue;
    public long NowServerSeconds => Math.Max(floorSec, offsetSec is long o ? Mono() + o : device.NowServerSeconds);

    public void SetFloor(long lastSavedServerTime) { if (lastSavedServerTime > floorSec) floorSec = lastSavedServerTime; }

    // Called by ApiClient on every response with X-Server-Time (01 section 5.1), and by SyncAsync.
    public void Observe(long serverTimeSec, long rttMs)
    {
        long extrapolated = NowServerSeconds;
        offsetSec = serverTimeSec + Math.Max(0, rttMs) / 2000 - Mono(); lastSyncMono = mono.Ticks;
        Resynced?.Invoke(extrapolated, NowServerSeconds);  // SessionRunner: clock_anomaly{resync} past CLOCK_JUMP_WARN_SEC (01 8.2)
    }
    public event Action<long, long> Resynced;

    public bool ResyncDue => api != null && no sync in flight && mono.Ticks - lastSyncMono >= CLOCK_RESYNC_SEC * mono.Frequency;

    // GET /time; SessionRunner fires it (not awaited) at boot, resume and gap recovery, and on the frame ResyncDue turns true.
    // True when an observation landed; false in Local mode, on any failure or cancellation (the offset is kept). Never throws.
    // A call while one is in flight joins it; an attempt restarts the cadence, so an offline device retries every 900 s.
    public Task<bool> SyncAsync(CancellationToken ct = default)
    { if (api == null) return false; lastSyncMono = mono.Ticks; long t0 = mono.Ticks; var t = await api.GetServerTimeAsync(ct); Observe(t, ms since t0); return true; }
}

public sealed class FakeClock : IClock
{
    public long NowServerSeconds { get; private set; }
    public bool IsAuthoritative  => true;
    public void Set(long t) => NowServerSeconds = t;
    public void Advance(long dt) => NowServerSeconds += dt;
}
```

As built (issues #78, #149): `ServerClock.cs` holds both clocks. `SystemClock` is the device time floored at the loaded save's `LastSavedServerTime`, never authoritative; `ServerClock` reads it before the first observation. `IClockFloor.SetFloor` is called by `LocalSaveStore` after each load and the floor only rises. Local mode is `ServerClock(api: null)` (the composition root's default); Remote mode passes `HeroCat.Remote`'s `RemoteTimeApi` over `GET /time`, and its `ApiClient` (issue #143) forwards every response's `X-Server-Time` to `Observe` through the `IServerTimeObserver` seam that `ServerClock` implements, so any API call restarts the `CLOCK_RESYNC_SEC` cadence. `IMonoClock` is the monotonic seam `SessionRunner` also uses. `IClockResync` (`Resynced`, `ResyncDue`, `SyncAsync`) is what `SessionRunner` sees, so a wrapper such as the client's debug `OffsetClock` forwards all three. `clock_anomaly{kind: resync, seconds, negative}` is tracked by `SessionRunner.OnClockSynced` from `Resynced`, not by the clock, so it is emitted once per sync whose result differs from the extrapolation by more than `CLOCK_JUMP_WARN_SEC`. This includes the first observation of an offline launch, whose extrapolation was the floored device clock.

### 11.2 Rules

1. Nothing in `HeroCat.Sim` reads a clock. The sim receives `now` only through the logged system commands `ApplyServerTime(nowServerSeconds)` and `ComputeOfflineCatchup(nowServerSeconds)` (which calls `Catchup.Compute` inside `Apply`; issue #6). Between them the sim advances a virtual server time per tick and rolls the day on the tick that crosses midnight (06; issue #5). In `HeroCat.Runtime`, only `ServerClock.cs` and `SessionRunner.cs` may touch `DateTimeOffset.UtcNow` or `Stopwatch`. CI grep (10).
2. Consumers of `IClock`: `SaveStamp`, the `ApplyServerTime` command (re-anchors virtual time; issued only at boot, resume, gap recovery and every `WALL_CLOCK_SYNC_SEC = 60`, never per frame; issue #5), the `ComputeOfflineCatchup` command, `Pending*` timestamps, analytics timestamps, cloud-save metadata. Combat ticks never read the clock; the monotonic accumulator in `SessionRunner` drives them.
3. Between observations time advances by the monotonic clock, so a device clock change mid-session has no effect once `IsAuthoritative`.
4. Until the first observation (offline launch), the device clock is served floored at `LastSavedServerTime`, so time cannot run backwards past the last save. `IsAuthoritative = false` flows into the save metadata and into the conflict rule (9.3 step 4).

### 11.3 Backwards-clock rules

| Situation | Behaviour |
|---|---|
| `now < Time.LastTickServerTime` at resume | `ComputeOfflineCatchup` credits `ElapsedSec = 0`; nothing credited; `clock_anomaly{backwards}` once per session when the gap exceeds `CLOCK_BACKWARDS_TOLERANCE_S` (5 s, 01 section 3.2; issue #246), nothing for smaller jitter. |
| `dayKey(now) < Time.DayKey` | `ApplyServerTime` does nothing (`if newKey <= DayKey return`). Dailies are not reset; login diamonds not re-granted. |
| `now < Offline.Pending.ComputedServerTime` at load | P18 clears the pending reward (it was never credited). |
| `now < Pending.Summon.RequestedServerTime` | Irrelevant; the server's idempotency store decides. |
| Resync shows the server is *ahead* of the extrapolation by more than `CLOCK_JUMP_WARN_SEC` | Extra elapsed time is credited as a second catch-up window (01 section 8.2 step 2), capped by the 24 h sum. |
| Resync shows the server is *behind* the extrapolation by more than `CLOCK_JUMP_WARN_SEC` (the same 300 s threshold as *ahead*; 11.1, 01 section 8.2 step 2; issue #257) | Nothing is taken back; `clock_anomaly{resync, negative}`; the save's `LastTickServerTime` keeps its larger value, which simply shortens the next window. A smaller backwards correction emits no `resync` event: the next `ApplyServerTime` re-anchor (at most `WALL_CLOCK_SYNC_SEC` later) reports it as `clock_anomaly{backwards}` once it exceeds `CLOCK_BACKWARDS_TOLERANCE_S` (once per session). |
| Device clock set years ahead, then back (offline) | At most one `OFFLINE_CAP_H` window and one daily reset were gained while unauthoritative; the next authoritative upload carries `CLOCK_AHEAD` and the server's ledger reconciliation (08) corrects diamonds. |

## 12. Server-side relational schema (PostgreSQL 16)

All timestamps `timestamptz`, all ids `uuid` unless noted. Money is never stored as float: prices are `integer` minor units plus `char(3)` currency. Row-level comments give the owning API (08). This section owns every server table, including the server-only ones 08 reads and writes, so the GDPR delete job (13.2) can enumerate them from here (issue #53).

**Migration tooling** (issue #33): the schema ships as plain versioned SQL files, `server/db/migrations/NNNN_<name>.sql` (`0001_init.sql` is the DDL below, verbatim; `0002_save_history_trim.sql` adds the 13.1 trim trigger and `0003_partitions.sql` the partitions, both described after the DDL; issue #154; a later file that changes the DDL is reproduced verbatim after it, like `0004_save_metadata.sql` through `0008_currency_daily_sources.sql`), applied in order by a small in-house runner (`HeroCat.Server --migrate` in the deploy pipeline's pre-deploy job and in CI; `HeroCat:Database:MigrateOnStartup` also runs it at api startup, off by default; issue #192) that records applied files in a `schema_migrations` table. No EF Core migrations; queries use Npgsql/Dapper, or EF Core for queries only, never to change the schema. A migration file is immutable once merged. CI applies the set to an empty PostgreSQL 16 container (`postgres:16`, as in 10) and runs AC 16; `check.sh` runs the same job behind a "Postgres available" guard and reports it as skipped, not passed, where no Postgres or Docker exists (cloud sessions).

```sql
create table accounts (
  player_id         uuid primary key,
  display_name      text not null,
  avatar_id         text not null default 'avatar_default',
  region            char(2) not null,
  age_gate          smallint not null default 0,          -- 0 unknown, 1 under-13, 2 adult
  created_at        timestamptz not null default now(),
  last_login_at     timestamptz,
  status            text not null default 'active',       -- active | soft_flagged | banned | delete_pending | merged_away (08 /auth/merge) | deleted (GDPR tombstone row, 13.2)
  status_reason     text,
  delete_requested_at timestamptz,
  tutorial_diamonds_granted boolean not null default false,
  refund_debt       bigint not null default 0              -- diamonds clamped away by a refund that would have gone negative (08 section 5.4; issue #53)
);
-- First-purchase double (issue #50): per SKU, diamond packs only, derived, no column. A SKU's double is used when
-- `purchases` has a row for (player_id, sku) with status in ('granted', 'refunded', 'chargeback').
-- 08's refunds-in-90-days count is derived too: count(purchases where refunded_at >= now() - interval '90 days').

create table account_links (                               -- Apple / Google Play Games identities
  provider          text not null,                         -- 'apple' | 'google_play'
  provider_user_id  text not null,                         -- opaque id from the provider; hashed with a server pepper
  player_id         uuid not null references accounts on delete cascade,
  linked_at         timestamptz not null default now(),
  primary key (provider, provider_user_id)
);
create index on account_links (player_id);

create table devices (
  device_id         uuid primary key,                      -- GameState.DeviceId
  player_id         uuid not null references accounts on delete cascade,
  hmac_key_enc      bytea not null,                        -- envelope-encrypted with KMS; never logged
  model             text, os text, client_version text,
  first_seen_at     timestamptz not null default now(),
  last_seen_at      timestamptz not null default now(),
  push_token        text                                   -- P1 notifications
);
create index on devices (player_id);

create table refresh_tokens (
  token_hash        bytea primary key,                     -- sha256 of the token
  player_id         uuid not null references accounts on delete cascade,
  device_id         uuid not null references devices,
  family_id         uuid not null,                         -- one family per login; rotation keeps it; reuse of a revoked token revokes the family (08 TOKEN_REUSED; issue #53)
  replaced_by       bytea,                                 -- token_hash of the rotated successor; null = current
  issued_at         timestamptz not null, expires_at timestamptz not null, revoked_at timestamptz
);
create index on refresh_tokens (family_id);

create table saves (                                       -- one row per player: current save of record
  player_id         uuid primary key references accounts on delete cascade,
  save_seq          bigint not null,
  device_id         uuid not null references devices,
  schema_version    integer not null,
  content_version   text not null,
  client_version    text not null,
  last_saved_server_time bigint not null,                  -- as stamped by the client; see clock_authoritative
  clock_authoritative boolean not null,
  size_bytes        integer not null,
  sha256            bytea not null,
  hmac_valid        boolean not null,
  blob_key          text not null,                         -- s3://herocat-saves-<env>/saves/{player_id}/{save_seq}.sav
  etag              text not null,                         -- opaque; changes on every write; used for If-Match
  progress_score    double precision not null,
  summary           jsonb not null,                        -- the "summary" object from 9.1
  flags             text[] not null default '{}',          -- plausibility flags from the last upload
  rollback_pending  boolean not null default false,        -- set by an anti-cheat rollback; next PUT returns 409 SAVE_ROLLED_BACK, cleared once the device downloads (08; issue #53)
  cmdlog_requested  boolean not null default false,        -- next PUT response carries requestCmdLog: true; cleared when an upload includes the log (08 section 7.3)
  uploaded_at       timestamptz not null default now()
);

create table save_history (                                -- last SAVE_HISTORY_KEEP blobs per player, for support rollback
  player_id uuid not null references accounts on delete cascade,
  save_seq  bigint not null, device_id uuid not null, blob_key text not null, sha256 bytea not null,
  progress_score double precision not null, uploaded_at timestamptz not null default now(),
  primary key (player_id, save_seq)
);

create table diamond_ledger (                              -- server-authoritative hard currency; the sum is the truth
  id            bigserial,
  player_id     uuid not null references accounts on delete restrict,   -- financial: the delete job re-points or deletes rows first (13.2; issue #32)
  delta         integer not null,
  reason        text not null,                             -- CurrencyReason enum name: IAP, MAIL, SUMMON, REFUND, ADMIN, or a client-side reason credited from the save (LOGIN, QUEST, SHOP_EXCHANGE, ...)
  ref_type      text, ref_id text,                         -- 'purchase' | 'inbox_item' | 'summon' | 'save_reconcile' (ref_id = save_seq; one row per reason per upload, issue #49)
  request_id    text,                                      -- client requestId when client-originated
  created_at    timestamptz not null default now(),
  primary key (id, created_at)                             -- a partitioned table's keys must include the partition column
) partition by range (created_at);                         -- one partition per month (13.1)
create index on diamond_ledger (player_id, created_at);
create index on diamond_ledger (player_id, request_id) where request_id is not null;   -- not unique: a unique index would have to include created_at. At-most-once per request is the idempotency row written in the same transaction (00 section 8.13)
create index on diamond_ledger (player_id, reason) where ref_type = 'save_reconcile';   -- "already credited" per reason (3.2)

create table ticket_ledger (                               -- server-authoritative summon tickets (00 decision 15; issues #34, #49); the sum per ticket is the balance
  id            bigserial primary key,
  player_id     uuid not null references accounts on delete cascade,
  ticket        text not null,                             -- currency code: TICKET_SUMMON_WEAPON | _SKILL | _COMPANION | _RUNE
  delta         integer not null,
  reason        text not null,                             -- CurrencyReason enum name: SUMMON, MAIL, ADMIN, or a client-side reason credited from the save (DAILY_GIFT, QUEST, ACHIEVEMENT, ...)
  ref_type      text, ref_id text,                         -- 'inbox_item' | 'summon' | 'save_reconcile' (ref_id = save_seq)
  request_id    text,
  created_at    timestamptz not null default now()
);
create index on ticket_ledger (player_id, ticket);
create unique index on ticket_ledger (player_id, request_id) where request_id is not null;

create table granted_items (                               -- per-player item ledger for non-summon item grants (inbox claims); feeds ITEM_WITHOUT_PULL and the summon handler's CollectionState (08; issue #34)
  player_id     uuid not null references accounts on delete cascade,
  item_id       text not null,
  amount        integer not null,                          -- copies granted
  source_ref    text not null,                             -- inbox_item_id
  created_at    timestamptz not null default now(),
  primary key (player_id, item_id, source_ref)
);

create table idempotency (                                 -- 00 section 8.13: every mutating request, 24 h
  player_id   uuid not null,
  request_id  text not null,
  route       text not null,
  status_code smallint not null,
  response    jsonb not null,
  created_at  timestamptz not null default now(),
  primary key (player_id, request_id)
);
create index on idempotency (created_at);                  -- purge job deletes rows older than IDEMPOTENCY_TTL_H = 24
create index on idempotency (request_id);                  -- cross-player reuse -> DUPLICATE_REQUEST_ID (10.2; issue #53)

create table purchases (
  purchase_id       uuid primary key,
  player_id         uuid not null references accounts on delete restrict,   -- financial (13.2; issue #32)
  platform          text not null,                         -- 'apple' | 'google_play'
  sku               text not null,
  transaction_id    text not null,                         -- Apple transactionId / Google purchaseToken hash
  original_transaction_id text,                            -- Apple; for restores
  price_minor       integer, currency char(3),
  status            text not null,                         -- pending | granted | already_granted | invalid | refunded | chargeback
  raw_receipt_key   text,                                  -- S3 key of the signed payload (retained 7 years for tax/fraud; not in PG)
  inbox_item_id     uuid,                                  -- the immediate portion; scheduled package portions point back via inbox_items.source_ref (issue #51)
  granted_at        timestamptz, refunded_at timestamptz,
  created_at        timestamptz not null default now(),
  unique (platform, transaction_id)
);
create index on purchases (player_id, created_at);
create index on purchases (player_id, sku) where status in ('granted', 'refunded', 'chargeback');   -- first-purchase double lookup (issue #50)
create index on purchases (player_id, refunded_at) where refunded_at is not null;                -- refunds-in-90-days count (08 section 5.4)

create table store_notifications (                         -- Apple / Google webhook dedupe (08 section 5.4; issue #53)
  platform          text not null,                         -- 'apple' | 'google_play'
  notification_id   text not null,                         -- Apple notificationUUID / Google RTDN messageId
  notification_type text not null,
  transaction_id    text,                                  -- joins purchases (platform, transaction_id)
  received_at       timestamptz not null default now(),
  processed_at      timestamptz,
  primary key (platform, notification_id)
);

create table inbox_items (
  inbox_item_id  uuid primary key,
  player_id      uuid not null references accounts on delete cascade,
  kind           text not null,                            -- iap | mail | compensation | coupon | refund | admin
  title_key      text not null, body_key text not null, params jsonb not null default '{}',
  grants         jsonb not null,                           -- [{"kind":"currency","id":"DIAMOND","amount":1200}, {"kind":"item","id":"wpn_017","amount":1}]
  source_ref     text,                                     -- purchase_id | mail_blast_id | coupon_code
  created_at     timestamptz not null default now(),
  available_at   timestamptz not null default now(),       -- scheduled package portions are created at purchase with future available_at; GET /inbox hides rows not yet available (issue #51)
  expires_at     timestamptz,                              -- null = never; a refund sets it to now() on undelivered portions
  claimed_at     timestamptz, claimed_request_id text
);
create index on inbox_items (player_id, available_at) where claimed_at is null;
create unique index on inbox_items (source_ref, player_id) where kind = 'mail';   -- blast materialisation is idempotent per (mail_blast_id, player_id) (08; issue #53)

create table summon_log (                                  -- one row per pull (not per batch): 08 audit requirement
  id              bigserial,
  player_id       uuid not null references accounts on delete restrict,   -- financial (13.2; issue #32)
  request_id      text not null,
  banner          text not null,                           -- WEAPON | SKILL | COMPANION | RUNE
  batch_index     smallint not null,                       -- 0..9
  item_id         text not null, rarity char(1) not null,
  pity_before     smallint not null, pity_after smallint not null,
  banner_level    smallint not null,
  guarantee       boolean not null,                        -- slot 10 ran with forceEplus (04)
  rate_table_hash bytea not null,                          -- hash of the published rate table used (compliance)
  u1              double precision not null,               -- the pull's three uniforms in 04's fixed order (L+ check, rarity pick, item pick),
  u2              double precision not null,               -- all logged so 08 can reproduce the pull exactly
  u3              double precision not null,
  payment         text not null,                           -- DIAMOND | TICKET
  created_at      timestamptz not null default now(),
  primary key (id, created_at)
) partition by range (created_at);                         -- one partition per month (13.1)
create index on summon_log (player_id, created_at);
create index on summon_log (request_id);

create table banner_pity (                                 -- server-held pity, authoritative in MVP
  player_id  uuid not null references accounts on delete cascade,
  banner     text not null,
  pity       smallint not null default 0,                  -- pulls since last L+ (issue #24)
  exp        bigint not null default 0,                    -- cumulative banner EXP; level derived via BANNER_LVL_EXP (04)
  total_pulls bigint not null default 0,
  updated_at timestamptz not null default now(),
  primary key (player_id, banner)
);

create table dungeon_seeds (
  grant_id   uuid primary key,
  player_id  uuid not null references accounts on delete cascade,
  request_id text not null,
  kind       text not null, floor integer not null, day_key integer not null,
  seed       bigint not null,                              -- ulong stored as signed; cast on read
  issued_at  timestamptz not null default now(), expires_at timestamptz not null,
  result     text,                                         -- null | cleared | failed (filled from the next save upload)
  unique (player_id, request_id)
);

create table config_versions (                             -- remote config and content manifest history (08)
  id serial primary key, env text not null, payload jsonb not null, min_client_version text not null,
  content_version text not null, rollout_pct smallint not null default 100,
  active boolean not null default false, created_at timestamptz not null default now(), created_by text not null
);

create table mail_blasts (                                 -- live ops (08)
  mail_blast_id uuid primary key, title_key text not null, body_key text not null, grants jsonb not null,
  audience jsonb not null,                                 -- {"all":true} | {"region":["JP"]} | {"playerIds":[...]} | {"minStage":100}
  send_at timestamptz not null, expires_at timestamptz, created_by text not null, sent_count integer not null default 0,
  requires_approval boolean not null default false,        -- two-person rule (08): over MAIL_GRANT_APPROVAL_DIAMONDS per player or over 10,000 players
  approved_by text, approved_at timestamptz,               -- second admin; must differ from created_by; the worker skips a blast with requires_approval and approved_by null (issue #53)
  check (approved_by is null or approved_by <> created_by)
);

create table moderation_actions (                          -- anti-cheat action matrix (08)
  id bigserial primary key, player_id uuid not null references accounts on delete cascade,
  action text not null,                                    -- log | soft_flag | rollback | ban | unban | appeal_granted | account_merge
  flags text[] not null, detail jsonb, actor text not null, created_at timestamptz not null default now()
);

create table account_exports (                             -- GET/POST /account/export job state (08 section 2; issue #53)
  export_id     uuid primary key,
  player_id     uuid not null references accounts on delete cascade,
  status        text not null default 'queued',            -- queued | running | ready | failed | expired
  requested_at  timestamptz not null default now(),        -- rate limit: one per 7 days per player
  ready_at      timestamptz,
  blob_key      text,                                      -- S3 key of the export JSON; object deleted when expires_at passes
  expires_at    timestamptz                                -- ready_at + 7 days (presigned link lifetime)
);
create index on account_exports (player_id, requested_at);

create table events (                                      -- backend economy events (00 section 3; section 14); keyed by hash, never by player_id
  ts             timestamptz not null,
  name           text not null,
  player_id_hash bytea not null,
  session_id     uuid,
  day_index      integer,
  stage          integer,
  props          jsonb not null default '{}',
  env            text not null
) partition by range (ts);                                 -- one partition per day
create index on events (player_id_hash, ts);
create materialized view currency_daily as                 -- rebuilt nightly for 06's source/sink dashboards
  select player_id_hash, day_index, props->>'currency' as currency, props->>'reason' as reason,
         sum((props->>'delta')::double precision) as sum_delta
  from events where name = 'currency_changed' group by 1, 2, 3, 4;

-- P1 stubs: shape fixed so MVP foreign keys and the GDPR delete job already know about them.
create table coupons (code text primary key, grants jsonb not null, max_redemptions integer, per_player_limit smallint not null default 1, redeemed_count integer not null default 0, expires_at timestamptz, created_by text not null, created_at timestamptz not null default now());
create table coupon_redemptions (code text not null references coupons, player_id uuid not null references accounts on delete cascade, seq smallint not null default 1, inbox_item_id uuid not null, redeemed_at timestamptz not null default now(), primary key (code, player_id, seq));   -- seq <= coupons.per_player_limit (08 P1; issue #34)
create table guilds (guild_id uuid primary key, name text not null unique, created_at timestamptz not null default now(), leader_id uuid references accounts on delete set null);   -- P1 guild logic transfers leadership when the leader leaves or is deleted
create table guild_members (guild_id uuid references guilds on delete cascade, player_id uuid references accounts on delete cascade, role text not null, joined_at timestamptz not null default now(), primary key (guild_id, player_id));
create table guild_chat (id bigserial primary key, guild_id uuid references guilds on delete cascade, player_id uuid references accounts on delete set null, body text not null, created_at timestamptz not null default now());
```

**Trim trigger** (`0002`): `save_history_trim`, after each insert into `save_history`, deletes the player's rows beyond the newest `SAVE_HISTORY_KEEP` (newest by `uploaded_at`, then `save_seq`, then `device_id`; `0007` replaced the function when the history key gained the device).

**Partitions** (`0003`; issue #154): `ensure_partitions(p_parent regclass, p_grain text, p_from timestamptz, p_until timestamptz) returns integer` creates every missing partition whose range touches `[p_from, p_until]`: UTC month ranges named `<parent>_pYYYYMM` for `p_grain = 'month'`, UTC day ranges named `<parent>_pYYYYMMDD` for `'day'`; it returns how many it created and a rerun is harmless. The migration creates the current month plus 3 months for `diamond_ledger` and `summon_log` and today plus 14 days for `events`; the worker's partition job keeps that horizon ahead of now (issue #262). There is no default partition: a row outside every partition fails its insert, so a missing partition is loud, and a new range partition never collides with rows parked in a default one. As built (issue #262): the worker's `partitions` job (`HeroCat.Server.Worker.PartitionMaintenanceService`) runs at worker start and then daily, calls `ensure_partitions` with the same horizons (`PARTITION_AHEAD_MONTHS = 3` for `diamond_ledger` and `summon_log`, `PARTITION_AHEAD_DAYS = 14` for `events`; 08 constants), and publishes each table's horizon, the days from now to the end of its last partition, as `db_partition_horizon_days{table}` (08 section 9.4), which 10 section 10's DB alarm watches. It never drops a partition (retention, 13.1, is a separate job).

**Save metadata columns** (`0004`; issue #198): the cloud-save endpoints (08 section 3.1) return the header's `hmac` in the 9.1 metadata and each history entry's `summary`, which the DDL above did not keep, and a rollback (08 section 7.4) needs each history entry's flags to find the last unflagged one. Shown verbatim, as its file:

```sql
-- 0004_save_metadata.sql (specs/02 section 12 "Save metadata columns"; issue #198): what the cloud-save endpoints return
-- that 0001 did not keep. saves.hmac is the upload header's hmac field (64 lowercase hex, or '-'), returned in the 9.1
-- metadata; save_history keeps each upload's 9.1 summary for GET /save/history and its plausibility flags, so a rollback
-- (08 section 7.4) can find the last entry without any.
alter table saves add column hmac text not null default '-';
alter table save_history add column summary jsonb not null default '{}';
alter table save_history add column flags text[] not null default '{}';
```

**Analytics ingest** (`0005`; issue #209): the backend copy of the 14.1 envelope for `POST /analytics/events` (08 section 3.5) and for server-written economy events. Each later migration that changes this section's DDL is shown here verbatim, as its file:

```sql
-- 0005_analytics_events.sql (specs/02 section 12 "Analytics ingest"; issue #209): the envelope columns POST
-- /analytics/events stores (specs/02 section 14.1, specs/08 section 3.5) and the per-event dedupe key. event_id is the
-- client's eventId (a server-written row gets a random one); the unique index includes ts because a partitioned table's
-- unique keys must hold the partition column, and a retried event repeats its ts. age_gate is the envelope's 0/1, stamped
-- from the access token, never from the client. Every new column is nullable or has a default, so a row written without
-- them (a server-side event) still inserts.
alter table events
  add column event_id            uuid not null default gen_random_uuid(),
  add column source              text not null default 'client',
  add column device_id           uuid,
  add column client_version      text,
  add column content_version     text,
  add column schema_version      integer,
  add column debug_tainted       boolean,
  add column clock_authoritative boolean,
  add column age_gate            smallint not null default 0,
  add column received_at         timestamptz not null default now();
create unique index on events (event_id, ts);
```

**Ledger and lookup indexes** (`0006`; issue #294): `ticket_ledger`'s unique `(player_id, request_id)` index now covers only `ref_type = 'summon'`, the one ticket row a request writes at most once, so an inbox claim (several lines, or claim-all) and the `PUT /save` step 5a credit (one row per reason; 08 section 3.1) store their request id on every row. Restricting it was chosen over widening it to `(player_id, request_id, ticket, reason, ref_id)`, which would still refuse an inbox item with two lines of one ticket. The summon handler's charge insert has no `on conflict`, so nothing infers the index. `config_versions (env) where active` makes "at most one active row per environment" a constraint (activation, 08 section 6.2, clears the old row before setting the new one, under its advisory lock), and `summon_log (player_id, banner, item_id)` serves the summon handler's per-pull collection query (08 section 4.3). Shown verbatim, as its file:

```sql
-- 0006_ledger_indexes.sql (specs/02 section 12 "Ledger and lookup indexes"; issue #294). ticket_ledger's unique
-- (player_id, request_id) index allowed one row per request, but an inbox claim with two ticket lines, claim-all and the
-- PUT /save step 5a credit (one row per reason) write several; it now guards only the summon charge, the one row a request
-- writes at most once. config_versions keeps at most one active row per env (activation clears the old row first, under
-- its advisory lock), and summon_log gets the (player_id, banner, item_id) index the per-pull collection query reads.
drop index ticket_ledger_player_id_request_id_idx;
create unique index on ticket_ledger (player_id, request_id) where request_id is not null and ref_type = 'summon';
create unique index on config_versions (env) where active;
create index on summon_log (player_id, banner, item_id);
```

**Save history per device** (`0007`; issue #295): two devices of one player can upload the same `saveSeq` (an accepted `SEQ_REGRESSION`, 08 section 3.1 step 4), so the blob key is `saves/{player_id}/{save_seq}-{deviceId8}.sav` (the first 8 lowercase hex digits of the device id; the `0001` comment on `saves.blob_key` shows the earlier `{save_seq}.sav`) and the history key includes `device_id`; neither upload replaces the other. Reads use the stored `blob_key`, so a key written under the old scheme stays readable (only dev data had one). The trim function is replaced to tell rows apart by `(save_seq, device_id)`. Shown verbatim, as its file:

```sql
-- 0007_save_history_device.sql (specs/02 section 12 "Save history per device"; issue #295). Two devices of one player can
-- upload the same saveSeq (an accepted SEQ_REGRESSION upload, specs/08 section 3.1 step 4), so a history entry is keyed by
-- the device too, as its blob key is (saves/{playerId}/{saveSeq}-{deviceId8}.sav), and neither upload replaces the other.
-- The trim still keeps SAVE_HISTORY_KEEP = 10 rows per player, now told apart by (save_seq, device_id).
alter table save_history drop constraint save_history_pkey, add primary key (player_id, save_seq, device_id);

create or replace function save_history_trim() returns trigger language plpgsql as $$
begin
  delete from save_history h
  where h.player_id = new.player_id
    and (h.save_seq, h.device_id) not in (
      select k.save_seq, k.device_id from save_history k
      where k.player_id = new.player_id
      order by k.uploaded_at desc, k.save_seq desc, k.device_id desc
      limit 10);
  return null;
end
$$;
```

**Currency dashboard sources** (`0008`; issue #296): the server writes `currency_changed` rows for its own ledger writes (08 section 3.5) and the client reports the same changes, so `currency_daily` takes each `(currency, reason)` from one source: a server-ledger currency (`DIAMOND`, the four `TICKET_SUMMON_*`) with a server-originated reason (`IAP`, `MAIL`, `ADMIN`, `REFUND`, `SUMMON`; the reasons 08 section 3.1 step 5a excludes from client crediting) from `source = 'server'` rows, every other pair from `source = 'client'` rows. Shown verbatim, as its file:

```sql
-- 0008_currency_daily_sources.sql (specs/02 section 12 "Currency dashboard sources"; issue #296). The server writes its
-- own currency_changed rows (source = 'server') for the ledgers it owns, and the client reports the same changes, so each
-- (currency, reason) is counted from one source only: a server-ledger currency (DIAMOND and the four summon tickets) with a
-- server-originated reason (IAP, MAIL, ADMIN, REFUND, SUMMON) from the server's rows, everything else from the client's.
-- A change only the client sees, such as Rock Candy from MAIL or from a summon's overflow, stays a client row.
drop materialized view currency_daily;
create materialized view currency_daily as                 -- rebuilt nightly for 06's source/sink dashboards
  select player_id_hash, day_index, props->>'currency' as currency, props->>'reason' as reason,
         sum((props->>'delta')::double precision) as sum_delta
  from events
  where name = 'currency_changed'
    and (source = 'server') = (props->>'currency' in ('DIAMOND', 'TICKET_SUMMON_WEAPON', 'TICKET_SUMMON_SKILL',
                                                      'TICKET_SUMMON_COMPANION', 'TICKET_SUMMON_RUNE')
                               and props->>'reason' in ('IAP', 'MAIL', 'ADMIN', 'REFUND', 'SUMMON'))
  group by 1, 2, 3, 4;
```

**Dungeon seed correlation** (`0009`; issue #205): `POST /dungeon/seed` checks a floor against the last uploaded save's `Dungeons.BestFloor`, and `PUT /save` compares each upload's `BestFloor` with the previous upload's (08 sections 3.3 and 7.3). `dungeon_progress` holds, per player and kind, the `BestFloor` of the last upload that carried `Dungeons` (the last upload wins, lower or not), so neither route reads a save blob; it is not rewound by a rollback (08 section 7.4), so after one the unlock check may tolerate up to the rolled-back best until the next upload. Shown verbatim, as its file:

```sql
-- 0009_dungeon_progress.sql (specs/02 section 12 "Dungeon seed correlation"; issue #205): POST /dungeon/seed checks the
-- floor against the last uploaded save's Dungeons.BestFloor, and PUT /save compares each upload's BestFloor with the
-- previous one (specs/08 sections 3.3, 7.3). dungeon_progress keeps, per player and kind, the BestFloor of the last upload
-- that carried Dungeons, so neither reads a save blob. The index serves the daily cap's count and the correlation.
create table dungeon_progress (
  player_id  uuid not null references accounts on delete cascade,
  kind       text not null,                                -- Dungeon name: FISH_COIN | ROCK_CANDY | EXP
  best_floor integer not null default 0,
  save_seq   bigint not null,                              -- the upload it came from
  updated_at timestamptz not null default now(),
  primary key (player_id, kind)
);
create index on dungeon_seeds (player_id, kind, day_key);
```

**Idempotency rows** (as built, issue #194; 08 section 1.1): `status_code = 0` marks a row `in_flight`; any other value is the final HTTP status of the stored response. `response` is a jsonb object `{ "v": 1, "requestSha256", "contentType", "headers", "bodyBase64" }`: `requestSha256` fingerprints method, route and body (a reused id with another fingerprint is refused), and the body is base64 so the replay is byte-identical (jsonb would re-order a JSON body's keys and drop its whitespace); the `in_flight` row holds only `v` and `requestSha256`. `/auth/guest` has no player yet, so its rows use `player_id` = the first 16 bytes of `SHA-256("herocat/idempotency/device/" + lowercase deviceId)` with the RFC 9562 version-8 and variant bits set, which cannot equal a server-made (version 4) `playerId`; the 13.2 delete leaves those rows to the purge. A row older than `IDEMPOTENCY_TTL_H` counts as absent before the hourly purge deletes it.

Redis structures (P1 leaderboards, MVP rate limits and sessions):

| Key | Type | Content | TTL |
|---|---|---|---|
| `lb:stage:{weekKey}` | ZSET | member `playerId`, score `HighestCleared * 1e6 + (1e6 - secondsIntoWeek/60)` so ties favour the earlier clear | 14 days |
| `lb:fish:{weekKey}` | ZSET | score `HighestFish` | 14 days |
| `lb:name:{playerId}` | STRING | display name + avatar for leaderboard rows | 1 day |
| `rl:{route}:{playerId}` | ZSET (accepted-request timestamps) | sliding-window rate-limit log (08 per-endpoint limits); `{route}` is the lowercase method and path below `/v1` (`put/save`), and all of a route's windows share the set, so the TTL is the longest of them | window length |
| `rl:{route}:ip:{ip}` | ZSET (accepted-request timestamps) | the same log per client IP for `auth none` routes (08 section 1.1); an IPv6 address's colons are written `-` | window length |
| `sess:{playerId}` | HASH | last device, last ip hash, last activity | 1 h |
| `summon:lock:{playerId}` | STRING | per-player mutex for the summon handler | 10 s |
| `maint` | STRING | maintenance JSON | none |

As built (issue #154): `HeroCat.Server.Infrastructure.Redis.RedisKeys` holds these keys and TTLs for every handler (a test diffs it against this table). `{playerId}` is the lowercase 36-character GUID, `{weekKey}` the integer week key of 00 section 8 item 7, `{route}` a key segment without spaces or braces.

## 13. Data retention and GDPR/CCPA delete

### 13.1 Retention

| Data | Retention | Mechanism |
|---|---|---|
| Current save blob + `saves` row | While account active | |
| `save_history` | Last `SAVE_HISTORY_KEEP = 10` blobs per player | Trigger on insert (`save_history_trim`, section 12) deletes the oldest; S3 lifecycle removes orphaned keys after 7 days |
| `idempotency` | `IDEMPOTENCY_TTL_H = 24` | Hourly purge job |
| `summon_log`, `diamond_ledger` | 7 years (financial audit; loot-box disclosure disputes) | Partitioned by month; partitions created ahead by `ensure_partitions` (section 12) |
| `ticket_ledger`, `granted_items` | While account active | Deleted with the account (13.2) |
| `store_notifications` | 90 days | Daily purge (dedupe only needs the stores' retry window) |
| `account_exports` | Row 30 days; export object 7 days | Daily purge; S3 lifecycle on the export prefix |
| `purchases` + raw receipts | 7 years | Tax law in several launch regions |
| `dungeon_seeds` | 30 days | Daily purge |
| `dungeon_progress` | While account active | Deleted with the account (13.2) |
| `refresh_tokens` | 60 days from issue or until revoked | |
| Inactive accounts | Purged after `RETENTION_INACTIVE_DAYS = 730` with no login | Monthly job runs the delete flow below; players with any purchase are exempt from auto-purge (the receipts obligation) and instead have the save blob removed only |
| Analytics raw events | 25 months in the warehouse, aggregated tables indefinitely | Warehouse policy |
| Server logs | 30 days hot, 1 year cold | |

### 13.2 Delete flow

```mermaid
sequenceDiagram
    participant U
    participant C as Client
    participant API
    participant Job as Nightly job
    U->>C: SCR_SETTINGS "Delete account" (confirm twice, type DELETE)
    C->>API: POST /account/delete {requestId}
    API->>API: accounts.status = delete_pending, delete_requested_at = now; revoke all refresh_tokens
    API-->>C: {graceUntil}
    C->>C: wipe local saves, tokens, device key; restart to SCR_TITLE
    Note over U,API: within GDPR_DELETE_GRACE_DAYS the player can sign in with a linked platform and cancel (POST /account/delete/cancel)
    Job->>Job: for accounts with delete_requested_at < now - grace: hard delete
```

Hard delete, in one transaction per account (issue #32). The three financial tables (`purchases`, `summon_log`, `diamond_ledger`) reference `accounts` with `on delete restrict`, so a job bug that skips step 2 or 3 fails the transaction instead of destroying financial records.
1. Delete S3 objects `saves/{playerId}/*` and `exports/{playerId}/*`.
2. **Purchaser** (any `purchases` row for the player): insert a tombstone `accounts` row with `player_id = tombstone_id`, where `tombstone_id` is the UUID made of the first 16 bytes of `sha256(player_id || server_pepper)`, `status = 'deleted'`, `display_name = ''`, `avatar_id = 'avatar_default'`, `region = 'ZZ'`, `age_gate = 0` and every other nullable column null (`on conflict do nothing`, so a rerun is harmless). Then `update purchases, summon_log, diamond_ledger set player_id = tombstone_id where player_id = ?`. This keeps referential integrity and retains the financial records without a link to the person: the legal-obligation basis under GDPR art. 17(3)(b).
3. **Non-purchaser**: delete the player's `summon_log` and `diamond_ledger` rows (open question 8 default).
4. `delete from accounts where player_id = ?` cascades to `account_links`, `devices`, `refresh_tokens`, `saves`, `save_history`, `inbox_items`, `banner_pity`, `ticket_ledger`, `granted_items`, `dungeon_seeds`, `dungeon_progress`, `account_exports`, `guild_members`, `moderation_actions`, P1 `coupon_redemptions`; `delete from idempotency where player_id = ?` (no FK).
5. Analytics: publish `player_deleted{playerIdHash}` to the warehouse; the warehouse job removes raw `events` rows for that hash within 30 days.
6. `guild_chat.player_id` is set null by the cascade; message bodies remain (they are the guild's record, and contain no identifier).

Export (`POST /account/export`, job state in `account_exports`): assembles `accounts`, `account_links` (provider only), `devices` (model/os), current save JSON, `purchases`, `inbox_items`, `diamond_ledger`, `ticket_ledger`, `granted_items`, `summon_log`, `moderation_actions` into one JSON file in S3 with a 7-day presigned link mailed to the linked platform email or shown in `SCR_SETTINGS`. Rate limit one per 7 days.

## 14. Analytics envelope, queue and event index

Each feature spec owns its own events' field lists, next to the state and commands they describe (00 decision 96, issue #245). This section owns only the common envelope, naming, the offline queue, the session and save-health events in 14.3, and the index in 14.4. A field list is written once, in its owner's section; every other spec links to it.

### 14.1 Envelope

Added by the sink to every event: `ts` (server seconds), `sessionId` (GUID per process), `playerIdHash` (sha256 of playerId with a per-environment salt; never raw), `deviceId`, `clientVersion`, `contentVersion`, `schemaVersion`, `debugTainted`, `clockAuthoritative`, `dayIndex = (ts - CreatedServerTime) / DAY_SECONDS`, `stage` (current), `ageGate` (0/1, 08 section 10.1). Owner sections do not repeat these fields.

As built (`HeroCat.Runtime.Services.AnalyticsEnvelope`, issue #155): `playerIdHash` is lowercase hex `sha256(UTF8(salt + ":" + playerId))` with the playerId as a 36-character GUID, and `null` for an unlinked local guest (`PlayerId = Guid.Empty`; `deviceId` identifies it). `deviceId` is the install id (8.4), `schemaVersion` is `SAVE_SCHEMA_VERSION`, `contentVersion` the active `ContentDb.Version`. Every field is always present; `dayIndex` and `stage` are `null` for events tracked before the save loads (the save-health events of 8.3). Each queued event also carries `eventId` (32 hex, random), the server's per-event dedupe key. A queued line is `{"name", "eventId", <envelope fields>, "props": {<owner fields>}}`.

Names snake_case; properties camelCase. The sink drops an event whose name is not snake_case and a property that is not camelCase, names an envelope field, or is a raw `playerId`; it never throws. `HeroCat.Runtime.Services.AnalyticsNames` holds every name in the 14.4 index (a test diffs it against the index) and `AnalyticsEvents` the typed events of 14.3 and of the owner sections (`currency_changed` and the rest of 06 section 13, 03 section 11, 04 and 05 section 13), so emitters cannot misspell a name or a field. Economy events (those that carry a currency, purchase, summon, dungeon or inbox payload) go to the backend `events` table (00 section 3) as well as Firebase so they can be joined with `purchases` and `saves`. The `events` table and its nightly `currency_daily` materialized view (06 source/sink dashboards) are defined in section 12 (issue #34).

### 14.2 Offline queue

The sink enqueues on the main thread and flushes on the thread pool (01 threading table). Pending events persist to `analytics/queue.ndjson` (8.4; excluded from OS backup) so they survive a kill while offline. Uploads go in batches of at most `ANALYTICS_BATCH_MAX` events; `OnPause` and `OnQuit` flush (01), and the queue also uploads every `ANALYTICS_UPLOAD_SEC` of foreground time. The queue holds at most `ANALYTICS_QUEUE_MAX` events; past that the oldest are dropped.

As built (`AnalyticsSink`, `AnalyticsQueue`, issue #155):

1. The file is one JSON event per line (14.1), appended per event. It may hold up to `2 * ANALYTICS_QUEUE_MAX` lines between compactions; it is rewritten (temp file, then rename) after each accepted batch and when an overflow finds it that long. A load keeps the newest `ANALYTICS_QUEUE_MAX` lines and skips a line that does not parse (a torn last line after a kill). A file error never throws: the events stay in memory.
2. A flush first raises the sink's flush hook, so in-memory aggregates (06 section 13's `KILL`/`BOSS` window) reach the queue, then uploads on the thread pool, oldest first. Each batch's `X-Request-Id` is the first 32 hex of `sha256` of its events' `eventId`s, so a retry of the same events reuses it and a different set never does.
3. An upload outcome is accepted (the events leave the queue), retry (offline, timeout, 5xx, 429, or the uploader threw: the flush stops and the next flush retries the same batch) or rejected (a non-retryable 4xx: the batch is dropped so it cannot block the queue). A sink with no uploader keeps the events on disk. The backend answers `200` with per-event refusals (accepted), or `400` for a malformed batch (rejected).
4. A Local build without a backend uses `NullAnalytics` and writes no queue file (01 section 1.2); bots and tests use `MemoryAnalytics` (no envelope, no files).
5. The backend leg is `HeroCat.Remote.Services.RemoteAnalyticsUploader` over `ApiClient` (issue #297): one `POST /analytics/events` (08 section 3.5) per batch, `X-Request-Id` = the batch id of item 2, carrying the batch's economy events (14.1) byte for byte as queued, oldest first. Any `2xx` is accepted and the response's per-event `rejected` rows are final (counted, never resent). Offline, timeout, `5xx`, `408`/`425`/`429`, `409` (`IDEMPOTENCY_IN_FLIGHT`), `401`/`403` and `426` are retry, after the ApiClient's own 01 section 5.2 backoff; a client with credentials but no access token (signed out) sends nothing and retries. Any other `4xx` is rejected. Lines that are not economy events, or do not parse, are not sent and leave the queue with their batch; a batch without an economy event sends nothing. The Firebase leg, which gets every event, is not built yet (TODO(#303)), so a Remote build today records only the backend's economy events.
6. Consent and age (08 sections 10.1, 10.2) do not gate the backend leg: economy events are recorded for every player (legitimate interest), with the server stamping `age_gate` from the token's `age` claim. Advertising-id props (`advertisingId`, `adId`, `idfa`, `idfv`, `gaid`, `aaid`) are removed from a line before it is sent, for every player. Firebase's `analytics_storage = denied` mode for `ageGate = 1` and the advertising consent belong to the Firebase leg.
7. Uploads run on `OnPause` and `OnQuit` (a flush: the flush hook of item 2, then the upload) and every `ANALYTICS_UPLOAD_SEC` of foreground time (`SessionRunner`, through `IAnalyticsUpload.Upload`: the upload alone, without the flush hook, so 06 section 13's aggregation windows close only on their own clock, pause and quit). Background time does not count toward the interval.
8. Composition: `RemoteGameServices.Wire` builds the sink (envelope, the queue under `{dataDir}/analytics`, the backend uploader) when `ServicesOptions.AnalyticsSalt` is set (the environment's `playerIdHash` salt, equal to the server's `HeroCat:Analytics:PlayerIdSalt`) and no `Analytics` is passed; `ServicesOptions.SessionId` is the envelope's `sessionId`. Without a salt, analytics stays `NullAnalytics`.

### 14.3 Events owned here (session, save health, cloud, clock, client performance, content updates)

| Event | Properties | Emitted by | Notes |
|---|---|---|---|
| `session_start` | `offlineElapsedSec`, `offlineCredited`, `coldBoot` (bool), `bootMs` | Runtime at `SCR_MAIN` shown, and when a session reopens after a resume or an in-app gap | Every `session_end` has its start (issue #298). Cold boot: `BootFlow` at the handoff, `bootMs` the boot's total. Warm (`coldBoot = false`): `SessionRunner` when the session opens after the catch-up of `OnResume` or of an in-app gap (a resume while the session is still open sends nothing), `bootMs` the runner's catch-up milliseconds. The offline fields are the `Offline.Pending` record after that catch-up (the sum when a reward was already pending), 0 when none. |
| `session_end` | `durationSec`, `stageStart`, `stageEnd`, `kills`, `ticks`, `trigger` (pause/quit/gap) | Runtime on pause/quit | `SessionRunner` (issue #274): a session opens after the catch-up of boot, resume or an in-app gap and ends once, before the analytics flush of `OnPause`/`OnQuit` (a quit right after a pause sends nothing) or at the last frame before an in-app gap. `durationSec` is monotonic foreground time; the stages are `Combat.Stage`; `kills` and `ticks` are the `Stats.Kills` and runner ticks since the open. |
| `cloud_save_uploaded` | `saveSeq`, `sizeBytes`, `trigger`, `latencyMs`, `flags[]` | Runtime | |
| `cloud_conflict` / `cloud_autoresolved` | 9.4 | Runtime | |
| `save_fallback`, `save_corrupt{problems}`, `save_corrupt_all{supportId}`, `save_repaired{rule}`, `save_reject{file, reason}`, `save_write_failed{consecutive}`, `migration_applied{from, to}` | | Runtime | Save health. `save_corrupt_all` is a new profile replacing an unreadable save (8.3; issue #248). |
| `clock_anomaly` | `kind` (backwards/gap/resync), `seconds` | Runtime | Section 11 and 01; also emitted with `kind = backwards` when 06's offline compute sees `ClockWentBackwards`. |
| `player_deleted` | `playerIdHash` | Server | 13.2 step 5. |
| `profile_created` | | Runtime (boot step 4, new profile) | The funnel step only; the `RngRoot` seed (3.4) is never sent (issue #261). |
| `boot_ms` | `totalMs`, `contentMs`, `configMs`, `authMs`, `saveMs`, `catchUpMs`, `handoffMs` | Runtime, once per cold boot | 10 section 5's cold-boot metric with the 01 section 8.1 step breakdown (issue #261). |
| `resume_ms` | `ms` | Runtime, once per warm resume | 10 section 5; emitted with issue #218. |
| `perf_tick` | `p50Ms`, `p95Ms`, `maxMs`, `ticks` | Runtime, sampled once per session (not per tick) | 10 section 5's tick cost; emitted with issue #218. |
| `content_update_failed` | `version` (the offered `content_version`), `code` (`E900`-`E903` or a 09 build rule), `location` (file or row) | Runtime, once per downloaded bundle ruled out (09 section 10.3 step 5) | `ContentUpdater.Failed` with the bundle's first error, raised on the thread pool after the staged copy is discarded; `BootFlow` posts it to the runner's `MainThreadDispatcher` and tracks it there (issue #298). A failed version is not re-downloaded in the session, so it is reported once. 10's client-health alarm reads it. |

### 14.4 Event index

| Event | Owner |
|---|---|
| `stage_cleared`, `boss_failed`, `wall_broken`, `training`, `level_up`, `awakened`, `research_bought` | 03 section 11 |
| `summon`, `summon_reconciled`, `summon_pending`, `summon_resolved`, `item_acquired`, `item_equipped`, `limit_break` | 04 section 13 |
| `fish_release`, `fish_merge`, `fish_discovered`, `fish_discard`, `fish_board_snapshot` | 05 section 13 |
| `currency_changed`, `quest_completed`, `quest_claimed`, `achievement_unlocked`, `dungeon_run`, `offline_catchup`, `offline_claimed`, `day_rollover`, `daily_gift_claimed`, `exchange`, `shop_view`, `iap_initiated`, `iap_completed`, `iap_failed`, `iap_status`, `inbox_claimed` | 06 section 13 |
| `plausibility_flag` | 08 section 7.4 |
| `session_start`, `session_end`, `cloud_save_uploaded`, `cloud_conflict`, `cloud_autoresolved`, `save_*`, `migration_applied`, `clock_anomaly`, `player_deleted`, `profile_created`, `boot_ms`, `resume_ms`, `perf_tick`, `content_update_failed` | 14.3 |

07 defines no events of its own; screen-driven events (`shop_view`) are owned by the feature spec of the screen. A new event is added to its owner's section and to this index in the same change.

## Open questions

1. **Summon streams in Remote-mode saves.** Resolved: kept in every save in every mode (00 decision 14); R4 requires them and the validator ignores their counters in Remote mode (issue #9).
2. **`progressScore` weights** (9.1). Owner: 06. Default: as written; revisit after closed beta conflict telemetry.
3. **Conflict modal replacing a running sim** (9.4). Alternative: defer applying the other device's save to the next cold boot. Owner: 07. Default: replace immediately with the warning line, because deferring makes the modal's choice feel ignored.
4. **`OwnedItem.Overflow` semantics** past level 10. Resolved: no field; a copy past 160 converts to Rock Candy at pull time in `ApplySummonResult` and `Copies` stays at 160 (04 section 5.3, issue #7).
5. **OS backup exclusion** (8.4). Resolved: saves are excluded from OS backup; cloud save, guests included, is the backup (00 decision 27).
6. **`PENDING_STALE_DAYS = 7` vs `IDEMPOTENCY_TTL_H = 24`.** Resolved: a pending summon older than 24 h executes fresh without asking, up to 7 days, since the diamonds were never charged (00 decision 28).
7. **Week start** (`WEEK_EPOCH_OFFSET`). Resolved: Monday 00:00 UTC, `WeekKey = floor((dayKey - 4) / 7)` (00 contract 7, decision 21).
8. **Tombstone retention for non-purchasers' `summon_log`.** Resolved: default taken; a non-purchaser's `summon_log` and `diamond_ledger` rows are deleted with the account, a purchaser's are re-pointed to the tombstone account (13.2), unless legal later says otherwise (issue #32).
9. **Party model** (00 decision 109). Resolved: `Hero` and the loadout's weapon, skills and companions become `Party` with five `PartySlot`s; schema v1 redefined in place, no migration (5). Implementation (sim, runtime, fixtures, golden files) is tracked in issue #309; until it lands the code still has `HeroState`.

## Acceptance criteria

1. Round trip: a `GameState` with every persisted field non-default (including `null` rune slots, a `PartySlot` with a `null` companion, weapon and skill but a non-zero `AwakenRank`, empty collections, a non-null `Pending.Summon`, a non-null `Offline.Pending`, a non-null `Combat.DungeonRun`, a non-empty `Dungeons.RecentRuns`, `Combat.Boss == Walled`, a non-empty `Combat.MissileQueue`) encodes and decodes to a value-equal object (deep equality test) except `Combat.Fight`, which decodes as `null`. `DeepClone` of a state mid-boss (non-null `Fight`) continues tick-for-tick identically (03 AC15).
2. Determinism after round trip: for each stream in 3.4, the next `Derive` after save/load equals the next draw of the un-restored copy.
3. Byte stability: two encodes of value-equal states are byte-identical (sorted sets, round-trip doubles).
4. Header: wrong CRC, `len` off by one in either direction, missing newline, truncated body each raise `SaveCorruptException`; an HMAC mismatch does **not** (loads with `HmacValid = false`).
5. Rotation: after two writes, `slot0.tmp` absent, `slot0.sav` is write 2, `slot0.prev.sav` is write 1, `SaveSeq == 2`.
6. Fallback: a flipped body byte in `slot0.sav` loads `prev` and emits `save_fallback`; both corrupt yields a new profile with two quarantined files, `save_corrupt_all` and the one-time notice, and the next boot shows no notice (issue #248); quarantined files older than `CORRUPT_KEEP_DAYS` are deleted on the next load.
7. Fault injection between the two renames: `slot0.sav` absent, `prev` present, load succeeds from `prev`.
8. `SaveTooNewException` on `SchemaVersion = Current + 1`; no file renamed; `prev` not consulted.
9. Every repair rule P1-P21 and reject rule R2-R5 has a table-driven test asserting the repaired value or the exception and the emitted rule id; `SAVE_REPAIR_MAX + 1` distinct rule hits is a reject.
10. Migrations M1-M7 pass; in particular every `Fixtures/Saves/v{n}.json` migrates to `Current` with zero repairs.
11. Catch-up mutates state only inside `Apply` (issue #6): applying `ComputeOfflineCatchup(now)` for a gap of at least `OFFLINE_MIN_S` leaves every field except `Offline.Pending` and `Time.LastTickServerTime` value-equal (a second gap sums into the pending record, latest `Stage` kept); for a shorter gap it changes only `Fish.TankCount`, `Fish.TankRefillTick` and `Time.LastTickServerTime` (issue #21). Then `ClaimOfflineReward` changes only `Wallet.Gold`, `Party.Exp`/`Party.Level`, `Fish.TankCount`/`TankRefillTick`, `Offline.*`, `Stats.*`. Replaying the command log from the pre-gap save reproduces the same `StateHash`.
12. Conflict rules: a table of `(L, R)` metadata pairs covering each branch of 9.3 produces the expected winner and `Status`; a non-authoritative local stamp 1 h ahead loses to an authoritative remote stamp.
13. Uploads are suspended while `Status == Conflict` is unresolved (no `PUT` observed by a fake API during that window).
14. `ServerClock`: `Observe` makes `IsAuthoritative` true; a device clock change of +1 day between observations changes `NowServerSeconds` by the monotonic elapsed only; before any observation, `NowServerSeconds >= floor`.
15. Backwards clock: `dayKey(now) < DayKey` resets nothing and emits `clock_anomaly`; `now < LastTickServerTime` credits zero.
16. The SQL migration set (section 12; issue #33) applies to an empty PostgreSQL 16 database and creates every table in section 12, including `ticket_ledger`, `granted_items`, `store_notifications`, `account_exports`, `events` and the P1 stubs (issue #53), with section 12's columns, types, nullability, foreign-key delete rules and indexes, the partitions, and the trim trigger (an 11th `save_history` row for a player leaves `SAVE_HISTORY_KEEP`; issue #154). A GDPR hard delete of a player with purchases leaves no row in any table referencing the old `player_id`, and exactly the player's `purchases`, `summon_log` and `diamond_ledger` rows referencing the tombstone account (`status = 'deleted'`); for a player without purchases, nothing remains. Deleting an `accounts` row that still has financial rows fails (`on delete restrict`; issue #32). Runs in CI and in `check.sh` when Postgres is available.
17. Moved to 06 acceptance 25 (`currency_changed` is owned by 06 section 13; issue #245).
18. No `UnityEngine`, `DateTime*.Now`, `Stopwatch` in `HeroCat.Sim`; in `HeroCat.Runtime` only `ServerClock.cs` and `SessionRunner.cs` reference the clock APIs (CI grep: `tools/check_clock_apis.py` in `check.sh`, with a self-test that proves it catches a bad line).
19. IL2CPP build: a save produced by the editor loads on an ARM64 device build and vice versa with zero repairs (device-farm test, 10).
20. New profile (3.5): `ProfileFactory.Create` yields `Party.Slots[i].Companion == TUTORIAL_COMPANION_IDS[i]` for slots 0-4, `Slots[i].Weapon == TUTORIAL_WEAPON_IDS[i]`, one copy of each in its collection, every `Combat.Mp[i] == 0`, a full tank, and validates with zero repairs. A body with root `Hero` and no `Party` is rejected by R3.

## Constants introduced

| Name | Value | Tag | Section |
|---|---|---|---|
| `SAVE_SCHEMA_VERSION` | 1 | I | 2 |
| `SAVE_KEEP_COUNT` | 2 | I | 2 |
| `SAVE_REPAIR_MAX` | 20 | I | 2 |
| `CORRUPT_KEEP_DAYS` | 7 | I | 2 |
| `CONFLICT_KEEP_DAYS` | 30 | S | 2 |
| `CONFLICT_PROGRESS_MARGIN` | 0.10 | S | 9.3 |
| `CLOCK_RESYNC_SEC` | 900 | I | 11 |
| `CLOCK_JUMP_WARN_SEC` | 300 | I | 11 |
| `CLOCK_MAX_SKEW_ACCEPT_S` | 120 | S | 9.2 |
| `HMAC_KEY_BYTES` | 32 | S | 10 |
| `SAVE_BLOB_MAX_BYTES` | 262144 | S | 9.2 |
| `SAVE_HISTORY_KEEP` | 10 | S | 9.2, 13 |
| `PENDING_STALE_DAYS` | 7 | S | 6.4 P17 |
| `IDEMPOTENCY_TTL_H` | 24 | I (from 00 section 8.13) | 12 |
| `GDPR_DELETE_GRACE_DAYS` | 14 | S | 13 |
| `RETENTION_INACTIVE_DAYS` | 730 | S | 13 |
| `ANALYTICS_BATCH_MAX` | 100 | I | 14 |
| `ANALYTICS_QUEUE_MAX` | 10000 | I | 14 |
| `ANALYTICS_UPLOAD_SEC` | 60 | S | 14.2 |
| `DUNGEON_RECENT_RUNS_MAX` | 30 | S | 2, 3.2 |
| `progressScore` weights | 100 / 10 / 1 / 5 / 50 / 2000 / 20 | S | 9.1 |

All are engineering constants (code-resident). None needs the tuning tool; 10's fault-injection, conflict-table and perf tests are the check.

Used from 00 by name: `TICK_HZ`, `KILLS_PER_STAGE`, `BOSS_EVERY`, `BOSS_TIME_TICKS`, `STAGE_CAP`, `TAP_ZAP_MAX_PER_SEC`, `DAY_SECONDS`, `OFFLINE_RATE`, `OFFLINE_CAP_H`, `OFFLINE_MIN_S`, `LOGIN_DIAMONDS`, `TUTORIAL_DIAMONDS`, `DIAMONDS_PER_DAY_F2P`, `PULL_COST[*]`, `PITY_HARD`, `PARTY_SLOTS`, `RUNE_SLOTS`, `DUPE_LVL_CUM`, `LIMIT_BREAK_RANKS`, `BANNER_LVL_EXP`, `SPAWN_DELAY_TICKS`, `FISH_BOARD_W`, `FISH_BOARD_H`, `FISH_MAX_LVL0`, `TANK_CAP0`, `TANK_REFILL_S0`, `AWAKEN_STAGES`. From 01: `AUTOSAVE_INTERVAL_SEC`, `CMDLOG_MAX_BYTES`, `WEEK_EPOCH_OFFSET`, `WALL_CLOCK_SYNC_SEC`, `CLOCK_BACKWARDS_TOLERANCE_S`. From 03 (declared there): `TRAIN_CAP[stat]`. From 04: `TUTORIAL_WEAPON_ID`, `TUTORIAL_COMPANION_IDS`. From 05: `FISH_MAX_LVL_HARD`. From 09: `TUTORIAL_DONE` (row count of `tutorial_steps`).
