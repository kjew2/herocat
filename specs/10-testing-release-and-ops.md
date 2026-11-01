## 10 - Testing, Release and Ops

This spec says how Whisker Hero is proven, shipped and kept alive: the test pyramid per assembly with counts, the golden-file determinism test, the headless bot harness `Whisker.Bots` that is the acceptance test for every balance change (00 section 8 item 12), server load tests, client performance budgets, the CI/CD workflows, versioning and the compatibility matrix, the store release checklist, the soft-launch plan and its KPIs, live-ops runbooks, dashboards and alarms, and the 12-month roadmap. Constants are 00's names; harness-only constants are declared in section 3.2 and listed at the end.

Tiers covered: **M0** (Sim and Runtime tests, bots with the local seams, `pr.yml`, `content.yml`, a local Android build), **MVP** (Server tests, load tests, device farm, store builds, blue/green deploy, soft-launch KPIs, runbooks), **P1** (rotated seeds, lapsed-player bots, guild/leaderboard load profiles).

Depends on: 00 (sections 6.6, 8, 11), 01 (`SessionRunner`, network layer, error taxonomy), 02 (save schema, migrations, fixtures, clock seam), 03 (bot policy, layer shares, `Query`), 04 (pull algorithm, test vectors), 05 (fish model), 06 (quests, dungeons, offline), 07 (perf rules, screens), 08 (endpoints, plausibility checks, config), 09 (content, `progress_targets`, exporter).

## 1. Test pyramid

| Layer | Project / place | Count (MVP) | Runs | Budget |
|---|---|---|---|---|
| Sim unit | `src/Whisker.Sim.Tests` (NUnit) | ~420 | every PR | < 20 s |
| Runtime unit | `src/Whisker.Runtime.Tests` | ~180 | every PR | < 20 s |
| Server unit + integration | `src/Whisker.Server.Tests` (Testcontainers Postgres + Redis) | ~160 | every PR touching `src/Whisker.Server` or Sim | < 3 min |
| Bot acceptance + golden | `src/Whisker.Bots.Tests` | 6 ACs + 4 goldens + 28 unit | every PR touching Sim, content or bots | < 4 min |
| Unity EditMode | `unity/Assets/Tests/EditMode` | ~60 (view-model binding, formatting, `ScreenRouter`) | every PR touching `unity/` | < 2 min |
| Unity PlayMode smoke | `unity/Assets/Tests/PlayMode` | 4 | nightly and release builds | < 6 min |
| Device-farm smoke | Firebase Test Lab / AWS Device Farm, 4 devices | 1 scripted run per device | release candidates | < 30 min |
| Load | `src/Whisker.Server.LoadTests` (k6 scripts) | 3 profiles | weekly on stage, before every server release | 20 min |

Counts are targets at MVP exit; `pr.yml` fails if a project's test count drops below 90% of the number recorded in `tests/counts.json` (someone deleted tests to go green).

### 1.1 `Whisker.Sim.Tests`

Grouped by what they protect. Each group names the fixtures it uses; fixtures are JSON `GameState` bodies under `src/Whisker.Sim.Tests/Fixtures/` built by `Whisker.Runtime` in a `[OneTimeSetUp]` (Sim has no JSON; the test project references Runtime).

| Group | Tests | What they assert |
|---|---|---|
| Determinism | 18 | `Advance(N)` from the same state twice yields identical `StateHash`; the same with a recorded command log interleaved; parallel runs of four seeds equal sequential; no `float`, `Math.Pow/Exp/Log`, `DateTime`, `Random` symbol in the assembly (Roslyn analyzer test over the compiled IL via `Mono.Cecil`). |
| RNG stream isolation | 14 | Drawing from `fish_spawn` never changes the next `combat` value; each stream's `(seed, counter)` after N draws equals SplitMix64 reference vectors; stream counters persist through a save round trip; summon has no client stream in MVP builds (`Rng.Streams` has exactly 5 names). |
| Curve eval | 12 | `CurveDef.Eval` bit-equal to a 128-bit decimal reference for `enemy_hp`, `gold_per_kill`, `exp_per_kill`, every `training_curves` row and every `research` cost at `x` in `{0,1,2,9,10,99,100,549,550,8999,9000}`; `EvalSum` equals the loop sum within 1 ulp; `MaxAffordable` equals a linear scan. |
| Command validation | 70 | For every `Command` in 00 section 5.1 and 07's list: every rejection reason reachable (`NotEnoughGold`, `StatAtCap`, `BoardFull`, `TankEmpty`, `NotOwned`, `SlotLocked`, `AlreadyClaimed`, `DayKeyMismatch`, `TapRateExceeded`, ...); a rejected command leaves `StateHash` unchanged and emits exactly one `CommandRejected`. |
| Combat tick | 60 | 03 acceptance criteria 1-19 as tests: attack schedule at `ATK_SPD` 0/50/100, crit factor, skill cast order and cooldowns, MP pool and regen, fish missile damage queue, Tap Zap cap, kill -> loot -> stage advance, boss timer, wall, `AttemptBoss`, first-clear diamonds, `StageCapReached`. |
| Economy | 40 | `Wallet.Apply` reasons, every `Reason` value emitted by at least one path, Training bulk cost closed form equals charge, Research effects via `Query`, Awakening gates, hero level-up loop, limit break (04). |
| Summon (local) | 30 | 04 test vectors 1-16 through `LocalSummonService`: pity table, hard pity at 80, M share, 10-pull guarantee, banner level thresholds, pool index mapping, overflow Rock Candy. |
| Fish Board | 36 | 05 acceptance: release to random empty cell on `fish_spawn`, full board refuses, merge equal levels, cap behaviour, discovery once per level, merge coins, missile per merge, board passive DPS sum, tank refill from ticks. |
| Catch-up arithmetic | 22 | 06 section 7: `elapsed = clamp(now - lastTick, 0, OFFLINE_CAP_H*3600)`; below `OFFLINE_MIN_S` grants nothing; gold and EXP at `OFFLINE_RATE` of the current-stage kill rate (discrete kill time from `Query.KillTimeSeconds`); tank fills; no stage, drops or quest progress; backwards clock grants nothing; `RES_OFFLINE_CAP` extends the cap; two catch-ups in a row with no elapsed time grant zero. |
| Quests, dungeons, day keys | 48 | 06: `EnsureDay` resets and preserves the right fields; weekly key via `WEEK_EPOCH_OFFSET`; every `QuestGoal` increments from its event; `SetMax` goals; achievement rungs; dungeon ticket grant, run with a fixed seed is deterministic and the reward matches `dungeon_floors`; sweep rule; app open across rollover. |
| Content | 30 | 09 acceptance 2, 5, 6 (loader scope), 7, 8, 9, 11, 12, 13, 17, 18 as listed there. |
| Snapshot and events | 20 | Per-tick snapshot fields match state; event buffer coalescing at `EVENT_COALESCE_AT` (01); every `Event` type serialises to the analytics envelope. |
| Perf (`[Category("Perf")]`) | 6 | 03 criterion 19: `Advance(1_000_000)` from stage 300 under 2 s Release; under 128 B allocated per kill with the default sink; `Build` under 30 ms. |

### 1.2 `Whisker.Runtime.Tests`

