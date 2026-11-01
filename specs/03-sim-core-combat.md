# 03 - Sim Core and Combat

Scope: the `HeroCat.Sim` assembly. Pure C#, no `UnityEngine` reference. It owns the tick loop, the state object graph, combat resolution, the RNG streams, the economy actions, the event outbox and the command API. The Unity client (`specs/07`), the bot harness (`specs/09`) and the offline catch-up (`specs/06`) all drive this one assembly.

Constants are named as in `specs/00` section 2 and are read from `ContentDb` (`specs/05`). Nothing in this document is a literal in production code. Where this document and `specs/00` disagree, `specs/00` wins.

## 1. Design goals

1. One tick function, one state object. Everything observable is a pure function of `(ContentDb, GameState)` plus the sequence of commands and ticks applied.
2. Save and restore are exact. Serialising `GameState` mid-fight and restoring it continues the same fight, the same drop sequence and the same gacha results (`specs/01` section 6).
3. Deterministic across machines. Two builds on different CPUs produce byte-identical bot tables for the same seed (`specs/02` section 9 item 6).
4. Fast enough for bots: `Advance(1_000_000)` in under one second on a developer laptop in Release.
5. Cheap enough for phones: the 10 Hz tick is a few hundred nanoseconds; the client never waits on it.

## 2. Project layout and namespaces

```
src/
  HeroCat.Sim/                 netstandard2.1, LangVersion 9.0 (Unity 2022 LTS compatible), no package references
  HeroCat.Sim.Tests/           net8.0, NUnit (same framework the Unity Test Runner uses, so tests can also run inside Unity)
  HeroCat.Bots/                net8.0 console, see specs/09
  HeroCat.Bots.Tests/          net8.0, NUnit, see specs/09
HeroCat.sln
```

Namespaces inside `HeroCat.Sim`:

| Namespace | Holds |
|---|---|
| `HeroCat.Sim` | `Simulation`, `SimQuery`, command results, `DebugCommands` |
| `HeroCat.Sim.State` | `GameState` and every nested state class and record |
| `HeroCat.Sim.Content` | `IContentDb` and the content record types the sim reads (loader lives in `specs/05`) |
| `HeroCat.Sim.Rng` | `SplitMix64`, `RngStream`, `StreamNames`, `Fnv1a64` |
| `HeroCat.Sim.Combat` | the per-tick combat step (internal) |
| `HeroCat.Sim.Economy` | stat levels, gear drops, enhancement, catch-up (internal) |
| `HeroCat.Sim.Gacha` | `IGachaService`, `LocalGachaService`, `PullResult` (algorithm in `specs/01` section 8) |
| `HeroCat.Sim.Events` | `SimEvent` records, `IEventSink`, `EventOutbox`, `NullEventSink` |
| `HeroCat.Sim.Time` | `IClock`, `FakeClock`, `SystemClock` |
| `HeroCat.Sim.Format` | `BigNumber.Short(double)` display formatter |

Rules for the assembly:

- `InternalsVisibleTo("HeroCat.Sim.Tests")` only. Bots and the client use the public surface.
- No `System.Text.Json`, no reflection, no `DateTime.Now`, no `System.Random`, no `Math.Pow`, `Math.Exp` or `Math.Log` anywhere in the assembly (section 4 explains why).
- No static mutable state. Two `Simulation` instances in one process must not affect each other.

## 3. Content the sim reads

`IContentDb` is defined in this assembly because the sim is its consumer; `specs/05` defines the file format, the loader and validation. The property names below are a contract: `specs/05` may add members but must keep these.

```csharp
namespace HeroCat.Sim.Content
{
    public enum Rarity { C = 0, R = 1, E = 2, L = 3, M = 4 }   // used for gear rarity and hero tier alike

    public interface IContentDb
    {
        string Version { get; }          // human label, e.g. "2026.10.30-a"
        string Hash { get; }             // stable hash of the loaded content, printed in bot output headers

        CombatContent Combat { get; }    // TICK_HZ, BASE_ATK, ATTACK_INTERVAL_TICKS, HP0, HP_G, KILLS_PER_STAGE,
                                         // SPAWN_DELAY_TICKS, BOSS_EVERY, BOSS_HP_MULT, BOSS_TIME_TICKS, BOSS_GOLD_MULT, STAGE_CAP
        EconomyContent Economy { get; }  // GOLD0, GOLD_G, STAT_PER_LVL, STAT_COST0, STAT_COST_G, GEMS_PER_DAY_F2P,
                                         // PULL_COST_GEMS, FREE_PULLS_PER_DAY, OFFLINE_RATE, OFFLINE_CAP_H, OFFLINE_MIN_GAP_S
        GearContent Gear { get; }        // GEAR_SLOTS, GEAR_DROP_P, BOSS_GEAR_DROP_P, BOSS_GEAR_MIN_RARITY, GEAR_SLOT_G,
                                         // RarityWeights[5], RarityMult[5], ENH_MAX, ENH_PER_LVL, ENH_SUCCESS floor/step, ENH_COST gold mult
        HeroContent Heroes { get; }     // HERO_SLOTS, BasePct[5], HERO_LVL_CUM[10], HERO_LVL_BONUS, ShardValue[5], Pool[5] of HeroId
        BannerDef HeroBanner { get; }   // BaseRates[5], PITY_SOFT_START, PITY_SOFT_STEP, PITY_HARD, M_SHARE_OF_LPLUS
        ClassContent Class { get; }      // CLASS_STAGES[], CLASS_MULT

        // Derived tables, see section 4. Implemented once in HeroCat.Sim.Content.PowerTables, not by the loader.
        double Hp(int stage);
        double Gold(int stage);
        double StatCost(int level);
        double GearSlotPct(int tier);    // GEAR_SLOT_G^tier - 1, before rarity
        double ClassMult(int advances);
    }

    public readonly record struct HeroId(string Value);
}
```

Each content record is a plain C# record with `init` properties named after the constant in `specs/00` (`TickHz`, `BaseAtk`, `AttackIntervalTicks`, `Hp0`, `HpG`, and so on). Content validation (rates sum to 1, positive growth factors, `HERO_LVL_CUM` strictly increasing) is `specs/05`'s job; the sim assumes valid content and asserts it only in `Validate()` (section 15).

Two values the sim needs that `specs/00` section 2 describes in prose rather than naming: the boss gear drop chance (0.10) and the boss minimum rarity (Rare). This spec calls them `BOSS_GEAR_DROP_P` and `BOSS_GEAR_MIN_RARITY`; see Open decisions.

## 4. Numeric model

**Type.** Every magnitude (HP, damage, DPS, gold, cost, percentages) is `double`. Counts (gems, shards, levels, stage, kills, ticks, RNG counters) are `int`, `long` or `ulong`. `float` and `decimal` are not used. `double` holds stage 550 (1e17) with 1e291 of headroom; `specs/02` section 2 notes the switch to a mantissa/exponent type if stages ever pass ~9000. The sim reads a `STAGE_CAP` constant from `CombatContent` (proposed value 9000, to be added to `specs/00` and `specs/05`) and emits `StageCapReached` instead of advancing past it. Switching the number type is P2.

**No transcendental functions.** `Math.Pow` and friends are not bit-exact across libm implementations (x64 glibc, Apple ARM64, Windows UCRT, IL2CPP). A one-ULP difference in `HP(s)` flips a `ceil(HP / hitDamage)` boundary somewhere in a 30-day run and breaks cross-machine determinism. All geometric curves are therefore built by repeated multiplication, which uses only IEEE 754 basic operations and is bit-exact everywhere:

