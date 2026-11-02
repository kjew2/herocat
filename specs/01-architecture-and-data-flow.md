## 01 - Architecture and Data Flow

Fixes how the four assemblies (`HeroCat.Sim`, `HeroCat.Runtime`, Unity client, `HeroCat.Server`) depend on each other, the `Command -> Simulation -> Event` pipeline every feature goes through, the `SessionRunner` that drives ticks under mobile lifecycle, the threading and network rules, and a sequence diagram plus step list for every end-to-end flow in the game. Other specs describe what a feature does; this one describes which assembly does it, in what order, and what happens when the network or the OS interrupts it.

Tiers covered: M0, MVP, P1 (where a P1 flow changes a seam, it is marked).

Depends on: 00 (sections 3, 4, 8), 02 (save, clock, cloud save), 04 (summon result shape), 06 (offline catch-up, day key), 08 (API contracts).

## 1. Assemblies and dependency rules

### 1.1 Layer diagram

```mermaid
flowchart TB
    subgraph Unity["unity/ Assets/HeroCat.Client (UnityEngine allowed)"]
        Screens["Screens (uGUI prefabs)"]
        VMs["ViewModels"]
        Boot["Bootstrapper (composition root)"]
        Platform["Platform adapters: IAP SDK, Firebase, Addressables"]
    end
    subgraph Runtime["src/HeroCat.Runtime (plain C#, Newtonsoft.Json, System.Net.Http)"]
        Runner["SessionRunner"]
        Save["SaveSerializer, Migrations, LocalSaveStore, CloudSaveClient"]
        Clock["ServerClock, FakeClock"]
        Net["ApiClient, SummonClient, InboxClient, ConfigClient, ReceiptClient, DungeonSeedClient"]
        Local["LocalSummonService, LocalInboxService, LocalConfigService, LocalDungeonSeedService"]
        Content["ContentLoader"]
        Analytics["Analytics sinks"]
    end
    subgraph Sim["src/HeroCat.Sim (netstandard2.1, no UnityEngine/IO/clock/JSON/reflection)"]
        GS["GameState"]
        Simulation["Simulation: Apply(Command), Advance(ticks)"]
        Cmds["Commands"]
        Evts["Events"]
        Rng["Rng streams"]
        Catchup["Catchup"]
        ContentDb["ContentDb"]
    end
    subgraph Server["src/HeroCat.Server (ASP.NET Core 8)"]
        Api["Controllers"]
        Resim["Plausibility re-simulator"]
        SummonSvc["SummonService (authoritative)"]
    end
    subgraph Bots["src/HeroCat.Bots"]
        Harness["Headless harness"]
    end
    Screens --> VMs --> Runner
    Boot --> Runner
    Boot --> Platform
    Runner --> Simulation
    Save --> GS
    Net --> Save
    Local --> Simulation
    Content --> ContentDb
    Api --> Resim --> Simulation
    SummonSvc --> ContentDb
    Harness --> Runner
    Harness --> Clock
```

### 1.2 Dependency rules