| Group | Tests | What they assert |
|---|---|---|
| Serializer round trip | 20 | 02 section 6: `Encode(Decode(x)) == x` bytes for every fixture; doubles round-trip (`R` format); enums as strings; unknown fields ignored on read and dropped on write; `de-DE` culture; `DefaultValueHandling.Include` writes zeros. |
| Migrations | 1 per version + 8 | 02 M1-M7: every `Fixtures/Saves/v{n}.json` migrates to `Current` with zero repairs; a `v{Current+1}` body raises `SaveTooNewException`; each step is idempotent; the migration log names every step. The "every fixture migrates" test is parameterised over the directory so adding `v{n+1}.json` is the only step when the schema bumps. |
| `LocalSaveStore` | 24 | 02 section 7: atomic write via temp + rename (kill the process between write and rename with a fault-injecting `IFileSystem` and reload); two slots rotate; checksum mismatch falls to `prev`; both corrupt -> `SaveCorruptException` with both files preserved under `corrupt/`; `SAVE_REPAIR_MAX` repairs then reject; a write fails `SAVE_FAIL_WARN_COUNT` times -> warning event. |
| `CloudSaveClient` | 26 | 02 section 9: upload triggers and `CLOUD_UPLOAD_MIN_SEC` debounce; `If-Match` ETag flow; conflict table (higher server time wins unless local `progressScore` exceeds by `CONFLICT_PROGRESS_MARGIN`); the user-facing conflict decision is surfaced exactly once; a 409 with a newer server save never overwrites the local without the decision; HMAC envelope present and valid. |
| Clock seams | 18 | 02 section 11: `ServerClock` keeps a monotonic offset, a backwards wall clock does not move `Now`; `CLOCK_JUMP_WARN_SEC`; `CLOCK_RESYNC_SEC` triggers a `/time` call; `FakeClock` is the only clock the bots see; `IClock.Now` is never read inside `Whisker.Sim` (analyzer test). |
| `SessionRunner` | 22 | 01 section 3.2: `MAX_TICKS_PER_FRAME` cap; pause on background, catch-up on resume; `IN_APP_GAP_SEC` rule; autosave every `AUTOSAVE_INTERVAL_SEC`; command log capped at `CMDLOG_MAX_BYTES`; events delivered on the main thread only. |
| `ContentLoader` | 16 | 09 section 10.3 steps 2-8 with a fake HTTP layer: hash failure keeps the bundle; `min_client_version` gate; atomic rename; hot-apply swaps strings and shop only; `ContentDb` identity stable across a config refresh. |
| Network | 20 | 01 section 5: retry/backoff (`HTTP_RETRY_*`), `requestId` reuse on retry, 426/409/503 mapping to the error taxonomy, offline-mode allow list. |
| Local seams | 14 | `LocalSummonService` equals the server algorithm on 04's test vectors; `LocalConfigService` returns "everything on"; `LocalInboxService` claim idempotent. |
| Analytics | 12 | 02 section 14: envelope fields, batching at `ANALYTICS_BATCH_MAX`, queue cap, `currency_changed` carries `reason`, `playerIdHash` never raw. |

### 1.3 `Whisker.Server.Tests`

Integration tests run against real Postgres and Redis in containers; unit tests mock nothing in the Sim path because the server references `Whisker.Sim` directly.

| Group | Tests | What they assert |
|---|---|---|
| Summon algorithm vs Python model | 20 | 08 section 4 pseudocode on 04's vectors; a 1,000,000-pull Monte Carlo per banner level compared to `tools/gacha_model.py` output within 0.3 pp per rarity and within 0.05 pp on L+ (the Python model is the oracle, run in CI from the same content); pity state is per banner and per player; 10-pull guarantee; the audit row count equals pulls; `rateTableHash` in the published rates endpoint equals the hash of `banner_levels` rows for that level. |
| Idempotency replay | 14 | 00 section 8 item 13: the same `requestId` twice returns the stored response and performs no second write (summon, inbox claim, save PUT, dungeon seed); a different body with the same `requestId` -> `409 IDEMPOTENCY_IN_FLIGHT` or `400 VALIDATION`; the key expires after `IDEMPOTENCY_TTL_H`. |
| Receipt validation stubs | 18 | 08 section 5 with recorded App Store Server API and Google Developer API responses: valid, already-consumed, refunded, sandbox-on-prod, expired JWS, malformed; grant lands in `inbox_items` exactly once; refund flips the row and queues a `REFUND` wallet delta; restore purchases re-grants nothing already granted. |
| Plausibility checks | 24 | 08 section 7, one test per named check: stage vs elapsed time vs re-simulated DPS bound, currency delta bounds, duplicate `requestId`, clock skew above `CLOCK_MAX_SKEW_ACCEPT_S`, save blob over `SAVE_BLOB_MAX_BYTES`; the action matrix (log / soft flag / rollback / ban) chosen per severity; a flagged save never silently overwrites the previous good one. |
| Auth | 16 | guest device id -> JWT, refresh, link Apple / Google, link conflict, multi-device, token revoke, `CLIENT_TOO_OLD` on every route except the allow list. |
| Save | 14 | ETag flow, conflict detection, blob to S3, metadata row, `SAVE_HISTORY_KEEP`, GDPR delete removes blobs and rows after `GDPR_DELETE_GRACE_DAYS`. |
| Config and content | 10 | `GET /config` body, `minContentVersion` map, rollout bucket is stable per player, maintenance flag, kill switches. |
| Rate limits | 8 | per-route limits from 08 enforced via Redis, 429 body shape. |
| Inbox and mail | 12 | grant, expiry at `INBOX_EXPIRY_DAYS`, claim-all idempotent, admin mail blast to a segment. |
| Dungeon seed | 8 | seed issued once per ticket, `DUNGEON_SEED_TTL_S`, result accepted only with the matching seed, re-simulation of the submitted run equals the claimed reward. |

### 1.4 Unity PlayMode smoke (4 tests, nightly and release)

1. **Boot to `SCR_MAIN` and run 60 s.** Launch with bundled content and a fresh profile in local mode; assert `SCR_MAIN` is active within 4 s, the sim advances at least 550 ticks in 60 s (allowing the `MAX_TICKS_PER_FRAME` cap), no error log, no exception, at least one `EnemyKilled`.
2. **Every screen opens.** `ScreenRouter.Push` each `SCR_*` with tier `<= MVP` from a stage-100 fixture; assert no missing prefab, no null binding, no unlocalised key (`[` prefix) in any visible `TextKeyLabel`.
3. **Save and reload.** Play 30 s, trigger autosave, quit to `SCR_BOOT`, reload; `StateHash` equals the saved hash.
4. **Offline modal.** Advance the `FakeClock` 3 h while backgrounded; on resume `SCR_OFFLINE_REWARD` shows with gold equal to `Catchup.Compute` for the fixture.

### 1.5 Device-farm smoke

Four reference devices, chosen to bracket the 00 section 3 minimums: a 2 GB Android 8 (API 26) low-end phone (Galaxy J-series class), a mid Android 12 (Pixel 4a class), an iPhone 8 on iOS 15, and a current iPhone. The run installs the release candidate, executes PlayMode test 1 as an instrumented build (`-runTests -testPlatform Android`), captures a frame-time trace and memory peak, and uploads the per-locale screenshots 09 section 13.5 needs. Pass criteria are the section 5 budgets for the device's class. Flaky-device policy: two runs; both must pass.

## 2. Golden-file determinism test

The sim's determinism claim (00 section 3: identical on x64, ARM64, IL2CPP) is a property only a cross-platform test can prove.

Inputs, committed under `golden/`:

| File | Content |
|---|---|
| `golden/seed7-commands.jsonl` | 30 bot-days of commands from `run --bot greedy-f2p --seed 7 --days 30 --record` (one `{tick, command}` per line, about 40k lines) |
| `golden/seed7-hashes.txt` | `day, tick, stateHash` after each day's catch-up, plus the final hash, as produced on CI x64 Linux |
| `golden/content-hash.txt` | the `content_hash` the run used |

Procedure (`Whisker.Bots golden --replay`): build `ContentDb`, create the seed-7 profile with `FakeClock` at the harness epoch, replay the command log by tick with `Advance` between commands, and print the day hashes. `StateHash` is a 64-bit FNV-1a over the canonical binary encoding of `GameState` that `Whisker.Sim.Hashing` produces (doubles as `DoubleToInt64Bits`, strings UTF-8, collections in storage order); it is not the JSON, so Newtonsoft formatting cannot cause a false mismatch.

Where it runs:

| Platform | How | Workflow |
|---|---|---|
| x64 Linux | `dotnet run` in `pr.yml` | every PR |
| x64 Windows, Apple ARM64 | `dotnet run` on hosted runners in `nightly-bots.yml` | nightly |
| IL2CPP Android ARM64 | the device-farm build includes `GoldenReplayTest` as a PlayMode test that reads the two golden files from `StreamingAssets/golden/` and asserts hash equality | release candidates and nightly on one device |
| IL2CPP iOS ARM64 | same, via Unity Cloud Build or the self-hosted mac | release candidates |

A mismatch is a P0 for the sim: first suspects are a `float`, a `Math.Pow`, a `Dictionary` enumeration order feeding arithmetic, or a `foreach` over a `HashSet`. The regenerate command (`golden --update`) is refused unless the content hash changed or the PR carries the label `sim-behaviour-change`.

## 3. Headless bot harness: `Whisker.Bots`

### 3.1 Layout

```
src/Whisker.Bots/                       net8.0 console; public types so Whisker.Bots.Tests drives it in-process
  Program.cs                            CLI (section 3.8)
  HarnessConstants.cs                   section 3.2
  BotRunner.cs                          day schedule (section 3.3)
  Policies/IBotPolicy.cs
  Policies/GreedyF2PBot.cs              03 section 8.3, step for step
  Policies/LazyF2PBot.cs                claims-only
  Policies/LightSpenderBot.cs           $20 / month
  Policies/WhaleBot.cs
  Measure/BotEventSink.cs               walls, L+ counts, pulls, highest rarity
  Measure/DayRow.cs
  Measure/LayerShares.cs                03 section 8.5
  Report/TableWriter.cs                 table / csv / json
  Report/Sweep.cs                       percentiles
  Acceptance/AcceptanceChecker.cs       section 3.6
  Golden/Golden.cs                      section 2 and 3.7
src/Whisker.Bots.Tests/
golden/
```