```csharp
// HeroCat.Sim.Content.PowerTables (internal). One instance per IContentDb.
internal sealed class GeometricTable
{
    private readonly List<double> _values;   // _values[0] = a, _values[k] = _values[k-1] * g
    private readonly double _g;
    public GeometricTable(double a, double g, int initialCapacity) { ... }
    public double this[int k] { get { while (_values.Count <= k) _values.Add(_values[_values.Count - 1] * _g); return _values[k]; } }
}
// Hp(s)        = hpTable[s]          with a = HP0,        g = HP_G
// Gold(s)      = goldTable[s]        with a = GOLD0,      g = GOLD_G
// StatCost(n)  = statCostTable[n]    with a = STAT_COST0, g = STAT_COST_G
// GearSlotPct(t) = gearTable[t] - 1  with a = 1.0,        g = GEAR_SLOT_G
// ClassMult(k) = classTable[k]       with a = 1.0,        g = CLASS_MULT
```

The tables are seeded to length 1000 at construction (enough for 30 days) and grow on demand. The server side (`specs/08`) must use the same construction when it recomputes anything; the difference from `Math.Pow` is in the last few bits and is irrelevant to balance but relevant to equality.

**Hit arithmetic.** Enemy HP is reduced by subtraction each hit (`EnemyHp -= HitDamage`), never by precomputing a hit count. The advisory `HitsToKill` query (section 14) uses `Math.Ceiling(hp / dmg)`, which agrees with the subtraction loop except in rare last-bit cases; the loop is authoritative.

**Display formatter.** `HeroCat.Sim.Format.BigNumber.Short(double v)` returns three significant digits with a suffix: none below 1000, then `K, M, B, T, aa, ab, ..., az, ba, ...` for each further factor of 1000 (`K` = 1e3, `T` = 1e12, `aa` = 1e15, `ab` = 1e18). Examples: `999`, `1.23K`, `45.6M`, `123aa` for 1.23e17, `2.90ab` for 2.9e18. Rounding is half-away-from-zero, culture-invariant. It lives in the sim assembly so the client (`specs/07`) and any bot column that wants a short form agree. The bot table itself uses scientific notation (`specs/09`), not this formatter.

## 5. RNG design

### 5.1 Requirements

1. Random access: result `k` of a stream is a pure function `Derive(seed, k)`. The commitment in `specs/01` section 6 depends on this: a restored save restores an older counter and re-receives the same results.
2. Named, independent streams: `combat`, `gear_drop`, `gear_rarity`, `enhance`, `gacha_hero` (`specs/00` section 5). Consuming draws on one stream never changes another.
3. 53-bit uniform doubles, so `(int)(u * n)` is unbiased for any pool size we have.
4. Trivially reimplementable in Python (tools), Go or TypeScript (server), with published test vectors.

### 5.2 Algorithm: SplitMix64 as a stateless derive

```csharp
namespace HeroCat.Sim.Rng
{
    public static class SplitMix64
    {
        public const ulong Gamma = 0x9E3779B97F4A7C15UL;

        public static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// Output k (0-based) of the reference SplitMix64 generator seeded with `seed`.
        public static ulong Derive(ulong seed, ulong k) => Mix(unchecked(seed + (k + 1) * Gamma));

        /// Uniform in [0, 1) with 53 bits of precision.
        public static double ToUnit(ulong x) => (x >> 11) * (1.0 / (1UL << 53));
    }

    public static class Fnv1a64
    {
        public static ulong Hash(string s)   // FNV-1a over the UTF-8 bytes
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 0x100000001B3UL; }
            return h;
        }
    }

    public static class StreamNames
    {
        public const string Combat = "combat", GearDrop = "gear_drop", GearRarity = "gear_rarity",
                            Enhance = "enhance", GachaHero = "gacha_hero";
        public static readonly string[] All = { Combat, GearDrop, GearRarity, Enhance, GachaHero };

        /// Per-stream seed. profileSeed comes from the profile (section 6); the server may override one stream's seed (specs/08).
        public static ulong StreamSeed(ulong profileSeed, string name) => SplitMix64.Mix(unchecked(profileSeed + Fnv1a64.Hash(name)));
    }

    /// Persisted as part of GameState.Rng. Seed and Counter are the entire state.
    public sealed class RngStream
    {
        public string Name { get; init; }
        public ulong Seed { get; init; }
        public ulong Counter { get; set; }

        public double NextDouble() => SplitMix64.ToUnit(SplitMix64.Derive(Seed, Counter++));
        public int NextIndex(int n) => (int)(NextDouble() * n);              // 0..n-1; same arithmetic as specs/01 section 8
        public double PeekDouble(ulong counter) => SplitMix64.ToUnit(SplitMix64.Derive(Seed, counter));   // audit and tests only
    }
}
```

Why SplitMix64 and not PCG32 or `System.Random`:

- SplitMix64's output is already a hash of `seed + (k+1) * gamma`; "stateless derive" is its natural form, not a bolt-on. PCG32 is a sequential LCG; random access needs an O(log k) jump-ahead and its 32-bit output needs two draws per 53-bit double. Both are solvable but add code and audit surface for no gain.
- `Derive(seed, k)` is exactly the k-th output of the reference generator (Java `SplittableRandom`, Rust `splitmix64`, every C reference). A server team can audit a pull with any off-the-shelf implementation.
- `System.Random` changed algorithm in .NET 6, is not available with the same semantics inside Unity's runtime and has no random access.
- Quality is adequate for a game: SplitMix64 passes BigCrush. It is not a CSPRNG; the gacha seed's secrecy comes from the server never sending it (`specs/01` section 6, `specs/08`), not from the mixer.

The `+ 1` in `Derive` matters: `Mix(0) == 0`, so without the offset a zero seed would start with a zero draw. With the offset the first output for seed 0 is the standard reference value below.

### 5.3 Test vectors

Produced by the Python reference in section 5.4. The first line is the published SplitMix64 reference value (`new SplittableRandom(0).nextLong()` in Java is `-2152535657050944081`, which is this number as a signed 64-bit integer).

```
Derive(0x0000000000000000, 0)    = 0xE220A8397B1DCDAF   ToUnit = 0.88331080821364261
Derive(0x0000000000000000, 1)    = 0x6E789E6AA1B965F4   ToUnit = 0.43152799704850997
Derive(0x0000000000000000, 2)    = 0x06C45D188009454F   ToUnit = 0.026433771592597743
Derive(0x0000000000000000, 1000) = 0x2CFA2F23425329E1   ToUnit = 0.17569250689332405
Derive(0x0000000000000007, 0)    = 0x63CBE1E459320DD7   ToUnit = 0.38982974839127149
Derive(0x0000000000000007, 1)    = 0x044C3CD7F43C661C   ToUnit = 0.016788294528156111
Derive(0x0000000000000007, 2)    = 0xE6984080BAB12A02   ToUnit = 0.90076068060688341
Derive(0x0000000000000007, 1000) = 0xCF1B8D545D1615CA   ToUnit = 0.80901416120203629
Derive(0x123456789ABCDEF0, 0)    = 0x161922C645CE50E8   ToUnit = 0.08632104244844041
Derive(0x123456789ABCDEF0, 1)    = 0xAD760CAFA1697B60   ToUnit = 0.67758254325727829

Fnv1a64("combat")      = 0xCA0BF97CCE717B21   StreamSeed(7, "combat")      = 0xD638BA460341F349
Fnv1a64("gear_drop")   = 0xC1A5A0EE94A0A3BA   StreamSeed(7, "gear_drop")   = 0xFB1D7F3436BC41A4
Fnv1a64("gear_rarity") = 0x6AB86776709CD868   StreamSeed(7, "gear_rarity") = 0x9A1426458CA81741
Fnv1a64("enhance")     = 0xDC465426329C1EE9   StreamSeed(7, "enhance")     = 0x84C074F86395F937
Fnv1a64("gacha_hero")  = 0x8C00A1F67FD04D96   StreamSeed(7, "gacha_hero")  = 0x9BB72FE2750126AC
```

### 5.4 Python reference (for `tools/` and server audits)

