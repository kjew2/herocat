# 08 - Backend Seams

Scope: the interfaces the prototype implements locally that a backend will own later, written so that adding a server is additive: new implementations of existing interfaces, one new assembly, no change to the sim. Each interface is given as a C# sketch with its local implementation named, its remote contract, and the error behaviour when the network is absent. Constants are canonical in `specs/00` section 2; the save shape is `specs/06`; gacha math is `specs/01`; payment rules are `specs/04` section 7.5.

## 1. Principle and assembly layout

The sim (`HeroCat.Sim`, pure C#, 00 section 5.1) depends on interfaces only. The client's composition root picks implementations. Nothing in the sim branches on "local or remote".

| Assembly | Contains | Tag |
|---|---|---|
| `HeroCat.Sim` | The interfaces in this spec, every `Local*` / `File*` / `Fake*` implementation, `Services.Local(...)`. | P0 |
| `HeroCat.Client` | Unity wiring (`specs/07`): calls `GameServices.Local` or `RemoteGameServices.Create` from a build-time `BackendMode` (`Local` by default; the scripting define `HEROCAT_BACKEND_REMOTE` selects `Remote`). Gacha stays the sim's local roll in both modes until the section 4.3 contract exists, because `Simulation.Pull` charges before calling the service. | P0 |
| `HeroCat.Remote` | `ServerClock`, `RemoteGachaService`, `CloudSaveStore`, `RemoteReceiptValidator`, `RemoteConfig`, `RemoteAnalyticsSink`, the HTTP client. Vendor-neutral: calls go through `IRemoteTransport` (`RemoteRequest` in, HTTP-style `RemoteResponse` out), and the chosen BaaS SDK (section 10) is one transport implementation. Until the spike decides, it ships `OfflineTransport` (every call is `Offline`), `FakeTransport` for tests, and stubs: `ServerClock` and a local-first `CloudSaveStore` work against the transport, `RemoteGachaService` throws `Unsupported`. | P1 (skeleton in P0) |
| `HeroCat.Server` | The server-side code that must be bit-identical with the client: 03's generator, 01 section 8's `Pull`, the pity table. Shared source with `HeroCat.Sim` if the backend runs .NET; a port with cross-language test vectors otherwise (section 4.3). | P1 |

```csharp
namespace HeroCat.Sim.Services;

public sealed class Services
{
    public IClock            Clock        { get; init; }
    public IGachaService     Gacha        { get; init; }
    public ISaveStore        SaveStore    { get; init; }
    public IAnalytics        Analytics    { get; init; }
    public IRemoteConfig     RemoteConfig { get; init; }
    public IReceiptValidator Receipts     { get; init; }

    // P0. Everything runs on the device.
    public static Services Local(string dataDir, Simulation sim, ContentDb db)
    {
        var clock = new LocalClock();
        return new Services
        {
            Clock        = clock,
            Gacha        = new LocalGachaService(sim, db),
            SaveStore    = new FileSaveStore(Path.Combine(dataDir, "saves"), clock, db),
            Analytics    = new FileAnalyticsSink(Path.Combine(dataDir, "analytics"), clock),
            RemoteConfig = new NoRemoteConfig(),
            Receipts     = new DisabledReceiptValidator(),
        };
    }

    // P1. Lives in HeroCat.Remote as a static factory, since Services cannot reference that assembly:
    // RemoteGameServices.Create(string dataDir, IRemoteTransport transport, RemoteOptions o = null, IAnalytics a = null)
}
```

The bot harness (`specs/09`) builds its own `Services` with `FakeClock`, `LocalGachaService`, an in-memory `ISaveStore` and `BotAnalyticsSink`.

Shared error type, used by every interface (section 9 gives the per-operation policy):

```csharp
public enum ServiceErrorKind { Offline, Timeout, Rejected, Conflict, Unsupported, Internal }

public sealed class ServiceException : Exception
{
    public ServiceErrorKind Kind { get; }
    public string Code { get; }          // machine-readable, e.g. "CannotAfford", "PaymentMismatch", "BadBanner"
    public bool Retryable => Kind is ServiceErrorKind.Offline or ServiceErrorKind.Timeout or ServiceErrorKind.Internal;
}
```

## 2. Summary table

| Interface | Local (P0) | Remote (P1) | Owns |
|---|---|---|---|
| `IClock` | `LocalClock`, `FakeClock` | `ServerClock` | Server time for the economy (06 section 11). |
| `IGachaService` | `LocalGachaService` | `RemoteGachaService` | Who rolls, who holds the `gacha_hero` seed, who debits gems. |
| `ISaveStore` | `FileSaveStore`, `MemorySaveStore` (tests) | `CloudSaveStore` | Where the save lives; conflict rule. |
| `IReceiptValidator` | `DisabledReceiptValidator`, `FakeReceiptValidator` (debug) | `RemoteReceiptValidator` | IAP verification and gem grants. |
| `IRemoteConfig` | `NoRemoteConfig`, `FileRemoteConfig` (testers) | `RemoteConfig` | Content table overlay by `content_version`. |
| `IAnalytics` | `FileAnalyticsSink`, `BotAnalyticsSink`, `NullAnalytics` | `RemoteAnalyticsSink` | Event delivery. |
| `IAccountService` | `NoAccount` (anonymous local) | `RemoteAccountService` | Account id, ranked genesis and seed issue (`specs/10` section 3). |
| `IProgressCertifier` | `NullCertifier` | `RemoteProgressCertifier` | Segment verification and the certified record (`specs/10` section 4). |
| `ILeaderboardService` | none | `RemoteLeaderboardService` | Read-only boards fed by certification (`specs/10` section 7). |
| `IPvpService` | none | `RemotePvpService` | Matchmaking, battle resolution, rating (`specs/10` section 9; interface written with the PvP rules). |

## 3. `IClock`

Defined in 06 section 11.1 with `LocalClock` and `FakeClock`. Repeated here for the remote implementation.

```csharp
public interface IClock
{
    long NowServerSeconds { get; }
    bool IsAuthoritative  { get; }
}
```

### 3.1 `ServerClock` (P1)

```csharp
public sealed class ServerClock : IClock
{
    // offset = serverNow - monotonicNow, captured at the last successful sync.
    long?  offsetSec;                 // null until the first sync in this process
    long   floorSec;                  // Profile.LastSavedAtServerSec at load; time never reads below this
    readonly Func<long> mono = () => Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public bool IsAuthoritative => offsetSec.HasValue;
    public long NowServerSeconds => Math.Max(floorSec,
        offsetSec is long o ? mono() + o : DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public async Task SyncAsync(ITimeApi api, CancellationToken ct)
    {
        long t0 = mono();
        long server = await api.GetServerTimeAsync(ct);       // one round trip; the BaaS call from section 10
        long t1 = mono();
        offsetSec = server - (t0 + (t1 - t0) / 2);            // midpoint estimate; +/- half the RTT is fine at 1 s resolution
    }
}
```

- Sync at session open, on every `OnResume`, and every `CLOCK_RESYNC_SEC = 900` while in the foreground. Between syncs time advances by the monotonic clock, so a device clock change mid-session has no effect.
- Until the first sync succeeds, `IsAuthoritative` is false and the device clock is served, floored at the last save (06 section 11.2 rule 6). Nothing else in the sim changes behaviour on `IsAuthoritative`; it exists for analytics and for the cloud-save tiebreak (5.2), which must not trust a non-authoritative stamp against an authoritative one.
- A sync whose result differs from the current estimate by more than `CLOCK_JUMP_WARN_SEC = 300` emits `clock_anomaly{kind: resync, seconds}`.

## 4. `IGachaService`

### 4.1 Contract

```csharp
public enum PaymentKind : byte { FreeDaily, FreeBanked, Gems }     // 04 section 7.5 priority order

public sealed record PullRequest(string BannerId, int Count, PaymentKind Payment, string IdempotencyKey);
                                                                     // Count is 1 or 10; Payment is Economy.QuotePayment(Count)

public sealed record PullOutcome(
    PullResult[] Results,        // 01 section 8, length == Count
    BannerState  Banner,         // pity and TotalPulls after the batch
    Currencies   Balances,       // after the batch (remote: the server ledger)
    long         ServerTimeSec);

public interface IGachaService
{
    // Rolls Count pulls on BannerId, applies them to the Simulation it was constructed with
    // (copies, shards, pity, counters, balances, quest progress), and returns what happened for display.
    // Exactly once per IdempotencyKey: replaying a key returns the original outcome and applies nothing.
    // Throws ServiceException; never partially applies.
    Task<PullOutcome> PullAsync(PullRequest req, CancellationToken ct);
}
```

Both implementations own applying the result to the sim, so the caller (`PullFlow`, 4.4) is identical in both modes and `PullOutcome` is for the reveal screen only.

### 4.2 `LocalGachaService` (P0)

Runs 01 section 8 on the local `gacha_hero` stream, synchronously, inside a completed `Task`.

```csharp
public sealed class LocalGachaService : IGachaService
{
    readonly Simulation sim; readonly ContentDb db;
    (string Key, PullOutcome Outcome)? last;     // one-deep replay cache; enough because PullFlow allows one pull in flight

    public Task<PullOutcome> PullAsync(PullRequest r, CancellationToken ct)
    {
        if (last is { } l && l.Key == r.IdempotencyKey) return Task.FromResult(l.Outcome);

        var banner = db.Banners[r.BannerId] ?? throw new ServiceException(ServiceErrorKind.Rejected, "BadBanner");
        if (sim.Economy.QuotePayment(r.Count) != r.Payment) throw new ServiceException(ServiceErrorKind.Rejected, "PaymentMismatch");
        if (sim.Economy.PayForPulls(r.Count) == PullPayment.CannotAfford) throw new ServiceException(ServiceErrorKind.Rejected, "CannotAfford");

        var st  = sim.BannerView(r.BannerId);                        // 01's BannerState view over GameState (06 section 3.1)
        var rng = sim.Rng["gacha_hero"];
        var results = r.Count == 10 ? Gacha.Pull10(banner, st, rng)
                                    : new[] { Gacha.Pull(banner, st, rng, forceEplus: false) };
        foreach (var _ in results) sim.Economy.Progress(QuestKind.Pulls, 1);   // 04 section 7.2

        var outcome = new PullOutcome(results, st.Persisted with { }, sim.State.Currencies with { }, sim.Clock.NowServerSeconds);
        last = (r.IdempotencyKey, outcome);
        return Task.FromResult(outcome);
    }
}
```

`Economy.QuotePayment(count)` returns the kind `PayForPulls` would charge (the priority rule of 04 section 7.5) without charging. The client computes it to show "Free pull" or "100 gems" on the button and sends it in the request; the service rejects a mismatch rather than silently charging something else, which is the behaviour the remote server needs and the local one mirrors so the client's handling is tested in P0.

### 4.3 Remote contract (P1)

The server owns the `gacha_hero` seed and counter for each profile (01 section 6). The client never receives the seed.

```
POST /v1/gacha/pull
Headers: Authorization (BaaS session), Idempotency-Key: <key>
Body:    { bannerId, count, paymentKind, clientContentVersion }

200 OK
{ results: [ { heroId, tier, isNew, levelBefore, levelAfter } ],      // length == count
  banner:  { pityCounter, totalPulls },
  balances:{ gems, freePullsDaily, freePullsBanked, shards },
  serverTimeSec }

402 CannotAfford      the server ledger cannot pay with paymentKind's priority rule
409 PaymentMismatch   paymentKind differs from what the server would charge (client out of date; refresh balances and re-quote)
400 BadBanner | BadCount
423 PullInFlight      another request for this profile is being processed (client bug; treat as Timeout and retry the same key)
5xx / network         retryable
```

Server rules:
1. Per-profile mutex around the whole handler, so two requests cannot both roll against the same counter.
2. Look up `Idempotency-Key` first. A hit returns the stored response unchanged, with no roll and no debit. Keys are stored for `IDEMPOTENCY_TTL_H = 48`.
3. Run 04 section 7.5 `PayForPulls` against the server ledger; then 01 section 8 `Pull` / `Pull10` against the server-side `(seed, counter)`; persist ledger, banner state, counter and the response under the key in one transaction.
4. The algorithm must be bit-identical with the client's `LocalGachaService` so that the bot harness, which runs locally, predicts live rates. Either the server runs .NET and compiles `HeroCat.Server` from the same sources, or the generator (`specs/03`) and `Pull` are ported and both sides pass `Fixtures/Gacha/vectors.json`: 20 seeds, 1000 pulls each, the expected `(tier, heroId, pityAfter)` sequence. A port that cannot pass that file is not acceptable. This is a selection criterion in section 10.
   File format (`format: 1`): header `contentHash`, `bannerId`, `pullsPerSeed`; then `vectors[]`, one per profile seed 1..20, each with `streamSeed` (the `gacha_hero` stream seed, a decimal string because it exceeds 2^53) and `batches[]`. Each vector starts from pity 0, no copies and counter 0, and runs its batches in order; a batch of length 10 is a guaranteed 10-pull, length 1 a single pull. Each pull is `[tier, heroId, pityAfter]`. Generated by `dotnet run --project src/HeroCat.Bots -- gacha-vectors --update` (`--check` verifies); `HeroCat.Sim.Tests` replays it against `LocalGachaService` and `HeroCat.Server.Tests` against the server handlers.

Client-side application on 200: set `Banners[id] = banner`; set `Rng["gacha_hero"].Counter = 3 * totalPulls` (keeps 06 rule P10 true; the local seed is unused in remote mode); for each result add one copy through the same dupe-to-level path `Pull` uses, so levels and shards agree with the server; replace `Currencies.Gems`, `FreePullsDaily`, `FreePullsBanked`, `Shards` with `balances` (the server ledger wins); `Progress(Pulls, count)`; clear `PendingPull`.

### 4.4 Idempotency and the pending pull

```
PullFlow(bannerId, count):
  if state.PendingPull != null: return Resolve()           // never two in flight
  key = Guid.NewGuid().ToString("N")
  payment = economy.QuotePayment(count)
  state.PendingPull = new PendingPull { key, bannerId, count, payment.ToString(), clock.Now }
  store.Save(state)                                         // the intent is on disk before the request leaves
  outcome = await gacha.PullAsync(new PullRequest(bannerId, count, payment, key))   // with retries, below
  state.PendingPull = null                                  // the service already applied the outcome
  store.Save(state)
  Emit PullsCompleted(outcome)                              // 07's reveal

Resolve():   // at session open if PendingPull != null, and from the button while pending
  same call with the saved key; the server replays or completes; apply; clear; save
```

Retry policy for `Retryable` errors: 1 s, 2 s, 4 s, 8 s, then stop with the pending pull still saved. `Rejected` errors clear the pending pull without applying anything (nothing was charged) and surface the code. In local mode the pending pull exists for a few microseconds and the replay cache is exercised only by tests; that is fine, the point is that the client code path is the same.

Why a key per request rather than `(profileId, totalPullsBefore)`: the latter is also unique per lineage, but a cloud-save conflict (5.2) can rewind `totalPulls` on one device and the key would then collide with a different intent. A random key cannot.

### 4.5 What the client displays while waiting

Owned by `specs/07`; the contract it must honour:

- Request fires on button press. The reveal sequence opens with a pre-roll animation of `GACHA_PREROLL_MS = 800`; an outcome arriving within it feels instant.
- If no outcome by the end of the pre-roll, hold the pre-roll loop with "Summoning...". After `GACHA_SLOW_MS = 3000` add "Taking longer than usual". After the retry policy is exhausted (about 15 s): "Your summon is saved and will be revealed when you are back online", close the reveal, keep the pull button in a "Pending" state that calls `Resolve()`.
- Balances shown as already debited while pending (the local `PayForPulls` ran in local mode; in remote mode the client shows `Gems - cost` provisionally and replaces it with `balances` on 200, or restores it on `Rejected`).
- Never offer a "Try again" that would create a new key while one is pending. Never show "Failed" without having asked the server (replay) whether the pull happened.

## 5. `ISaveStore`

### 5.1 Contract

```csharp
public enum LoadStatus { Loaded, LoadedFromBackup, NewProfile, Conflict, TooNew }

public sealed record SaveSummary(int Stage, long Gems, long TotalPulls, long LastSavedAtServerSec, Guid DeviceInstallId, long SaveSequence);

public sealed record LoadResult(
    LoadStatus Status,
    GameState? State,                    // the chosen state; null for TooNew
    GameState? Other,                    // Conflict only: the losing candidate, for the prompt
    IReadOnlyList<string> Repairs);      // 06 section 7.2 rule ids that fired

public interface ISaveStore
{
    Task<LoadResult> LoadAsync(long nowServerSec, ContentDb db, CancellationToken ct);
    void             Save(GameState state);                      // synchronous; 06 section 5 (stamp, atomic write)
    Task             ResolveConflictAsync(GameState chosen, GameState rejected, CancellationToken ct);   // local: no-op
}
```

`Save` is synchronous because the pause path must finish before the OS callback returns (06 section 5.3). A remote store does the local write synchronously and the upload asynchronously.

### 5.2 Implementations

**`FileSaveStore(dir, clock, db)`** (P0): exactly 06 sections 5 to 7. `ResolveConflictAsync` is a no-op; `Status` is never `Conflict`.

**`MemorySaveStore`** (tests, bot): holds the last `byte[]` and the previous one; same `Decode` / `Migrate` / `Validate` path so round-trip tests run without a filesystem.

**`CloudSaveStore(FileSaveStore local, ICloudBlobApi remote, IClock clock)`** (P1):

Save:
1. `local.Save(state)` always, first.
2. Enqueue an upload of the same bytes with metadata `{ profileId, deviceInstallId, saveSequence, lastSavedAtServerSec, schemaVersion, sha256 }`. Coalesce to at most one upload per `CLOUD_UPLOAD_MIN_SEC = 60`, plus one on pause. Failures stay queued; the queue holds one item (the latest).

Load, with `L` the local candidate (06 load path) and `R` the remote one (fetched with `CLOUD_FETCH_TIMEOUT_SEC = 5`, else treated as absent and `cloud_stale` emitted):
1. Only one exists: use it. `R` only: write it locally, `Status = Loaded`.
2. Same `sha256`: use `L`.
3. Same `DeviceInstallId`: the higher `SaveSequence` wins, no prompt (same device lineage; the other is just older).
4. Different devices: **last writer wins by `LastSavedAtServerSec`** (server time, stamped by `IClock`). Ties: higher `SaveSequence`, then `R`. A stamp made with `IsAuthoritative == false` loses to an authoritative one regardless of value (06 section 11.2 rule 6).
5. If the loser is meaningfully ahead on anything the player would notice (`Stage` higher, `TotalPulls` higher, or `Gems` higher by at least `PULL_COST_GEMS`), `Status = Conflict` with both states. The client shows the two `SaveSummary`s (stage, gems, pulls, last saved, "this device" or "other device") with the LWW winner preselected. `ResolveConflictAsync(chosen, rejected)` writes `chosen` locally, uploads it, and keeps `rejected` as `slot0.conflict-{unix}.sav` for `CORRUPT_KEEP_DAYS`.

Why last-writer-wins and not a merge: the save is a single RNG lineage. Two devices that both made pull `k` got the same result from the same counter; merging copy counts would double-count it, and merging counters is undefined. Picking one lineage and discarding the other is the only consistent operation. The prompt exists so the discard is the player's choice when it matters.

## 6. `IReceiptValidator` (P1)

```csharp
public enum StorePlatform : byte { AppleAppStore, GooglePlay }

public sealed record Receipt(StorePlatform Platform, string ProductId, string TransactionId, string Payload);
public sealed record ReceiptResult(bool Valid, string ProductId, long GemsGranted, string ServerGrantId);

public interface IReceiptValidator
{
    // Validates with the store and, if valid, credits the server gem ledger exactly once per TransactionId.
    // The client then calls Economy.GrantGems(GemSource.Iap, GemsGranted) (04 section 11.4).
    Task<ReceiptResult> ValidateAndGrantAsync(Receipt receipt, string idempotencyKey, CancellationToken ct);
}
```

- **`DisabledReceiptValidator`** (P0 release): throws `ServiceException(Unsupported, "NoIap")`. The store screen is not in P0 (00 section 1), so nothing calls it; it exists so the composition root has no null.
- **`FakeReceiptValidator`** (P0, `HEROCAT_DEBUG` only): returns `Valid` with `GemsGranted` from a local product table, for exercising the grant path in development builds.
- **`RemoteReceiptValidator`** (P1): posts the receipt; the server verifies with the App Store Server API or the Google Play Developer API, deduplicates on `TransactionId` (a replayed receipt returns the original grant and credits nothing), writes the grant to the ledger, and returns. The client treats a pending receipt like a pending pull: persist intent before the request, resolve at next session open. That requires a `PendingReceipt` field, which is a schema step when P1 arrives (06 section 8).

## 7. `IRemoteConfig` (P1)

```csharp
public sealed record ContentOverlay(string ContentVersion, string TablesJson, int MinBuildId, long FetchedAtServerSec);

public interface IRemoteConfig
{
    // Returns null when no overlay applies to this build, or when offline and nothing is cached.
    Task<ContentOverlay?> FetchAsync(string installedContentVersion, int buildId, CancellationToken ct);
}
```

- **`NoRemoteConfig`** (P0): returns `null`.
- **`FileRemoteConfig(path)`** (P0, testers): reads `persistentDataPath/content_overlay.json` if present. Lets a designer drop retuned tables onto a device without a build.
- **`RemoteConfig`** (P1): fetches from the BaaS remote-config store, caches the last good overlay on disk, returns the cache when offline.

Application rules, which `HeroCat.Client` follows regardless of implementation:
1. Applied at session start only, before `ContentDb` is built. Never mid-session: a balance change under a running sim changes kill times, and therefore the catch-up formula and the wall condition, under the player's feet.
2. An overlay is a full set of tables for one `content_version` (05's pipeline output), not a patch. It goes through the 05 validator; on failure the shipped tables are used and `content_overlay_rejected` is emitted.
3. `MinBuildId` gates rollout; an overlay for a newer build is ignored.
4. A cached overlay applies immediately; a newly fetched one applies next session.
5. The save's `ContentVersion` (06 section 3.1) records which tables wrote it, so analytics can segment by tuning.

## 8. `IAnalytics` (P0, local log sink)

### 8.1 Contract

```csharp
public abstract record AnalyticsEvent(string Name);

public interface IAnalytics
{
    void Track(AnalyticsEvent e);     // non-blocking, never throws, never calls back into the sim
    void Flush();                     // on pause and quit
}
```

The sink adds the envelope: `ts` (server seconds), `sessionId` (GUID per process), `profileId`, `contentVersion`, `buildId`, `debugTainted`, `clockAuthoritative`. `profileId` is written as-is by the local sink and hashed (SHA-256, salted per build) by the remote sink.

### 8.2 Event list

Names are snake_case. Day index is `(ts - CreatedAtServerSec) / DAY_SECONDS`.

| Event | Properties | Feeds |
|---|---|---|
| `session_start` | `stage`, `dayIndex`, `offlineElapsedSec` | Session counts, return cadence |
| `session_end` | `durationSec`, `stageStart`, `stageEnd`, `kills` | Active time per day |
| `stage_reached` | `stage`, `dayIndex`, `lifetimeActiveSec` | **Stage reached per day** (max per `dayIndex`), the 00 section 2.6 target table |
| `wall_hit` | `stage`, `dps`, `bossHp` | First failed boss attempt on a stage |
| `wall_broken` | `stage`, `wallSec` (server time from `wall_hit`), `activeSecDuringWall`, `attempts` | **Time-to-wall and wall length**, 02 section 9 criterion 4 |
| `currency` | `currency` (gold, gems, freeDaily, freeBanked, shards), `delta`, `reason`, `stage` | **Currency sources and sinks**. Positive delta is a source, negative a sink. `reason` is one of: kill, boss_kill, offline, gear_sell, stat_level, enhance, quest, login, first_clear, chest, pull, tutorial, debug, iap. The two kill reasons are summed by the sim and emitted once per `CURRENCY_AGG_SEC = 60`; the rest emit per occurrence. |
| `pull` | `bannerId`, `count`, `payment`, `tiers[]`, `lplus`, `pityAfter`, `totalPullsAfter` | **Pull counts**, realised L+ rate against 01 section 3.2 |
| `enhance` | `slot`, `levelBefore`, `success`, `cost` | Enhancement spend share |
| `gear_equipped` | `slot`, `rarity`, `tier`, `replacedRarity`, `replacedTier` | Gear lag per slot |
| `class_advance` | `index`, `stage`, `dayIndex` | Milestone timing |
| `offline_catchup` | `elapsedSec`, `creditedSec`, `capped`, `gold`, `stage` | Offline share of income; cap hit rate |
| `daily_rollover` | `dayKey` | |
| `quest_completed` | `questId` | Daily completion rates |
| `chest_claimed` | | Chests per day against the 01 section 5 assumption of two |
| `save_fallback`, `save_corrupt`, `save_repaired` (`rule`), `save_write_failed` (`consecutive`), `migration_applied` (`from`, `to`) | | Save health |
| `clock_anomaly` | `kind` (backwards, gap, resync), `seconds` | Clock health |
| `content_overlay_rejected` | `contentVersion` | |
| `debug_used` | `command` | Taints the profile |
| `plausibility_flag` | `stage`, `dayIndex`, `expectedStage` | Emitted by the sink when `stage > 2 * bot curve stage for dayIndex` (02 section 6 table, interpolated); marks a probable edited save |

### 8.3 Sinks

- **`FileAnalyticsSink(dir, clock)`** (P0): NDJSON, one file per day `events-{yyyyMMdd}.ndjson`, buffered writer flushed on `Flush()` and every 100 events, files older than 14 days deleted at startup, total capped at 20 MB (oldest first). A designer pulls the files off the device and runs the `specs/09` aggregation script over them.
- **`BotAnalyticsSink`** (P0, `specs/09`): in-memory list; the harness aggregates it into the acceptance table. `stage_reached` and `currency` are the two events it needs.
- **`NullAnalytics`**: drops everything. Default in unit tests.
- **`RemoteAnalyticsSink(FileAnalyticsSink inner, IAnalyticsApi api)`** (P1): writes through to the file sink, batches uploads of 100 events or 30 s, keeps at most `ANALYTICS_QUEUE_MAX = 10000` events queued offline and drops the oldest beyond that.

## 9. Error model and offline tolerance

Rules of thumb: anything client-authoritative (section 11) never waits on the network; anything that touches the server's ledger or seed blocks when offline and persists its intent so it can be resolved later; everything else degrades to a cached or local answer.

| Operation | Local mode (P0) | Remote mode, offline (P1) | Why |
|---|---|---|---|
| Combat tick, stage push, gold, stat levels, enhancement, gear equip and sell, class advance, quest progress | No service call | No service call | Client-authoritative (section 11). |
| Save | Local write | Local write; upload queued | A save must never fail because the network did. |
| Load | Local | Local, after a 5 s cloud fetch timeout; conflict check deferred to the next online load | Start the game in bounded time. |
| Daily rollover, idle chest, offline catch-up | Local clock | Non-authoritative clock, floored at last save (06 section 11.2 rule 6) | Bounded loss (one day's gems, 8 h gold); cloud LWW reconciles. |
| Gacha pull | Synchronous | **Blocks.** Button shows "Connect to summon". A pending pull resolves on reconnect. | The server holds the seed (01 section 6). Pre-issued offline results would reveal future pulls. Pulls are six a day and need a connection most players have. |
| Receipt validation | Unsupported | **Blocks**, intent persisted | Store purchases are online by definition. |
| Remote config | None | Cached overlay, else shipped tables | Content is always available. |
| Analytics | File | File, upload queued, drop oldest past the cap | Never block, never grow unbounded. |
| Clock sync | None | Retry on resume and every `CLOCK_RESYNC_SEC` | |

Exception mapping in `HeroCat.Remote`: no connectivity or DNS failure -> `Offline`; HTTP timeout -> `Timeout`; 408 -> `Timeout`; 409 -> `Conflict`; other 4xx -> `Rejected` with the body's `code` (else `Http<status>`); 5xx and anything unexpected -> `Internal`. The sim and the client only ever see `ServiceException`.

## 10. BaaS comparison for v1

Assumptions behind the cost column: 10,000 DAU, each with 2 sessions a day, 6 pulls, about 20 cloud-save uploads (one per minute coalesced across a 2 h session), 2 clock syncs, one remote-config fetch, and about 400 analytics events. That is roughly 6 M API calls and 4 M analytics events a month. Prices are list prices as understood in October 2026 and must be re-checked against each vendor's pricing page before signing; treat the column as an order of magnitude.

| | Unity Gaming Services (UGS) | PlayFab (Microsoft) | Firebase + Cloud Functions | Nakama (Heroic Labs) |
|---|---|---|---|---|
| Auth | Unity Authentication: anonymous, Apple, Google Play, custom OpenID. Free. | Anonymous device id, Apple, Google, many others. Included. | Firebase Auth: anonymous, Apple, Google. Free at this scale. | Device, email, Apple, Google, custom. Included. |
| Cloud save | Cloud Save: per-player key-value JSON, plus file storage for larger blobs. Our 4 KB save fits a single key. | Player Data (title-scoped key-value, JSON values) and Entity Objects. Fits. | Firestore document per profile. Fits. Optimistic concurrency via document versions. | Storage engine: collections of JSON objects per user with a version for optimistic writes and read/write permissions. Fits well; the version field maps directly onto 5.2. |
| Server time | No dedicated endpoint; a one-line Cloud Code script returning `Date.now()`. | `GetTime` client API, built in. | No dedicated endpoint; a Cloud Function, or `serverTimestamp()` on a write. | No dedicated endpoint; a one-line RPC. |
| Remote config | Remote Config service: key-value JSON with campaigns and overrides. Free. | Title Data: key-value JSON; no staged rollout without CloudScript. | Remote Config: strong, with conditions and percentage rollouts. Free. | None built in; a storage object with public read, or an RPC. |
| Receipt validation | Economy service redeems Apple and Google Play purchases server-side and grants configured rewards. | `ValidateIOSReceipt`, `ValidateGooglePlayPurchase`, built in. | Not built in; write it in a Cloud Function against the store APIs (about a day), or use RevenueCat. | `ValidatePurchaseApple` / `ValidatePurchaseGoogle` built in, including subscriptions. |
| Server runtime for the gacha roll (4.3 rule 4) | Cloud Code: JavaScript, or C# modules (Cloud Code Modules run .NET). The C# module path lets `HeroCat.Server` compile from the shared sources. | CloudScript (JavaScript) or Azure Functions (C# supported). Azure Functions gives the shared-source path. | Cloud Functions: Node.js, Python, Go. No .NET; the generator and `Pull` would be ported to Go and held to the test vectors. | Go plugins, TypeScript or Lua runtime. Go port with exact uint64 arithmetic is straightforward; TypeScript needs BigInt for the generator and is slower. |
| Analytics | UGS Analytics, free tier adequate at this scale. | PlayStream events and basic dashboards; export for real analysis. | Firebase Analytics / Google Analytics, free, with BigQuery export. Best of the four. | None; bring your own (the file sink plus an export job). |
| Estimated monthly cost at 10 k DAU | Low. The services used here have free tiers sized for small titles; Cloud Code and Cloud Save are metered beyond them. Estimate 0 to 50 USD. | Standard tier has a base fee (about 99 USD) plus meters; estimate 150 to 400 USD. | Blaze pay-as-you-go. Firestore reads/writes at this volume are a few dollars; Functions within or just past the free tier; estimate 20 to 60 USD. | Self-hosted: software is Apache-2.0, infrastructure for one small VM plus managed Postgres about 50 to 150 USD. Heroic Cloud managed hosting starts in the low hundreds. |
| Lock-in and risk | Single-vendor with the engine. Unity's commercial terms have changed abruptly before. Everything we use is reachable through this spec's interfaces, so a swap is confined to `HeroCat.Remote`. | Mature, stable pricing; heavier SDK; Azure account required. | Google lock-in on Firestore data model; everything else is portable. | No lock-in; we own the server. Ops burden: upgrades, backups, on-call. |

Recommendation for v1: **Unity Gaming Services**, with **Nakama** as the fallback if UGS Cloud Code Modules cannot run `HeroCat.Server` unchanged or if commercial terms move.

Reasons:
1. One SDK already in the Unity project covers all six interfaces, with no ops.
2. Cloud Code Modules in C# compile the gacha roll from the same sources as the client, which satisfies 4.3 rule 4 without a port and without the cross-language vector file. That is the single largest correctness risk in adding a server, and UGS and PlayFab (via Azure Functions) are the two that remove it.
3. Cost at 10 k DAU is the lowest of the four.
4. Receipt validation through Economy and remote config are built in, so P1 does not grow a custom backend.

Against PlayFab: the base fee and the Azure Functions deployment pipeline are more than this project needs at 10 k DAU; it becomes the right answer if the game needs its live-ops tooling. Against Firebase: no .NET runtime, so the gacha roll is a Go port with a vector-file contract, and receipts are custom; it is the right answer if the team already standardises on Firebase Analytics. Against Nakama as first choice: the ops burden lands on a team that has none, and we gain nothing from owning the server until P2 (leaderboards, guilds), where it becomes attractive.

Decision checkpoint before P1 starts: a two-day spike that deploys the `Pull` test vectors as a UGS Cloud Code Module and reproduces `Fixtures/Gacha/vectors.json`. Pass means UGS; fail means Nakama with a Go port.

## 11. What stays client-authoritative, and why

Client-authoritative in P0 and P1 (no server call, the save is the truth, cloud save stores it as a blob):

| Domain | Specifically | Why the client keeps it |
|---|---|---|
| Combat | Ticks, kill timing, boss attempts, walls | The sim must run at `TICK_HZ` on the device for the game to exist; a server cannot tick 10 k players at 10 Hz for nothing, and 02 section 7 already rejects simulating progress server-side. |
| Stages | `Stage`, `KillsInStage`, first clears, class advances | Derived from combat. A plausibility check (8.2 `plausibility_flag`) is the only server-side interest until P2. |
| Gold and gold sinks | Kill gold, offline catch-up, stat levels, enhancement | Gold buys nothing real and touches no other player. Its only value is to the player's own curve. |
| Gear | Drops, rarity, equip, sell | Same. The `gear_drop`, `gear_rarity` and `enhance` streams stay local with their counters in the save. |
| Hero levels and equips | `Copies`, `Equipped` | Derived from pulls. In remote mode the pulls come from the server and the client mirrors them. |
| Daily quest progress and idle chest | Counters and timers | Driven by server time via `IClock`; the gems they grant are small and bounded per day. |

Server-authoritative from P1 (the seams in sections 3 to 7):

| Domain | Why it moves |
|---|---|
| Server time | Every daily and offline rule depends on it; the device clock is the one input a player can change for free. |
| Gacha roll and the `gacha_hero` seed | 01 section 6: no result may depend on anything the client controls, and the seed must not be readable. |
| Gem and free-pull ledger | Gems are bought with money. Once receipts exist, the balance must be where the receipts are. |
| Receipts | Store APIs require a server secret. |
| Content overlay | Tuning without a build. |

Why this split is safe: P0 and P1 have no shared surface between players (00 section 1: leaderboards, PvP, guilds are P2). A cheater who edits gold or stage harms only their own experience and the tuning data, and 06 section 12 covers the data side. The cost of defending combat server-side is a full server sim or a replay verifier, neither of which pays for itself before P2.

**Revised by `specs/10`.** The table above describes single-player and unranked play. Leaderboards, PvP and paid balances need values the server believes, so for ranked profiles combat, gold and gear are *server-verified* rather than client-authoritative: the client uploads a segment of its command log, the server replays it on the same `HeroCat.Sim` source and accepts the result only if the state hashes match (`specs/10` section 4). The decision recorded here earlier (replay verifier versus plausibility bound) is made: replay, because the sim is deterministic and a day of play replays in tens of milliseconds. Plausibility remains only as the analytics flag in `specs/06` section 12.2.

## 12. Open decisions

1. **Local gacha in remote mode as a fallback.** Whether a build configured for remote gacha may fall back to the local roll when the server is unreachable for more than some hours. Current answer is no (section 9), because the local seed would have to be trusted again. Revisit if playtests show the block is felt.
2. **Payment ledger split.** In P1, gems move server-side while gold stays local. Confirm free-pull credits (daily and banked) move with gems, as 4.3 assumes, since they pay for pulls.
3. **Cloud-save conflict threshold.** The "meaningfully ahead" rule in 5.2 (stage, pulls, `PULL_COST_GEMS` of gems). May need to include gear if playtesters find silent overwrites of a Legendary drop.
4. **Analytics profile id hashing.** Salted per build (8.1) means the same player cannot be followed across builds in the remote dataset. Decide whether that is wanted before P1.
5. **UGS spike result** (section 10). The recommendation is conditional on the two-day spike.
6. **`PendingReceipt` schema step.** Add it to v1 now (a nullable field costs nothing and avoids a migration) or wait for P1. Leaning add now, alongside `PendingPull`.
7. **Server-side plausibility bound.** Whether `plausibility_flag` should also be computed server-side on cloud-save upload in P1, so flagged profiles can be excluded from live tuning data without trusting the client's own flag.
8. **TypeScript vs Go for a Nakama port.** Only relevant if the fallback is taken; Go for exact uint64 arithmetic unless the team strongly prefers TypeScript.
9. **Competitive-play seams.** `IAccountService`, `IProgressCertifier`, `ILeaderboardService` and `IPvpService` are fixed in shape by `specs/10`; their contracts are written when P1a to P1d start. The BaaS spike in section 10 must now also cover running the certifier (`Replay` plus `StateHash`) in the chosen server runtime, in addition to the gacha vectors.
