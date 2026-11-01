# 09 - Test Harness and Bots

Scope: the headless bot runner `HeroCat.Bots`, built on `HeroCat.Sim` (`specs/03`). It plays N days in seconds, prints the tuning table, and is the acceptance test for every balance change (`specs/00` section 5 item 6). The acceptance criteria it enforces are `specs/02` section 9; the stage targets are `specs/00` section 2.6.

Constants are named as in `specs/00` section 2 and are read through `IContentDb`. Harness-only constants (session length, epoch, seed list) are defined here in section 3 and live in the bots project, not in game content.

## 1. Design goals

1. Same code as the game. The bot drives `Simulation.Advance`, the public command API and `SyncWallClock`; it has no private shortcut into the sim.
2. Mirrors `tools/power_curve_model.py` closely enough that the Python model and the C# run agree within the +/-15% that `specs/02` section 6 grants the model, so a disagreement is a finding and not noise.
3. Deterministic to the byte for a given seed, content and bot, on every machine.
4. 20 seeds times 30 days finishes inside a CI time budget.
5. A balance change is a reviewable diff: content change, golden-file change and acceptance result land in one CR.

## 2. Project layout

```
src/
  HeroCat.Bots/                 net8.0 console; public types so the test project can drive it in-process
    Program.cs                  CLI (section 12)
    HarnessConstants.cs
    BotRunner.cs                day schedule (section 4)
    Policies/IBotPolicy.cs
    Policies/GreedyF2PBot.cs
    Policies/WhaleBot.cs
    Measure/BotEventSink.cs     L+ count, wall tracker
    Measure/DayRow.cs
    Measure/LayerShares.cs
    Report/TableWriter.cs       table, csv, json (section 7)
    Report/Sweep.cs             percentiles (section 8)
    Acceptance/AcceptanceChecker.cs
    Acceptance/acceptance-targets.json
    Golden/GoldenFiles.cs
  HeroCat.Bots.Tests/           net8.0, NUnit
golden/
  run-greedy-f2p-seed7-30d.txt
  run-whale-seed7-30d.txt
  sweep-greedy-f2p-seeds1-20-30d.txt
.gitattributes                  golden/** text eol=lf
```

The bots project references `HeroCat.Sim`, `HeroCat.Runtime` (save round-trip, `FakeClock`; `specs/06`) and the `specs/05` content loader. It has no Unity reference and no other package dependency; `System.CommandLine` may be used for argument parsing if the team prefers, with the verbs and options of section 12 unchanged.

## 3. Harness constants

```csharp
namespace HeroCat.Bots
{
    public static class HarnessConstants
    {
        public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);   // fixed so DayKey rollovers are reproducible
        public const int ActiveMinutesPerDay = 120;      // specs/00 section 2.6: 2 h active per day
        public const int DecisionIntervalSeconds = 60;   // the Python model spends once per minute
        public const int DefaultDays = 30;
        public const string DefaultSeeds = "1-20";
        public static readonly int[] SummaryDays = { 1, 2, 3, 5, 7, 10, 14, 21, 30 };   // rows printed by default; all days are always recorded
    }
}
```

Profile seed for `--seed N` is `ProfileSeed = N` (`specs/03` section 5.6). `ProfileId` is `Guid` built from the seed (`new Guid((int)N, 0, 0, new byte[8])`) so a run is fully specified by `(bot, seed, days, content)`.

## 4. How a day is scheduled

One bot day is: active ticks in decision-sized batches, then exactly one `SyncWallClock` call that credits the night and rolls the day.

