# 10 - Online, Competitive and Paid Play: Trust Model

Scope: what must be true in P0 so that accounts, IAP, leaderboards and asynchronous PvP can be added in P1 without migrating the engine or invalidating saves, and so that those features are fair. This spec fixes the trust model and the shapes. It does not design the PvP battle rules (section 9 is a sketch) or pick the backend vendor (`specs/08` section 10).

Tags: **P0** = shapes that must exist in the first build (all cheap, none needs a server). **P1** = server features built on those shapes. **P2** = real-time PvP, guilds, anything needing live sessions.

This spec supersedes the sentence in `specs/08` section 11 ("What P2 changes") and the P2 tag on leaderboards and PvP in `specs/00` section 1.

## 1. Goals and non-goals

Goals:

1. **No competitive surface reads a value the client merely claims.** Leaderboards, PvP builds and paid balances come from server-held or server-verified state.
2. **The sim stays on the device.** Combat ticks 10 times a second and must work offline (`specs/08` section 11). The server verifies; it does not play.
3. **One engine, many hosts.** The same C# `HeroCat.Sim` runs in the Unity client, the browser (WebAssembly), the bot harness and the server. A fairness rule that needs a second implementation of a game rule is a bug in this spec.
4. **Single-player stays offline-first.** A player with no connection plays, saves, earns gold and offline progress exactly as in P0. Only ranked surfaces, pulls and purchases need the network.
5. **An honest player is never punished for a bug.** A verification mismatch freezes ranked progress and reports; it does not ban (section 4.6).

Non-goals: stopping auto-clickers and bots that play legal moves (they are playing; detection is behavioural and later), real-time PvP, anti-collusion between friends, payment fraud beyond store receipts, protection from a compromised server.

## 2. The trust model: authoritative versus verified

Two ways for the server to trust a value:

- **Authoritative**: the server holds the value and the client displays it. Used where the client's input to the value is a request, not a result (gems, pulls, purchases, time).
- **Verified**: the client computes the value and the server re-derives it from a log before accepting it. Used where the value comes out of ticking and a server cannot tick for every player (stage, gold, gear).

Every field in `GameState` falls into one row:

| Domain | Fields | Trust | How |
|---|---|---|---|
| Time | server clock, day key, chest timer reads | Authoritative | `IClock` / `ServerClock` (`specs/08` section 3). Signed timestamps, section 4.3. |
| Gacha | `gacha_hero` seed and counter, pity, results, hero copies, shards | Authoritative | `IGachaService` remote (`specs/08` section 4). |
| Premium ledger | gems, free-pull credits, purchase grants | Authoritative | Server ledger; receipts (`specs/08` section 6); section 8. |
| Identity | account id, profile genesis, seeds | Authoritative | Section 3. |
| Progress | stage, kills, class advances, first clears | Verified | Segment certification, section 4. |
| Gold economy | gold, stat levels, enhancement, gear, equips | Verified | Same segments. Gear and enhancement streams are replayed from server-issued seeds. |
| Cosmetic and UI | tab, toggles, analytics flags | None | Client only. |

"Verified" is a property of the *last certified checkpoint*, not of live client memory. Ranked surfaces read the checkpoint (section 4.7).

Why not make gold and gear authoritative too: they change every tick. A server round trip per kill is a 10 Hz service for every player and breaks offline play. Verification moves that cost to once per segment, in bulk, and a segment is cheap to replay (section 4.5).

## 3. Provenance: ranked profiles start at a server genesis

Verification replays a segment from a known-good starting state. The chain of known-good states must begin somewhere the server controls, or the first link is a client claim.

1. **Account.** `AccountId` is issued by the server and is separate from `ProfileId`. Anonymous first, linkable to Apple or Google later. P0 keeps `ProfileId` only; `AccountId` is a nullable field in save v1 (section 12).
2. **Genesis.** Creating a ranked profile calls the server once. The server returns the profile's **stream seeds** (all five streams of `specs/00` section 5.2), the `content_version` and a `GenesisHash` over `(accountId, profileId, seeds, contentHash)`. The `gacha_hero` seed is not returned (it stays server-side); the client receives only `Counter = 0` for it.
3. **Why seeds are server-issued.** If the client picks its own seeds, it can grind offline for a profile whose first 1,000 gear drops and enhancement rolls are lucky, then register that profile. The streams are random access (`specs/03` section 5), so grinding is cheap. Server-issued seeds remove the option.
4. **Ranked eligibility** is a profile flag, `RankedLineage`, set only at genesis. A profile created offline in P0 gets local seeds and `RankedLineage = false`. It plays everything except ranked surfaces. It cannot be upgraded later, because its seeds were client-chosen. The player may start a ranked profile alongside it (losing nothing from the unranked one).
5. **Offline first launch.** A ranked profile needs one successful genesis call. A player who launches offline for the first time gets an unranked profile and a prompt to start a ranked one when online. This is the only place ranked play requires a connection before the first tick.