References `Whisker.Sim`, `Whisker.Runtime` (for `FakeClock`, `ContentLoader`, the local seams and the save round trip) and nothing else. No Unity.

### 3.2 Harness constants

```csharp
public static class HarnessConstants
{
    public static readonly DateTimeOffset Epoch = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);  // a Monday 09:00 UTC so weekly keys and the daily rollover (00:00 UTC) are both exercised
    public const int ActiveMinutesPerDay   = 120;   // 00 section 6.6
    public const int SessionsPerDay        = 2;     // two 60-minute sessions, 06 section 3; the gap lands across the 00:00 UTC rollover on alternate days
    public const int DecisionIntervalSeconds = 60;  // policy decides once per simulated minute
    public const int DefaultDays           = 30;
    public const string DefaultSeeds       = "1-20";
    public static readonly int[] SummaryDays = { 1, 2, 3, 5, 7, 10, 14, 21, 30 };
    public const double LightSpenderUsdPerMonth = 20.0;
    public const int WhaleDay1Usd          = 100;   // dia_6 once on day 1, then dia_4 every 3 days (section 3.4)
}
```

Profile seed for `--seed N` is `N`; `playerId` is a GUID derived from it, so a run is fully specified by `(bot, seed, days, content_hash)`.

### 3.3 Day schedule

```csharp
public RunResult Run(IBotPolicy policy, ulong seed, int days)
{
    var db    = _content;
    var clock = new FakeClock(HarnessConstants.Epoch);
    var state = GameState.NewProfile(db, PlayerIdFor(seed), seed, clock.Now);     // applies tutorial_steps grants (TUTORIAL_DIAMONDS, TUTORIAL_WEAPON_ID)
    var sink  = new BotEventSink();
    var sim   = new Simulation(db, state, clock, sink);
    var seams = LocalSeams.For(db, seed);                                        // LocalSummonService, LocalInboxService, LocalConfigService
    var ctx   = new BotContext(sim, state, db, clock, sink, seams);
    var rows  = new DayRow[days];
    int ticksPerDecision = HarnessConstants.DecisionIntervalSeconds * db.Constants.TickHz;   // 600
    int decisionsPerSession = HarnessConstants.ActiveMinutesPerDay / HarnessConstants.SessionsPerDay;   // 60

    for (int day = 1; day <= days; day++)
    {
        ctx.Day = day;
        for (int s = 0; s < HarnessConstants.SessionsPerDay; s++)
        {
            sim.Resume(clock.Now);                       // Runtime path: catch-up for the gap (OFFLINE_RATE, OFFLINE_CAP_H), EnsureDay, offline modal event
            policy.OnSessionStart(ctx);                  // claim offline reward, daily gift, claimable quests, tickets
            for (int d = 0; d < decisionsPerSession; d++)
            {
                sim.Advance(ticksPerDecision);
                clock.Advance(TimeSpan.FromSeconds(HarnessConstants.DecisionIntervalSeconds));
                policy.Decide(ctx);
            }
            policy.OnSessionEnd(ctx);                    // spend remaining, release the tank
            sim.Suspend(clock.Now);
            clock.Advance(SessionGap(day, s));           // 11 h, so two sessions per 24 h; the second gap spans 00:00 UTC on even days
        }
        rows[day - 1] = Snapshot(day, sim, sink, clock);
    }
    return new RunResult(policy.Name, seed, days, db.Version, db.ContentHash, rows, sink.Walls, sink.Spend);
}
```

Facts a developer can rely on: ticks per day `ActiveMinutesPerDay * 60 * TICK_HZ` = 72,000; a 30-day run is 2.16 M ticks; 20 seeds are 43.2 M ticks, which at 1 µs/tick is 43 s single-threaded and 15-25 s across 2-4 cores. Catch-up runs twice a day with 11 h gaps, never hitting `OFFLINE_CAP_H` (a `--lapsed` option in P1 inserts skipped days). Diamonds come from the sim's own quests, first clears and login (`--diamonds content`, default); `--diamonds flat` grants `DIAMONDS_PER_DAY_F2P` at day start for parity with `power_curve_model.py`.

### 3.4 Policies

```csharp
public interface IBotPolicy
{
    string Name { get; }
    void OnSessionStart(BotContext ctx);
    void Decide(BotContext ctx);           // once per DecisionIntervalSeconds of active play
    void OnSessionEnd(BotContext ctx);
}
```