```csharp
public sealed class BotRunner
{
    public BotRunner(IContentDb content) { ... }

    public RunResult Run(IBotPolicy policy, ulong seed, int days)
    {
        var c = _content;
        var clock = new FakeClock { UtcNow = HarnessConstants.Epoch };
        var state = GameState.NewProfile(c, ProfileIdFor(seed), seed, clock.UtcNow);
        var sink  = new BotEventSink(state);                        // Mask = BossFailed | BossCleared | StageAdvanced | HeroPulled | OfflineGoldCredited
        var sim   = new Simulation(c, state, sink);
        var ctx   = new BotContext(sim, state, c, clock, sink);
        var rows  = new DayRow[days];

        int ticksPerDecision = HarnessConstants.DecisionIntervalSeconds * c.Combat.TickHz;        // 600
        int decisionsPerDay  = HarnessConstants.ActiveMinutesPerDay * 60 / HarnessConstants.DecisionIntervalSeconds;   // 120

        for (int day = 1; day <= days; day++)
        {
            ctx.Day = day;
            var dayStart = HarnessConstants.Epoch + TimeSpan.FromDays(day - 1);   // clock is already here: the previous day's sync put it here

            sim.Debug.GrantGems(c.Economy.GemsPerDayF2p);         // GEMS_PER_DAY_F2P, see section 5.3
            policy.OnDayStart(ctx);                                // free pulls (FREE_PULLS_PER_DAY), whale injections

            for (int d = 0; d < decisionsPerDay; d++)
            {
                sim.Advance(ticksPerDecision);                     // 60 s of play
                clock.Advance(TimeSpan.FromSeconds(HarnessConstants.DecisionIntervalSeconds));
                policy.Decide(ctx);                                // spend, equip, boss retry
            }

            clock.Set(dayStart + TimeSpan.FromDays(1));
            sim.SyncWallClock(clock.UtcNow);                       // the one catch-up call: 22 h gap, credited up to OFFLINE_CAP_H at OFFLINE_RATE; then DayRolledOver resets free pulls

            rows[day - 1] = Snapshot(day, sim, sink);              // taken after the catch-up, like the Python model's row
        }
        return new RunResult(policy.Name, seed, days, c.Version, c.Hash, rows, sink.Walls.ToArray());
    }
}
```

Facts a developer can rely on:

- Ticks per day: `ActiveMinutesPerDay * 60 * TICK_HZ` (72,000). Ticks per 30-day run: 2,160,000. Twenty seeds: 43.2 million ticks, which is the number the time budget in section 13 is derived from.
- `SyncWallClock` is called exactly once per day. Mid-day the fake clock and the tick count move in lockstep, so that call sees a gap of `24 h - ActiveMinutesPerDay`, which the sim caps at `OFFLINE_CAP_H`.
- Commands are issued only between `Advance` calls, never from inside an event handler.
- Day 1 starts at `Epoch`; `NewProfile` sets `Daily.DayKey` from it, so the day-1 free pulls are available without a rollover.

## 5. Policies

### 5.1 Interface

```csharp
public sealed class BotContext
{
    public Simulation Sim { get; }
    public GameState State { get; }
    public IContentDb Content { get; }
    public FakeClock Clock { get; }
    public BotEventSink Stats { get; }
    public int Day { get; set; }
}

public interface IBotPolicy
{
    string Name { get; }                 // CLI id and golden-file component: "greedy-f2p", "whale"
    void OnDayStart(BotContext ctx);
    void Decide(BotContext ctx);         // once per DecisionIntervalSeconds of active play
}
```

### 5.2 GreedyF2PBot

Mirrors `Bot.spend()` and the per-day loop in `tools/power_curve_model.py`, step for step and in the same order.

```csharp
public class GreedyF2PBot : IBotPolicy
{
    public virtual string Name => "greedy-f2p";
    protected virtual bool UseTenPulls => false;          // the Python model pulls singles only

    public virtual void OnDayStart(BotContext ctx)
    {
        for (int i = 0; i < ctx.Content.Economy.FreePullsPerDay; i++) ctx.Sim.Pull();   // consumes the day's free pulls, no gems
        ctx.Sim.AutoEquipBestHeroes();
    }

    public void Decide(BotContext ctx)
    {
        var sim = ctx.Sim;
        var c = ctx.Content;

        sim.BuyStatLevelsMax();                                                   // 1. stat levels while affordable

        int slot = WeakestEnhanceableSlot(ctx.State, c.Gear.EnhMax);              // 2. one attempt on the least-enhanced occupied slot
        if (slot >= 0) sim.Enhance(slot);                                         //    (rejected for gold is fine: the Python model also skips then)

        SpendGems(ctx);                                                           // 3. gems -> pulls, 4. equip best six

        if (ctx.State.Stage.Boss == BossPhase.Walled && sim.Query.BossClearable)   // 5. retry the boss once the numbers say it dies in time
            sim.AttemptBoss();
    }

    protected void SpendGems(BotContext ctx)
    {
        var sim = ctx.Sim; var w = ctx.State.Currencies; int cost = ctx.Content.Economy.PullCostGems;
        bool pulled = false;
        if (UseTenPulls) while (w.Gems >= 10 * cost) { sim.Pull10(); pulled = true; }
        while (w.Gems >= cost) { sim.Pull(); pulled = true; }
        if (pulled) sim.AutoEquipBestHeroes();
    }

    /// Lowest Enhance among occupied slots below ENH_MAX; lowest index on ties; -1 if none.
    protected static int WeakestEnhanceableSlot(GameState s, int enhMax) { ... }
}
```

Where it deviates from the Python model and why, see section 14.