The browser prototype (`web/`, the C# core compiled to WebAssembly) creates local seeds and is permanently unranked until it gains a server genesis. That is correct for a playtest build and must be stated in its README.

## 4. Segment certification (P1 service, P0 shapes)

### 4.1 Flow

```
client                                               server
  play: ticks + commands, recording the command log
  every CERT_INTERVAL_MIN foreground minutes, on pause, and before any ranked action:
  upload ProgressSegment(fromSeq, fromStateHash, commands, signed times, toStateHash) ->
                                                     load certified state at fromSeq (compare-and-swap)
                                                     check content pin, time bound, no debug (4.3)
                                                     replay commands on HeroCat.Sim (same source)
                                                     compare StateHash at the end
                                                     <- Accepted(seq+1, stateHash, flags) or Rejected(code)
  store Cert { seq, stateHash, serverSec }
```

### 4.2 Contract

```csharp
public sealed record SignedTime(long ServerSec, string Signature);          // from ServerClock; the client cannot forge one

public sealed record ProgressSegment(
    string ProfileId, long FromSeq, string FromStateHash,
    long FromTick, long ToTick,
    string ContentHash,
    SignedTime[] Times,              // first and last bracket the segment; others pin SyncWallClock calls
    byte[] Commands,                 // CommandRecord[] (section 6), compact binary
    string ToStateHash);

public sealed record CertResult(bool Accepted, long Seq, string StateHash, long HighestStage, CertFlag[] Flags, CertRejectCode? Reason);

public interface IProgressCertifier
{
    Task<CertResult> CertifyAsync(ProgressSegment seg, CancellationToken ct);   // idempotent per (profileId, fromSeq, toStateHash)
}
```

`Seq` increases by one per accepted segment. Upload is a compare-and-swap on `FromSeq`: if the server is already past it (another device certified first), the result is `Conflict`, and the client resolves it with the cloud-save conflict prompt of `specs/08` section 5.2. This replaces last-writer-wins by wall time for ranked profiles: the winner is the first certified segment, and the loser's uncertified tail is the "other device" candidate.

### 4.3 What the server checks before replaying

1. **Chain.** `FromSeq` and `FromStateHash` equal the server's certified record.
2. **Content pin.** `ContentHash` equals the content version pinned for the profile's current season (section 11). A client on stale content is told to update; it is not certified against newer tables.
3. **Time bound.** Ticks claimed must fit the signed time: `ToTick - FromTick <= (lastTime - firstTime) * TICK_HZ * (1 + CERT_TIME_SLACK) + CERT_TIME_FLOOR_TICKS`, plus the offline gap credited by `SyncWallClock` records. This is the speed-hack bound: a client cannot tick 72,000 times in 60 seconds of server time. Proposed `CERT_TIME_SLACK = 0.02`, `CERT_TIME_FLOOR_TICKS = 600`.
4. **No debug.** A segment containing any `Debug.*` record is rejected and sets `DebugTainted`. Debug commands in a ranked profile are compiled out of release builds (`specs/06` section 13.2); this check is the server-side half.
5. **Ledger agreement.** Pull, purchase and chest records in the log carry server ledger ids. The server checks each against its own ledger and applies its numbers, not the client's (section 8).
6. **Size.** At most `CERT_MAX_SEGMENT_TICKS = 1_000_000` (about 28 h at 10 Hz) and `CERT_MAX_COMMANDS = 20_000` per segment, so one request has a bounded cost.

### 4.4 What replay does

The server builds a `Simulation` from the certified state at `FromSeq`, applies each `CommandRecord` at its tick (`Advance` between them), applies `SyncWallClock` with the signed times, and compares `StateHash()` (section 6) at the end. It has no special rules: the same `Tick()` and commands run. Equality of the hash is the whole proof, and it covers gold, gear, enhancement, stage and every counter at once, so there is no separate list of fields to forget.

### 4.5 Cost

`specs/03` section 17 budgets 1 µs per tick. A day of play is about 72,000 ticks (`specs/09` section 4): tens of milliseconds. A 30-day history, 2.16 million ticks, is about 2 s. Because every segment starts from a certified state, the server never replays history; it replays only what happened since the last certification. A worker pool sized for peak upload bursts is the whole infrastructure, and a rejected or throttled upload loses the player nothing but ranked freshness.

### 4.6 Failure and honesty

| Result | Meaning | Client behaviour |
|---|---|---|
| `Accepted` | Hash matched. | Store `Cert`, continue. |
| `Conflict` | Another device certified first. | Cloud-save conflict prompt. |
| `Rejected: StateMismatch` | Replay ended in a different state. | Ranked frozen at the last certified checkpoint. Single-player continues. Upload a desync report (log slice, both hashes, build id). Offer **Restore last certified state** (the server stores it with the cloud save) or keep playing unranked. |
| `Rejected: TimeBound`, `Debug`, `ContentStale` | Rule violation or stale build. | As named. `ContentStale` prompts an update. |
| `Throttled` / network | Not a verdict. | Retry with backoff. Keep the log; never discard it before acceptance. |

The first `StateMismatch` is never a ban. A mismatch can come from a real determinism bug (section 5). Repeated mismatches on one profile with matching builds and no debug marker are flagged for review; a mismatch that reproduces across many honest profiles on one platform pages the team, because it is a bug in the sim.

### 4.7 What ranked surfaces read

Leaderboards and PvP snapshots read **only** the server's certified record `{ accountId, seq, stateHash, highestStage, certifiedAtServerSec, build snapshot }`, written by `IProgressCertifier` and nothing else. No endpoint accepts a score, stage or power value from a client. The player sees "progress since last sync is not counted yet" with the uncertified delta, which is honest and gives a reason to sync.

## 5. Determinism requirements

Replay verification is only as good as bit-exact agreement between the client and the server. `specs/03` section 4 and `specs/09` section 10 already ban transcendental functions and floats in the sim and run golden files across x64 and ARM64. This spec adds:

1. **Golden command-log runs.** `Fixtures/Replay/*.json`: a genesis (seeds, content hash), a command log, and the expected `StateHash` at each day boundary. Generated by the bot harness. Run in CI on x64 Linux and Apple ARM64, in the .NET server runtime, **and on an IL2CPP device build** (a Unity test that loads the fixture and asserts the hashes). The device run is the one most likely to fail: IL2CPP compiles to C++, and a C++ compiler may fuse multiply-add on some ARM targets, which changes the last bit of a `double`. If it does, fix it in the sim (explicit intermediate stores, or a build flag) before any server verifies on-device results. Treat this as a hypothesis to test, not a conclusion.
2. **No platform-dependent iteration.** Any `Dictionary` the sim iterates must have a defined order (insertion-ordered or sorted by key). `Copies` and `Banners` are maps; the serialiser and `StateHash` sort by key.
3. **WebAssembly host.** The .NET WebAssembly build runs the same fixtures. A browser client of a ranked profile is therefore certifiable by the same rule.
4. **Version skew.** The server keeps the sim source for every released content/build pair it certifies. A client one build behind certifies against its own build's sim until a forced-update floor passes it.

## 6. API additions to `HeroCat.Sim` (P0 shapes)

Small, and all cheap now; expensive to retrofit.

```csharp
public enum CommandKind : byte
{
    BuyStatLevels, Enhance, EquipHero, UnequipHero, AutoEquipBest, AttemptBoss,
    Pull, Pull10, ClaimChest, SyncWallClock, GrantFromLedger, Debug,
}

// A value-type record per player action. The sim appends one when a command runs.
public readonly record struct CommandRecord(long Tick, CommandKind Kind, long A, long B, string? LedgerId);

public interface ICommandLog { void Append(in CommandRecord r); }         // NullCommandLog in the bot's fast path

public sealed partial class Simulation
{
    public ICommandLog CommandLog { get; set; }
    public string StateHash();       // SHA-256 over a canonical encoding of GameState (sorted maps, fixed field order, IEEE doubles as raw bits)
    public void Replay(ReadOnlySpan<CommandRecord> log, SignedTime[] times);   // applies the log; used by the server and by tests
}
```

Rules:
- `Tick()` itself is not logged. Ticks are implied by the gaps between records (`Advance(next.Tick - sim.State.Tick)`).
- `SyncWallClock` is a record; its argument is a signed server time on ranked profiles.
- The canonical encoding is defined once, in `HeroCat.Sim`, and the server reuses it. It is not JSON; it does not depend on a serialiser version.
- `Debug` records exist so tests can replay a debug-assisted run; certification rejects them (4.3).

As built (`src/HeroCat.Sim/Replay`, `Simulation.Replay.cs`):
- `CommandRecord` is a `readonly struct` with the same members (the sim compiles as C# 9, which has no `record struct`). `ListCommandLog` is the in-memory log, bounded by an optional capacity (`CERT_MAX_COMMANDS`); `CommandLogCodec` is the compact binary form.
- `CommandKind` appends `TutorialGrants`, `OfflineCatchUp` and `EnsureDay` after `Debug`, because those public methods also change state. Values are wire-stable: append only.
- Arguments: hero ids are their index in `ContentDb.HeroList` (content is pinned by `ContentHash`); `SyncWallClock` is `A = nowServerSec, B = applyCatchUp`; `AutoEquipBest` has `A = 1` for on, 0 for `SetAutoEquipHeroes(false)`; `Debug` has `A = DebugOp | heroIndex << 8`, `B` = the argument (gold as raw double bits). A command is recorded whether it is accepted or rejected; nested calls inside a command are not recorded.
- `Replay(log, times, toTick)` also advances to an end tick, and `ApplyCommand(record)` runs one record. When `times` is given, each `SyncWallClock` time must be one of them; signatures are checked by the server, not the sim.
- `StateHasher` holds the canonical encoding and a hand-rolled SHA-256 (no platform crypto, for IL2CPP and WebAssembly). It hashes every `GameState` field except `SchemaVersion`, `ContentVersion`, `Cert` and `PendingReceipt` (section 12).

This promotes the "replay log of commands" row in `specs/03` section 20 from a P1 hook to a P0 shape.

## 7. Leaderboards (P1)

- **Key**: `highestStage` from the certified record. Tie-break: earlier `certifiedAtServerSec`. A second board, "clear time to stage N", is possible later because the log has ticks.
- **Writers**: `IProgressCertifier` only (4.7). `ILeaderboardService` has read endpoints and no score submission.
- **Eligibility**: `RankedLineage`, not `DebugTainted`, not flagged. One entry per account.
- **Scope**: all-time and per season (section 11). Friends and region lists are filters on the same data.
- **Staleness**: a player's entry reflects the last certified segment. Show it.

## 8. IAP and the ledger (P1)

Fairness angle only; the validator contract is `specs/08` section 6.

- Gems, free-pull credits and purchase grants live in the server ledger. The save's `Currencies` fields for them are a mirror (`specs/08` section 4.3 already applies this).
- Grants are idempotent on the store `TransactionId`. A refund or chargeback creates a negative ledger entry; the policy for a negative balance (clamp to zero, block pulls, or claw back copies) is an open decision (section 15), because it must be written before launch and not improvised.
- Pulls from purchased gems use the same server roll and the same rates as free ones. The rate screen (`specs/07` section 11.6) is the player-facing disclosure. Some jurisdictions regulate randomised paid items; that needs a legal check before release and is not decided here.
- Spending gems on a PvP-relevant advantage is a product decision, not a technical one: see PvP fairness levers (9.4).
- `PendingReceipt` is in save v1 (section 12), so a purchase interrupted by a crash resolves at the next launch like a pending pull.

The web client cannot use app-store billing. If a web build ever sells gems it needs a separate payment provider and a store-policy review; the ledger does not care which source credited it.

## 9. Asynchronous PvP (P1 design sketch)

Not a rules spec. It fixes the architecture so P0 does not block it.

### 9.1 Shape

Asynchronous means neither player is online during the battle. A player attacks a stored snapshot of another player's build; the defender's snapshot defends without input. The result is computed once, on the server.

### 9.2 The battle function

```csharp
public sealed record BuildSnapshot(
    string AccountId, long CertSeq, string StateHash, string ContentHash,
    int Stage, int StatLevel, int ClassAdvances,
    GearPieceSnapshot?[] Gear, HeroSnapshot?[] Party);          // only what battle needs; derived from a certified state

public sealed record BattleResult(bool AttackerWon, int Rounds, BattleEvent[] Events, string ResultHash);

public static class BattleSim
{
    // Pure and deterministic. No Simulation, no clock, no IO, no Math.Pow. Own RNG stream, derived from battleSeed.
    public static BattleResult Resolve(BuildSnapshot attacker, BuildSnapshot defender, ulong battleSeed, BattleRules rules);
}
```

- Lives in the pure assembly beside `HeroCat.Sim` so the client, the server, the bots and the browser share it.
- **The server resolves; the client animates.** The client may run `Resolve` with the same inputs to play the fight, and must display the server's result. If they differ it reports a desync (a bug), and the server's result stands.
- `battleSeed = Derive(serverSecret, matchId)`, issued per match. The client cannot choose or peek at it before the match is created. PvP uses its own stream, never the five game streams (`specs/00` section 5.2), so adding PvP changes no PvE counter.
- `BuildSnapshot` is built **by the server** from the certified state, never uploaded by the client.

### 9.3 Matchmaking, rating and rewards (server only)

- Opponent selection, rating (Elo or Glicko) and season state are server code. The client asks for candidates and picks among those offered; it cannot name an opponent.
- Defense snapshots are frozen at match creation, so a defender cannot change a build mid-match.
- Rewards are ledger entries (gems, shards, season points), applied by the server after the result.
- `IPvpService` (`FindOpponents`, `StartBattle`, `GetResult`) is a P1 interface to be written with the battle rules. It joins the table in `specs/08` section 2.

### 9.4 Fairness levers (open: choose before PvP rules are written)

PvE power compounds from stage depth, stat levels, gear rolls and gacha, so raw power will be very unequal between players of different age and spend. Options, from least to most intervention:

1. **Bracketed matchmaking** by certified power band. Cheapest; does nothing about ceiling effects inside a bracket.
2. **Compressed PvP stats**: battle uses a monotone compression of each layer (for example a capped multiplier per layer), so one extra stage or one lucky Mythic is worth less. Keeps progression meaningful, limits runaway advantage.
3. **PvP-only progression**: a separate, simple stat track bought with PvP currency, with PvE power as a small bonus. Strongest fairness, weakest tie to the rest of the game.

Recommendation: 1 plus 2, because it needs no new progression system and the compression lives in `BattleRules`, so it can be retuned without a client release (it ships as content). The decision belongs in the P1 PvP spec with a bot-style simulation of match outcomes by player age and spend, in the way `specs/09` validates PvE.

### 9.5 What PvP forces on the PvE engine

- **Hero roles.** Heroes are passive percentage bonuses in P0 (`specs/01` section 7.1). A battle needs something to act on. Either `BattleRules` defines how a passive bonus maps to battle stats, or P1 hero roles (`specs/01` section 7.1) arrive first. Pick one before the PvP rules.
- **Gear substats** (`specs/04` section 11.1) change what a gear piece means in battle. `GearPieceSnapshot` must carry them when they exist; the snapshot is versioned with `ContentHash`.
- **Content changes mid-season** change who wins. Section 11.

## 10. Accounts and identity (P1)

- `AccountId` (server) is the leaderboard and PvP identity. `ProfileId` (client) is the save identity. One ranked profile per account per season in P1.
- Anonymous account first; link to Apple or Google to recover on a new device. Recovery restores the last certified state (section 4.6), which is what the server can vouch for.
- Device attestation (Play Integrity, Apple App Attest) is a hardening step for P1 once there is something worth farming. It is not required for verification to work, and it does not replace it.
- Support tooling needs: view a profile's certified history, flag, restore, and reverse a ledger entry. Plan it with the backend (section 14).

## 11. Seasons and content pinning

A ranked season pins one `content_version` (`specs/05`). Remote config (`specs/08` section 7) may retune between seasons but not inside one, for ranked profiles: a mid-season balance change would invalidate segment verification and rewrite the leaderboard. Non-ranked play still gets overlays at session start as before. Season rollover: rating reset policy, rewards and the new content pin are server data.

## 12. Save schema v1: fields to add now

All nullable or defaulted, so no migration and P0 builds ignore them. `specs/06` section 3.1 is patched accordingly.

| Field | Where | P0 value | Used by |
|---|---|---|---|
| `AccountId` (string?) | `ProfileHeader` | `null` | section 10 |
| `RankedLineage` (bool) | `ProfileHeader` | `false` | section 3 |
| `Cert` (`CertState?`: `Seq`, `StateHash`, `CertifiedAtServerSec`, `GenesisHash`) | `GameState` | `null` | section 4 |
| `PendingReceipt` (`PendingReceipt?`) | `GameState` | `null` | section 8; the "add now" side of `specs/08` open decision 6 |
| `CommandLog` (bounded, since last cert) | not in the save | in memory; spilled to a sidecar file | section 4; a crash between certifications must not lose the log, so the save store writes a sidecar `slot0.log` on the same cadence as the save |

The log sidecar is part of the atomic-write rotation in `specs/06` section 5.2: a save and its log are written together or the previous pair is kept.

## 13. Threats and what is left

| Attack | Mitigation | Residual |
|---|---|---|
| Edit the save or memory (gold, stage, gear) | Next segment's `StateHash` will not match a legal replay; rejected, ranked frozen. | Single-player state is whatever the player wants. |
| Modified build that grants items | Same: state is re-derived from legal commands. | None for ranked. |
| Speed hack, clock change | Signed server time bounds ticks (4.3). Device clock is ignored for ranked. | Slack of 2 percent. |
| Seed grinding for lucky drops | Seeds server-issued at genesis (3). | Seeds are known to the client after genesis (needed to tick); knowing future gear drops gives no choice except which slot receives a known enhancement success, a small advantage; accepted for P1, revisit if PvP power is gear-dominated. |
| Peek at or replay gacha | Server-held seed; server roll; idempotency keys (`specs/08` section 4). | None. |
| Reload an old save to retry | Counters in the log; cert compare-and-swap on `Seq`. | Equivalent to not having played. |
| Debug commands in ranked | Compiled out of release; rejected and tainted server-side (4.3). | None. |
| Receipt replay or forgery | Server validation, dedupe on `TransactionId`. | Store-side fraud. |
| Several accounts or devices to farm rewards | Out of scope here; device attestation and per-account limits later. | Open. |
| Auto-clickers and bots | Legal commands; not detected. | Open, behavioural. |
| Rating manipulation, win trading | Server-chosen opponents (9.3). | Open for P2 social features. |
| Determinism bug causing honest mismatches | Golden runs on every platform incl. IL2CPP (5.1); desync reports; no first-strike bans (4.6). | Risk until the device fixture passes. |
| Content change mid-season | Season pin (11). | None. |

## 14. Order of work

**P0 (now, no server):** `CommandRecord` and `ICommandLog`; `StateHash`; `Replay`; the schema v1 fields (section 12); golden replay fixtures in the harness and CI (section 5.1, including a device run of the fixture); web prototype README states it is unranked; keep every stream seed individually persisted (already true).

**P1a:** accounts and genesis; `ServerClock`; remote gacha; the gem ledger; receipt validation and IAP.
**P1b:** `IProgressCertifier`, cloud save with the certified lineage, desync reporting, restore-last-certified.
**P1c:** leaderboards, seasons and content pinning.
**P1d:** PvP rules spec (fairness lever chosen first), `BattleSim`, `IPvpService`, matchmaking and rating.

Each step is usable without the next. P1a without P1b is a paid game with an honest economy and no rankings.

## 15. Open decisions

1. **Ranked on a profile that started offline.** Section 3 says it can never be ranked. Alternative: allow a one-time server "adoption" that replays the whole log from a client-chosen genesis and then re-seeds forward. That reintroduces the grinding risk for the pre-adoption prefix, so it is rejected for now; confirm the player-facing wording and whether first-launch offline should hold the tutorial until a genesis call succeeds.
2. **Negative ledger balance after a chargeback.** Clamp, block pulls, or claw back copies (section 8).
3. **Whether to hide gear and enhancement seeds from the client** (the residual in section 13) by moving those streams server-side. Costs one round trip per drop, which breaks offline play; likely not worth it unless PvP power is gear-dominated.
4. **`CERT_INTERVAL_MIN`, `CERT_TIME_SLACK`, `CERT_MAX_SEGMENT_TICKS`** are proposals; tune against real upload sizes and server cost.
5. **Fairness lever for PvP** (9.4), and how heroes act in battle (9.5).
6. **One ranked profile per account** versus several (affects leaderboard dedupe and support).
7. **Where the canonical `StateHash` encoding lives** if the server is not .NET (needs a port plus the fixture in section 5.1, as with the gacha vector file in `specs/08` section 4.3).
8. **IL2CPP floating-point behaviour** (5.1): whether the device fixture passes as is. If not, the fix lands in `specs/03` section 4.