| Rule | Enforced by |
|---|---|
| `HeroCat.Sim` references nothing but `netstandard2.1`. No `UnityEngine`, `System.IO`, `System.Net`, `Newtonsoft`, `System.Reflection`, `System.DateTime`/`DateTimeOffset.Now`, `Stopwatch`, `Math.Pow/Exp/Log`. | `HeroCat.Sim.csproj` has no package references; a CI source grep (10) fails the build on any of the banned identifiers. |
| `HeroCat.Runtime` references `HeroCat.Sim` and `Newtonsoft.Json`. No `UnityEngine`. | asmdef in Unity marks it `noEngineReferences: true`; the .NET csproj has no Unity reference. |
| `HeroCat.Client` references `HeroCat.Sim` and `HeroCat.Runtime` as local UPM packages (`com.herocat.sim`, `com.herocat.runtime` in `unity/HeroCatClient/Packages/manifest.json`, both `file:` references to `src/`). | Unity asmdef `references`. |
| Screens never reference `GameState` or `Simulation`. They reference a `ViewModel`. | asmdef split: `HeroCat.Client.Screens` references `HeroCat.Client.ViewModels` only; `ViewModels` references Runtime + Sim. |
| `HeroCat.Server` references `HeroCat.Sim` for `ContentDb`, rate tables and re-simulation. It never references `HeroCat.Runtime` (the server has its own persistence); it compiles Runtime's `Content/ContentLoader.cs` source as a linked file, under the namespace `HeroCat.Server.Content` (the `HEROCAT_SERVER` constant), so content sets load through the one loader (issue #199). | csproj. |
| `HeroCat.Bots` references `HeroCat.Sim` and `HeroCat.Runtime` (for `SessionRunner`, `FakeClock`, `LocalSummonService`). | csproj. |
| Server-sensitive operations go through the seven interfaces of 00 section 8.11 (`IClock`, `ISummonService`, `ISaveStore`, `IInboxService`, `IReceiptValidator`, `IConfigService`, `IDungeonSeedService`; issue #25). Interfaces live in `HeroCat.Runtime.Services`. Local and remote implementations live beside them. The sim never sees any of them. | Code review; `HeroCat.Sim` cannot reference `Runtime`, so the sim cannot see them by construction. |

The composition root (`GameServices.Build`, called by the engine-free `BootFlow` of 8.1 once content is loaded; Unity's `GameRoot` only forwards to it) picks implementations from a build-time `BackendMode`. The record is named `GameServices` because a type `Services` inside the `HeroCat.Runtime.Services` namespace is shadowed by the namespace. Services that read state (`LocalSummonService`, `LocalDungeonSeedService`) get a `Func<GameState>` because they are built before the save loads. The remote clients live in the separate `HeroCat.Remote` assembly (keeps `System.Net.Http` out of bots), which supplies the Remote branch through `ServicesOptions.Remote`:

```csharp
public enum BackendMode { Local, Remote }   // Local = M0 and any build with no server configured

public static GameServices Build(BackendMode mode, ServicesOptions o)   // o: dataDir, content, state accessor, overrides
{
    if (mode == BackendMode.Local)
        return new GameServices
        {
            Clock    = new ServerClock(api: null),                   // serves device clock floored at last save; IsAuthoritative = false
            Summon   = new LocalSummonService(sim, db),              // same algorithm as the server (04, 08); reads state, never writes it
            Saves    = new LocalSaveStore(Path.Combine(dataDir, "saves")),
            Inbox    = new LocalInboxService(),                      // empty inbox, grants from tutorial only
            Receipts = new DisabledReceiptValidator(),
            Config   = new LocalConfigService(),                     // bundled content only
            Dungeons = new LocalDungeonSeedService(sim),             // peeks the next `dungeon` draw; StartDungeon consumes it (8.8)
            Analytics = new NullAnalyticsSink(),                     // no backend: events are dropped, Flush is a no-op
        };
    var api = new ApiClient(opts.BaseUrl, tokenStore);
    return new GameServices
    {
        Clock    = new ServerClock(api),
        Summon   = new SummonClient(api, sim, db),
        Saves    = new CloudSaveClient(new LocalSaveStore(...), api, clock),
        Inbox    = new InboxClient(api),
        Receipts = new ReceiptClient(api),
        Config   = new ConfigClient(api, cacheDir),
        Dungeons = new DungeonSeedClient(api),                       // POST /dungeon/seed
        Analytics = new AnalyticsSinks(api),                         // Firebase + backend economy events (08); flushed in 3.1
    };
}
```

## 2. The Command -> Simulation -> Event pipeline

### 2.1 Base types

```csharp
namespace HeroCat.Sim.Commands { ... }   // block namespaces: the sim is C# 9 (CLAUDE.md), so no file-scoped namespace

public abstract record Command
{
    public long IssuedAtTick { get; init; }      // Simulation.Tick at the moment of Apply; set by Simulation (cmd with { IssuedAtTick = Tick }), not the caller
    internal abstract ApplyResult Execute(Simulation sim);   // the dispatch: each command type carries its handler, so adding one touches one file
}

public abstract record Event                     // namespace HeroCat.Sim.Events
{
    public long Tick { get; internal set; }      // sim tick at emission; stamped by the emitter (C# 9 cannot set an init property after construction)
}

public enum RejectCode                           // category: picks the UI presentation (section 7)
{
    None, CannotAfford, InvalidTarget, Locked, Cooldown, Capped, AlreadyClaimed, NotOwned,
    SlotOccupied, BoardFull, TankEmpty, LevelMismatch, NotAvailable, RateLimited, BadArgument, StaleRequest
}

public readonly struct ApplyResult               // a readonly struct: record structs are C# 10
{
    public bool Ok { get; }  public RejectCode Code { get; }  public string Reason { get; }   // Reason null when Ok
    public static readonly ApplyResult Accepted = new ApplyResult(true, RejectCode.None, null);
    public static ApplyResult Reject(RejectCode c, string reason = null) => new ApplyResult(false, c, reason);
}

}
```

A reject is two-level (issue #11): `Code` is the category from this enum, and `Reason` is a stable key from the owning spec's closed reason list (for example 06's `DUNGEON_FLOOR_NOT_UNLOCKED` under `Locked`, 03's `STAT_AT_CAP` under `Capped`), localized as `reject.<reason_lower>` (the reason stays UPPER_SNAKE in `ApplyResult.Reason`; the client lowercases it to build the key, so `STAT_AT_CAP` shows `reject.stat_at_cap`; a reject without a reason uses the snake-cased `RejectCode`, `reject.cannot_afford`; issue #248). 01 owns the type; each owning spec owns its reason list, and its tests assert both values.

Concrete commands (`VerbNoun`, 00 section 5.1) and the events they emit. Full field lists are in the owning spec; this table fixes the inventory and the owning assembly path.

| Command | Fields | Owner | Emits on success |
|---|---|---|---|
| `TrainStat` | `Stat stat, int count` (1/10/100) | 03 | `StatTrained`, `CurrencyChanged(GOLD, TRAINING)` |
| `TrainStatMax` | `Stat stat` | 03 | `StatTrained`, `CurrencyChanged(GOLD, TRAINING)` |
| `EquipWeapon` | `string weaponId` | 04 | `ItemEquipped` |
| `EquipSkill` | `int slot, string skillId` | 04 | `ItemEquipped` |
| `UnequipSkill` | `int slot` | 04 | `ItemEquipped` (slot cleared) |
| `EquipCompanion` | `int slot, string companionId` | 04 | `ItemEquipped` |
| `UnequipCompanion` | `int slot` | 04 | `ItemEquipped` (slot cleared) |
| `AutoEquipBestWeapon`, `AutoEquipBestSkills`, `AutoEquipBestCompanions` | none | 04 | `ItemEquipped` per changed slot |
| `LimitBreak` | `string itemId` | 04 | `ItemLimitBroken`, `CurrencyChanged(ROCK_CANDY, LIMIT_BREAK)` |
| `ApplySummonResult` | `SummonResultPayload payload` (the service output, 2.5) | 04 | `ItemAcquired` x N, `CurrencyChanged(..., SUMMON)`, `BannerLeveledUp?` |
| `ReleaseFish` | none | 05 | `FishReleased`, `FishDiscovered?` |
| `ReleaseMax` | none | 05 | one `FishReleased` per fish |
| `MergeFish` | `int fromCell, int toCell` | 05 | `FishMerged` (carries the missile size), `FishDiscovered?`, `CurrencyChanged(FISH_COIN)` |
| `MergeAll` | none | 05 | one `FishMerged` per merge |
| `MoveFish` | `int source, int target` | 05 | `FishMoved` |
| `DiscardFish` | `int cell` | 05 | `FishDiscarded`, `CurrencyChanged(FISH_COIN, FISH_MERGE)` |
| `TapZap` | `int count` | 03 | `TapZapped`; taps above `TAP_ZAP_MAX_PER_SEC` are dropped |
| `AttemptBoss` | none | 03 | `EnemySpawned` (the boss) |
| `SelectStage` | `int? stage` (farm target `<= HighestCleared`, any completed stage per 02; null returns to the frontier; `Combat.Stage` stays the frontier and keeps its kills and boss phase, issue #35) | 03 | `StageChanged` |
| `ClaimOfflineReward` | none | 06 | `OfflineRewardClaimed`, `CurrencyChanged(GOLD, OFFLINE)`, `CurrencyChanged(HERO_EXP, OFFLINE)` |
| `StartDungeon` | `Dungeon kind, int floor, ulong seed, string grantId, bool localSeed` | 06 | `DungeonStarted` |
| `SweepDungeon` | `Dungeon kind` | 06 | `DungeonSwept`, `CurrencyChanged(..., DUNGEON)` |
| `ClaimQuest` | `string questId` | 06 | `QuestClaimed`, `CurrencyChanged(..., QUEST)` |
| `ClaimAllQuests` | `QuestKind kind` | 06 | one `QuestClaimed` per quest |
| `ClaimDailyGift` | none | 06 | `DailyGiftClaimed`, `CurrencyChanged(TICKET_SUMMON_WEAPON, DAILY_GIFT)` |
| `ExchangeItem` | `string offerId, int qty` | 06 | `ItemExchanged`, `CurrencyChanged(DIAMOND, SHOP_EXCHANGE)`, `CurrencyChanged(TICKET_*, SHOP_EXCHANGE)` |
| `Awaken` | none | 03 | `Awakened`, `CurrencyChanged(ROCK_CANDY, AWAKENING)` |
| `Research` | `string nodeId` | 03 | `ResearchLeveled`, `CurrencyChanged(FISH_COIN, RESEARCH)` |
| `ApplyInboxGrant` | `InboxGrantPayload grant` | 06/08 | `CurrencyChanged(..., IAP or MAIL)`, `ItemAcquired?` |
| `ApplyServerTime` | `long serverSeconds` | 02/06 | `DayRolledOver?`, `WeekRolledOver?` (re-anchors the sim's virtual server clock, 3.1) |
| `ComputeOfflineCatchup` | `long nowServerSeconds` | 06 | `OfflineRewardComputed?` (stores or sums into `Offline.Pending`, stamps `LastTickServerTime`; 8.7) |
| `SetTutorialStep` | `int step` | 07 | `TutorialStepChanged` |
| `SetAutoMerge` (P1) | `bool on` | 05 | per 05 section 6 |

P1 commands not listed here (`CastSkill(slot)`, 04 section 7.2) follow the same rules; their rows live in the owning spec. `RetreatFromBoss` is not in MVP (issue #11): a boss attempt ends by kill or timer.

System commands (`ApplySummonResult`, `ApplyInboxGrant`, `ApplyServerTime`, `ComputeOfflineCatchup`) are issued by `HeroCat.Runtime`, never by a screen. They exist so that a server result, a clock re-anchor or an offline catch-up enters the sim through the same validated door as a tap, which is what lets the re-simulator replay a command log that includes them. No Runtime code writes `GameState` directly (issue #6).

### 2.2 `Simulation` surface

```csharp
public sealed class Simulation
{
    public GameState State { get; }               // read by Runtime for saving; screens never see it
    public ContentDb Db { get; }
    public long Tick => State.Time.Tick;

    public Simulation(GameState state, ContentDb db);

    public ApplyResult Apply(Command cmd);        // validate -> mutate -> emit. Never throws for player input.
    public void Advance(int ticks);               // runs Tick() `ticks` times. 03 owns the body.
    public IReadOnlyList<Event> DrainEvents();    // moves the buffer out; caller owns the list
    public Snapshot GetSnapshot();                // 2.4; allocation-free after warm-up
}
```

Rules:

1. `Apply` is the only mutation entry point besides `Advance`, including for offline catch-up and the summon debit (issue #6). Every branch of `Apply` either returns a reject with no state change, or mutates and emits. A reject never emits an `Event`; the caller reports it through the UI (section 7).
2. Validation is total: `Apply` on a command with an unknown id, a negative slot, or a stage past `STAGE_CAP` returns `BadArgument`, it never throws. Throwing is reserved for programmer errors (null command, `ContentDb` missing a table), which are bugs and should crash in development.
3. `Apply` is synchronous and completes within one tick. An intent that needs the server (00's `Summon(banner, count, payment, requestId)`) is never passed to `Apply`; it is a `Runtime` flow (`SummonFlow`, 8.5) that ends with a system command.
4. Commands, system commands included, are recorded in the command log (2.6) before they are applied so the log can be replayed.

### 2.3 Event buffering

`Simulation` appends to a `List<Event>` with capacity `EVENT_BUFFER_MAX = 4096`. If a single `Advance` exceeds it (a 10 s catch-up at `MAX_TICKS_PER_FRAME` on a high-DPS hero can kill a few hundred mobs), the sim coalesces per tick: repeated `EnemyKilled` and `CurrencyChanged(KILL)` in the same `Advance` call are summed into `EnemyKilledBatch(count, goldTotal, expTotal, fromStage, toStage, killGold, killExp)` once the buffer passes `EVENT_COALESCE_AT = 1024`. `goldTotal`/`expTotal` sum the folded kills (boss kills included, whose `CurrencyChanged(BOSS)` is not folded); `killGold`/`killExp` sum only the folded `CurrencyChanged(KILL)` amounts, so 06 section 13's `currency_changed` stays exact (issue #155). Coalescing is deterministic (it depends only on counts) and the bot harness turns it on permanently to keep memory flat.

`SessionRunner` drains the buffer once per frame after ticking and dispatches to the `EventBus`:

```csharp
public sealed class EventBus
{
    readonly Dictionary<Type, List<Delegate>> subs = new();
    public IDisposable Subscribe<T>(Action<T> handler) where T : Event;
    public void Publish(IReadOnlyList<Event> events);   // main thread only; handlers must not call Apply re-entrantly (they queue a command instead)
}
```

Handlers run in subscription order on the main thread. A handler that wants to issue a command calls `runner.Enqueue(cmd)`, which is applied at the start of the next frame, so event handling never nests a mutation inside a drain.

### 2.4 Snapshot for HUD polling

00 section 8.8 allows the combat viewport and HUD counters to poll. They poll a struct the sim refreshes once per tick, not `GameState`:

```csharp
public readonly struct Snapshot
{
    public long   Tick;
    public int    Stage, KillsInStage, Chapter;     // Stage is the frontier (02 `Combat.Stage`)
    public int?   SelectedStage;                     // 02 `Combat.Selected`: the side stage being farmed, null = frontier (issue #254)
    public BossPhase BossPhase; public int BossTicksLeft;   // 02 `Combat.BossPhase` (None/Pending/Walled), issue #7
    public double EnemyHp, EnemyHpMax;
    public double HeroDps;                      // the 00 section 7 formula evaluated this tick
    public double Gold, FishCoin, HeroExp, HeroExpToNext;
    public long   Diamonds, RockCandy;               // counted currencies are long (00 contract 9)
    public int    HeroLevel, AwakenRank;
    public double Mp, MpMax;
    public int    TankFish, TankCap; public int TankRefillTicksLeft;
    public int    PendingInboxCount;             // set by Runtime, not sim; see 2.7
    public ReadOnlySpan<DamageNumber> DamageNumbersThisTick => ...;   // up to DMG_NUMBERS_PER_TICK_MAX = 16
}
```

The snapshot is a value copy taken at the end of each `Advance`; between ticks it does not change, so a 60 fps HUD reads the same struct six times per tick and that is fine. Nothing in the snapshot is a reference into `GameState`.

### 2.5 Result payloads from services

Services (summon, inbox, dungeon seed) return payloads that become system commands. The shapes are fixed here so 04, 06 and 08 agree:

```csharp
public sealed record SummonResultPayload(
    string RequestId,                 // client-generated GUID "N" format, 32 hex chars
    Banner Banner,
    int    Count,                     // 1 or 10
    SummonPull[] Pulls,               // length == Count
    int    PityAfter,                 // pulls since last L+ after this batch
    int    BannerLevelAfter, int BannerExpAfter,
    CurrencyDelta[] Debits,           // what the pull costs: DIAMOND or a ticket; ApplySummonResult performs the debit
    CurrencyDelta[]? BalancesAfter,   // Remote: the server's balances for the debited currencies; null in Local
    long?  StreamCounterAfter,        // Local: summon_<banner>.Counter after the pull; null in Remote
    long   ServerTimeSec);

public sealed record SummonPull(string ItemId, Rarity Rarity, bool IsNew, int LevelBefore, int LevelAfter,
    int CopiesAfter,                  // capped at 160 (04 section 5.3)
    long OverflowRockCandy,           // Rock Candy from a copy past the cap, else 0
    bool GuaranteeApplied);

public sealed record InboxGrantPayload(string InboxItemId, GrantLine[] Lines, CurrencyReason Reason);   // Reason in {IAP, MAIL, REFUND, ADMIN}
public sealed record GrantLine(string Kind, string Id, long Amount);   // Kind: "currency" | "item"
```

This payload is the only summon contract (issue #13); 04 and 08 use it. `ApplySummonResult` is the only summon mutator in both modes (issue #6): it adds items and Rock Candy overflow, sets pity and banner level/EXP, and performs the debit. In Local mode it rejects `CannotAfford` if `Debits` are not covered, subtracts them, and sets `summon_<banner>.Counter = StreamCounterAfter` (so 00 contract 2's `Counter == 3 * TotalPulls` holds and a replay of the logged command is exact). In Remote mode it applies `Debits`, then replaces those balances with `BalancesAfter`, because the server ledger wins (00 section 4); if the local balance minus `Debits` disagreed with the server balance, Runtime emits the `summon_reconciled` analytics event (04 section 13).

### 2.6 Command log

`HeroCat.Runtime.CommandLog` appends every applied command, system commands included, as one JSONL line `{tick, type, fields...}` to `saves/cmdlog.jsonl`, rotating at `CMDLOG_MAX_BYTES = 1 MiB` (oldest dropped). It is uploaded with the save in MVP (02 section 9.2) only when the server asks for it (08 anti-cheat spot check), never routinely. The bot harness keeps it in memory and uses it for the golden-file determinism test (10); the golden command log uses the same JSONL format, so one parser serves runtime upload, server re-simulation and golden replay (issue #28).

### 2.7 What is not in the sim

`PendingInboxCount`, network state, download progress, and the tutorial's "has seen screen X" flags are Runtime or client state. Coach marks, new-item badges, sort/filter/step prefs and settings toggles live in the Runtime `ClientPrefs` store, a local JSON file next to the save that is never cloud-synced and never in `GameState` (issue #47). They are injected into the snapshot or view-models by Runtime after the sim produced its snapshot. The rule: if the server or the OS is the source of truth for a value, it is not in `GameState`.

## 3. `SessionRunner`

### 3.1 Responsibilities

Lives in `HeroCat.Runtime`. Owns one `Simulation`, drives ticks from a monotonic clock, applies lifecycle transitions, schedules autosaves, and drains events. The Unity side forwards four callbacks and nothing else. Every command, player or system, goes through `Issue`, so the command log holds exactly what was applied.

Server time reaches the sim only through logged `ApplyServerTime` commands at sync points: boot, resume, gap recovery and every `WALL_CLOCK_SYNC_SEC = 60` of foreground time (issue #5). Between them the sim advances a virtual server clock with its ticks (`anchor + (Tick - anchorTick) / TICK_HZ`), so a day rollover happens on a tick and is a pure function of the logged inputs (06). A re-anchor whose server time is behind the virtual clock by at most `CLOCK_BACKWARDS_TOLERANCE_S = 5` is resync jitter and emits nothing; only a larger gap emits `ClockWentBackwards` (and Runtime's `clock_anomaly{backwards}`, the same rule for a resume behind `LastTickServerTime`; issue #246). The constant is a content constant (09 section 5.1) because the sim reads it.

```csharp
public sealed class SessionRunner
{
    readonly Simulation sim; readonly GameServices svc; readonly EventBus bus; readonly CommandLog log;
    readonly double tickDt;                   // 1.0 / TICK_HZ
    double acc; long lastMono; long lastSaveMono; long lastClockSyncMono; bool dirty; int saveFailures;
    readonly Queue<Command> pending = new();

    public SessionRunner(Simulation sim, GameServices svc, EventBus bus, CommandLog log);

    public void Enqueue(Command c) => pending.Enqueue(c);

    public void OnFrame()
    {
        long mono = MonoClock.Ticks();  double dt = (mono - lastMono) / (double)MonoClock.Frequency;  lastMono = mono;

        if (dt > IN_APP_GAP_SEC) { ResumeFromGap(); acc = 0; return; }    // frozen without a pause callback (debugger, OS freeze)

        if (svc.Clock is IClockResync c && c.ResyncDue) _ = c.SyncAsync();   // CLOCK_RESYNC_SEC without traffic (02 11.1)

        while (pending.Count > 0) Issue(pending.Dequeue());

        acc = Math.Min(acc + dt, IN_APP_GAP_SEC);
        int n = 0;
        while (acc >= tickDt && n < MAX_TICKS_PER_FRAME) { sim.Advance(1); acc -= tickDt; n++; }
        if (n > 0) dirty = true;

        if ((mono - lastClockSyncMono) / (double)MonoClock.Frequency >= WALL_CLOCK_SYNC_SEC)
        {   // re-anchor the sim's virtual server clock; between syncs the sim derives server time from ticks (issue #5)
            Issue(new ApplyServerTime(svc.Clock.NowServerSeconds)); lastClockSyncMono = mono;
        }
        bus.Publish(sim.DrainEvents());

        if (dirty && (mono - lastSaveMono) / (double)MonoClock.Frequency >= AUTOSAVE_INTERVAL_SEC) SaveNow(SaveTrigger.Periodic);
    }

    public void OnPause()  { SaveNow(SaveTrigger.Pause); svc.Analytics.Flush(); }
    public void OnResume() { ResumeFromGap(); }
    public void OnQuit()   { SaveNow(SaveTrigger.Quit);  svc.Analytics.Flush(); }

    void Issue(Command c)                                              // the one path into the sim: log, then apply
    {
        log.Append(c); var r = sim.Apply(c); if (!r.Ok) bus.PublishReject(c, r);
        else bus.PublishSystem(new CommandApplied(c, eventsOfThisCommand));   // runtime notice: analytics groups per-command rows (05/06 section 13)
    }

    void ResumeFromGap()
    {
        long now = svc.Clock.NowServerSeconds;                         // the extrapolated time (8.2 step 1)
        Issue(new ApplyServerTime(now));                               // re-anchor; day/week rollover first
        Issue(new ComputeOfflineCatchup(now));                         // 06 algorithm inside Apply; stores or sums into Offline.Pending
                                                                       // and stamps LastTickServerTime; does NOT credit yet (8.7)
        bus.Publish(sim.DrainEvents());
        SaveNow(SaveTrigger.Resume);
        lastMono = MonoClock.Ticks(); lastClockSyncMono = lastMono;
        _ = svc.Clock.SyncAsync();                                     // fire and forget, never throws; the result lands on a later frame
    }

    void SaveNow(SaveTrigger t)
    {
        try { svc.Saves.Save(sim.State, t); dirty = false; saveFailures = 0; lastSaveMono = MonoClock.Ticks(); }
        catch (SaveWriteException) { if (++saveFailures == SAVE_FAIL_WARN_COUNT) bus.PublishSystem(new SaveWriteFailed(saveFailures)); }
    }
}
```

### 3.2 Tick budget

| Constant | Value | Meaning |
|---|---|---|
| `MAX_TICKS_PER_FRAME` | 100 | At most 10 s of sim per frame. A 25 s hitch is caught up over 3 frames. |
| `IN_APP_GAP_SEC` | 30 | A frame gap above this is treated as an offline interval and goes through `Catchup`, not ticks. |
| `AUTOSAVE_INTERVAL_SEC` | 30 | Periodic save while dirty. |
| `WALL_CLOCK_SYNC_SEC` | 60 | Foreground cadence of the logged `ApplyServerTime` re-anchor (3.1). |
| `CLOCK_BACKWARDS_TOLERANCE_S` | 5 | A server time behind the virtual clock (or behind `LastTickServerTime` at resume) by at most this is jitter: no `ClockWentBackwards`, no `clock_anomaly` (3.1; issue #246). Content constant, read by the sim. |
| `TICK_BUDGET_MS` | 8 | Soft budget: if the tick loop has already used this much wall time this frame, stop at the current `n` even below `MAX_TICKS_PER_FRAME`. Keeps 30 fps on 2 GB devices during catch-up bursts. The remainder stays in `acc`. |

Tick cost target: one `Advance(1)` under 0.1 ms on a 2019 mid-range Android (Snapdragon 665 class) with zero GC allocation (10 perf budget). The fish board, skills and companions all run inside the same tick.

### 3.3 Lifecycle mapping

| Platform event | iOS | Android | Runner call |
|---|---|---|---|
| App goes to background | `applicationDidEnterBackground` -> Unity `OnApplicationPause(true)` | `onPause`/`onStop` -> `OnApplicationPause(true)` | `OnPause()` |
| App returns | `applicationWillEnterForeground` -> `OnApplicationPause(false)` | `onResume` -> `OnApplicationPause(false)` | `OnResume()` |
| App killed by user or OS | No callback guaranteed | No callback guaranteed | None. Last save stands (at most `AUTOSAVE_INTERVAL_SEC` lost, 02 section 8). |
| App quit via back button (Android) | n/a | `OnApplicationQuit` | `OnQuit()` |
| Low memory | `didReceiveMemoryWarning` -> `Application.lowMemory` | `onTrimMemory` -> `Application.lowMemory` | `SaveNow(Pause)` then client unloads Addressables for non-visible screens. |
| Focus lost but visible (split screen, notification shade, incoming call) | `OnApplicationFocus(false)` | `OnApplicationFocus(false)` | Nothing. Ticks continue. The sim keeps running while visible. |
| Screen locked | `OnApplicationPause(true)` | `OnApplicationPause(true)` | `OnPause()` |

Unity's `OnApplicationPause(true)` runs before the process is suspended; the save must complete synchronously inside it (02 section 8.1). Budget: under 50 ms for serialize + write of a 20-40 KB save.

The client requests `Screen.sleepTimeout = NeverSleep` while on `SCR_MAIN` (idle-on-desk mode, 00 pillar 1) and `SystemSetting` elsewhere.

## 4. Threading model

| Work | Thread | Why |
|---|---|---|
| `Simulation.Apply`, `Advance`, `DrainEvents`, `GetSnapshot` | Unity main thread only | The sim is not thread-safe and does not need to be; a tick is well under a millisecond. |
| `EventBus.Publish`, view-model updates, uGUI | Main thread | Unity requirement. |
| Save serialize (`SaveSerializer.Encode`) | Main thread | It is the snapshot; must see a consistent state. ~2 ms for 40 KB. |
| Save file write (`LocalSaveStore`) | Main thread on `Pause`/`Quit` (must finish before suspension); otherwise main thread too in MVP | Measured under 10 ms on flash; not worth a worker until profiling says so. |
| HTTP (`ApiClient`) | `async`/`await` on the thread pool via `HttpClient`; continuations marshalled back to main thread by Unity's `SynchronizationContext` | IO never blocks a frame. |
| Cloud save upload | Thread pool; reads an already-encoded `byte[]`, never `GameState` | No shared mutable state crosses threads. |
| Content bundle download and hash check | Thread pool, then an atomic swap on main thread at a safe point (section 8.12) | |
| Analytics sink | Main thread enqueue, thread-pool flush | |
| Bot harness | One thread per bot, each with its own `Simulation` and `FakeClock` | Sim instances share nothing but the immutable `ContentDb`. |

Rule: nothing off the main thread holds a reference to `GameState` or `Simulation`. Background work receives copies (`byte[]`, records) and returns results that are turned into commands on the main thread.

`ContentDb` is immutable after construction and may be read from any thread.

## 5. Network layer

### 5.1 `ApiClient`

Route paths in this spec are relative to 08's `/v1` base URL (08 section 1.1), as in 02 and 08.

```csharp
public sealed class ApiClient
{
    readonly HttpClient http;             // one instance per process; base address https://api.<env>.whiskerhero.example
    readonly ITokenStore tokens;          // access + refresh JWT in platform secure storage (Keychain / EncryptedSharedPreferences)
    readonly string clientVersion;        // "1.4.2+318" semver + build number
    readonly string contentVersion;       // active ContentDb version

    public Task<ApiResponse<T>> SendAsync<T>(ApiRequest req, CancellationToken ct);
}

public sealed record ApiRequest(HttpMethod Method, string Path, object? Body, bool Idempotent, string? RequestId = null, bool RequiresAuth = true);

public sealed record ApiResponse<T>(bool Ok, int Status, T? Body, ApiError? Error, long ServerTimeSec);
public sealed record ApiError(string Code, string Message, int? RetryAfterSec);   // 08 body `retryAfterSec`, else the `Retry-After` header (issue #30)
public enum ErrorClass { Ok, Offline, Timeout, ServerError, Retryable, AuthExpired, Rejected, Conflict, Maintenance, Fatal }
```

Headers on every request:

| Header | Value |
|---|---|
| `Authorization` | `Bearer <accessJwt>` when `RequiresAuth` |
| `X-Request-Id` | the client-generated `requestId` for mutating requests (00 section 8.13); a fresh GUID for reads (tracing only) |
| `X-Client-Version` | `1.4.2+318` |
| `X-Content-Version` | `2026.11.03` |
| `X-Device-Id` | install GUID (02) |
| `Accept-Language` | device locale |

Every response carries `X-Server-Time` (unix seconds). `ApiClient` forwards it to `ServerClock.Observe(serverTime, rttMs)` so the clock resyncs on every call for free.

As built (issue #143): `HeroCat.Remote.Api.ApiClient` over one `HttpClient` whose innermost `HttpMessageHandler` is injectable (production: the platform handler; tests: `FakeApiHandler`, a fault-injecting fake of 08's pipeline with its idempotency replay; a build without a deployed server: `OfflineHandler`). `ErrorClass` is Runtime's (`HeroCat.Runtime.Errors`, issue #69). `ApiError` also carries the classifying `ErrorRule` and the body's `details`, and its `Code` is the body's wire code when present, else the row's client code (`NET_OFFLINE`, `NET_TIMEOUT`, `HTTP_<status>`). `ApiResponse<T>` also carries the `RequestId` sent and the attempt count; `EnsureOk()` / `ToServiceException()` turn a failure into the `ServiceException` the seams throw. `SendAsync` never throws for a failed call, only for the caller's cancellation. A mutation (any method but GET/HEAD) without the caller's 32-hex `requestId` is a programming error (`ArgumentException`); a read gets one fresh GUID per call, shared by its retries. `ApiRequest` adds `Timeout` (per attempt, default `HTTP_TIMEOUT_S`), `NoRetry` (used by `GET /time`, whose caller has its own cadence; a 401 `TOKEN_EXPIRED` still refreshes) and extra `Headers` (`If-Match`, `X-Save-Meta`). The observer is Runtime's `IServerTimeObserver`, which `ServerClock` implements; the round trip comes from the injectable `IMonoClock`. `IApiCredentials` (access token plus `RefreshAsync`, implemented by the AuthClient of issue #148) is refreshed single-flight: a 401 that arrives while a refresh runs joins it, and one for a token already replaced replays without refreshing, because refreshing a rotated token again would be `TOKEN_REUSED`. The whole exchange starts on the thread pool (`Task.Run`, then `ConfigureAwait(false)`), so serialization, IO, parsing and backoff never run on the main thread; the caller's own `await` resumes on its context (section 4).

### 5.2 Retry and backoff

Responses are classified by `(status, code)`, code first (issue #30). 08's wire codes are canonical. A known code decides the class, the retry and the client action; an unknown code falls back to its status rule. This one table (`ErrorClassifier.Table` in `HeroCat.Runtime`) drives `ApiClient` and the localization validator (acceptance 14); flows cite a code's row instead of re-deriving the rule.

| Status | Code | Class | Retry | Client action |
|---|---|---|---|---|
| n/a | (DNS failure, `HttpRequestException`) | `Offline` | Only if `Idempotent` | `NET_OFFLINE`. |
| n/a | (timeout, `HTTP_TIMEOUT_S = 10`) | `Timeout` | Only if `Idempotent` | `NET_TIMEOUT`. |
| 401 | `TOKEN_EXPIRED` | `AuthExpired` | Refresh token once (`POST /auth/refresh`), replay the request once | A second 401 -> `SCR_TITLE`. |
| 401 | `UNAUTHENTICATED`, `TOKEN_REVOKED`, `TOKEN_REUSED` | `AuthExpired` | No | Clear tokens, `SCR_TITLE`. |
| 402 | `INSUFFICIENT_FUNDS` | `Rejected` | No | Refresh balances; modal. |
| 403 | `ACCOUNT_BANNED` | `Fatal` | No | Full screen with the appeal url from the body. |
| 409 | `IDEMPOTENCY_IN_FLIGHT` | `Retryable` | After `retryAfterSec`, same `requestId` | None visible. |
| 409 | `PAYMENT_MISMATCH` | `Rejected` (silent) | No | Refresh balances and re-quote (8.5.2 step 1); nothing charged. |
| 409 | `SAVE_CONFLICT` | `Conflict` | No | Resolve per 02 section 9.3 (8.10). |
| 409 | `SAVE_STALE` | `Rejected` (silent) | No | Drop that blob; the latest local save uploads at the next trigger. |
| 409 | `SAVE_ROLLED_BACK` | `Conflict` | No | Download the server save (`GET /save`), replace local, show the rollback notice (08 section 7). |
| 409 | `CONTENT_TOO_OLD` | `Rejected` | No | Modal "Update ready, restart to continue"; the newer bundle applies at cold boot (8.12). |
| 409 | `INBOX_ALREADY_CLAIMED` | `Ok` (success-equivalent) | No | Apply the carried grants if the id is not in `Inbox.ClaimedIds` (8.9 step 6). |
| 409 | `INBOX_NOT_AVAILABLE` | `Rejected` (silent) | No | Scheduled portion not yet available (08 section 3.2, issue #51); refresh the inbox list. |
| 409 | `PROVIDER_ALREADY_LINKED` | `Rejected` | No | Account choice flow (8.13 step 4). |
| 409 | `BANNER_CLOSED`, `SKU_NOT_AVAILABLE` | `Rejected` | No | Modal. |
| 426 | `CLIENT_TOO_OLD` | `Rejected` | No | Update-only block with store link. |
| 429 | `RATE_LIMITED` | `Retryable` | Honour `retryAfterSec`, else backoff | None visible until retries are exhausted. |
| 429 | `DUNGEON_SEED_CAP`, `EXPORT_RATE_LIMITED` | `Rejected` | No | Modal. |
| 503 | `MAINTENANCE` | `Maintenance` | Poll `/config` every `MAINT_POLL_S` | Block screen (section 7). |
| 503 | `SUMMON_DISABLED` | `Rejected` | No | Re-fetch config; the kill switch disables the button (8.12). |
| 503 | `STORE_UNAVAILABLE` | `Retryable` | Keep the pending receipt; `RECEIPT_RETRY_S` | None visible (8.9 step 4). |
| 400 | `RECEIPT_INVALID` | `Rejected` | No | 8.9 step 4. |
| 408, 425, 429, 502, 503, 504 | any other | `Retryable` | Honour `retryAfterSec` / `Retry-After` if present, else backoff schedule | |
| 500 | any | `ServerError` | Once | `SRV_5XX` modal. |
| other 4xx | any other | `Rejected` | Never | Modal with the body's `code`. |

Backoff: `HTTP_RETRY_BASE_MS = 1000`, doubling, full jitter (`delay = random(0, min(HTTP_RETRY_CAP_MS = 8000, base * 2^attempt))`), `HTTP_RETRY_MAX = 4` attempts. Total worst case about 15 s, which is also the summon "pending" threshold (section 8.5). As built (issue #143): `attempt` is the 0-based retry index and `HTTP_RETRY_MAX` counts retries after the first send, so a request goes out at most 5 times and the backoff waits sum to at most 1 + 2 + 4 + 8 = 15 s. All retry kinds of one call (offline, timeout, `Backoff` rows, the single 500 retry) share that budget; a `retryAfterSec` / `Retry-After` wait replaces the jittered delay for that retry; the 401 refresh-and-replay is outside the budget and happens once. An HTTP-date `Retry-After` falls back to the backoff schedule.

A mutating request is retried **only with the same `requestId`**. The server replays the stored result (00 section 8.13), so a retry after a timeout can never double-charge. The client treats a replayed 200 identically to a first 200.

### 5.3 Connectivity state

`NetworkMonitor` (client, wraps `Application.internetReachability` plus the last `ApiClient` outcome) publishes `ConnectivityChanged(online: bool)`. Screens use it to grey out server-only buttons (summon in Remote mode, shop, inbox refresh) with the `NET_OFFLINE` message. It is advisory: the real test is the request.

As built (issue #143): `HeroCat.Remote.Api.NetworkMonitor` over an `IReachability` (the client's `UnityReachability` adapter reads `Application.internetReachability`) and the `ApiClient.ServerReached` outcome. The last outcome since reachability last changed decides: any HTTP response means online, a connection failure offline, a timeout nothing; with no outcome yet, reachability decides. A reachability change in either direction forgets the outcome. The client raises outcomes on the thread pool; `Poll()`, called on the main thread once per frame, is the only place `ConnectivityChanged` is raised.

## 6. Offline-mode rules (what works without network)

| Area | Local mode (M0) | Remote mode, offline (MVP) | Rule |
|---|---|---|---|
| Combat, stages, training, hero level, awakening, research, fish board, tap zap | Works | Works | Client-authoritative (00 section 4). |
| Offline catch-up, day rollover | Works on device clock floored at last save | Works on `ServerClock` extrapolated from last sync; `IsAuthoritative = false` | Bounded loss, reconciled by cloud save. |
| Save | Local | Local; upload queued | A save never fails because the network did. |
| Load | Local | Local after `CLOUD_FETCH_TIMEOUT_S = 5`; conflict check deferred | Bounded boot time. |
| Summon | Works (local service) | **Blocked.** Button shows `SUMMON_NEEDS_NET`. A pending request resolves on reconnect. | Server holds the roll. No pre-issued results. |
| Quest progress | Works | Works | Local counters. |
| Quest claim (diamonds) | Works | Works; diamonds credited locally, credited to the server ledger on the next save upload (8.5.2 step 1) | Diamonds from quests are bounded per day (`DIAMONDS_PER_DAY_F2P`); the server credits the per-reason delta clamped to that allowance (issue #49). |
| Dungeon run | Works (local seed from the `dungeon` stream) | **Blocked** for a new run (needs server seed). Sweep works if the floor was already cleared. | 00 section 4: server-issued seed in MVP. |
| Shop, IAP, inbox claim | Not available | **Blocked** with `NET_OFFLINE` | Server ledger. |
| Remote config / content | Bundled | Last cached, else bundled | Content is always available. |
| Login | Guest auto-created locally | Cached JWT; if expired, play offline as the cached account, re-auth on reconnect | Never lock a player out of their own device's save. |

## 7. Error taxonomy shown to the user

Every user-visible failure maps to one of these classes. The class picks the presentation; the `code` picks the localization keys `err.<code_lower>.title` and `err.<code_lower>.body` (07 section 4.27): the code as listed here and in 5.2 (server codes are 08's wire codes, issue #30), lowercased by the client when it builds the key, so `NET_OFFLINE` shows `err.net_offline.title`. Codes stay UPPER_SNAKE on the wire and in code; only the key is lowercase, because 09's key pattern is (issue #248). Sim rejects use the single toast key `reject.<reason_lower>` (2.1).

| Class | Presentation | Examples (`code`) | Retry |
|---|---|---|---|
| `Reject` (sim) | Inline: button shake + short toast under the button (07 places it next to the button, not in the bottom toast slot) | `CannotAfford`, `BoardFull`, `TankEmpty`, `Cooldown`, `Locked`, `AlreadyClaimed` | Not applicable; the player changes something. |
| `Offline` | Non-blocking card in the action-strip position (07), auto-hides on reconnect; keeps the combat viewport clear | `NET_OFFLINE` | Automatic. |
| `Timeout` / `ServerError` | Toast "Could not reach the server. Retrying..." then modal after retries exhausted with Retry / Later | `NET_TIMEOUT` (a summon timeout uses it too; there is no `SUMMON_TIMEOUT`), `SRV_5XX` | Button. |
| `Rejected` (server) | Modal with a specific message | `INSUFFICIENT_FUNDS`, `RECEIPT_INVALID`, `PROVIDER_ALREADY_LINKED`, `CLIENT_TOO_OLD`, `CONTENT_TOO_OLD`, `BANNER_CLOSED`, `SUMMON_DISABLED`; client-side `IAP_PENDING` (store reports a deferred payment; the pending receipt is kept, 8.9) | Depends; `CLIENT_TOO_OLD` has only "Update". |
| `Conflict` | Dedicated choice modal: the conflict modal (02 section 9.4) for `SAVE_CONFLICT`; [Restore from cloud] / [Reset profile] for `SAVE_CORRUPT` (local save unreadable, a cloud or backup copy exists); a rollback notice for `SAVE_ROLLED_BACK` | `SAVE_CONFLICT`, `SAVE_CORRUPT`, `SAVE_ROLLED_BACK` | Player chooses (rollback: acknowledge). |
| `Maintenance` | Full-screen block on `SCR_BOOT`/`SCR_TITLE` with the message from config and an ETA | `MAINTENANCE` | Automatic poll every `MAINT_POLL_S = 60`. |
| `Fatal` | Full-screen with support id; app cannot continue | `SAVE_TOO_NEW`, `CONTENT_CORRUPT`, `ACCOUNT_BANNED` (appeal url) | Reinstall / contact support. |

No readable save copy anywhere is not an error class (issue #248; 02 section 8.3 owns it): the copies are quarantined, a new profile starts, analytics gets `save_corrupt_all{supportId}`, and `SCR_MAIN` shows a one-time acknowledge-only notice (`notice.save_corrupt_all.title` / `.body`, "your save could not be read; a new game was started; contact support with id X") with the support id. There is no `SAVE_CORRUPT_ALL` row in the 5.2 / section 7 table.

Rules: never two modals at once (07); a `Reject` never produces a modal; a reject code is never shown raw, always through its key (`reject.<reason_lower>`, 2.1); every modal has a support id (`sessionId` prefix + minute) in small print so a screenshot is actionable.

## 8. End-to-end flows

Each flow: a mermaid sequence diagram, then the numbered steps with assembly and failure handling. Participants: `U` player, `S` screen, `VM` view-model, `R` SessionRunner, `Sim`, `RT` other Runtime pieces (saves, clock, clients), `API` server.

### 8.1 Cold boot

```mermaid
sequenceDiagram
    participant U
    participant Boot as SCR_BOOT
    participant RT as Runtime
    participant API
    participant Sim
    participant Main as SCR_MAIN
    Boot->>RT: ContentLoader.LoadBundled()
    RT->>API: GET /config (3 s timeout; cached JWT if any)
    API-->>RT: config {minClientVersion, contentVersion, maintenance, killSwitches}
    RT->>API: POST /auth/guest or /auth/refresh
    API-->>RT: {accessJwt, refreshJwt, playerId, serverTime}
    RT->>RT: ServerClock.Observe
    RT->>API: GET /config again with the fresh JWT (background; server applies the rollout bucket)
    RT->>RT: compare content version; schedule download if newer
    RT->>RT: LocalSaveStore.Load + CloudSaveClient.Fetch (5 s)
    RT->>RT: pick save (02 conflict rules) / migrate / validate
    RT->>Sim: new Simulation(state, db)
    RT->>Sim: Apply ApplyServerTime(now), ComputeOfflineCatchup(now)
    RT->>Boot: ready(pendingOffline?)
    Boot->>Main: show SCR_MAIN
    Main->>Main: if Offline.Pending -> open SCR_OFFLINE_REWARD
```

Steps:

1. **Content load** (Runtime `ContentLoader`): read `StreamingAssets/content/manifest.json`, verify each table's hash, parse into DTOs, hand DTOs to `ContentDb.Build(dtos)` (sim). If a cached newer bundle exists in `persistentDataPath/content/<version>/` and its manifest hash verifies, use it instead. Failure of both -> `Fatal CONTENT_CORRUPT`. Target under 300 ms.
2. **Config fetch** (`ConfigClient`): `GET /config` with `CONFIG_FETCH_TIMEOUT_S = 3`, sending the cached access JWT if one exists (it may be expired). The server applies the content rollout bucket only for a valid JWT; without one it returns the non-rollout default (08; issue #31). After step 3 succeeds, `ConfigClient` re-fetches config once in the background with the fresh JWT; this fetch is off the boot critical path, and any newer bundle it names applies at the next cold boot (8.12). On success: if `minClientVersion > clientVersion` -> `CLIENT_TOO_OLD` block with store link; if `maintenance.active` -> `Maintenance` block; store kill switches; if `contentVersion` newer than loaded -> start background download (8.12). On timeout/offline: use cached config if under `CONFIG_CACHE_MAX_AGE_S = 86400`, else defaults (everything enabled, no gate). Local mode skips this step.
3. **Auth** (`AuthClient`): if a refresh token exists, `POST /auth/refresh`; else if no account at all, `POST /auth/guest` with the device id. Store tokens in secure storage. On offline: continue with cached `playerId` if any; else create a local guest profile with `playerId = null` and link it on first successful connection (08 section 3). First `X-Server-Time` observation makes `ServerClock.IsAuthoritative = true`. As built (issue #148): `GameServices.Account` (`IAccountService`; `LocalAccountService` in Local builds, `HeroCat.Remote.Services.AuthClient` in Remote ones, which is also the `ApiClient`'s `IApiCredentials`) returns a `SignInResult` (`Local`, `SignedIn`, `Offline`, `SignInRequired`) and never stops the boot: a refused session still loads the local save and the client offers `SCR_TITLE`'s sign-in. Tokens live in an `ITokenStore` (`tokens.json` in an `ISecureStore`, 02 section 8.4) and are written before they are used; the save HMAC key is `device_key` in the same store. The guest call's `X-Request-Id` is stored before the call, so a 201 lost to a timeout or a kill is replayed by 08's idempotency store on the next launch instead of answering `DEVICE_ALREADY_BOUND`; a final 4xx drops it. A refresh is single-flight; offline, timeout, 5xx and 429 keep the presented token and retry it only while the retry starts within `REFRESH_REUSE_GRACE_S - HTTP_TIMEOUT_S` of the first send, so it lands inside 08's same-device grace (issue #273); a 401 or `403 DEVICE_MISMATCH` clears the tokens and raises `SessionLost`. A loaded save with `PlayerId = Guid.Empty` is adopted by the signed-in (or cached) account at boot (`BootResult.AdoptedLocalSave`, open question 7), keeping its `RngRoot`.
4. **Save load** (`CloudSaveClient.LoadAsync`): local load per 02 section 8.3 in parallel with `GET /save/meta` (5 s). Resolve per 02 section 9.3; a `Conflict` result shows the conflict modal before `SCR_MAIN`. No save anywhere -> `GameState.NewProfile(playerId, now)` with `TUTORIAL_DIAMONDS` granted via `Wallet.Apply(+1500 DIAMOND, TUTORIAL)` and the starter weapon `TUTORIAL_WEAPON_ID` (both from 09's `tut_welcome` step grant, the only grant path, applied once and guarded by `Tutorial.TutorialDiamondsGranted`, 02; issue #46), tutorial step 0, a full Magic Tank and MP 0 (02 section 3.1). No `Simulation` exists yet, so `ProfileFactory` reports the grant as a `currency_changed` analytics row with `reason = TUTORIAL` itself (06 section 13; issue #246). Copies that exist but none is readable: the same new profile plus 02 section 8.3's `save_corrupt_all` and one-time notice, never a fatal error (section 7; issue #248).
5. **Offline catch-up**: issue the logged system commands `ApplyServerTime(now)` (day/week rollover first, so a new day's quests exist before anything is credited), then `ComputeOfflineCatchup(now)`, which runs `Catchup.Compute` inside `Apply`, stores or sums the result into `state.Offline.Pending` and stamps `LastTickServerTime = now` (8.7; issue #6). Save immediately.
6. **Main screen**: `SCR_BOOT` hands over to `SCR_MAIN`; `SCR_MAIN` opens `SCR_OFFLINE_REWARD` if `Offline.Pending != null`, then the tutorial overlay if `Tutorial.Step < TUTORIAL_DONE` (`TUTORIAL_DONE` = row count of `tutorial_steps`, 09; issue #46). Ticks start on the first `OnFrame` after `SCR_MAIN` is shown (not during the modal, so the modal's numbers are exactly what gets credited).

Boot target: under 4 s to `SCR_MAIN` on the reference low device with warm cache (10). Failures in steps 2-4 degrade per section 6; only step 1 is fatal.

### 8.2 Warm resume from background

```mermaid
sequenceDiagram
    participant OS
    participant R as SessionRunner
    participant RT as Runtime
    participant Sim
    participant Main as SCR_MAIN
    OS->>R: OnApplicationPause(false)
    R->>RT: Clock.SyncAsync() (async)
    R->>Sim: ApplyServerTime(clock.Now)
    R->>Sim: ComputeOfflineCatchup(clock.Now) -> Offline.Pending if >= OFFLINE_MIN_S
    R->>RT: Saves.Save(Resume)
    R->>Main: publish events (DayRolledOver?, OfflinePending)
    Main->>Main: open SCR_OFFLINE_REWARD if pending
    RT-->>R: clock sync lands (later frame); ServerClock adjusts offset
```

1. `ResumeFromGap` runs synchronously inside the callback using the extrapolated server time (monotonic offset from the last sync, 02 section 11). The sync request is fired but not awaited.
2. If the sync later reports a server time that differs from the extrapolation by more than `CLOCK_JUMP_WARN_SEC = 300`, emit `clock_anomaly` and, if the real elapsed time was *longer*, credit the difference as a second catch-up window (`ApplyServerTime` then `ComputeOfflineCatchup` with the corrected now; it sums into the pending record and the effective cap applies to the sum, 8.7). If shorter, nothing is taken back (the discrepancy is bounded by the cap and logged). As built (issue #65): a clock that implements `IClockResync` (`ServerClock`, and the client's `OffsetClock` wrapper) raises `Resynced(extrapolated, synced)` after each observation, from any thread; `SessionRunner` handles it on its next frame in `OnClockSynced`, which tracks `clock_anomaly{kind: resync, seconds, negative}` and, when longer, issues the two commands and saves. The sync itself is `IClockResync.SyncAsync` (02 section 11.1, issue #149), fired without awaiting at the end of `ResumeFromGap` (boot, resume, gap recovery) and on the frame `ResyncDue` turns true (`CLOCK_RESYNC_SEC` without an observation); it never throws, and a failure keeps the offset.
3. Pending summon or IAP intents in `state.Pending` are resolved (8.5, 8.9) after the offline modal closes.
4. Cloud save: `CloudSaveClient` fetches `GET /save/meta`; if the server's `saveSeq` is ahead of local and from another device -> conflict modal (02 section 9.4), otherwise nothing. This is the only time a conflict is detected mid-session.

### 8.3 Tick loop (kill -> loot -> wallet -> events -> view-model)

```mermaid
sequenceDiagram
    participant R as SessionRunner
    participant Sim
    participant Bus as EventBus
    participant VM as ViewModels
    participant HUD
    R->>Sim: Advance(1) x n (n <= MAX_TICKS_PER_FRAME)
    Note over Sim: hero attack, skills, companions, fish missiles, tap zap -> enemy HP
    Note over Sim: enemy HP <= 0: Loot.Roll(loot stream, boss only) -> Wallet.Apply(GOLD, KILL), Wallet.Apply(HERO_EXP, KILL)
    Note over Sim: KillsInStage++ ; == KILLS_PER_STAGE -> StageCleared, Stage++
    Note over Sim: emit EnemyKilled, CurrencyChanged, StageCleared, LevelUp
    R->>Sim: DrainEvents()
    R->>Bus: Publish(events)
    Bus->>VM: handlers update bound fields
    HUD->>Sim: GetSnapshot() (per frame, read-only)
```

Per tick, in this order (03 owns the bodies):

1. `Time.Tick++`; the virtual server clock advances with it and `EnsureDay` runs on the tick that crosses a day key (8.11). Boss timer decrement while a boss fight is on.
2. Spawn: if no enemy and `SpawnDelayLeft == 0`, spawn for `Selected ?? Stage` (a selected farm stage pays its own gold/EXP and never advances anything, issue #35) (`HP0 * HP_G^Stage` by table lookup, x `BOSS_HP_MULT` for a boss).
3. Damage sources, fixed order: hero auto-attack (every `ATTACK_INTERVAL_TICKS` scaled by `ATK_SPD`), skill auto-casts, companion abilities (MP check), queued fish missiles, queued tap zaps (capped). Draws follow 03's fixed order so stream counters advance deterministically regardless of frame rate: one `crit` draw per hero hit (`crit.Counter == Stats.HeroHits`); `combat` is reserved and takes no draws in MVP (issue #8).
4. Death: on a boss kill only, one `loot` draw (boss Rock Candy chance; `loot.Counter == Stats.BossKills`, issue #8); then `Wallet.Apply(GOLD, KILL)`, `Wallet.Apply(HERO_EXP, KILL)`, quest counters (`defeat monsters`), `KillsInStage++`.
5. Stage advance / boss resolution (frontier only); `StageCleared` carries `firstClear` and the diamond grant (reason `FIRST_CLEAR`) when `Stage % BOSS_EVERY == 0` and `Stage > HighestFirstClearBoss` (02; issue #35).
6. Magic Tank refill tick (05); passive `FISH_BOARD_DPS_PCT` is recomputed only when the board changes, not per tick.
7. Snapshot refresh.

Events coalesce above `EVENT_COALESCE_AT` (2.3). View-models update bound fields; screens re-render through their binding layer (07). The HUD reads the snapshot directly.

### 8.4 Training purchase

```mermaid
sequenceDiagram
    participant U
    participant S as SCR_HERO
    participant VM as HeroViewModel
    participant R as SessionRunner
    participant Sim
    U->>S: tap "ATK +10"
    S->>VM: Train(ATK, 10)
    VM->>R: Enqueue(TrainStat{ATK, 10}) (or TrainStatMax{ATK})
    R->>Sim: Apply (next frame)
    Sim-->>R: Accepted or Reject(CannotAfford)
    Sim->>R: events StatTrained, CurrencyChanged(GOLD, -cost, TRAINING)
    R->>VM: publish
    VM->>S: level, next cost, DPS preview update
```

1. The button label shows the bulk cost: `sum_{i=n}^{n+k-1} TRAIN_COST0 * TRAIN_COST_G^i`, computed by the view-model from `ContentDb` curves (repeated multiplication, 00 section 8.1), and greys out when `Gold < cost` for 1 step. "Max" shows the largest `k` affordable, computed by the same walk.
2. `TrainStat` validation: stat exists; `count >= 1`; current level + count <= `TRAIN_CAP[stat]` (03) else `Capped`; affordable else `CannotAfford` (03's reasons in `Reason`). `TrainStatMax` buys the largest affordable count, clamped at the cap. Partial purchase is never applied: "x100" with gold for 37 is a reject, "Max" buys 37.
3. Mutation: `Wallet.Apply(-cost, TRAINING)`, `Training.Level[stat] += count`, DPS cache invalidated. Emits `StatTrained(stat, from, to, cost)` and the `CurrencyChanged`.
4. Analytics: `currency_changed` (06 section 13) with `reason = TRAINING`; one `training` event (03 section 11) per command, not per level.
5. Failure UX: `CannotAfford` is an inline shake; it should be rare because the button was grey. A race (gold spent on another screen in the same frame) is the only path.

Worked number: ATK level 120 -> 130: `50 * 1.07^120 * (1.07^10 - 1) / 0.07 = 50 * 3339.4 * 13.816 = 2,307,000` gold (2.31M).

### 8.5 Summon

#### 8.5.1 M0 local path

```mermaid
sequenceDiagram
    participant U
    participant S as SCR_SUMMON
    participant F as SummonFlow (Runtime)
    participant L as LocalSummonService
    participant Sim
    participant RS as SCR_SUMMON_RESULT
    U->>S: tap "10x Weapon"
    S->>F: Summon(WEAPON, 10)
    F->>F: requestId = Guid; state.Pending.Summon = {requestId, WEAPON, 10}; Save
    F->>L: PullAsync(req)
    L->>Sim: read wallet/pity/bannerLevel/stream; roll on a clone (04); no write
    L-->>F: SummonResultPayload (Debits, StreamCounterAfter)
    F->>Sim: Apply(ApplySummonResult(payload)): debit, items, pity, stream counter
    F->>F: state.Pending.Summon = null; Save
    F->>RS: show reveal
```

1. `SCR_SUMMON` shows cost (`PULL_COST[banner] * count` diamonds, or a ticket if `TICKET_SUMMON_<banner> >= count`; tickets are spent first, 06), pity progress (`pity / PITY_HARD`), banner level and the rate table for the current level. The button is disabled when neither tickets nor diamonds cover the cost.
2. `SummonFlow.Summon` (Runtime): refuse if `state.Pending.Summon != null` (one in flight). Generate `requestId`, persist the intent in `state.Pending.Summon`, save synchronously. The intent is on disk before any roll.
3. `LocalSummonService.PullAsync`: checks affordability before drawing, then runs the 04 pull algorithm on cloned banner, collection and `summon_<banner>` stream state (M0 only; 00 section 8.2) and returns the payload with `Debits` and `StreamCounterAfter`. It reads `GameState` but never writes it (issues #6, #13). Completes synchronously inside a finished `Task`.
4. `Apply(ApplySummonResult)` (logged like any command): debits `Wallet.Apply(-cost, SUMMON)`, adds items (dupe -> level per `DUPE_LVL_CUM`, copies past the cap -> Rock Candy), sets pity, banner level/EXP and the stream counter (2.5), emits `ItemAcquired` x N and `CurrencyChanged`, bumps quest counters (`summon weapon`). Clears pending, saves. Reveal opens.
5. Kill mid-flow: either the save with the pending intent exists and nothing was charged (step 4 not reached; the local service is pure, so it re-runs on resume from the same state and stream counter), or the post-apply save exists. The local service keeps a one-deep replay cache keyed by `requestId` for the in-process case.
6. As built (issue #68): `SessionRunner.Summons` is the `SummonFlow`. The intent is Runtime bookkeeping written straight into `GameState.Pending.Summon` (not a logged command; it is set and cleared inside one synchronous flow, so only a killed flow leaves it in a save). If the intent save fails the summon is refused with `SummonError.SaveFailed` and nothing rolls; a second summon while one is pending gets `SummonError.InFlight`. `ResolveAsync()` re-runs a saved intent with its `requestId`; the client calls it once per boot or resume when `ShouldResolve` (an intent is saved and `SCR_OFFLINE_REWARD` is closed). A refused pull clears the intent (nothing was charged); `Network` and `RateLimited` keep it for the next resolve. A `requestId` the sim already applied is refused (`SUMMON_ALREADY_APPLIED`, surfaced as `SummonError.Replayed`), so a stale copy of the intent cannot charge twice. The `summon` analytics event (04 section 13) is tracked once per applied request.

#### 8.5.2 MVP server path

```mermaid
sequenceDiagram
    participant U
    participant S as SCR_SUMMON
    participant F as SummonFlow
    participant C as SummonClient
    participant API as POST /summon/pull
    participant Sim
    participant RS as SCR_SUMMON_RESULT
    U->>S: tap "10x Weapon"
    S->>F: Summon(WEAPON, 10)
    F->>F: Pending.Summon = {requestId,...}; Save
    F->>RS: open pre-roll (SUMMON_PREROLL_MS)
    opt unsynced earnings (diamonds or tickets)
        F->>API: PUT /save now (server credits the earned delta, 08 section 3.1)
    end
    F->>C: PullAsync(req)
    C->>API: {requestId, banner, count, payment, clientContentVersion}
    alt 200
        API-->>C: payload + balances + serverTime
        C->>Sim: Apply(ApplySummonResult)
        F->>F: Pending = null; Save
        F->>RS: reveal
    else retryable (timeout/5xx/offline)
        C->>API: retry same requestId (backoff, up to HTTP_RETRY_MAX)
        API-->>C: replayed or fresh 200
    else 4xx Rejected
        API-->>C: {code}
        C->>F: Pending = null (nothing charged); Save
        F->>RS: close; show modal(code)
    end
```

1. Steps 1-2 as M0. Payment quote: the client sends `payment: "DIAMOND" | "TICKET"` per the 06 priority rule; the server rejects a mismatch with `409 PAYMENT_MISMATCH` (5.2: client refreshes balances and re-quotes, no charge).
   **Sync before pull** (issue #49): diamonds and tickets earned client-side (login, quests, first clears, achievements, tutorial, daily gift) reach the server ledgers only through `PUT /save`, which credits the per-reason delta since the last upload into `diamond_ledger` / `ticket_ledger` (`ref_type = save_reconcile`, clamped to the per-day allowance; 02, 08 section 3.1). The server charges only what has been uploaded, so when the save's per-reason earned counters (02 `Stats`) are ahead of the last acknowledged upload (`CloudSaveClient.HasUnsyncedEarnings`), `SummonFlow` uploads immediately and awaits the 200 before `PullAsync`. The round trip runs inside the pre-roll. If that upload fails, the pull is still sent; a `402 INSUFFICIENT_FUNDS` then follows step 6.
2. `SummonClient.PullAsync` -> `POST /summon/pull` (08 section 4). The reveal overlay opens immediately with the pre-roll loop (`SUMMON_PREROLL_MS = 800`). A result inside that window feels instant.
3. On 200: `Apply(ApplySummonResult)` with the server's items, pity, banner level and **balances** (`BalancesAfter` replace the local values, 2.5; safe because unsynced earnings were uploaded first). Clear pending, save, reveal.
4. On retryable failure (5.2, including `409 IDEMPOTENCY_IN_FLIGHT`): retry with the same `requestId`. After `SUMMON_SLOW_MS = 3000` the overlay adds "Taking longer than usual". After retries are exhausted (about 15 s): close the overlay, show toast `SUMMON_PENDING_SAVED` ("Your summon is saved and will be revealed when you are back online"), the button becomes "Pending..." and calls `Resolve()`.
5. `Resolve()`: at boot/resume or on the pending button, re-send with the saved `requestId`. The server either replays the stored result (it completed) or executes it (it never arrived). Both are a 200 and are applied identically.
6. On a non-retryable error, clear pending and take the code's 5.2 row: `402 INSUFFICIENT_FUNDS` (balances drifted) -> refresh balances, modal; `BANNER_CLOSED`, `CONTENT_TOO_OLD`, `503 SUMMON_DISABLED` -> modal; `426 CLIENT_TOO_OLD` -> update block; `503 MAINTENANCE` -> maintenance block. Nothing was charged, so nothing to undo.
7. Rules for the UI: never offer a second summon while one is pending; never show "Failed" without having asked the server by replay; balances display `diamonds - cost` provisionally while pending and snap to the server value on 200.
8. Analytics (04 section 13): `summon` event emitted on apply, `summon_pending` when the pending path is entered, `summon_resolved` with `latencySec` when it closes.

Why a random `requestId` rather than `(playerId, totalPulls)`: a cloud-save conflict can rewind `totalPulls` on one device, and a derived key would then collide with a different intent (sibling 08 section 4.4). A random key cannot.

### 8.6 Fish release and merge

```mermaid
sequenceDiagram
    participant U
    participant B as FishBoardView
    participant VM as FishBoardViewModel
    participant R as SessionRunner
    participant Sim
    U->>B: tap tank
    B->>VM: Release()
    VM->>R: Enqueue(ReleaseFish)
    R->>Sim: Apply
    Sim-->>R: Accepted / Reject(BoardFull | TankEmpty)
    Sim->>R: FishReleased{cell, level 1}, FishDiscovered?
    U->>B: drag fish A onto fish B
    B->>VM: Merge(fromCell, toCell)
    VM->>R: Enqueue(MergeFish)
    R->>Sim: Apply
    Sim-->>R: Accepted / Reject(LevelMismatch | InvalidTarget | Capped | BadArgument)
    Sim->>R: FishMerged{toCell, newLevel, missilePct}, FishDiscovered?, CurrencyChanged(FISH_COIN, FISH_MERGE|FISH_DISCOVERY)
```

1. `ReleaseFish`: reject `TankEmpty` if `Fish.TankCount == 0`, `BoardFull` if no empty cell. Else pick the cell with one `fish_spawn` draw by 05's rule (`k = (int)(u * empties)`, `u = NextDouble()`), decrement tank, place level 1. Emits `FishReleased`. Level 1 is never a discovery after the first fish ever.
2. `MergeFish(from, to)`: reject `BadArgument` if `from == to` or a cell is out of range (never a throw, issue #11); `InvalidTarget` (reason `EMPTY_CELL`) if either cell is empty; `LevelMismatch` if levels differ; `Capped` (reason `FISH_AT_LEVEL_CAP`) if `level == FISH_MAX_LVL` (current cap incl. Research). 05 owns the reason list. Else `to.level++`, `from` empties, `FishMerged`. If `to.level` not in `Fish.DiscoveredLevels`: add, `Wallet.Apply(FISH_DISCOVERY_COINS[n], FISH_DISCOVERY)`, `FishDiscovered`. Else `Wallet.Apply(FISH_DISCOVERY_COINS[n] * FISH_MERGE_COINS_PCT, FISH_MERGE)`. Queue a missile of `FISH_MISSILE_PCT[n]` for the next tick (no separate event: the client animates from `FishMerged`, damage lands as 03's `FishMissileHit`), recompute board passive DPS. `ReleaseMax` and `MergeAll` are sim commands with 05's deterministic order, one log entry each.
3. The drag is client-side only until drop; the sim sees one command. A drop on an empty cell sends `MoveFish(from, to)` (05 section 4.5, MVP). An invalid drop snaps back with a haptic (07). A drop that arrives after the source cell changed (merge-all raced) is rejected with `InvalidTarget` and the board re-renders from the next snapshot.
4. Quest counters: `release fish`, `merge fish`. Analytics: 05 section 13's set (`fish_release`, `fish_merge` batched per bulk command, `fish_discovered`, `fish_discard`, `fish_board_snapshot`).
5. Determinism: the only RNG is `fish_spawn`; merges are pure. The bot's fish policy (05) issues the same commands.

### 8.7 Offline reward claim

```mermaid
sequenceDiagram
    participant R as SessionRunner
    participant Sim
    participant M as SCR_OFFLINE_REWARD
    participant U
    R->>Sim: Apply ComputeOfflineCatchup(now) -> Offline.Pending{elapsed, credited, capped, gold, exp, tankFill}
    R->>M: open (gold, EXP, duration, "capped at 24 h")
    U->>M: tap Claim
    M->>R: Enqueue(ClaimOfflineReward)
    R->>Sim: Apply: Wallet.Apply(GOLD, OFFLINE), Wallet.Apply(HERO_EXP, OFFLINE), TankCount = min(TankCap, TankCount + TankFill); Pending = null
    Sim->>R: OfflineRewardClaimed, CurrencyChanged x2, LevelUp?
    R->>R: Save
    M->>M: close; ticks begin
```

1. `ComputeOfflineCatchup(now)` runs `Catchup.Compute` (sim, 06 section 7) inside `Apply`: `credited = clamp(now - LastTickServerTime, 0, effectiveCap)` with `effectiveCap = OFFLINE_CAP_H * 3600` plus the Research `RES_OFFLINE_CAP` extension (00 decision 12); gold and EXP at `OFFLINE_RATE` of the current stage's kill rate; `TankFill` from 05's `CatchUpTank(credited * TICK_HZ)`, which carries the refill progress. No stage, drop, quest or stream change (issues #6, #21).
2. The result is stored in `state.Offline.Pending` **and** `LastTickServerTime` is stamped immediately, so the window cannot be computed twice. A second gap while a reward is pending sums into the same record (amounts and seconds summed, the effective cap applied to the summed credited time, `Stage` and `KillTimeSec` from the latest gap for display; issue #21). If the app dies before the claim, the pending reward is still in the save and the modal reopens at next boot. The modal is the only way to claim; there is no auto-claim in MVP (the P1 2x option needs the modal open).
3. `ClaimOfflineReward`: reject `NotAvailable` (reason `NO_PENDING_OFFLINE_REWARD`) if `Pending == null`. Else credit, add the tank fill (`TankCount = min(TankCap, TankCount + TankFill)`; the live tank kept ticking since compute) and clear. Emits `OfflineRewardClaimed(elapsedSec, creditedSec, capped, gold, exp)`.
4. Gaps under `OFFLINE_MIN_S` produce no pending and no modal; `ComputeOfflineCatchup` credits only the tank refill for the gap (`CatchUpTank`) and stamps `LastTickServerTime` (issue #21).
5. Ticks do not run while the modal is open: `SessionRunner` holds its tick loop whenever `Offline.Pending` is set (`TicksHeldForOfflineReward`, from the compute that stored it to the `ClaimOfflineReward` that clears it; commands, clock syncs, saves and events continue), so the numbers on the modal are exactly what is credited and no screen has to toggle `TicksEnabled` (issues #21, #108).

### 8.8 Dungeon run (server-issued seed, MVP)

```mermaid
sequenceDiagram
    participant U
    participant S as SCR_DUNGEONS
    participant F as DungeonFlow
    participant API as POST /dungeon/seed
    participant Sim
    participant Run as SCR_DUNGEON_RUN
    U->>S: tap "Enter Fish Coin Dungeon floor 7"
    S->>F: Start(FISH_COIN, 7)
    F->>F: requestId; Pending.Dungeon = {requestId, kind, floor}; Save
    F->>API: {requestId, kind, floor, clientContentVersion}
    API-->>F: {seed, grantId, expiresAt}
    F->>Sim: Apply(StartDungeon{kind, floor, seed, grantId})
    Sim->>Run: DungeonStarted; main-loop ticks run the fight on the temporary `dungeon_run` stream
    Sim->>F: DungeonCleared{kind, floor, rewards} (debits one ticket) or DungeonFailed (costs nothing)
    F->>F: Pending = null; Save
    F->>API: (next save upload carries Dungeons.RecentRuns for seed correlation, 08)
```

1. Precondition in the view-model: ticket available (`Wallet.DungeonTicket[kind] >= 1`), floor unlocked (`floor <= Dungeons.BestFloor[kind] + 1`), no boss attempt active (the sim rejects `StartDungeon` with `(NotAvailable, BOSS_ATTEMPT_IN_PROGRESS)` during a boss and the screen asks the player to finish the fight first; there is no `RetreatFromBoss` in MVP, issue #11; confirmed by issue #42). `SCR_DUNGEONS` labels the floor "Clearable" from `Simulation.PreviewDungeon` (06), which runs the same fight headlessly on a cloned state.
2. Seeds come from the `IDungeonSeedService` seam (issue #25). MVP (`DungeonSeedClient`): `POST /dungeon/seed` returns a server seed bound to `(playerId, kind, floor, dayKey)` with `expiresAt = now + DUNGEON_SEED_TTL_S = 600`. The server records the issue in `dungeon_seeds` so the save upload can be checked (08). M0: `LocalDungeonSeedService` reads the next `dungeon` stream draw without advancing it and returns it with `localSeed = true`; `Apply(StartDungeon)` then consumes that one draw, so the service never writes state (as for summons, issue #6) and bots and M0 work without a server.
3. `StartDungeon` (sim; ticked run model, issue #42): reject with 06 section 6.2's reasons: `(Locked, DUNGEON_LOCKED)` before the dungeon unlock, `(Locked, DUNGEON_FLOOR_NOT_UNLOCKED)` for a floor not yet unlocked, `(CannotAfford, NOT_ENOUGH_TICKET_<kind>)` without a ticket (for example `NOT_ENOUGH_TICKET_FISH_COIN`; issue #244), `(NotAvailable, DUNGEON_RUN_IN_PROGRESS)` if a run is already active, `(NotAvailable, BOSS_ATTEMPT_IN_PROGRESS)` during a boss fight. Else validate only: no ticket is spent at start (00 decision 16). Swap the combat context to the dungeon fight (`Combat.Mode = Dungeon`, 02 `Combat.DungeonRun`), reseed a *temporary* `dungeon_run` stream with the server seed (the persistent `dungeon` stream is untouched in MVP). `SCR_DUNGEON_RUN` replaces the combat viewport. The Fish Board is hidden and frozen for the run (issue #40): passive `FISH_BOARD_DPS_PCT` only, no fish missiles or Tap Zap, the tank keeps refilling, and board commands are rejected while `Combat.Mode == Dungeon` (05 owns the reason).
4. The fight is one boss enemy per floor from `dungeon_floors` (09; `DungeonRun.EnemyIndex` is always 0 in MVP) run by the normal tick loop for at most `BOSS_TIME_TICKS`. Outcome `DungeonCleared` debits one ticket (`Wallet.Apply(-1, DUNGEON)`), grants the floor's reward table through `Wallet.Apply(..., DUNGEON)` and updates `Dungeons.BestFloor[kind]`; `DungeonFailed` grants nothing and costs nothing (00 decision 16). Either way the run is appended to `Dungeons.RecentRuns` (`{GrantId, Kind, Floor, Cleared}`, 02; issue #52) and `Combat.Mode = Stage` resumes at the same stage and kills.
5. Sweep: `SweepDungeon(kind)` grants `RewardOf(kind, BestFloor)` instantly with no fight and debits one ticket; no seed needed (deterministic by content). Rejected `(NotAvailable, NOTHING_TO_SWEEP)` when `BestFloor == 0` (06). Works offline.
6. Failure: seed request offline -> `NET_OFFLINE` banner, nothing spent. Seed request times out -> retry same `requestId`; the server replays the same seed. App dies mid-run -> on load `Combat.Mode == Dungeon` with a run in progress is abandoned (02 recovery rule P16): mode reset, nothing to refund because no ticket was spent. The server sees a seed issued with no matching `RecentRuns` entry and ignores it (expiry). Validation is correlation only in MVP (08 `DUNGEON_WITHOUT_SEED`; no replay, issue #52): the server matches `RecentRuns` clears to issued seeds, and soft-currency rewards stay bounded by 08's `GOLD_DELTA_BOUND` and `RESEARCH_WITHOUT_COINS` checks.

### 8.9 IAP purchase -> receipt validation -> inbox -> claim

```mermaid
sequenceDiagram
    participant U
    participant S as SCR_SHOP
    participant F as PurchaseFlow
    participant Store as StoreKit2 / Play Billing
    participant RC as ReceiptClient
    participant API as POST /iap/validate
    participant IB as InboxClient
    participant Sim
    U->>S: tap "Diamond pack 3 ($9.99)"
    S->>F: Buy(sku)
    F->>Store: purchase(sku)
    Store-->>F: transaction {transactionId, signedPayload}
    F->>F: Pending.Receipts += {requestId, platform, sku, transactionId, payload}; Save
    F->>RC: Validate(requestId, receipt)
    RC->>API: {requestId, platform, sku, transactionId, payload}
    API->>API: verify with Apple/Google; grant to inbox_items (idempotent on transactionId)
    API-->>RC: {status: GRANTED, inboxItemId}
    F->>Store: finishTransaction / acknowledge
    F->>F: Pending.Receipts -= it; Save
    F->>IB: GET /inbox
    IB-->>F: items
    U->>S: SCR_INBOX "Claim"
    F->>API: POST /inbox/claim {requestId, inboxItemId}
    API-->>F: {grant lines, balances}
    F->>Sim: Apply(ApplyInboxGrant)
```

1. Purchase through Unity IAP (StoreKit 2 on iOS, Play Billing 6 on Android). The store transaction is **not** finished/acknowledged until the server has confirmed the grant; an unacknowledged Play purchase is auto-refunded after 3 days, so step 4 must happen before then and the pending receipt list guarantees retries.
2. Persist the receipt intent (`Pending.Receipts`, a list, not a single slot: a player can complete a second purchase while the first validation is in flight) and save before calling the server.
3. `POST /iap/validate` (08 section 6). Server verifies the signed payload with the store API, checks the `transactionId` has not been granted before, writes `purchases` and `inbox_items` in one transaction, returns `GRANTED` (or `ALREADY_GRANTED` with the same `inboxItemId`, which the client treats identically).
4. On 200: finish/acknowledge the store transaction, remove the pending receipt, save. On retryable failure: keep the pending receipt, retry with the same `requestId` on resume and every `RECEIPT_RETRY_S = 60` while in the foreground. On `400 RECEIPT_INVALID`: remove pending, do **not** finish the transaction (let the store's own refund path handle it), show `err.receipt_invalid` (title and body) with a support id, analytics `iap_failed` with `errorCode = RECEIPT_INVALID` (06 section 13). A store-side deferred payment (Ask to Buy, pending card) shows `IAP_PENDING` and keeps the pending receipt.
5. The grant lands in the Inbox, never directly in the wallet. `SCR_SHOP` shows a toast "Sent to your Inbox" and the inbox badge increments. First-purchase doubling is applied server-side when writing the inbox item (06).
6. Claim: `POST /inbox/claim {requestId, inboxItemId}` -> `Apply(ApplyInboxGrant(payload))` -> `Wallet.Apply(+diamonds, IAP)`; `Inbox.ClaimedIds += id`. Replay-safe by `requestId`; a double claim returns `409 INBOX_ALREADY_CLAIMED` carrying the original grants, which the client treats as success (5.2): it applies them only if the id is not already in `ClaimedIds`.
7. Restore purchases (`SCR_SETTINGS`): query the store for owned non-consumed transactions, run step 3 for each. Server deduplicates on `transactionId`.
8. Refund/chargeback (08): server marks the purchase `REFUNDED`, writes a negative inbox item or applies a negative balance adjustment on next save upload with reason `REFUND`. The client shows "A purchase was refunded; N diamonds were removed" once.

### 8.10 Cloud save upload, download and conflict

```mermaid
sequenceDiagram
    participant R as SessionRunner
    participant CS as CloudSaveClient
    participant LS as LocalSaveStore
    participant API as /save
    participant U
    R->>CS: Save(state, trigger)
    CS->>LS: write (atomic, 02)
    CS->>CS: enqueue upload (coalesce CLOUD_UPLOAD_MIN_SEC; immediate on Pause/Quit/Summon/IAP)
    CS->>API: PUT /save {If-Match: etag, meta, hmac} + blob
    alt 200
        API-->>CS: {etag, saveSeq, serverTime, flags[]}
    else 409 SAVE_CONFLICT
        API-->>CS: {serverMeta}
        CS->>API: GET /save (blob)
        CS->>CS: resolve per 02 section 9.3
        alt auto-resolvable
            CS->>LS: write winner; PUT again with new etag
        else needs player
            CS->>U: conflict modal (two SaveSummary cards)
            U->>CS: pick
            CS->>LS: write chosen; keep rejected as slot0.conflict-*.sav
            CS->>API: PUT chosen (If-Match: serverEtag)
        end
    end
```

1. Every `Save` writes locally first (02 section 8.2). Upload is coalesced to at most one per `CLOUD_UPLOAD_MIN_SEC = 60` except on `Pause`, `Quit`, after a summon apply and after an inbox claim, which upload immediately (the player saw something they must not lose), and before a summon pull with unsynced earnings, which uploads and awaits the 200 (8.5.2 step 1, issue #49).
2. `PUT /save` carries `If-Match: <etag>` of the last server version this device saw, the metadata (`saveSeq`, `lastSavedServerTime`, `deviceId`, `schemaVersion`, `contentVersion`, `progressScore`, `sha256`) and the HMAC envelope (02 section 10). The blob goes to S3 via the API (08). Response returns the new `etag` and any plausibility `flags`.
3. `409 SAVE_CONFLICT` means another device wrote since (`SAVE_STALE` and `SAVE_ROLLED_BACK` follow their 5.2 rows). Download the server blob and resolve per 02 section 9.3: same device lineage -> higher `saveSeq` wins silently; different devices -> higher `lastSavedServerTime` wins unless the loser's `progressScore` is higher by more than `CONFLICT_PROGRESS_MARGIN` (02), in which case the conflict modal shows both. The modal never appears during a boss or a reveal; it waits for `SCR_MAIN` idle.
4. Download at boot (8.1 step 4) uses the same resolution with `L` = local and `R` = server.
5. Offline: the queue holds the latest blob only; a failed upload is retried at the next trigger. A device that stays offline for days uploads once on reconnect and may hit 409 then; the resolution is identical.

### 8.11 Daily rollover while the app is open

```mermaid
sequenceDiagram
    participant R as SessionRunner
    participant Clock as ServerClock
    participant Sim
    participant VM as QuestsViewModel
    R->>Clock: NowServerSeconds (every WALL_CLOCK_SYNC_SEC, and at boot/resume/gap)
    R->>Sim: ApplyServerTime(now) (logged; re-anchors the virtual clock)
    Note over Sim: each tick: virtual now = anchor + ticks / TICK_HZ; dayKey(now) > Time.DayKey ?
    Sim->>Sim: EnsureDay: Quests.ResetDaily(newDayKey); Dungeon tickets reset; login grant if LoginDayKey < newDayKey; Time.DayKey = newDayKey
    Sim->>Sim: weekKey(now) > Time.WeekKey ? Quests.ResetWeekly
    Sim->>R: DayRolledOver{oldKey, newKey}, CurrencyChanged(DIAMOND, +LOGIN_DIAMONDS, LOGIN)
    R->>VM: publish; red dots update; SCR_QUESTS re-renders if open
```

1. Rollover is tick-driven (06): the tick whose virtual server time crosses a day key runs `EnsureDay`. `ApplyServerTime` is issued and logged only at sync points (3.1; issue #5); it re-anchors the virtual clock and runs the same key check, which handles a jump. Between syncs the check is a two-integer comparison per tick. `dayKey = floor(now / DAY_SECONDS)`, `weekKey = floor((now - WEEK_EPOCH_OFFSET) / (7 * DAY_SECONDS))` (06 fixes the week start; default Monday 00:00 UTC, `WEEK_EPOCH_OFFSET = 4 * DAY_SECONDS` because 1970-01-01 was a Thursday).
2. Rollover is forward-only: `if newKey <= Time.DayKey return`. A backwards clock never resets the day (02 section 11.3).
3. What resets: daily quest progress and claimed flags, dungeon tickets to the daily grant, daily free gift, login diamonds granted once per day key (`LOGIN_DIAMONDS` auto-granted with reason `LOGIN`, guarded by `Time.LoginDayKey`; `d_login` is a counting row that pays nothing, issue #20). What persists: everything else. A boss attempt or dungeon run in progress is not interrupted; the quest it would complete counts toward the new day, and a dungeon run that clears debits its ticket from the new day's allotment (06).
4. A rollover during `SCR_QUESTS` re-renders the list in place with a toast `NEW_DAY`. A `ClaimQuest` for a quest the rollover just reset runs `EnsureDay` first and is rejected `(NotAvailable, QUEST_NOT_COMPLETED)` (06 sections 1.3 and 5.5), and the UI refreshes.
5. Skipped days: a 3-day absence rolls once (the day key jumps); daily quests are not retroactively granted. Login diamonds are once per *key change*, not per day skipped.

### 8.12 Remote config and content hot update

```mermaid
sequenceDiagram
    participant Boot as SCR_BOOT
    participant CC as ConfigClient
    participant API as GET /config, CDN
    participant CL as ContentLoader
    participant Sim
    Boot->>CC: Fetch()
    CC->>API: GET /config (Authorization: cached JWT if any)
    API->>API: apply rollout bucket server-side (JWT present) else non-rollout default
    API-->>CC: {contentVersion: "2026.11.10", contentUrl, sha256, minClientVersion, rolloutPct, inBucket?, killSwitches, hotApplyTables}
    CC->>CL: DownloadAsync(contentUrl, sha256) (thread pool)
    CL-->>CC: bundle verified into content/2026.11.10/
    CC->>CC: mark "pending content"; apply at next cold boot
```

1. Config is fetched at boot (8.1 step 2) and re-fetched every `CONFIG_REFRESH_S = 1800` in the foreground and on every resume. Kill switches (`shopEnabled`, `summonEnabled[banner]`, `dungeonsEnabled`, `cloudSaveEnabled`, `adsEnabled`) apply **immediately**: a view-model reads `IConfigService.Flags` and disables the button with `FEATURE_DISABLED`. A kill switch never touches `GameState`.
2. Content bundles apply **only at the next cold boot**, never mid-session (sibling 08 section 7 rule 1): kill times, catch-up arithmetic and the wall condition would change under a running sim. The downloaded bundle is validated (manifest hash, per-table hash, 09 validator) on the thread pool and marked ready; `SCR_SETTINGS` shows "Update ready, restarts on next launch". Exception: a bundle whose manifest sets `hot_apply: true` is swapped at a safe point (no modal open, not in a boss). The exporter (09) sets it only when the diff touches nothing but the four hot-apply tables of 00 contract 3: `localization`, `shop_products`, `shop_packages`, `inbox_templates` (issue #54). None is read by the sim; the Shop and Inbox view-models re-read their tables on swap. The manifest bool is the single source; config's `hotApplyTables` (08) only echoes the exporter's constant and the client does not act on it.
3. `minClientVersion` above the running client -> `CLIENT_TOO_OLD` full block with store link, checked at boot and at every config refresh (a mid-session block waits for `SCR_MAIN` idle and saves first).
4. Rollout buckets are computed by the server only (issue #31): `GET /config` returns the version for the caller's bucket when the request carries a valid JWT, and the non-rollout default otherwise. The client never hashes `playerId`; `rolloutPct` and `inBucket` in the response are informational. A guest with `playerId = null` gets the default until its first auth. `maintenance: {active, messageKey, until}` (`messageKey` is a localization key, 08 section 6.1) blocks at boot and shows a banner mid-session with a countdown; at `until` the client re-fetches config.
5. Server-side minimum content version per client version (09) means an old client is never handed a bundle it cannot parse.

As built (issue #144): `RemoteConfigService` (HeroCat.Remote) is the `ConfigClient`: one `GET /config` attempt, never retried, bounded by `CONFIG_FETCH_TIMEOUT_S` (and `BootFlow` waits no longer than that whatever the service does); a good answer is cached under `persistentDataPath/config/` with its `serverTime`, and the cache counts as fresh while `|now - fetchedAt| < CONFIG_CACHE_MAX_AGE_S`. Only a live answer blocks boot for maintenance; a cached `maintenance.active` does not (offline play continues through a window, 08 section 8.4), while a cached `minClientVersion` still gates. `ConfigRefresher` (Runtime) runs the cadence from `SessionRunner.OnFrame`/`OnResume`: every `CONFIG_REFRESH_S`, every resume, every `MAINT_POLL_S` while maintenance is active and once at `until`; the maintenance banner shows during the window and from `MAINT_ANNOUNCE_S` before an announced one. `ContentUpdater` (Runtime) validates a downloaded bundle (manifest, per-table sha256, `E903`, `ContentDb.Build`) on the thread pool over the `IContentStaging` seam whose CDN implementation is issue #212, discards a failing one (not re-downloaded that session) and treats a bundle as hot only when its manifest sets `hot_apply` and its tables differ from the running set's only in the four allowlist tables; the hot tables reach the client as a `HotContentSet` beside the sim's `ContentDb`. A kill-switched banner refuses in `SummonFlow` before any intent is saved (`SummonError.Disabled`) and its buttons show `err.summon_disabled.title`.

### 8.13 Logout and account switch

```mermaid
sequenceDiagram
    participant U
    participant S as SCR_SETTINGS
    participant F as AccountFlow
    participant CS as CloudSaveClient
    participant API
    participant Boot as SCR_BOOT
    U->>S: tap "Switch account" / "Log out"
    S->>F: Logout()
    F->>CS: Save(Quit) + upload now (await, 10 s)
    alt upload ok or user confirms "log out anyway"
        F->>API: POST /auth/logout {refreshToken}
        F->>F: clear tokens; move saves/ to saves.<playerId>.archived/ (kept ARCHIVE_KEEP_DAYS)
        F->>Boot: restart to SCR_TITLE
    else upload failed and user cancels
        F->>S: stay
    end
    Boot->>U: SCR_TITLE: Sign in with Apple / Google / continue as guest
```

1. Logout requires the current save to be uploaded (await up to `LOGOUT_UPLOAD_TIMEOUT_S = 10`). If it fails, the modal `LOGOUT_UNSYNCED` warns that unsynced progress on this device will be unavailable on other devices and offers "Log out anyway" / "Cancel".
2. Tokens are cleared from secure storage; the local save directory is archived under the old `playerId` (not deleted) for `ARCHIVE_KEEP_DAYS = 30` so a mistaken logout is recoverable from `SCR_TITLE` ("Found a local save for another account").
3. `SCR_TITLE` sign-in: Apple / Google Play Games -> `POST /auth/link-login`; the server returns the `playerId` for that platform account. Boot continues at 8.1 step 4 with no local save, so the cloud copy is downloaded.
4. Linking a guest to a platform account that already owns a different `playerId` is the `409 PROVIDER_ALREADY_LINKED` case (`details.otherSummary`, 08): the modal shows both `SaveSummary` cards (this device's guest vs the account's cloud save) and the player picks which to keep. The loser is archived server-side (08).
5. Delete account (`SCR_SETTINGS`): `POST /account/delete` -> server starts the GDPR flow (02 section 13.2); the client wipes local saves and tokens and restarts.

As built (issue #148): `HeroCat.Runtime.Account.AccountFlow` runs steps 1, 2 and 5 engine-free; the screens pass the save-and-upload and the `LOGOUT_UNSYNCED` modal as delegates, and stop the session before the archive. An upload that throws, fails or outlasts `LOGOUT_UPLOAD_TIMEOUT_S` asks the modal; "Cancel" changes nothing. `POST /auth/logout` is one attempt (no backoff schedule) and the tokens are cleared whatever it answers, since the family is unusable once the client forgets it. `SaveArchive` moves `saves/` in one rename to `saves.<playerId>.archived/` (a local guest that never connected archives under `Guid.Empty`; a second archive of the same player replaces the first) and stamps it with the clock's unix seconds in an `archived_at` file, which boot step 4 prunes by (`ARCHIVE_KEEP_DAYS`), like the quarantine purge, not by file mtimes. `SaveArchive.Restore` moves an archive back only into an empty `saves/`. Account deletion wipes `saves/` and that player's archive only after the server accepts. Steps 3-4 (`/auth/link-login`, `/auth/merge`) are issue #196.

## Open questions

1. Resolved: the tutorial overlay keeps ticks running except during step 1; `SetTutorialStep` advances `GameState.Tutorial.Step` when a step's `expect_event` has fired `expect_count` times (09 `tutorial_steps`; issue #46).
2. **Second catch-up window after clock correction** (8.2 step 2). Credits extra time when the real gap was longer than the extrapolation. Owner: 06. Default: credit it into the same pending record, capped by the effective cap on the sum (8.7, issue #21), because the alternative punishes a player whose device clock drifted.
3. **Command log upload policy.** 2.6 uploads only on server request. Should MVP upload the last 1 MiB with every save so plausibility checks can replay without a round trip? Owner: 08. Default: no; bandwidth at 100k DAU is not worth it until flags exceed 1%.
4. Resolved: hot-apply allowlist is 00 contract 3's four tables, carried by the manifest `hot_apply` bool (8.12 step 2; issue #54).
5. **`TICK_BUDGET_MS` on high-end devices.** A fixed 8 ms is conservative on flagships. Owner: 10. Default: fixed; revisit if catch-up after a hitch is visibly slow.
6. Resolved: a failed or abandoned dungeon run costs nothing; the ticket is debited on clear only (8.8 steps 3-4; 00 decision 16, issue #42).
7. **Local guest with `playerId = null`** (8.1 step 3). Playing with no account until first connection complicates save upload lineage. Owner: 08. Default: allowed; the first successful guest auth adopts the local save as that player's first upload.

## Acceptance criteria

1. `HeroCat.Sim.dll` built in Release references only `netstandard`; the CI identifier grep for `UnityEngine`, `System.IO`, `Newtonsoft`, `DateTime.Now`, `DateTime.UtcNow`, `Stopwatch`, `Math.Pow`, `Math.Exp`, `Math.Log`, `System.Reflection` finds zero hits in `src/HeroCat.Sim/`.
2. Every `Command` in table 2.1 has an `Apply` branch that returns a `RejectCode` (and the owning spec's `Reason`) for each listed invalid input without mutating state (unit test per command: state hash before == after on reject, no events emitted).
3. `Simulation.Apply` never throws for any command constructed from fuzzed field values (property-based test, 10,000 cases per command type).
4. A frame gap of 25 s produces `25 * TICK_HZ` ticks over at most 3 frames with `TICK_BUDGET_MS` disabled; a gap of 31 s produces 0 ticks and one logged `ComputeOfflineCatchup` covering 31 s.
5. `SessionRunner.OnPause` completes a save synchronously in under 50 ms on the reference low device with a 40 KB state (perf test, 10).
6. Golden determinism: the same seed and command log (including the `ApplySummonResult`, `ApplyServerTime` and `ComputeOfflineCatchup` system commands; replaying a Local-mode summon reproduces the debit and the `summon_<banner>` counter) produce byte-identical `GameState` hashes on x64 .NET, ARM64 IL2CPP and the bot harness (10).
7. Summon idempotency: a `POST /summon/pull` that times out after the server committed, then is retried with the same `requestId`, yields exactly one debit, one audit row and the identical payload (server test, 08; client test with a fault-injecting `ApiClient`).
8. Killing the process at any point of the summon flow (before save, after intent save, after server 200, after apply) and relaunching results in exactly one of: no charge and no items, or one charge and the items. Never partial (fault-injection test over the five points).
9. Offline reward: `Catchup.Compute` then kill before claim, relaunch: the modal shows the same numbers and the window is not credited twice (`LastTickServerTime` is stamped at compute time).
10. Day rollover with the app open resets daily quests exactly once at the key boundary, and a clock that goes back one day resets nothing (FakeClock test).
11. Kill switch `summonEnabled[WEAPON] = false` disables the summon button within one config refresh without a restart; a new content bundle never changes `ContentDb` mid-session (assert the `ContentDb` instance identity is stable across a config refresh).
12. `EventBus` handlers calling `runner.Enqueue` never cause a nested `Apply` (assert `Apply` re-entrancy guard is never hit in the PlayMode smoke test).
13. Logout with a failed upload shows `LOGOUT_UNSYNCED` and does not clear tokens unless confirmed; the archived save directory exists after logout.
14. Every code in the 5.2 table and section 7 that shows a message has `err.<code_lower>.title` and `err.<code_lower>.body` keys in the English `localization`, every reject reason and `RejectCode` fallback has its `reject.<reason_lower>` key, and the save-reset notice has its `notice.save_corrupt_all.*` keys (09 validator rule; the test reads `ErrorClassifier.Table` and the sim's reason lists, issues #30, #248).
15. Boot to `SCR_MAIN` under 4 s with warm content cache on the reference low device, under 6 s with a 3 s config timeout and offline (10 perf test).

## Constants introduced

Engineering constants, code-resident in `HeroCat.Runtime.RuntimeConfig` (not balance; not in `ContentDb`). `I` = carried from the sibling project where it had the same role; `S` = new here. Tuning tool: none of these are balance constants; 10's perf and fault tests are the check.

| Name | Value | Tag | Where |
|---|---|---|---|
| `MAX_TICKS_PER_FRAME` | 100 | I | 3.2 |
| `IN_APP_GAP_SEC` | 30 | I | 3.2 |
| `AUTOSAVE_INTERVAL_SEC` | 30 | I | 3.2 |
| `WALL_CLOCK_SYNC_SEC` | 60 | I | 3.1, 3.2, 8.11 |
| `CLOCK_BACKWARDS_TOLERANCE_S` | 5 | S | 3.1, 3.2 (content constant, 09 section 5.1) |
| `TICK_BUDGET_MS` | 8 | S | 3.2 |
| `SAVE_FAIL_WARN_COUNT` | 3 | I | 3.1 |
| `EVENT_BUFFER_MAX` | 4096 | S | 2.3 |
| `EVENT_COALESCE_AT` | 1024 | S | 2.3 |
| `DMG_NUMBERS_PER_TICK_MAX` | 16 | S | 2.4 |
| `CMDLOG_MAX_BYTES` | 1 MiB | S | 2.6 |
| `HTTP_TIMEOUT_S` | 10 | S | 5.2 |
| `HTTP_RETRY_BASE_MS` | 1000 | I | 5.2 |
| `HTTP_RETRY_CAP_MS` | 8000 | I | 5.2 |
| `HTTP_RETRY_MAX` | 4 | I | 5.2 |
| `CONFIG_FETCH_TIMEOUT_S` | 3 | S | 8.1 |
| `CONFIG_CACHE_MAX_AGE_S` | 86400 | S | 8.1 |
| `CONFIG_REFRESH_S` | 1800 | S | 8.12 |
| `CLOUD_FETCH_TIMEOUT_S` | 5 | I | 6, 8.1 |
| `CLOUD_UPLOAD_MIN_SEC` | 60 | I | 8.10 |
| `CLOCK_JUMP_WARN_SEC` | 300 | I | 8.2 |
| `SUMMON_PREROLL_MS` | 800 | I | 8.5 |
| `SUMMON_SLOW_MS` | 3000 | I | 8.5 |
| `DUNGEON_SEED_TTL_S` | 600 | S | 8.8 |
| `RECEIPT_RETRY_S` | 60 | S | 8.9 |
| `MAINT_POLL_S` | 60 | S | 7 |
| `MAINT_ANNOUNCE_S` | 3600 | S | 8.12 (08 section 8.4's "banner 1 h before") |
| `LOGOUT_UPLOAD_TIMEOUT_S` | 10 | S | 8.13 |
| `ARCHIVE_KEEP_DAYS` | 30 | S | 8.13 |
| `REFRESH_REUSE_GRACE_S` | 30 | S (08's, mirrored) | 8.1 (the refresh retry window) |
| `WEEK_EPOCH_OFFSET` | `4 * DAY_SECONDS` | S | 8.11 (06 owns the week-start decision) |

Used from 00 by name: `TICK_HZ`, `ATTACK_INTERVAL_TICKS`, `KILLS_PER_STAGE`, `BOSS_EVERY`, `BOSS_HP_MULT`, `HP0`, `HP_G`, `STAGE_CAP`, `TAP_ZAP_MAX_PER_SEC`, `TRAIN_COST0`, `TRAIN_COST_G`, `PULL_COST[*]`, `PITY_HARD`, `DUPE_LVL_CUM`, `DAY_SECONDS`, `OFFLINE_RATE`, `OFFLINE_CAP_H`, `RES_OFFLINE_CAP`, `OFFLINE_MIN_S`, `TUTORIAL_DIAMONDS`, `LOGIN_DIAMONDS`, `DIAMONDS_PER_DAY_F2P`, `FISH_MAX_LVL0`, `FISH_DISCOVERY_COINS`, `FISH_MERGE_COINS_PCT`, `FISH_MISSILE_PCT`, `FISH_BOARD_DPS_PCT`.