### 5.3 Gem income

`GEMS_PER_DAY_F2P` is granted flat at the start of each day with `Debug.GrantGems`, exactly as the Python model does `b.gems += GEMS_PER_DAY`. When `specs/04`'s daily quests and idle chests exist in the sim, the runner gains `--gems flat|content` (default `content`) and the flat grant stays available for parity runs against the Python model. Starter grants (`specs/01` section 5) are not applied, matching the model.

### 5.4 WhaleBot

```csharp
public sealed class WhaleBot : GreedyF2PBot
{
    public const int Day1ExtraPulls = 100;                 // specs/02 section 4.3: "+100 pulls on day 1"
    public override string Name => "whale";
    protected override bool UseTenPulls => true;           // a paying player buys 10-pulls and gets the specs/01 section 4 guarantee

    public override void OnDayStart(BotContext ctx)
    {
        if (ctx.Day == 1) ctx.Sim.Debug.GrantGems(Day1ExtraPulls * ctx.Content.Economy.PullCostGems);
        base.OnDayStart(ctx);                              // free pulls
        SpendGems(ctx);                                    // the 100 pulls land before the first minute of play, as ten Pull10 calls
    }
}
```

From day 2 the whale behaves like the F2P bot except that it keeps using `Pull10` whenever it holds `10 * PULL_COST_GEMS` (with flat income it never does, so the two bots differ only through day 1). `compare` (section 12) prints the stage gap per day; `specs/02` section 4.3 expects roughly 25-35 stages at day 7.

## 6. Measurements

### 6.1 Event sink

```csharp
public sealed class BotEventSink : IEventSink
{
    public EventKinds Mask => EventKinds.BossFailed | EventKinds.BossCleared | EventKinds.StageAdvanced
                            | EventKinds.HeroPulled | EventKinds.OfflineGoldCredited;
    public int LPlus { get; }                    // HeroPulled with Result.Tier >= Rarity.L
    public List<WallRecord> Walls { get; }
    public int OfflineCredits { get; }           // sanity: equals days completed
    public void Publish(SimEvent e) { ... }
}

public sealed class WallRecord
{
    public int Stage;
    public long OpenTick, CloseTick;             // CloseTick = -1 while open
    public long OpenUtcMs, CloseUtcMs;           // wall clock, see below
    public bool IsOpen => CloseTick < 0;
    public double WallClockHours(long nowUtcMs) => ((IsOpen ? nowUtcMs : CloseUtcMs) - OpenUtcMs) / 3_600_000.0;
    public double ActiveHours(long nowTick, int tickHz) => ((IsOpen ? nowTick : CloseTick) - OpenTick) / (double)tickHz / 3600.0;
}
```

Wall rules:

- A wall opens on `BossFailed` with `FailsOnStage == 1` and closes on the `StageAdvanced` whose `FromStage` equals the wall's stage. One wall per stage at most.
- Wall-clock time of an event at tick `t` is `State.Clock.LastSyncUtcMs + (t - State.Clock.TickAtLastSync) * 1000 / TICK_HZ`, evaluated when the event arrives. Because the runner advances the fake clock in lockstep and re-syncs once per night, a wall that spans a night includes the `24 h - ActiveMinutesPerDay` offline gap, and a wall that opens and closes within one session measures its minutes.
- `ActiveHours` counts only ticks, so it excludes nights. Both are kept; the table prints wall-clock, acceptance check 4 uses wall-clock (see Open decisions).

### 6.2 Day row

```csharp
public sealed record DayRow(
    int Day, int Stage, double Dps, int StatLvl, double StatMult, double GearMult, double HeroMult, double ClassMult,
    int Pulls, int LPlus, int FirstWallStage, double LongestWallHours);
```

`Snapshot(day, sim, sink)` reads `Query.Dps`, `Query.StatMult`, `Query.GearMult`, `Query.HeroMult`, `Query.ClassMult`, `State.Stat.Level`, `State.Stage.Stage`, `State.HeroBanner.TotalPulls`, `sink.LPlus`, `State.Lifetime.FirstWallStage`, and `LongestWallHours = max over sink.Walls of WallClockHours(nowUtcMs)` with `nowUtcMs` the clock after the day's sync (an open wall counts with its duration so far; 0 when there has been no wall).

### 6.3 Layer shares

For acceptance check 2 and the `--shares` option. From a `DayRow`, with `L = ln(Dps)`:

```
statShare  = ln(StatMult)  / L
gearShare  = ln(GearMult)  / L
heroShare = ln(HeroMult) / L
classShare = ln(ClassMult) / L
baseShare  = ln(BASE_ATK)  / L        (the remainder; the five sum to 1)
```