```python
M = (1 << 64) - 1
GAMMA = 0x9E3779B97F4A7C15
def mix64(z):
    z &= M
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & M
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & M
    return (z ^ (z >> 31)) & M
def derive(seed, k):  return mix64((seed + (k + 1) * GAMMA) & M)
def to_unit(x):       return (x >> 11) * (1.0 / (1 << 53))
def fnv1a64(s):
    h = 0xCBF29CE484222325
    for b in s.encode(): h ^= b; h = (h * 0x100000001B3) & M
    return h
def stream_seed(profile_seed, name): return mix64((profile_seed + fnv1a64(name)) & M)
```

### 5.5 Draw budget per stream

The counter of every stream is a known function of lifetime counters, which `Validate()` checks (section 15) and the server can audit:

| Stream | Draws | When | Counter invariant |
|---|---|---|---|
| `combat` | 0 in P0 | Reserved for P1 crit and attack-speed procs | `== 0` |
| `gear_drop` | 1 per kill | `u < dropChance` | `== Lifetime.Kills` |
| `gear_rarity` | 2 per drop | `u1` rarity, `u2` slot | `== 2 * Lifetime.GearDrops` |
| `enhance` | 1 per attempt | `u < ENH_SUCCESS(l)` | `== Lifetime.EnhanceAttempts` |
| `gacha_hero` | 3 per pull | `u1` L+ check, `u2` tier, `u3` hero index (`specs/01` section 6) | `== 3 * HeroBanner.TotalPulls` |

Draws are consumed even when the outcome is predetermined (hard pity, boss minimum rarity) so these equalities hold.

### 5.6 Profile seed

`GameState.Rng.ProfileSeed` is a `ulong` assigned once at profile creation. Prototype: `SplitMix64.Mix(lo ^ hi)` where `lo` and `hi` are the two little-endian 64-bit halves of `ProfileId.ToByteArray()`. Bots and tests pass an explicit seed instead (`GameState.NewProfile(content, id, profileSeed, createdUtc)`); the harness's `--seed 7` means `ProfileSeed = 7`. Stream seeds are derived from it by `StreamNames.StreamSeed` at creation and persisted individually so that a server-issued `gacha_hero` seed (`specs/08`) is a per-stream override, not a schema change.

## 6. State object graph

`GameState` is the save payload. It is a plain mutable object graph: public properties, no behaviour beyond `DeepClone()` and `NewProfile(...)`. `specs/06` owns serialisation and the `SchemaVersion` field; this section owns the in-memory shape. Everything `Tick()` reads or writes lives here. `Simulation` holds no state of its own except caches that are rebuilt from `GameState` on construction.

```csharp
namespace HeroCat.Sim.State
{
    public sealed class GameState
    {
        public int SchemaVersion { get; set; }            // specs/06
        public Guid ProfileId { get; set; }
        public long Tick { get; set; }                    // lifetime ticks simulated
        public WallClockState Clock { get; set; }
        public DailyState Daily { get; set; }
        public Currencies Currencies { get; set; }
        public StatState Stat { get; set; }
        public StageState Stage { get; set; }
        public CombatState Combat { get; set; }
        public GearState Gear { get; set; }
        public HeroState Heroes { get; set; }
        public BannerState HeroBanner { get; set; }
        public ClassState Class { get; set; }
        public RngState Rng { get; set; }
        public LifetimeStats Lifetime { get; set; }

        public static GameState NewProfile(IContentDb content, Guid profileId, ulong profileSeed, DateTimeOffset createdUtc);
        public GameState DeepClone();
    }

    public sealed class WallClockState { public long LastSyncUtcMs; public long TickAtLastSync; }
    public sealed class DailyState     { public int DayKey; public int FreePullsUsed; }      // DayKey = UTC days since 1970-01-01
    public sealed class Currencies         { public double Gold; public int Gems; public int Shards; }
    public sealed class StatState      { public int Level; }

    public enum BossPhase { None, Pending, Walled }
    public sealed class StageState
    {
        public int Stage = 1;
        public int KillsInStage;          // 0 .. KILLS_PER_STAGE-1
        public BossPhase Boss;            // None: normal mobs. Pending: the next spawn is the boss. Walled: boss failed on this stage, farming normals.
        public int BossFailsOnStage;      // reset on stage advance
    }

    public enum CombatPhase { Spawning, Fighting }
    public sealed class CombatState
    {
        public CombatPhase Phase;
        public int SpawnTimer;            // ticks until spawn, valid in Spawning
        public int AttackTimer;           // ticks until next hit, valid in Fighting
        public int BossTimer;             // ticks until boss despawns, valid when EnemyIsBoss
        public bool EnemyIsBoss;
        public double EnemyHp;
        public double EnemyMaxHp;
    }

    public sealed record GearPiece(int Tier, Rarity Rarity, int Enhance);   // BasePct is derived, never stored
    public sealed class GearState { public GearPiece?[] Slots; }           // length GEAR_SLOTS; null = empty slot

    public sealed class HeroState
    {
        public Dictionary<HeroId, int> Copies;   // lifetime copies per hero
        public HeroId?[] Equipped;               // length HERO_SLOTS; distinct, all owned
    }
    public sealed class BannerState { public int PityCounter; public int TotalPulls; }
    public sealed class ClassState  { public int Advances; }

    public sealed class RngState
    {
        public ulong ProfileSeed;
        public Dictionary<string, RngStream> Streams;   // keyed by StreamNames.*; a missing key is created on load (P1 streams)
    }

    public sealed class LifetimeStats
    {
        public long Kills, BossKills, BossFails, GearDrops, EnhanceAttempts, EnhanceSuccesses;
        public double GoldEarned, OfflineGoldEarned;
        public int FirstWallStage;        // stage of the first BossFailed ever; 0 until then
    }
}
```

`NewProfile` sets: `Tick = 0`, `Stage = 1`, `KillsInStage = 0`, `Boss = None`, `Combat.Phase = Spawning` with `SpawnTimer = SPAWN_DELAY_TICKS`, all wallets zero, all slots empty, `Daily.DayKey = DayKeyOf(createdUtc)`, `Clock.LastSyncUtcMs = createdUtc`, `Clock.TickAtLastSync = 0`, five streams with `Counter = 0`. Starter grants (tutorial gems and free pulls, `specs/01` section 5) are applied by the economy layer in `specs/04`, not by `NewProfile`.

Units, to avoid a recurring confusion: a gear piece's `BasePct` is a fraction (`0.5` means +50%), matching `prod(1 + gearPct * ...)` in `specs/00` section 3. A hero's `heroPct` is in percentage points (`40` means +40%), matching `1 + sum(heroPct) / 100`.

## 7. Tick loop and combat resolution

### 7.1 Entry points

```csharp
public sealed class Simulation
{
    public Simulation(IContentDb content, GameState state, IEventSink events, IGachaService? gacha = null);

    public IContentDb Content { get; }
    public GameState State { get; }
    public SimQuery Query { get; }            // section 14

    public void Tick();                       // exactly one tick
    public void Advance(int ticks);           // Tick() repeated; no commands can interleave
    public void SyncWallClock(DateTimeOffset nowUtc);   // section 11
    // commands: section 13
}
```

`Advance(n)` is `for (i < n) Tick()`. Implementations may fast-forward the Spawning phase (jump `SpawnTimer` ticks at once) provided every observable value after `Advance(n)` equals the value after `n` individual `Tick()` calls, including event tick stamps. P0 ships the plain loop; the fast path is a P1 optimisation if the budget in section 17 is missed.

### 7.2 One tick, in order

```
Tick():
  State.Tick += 1
  switch Combat.Phase:
    Spawning:
      Combat.SpawnTimer -= 1
      if Combat.SpawnTimer == 0: Spawn()
    Fighting:
      Combat.AttackTimer -= 1
      if Combat.AttackTimer == 0:
        Combat.AttackTimer = ATTACK_INTERVAL_TICKS
        Combat.EnemyHp -= Query.HitDamage                 # publish HitLanded if masked in
        if Combat.EnemyHp <= 0: Kill(); return
      if Combat.EnemyIsBoss:
        Combat.BossTimer -= 1
        if Combat.BossTimer == 0: BossFail()
```