| Policy | `Name` | Behaviour | Spend |
|---|---|---|---|
| Greedy F2P | `greedy-f2p` | 03 section 8.3 verbatim: Training `Max` on ATK then the stat with the best marginal log-DPS per gold; Research by the same rule with Fish Coins; Awakening when both gates pass; weapon tickets pulled immediately; diamonds -> 10-pulls on the banner with the lowest `pullsToNextLevelOrPity`-weighted expected log-DPS gain, singles only when a 10-pull is unaffordable and pity is within 10; `AutoEquipBest*` after every pull; dungeons run as soon as tickets exist, sweep when allowed; fish: release the whole tank, `MergeFish` greedy from highest level down (05's "attentive" policy at 60 merges/min cap); `AttemptBoss` when `Query.BossClearable`; claim every quest when claimable. | 0 |
| Lazy F2P | `lazy-f2p` | `OnSessionStart` only: claim offline reward, daily gift, quests, run or sweep dungeons, Training `Max` on ATK, pull tickets, release and merge once, then `Decide` does nothing. Models 00 pillar 1's "logs in to claim and spend" player. | 0 |
| Light spender | `light-spender` | Greedy F2P plus `pkg_monthly` on day 1 (drip via Inbox) and `dia_3` with first-purchase double on day 2, then `dia_2` on days 10 and 20; total `LightSpenderUsdPerMonth`. Purchases go through `LocalInboxService` so the IAP -> inbox -> claim path is exercised. | $20 |
| Whale | `whale` | Greedy F2P plus `dia_6` on day 1 and `dia_4` every third day; always 10-pulls; buys every package once; limit-breaks as soon as affordable. | ~$300 / month |

Each purchase is logged in `sink.Spend` as `(day, sku, usd, diamonds)` so the acceptance table's "diamonds spent" column separates earned and bought.

### 3.5 Measurements

```csharp
public sealed record DayRow(
    int Day, int Stage, int HighestCleared, double Dps,
    double Gold, double GoldSpent, long Diamonds, long DiamondsEarned, long DiamondsSpent, double UsdSpent,
    int Pulls, int LPlus, Rarity HighestRarity, int HeroLevel, int AwakenRank,
    int FishMaxLevel, long FishCoins, long RockCandy,
    int FirstWallStage, double LongestWallHours, double CurrentWallHours,
    LayerShares Shares);

public sealed record LayerShares(double Training, double HeroLevel, double Weapons, double Skills, double Companions, double FishBoard, double Research, double Awakening, double Crit, double Speed, double Base);
```

Wall rules: a wall opens on the first `BossFailed` at a stage and closes on `StageCleared` for that stage; wall-clock hours include the gaps between sessions (what the player feels), active hours count ticks only; both are recorded. `LayerShares` is `ln(layerMult) / ln(DPS)` per factor of 00 section 7, computed with `Math.Log` in the harness (reporting only, never fed back).

### 3.6 Acceptance table and CI gate

Targets are read from `ContentDb.ProgressTargets` (09 section 5.31), so a target change is a content PR like any other. Thresholds that are not in 00 live in `src/Whisker.Bots/Acceptance/acceptance.json`:

```json
{
  "sweepBot": "greedy-f2p", "seeds": "1-20", "days": 30,
  "stageCheckDays": [1, 7, 30],
  "shareCheckDays": [7, 30], "shareMax": 0.40, "shareMin": 0.05,
  "firstWallMinStage": 60, "firstWallMaxStage": 200,
  "maxWallActiveMinutesThroughDay14": 240,
  "lazyFraction": { "day7": 0.55, "day30": 0.60 },
  "whaleGapStagesDay7": [25, 60],
  "diamondsPerDayTolerance": 0.15,
  "timeBudgetSeconds": 240
}
```

| Id | Check (greedy-f2p sweep unless stated) | Source |
|---|---|---|
| AC1 | For each day in `stageCheckDays`: median stage within `+/- tolerance` of `progress_targets[day]` | 00 section 6.6 (+/-20%) |
| AC2 | For each seed and day in `shareCheckDays`: every layer share with a non-zero multiplier in `[shareMin, shareMax]` | 00 section 7 (40% / 5% rule), 03 section 8.5 |
| AC3 | For each seed: `FirstWallStage` at day 30 in `[60, 200]` | 03 section 8.6 |
| AC4 | For each seed: every wall opened by day 14 has active minutes `<= 240` | 03 section 8.6 |
| AC5 | Median `DiamondsEarned` per day over days 2-30 within `diamondsPerDayTolerance` of `DIAMONDS_PER_DAY_F2P` | 06 section 2 |
| AC6 | `lazy-f2p` median stage at day 7 and 30 is at least `lazyFraction` of `greedy-f2p`'s | 00 pillar 1 |
| AC7 | `whale` minus `greedy-f2p` median stage at day 7 in `whaleGapStagesDay7` | 00 pillar 4: paying is sooner, not elsewhere |
| AC8 | Every seed: `HighestRarity >= LEGENDARY` by day 30 on at least one banner (pity resolves within the window) | 04, 06 section 2 |
| AC9 | Determinism: seed 7 run twice in-process is byte-identical; the golden replay (section 2) matches | 00 section 3 |
| AC10 | Wall time of the whole sweep under `timeBudgetSeconds` | section 3.3 |

The CLI prints one `PASS`/`FAIL` line per check with the measured numbers and exits 1 on any failure. `content.yml` runs it on every PR that touches `content/`, `src/Whisker.Sim` or `src/Whisker.Bots`; a failing AC blocks merge. The example output format is the sibling's (`PASS AC1 day 7: median stage 371 in 304..456 (target 380)`), which reviewers already read.

Acceptance table (printed by `acceptance --table`; the columns the brief asks for), illustrative shape only:

```
# whisker-hero acceptance table   bot=greedy-f2p seeds=1-20 days=30 content=2026.11.03/3f9c1a2e
day  stageMed  goldEarnedMed  diaEarnedMed  diaSpentMed  pulls  highestRarity  fishLvlMed  longestWallH
  1        88        1.2e+04         1540         1500     16        EPIC             5          0.3
  7       371        9.8e+06         4180         4000     46        LEGENDARY        8          6.5
 30       548        3.1e+12        13900        13800    139        MYTHIC          11         31.0
```

### 3.7 Reports

- `report --longest-walls`: per seed the five longest walls (stage, opened day, wall-clock hours, active minutes, what unblocked it: the first layer whose multiplier rose by more than 10% in the hour before the clear). This is the tuning tool for `BOSS_TIME_TICKS` and `HP_G`.
- `report --layer-shares`: the section 3.5 shares at days 1, 3, 7, 14, 30 as a table, median over seeds, with the 40% / 5% bounds marked; the tool that validates 03 section 8.5.
- `report --diamonds`: earned by `Reason` per day, so 06's budget table can be checked against what the sim actually paid.
- `compare --bots greedy-f2p,lazy-f2p,light-spender,whale`: median stage per day per bot and the gaps.
- `golden --check | --update | --replay`: section 2 plus the four golden run tables (`run-greedy-f2p-seed7-30d.txt`, `run-lazy-f2p-seed7-30d.txt`, `run-whale-seed7-30d.txt`, `sweep-greedy-f2p-seeds1-20-30d.txt`).

### 3.8 CLI

```
dotnet run -c Release --project src/Whisker.Bots -- <verb> [options]

run         --bot greedy-f2p|lazy-f2p|light-spender|whale  --seed 7  --days 30  [--all-days] [--record path] [--format table|csv|json] [--diamonds content|flat] [--content dir] [--tier M0|MVP]
sweep       --bot  --seeds 1-20  --days 30  [--parallel N]
compare     --bots a,b,c  --seeds  --days
acceptance  [--table] [--verbose] [--targets path] [--time-budget-s N]
report      --longest-walls | --layer-shares | --diamonds  --seeds  --days
golden      --check | --update | --replay
bench       --ticks 1000000 --stage 300 --max-ms 2000
```

`--tier M0` builds the content at tier M0 (09 section 5.32) so the M0 milestone's "two weeks of simulated play within tolerance" (00 section 11) is `acceptance --tier M0 --days 14` with `stageCheckDays` clipped to the days that exist.

## 4. Server load tests (MVP)

k6 scripts under `src/Whisker.Server.LoadTests/`, run against the stage environment sized like prod, weekly and before every server release. Each profile runs 10 minutes after a 2-minute ramp; the pass line is the p99 column.

| Profile | Mix | Rate | p50 target | p99 target | Error budget |
|---|---|---|---|---|---|
| `summon` | `POST /summon/pull` 70% 1x, 30% 10x; 5% of requests are retries with a reused `requestId` | 500 rps | 40 ms | 250 ms | < 0.1% 5xx; 0 duplicate grants (checked after the run by `summon_log` vs wallet deltas) |
| `save` | `PUT /save` with a 60 KB blob and `If-Match`; 10% carry a stale ETag | 200 rps | 60 ms | 400 ms | < 0.1% 5xx; every stale ETag gets 409, never 200 |
| `boot` | `GET /config`, `POST /auth/refresh`, `GET /save` in sequence per virtual user | 300 users/s arriving | 80 ms per step | 500 ms | < 0.1% |

Sizing: 500 summon rps is about 10x the expected peak at 100k DAU (08's cost model assumes 0.5 pulls/s per 1k DAU at the evening peak). Plausibility re-simulation on `PUT /save` is the CPU hot spot; the test records CPU per request and the alarm in section 10 is set from this run.

## 5. Client performance budgets

Measured on the four devices of section 1.5; the budget column is the low-end device unless marked.

| Metric | Budget | How measured | Where enforced |
|---|---|---|---|
| Frame time on `SCR_MAIN` with board full and six skills casting | <= 33.3 ms p95 (30 fps floor, 00 section 3) | Unity Profiler recorder in PlayMode test 1; device-farm trace | release candidate gate |
| Sim tick cost | <= 0.5 ms per tick on the low-end device; <= 8 ms per frame for catch-up bursts (`TICK_BUDGET_MS`, 01) | `Stopwatch` around `SessionRunner.Tick` logged to analytics `perf_tick` | nightly dashboard |
| GC alloc per tick | 0 B steady state; <= 128 B per kill with the default event mask | `Whisker.Sim.Tests` perf group (`GC.GetAllocatedBytesForCurrentThread`) | every PR |
| Memory | <= 450 MB total on the 2 GB device at `SCR_MAIN`; <= 600 MB with `SCR_SUMMON_RESULT` 10x reveal | device-farm peak RSS | release candidate gate |
| Cold boot to interactive `SCR_MAIN` | <= 4.0 s on mid device, <= 6.0 s low-end (content load <= 300 ms of it, 01) | `boot_ms` analytics event with phase breakdown | nightly dashboard, release gate |
| Warm resume | <= 1.0 s to first frame | `resume_ms` event | nightly |
| APK / AAB download size | <= 150 MB; IPA <= 200 MB (MVP asset list, 07) | `build-android.yml` artifact size step | every build |
| Addressables remote download at first launch | 0 MB (everything MVP ships in the bundle; remote is for P1 content) | | |
| Damage number labels | pool of `UI_DAMAGE_LABEL_POOL`, never instantiate at runtime | EditMode test over the prefab | PR |
| Particles | <= `UI_MAX_PARTICLES` live | PlayMode assert | nightly |
| Crash-free sessions | >= 99.5% (00 section 11 soft-launch exit) | Crashlytics | soft launch KPI |

Perf regressions are a release blocker when the release-candidate run breaks a budget on any device, and a ticket when the nightly dashboard trends 10% worse over a week.

## 6. CI/CD

### 6.1 Branch strategy

Trunk-based. `main` is always releasable; every change is a PR with required checks (`pr.yml`, plus `content.yml` when content or sim changed, plus the Unity EditMode job when `unity/` changed). Release branches `release/1.2` are cut from `main` for a store submission and receive only cherry-picked fixes; the tag `client-v1.2.0+345` is created by `build-*.yml` on that branch. The server deploys from `main` continuously to stage and by tag `server-v2026.11.03` to prod. Content publishes from `main` by tag `content-2026.11.03`.

### 6.2 Workflows

`.github/workflows/pr.yml` (every PR, ~6 min):

```yaml
on: pull_request
jobs:
  dotnet:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4 { with: { dotnet-version: 8.0.x } }
      - run: dotnet restore Whisker.sln
      - run: dotnet build Whisker.sln -c Release --no-restore -warnaserror
      - run: dotnet test Whisker.sln -c Release --no-build --filter "TestCategory!=Perf&TestCategory!=Acceptance" --logger trx
      - run: dotnet test Whisker.sln -c Release --no-build --filter "TestCategory=Perf"        # separate so a perf flake reads as one
      - run: dotnet run -c Release --no-build --project src/Whisker.Bots -- bench --ticks 1000000 --max-ms 2000
      - run: dotnet run -c Release --no-build --project src/Whisker.Bots -- golden --check --replay
      - run: python3 tools/export_content.py check                                             # committed JSON matches CSVs
      - run: python3 -m pytest tools/tests -q                                                  # exporter fixtures
      - run: python3 tools/ci/test_counts.py --min-ratio 0.9                                   # section 1 count guard
      - run: python3 tools/ci/no_literals.py src/Whisker.Sim                                   # 09 acceptance 1
  server:
    if: contains(github.event.pull_request.changed_files, 'src/Whisker.Server') || contains(..., 'src/Whisker.Sim')
    services: { postgres: { image: postgres:16 }, redis: { image: redis:7 } }
    steps: [checkout, setup-dotnet, dotnet test src/Whisker.Server.Tests -c Release]
  unity-editmode:
    if: contains(..., 'unity/')
    runs-on: [self-hosted, unity-2022-lts]
    steps: [checkout, game-ci/unity-test-runner@v4 with testMode EditMode]
```

`.github/workflows/content.yml` (PRs touching `content/`, `content_src/`, `src/Whisker.Sim`, `src/Whisker.Bots`; ~8 min):

```yaml
steps:
  - run: python3 tools/export_content.py build --src content_src --out $KIROCREW_SCRATCH/content --previous content --strict-warnings --ui-keys unity/Assets/Whisker.Client/ui_keys.txt
  - run: diff -r $KIROCREW_SCRATCH/content content                                            # same as check, with a readable diff
  - run: python3 tools/gacha_model.py --content content --banner WEAPON --level 1 > gacha.txt   # the oracle for the server test
  - run: timeout 400 dotnet run -c Release --project src/Whisker.Bots -- acceptance --table --verbose --time-budget-s 240
  - run: dotnet run -c Release --project src/Whisker.Bots -- report --layer-shares --seeds 1-20 --days 30 > shares.txt
  - run: dotnet run -c Release --project src/Whisker.Bots -- golden --check
  - uses: actions/upload-artifact (acceptance table, shares, golden diff, exporter diff)
  - run: python3 tools/ci/pr_comment.py (posts the acceptance table and the day-30 median into the PR)
```

`.github/workflows/nightly-bots.yml` (02:00 UTC):

- `sweep` for all four bots, 30 days, seeds 1-20 and the rotating `21-40` block (Open question 2), uploaded as CSV to the metrics bucket for the balance dashboard.
- `golden --replay` on `windows-latest` and `macos-14` (ARM64).
- Pseudo-localized client build (`export_content.py pseudo`) + PlayMode test 2 + per-screen screenshots.
- `report --longest-walls` diff against the previous night, posted to the team channel when a wall moved by more than 2 h.

`.github/workflows/build-android.yml` and `build-ios.yml` (on `release/*` push and manual dispatch):

1. `export_content.py check`; copy `content/` to `unity/Assets/StreamingAssets/content/`.
2. Unity build via `game-ci/unity-builder` on the self-hosted runner (Android) or Unity Cloud Build / self-hosted mac with Xcode (iOS). Build number is `github.run_number`; version from `unity/HeroCatClient/version.json`.
3. IL2CPP, ARM64 only (plus ARMv7 for Android 8 devices under 2 GB if the device farm needs it; decided per release).
4. Sign with the keystore / provisioning profile from GitHub secrets (never in the repo).
5. Artifact size check (section 5); upload the AAB / IPA; trigger the device-farm smoke (section 1.5); on green, upload to Play internal testing / TestFlight automatically.

`.github/workflows/deploy.yml` (server and content):

- Server: `docker build` -> ECR -> deploy to stage on every `main` merge; prod on tag. Blue/green: the new task set receives 0% then 10% then 100% of traffic over 15 minutes behind the ALB, with automatic rollback if the 5xx rate or `summon_latency_p99` alarm fires; the old task set stays warm for 1 hour. DB migrations run as a pre-deploy job and must be backward-compatible with the previous server version (expand/contract).
- Content: on tag `content-*`, upload every file to `cdn/content/{version}/`, verify sha256 of each uploaded object, upload `manifest.json` last, then set `config.content` on stage. Prod promotion is a manual approval step that sets `rolloutPct` 5, then 25, then 100 on a schedule the operator confirms (09 section 12).

### 6.3 Versioning and compatibility matrix

| Thing | Scheme | Where it lives | Bump when |
|---|---|---|---|
| Client | semver `MAJOR.MINOR.PATCH` + build number (`1.2.0+345`) | `unity/HeroCatClient/version.json`, `X-Client-Version` header | MINOR per store release, PATCH per hotfix, MAJOR for a save-incompatible change |
| Content | `YYYY.MM.DD[.N]` (09 section 6.3) | `manifest.json`, `X-Content-Version`, `GameState.ContentVersion` | every content change |
| Save schema | integer `SAVE_SCHEMA_VERSION` (02) | `GameState.SchemaVersion` | any field add / rename / semantic change |
| API | path prefix `/v1` (08) | routes | a breaking contract change only; additive fields never bump |
| Server | `YYYY.MM.DD[.N]` tag | image tag, `/health` body | every prod deploy |

Compatibility rules, each with the test that proves it:

| Rule | Test |
|---|---|
| A client at version `c` loads any content with `min_client_version <= c` | Runtime test over the shipped manifest plus a synthetic newer one |
| The server accepts `X-Content-Version` from `minContentVersion[c]` up to current and holds a `ContentDb` for each | Server test: three versions loaded, oldest rejected with `409 CONTENT_TOO_OLD` |
| A save with `SchemaVersion <= Current` loads; `> Current` is refused with "update the app" | Runtime M3, M7 |
| Server `/v1` is additive for at least two client MINOR versions back | contract test replaying recorded requests from `client-v(N-2)` against the current server |
| A client two MINOR versions behind `minClientVersion` gets `426 CLIENT_TOO_OLD` with a store link, never a crash | PlayMode test with a fake config |
| Content `schema_version` is bumped only together with a client release whose `min_client_version` covers it | `E092` in the exporter |

Matrix published in `docs/compat.md` by `deploy.yml` on every prod deploy: rows are live client versions (from analytics), columns are content version, server version, oldest accepted save schema, and the date the row ages out (two MINOR versions).

## 7. Store release checklist

One checklist per submission, filled in the release ticket; `build-*.yml` links the artifact. Items marked **both** apply to App Store and Google Play.

| # | Item | Store | Owner |
|---|---|---|---|
| 1 | Release branch cut, version bumped, changelog written from merged PR titles | both | release manager |
| 2 | `pr.yml`, `content.yml`, device-farm smoke, golden replay on IL2CPP all green on the RC build | both | eng |
| 3 | Perf budgets (section 5) met on all four devices; crash-free >= 99.5% on the internal track for 48 h | both | eng |
| 4 | Privacy nutrition labels: data collected = identifiers (device id, player id), purchases, usage data (analytics events), diagnostics (crash logs); none used for tracking; matches 08's data inventory | App Store | PM |
| 5 | Data safety form: same inventory, encryption in transit yes, deletion path = `SCR_SETTINGS` -> delete account (08 GDPR flow) | Google Play | PM |
| 6 | Age rating: IARC / App Store questionnaire answered with "contains loot boxes / randomised items purchasable with real money: yes", "odds disclosed in-app: yes" (00 pillar 4); expected rating 12+ / Teen because of simulated gambling descriptors | both | PM |
| 7 | Loot-box odds disclosure visible on `SCR_SUMMON` per banner level (08 compliance: Apple 3.1.1, Google policy, Japan JOGA, China NPPA where applicable; Belgium and the Netherlands excluded from the store listing in MVP) | both | design |
| 8 | IAP products created with the exact `apple_product_id` / `google_product_id` from `shop_products` and `shop_packages`; price tiers match `usd_tier`; all in "Ready to submit"; a sandbox purchase of each SKU grants through the inbox on stage | both | eng |
| 9 | Restore purchases tested on a second device; refund path tested with a sandbox refund | both | eng |
| 10 | Review notes: test account credentials (guest with pre-filled stage-100 save on prod), how to reach `SCR_SUMMON` and `SCR_SHOP` in under a minute, note that the backend is required and up | both | release manager |
| 11 | Screenshots per locale from the nightly set (09 section 13.5), 6.7" and 6.5" and 5.5" iPhone classes, iPad if claimed; Play feature graphic; no placeholder art | both | art |
| 12 | Store listing copy per locale, keywords, support URL, privacy policy URL live | both | PM |
| 13 | Minimum OS set to iOS 15 / Android 8 (API 26) in the build; `targetSdk` at the store's current requirement | both | eng |
| 14 | Export compliance (standard HTTPS only) answered; encryption declaration in `Info.plist` | App Store | eng |
| 15 | Account deletion reachable in-app (both stores require it) and the flow tested end to end on stage | both | eng |
| 16 | Staged rollout plan: Play 5% -> 20% -> 50% -> 100% over 7 days with the section 10 alarms as halt conditions; App Store phased release on | both | release manager |
| 17 | Rollback plan written: Play halt rollout + promote previous AAB; App Store remove from sale is last resort, prefer a server-side `minClientVersion` hold and a content/config fix; save schema never rolled back | both | eng |
| 18 | `minClientVersion` on prod config left at the previous version until 48 h after 100% rollout | both | eng |
| 19 | Post-release watch rota: one engineer on alarms for the first 24 h | both | eng lead |

## 8. Soft-launch plan

Consistent with 00 section 11: staged rollout in two small markets, exit when D1 >= 40%, D7 >= 15%, crash-free >= 99.5%, no P0 economy exploit.

| Item | Decision |
|---|---|
| Markets | Philippines and Canada (Android first, iOS two weeks later). Philippines for volume and low CPI with a strong idle-RPG audience; Canada for payer behaviour close to the US. New Zealand as a one-week technical launch before both. |
| Duration | 8 weeks minimum: 2 weeks technical (NZ), 6 weeks KPI (PH + CA). Extend in 2-week steps until two consecutive weeks meet the exit KPIs or the team decides to pivot. |
| Acquisition | 3,000 installs per market in week 1 via paid UA, then organic; enough for D7 confidence at +/-3 pp. |
| Content cadence | Weekly `hot_apply` publishes allowed; one sim-affecting rebalance per two weeks, each preceded by the bot acceptance run and a stage soak of 48 h. |

KPI table (per market, weekly review):

| KPI | Definition | Week-2 floor (keep going) | Exit target | Source |
|---|---|---|---|---|
| D1 retention | players with a session on calendar day 1 after install / installs | 35% | >= 40% | analytics `session_start` |
| D7 | same, day 7 | 12% | >= 15% | |
| D30 | same, day 30 | 5% | >= 7% (informational for the exit) | |
| Crash-free sessions | 1 - crashed sessions / sessions | 99.0% | >= 99.5% | Crashlytics |
| Save-loss tickets | support tickets tagged `save-loss` per 1,000 DAU per week | 1.0 | 0 confirmed in the last 2 weeks (00 MVP exit) | support tool |
| Time to first wall | median hours from install to first `BossFailed` wall > 30 active minutes | 1.5 h to 6 h | 2 h to 5 h | `wall_opened` event |
| Day-7 median stage | median `highest_cleared` of D7-retained players | within 25% of `progress_targets[7]` | within 20% (00 section 6.6) | `stage_cleared` |
| Payer conversion D7 | payers / installs by day 7 | 1.5% | >= 3% (06 section 8.6) | `iap_completed` |
| ARPDAU | revenue / DAU | $0.04 | >= $0.08 (06) | |
| Session length and count | median minutes per session, sessions per DAU | 8 min, 2.5 | 10 min, 3 | |
| Economy integrity | plausibility flags per 1,000 saves; P0 exploits found | < 5; 0 unresolved | 0 P0 | 08 plausibility log |
| Summon odds audit | observed rarity split per banner level vs published, chi-square p | > 0.01 | > 0.01 every week | `summon_log` |

Decision rules: if D1 is under the floor after week 2, the tutorial and first-session loop (07 section 11) are the suspects and a content-only fix is tried first; if D7 is under the floor with D1 fine, the day-2 to day-7 wall shape is the suspect and the `report --longest-walls` output is compared with real `wall_opened` data; if conversion is under the floor with retention fine, shop placement and the first-purchase double are tuned before prices.

## 9. Live-ops runbooks

Every runbook has the same four parts. "Communicate" always means: in-game mail via the Inbox (compensation template), a store listing note if players are affected for more than 24 h, and the team channel first. "Postmortem" is a one-page document within 5 working days with a timeline, root cause, and the test or alarm that would have caught it, which becomes a PR.

### 9.1 Bad content push

- **Detect**: alarm `content_update_failed` rate > 1% of boots, or `boot_ms` p95 up 50%, or the summon odds audit deviates, or support reports within 30 min of a publish. The publish time is annotated on every dashboard.
- **Mitigate**: set `config.content.version` back to the previous version (clients fall back to the cached or bundled copy at next boot; `hot_apply` tables revert on the next config refresh). Takes effect in under `CONFIG_REFRESH_S`. Do not delete the bad version from the CDN (saves reference its hash). If the bad content changed a sim table, run the bots on it to quantify the damage window.
- **Communicate**: mail "we fixed a data issue" with compensation sized by the bot delta (section 9.7 table).
- **Postmortem**: which validator rule or acceptance check would have caught it; add it (09 section 8 or section 3.6).

### 9.2 Save corruption wave

- **Detect**: `save_corrupt` analytics events > 0.1% of boots in an hour, or `SaveCorruptException` in Crashlytics, or save-loss tickets.
- **Mitigate**: identify the client version and the write path; if a client bug, raise `minClientVersion` to block the version once a fix is live; if server-side, stop `PUT /save` accepting from that version via a kill switch (08). Recovery per player: server `save_history` (`SAVE_HISTORY_KEEP` versions) restored by support tooling; the local `prev` slot is tried first by the client automatically (02).
- **Communicate**: in-app banner via config `notice`, mail with a restore instruction and compensation.
- **Postmortem**: add the corruption shape as a `LocalSaveStore` fault-injection test.

### 9.3 Summon rate misconfiguration

- **Detect**: the weekly odds audit (section 8) runs hourly for 24 h after any `banners` or `banner_levels` change; alarm when observed L+ rate differs from published by more than 0.5 pp over 10,000 pulls, or `rateTableHash` served by `/summon/rates` differs from the content's.
- **Mitigate**: kill switch `summonEnabled[banner] = false` (08) within one config refresh; content rollback as in 9.1. Compute per-player impact from `summon_log` (every pull has the rate table hash it was rolled under).
- **Communicate**: this is a regulatory matter in several markets. If published odds were higher than actual: refund the diamond difference as "pulls at the published odds" (grant the missing expected L+ as a pity-counter reduction plus diamonds), publish a notice in-game and on the store listing. If actual odds were higher than published: no clawback; note it publicly.
- **Postmortem**: why `E037` or the server's rate hash check did not fire.

### 9.4 IAP grant failure

- **Detect**: `iap_completed` without a matching `inbox_items` row within 5 min > 0.5% of purchases; App Store Server Notifications / RTDN backlog > 100; support tickets tagged `missing-purchase`.
- **Mitigate**: the client retries receipt validation every `RECEIPT_RETRY_S` and "Restore purchases" re-submits; if the validator is down, queue receipts and grant on recovery (idempotent by transaction id). Manual grant via the admin mail tool against the store transaction id when the store's API confirms the purchase.
- **Communicate**: mail with the grant and a small apology grant; store review replies for anyone who left a one-star review about it.
- **Postmortem**: which stub in `Whisker.Server.Tests` receipt group was missing.

### 9.5 Exploit found

- **Detect**: plausibility flags clustering on one pattern (08 section 7), a leaderboard outlier (P1), a player report, or a save whose stage exceeds the bot's whale run by more than 30% at the same play time.
- **Mitigate**: confirm by re-simulating the save from its last trusted version plus command log (01 section 2.6); apply the 08 action matrix (rollback to last plausible save for first offence, ban for sale or repeat); close the hole with a server-side check first (no client release needed), then the client fix. Never ship a fix that punishes players who did not exploit.
- **Communicate**: a short public notice only if the exploit was public; affected honest players (rollback false positives) get the appeal path (08) and compensation.
- **Postmortem**: a new plausibility check with a test; a bot variant that reproduces the exploit, kept as a regression test.

### 9.6 Store rejection

- **Detect**: App Review / Play Policy message.
- **Mitigate**: triage in 2 hours: metadata-only fix (screenshots, age rating answers, privacy labels) resubmits same day; a binary fix goes through the release branch with the full RC gate; a policy dispute gets a written reply with the odds-disclosure screens attached. Keep the previous version live; never pull the app.
- **Communicate**: internal only unless a planned content push depended on the client release, in which case the content stays on stage and `minClientVersion` is not raised.
- **Postmortem**: add the item to the section 7 checklist.

### 9.7 Compensation sizing

| Impact | Grant |
|---|---|
| Under 1 h of degraded play | 1 `TICKET_SUMMON_WEAPON` |
| 1-24 h degraded or a lost offline reward | diamonds equal to one day of `DIAMONDS_PER_DAY_F2P` (400) plus the capped offline reward recomputed by the server |
| Lost progress restored from history | the above plus one 10-pull's worth of diamonds on the cheapest banner (1,000) |
| Rate misconfiguration | computed per player from `summon_log` (9.3) |

Compensation is always an Inbox grant with an `inbox_templates` row and `Reason.MAIL`, never a direct wallet write, so it shows up in the economy analytics.

## 10. Dashboards and alarms

Dashboards (Grafana over the metrics store; analytics warehouse for the economy views):

1. **Service**: request rate, 5xx rate, p50/p99 per route, DB connections, Redis memory, queue depth for receipts and RTDN, ECS task health, deploy annotations.
2. **Client health**: crash-free sessions, `boot_ms` and `resume_ms` p95 per device class, `perf_tick` p95, `content_update_failed`, `save_corrupt`, client version mix.
3. **Economy**: diamonds earned by `Reason` per DAU, diamonds spent per DAU, pulls per banner per level, observed vs published odds, median stage by install cohort and day (overlaid with the bot's `greedy-f2p` and `lazy-f2p` curves), wall distribution, Fish Coin and Rock Candy flows, plausibility flags.
4. **Revenue**: ARPDAU, conversion, SKU mix, refund rate, first-purchase double usage.
5. **Live ops**: current content version per client version, rollout percentage, kill switch states, maintenance flag, inbox grants outstanding.

Alarms (PagerDuty P1 pages the on-call; P2 is a channel message and a ticket next morning):

| Alarm | Condition | Severity |
|---|---|---|
| API 5xx | > 1% of requests over 5 min | P1 |
| Summon latency | `POST /summon/pull` p99 > 500 ms over 5 min | P1 |
| Save latency | `PUT /save` p99 > 800 ms over 5 min | P2 |
| Auth failures | 401/403 > 5% of requests over 10 min (token or clock issue) | P1 |
| Receipt backlog | unprocessed notifications > 100 for 10 min | P1 |
| IAP grant gap | purchases without inbox row > 0.5% over 30 min | P1 |
| Odds drift | observed L+ vs published > 0.5 pp over 10k pulls | P1 |
| Duplicate grant | any `requestId` with two wallet deltas | P1 |
| Plausibility spike | flags > 1% of saves over 1 h | P2 |
| Crash-free | < 99.0% over 1 h on any client version with > 500 sessions | P1 |
| Content update failures | > 1% of boots over 30 min | P2 |
| Save corruption | > 0.1% of boots over 1 h | P1 |
| Boot time | `boot_ms` p95 > 6 s on the mid device class over 1 h | P2 |
| DB | connections > 80% of pool, replication lag > 10 s, disk > 80% | P1 |
| Redis | memory > 80%, evictions > 0 | P2 |
| Cost | daily spend > 1.5x the 7-day average | P2 |
| CDN | 4xx on `content/*` > 0.5% (a missing file after a publish) | P1 |
| Rollout halt | any P1 during a staged rollout automatically freezes `rolloutPct` | - |

Every alarm links to the runbook section that handles it.

## 11. Roadmap: 12 months, M0 to P1

Team-weeks for a 2-3 person team (one gameplay/sim engineer, one client engineer, a half-to-full-time designer/artist; backend split between the two engineers). The solo-dev column is what one engineer-designer does alone, skipping the backend at M0 and MVP-lite as 00 section 3 allows (local seams, no IAP, TestFlight/internal-track only).

| Month | Milestone | Scope | Exit (00 section 11) | Team-weeks (2-3 people) | Solo-dev variant |
|---|---|---|---|---|---|
| 1 | M0 foundations | Repo, `Whisker.Sim` combat + stages + Training + hero level, `ContentDb` + exporter (09), RNG streams, `Whisker.Bots` greedy F2P with the day table, `pr.yml` | bots print the day table; determinism test green on x64 | 8 | 5 weeks, same scope minus Windows/mac golden runners |
| 2 | M0 systems | Weapon + skill summon (local), Fish Board + tank, offline catch-up, local save with migrations v1, `content.yml` with AC1-AC4, AC9 | `acceptance --tier M0 --days 14` passes | 9 | 6 weeks |
| 3 | M0 client | Unity project, `SCR_MAIN`, `SCR_HERO`, `SCR_WEAPONS`, `SCR_SKILLS`, `SCR_SUMMON`, `SCR_SUMMON_RESULT`, `SCR_OFFLINE_REWARD`, `SCR_ITEM_DETAIL`, placeholder art, `build-android.yml` | one person plays 20 min on device unprompted | 10 | 7 weeks |
| 4 | MVP sim | Companions + MP, Research, Awakening, quests, dungeons, day/week keys, inbox (local), bots: lazy, light spender, whale; AC5-AC8 | all bots within tolerance; 03 layer shares in band | 9 | 6 weeks |
| 5 | MVP backend 1 | ASP.NET skeleton, auth (guest + link), cloud save with conflict rules, config, server summons with audit log, `Whisker.Server.Tests`, `deploy.yml` to stage | summon test vs Python model green; idempotency replay green | 10 | skip; keep local seams. 2 weeks hardening local save instead |
| 6 | MVP backend 2 + client 2 | IAP receipts both stores, inbox, plausibility checks, admin mail; client: `SCR_COMPANIONS`, `SCR_RESEARCH`, `SCR_DUNGEONS`, `SCR_DUNGEON_RUN`, `SCR_QUESTS`, `SCR_SHOP`, `SCR_INBOX`, `SCR_SETTINGS`, `SCR_TITLE` | sandbox purchase grants end to end on stage | 11 | 6 weeks client only; no shop, settings without account |
| 7 | MVP polish | Tutorial, analytics, localization EN + ja/ko, art pass on hero/fish/boards, `SCR_STAGE_SELECT`, `SCR_PROFILE`, PlayMode smoke, device farm, perf budgets | section 5 budgets met on 4 devices | 9 | 6 weeks, EN only |
| 8 | Closed beta | 50 players (TestFlight + Play internal), D7 measured, save-loss zero, load tests, runbooks drafted, dashboards | 00 MVP exit | 6 | 4 weeks; beta of 20 via TestFlight |
| 9 | Soft launch prep + NZ | Store checklist (section 7), compliance, odds disclosure, staged rollout tooling, NZ technical launch | checklist complete; NZ crash-free >= 99.5% | 7 | not applicable without IAP; publish a free build in one market |
| 10-11 | Soft launch PH + CA | KPI loop (section 8), two rebalances via bots, hot-apply content weekly, first live-ops incidents | D1 >= 40%, D7 >= 15%, 0 P0 exploits | 10 | n/a |
| 12 | P1 start | Runes, Tower, Auto Merge, Coupons, Leaderboards (Redis), Events data model, Rewarded Ads SDK; rotating-seed bots and lapsed-player bot | scheduled by soft-launch data | 10 | pick Runes + Auto Merge |
| Total | | | | ~99 team-weeks (about 11 months of 2.3 FTE) | ~42 weeks to an MVP-lite with no backend |

Risks that move the dates, in order of likelihood: Unity build infrastructure (budget a week in month 3 for the self-hosted runner or Cloud Build), store review for loot-box disclosure (start the age-rating questionnaire in month 7), and a 03 retune that moves `progress_targets` (the bots make it a day, not a week, but the content PR needs design time).

## Open questions

1. **Acceptance sweep bot income mode.** Default `--diamonds content` (the sim's own quests). Keep `--diamonds flat` only as the parity mode against `power_curve_model.py` until that tool is retired. Owner guess: 03.
2. **Seed rotation.** Seeds 1-20 are fixed for the goldens. Default: the nightly also runs 21-40 and the acceptance check on them is advisory (reported, not gating) for one month, then gating if it never flapped. Owner: eng.
3. **Where the first-wall band lives.** `firstWallMinStage` / `firstWallMaxStage` are in `acceptance.json`; 03 section 8.6 states them. Default: move them into `progress_targets` as columns when 09 next revises the table, so a wall band change is a content PR. Owner: 03 and 09.
4. **Device farm vendor.** Firebase Test Lab (Android breadth, cheap) vs AWS Device Farm (iOS too). Default: Firebase for Android nightly, two physical iPhones on the self-hosted mac for iOS. Owner: eng.
5. **Unity Cloud Build vs self-hosted mac.** Default: self-hosted mac mini for iOS (one-off cost, no per-minute billing, Xcode control); Cloud Build as a fallback when the mac is down. Owner: eng.
6. **Soft-launch markets.** PH + CA defaults above; if UA budget is tight, PH only with CA replaced by a Canada-only iOS launch later. Owner: PM.
7. **Odds audit statistical power.** 10,000 pulls per banner level per day is reachable at 10k DAU for level 1-3 only; higher levels audit over a rolling week. Default: alarm thresholds scale with sample size (Wilson interval), not a fixed pp. Owner: eng.
8. **Blue/green vs rolling for the server.** Blue/green doubles capacity during deploys. Default: blue/green at MVP (deploys are rare, capacity is cheap at this scale); revisit at 100k DAU. Owner: eng.

## Acceptance criteria

1. `pr.yml` on a clean `main` finishes under 8 minutes with every step green; `content.yml` under 10 minutes.
2. `dotnet test` reports at least 90% of the counts in `tests/counts.json` per project; `counts.json` is updated only by a PR that adds tests.
3. `golden --replay` yields identical day hashes on x64 Linux, x64 Windows, Apple ARM64 and the IL2CPP Android build of the same commit.
4. `acceptance` on the shipped content and seeds 1-20 passes AC1-AC10 and prints the table of section 3.6; forcing `HP_G` to 1.10 in a scratch content dir makes AC1 fail on day 7 and day 30 with the measured medians in the message.
5. Running `acceptance` twice in one process produces byte-identical output; `sweep --parallel 1` and `--parallel 8` produce identical output.
6. `lazy-f2p` reaches at least 55% of `greedy-f2p`'s median stage on day 7 and 60% on day 30 (AC6); `whale` leads `greedy-f2p` by 25 to 60 stages on day 7 (AC7).
7. Every migration fixture `v1..vCurrent` migrates to current with zero repairs; a `vCurrent+1` fixture is refused (02 M3, M7) and the test is parameterised over the fixture directory.
8. Fault-injection: killing the process between temp write and rename, then reloading, yields the previous save and no exception; corrupting both slots yields `SaveCorruptException` with both files preserved.
9. The server summon Monte Carlo (1M pulls per banner level) matches `tools/gacha_model.py` within 0.3 pp per rarity and 0.05 pp on L+ for every level in `banner_levels`.
10. Every endpoint with a `requestId` returns the stored response on replay and performs exactly one write (checked by row counts and wallet deltas after the test).
11. Load profiles `summon`, `save`, `boot` meet their p99 targets on stage with zero duplicate grants and every stale ETag answered 409.
12. PlayMode smoke 1-4 pass on the RC build; the device-farm run passes on all four devices twice in a row.
13. Section 5 budgets are met on the RC build: p95 frame <= 33.3 ms on the low-end device, 0 B steady-state GC per tick, cold boot <= 4 s mid-device, AAB <= 150 MB, IPA <= 200 MB.
14. A content tag publishes to stage with every CDN object's sha256 verified and `manifest.json` uploaded last; a deliberately corrupted upload fails the verify step and leaves `config.content` unchanged.
15. A server deploy to stage with a task set returning 5xx is rolled back automatically within 15 minutes with no manual action.
16. Every item in the section 7 checklist has an owner and a link in the release ticket template; a release ticket cannot be closed with an unchecked item.
17. Every alarm in section 10 exists in the alerting tool, links to a runbook section, and has fired at least once in a staged drill on stage before soft launch.
18. Every runbook in section 9 has been walked through in a tabletop drill with the on-call rota before soft launch; the drill notes are linked from the runbook.
19. The soft-launch KPI dashboard shows every KPI in section 8 per market with the floor and exit lines drawn; the cohort stage curve overlays the bot curves.
20. `docs/compat.md` is regenerated by `deploy.yml` on every prod deploy and lists every client version with more than 1% of sessions.

## Constants introduced

Harness and operations constants; none is game balance and none lives in `ContentDb`. Kind `I` is carried from the sibling harness, `S` is new here.

| Name | Value | Kind | Section | Check |
|---|---|---|---|---|
| `HARNESS_EPOCH` | 2026-01-05 09:00 UTC | S | 3.2 | weekly key and rollover both exercised in a 30-day run (unit test) |
| `ACTIVE_MINUTES_PER_DAY` | 120 | I (00 section 6.6) | 3.2 | |
| `SESSIONS_PER_DAY` | 2 | S | 3.2 | catch-up runs twice per bot day |
| `DECISION_INTERVAL_S` | 60 | I | 3.2 | |
| `DEFAULT_SEEDS` | `1-20` | I | 3.2 | |
| `LIGHT_SPENDER_USD_PER_MONTH` | 20 | S | 3.2 | |
| `WHALE_DAY1_USD` | 100 | S | 3.2 | AC7 band |
| `SHARE_MIN`, `SHARE_MAX` | 0.05, 0.40 | I (00 section 7) | 3.6 | AC2 |
| `FIRST_WALL_MIN_STAGE`, `FIRST_WALL_MAX_STAGE` | 60, 200 | I (03 section 8.6) | 3.6 | AC3; Open question 3 |
| `MAX_WALL_ACTIVE_MIN_THROUGH_DAY14` | 240 | I (03) | 3.6 | AC4 |
| `LAZY_FRACTION_D7`, `LAZY_FRACTION_D30` | 0.55, 0.60 | S | 3.6 | AC6 |
| `WHALE_GAP_STAGES_D7` | 25-60 | S | 3.6 | AC7 |
| `DIAMONDS_PER_DAY_TOLERANCE` | 0.15 | S | 3.6 | AC5 |
| `ACCEPTANCE_TIME_BUDGET_S` | 240 | S | 3.6 | AC10 |
| `TEST_COUNT_MIN_RATIO` | 0.90 | S | 1 | criterion 2 |
| `LOAD_SUMMON_RPS`, `LOAD_SAVE_RPS`, `LOAD_BOOT_UPS` | 500, 200, 300 | S | 4 | p99 targets 250 / 400 / 500 ms |
| `PERF_FRAME_MS_P95` | 33.3 | I (00 section 3) | 5 | |
| `PERF_TICK_MS_MAX` | 0.5 | S | 5 | |
| `PERF_MEM_MB_MAX` | 450 (600 during reveal) | S | 5 | |
| `PERF_BOOT_S_MAX` | 4.0 mid / 6.0 low-end | S (01 states 4 s) | 5 | |
| `SIZE_AAB_MB_MAX`, `SIZE_IPA_MB_MAX` | 150, 200 | S | 5 | |
| `ROLLOUT_STEPS_PCT` | 5, 25, 100 (content); 5, 20, 50, 100 (store) | S | 6.2, 7 | |
| `BLUE_GREEN_RAMP_MIN` | 15 | S | 6.2 | criterion 15 |
| `COMPAT_CLIENT_MINORS_BACK` | 2 | S | 6.3 | contract test |
| Soft-launch KPI floors and exits | section 8 table | S (exits from 00 section 11) | 8 | weekly review |
| Alarm thresholds | section 10 table | S | 10 | drill (criterion 17) |
| Compensation ladder | section 9.7 | S | 9.7 | |

Used from 00 by name: `TICK_HZ`, `BOSS_EVERY`, `BOSS_TIME_TICKS`, `HP_G`, `STAGE_CAP`, `OFFLINE_RATE`, `OFFLINE_CAP_H`, `OFFLINE_MIN_S`, `DIAMONDS_PER_DAY_F2P`, `PULL_COST[*]`, `PITY_HARD`, `TUTORIAL_DIAMONDS`, the section 6.6 targets via `progress_targets`. From 01: `MAX_TICKS_PER_FRAME`, `TICK_BUDGET_MS`, `IN_APP_GAP_SEC`, `AUTOSAVE_INTERVAL_SEC`, `CMDLOG_MAX_BYTES`, `EVENT_COALESCE_AT`, `HTTP_RETRY_*`, `CONFIG_REFRESH_S`, `RECEIPT_RETRY_S`, `DUNGEON_SEED_TTL_S`. From 02: `SAVE_SCHEMA_VERSION`, `SAVE_REPAIR_MAX`, `SAVE_FAIL_WARN_COUNT`, `CLOUD_UPLOAD_MIN_SEC`, `CONFLICT_PROGRESS_MARGIN`, `CLOCK_RESYNC_SEC`, `CLOCK_JUMP_WARN_SEC`, `CLOCK_MAX_SKEW_ACCEPT_S`, `SAVE_BLOB_MAX_BYTES`, `SAVE_HISTORY_KEEP`, `IDEMPOTENCY_TTL_H`, `GDPR_DELETE_GRACE_DAYS`, `ANALYTICS_BATCH_MAX`. From 06: `INBOX_EXPIRY_DAYS`. From 07: `UI_DAMAGE_LABEL_POOL`, `UI_MAX_PARTICLES`.