`Math.Log` is allowed here: shares are reporting, never fed back into the sim, and the acceptance thresholds have 1e-6 of slack.

## 7. Output formats

All text output uses `CultureInfo.InvariantCulture`, `\n` line endings regardless of platform, and no trailing whitespace. Header lines start with `#` and never contain anything that varies between identical runs (no timestamps, no machine names, no sim assembly version; the version goes to stderr).

### 7.1 Single run table (`run`)

Exactly twelve columns, in this order:

| Column | Source | Format |
|---|---|---|
| `day` | row day | `{0,3}` |
| `stage` | `Stage` | `{1,5}` |
| `dps` | `Dps` | `{2,8:0.00e+00}` |
| `statLvl` | `StatLvl` | `{3,7}` |
| `statMult` | `StatMult` | `{4,8:0.00}` |
| `gearMult` | `GearMult` | `{5,10:0.00e+00}` |
| `heroMult` | `HeroMult` | `{6,9:0.00}` |
| `classMult` | `ClassMult` | `{7,9:0.00}` |
| `pulls` | `Pulls` | `{8,5}` |
| `L+` | `LPlus` | `{9,3}` |
| `firstWall` | `FirstWallStage`, `-` when 0 | `{10,9}` |
| `longestWallH` | `LongestWallHours` | `{11,12:0.00}` |

```csharp
const string RowFormat = "{0,3} {1,5} {2,8:0.00e+00} {3,7} {4,8:0.00} {5,10:0.00e+00} {6,9:0.00} {7,9:0.00} {8,5} {9,3} {10,9} {11,12:0.00}";
// header: string.Format(Invariant, RowFormat.Replace(":0.00e+00", "").Replace(":0.00", ""), "day", "stage", "dps", ...)
```

Illustrative output (numbers invented for shape, not golden values):

```
# herocat run v1
# bot=greedy-f2p seed=7 days=30 content=2026.10.30-a/3f9c1a2e
day stage      dps statLvl statMult   gearMult heroMult classMult pulls  L+ firstWall longestWallH
  1    88 3.10e+03      63     4.15   2.80e+01      1.90      1.50     6   0        70         0.25
  2   165 4.90e+05     122     7.10   1.20e+03      2.60      2.25    12   0        70         3.10
 ...
 30   545 7.10e+16     459    23.95   1.05e+13      5.64      5.06   180   4        70        26.50
```

Default rows are `SummaryDays` (clipped to `--days`); `--all-days` prints every day. The golden files are always generated with `--all-days`.

### 7.2 Sweep table (`sweep`)

One row per day, aggregated over seeds (section 8):

```
# herocat sweep v1
# bot=greedy-f2p seeds=1-20 days=30 content=2026.10.30-a/3f9c1a2e
day stageP10 stageMed stageP90   dpsMed statLvlMed gearMultMed heroMultMed classMultMed pullsMed L+Med firstWallMin firstWallMax longestWallHMax
```

Formats match the single-run columns they derive from. `firstWallMin`/`firstWallMax` ignore seeds with no wall yet and print `-` if none has walled.

### 7.3 CSV and JSON