The hit check runs before the boss timer check, so a hit landing on the final tick counts. With `ATTACK_INTERVAL_TICKS = 10` and `BOSS_TIME_TICKS = 200` the boss can take exactly `BOSS_TIME_TICKS / ATTACK_INTERVAL_TICKS` hits, which reproduces the wall condition `BOSS_HP_MULT * HP(s) <= DPS(s) * 20 s` from `specs/00` section 3 exactly.

### 7.3 Spawn

```
Spawn():
  isBoss = Stage.Boss == Pending
  Combat.EnemyIsBoss = isBoss
  Combat.EnemyMaxHp = Combat.EnemyHp = isBoss ? BOSS_HP_MULT * Hp(Stage.Stage) : Hp(Stage.Stage)
  Combat.AttackTimer = ATTACK_INTERVAL_TICKS
  Combat.BossTimer   = isBoss ? BOSS_TIME_TICKS : 0
  Combat.Phase = Fighting
  publish EnemySpawned
```

The first hit lands `ATTACK_INTERVAL_TICKS` after spawn, so a kill that needs `h` hits occupies `h * ATTACK_INTERVAL_TICKS + SPAWN_DELAY_TICKS` ticks, matching the kill-time formula in `specs/00` section 3 (`max(ATTACK_INTERVAL, ceil(HP/hit) * ATTACK_INTERVAL) + SPAWN_DELAY`, with `h >= 1` always).

### 7.4 Kill

```
Kill():
  s = Stage.Stage
  isBoss = Combat.EnemyIsBoss
  gold = Gold(s) * (isBoss ? BOSS_GOLD_MULT : 1)
  Currencies.Gold += gold; Lifetime.GoldEarned += gold; Lifetime.Kills += 1
  if isBoss: Lifetime.BossKills += 1
  publish EnemyKilled(s, isBoss, gold)

  RollGearDrop(isBoss)                                     # section 8.2, consumes gear_drop (and gear_rarity on a drop)

  if isBoss:
    publish BossCleared(s)
    AdvanceStage(viaBoss: true)
  else if Stage.Boss == Walled:
    pass                                                   # farming a walled stage: no kill credit toward advance
  else:
    Stage.KillsInStage += 1
    if Stage.KillsInStage == KILLS_PER_STAGE - 1 and s % BOSS_EVERY == 0:
      Stage.Boss = Pending                                 # the 10th enemy of a boss stage is the boss (first attempt is automatic)
    else if Stage.KillsInStage >= KILLS_PER_STAGE:
      AdvanceStage(viaBoss: false)

  Combat.Phase = Spawning; Combat.SpawnTimer = SPAWN_DELAY_TICKS

AdvanceStage(viaBoss):
  from = Stage.Stage
  if from >= STAGE_CAP: publish StageCapReached; Stage.Boss = None; return
  Stage.Stage = from + 1; Stage.KillsInStage = 0; Stage.Boss = None; Stage.BossFailsOnStage = 0
  publish StageAdvanced(from, from + 1, viaBoss)
  while Class.Advances < CLASS_STAGES.Length and Stage.Stage >= CLASS_STAGES[Class.Advances]:
    Class.Advances += 1; MarkDpsDirty(); publish ClassAdvanced(Class.Advances, Stage.Stage)
```

Reaching stage `CLASS_STAGES[k]` grants the advance on arrival (clearing stage 49 grants the first one), mirroring `tools/power_curve_model.py`.

### 7.5 Boss fail and the wall

```
BossFail():
  s = Stage.Stage
  Stage.Boss = Walled; Stage.BossFailsOnStage += 1; Lifetime.BossFails += 1
  if Lifetime.FirstWallStage == 0: Lifetime.FirstWallStage = s
  publish BossFailed(s, Stage.BossFailsOnStage)
  Combat.Phase = Spawning; Combat.SpawnTimer = SPAWN_DELAY_TICKS   # next spawn is a normal mob of stage s
```

While `Walled`, normal mobs of stage `s` spawn, pay `Gold(s)`, roll tier-`s` drops and do not count toward `KillsInStage`, which stays at `KILLS_PER_STAGE - 1`. The player leaves the wall only by killing the boss. Retrying is the `AttemptBoss` command (section 13). P1: auto-retry every 60 s as a content constant.

## 8. Economy actions

### 8.1 Stat levels