`--format csv` prints the same columns with the header `day,stage,dps,statLvl,statMult,gearMult,heroMult,classMult,pulls,lPlus,firstWallStage,longestWallH` and the same number formats as the table (so a spreadsheet import sees what the table shows). `--format json` prints `RunResult` (or the sweep's per-seed `RunResult[]`) with doubles in round-trip (`R`) precision, for tooling that wants full numbers. Golden files use the table format only.

## 8. Running many seeds

`sweep` runs seeds in parallel with `Parallel.ForEach` (one `Simulation` per seed, shared read-only `IContentDb`, no shared mutable state, which `specs/03` section 16 guarantees), then aggregates in seed order so output does not depend on scheduling.

Percentiles are nearest-rank on the sorted per-seed values, which keeps integer columns integer and makes the result byte-stable:

```csharp
public static T Percentile<T>(IReadOnlyList<T> sortedAscending, double p)   // 0 < p <= 1
    => sortedAscending[Math.Max(0, (int)Math.Ceiling(p * sortedAscending.Count) - 1)];
// n = 20: P10 -> index 1, median (p = 0.5) -> index 9, P90 -> index 17
```

Seed lists: `--seeds 1-20` (inclusive range), `--seeds 3,5,9`, `--seeds 7`. The default is `1-20`. The seed list is part of the sweep header, so a golden sweep file is tied to it.

## 9. Acceptance checks

`AcceptanceChecker` encodes `specs/02` section 9. The CLI verb `acceptance` and the NUnit tests in `HeroCat.Bots.Tests/AcceptanceTests.cs` call the same class, so they cannot disagree. Every check is a test that fails today if the balance is off; none is skipped or marked expected-to-fail.

Thresholds and targets live in `src/HeroCat.Bots/Acceptance/acceptance-targets.json`, the single copy in code of `specs/00` section 2.6 and `specs/02` section 9. Keep it in sync with those sections when they change.

```json
{
  "stageTargets": { "1": 90, "3": 240, "7": 380, "14": 480, "30": 550 },
  "stageTolerance": 0.20,
  "stageCheckDays": [1, 7, 30],
  "shareCheckDays": [1, 7, 30],
  "shareMin": 0.03,
  "shareMax": 0.80,
  "firstWallMinStage": 60,
  "firstWallMaxStage": 200,
  "maxWallHoursThroughDay": 14,
  "maxWallHours": 96.0,
  "statLevelSlope": 0.95,
  "statLevelIntercept": 50
}
```

| Id | `specs/02` 9.x | Check (over the `greedy-f2p` sweep, default seeds, 30 days) | Failure message names |
|---|---|---|---|
| AC1 | 1 | For each day in `stageCheckDays`: median stage within `+/- stageTolerance` of `stageTargets[day]` | day, median, allowed range, target |
| AC2 | 2 | For each seed and each day in `shareCheckDays`: each of the four layer shares (section 6.3) in `[shareMin, shareMax]` | seed, day, layer, share |
| AC3 | 3 | For each seed: `FirstWallStage` at day 30 is in `[firstWallMinStage, firstWallMaxStage]` (0 means no wall and fails) | seed, stage |
| AC4 | 4 | For each seed: every wall that opened on or before day `maxWallHoursThroughDay` has `WallClockHours <= maxWallHours`, measured at close or at the end of that day if still open | seed, stage, hours |
| AC5 | 5 | For each seed and every day row: `StatLvl <= statLevelSlope * Stage + statLevelIntercept` | seed, day, statLvl, bound |
| AC6 | 6 | `run --bot greedy-f2p --seed 7 --days 30 --all-days` executed twice in-process yields byte-identical text; and the golden check (section 11) passes | first differing line |
| AC7 | `specs/10` 5 | Replay equivalence: record the command log of `greedy-f2p` seeds 1-4 for 30 days, replay it from genesis on a fresh `Simulation`, and require equal `StateHash()` at every day boundary and at the end; the committed `Fixtures/Replay/*.json` hashes also match | seed, day, expected hash, actual hash |

The CLI prints one line per check and exits 1 if any fails:

```
# herocat acceptance v1
# bot=greedy-f2p seeds=1-20 days=30 content=2026.10.30-a/3f9c1a2e
PASS AC1 day 1: median stage 88 in 72..108 (target 90)
PASS AC1 day 7: median stage 371 in 304..456 (target 380)
PASS AC1 day 30: median stage 548 in 440..660 (target 550)
PASS AC2 layer shares in 0.03..0.80 on days 1,7,30 for 20 seeds
FAIL AC3 first wall in 60..200: seed 13 walled first at stage 210
PASS AC4 no wall opened by day 14 exceeds 96.00 h
PASS AC5 statLvl <= 0.95*stage+50 on every day for 20 seeds
PASS AC6 deterministic: seed 7 twice identical; golden matches
PASS AC7 replay equivalence: seeds 1-4, 30 days, hashes equal at every day boundary
RESULT 1 failure(s) in 38.2 s
```

The AC2 share values, AC3 first-wall stages and AC4 longest walls are also printed per seed under `--verbose`, so a tuning session sees which seeds are marginal without rerunning.

## 10. Determinism

Determinism is a property of `HeroCat.Sim` (`specs/03` sections 4, 5 and 18); the harness adds the conditions that make it observable:

- Only `InvariantCulture` formatting, fixed `\n`, no timestamps or environment data in the compared text.
- Seeds in the sweep are aggregated in seed order after parallel execution.
- `Math.Log` is used only in reporting (section 6.3) and never influences the run.
- The same binary on x64 Linux, x64 Windows and Apple ARM64 must produce the same golden bytes; the golden check in CI on a different OS than the developer's machine is the standing proof. If it ever differs, the first suspect is a transcendental function or a `float` that crept into the sim, not the harness.

Replay fixtures (`specs/10` section 5.1): the harness writes `Fixtures/Replay/seed-N.json` (genesis seeds, content hash, command log, expected `StateHash` at each day boundary). The same files are loaded by the .NET server runtime, the WebAssembly host and an IL2CPP device test; a hash that differs on any of them is a determinism bug in the sim, not in the harness.

Tests: run seed 7 twice sequentially and compare bytes; run seeds 1-4 in parallel twice and compare each; compare the in-process `run` output with the committed golden.

## 11. Golden-file workflow

Golden files are the expected output of fixed commands, checked in under `golden/`:

| File | Command |
|---|---|
| `golden/run-greedy-f2p-seed7-30d.txt` | `run --bot greedy-f2p --seed 7 --days 30 --all-days` |
| `golden/run-whale-seed7-30d.txt` | `run --bot whale --seed 7 --days 30 --all-days` |
| `golden/sweep-greedy-f2p-seeds1-20-30d.txt` | `sweep --bot greedy-f2p --seeds 1-20 --days 30` |

`golden --check` regenerates each in memory and compares bytes. On a mismatch it prints the first 20 differing lines as a unified diff and one of two verdicts:

- The golden header's `content=` hash differs from the loaded content: "content changed; if the acceptance checks pass and the change is intended, run `golden --update`".
- The hashes match but the body differs: "sim or bot behaviour changed with identical content; this is a determinism or logic regression unless a sim change in this CR explains it". Do not update the golden until the cause is understood.

Intentional rebalance, in order:

1. Change the constant in `tools/*.py`, rerun the model, update `specs/00` section 2 and the content tables (`specs/05`), as `specs/00` requires.
2. `dotnet run -c Release --project src/HeroCat.Bots -- acceptance` must pass. If it does not, the rebalance is not done.
3. `dotnet run -c Release --project src/HeroCat.Bots -- golden --update` rewrites the three files.
4. One CR with: the content change, the three golden diffs, the `specs/00` table edit, and in the description the before/after `sweep` lines for days 1, 7, 14 and 30. The golden diff is the review artifact: a reviewer reads the day-30 median stage and the longest-wall column and decides whether the change does what the CR says.
5. A golden change with no content or sim change in the same CR is rejected.

The Python model is not a golden. If the C# seed-7 run differs from `specs/02` section 6 by more than 15% on day 1, 7 or 30, section 14 lists the known reasons; anything beyond those is a bug in one of the two and must be resolved before the harness is trusted.

## 12. CLI

Entry: `dotnet run -c Release --project src/HeroCat.Bots -- <verb> [options]`. Always run in Release for timing; Debug is 5-10x slower and `bench` refuses to report a budget verdict in Debug.

| Verb | Options | Does |
|---|---|---|
| `run` (default) | `--bot greedy-f2p\|whale` (default `greedy-f2p`), `--seed N` (7), `--days N` (30), `--all-days`, `--shares`, `--format table\|csv\|json` (table), `--out path`, `--content path` | one run, prints the table |
| `sweep` | `--bot`, `--seeds LIST` (1-20), `--days`, `--format`, `--out`, `--content`, `--parallel N` (CPU count) | per-day P10/median/P90 over seeds |
| `compare` | `--seeds`, `--days`, `--content` | runs `greedy-f2p` and `whale` on the same seeds; prints median stage of each and the gap per day |
| `acceptance` | `--seeds`, `--days`, `--time-budget-s N` (180), `--targets path`, `--verbose`, `--content` | section 9; exit code 1 on any failure or if wall time exceeds the budget |
| `golden` | `--check` (default) or `--update`, `--golden-dir path` (`golden/`), `--content` | section 11; exit code 1 on mismatch in check mode |
| `bench` | `--ticks N` (1,000,000), `--stage S` (300), `--max-ms N` (1000) | `Advance(N)` from a profile jumped to stage S with `NullEventSink`; prints ticks/s and exits 1 if slower than `--max-ms` |

`--content` defaults to the content file checked into the repo (`specs/05` names it); pointing it at a candidate file is how a designer tries a tuning idea without editing the default.

Examples:

```
# the daily table for the canonical seed
dotnet run -c Release --project src/HeroCat.Bots -- run --bot greedy-f2p --seed 7 --days 30

# full 30 rows as CSV for a spreadsheet
dotnet run -c Release --project src/HeroCat.Bots -- run --seed 7 --days 30 --all-days --format csv --out $KIROCREW_SCRATCH/seed7.csv

# whale vs F2P head start
dotnet run -c Release --project src/HeroCat.Bots -- compare --seeds 1-20 --days 14

# try a candidate content file against the acceptance checks
dotnet run -c Release --project src/HeroCat.Bots -- acceptance --content content/candidate-gear-1.0078.json --verbose

# accept a rebalance
dotnet run -c Release --project src/HeroCat.Bots -- golden --update

# the perf budget from specs/03 section 17
dotnet run -c Release --project src/HeroCat.Bots -- bench --ticks 1000000 --max-ms 1000
```

## 13. CI step

Two gates: `dotnet test` (unit tests for both assemblies plus the acceptance and golden tests) and the bot CLI with a wall-time budget. The budget comes from section 4: 43.2 million ticks at the 1 µs/tick budget is 43 s single-threaded; parallel over seeds on a 2-4 core runner lands at 15-25 s; 180 s leaves room for slow shared runners while still catching a 5x regression.

```yaml
# .ci/sim.yml (shape; adapt to the repo's CI system)
steps:
  - run: dotnet restore HeroCat.sln
  - run: dotnet build HeroCat.sln -c Release --no-restore
  - run: dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory!=Perf"
    # HeroCat.Sim.Tests + HeroCat.Bots.Tests, including [Category("Acceptance")] and [Category("Golden")]
  - run: dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory=Perf"
    # separate step so a perf flake is visible as such
  - run: dotnet run -c Release --no-build --project src/HeroCat.Bots -- bench --ticks 1000000 --max-ms 2000
    # 2x the local budget for CI hardware
  - run: timeout 300 dotnet run -c Release --no-build --project src/HeroCat.Bots -- acceptance --seeds 1-20 --days 30 --time-budget-s 180
  - run: dotnet run -c Release --no-build --project src/HeroCat.Bots -- golden --check
```

Locally, `dotnet test --filter "TestCategory!=Acceptance&TestCategory!=Perf"` runs in a few seconds for inner-loop work; the acceptance category is for pre-CR and CI.

## 14. Known deviations from the Python model

The harness is expected to land within the +/-15% that `specs/02` section 6 grants the model. These are the reasons the numbers are not identical; none of them should be "fixed" in the sim, because the sim follows `specs/00`.

| Difference | Python model | Sim and bot | Direction |
|---|---|---|---|
| Kill time | continuous `max(1, HP/DPS) + 0.5` s | discrete hits: `ceil(HP/hit) * 1 s + 0.5 s` | sim slightly slower early, converges as hits per kill grow |
| First boss attempt | never attempts an unclearable boss | the 10th enemy of a boss stage is the boss; a fail costs `BOSS_TIME_TICKS` plus a spawn | sim loses ~20 s per boss stage reached while not yet clearable |
| Boss retry | instant when clearable | at the next decision point (up to 60 s later) | sim up to 60 s slower per wall |
| Boss gear drop | plain `GEAR_DROP_P` | `BOSS_GEAR_DROP_P` and `BOSS_GEAR_MIN_RARITY` per `specs/00` | sim gear slightly stronger |
| Enhancement target | any slot, including an empty one | occupied slots only | sim wastes slightly less gold before the first drops |
| Offline gold | continuous kill time | discrete kill time via `Query.KillTimeSeconds` | sim slightly less offline gold |
| Whale pulls | singles, no guarantee | `Pull10` with the E+ guarantee | whale hero layer slightly stronger |
| RNG | Python `random` | SplitMix64 streams | different sequences; only distributions are comparable |

## 15. Unit tests (`HeroCat.Bots.Tests`)

Schedule (`BotRunnerTests.cs`):
1. One day advances `State.Tick` by `ActiveMinutesPerDay * 60 * TICK_HZ`, publishes exactly one `OfflineGoldCredited` with `CreditedSeconds == OFFLINE_CAP_H * 3600`, exactly one `DayRolledOver`, and leaves the clock at the next midnight.
2. `Decide` is called `ActiveMinutesPerDay * 60 / DecisionIntervalSeconds` times per day (counting policy stub).
3. `Run(days: 3)` returns three rows with `Day` 1, 2, 3 and non-decreasing `Stage`.

GreedyF2PBot (`GreedyF2PBotTests.cs`):
4. After `Decide`, `Currencies.Gold < StatCost(Stat.Level)` (nothing affordable left).
5. With slots at enhancement levels `[3, 1, 2, null, null, null]`, `Decide` attempts slot 1 (`enhance` counter +1); with all occupied slots at `ENH_MAX`, no attempt.
6. `OnDayStart` leaves `FreePullsRemaining == 0` and `Currencies.Gems` unchanged.
7. With `7 * PULL_COST_GEMS + PULL_COST_GEMS / 2` gems, `Decide` makes 7 single pulls, never a `Pull10`, and leaves `PULL_COST_GEMS / 2` gems.
8. After pulls, `Equipped` equals the top `HERO_SLOTS` by `HeroPct`.
9. Walled with `BossClearable == true`: `Decide` leaves `Stage.Boss == Pending`; with `false`: still `Walled`.

WhaleBot (`WhaleBotTests.cs`):
10. After day-1 `OnDayStart`, `TotalPulls == FREE_PULLS_PER_DAY + Day1ExtraPulls`, `gacha_hero.Counter == 3 * TotalPulls`, and gems below `10 * PULL_COST_GEMS`.
11. On day 2, `OnDayStart` grants nothing extra.

Measurement (`WallTrackerTests.cs`, `LayerSharesTests.cs`):
12. Scripted events: a wall opened at tick 1000 and closed at tick 7000 within one session measures `6000 / TICK_HZ / 3600` hours both ways; a wall spanning one night measures `22 h + minutes` wall-clock and only the ticks as active hours.
13. An open wall at snapshot time reports its duration so far; `LongestWallHours` is 0 with no walls.
14. `LPlus` counts L and M results only.
15. Shares of a hand-built row sum to 1 within 1e-9 and match hand-computed values.

Report (`TableWriterTests.cs`, `SweepTests.cs`):
16. A fixed `DayRow` formats to an exact expected string; the same under `CultureInfo("de-DE")` as current culture.
17. The header aligns with the row (same column count and widths).
18. `Percentile` on `1..20`: P10 = 2, median = 10, P90 = 18; on a single value returns it.
19. Sweep output is identical whether run with `--parallel 1` or `--parallel 8`.
20. Seed list parsing: `1-20`, `3,5,9`, `7`; rejects `0-5` and `5-1`.

Determinism and golden (`DeterminismTests.cs`, `[Category("Golden")] GoldenTests.cs`):
21. `run` seed 7 twice yields identical bytes.
22. In-process output equals each committed golden file byte for byte; on mismatch the assertion message includes the regenerate command.

Acceptance (`[Category("Acceptance")] AcceptanceTests.cs`):
23. One `[Test]` per AC1-AC6, sharing a `[OneTimeSetUp]` that runs the default sweep once.

Bench (`[Category("Perf")] BenchTests.cs`):
24. `bench --ticks 1000000` from stage 300 completes under 2 s in Release.

## 16. P1 and P2

| Item | Tag | Note |
|---|---|---|
| Event-driven decisions (act on `GearDropped`, `BossFailed`, `StatLeveled` instead of every 60 s) | P1 | would remove the 60 s boss-retry latency; keep the minute cadence as the parity mode |
| `--gems content` using `specs/04` quests and idle chests | P1 | section 5.3 |
| Saver bot (banks gems for 10-pulls), lapsed-player bot (skips days), two-session bot (two 1 h sessions) | P1 | each is one `IBotPolicy`; the runner's day loop gains a session pattern option |
| Per-minute trace (`--trace path`) with stage, gold, DPS per decision | P1 | for wall-shape plots |
| Python parity script that runs both models from one content file and diffs them | P1 | would turn section 14 into a measured table |
| Substat and prestige bots | P2 | follow the sim |

## 17. Open decisions

1. Gem income is a flat `GEMS_PER_DAY_F2P` grant until `specs/04`'s quest system exists in the sim. When it does, decide whether the acceptance sweep uses `--gems content` (truer) or stays flat (comparable to the Python model).
2. AC4 measures walls in wall-clock hours (a wall that spans one night is at least 22 h). `specs/02` section 1 talks about "hours of real time" and section 9 about "bot time"; if the intent is active play time, switch AC4 to `ActiveHours` and add a `longestWallActiveH` column. The record keeps both so the switch is a one-line change.
3. AC2 is checked per seed. If a single low-L+ seed makes it flap, the alternative is checking the median seed's shares; the tuning answer in `specs/02` section 5 (raise hero base pct) is preferable to loosening the check.
4. Acceptance targets live in `acceptance-targets.json` in the bots project. If `specs/05` exposes `specs/00` section 2.6 as a content group, read them from `IContentDb` instead and delete the JSON.
5. Decision cadence is 60 s to mirror the Python model. The 60 s boss-retry latency is the largest known deviation; an event-driven mode (P1) would remove it, but the parity mode should remain the one the goldens are generated from until the Python model is retired.
6. The acceptance sweep runs twice in CI (inside `dotnet test` and as the CLI step with the time budget). Drop one once CI time matters; the CLI step is the one with the wall-time gate.
7. Seeds `1-20` are fixed. Rotating seeds would catch tuning that overfits those twenty, at the cost of the sweep golden; a monthly `--seeds 21-40` manual run is the cheap middle ground.