Cost of level `n -> n+1` is `StatCost(n)`. `BuyStatLevel()` rejects with `NotEnoughGold` when `Currencies.Gold < StatCost(Stat.Level)`; otherwise it deducts, increments, marks DPS dirty and publishes `StatLeveled`. `BuyStatLevelsMax()` repeats until rejected and returns the count bought (the client's "Max" button and the greedy bot use it).

### 8.2 Gear drops

```
RollGearDrop(isBoss):
  p = isBoss ? BOSS_GEAR_DROP_P : GEAR_DROP_P
  if gearDrop.NextDouble() >= p: return
  u1 = gearRarity.NextDouble(); u2 = gearRarity.NextDouble()
  rarity = isBoss ? SampleRenormalised(RarityWeights, minimum: BOSS_GEAR_MIN_RARITY, u1) : Sample(RarityWeights, u1)
  slot = (int)(u2 * GEAR_SLOTS)
  piece = new GearPiece(Tier: GEAR_TIER(Stage.Stage), rarity, Enhance: 0)
  Lifetime.GearDrops += 1
  current = Gear.Slots[slot]
  equipped = current == null || BasePct(piece) > BasePct(current)
  if equipped: Gear.Slots[slot] = piece; MarkDpsDirty()
  publish GearDropped(slot, piece, equipped, replaced: equipped ? current : null)
```

- `Sample` walks the cumulative weights in order C, R, E, L, M and returns the first rarity whose cumulative weight exceeds `u1`. `SampleRenormalised` does the same over the rarities at or above the minimum with weights rescaled to sum to 1 (the same technique `specs/01` section 3.1 uses for the non-L+ branch).
- `BasePct(piece) = GearSlotPct(piece.Tier) * RarityMult[piece.Rarity]`. Tier 0 pieces (stages 1-9) have `BasePct = 0` and are still real pieces; they occupy the slot until something better drops.
- **P0 equip rule**: a drop replaces the slot's piece iff its `BasePct` is strictly greater, enhancement is not considered and does not carry over, the replaced piece is discarded. This is the rule in `tools/power_curve_model.py` and is what the harness validates against. P1: a gear bag, manual equip, enhancement transfer; `GearDropped.Equipped = false` and `Replaced` already carry what the P1 UI needs.

### 8.3 Enhancement

```
Enhance(slot):
  piece = Gear.Slots[slot]
  if piece == null:                 return Rejected(EmptySlot)
  if piece.Enhance >= ENH_MAX:      return Rejected(EnhanceMaxed)
  cost = ENH_COST(Stage.Stage)      # = EnhCostGoldMult * Gold(Stage.Stage)
  if Currencies.Gold < cost:            return Rejected(NotEnoughGold)
  Currencies.Gold -= cost; Lifetime.EnhanceAttempts += 1
  success = enhance.NextDouble() < ENH_SUCCESS(piece.Enhance)    # = max(floor, 1 - step * level)
  if success: Gear.Slots[slot] = piece with { Enhance = piece.Enhance + 1 }; Lifetime.EnhanceSuccesses += 1; MarkDpsDirty()
  publish EnhanceAttempted(slot, success, newLevel, cost)
  return Ok(success, newLevel, cost)
```

Enhancement is allowed on tier-0 pieces (it does nothing useful; the UI may warn, the sim does not care).

### 8.4 DPS

```
StatMult  = 1 + STAT_PER_LVL * Stat.Level
GearMult  = prod over slots (piece == null ? 1 : 1 + BasePct(piece) * (1 + ENH_PER_LVL * piece.Enhance))
HeroMult = 1 + sum over Equipped (HeroPct(hero)) / 100
ClassMult = ClassMult(Class.Advances)
Dps       = BASE_ATK * StatMult * GearMult * HeroMult * ClassMult
HitDamage = Dps * ATTACK_INTERVAL_TICKS / TICK_HZ
```

`Dps` and `HitDamage` are cached in `Simulation` and recomputed lazily after any mutation that calls `MarkDpsDirty()` (stat buy, gear equip, enhancement success, hero equip or copy gain on an equipped hero, class advance). `Tick()` reads the cache; a stat bought mid-fight applies to the next hit.

## 9. Heroes and gacha

The pull algorithm, pity, the 10-pull guarantee, the dupe economy and the test vectors are specified in `specs/01` sections 3, 4, 7, 8 and 9 and are not restated. This section fixes how they attach to the sim.

```csharp
namespace HeroCat.Sim.Gacha
{
    public readonly record struct PullResult(HeroId Hero, Rarity Tier, bool IsNew, int LevelBefore, int LevelAfter, int ShardsGained);

    /// Server seam (specs/00 section 5). P0 ships LocalGachaService, which runs specs/01 section 8 on the gacha_hero stream.
    /// The remote contract (specs/08) returns the same PullResult[]; the sim applies results the same way in both cases.
    public interface IGachaService
    {
        PullResult[] Pull(BannerDef banner, BannerState bannerState, HeroState heroes, RngStream gachaHero, int count);  // count is 1 or 10
    }
}
```

- `Pull()` (section 13) uses a free pull when `Daily.FreePullsUsed < FREE_PULLS_PER_DAY`, otherwise charges `PULL_COST_GEMS`. `Pull10()` always charges `10 * PULL_COST_GEMS`; free pulls are never spent on a 10-pull in P0.
- Copies and shards are written into `HeroState` and `Currencies.Shards` by the service; the sim publishes one `HeroPulled` per result.
- `HeroLevel(copies)` and `HeroPct(hero)` follow `specs/01` section 7 and are exposed on `SimQuery`.
- `EquipHero(slot, id)` requires the hero to be owned and not already equipped in another slot; `UnequipHero(slot)` clears; `AutoEquipBestHeroes()` fills `Equipped` with the `HERO_SLOTS` highest `HeroPct` owned heroes (ties broken by `HeroId.Value` ordinal) and returns how many slots changed. All three mark DPS dirty.
- `Rarity` is the enum used for hero tiers; `specs/01`'s sketches write `Tier.M`, which maps to `Rarity.M` here. "Tier" as an `int` keeps the `specs/00` meaning (`GEAR_TIER(s)`).

## 10. Class advances

`Class.Advances` increments in `AdvanceStage` (section 7.4). `ClassMult(k)` comes from the geometric table. Class advances never decrease in P0 (P2 prestige would reset them with the stage).

## 11. Wall clock, catch-up and daily rollover

`Tick()` never reads a clock. Wall time enters the sim at one seam:

```csharp
namespace HeroCat.Sim.Time
{
    public interface IClock { DateTimeOffset UtcNow { get; } }
    public sealed class SystemClock : IClock { ... }                                   // client default until specs/08 supplies a server-synced clock
    public sealed class FakeClock : IClock { public DateTimeOffset UtcNow { get; set; } public void Advance(TimeSpan by); public void Set(DateTimeOffset to); }
}
```

The caller (client or harness) reads `IClock.UtcNow` and passes it in:

```
SyncWallClock(nowUtc):
  nowMs        = nowUtc.ToUnixTimeMilliseconds()
  simulatedMs  = (State.Tick - Clock.TickAtLastSync) * (1000 / TICK_HZ)
  gapMs        = nowMs - (Clock.LastSyncUtcMs + simulatedMs)
  if gapMs >= OFFLINE_MIN_GAP_S * 1000:  CatchUp(gapMs)
  else if gapMs < -1000:                 publish ClockWentBackwards(by: -gapMs)      # no state change; specs/08 decides what the client does
  Clock.LastSyncUtcMs = nowMs; Clock.TickAtLastSync = State.Tick
  dayKey = floor(nowMs / 86_400_000)
  if dayKey != Daily.DayKey: Daily.DayKey = dayKey; Daily.FreePullsUsed = 0; publish DayRolledOver(dayKey)

CatchUp(gapMs):
  creditedS   = min(gapMs / 1000, OFFLINE_CAP_H * 3600)
  killTimeS   = Query.KillTimeSeconds(Stage.Stage)                 # normal mob at the current stage, discrete formula
  gold        = creditedS * OFFLINE_RATE * Gold(Stage.Stage) / killTimeS
  Currencies.Gold += gold; Lifetime.OfflineGoldEarned += gold
  publish OfflineGoldCredited(elapsed: gapMs, credited: creditedS, gold)
```

Catch-up is closed-form (`specs/02` section 7): no ticks, no RNG draws, no stage push, no drops. The `specs/00` section 5 phrasing "offline catch-up calls `Advance`" is satisfied in spirit: catch-up lives in this assembly and uses the same `Query` functions, but it does not tick. `OFFLINE_MIN_GAP_S` (proposed 60 s) and the question of when the client calls `SyncWallClock` (app resume, every 60 s while open) belong to `specs/06` and `specs/07`; the mechanism is fixed here.

Daily rollover is a UTC day boundary in P0 (`DayKey` = days since epoch). Server-local reset hours are a `specs/08` concern and would change only `dayKey`'s computation.

## 12. Events

Events are what happened; state is what is. The client reads state for numbers and subscribes to events for feedback (hit flashes, drop toasts, "WALL" banners). Bots use events to measure walls.

```csharp
namespace HeroCat.Sim.Events
{
    public abstract record SimEvent(long Tick);

    public sealed record EnemySpawned(long Tick, int Stage, bool IsBoss, double Hp) : SimEvent(Tick);
    public sealed record HitLanded(long Tick, double Damage, double RemainingHp) : SimEvent(Tick);
    public sealed record EnemyKilled(long Tick, int Stage, bool IsBoss, double Gold) : SimEvent(Tick);
    public sealed record StageAdvanced(long Tick, int FromStage, int ToStage, bool ViaBoss) : SimEvent(Tick);
    public sealed record BossFailed(long Tick, int Stage, int FailsOnStage) : SimEvent(Tick);
    public sealed record BossCleared(long Tick, int Stage) : SimEvent(Tick);
    public sealed record GearDropped(long Tick, int Slot, GearPiece Piece, bool Equipped, GearPiece? Replaced) : SimEvent(Tick);
    public sealed record StatLeveled(long Tick, int NewLevel, double Cost) : SimEvent(Tick);
    public sealed record EnhanceAttempted(long Tick, int Slot, bool Success, int NewLevel, double Cost) : SimEvent(Tick);
    public sealed record HeroPulled(long Tick, PullResult Result, bool Free) : SimEvent(Tick);
    public sealed record HeroEquipped(long Tick, int Slot, HeroId? Hero) : SimEvent(Tick);      // Hero null = unequipped
    public sealed record ClassAdvanced(long Tick, int Advances, int Stage) : SimEvent(Tick);
    public sealed record OfflineGoldCredited(long Tick, long ElapsedMs, double CreditedSeconds, double Gold) : SimEvent(Tick);
    public sealed record DayRolledOver(long Tick, int DayKey) : SimEvent(Tick);
    public sealed record ClockWentBackwards(long Tick, long ByMs) : SimEvent(Tick);
    public sealed record StageCapReached(long Tick, int Stage) : SimEvent(Tick);

    [Flags]
    public enum EventKinds : uint
    {
        None = 0,
        EnemySpawned = 1 << 0, HitLanded = 1 << 1, EnemyKilled = 1 << 2, StageAdvanced = 1 << 3, BossFailed = 1 << 4,
        BossCleared = 1 << 5, GearDropped = 1 << 6, StatLeveled = 1 << 7, EnhanceAttempted = 1 << 8, HeroPulled = 1 << 9,
        HeroEquipped = 1 << 10, ClassAdvanced = 1 << 11, OfflineGoldCredited = 1 << 12, DayRolledOver = 1 << 13,
        ClockWentBackwards = 1 << 14, StageCapReached = 1 << 15,
        All = 0xFFFF,
        Default = All & ~(HitLanded | EnemySpawned),     // the two per-enemy events are opt-in
    }

    public interface IEventSink
    {
        EventKinds Mask { get; }           // the sim checks the mask BEFORE allocating an event
        void Publish(SimEvent e);
    }

    /// Client outbox: the sim pushes, the client drains once per frame after ticking.
    public sealed class EventOutbox : IEventSink
    {
        public EventOutbox(EventKinds mask = EventKinds.Default, int capacity = 65_536);
        public int Count { get; }
        public long Dropped { get; }        // oldest events are dropped when Count would exceed capacity
        public int Drain(List<SimEvent> into);   // appends, clears, returns count drained
    }

    public sealed class NullEventSink : IEventSink { Mask = EventKinds.None; }
}
```

Publishing is always guarded: `if ((_sink.Mask & EventKinds.EnemyKilled) != 0) _sink.Publish(new EnemyKilled(...));`. This is the only place the hot path allocates, and a bot with `NullEventSink` allocates nothing per tick. Events are stamped with `State.Tick` at publication; events raised by commands carry the tick of the last `Tick()` call.

P1: a replay log sink that records commands and tick counts for bug reports; `SimEvent` records are already serialisable shapes.

## 13. Command API

Commands are methods on `Simulation`. They validate, mutate, publish and return. They never throw for a rule violation; they throw `ArgumentOutOfRangeException` for malformed input (slot index outside `0..GEAR_SLOTS-1`, unknown `HeroId`), because that is a client bug, not a player action. Commands must not be called from inside an event handler during `Tick()`; the client drains the outbox after ticking and acts then.

```csharp
public enum Reject
{
    None = 0, NotEnoughGold, NotEnoughGems, EmptySlot, EnhanceMaxed, HeroNotOwned, HeroAlreadyEquipped,
    NoBossToAttempt, BossAlreadyPending, StageCap,
}

public readonly record struct CommandResult(Reject Reason) { public bool Ok => Reason == Reject.None; }
public readonly record struct EnhanceResult(Reject Reason, bool Success, int Level, double Cost) { public bool Ok => Reason == Reject.None; }
public readonly record struct PullOutcome(Reject Reason, PullResult[] Results, int FreePullsUsed, int GemsSpent) { public bool Ok => Reason == Reject.None; }

public sealed partial class Simulation
{
    public CommandResult BuyStatLevel();
    public int           BuyStatLevelsMax();
    public EnhanceResult Enhance(int slot);
    public PullOutcome   Pull();                 // free pull if one remains today, else PULL_COST_GEMS
    public PullOutcome   Pull10();               // 10 * PULL_COST_GEMS, with the specs/01 section 4 guarantee
    public CommandResult EquipHero(int slot, HeroId hero);
    public CommandResult UnequipHero(int slot);
    public int           AutoEquipBestHeroes();  // returns slots changed
    public CommandResult AttemptBoss();
    public DebugCommands Debug { get; }
}

/// Always compiled (bots and tests need it); the client shows it only behind a developer flag (specs/07).
public sealed class DebugCommands
{
    public void GrantGold(double amount);
    public void GrantGems(int amount);
    public void GrantHeroCopies(HeroId hero, int copies);
    public void SetStage(int stage);     // resets KillsInStage, Boss, combat to Spawning; for tests and the client's stage jump
}
```

`AttemptBoss()`:

```
  s = Stage.Stage
  if Stage.Boss == Pending:                         return Rejected(BossAlreadyPending)
  if s % BOSS_EVERY != 0 or Stage.Boss != Walled:   return Rejected(NoBossToAttempt)
  Stage.Boss = Pending
  Combat.Phase = Spawning; Combat.SpawnTimer = SPAWN_DELAY_TICKS     # the current normal mob despawns: no gold, no drop, no kill credit
  return Ok
```

The first attempt on a boss stage is automatic (the 10th enemy is the boss). `AttemptBoss` is only for retries after a fail. Despawning the current mob costs the player at most one partial kill and makes the button feel immediate.

## 14. Query API

Read-only derived values. Everything here is computable by the client and bots from `State` and `Content`; it lives on the sim so that there is exactly one implementation of each formula.

```csharp
public sealed class SimQuery
{
    public double Dps { get; }                       // cached, section 8.4
    public double HitDamage { get; }
    public double StatMult { get; }
    public double GearMult { get; }
    public double HeroMult { get; }
    public double ClassMult { get; }

    public double Hp(int stage);                     // normal mob
    public double BossHp(int stage);
    public double Gold(int stage);
    public int    HitsToKill(double hp);             // max(1, ceil(hp / HitDamage)); advisory, see section 4
    public double KillTimeSeconds(int stage);        // (HitsToKill(Hp(stage)) * ATTACK_INTERVAL_TICKS + SPAWN_DELAY_TICKS) / TICK_HZ
    public bool   BossClearable { get; }             // HitsToKill(BossHp(Stage.Stage)) * ATTACK_INTERVAL_TICKS <= BOSS_TIME_TICKS
    public bool   IsBossStage { get; }               // Stage.Stage % BOSS_EVERY == 0

    public double StatCost(int level);
    public double EnhanceCost { get; }                // ENH_COST(Stage.Stage)
    public double EnhanceSuccess(int level);          // ENH_SUCCESS(level)
    public double GearBasePct(GearPiece piece);
    public double GearSlotMult(int slot);

    public int    HeroLevel(int copies);
    public double HeroPct(HeroId hero);            // 0 if not owned
    public int    FreePullsRemaining { get; }
    public double PityRate(int n);                    // specs/01 section 3.1
}
```

## 15. Invariants and validation

`Simulation.Validate()` checks every invariant below and throws `InvariantViolationException` naming the first failure. Tests call it after every scenario; the fuzz test (section 19) calls it after every command. Release builds of the client do not call it per tick (cost), but call it on load and on save.

Currencies and counters:
1. `Currencies.Gold >= 0`, `Currencies.Gems >= 0`, `Currencies.Shards >= 0`, `Stat.Level >= 0`.
2. `Daily.FreePullsUsed` in `0..FREE_PULLS_PER_DAY`.
3. `0 <= HeroBanner.PityCounter < PITY_HARD`.

Stage and combat:
4. `1 <= Stage.Stage <= STAGE_CAP`; `0 <= KillsInStage < KILLS_PER_STAGE`.
5. `Stage.Boss != None` implies `Stage.Stage % BOSS_EVERY == 0` and `KillsInStage == KILLS_PER_STAGE - 1`.
6. `Boss == None` implies `BossFailsOnStage == 0`; `Boss == Walled` implies `BossFailsOnStage >= 1`.
7. `Combat.Phase == Spawning` implies `0 < SpawnTimer <= SPAWN_DELAY_TICKS`.
8. `Combat.Phase == Fighting` implies `EnemyHp > 0`, `EnemyHp <= EnemyMaxHp`, `0 < AttackTimer <= ATTACK_INTERVAL_TICKS`.
9. `EnemyIsBoss` implies `Phase == Fighting`, `Boss == Pending`, `0 < BossTimer <= BOSS_TIME_TICKS`, `EnemyMaxHp == BossHp(Stage)`.
10. `Class.Advances == count of CLASS_STAGES <= Stage.Stage`.

Inventories:
11. `Gear.Slots.Length == GEAR_SLOTS`; each piece has `0 <= Enhance <= ENH_MAX`, `Tier % BOSS_EVERY == 0`, `0 <= Tier <= GEAR_TIER(Stage.Stage)`.
12. `Heroes.Equipped.Length == HERO_SLOTS`; non-null entries are distinct and present in `Copies` with `Copies > 0`.
13. Every `HeroId` in `Copies` exists in the banner pool.

RNG (section 5.5):
14. `Streams` contains exactly `StreamNames.All`; each `Seed == StreamSeed(ProfileSeed, Name)` unless flagged as server-issued (P1 field).
15. `combat.Counter == 0`; `gear_drop.Counter == Lifetime.Kills`; `gear_rarity.Counter == 2 * Lifetime.GearDrops`; `enhance.Counter == Lifetime.EnhanceAttempts`; `gacha_hero.Counter == 3 * HeroBanner.TotalPulls`.

Derived:
16. `Query.Dps` is finite and `> 0`; the cached value equals a fresh recomputation.
17. `Lifetime.FirstWallStage == 0` iff `Lifetime.BossFails == 0`.

Clock:
18. `Clock.TickAtLastSync <= State.Tick`.

## 16. Threading model and client integration

The sim is single-threaded and not thread-safe. One `Simulation` instance is owned by one thread for its lifetime; the client owns it on the main thread. Bots run one instance per seed on one thread each; instances share nothing (section 2 forbids static mutable state, and `IContentDb` is immutable after load so it may be shared read-only).

Recommended client loop (`specs/07` owns the final version):

```csharp
void Update()
{
    _acc += Time.unscaledDeltaTime;
    int ticks = (int)(_acc * TICK_HZ);
    if (ticks > MAX_TICKS_PER_FRAME) ticks = MAX_TICKS_PER_FRAME;      // e.g. 50 = 5 s; a longer backlog is handled by SyncWallClock as catch-up
    _acc -= ticks / (double)TICK_HZ;
    _sim.Advance(ticks);
    _hud.Refresh(_sim.State, _sim.Query);                                // numbers come from state, not from events
    _outbox.Drain(_frameEvents); foreach (var e in _frameEvents) Dispatch(e); _frameEvents.Clear();
}
void OnApplicationFocus(bool focused) { if (focused) _sim.SyncWallClock(_clock.UtcNow); }
```

Commands are issued from UI callbacks on the same thread, between `Advance` calls. `SyncWallClock` is called on resume and on a 60 s timer while the app is open.

## 17. Performance budget

| Scenario | Budget | How |
|---|---|---|
| Client, 10 Hz on a low-end Android phone (IL2CPP, ARMv7/ARM64) | one `Tick()` under 5 µs, zero allocations when no event is masked in | timers are `int` decrements; the hot path touches `CombatState` and the cached `HitDamage` only |
| Bots, `Advance(1_000_000)` with `NullEventSink` | under 1 s in Release on a developer laptop (x64 or Apple ARM64) | 1 µs per tick budget; the per-tick work is a branch and a decrement, a kill costs one or three hash derives plus table lookups |
| `BuyStatLevelsMax()` | O(levels bought) | each iteration is a table lookup and a subtraction |
| `SyncWallClock` / `CatchUp` | O(1) | closed form |
| `Validate()` | O(slots + heroes) | not on the hot path |

Rules that keep the budget: no LINQ in `Tick()` or `Kill()`; no `string` operations; no dictionary lookups per tick (the five `RngStream` objects are held in fields after construction); `Dps` is recomputed only when dirty; `GeometricTable` grows by amortised append and is never rebuilt. The perf test in section 19 enforces the bot budget with a 2x margin so it does not flake on CI.

## 18. Save and restore contract

`GameState` is the complete save. `specs/06` serialises it; this section fixes what "complete" means:

1. After `var a = new Simulation(c, state)` and `var b = new Simulation(c, state.DeepClone())`, any identical sequence of `Tick`/`Advance`/commands/`SyncWallClock` calls on both produces identical `GameState` graphs and identical event sequences.
2. The clone may be taken at any point, including mid-fight (`Phase == Fighting`, boss timer running) and mid-wall.
3. `Simulation` caches (`Dps`, `HitDamage`, stream field references) are rebuilt in the constructor from `GameState`; nothing else is held.
4. `ulong` seeds and counters must round-trip exactly; `specs/06` must pick a serialiser that does (Unity `JsonUtility` does not handle `ulong` reliably; `System.Text.Json` in the bots does).
5. `GearPiece.BasePct` and all multipliers are derived and not saved, so a content change rebalances existing saves automatically.
6. Unknown stream names on load are created with `Counter = 0` (forward compatibility for P1 streams); unknown `HeroId`s are a `specs/06` migration concern.

## 19. Unit tests (`HeroCat.Sim.Tests`)

Fixtures build an `IContentDb` from the canonical table (via the `specs/05` loader on the checked-in content file) and a `FakeClock` at `2026-01-01T00:00:00Z`. Tests that need a specific RNG outcome use a `StubRngStream` (internal) that returns a scripted list of doubles; tests that need statistics use the real generator with a fixed seed and are therefore deterministic.

RNG (`RngTests.cs`):
1. `Derive` and `Fnv1a64` match every vector in section 5.3.
2. `ToUnit` is in `[0, 1)` for 1,000,000 draws and has mean within 0.002 of 0.5.
3. Two streams with different names and the same profile seed produce different first 100 draws; the same name and seed produce identical draws.
4. `NextIndex(n)` over 1,000,000 draws returns every value in `0..n-1` for n in {3, 6, 20} and nothing else.
5. Counter after `k` draws is `k`; `PeekDouble(j)` equals the j-th `NextDouble()` result.

Content tables (`PowerTableTests.cs`):
6. `Hp(s)`, `Gold(s)`, `StatCost(n)`, `GearSlotPct(t)` agree with `Math.Pow` within relative 1e-9 for s, n, t up to 1000 (sanity, not equality).
7. Tables grow on demand: `Hp(2500)` works and is finite.

Combat (`CombatTests.cs`):
8. From a fresh profile, the first `EnemySpawned` is at tick `SPAWN_DELAY_TICKS` and the first `HitLanded` at `SPAWN_DELAY_TICKS + ATTACK_INTERVAL_TICKS`.
9. With `HitDamage` set (via `GrantGold` + stat levels or a test hook) so that `Hp(1)` needs exactly 3 hits, each kill occupies `3 * ATTACK_INTERVAL_TICKS + SPAWN_DELAY_TICKS` ticks.
10. Ten normal kills on stage 1 raise `Stage` to 2 and publish one `StageAdvanced(1, 2, false)`.
11. Each normal kill adds `Gold(s)` to the wallet; a boss kill adds `BOSS_GOLD_MULT * Gold(s)`.
12. On stage 10 the 10th enemy spawns as a boss with `Hp == BOSS_HP_MULT * Hp(10)` and `BossTimer == BOSS_TIME_TICKS`.
13. A boss that needs exactly `BOSS_TIME_TICKS / ATTACK_INTERVAL_TICKS` hits is cleared; one that needs one more hit fails at tick `BOSS_TIME_TICKS` after spawn with `BossFailed(10, 1)`.
14. After a fail, `Boss == Walled`, normal mobs of stage 10 spawn, kills pay `Gold(10)` and `KillsInStage` stays at `KILLS_PER_STAGE - 1`.
15. `AttemptBoss` while walled sets `Pending`, despawns the current mob without gold, and the next spawn is the boss; `AttemptBoss` on a normal stage and while `Pending` is rejected with the right reason.
16. Clearing a walled boss publishes `BossCleared`, `StageAdvanced(10, 11, true)` and resets `BossFailsOnStage`.
17. Reaching each stage in `CLASS_STAGES` increments `Class.Advances` exactly once and multiplies `Dps` by `CLASS_MULT`.
18. `Query.Dps` equals a hand-computed value for a fixture with stat level 10, two gear pieces, three equipped heroes and one class advance (compare within 1e-12 relative).
19. A stat level bought mid-fight changes the damage of the next hit, not the previous one.
20. `Lifetime.FirstWallStage` is set by the first `BossFailed` and never changes afterwards.

Gear (`GearTests.cs`):
21. Over 1,000,000 normal kills with the real generator (seed 7), the drop count is within 3 standard deviations of `GEAR_DROP_P * kills`; boss kills within the same of `BOSS_GEAR_DROP_P`.
22. Rarity frequencies over 100,000 scripted drops match `RarityWeights` within 3 standard deviations; boss drops are never below `BOSS_GEAR_MIN_RARITY`.
23. A drop on stage 37 has `Tier == 30`; stages 1-9 give `Tier == 0`.
24. Equip rule: a higher-`BasePct` drop replaces and resets `Enhance` to 0 and reports `Replaced`; an equal or lower one leaves the slot and reports `Equipped == false`.
25. `GearBasePct` equals `GearSlotPct(tier) * RarityMult[rarity]` and `GearMult` is the product over slots including enhancement.
26. `Enhance`: rejected on empty slot, at `ENH_MAX`, and with insufficient gold (no gold deducted on rejection); success and failure both deduct `ENH_COST(Stage)`; scripted `u` just below and just above `ENH_SUCCESS(l)` succeed and fail respectively; `ENH_SUCCESS(15) == floor`.
27. Stream counters after a mixed scenario satisfy section 5.5 exactly.

Gacha (`GachaTests.cs`): the ten tests in `specs/01` section 9, plus:
28. `Pull()` consumes free pulls first and charges gems only when `FreePullsRemaining == 0`; `Pull10()` never consumes a free pull and is rejected below `10 * PULL_COST_GEMS` with nothing changed.
29. `AutoEquipBestHeroes` picks the top `HERO_SLOTS` by `HeroPct` with ordinal tie-break and marks DPS dirty.
30. `EquipHero` rejects unowned and already-equipped heroes; `UnequipHero` publishes `HeroEquipped(slot, null)`.

Clock (`ClockTests.cs`):
31. `SyncWallClock` after `Advance(600)` with the clock advanced 60 s credits nothing (gap 0).
32. A 3 h gap credits `3 * 3600 * OFFLINE_RATE * Gold(s) / KillTimeSeconds(s)`; a 30 h gap credits the `OFFLINE_CAP_H` amount; a gap below `OFFLINE_MIN_GAP_S` credits nothing.
33. Catch-up consumes no RNG draws and changes no stage, kill count or combat state.
34. Crossing a UTC midnight resets `FreePullsUsed` and publishes `DayRolledOver`; two syncs in the same day do not.
35. A clock that goes backwards publishes `ClockWentBackwards` and changes nothing else.

Determinism and save (`DeterminismTests.cs`):
36. Two fresh profiles with seed 7, the same scripted command schedule and `Advance(1_000_000)` produce `GameState` graphs that are equal field by field and identical event sequences.
37. `DeepClone` taken mid-fight, then separately mid-wall, then separately between pull 5 and pull 6 of a scripted pull sequence; `Advance(100_000)` (and the remaining pulls) on both sides gives equal graphs.
38. Running the same scenario on two threads concurrently gives the same result as sequentially.
39. Validate: a fuzz run of 200,000 random commands and tick batches (seeded `RngStream` chooses the command) never throws from `Validate()`.

Performance (`PerfTests.cs`, `[Category("Perf")]`, skipped in Debug configuration):
40. `Advance(1_000_000)` with `NullEventSink` from a profile at stage 300 completes in under 2 s (2x margin on the 1 s budget).
41. `Advance(100_000)` with `EventOutbox(EventKinds.Default)` allocates fewer than 64 bytes per kill on average (measured with `GC.GetAllocatedBytesForCurrentThread`).

## 20. P1 and P2 hooks

| Item | Tag | Where the P0 shape leaves room |
|---|---|---|
| Crit, attack speed, gold-find substats | P1 | `combat` stream exists with counter 0; `GearPiece` is a record that can gain a `Substats` member; `Dps` is a single cached function |
| Gear bag, manual equip, enhancement transfer | P1 | `GearDropped.Equipped/Replaced`; equip rule is one function |
| Auto boss retry every 60 s | P1 | a content constant and a timer in `StageState` |
| Shard shop | P1 | `Currencies.Shards` is already tracked |
| Pet banner, rate-up banners | P1 | `BannerDef`/`BannerState` are per banner; add a stream name per banner |
| Daily quests, idle chest gem income | P1 (`specs/04`) | `Debug.GrantGems` is the harness stand-in until then |
| Command log and `StateHash()` for server verification and bug reports | P0 shape, P1 service | section 22; `specs/10` sections 4 and 6 |
| Server-issued `gacha_hero` seed | P1 (`specs/08`) | per-stream `Seed` is persisted individually |
| Prestige / rebirth | P2 | would reset `Stage` and `Stat`; `Class.Advances` and a `MaxStage` field would need adding |
| Mantissa/exponent number type | P2 | only if `STAGE_CAP` is ever approached |

## 21. Open decisions

1. `BOSS_GEAR_DROP_P` (0.10) and `BOSS_GEAR_MIN_RARITY` (Rare) are described in `specs/00` section 2.3 prose but not named, and `STAGE_CAP` (proposed 9000, from `specs/02` section 2) is not in `specs/00` at all. This spec names all three; `specs/00` should adopt the names or rename them, and `specs/05` must carry them.
2. `OFFLINE_MIN_GAP_S` is proposed at 60 s and is not in `specs/00`. `specs/06` should own the value; it must exist in content.
3. The C/R/E/L/M enum is `Rarity` for both gear and heroes here; `specs/01` sketches say `Tier`. One name should win in `specs/05`'s content schema.
4. The P0 equip rule compares `BasePct` and discards enhancement, matching the Python model. If playtests find auto-replacing a +15 piece painful before the P1 gear bag exists, the alternative is to compare effective multiplier instead; that would move the harness numbers slightly.
5. The first boss attempt is automatic and `AttemptBoss` despawns the current mob. Both are choices, not derivations from `specs/00`; the client spec may prefer a confirm step.
6. Target framework: `netstandard2.1` with C# 9 is the safe Unity 2022 LTS choice. If the client moves to Unity 6 (.NET Standard 2.1 still, but C# 9 remains the ceiling), nothing changes; if the bots want C# 12 features, multi-target `netstandard2.1;net8.0`.
7. Whether `SyncWallClock`'s UTC-day rollover becomes a server-local reset hour (`specs/08`).
8. Starter grants (`specs/01` section 5: 1000 gems, 10 free pulls) are applied by `specs/04`, not by `NewProfile`; the harness does not apply them because the Python model does not. Confirm with `specs/04` and `specs/09`.

## 22. Competitive-integrity requirements

`specs/10` lets a server certify a client's progress by replaying its commands on this assembly and comparing a state hash. That needs four things here, all cheap now and costly to retrofit.

1. **Command log.** Every player action in section 13 (`BuyStatLevel*`, `Enhance`, `EquipHero`, `UnequipHero`, `AutoEquipBestHeroes`, `AttemptBoss`, `Pull`, `Pull10`, the chest claim, `SyncWallClock`, `Debug.*`) appends a `CommandRecord(Tick, Kind, A, B, LedgerId)` to an `ICommandLog` set on `Simulation`. `Tick()` is not logged; ticks are the gaps between records. The default is a no-op log so the bot's hot path is unchanged.
2. **`StateHash()`.** SHA-256 over a canonical encoding of `GameState`: fixed field order, maps sorted by key, `double` as raw IEEE bits, no serialiser involved. Defined once here; the server reuses it.
3. **`Replay(log, signedTimes)`.** Applies a log to a state: `Advance` between records, the command at its tick, `SyncWallClock` with the supplied times. The bot harness uses it for the replay-equivalence check (`specs/09` AC7).
4. **No new nondeterminism.** Anything iterated by the sim has a defined order (the `Copies` and `Banners` maps are iterated in key order). Streams still never read the clock.

Debug commands are logged so tests can replay a debug-assisted run; certification rejects any segment that contains one (`specs/10` section 4.3). In server-issued-seed mode (`specs/10` section 3) the profile's stream seeds are inputs to genesis, not values the client chooses.
