## 08 - Backend and Live Ops

Specifies `Whisker.Server`: every service and endpoint the client calls, the auth model, the server-authoritative summon algorithm and its audit trail, receipt validation for both stores, remote config and content versioning, anti-cheat plausibility checks and the action matrix, live-ops tooling, infrastructure and cost, and compliance. Everything here sits behind the six interfaces of 00 section 8.11 with local implementations, so M0 ships with no server at all; the server is additive from MVP.

Tiers covered: MVP (everything not marked otherwise), P1 (Guild, Leaderboard, Coupon, Rewarded Ads, Events schedule), with M0 notes where the local implementation differs.

Depends on: 00 (sections 3, 4, 6.3, 8), 01 (flows, `ApiClient`, error taxonomy), 02 (save metadata, conflict rules, relational schema, clock), 04 (banner level reshaping, item pools), 06 (currency rules, shop products, first-purchase double).

## 1. Service inventory

One ASP.NET Core 8 deployable (`Whisker.Server`), organised as feature folders; "service" below is a code boundary, not a separate process. Splitting into processes is a P2 question that nothing here forecloses because services share no in-memory state.

| Service | Owns | Tables (02 section 12) | Tier | M0 local implementation |
|---|---|---|---|---|
| Auth | guest creation, JWT issue/refresh, platform linking, logout, recovery, device registration, server time | `accounts`, `account_links`, `devices`, `refresh_tokens` | MVP | none needed; `PlayerId = Guid.Empty`, `ServerClock(api: null)` |
| Profile | display name, avatar, age gate, account delete/export | `accounts` | MVP | `profile.json` cache only |
| Save | upload/download, metadata, history, plausibility hook | `saves`, `save_history`, S3 | MVP | `LocalSaveStore` |
| Summon | pull, pity state, rate table, history | `banner_pity`, `summon_log`, `diamond_ledger`, `idempotency` | MVP | `LocalSummonService` (same algorithm, `summon_*` streams) |
| Inbox | list, claim, claim-all, expiry | `inbox_items`, `diamond_ledger` | MVP | `LocalInboxService` (empty) |
| IAP / Receipt | catalog, validate, restore, store webhooks, refunds | `purchases`, `inbox_items`, `diamond_ledger` | MVP | `DisabledReceiptValidator`; `FakeReceiptValidator` in debug builds |
| Config / Content | remote config, kill switches, content manifest pointer, min versions, maintenance | `config_versions` | MVP | `LocalConfigService` (bundled content, all flags on) |
| Dungeon seed | seed issue, result correlation | `dungeon_seeds` | MVP | `LocalDungeonSeedService` (one draw from `dungeon`) |
| Mail / Admin | mail blasts, compensation, moderation actions, config publishing, support lookups | `mail_blasts`, `moderation_actions`, `config_versions` | MVP (internal) | n/a |
| Guild | create/join/chat/boss | `guilds`, `guild_members`, `guild_chat` | P1 | n/a |
| Leaderboard | weekly boards from save summaries | Redis ZSETs | P1 | n/a |
| Coupon | redeem codes | `coupons`, `coupon_redemptions` (P1 tables) | P1 | n/a |
| Ads | rewarded-ad server-side verification callbacks | `ad_rewards` (P1) | P1 | n/a |

### 1.1 Conventions shared by every endpoint

- Base URL `https://api.{env}.whiskerhero.example/v1`. TLS 1.2+, HSTS. JSON bodies, `Content-Type: application/json; charset=utf-8`; the save blob is `application/octet-stream`.
- Headers from the client (01 section 5.1): `Authorization: Bearer <jwt>`, `X-Request-Id`, `X-Client-Version`, `X-Content-Version`, `X-Device-Id`, `Accept-Language`.
- Every response carries `X-Server-Time` (unix seconds) and `X-Request-Id` echoed.
- Error body, every 4xx/5xx:

```json
{ "code": "INSUFFICIENT_FUNDS", "message": "Not enough diamonds.", "requestId": "c1f0...", "retryAfterSec": null, "details": { "need": 1000, "have": 640 } }
```

`code` is from a closed list per endpoint below plus the global set: `UNAUTHENTICATED` (401), `TOKEN_EXPIRED` (401), `FORBIDDEN` (403), `CLIENT_TOO_OLD` (426), `CONTENT_TOO_OLD` (409), `RATE_LIMITED` (429), `MAINTENANCE` (503), `VALIDATION` (400), `IDEMPOTENCY_IN_FLIGHT` (409), `INTERNAL` (500).

- **Idempotency** (00 section 8.13): every mutating endpoint requires `X-Request-Id` (32 hex chars). The handler does `INSERT ... ON CONFLICT DO NOTHING` into `idempotency` with status `in_flight` first; if the row existed with a final status, the stored response is replayed with header `X-Idempotent-Replay: true`; if it existed as `in_flight`, `409 IDEMPOTENCY_IN_FLIGHT` with `retryAfterSec: 1`. Rows live `IDEMPOTENCY_TTL_H = 24`. A `requestId` seen under a different `playerId` is rejected `403` and flagged `DUPLICATE_REQUEST_ID` (section 7).
- **Rate limits** are per `playerId` (per IP for unauthenticated routes), sliding window in Redis (`rl:{route}:{playerId}`), reported with `RATE_LIMITED` and `Retry-After`. Values below are per minute unless stated.
- **Version gates**: `X-Client-Version < config.minClientVersion` -> `426 CLIENT_TOO_OLD` on every route except `/config`, `/time`, `/auth/*`. `X-Content-Version < minContentVersion[clientVersion]` -> `409 CONTENT_TOO_OLD` on `/summon/pull`, `/dungeon/seed`, `/save` PUT (the sim-sensitive routes).
- **Maintenance**: when `maint` is set in Redis, every route except `/config` and `/time` returns `503 MAINTENANCE` with the message and `until`.
- Auth column below: `none`, `jwt` (any valid access token), `jwt+device` (token's `deviceId` claim must match `X-Device-Id`), `admin` (internal network + SSO).

## 2. Auth

### 2.1 Model

```mermaid
flowchart LR
    Guest["Guest: device id -> playerId, JWT"] -->|link| Linked["Linked: + Apple / Google Play Games identity"]
    Linked -->|sign in on new device| Multi["Multiple devices share one playerId"]
    Guest -->|new device, no link| Lost["Lost (recoverable via support code only)"]
```

- **Tokens.** Access JWT: RS256, `exp` 1 h, claims `sub` (playerId), `dev` (deviceId), `age` (age gate), `iat`, `jti`. Refresh token: 256-bit random, stored hashed in `refresh_tokens`, 60 days, **rotated on every use** (the old one is revoked; reuse of a revoked refresh token revokes the whole family and returns `TOKEN_REUSED`, which forces re-login and is logged as a possible theft).
- **Guest.** `POST /auth/guest` with a device id creates an account and a device row. The device key for HMAC (02 section 10) is registered in the same call. A guest can play, buy, and cloud-save; the only thing a guest cannot do is recover the account on another device without linking.
- **Linking.** Sign in with Apple: the client sends the identity token (JWT) from `ASAuthorizationAppleIDCredential`; the server verifies signature against Apple's JWKS, `aud` = our bundle id, `nonce`, and takes `sub`. Google Play Games Services v2: the client sends the server auth code; the server exchanges it for tokens and calls `games.players.get('me')` for `playerId`. The provider id is stored as `sha256(providerUserId || pepper)`.
- **Multiple devices.** Allowed concurrently. Each has its own `deviceId`, key and refresh token family. Saves reconcile by 02 section 9.3. Summons are serialised per player by the Redis lock, so two devices pulling at once get `409 IDEMPOTENCY_IN_FLIGHT` / a replayed result, never a double roll.
- **Recovery.** `SCR_SETTINGS` shows a 12-character support code (`base32(hmac(playerId, serverKey))[:12]`). A player who lost a guest account can give support the code plus a recent purchase receipt; support links a new device via the admin API. No self-service recovery for unlinked guests, by design.
- **Age gate** is set once at first launch (`PATCH /profile {ageGate}`) from a neutral age screen (section 10.1). It is in the JWT so every handler can branch without a lookup.

### 2.2 Endpoints

**`POST /auth/guest`** -- auth `none` -- idempotent by `X-Request-Id` -- rate limit 5/min per IP, 20/day per IP.

```json
// request
{ "deviceId": "0f9e8d7c-...", "deviceKey": "<base64 32 bytes>", "model": "Pixel 7", "os": "Android 14", "clientVersion": "1.4.2+318", "region": "US" }
// 201
{ "playerId": "6b1f2c40-...", "accessToken": "eyJ...", "refreshToken": "r1.…", "accessExpiresAt": 1793670800, "profile": { ...Profile (02 section 4)... }, "serverTime": 1793667200 }
```

Errors: `DEVICE_ALREADY_BOUND` (409; this device already has a guest account; response includes `playerId` so the client refreshes instead), `VALIDATION`.

**`POST /auth/refresh`** -- auth `none` (refresh token in body) -- not idempotent (rotation) -- 30/min.

```json
{ "refreshToken": "r1.…", "deviceId": "0f9e8d7c-..." }
// 200
{ "accessToken": "eyJ...", "refreshToken": "r2.…", "accessExpiresAt": 1793674400, "serverTime": 1793670800 }
```

Errors: `TOKEN_EXPIRED` (401), `TOKEN_REVOKED` (401), `TOKEN_REUSED` (401, family revoked), `DEVICE_MISMATCH` (403).

**`POST /auth/link`** -- auth `jwt+device` -- idempotent -- 10/min.

```json
{ "provider": "apple", "identityToken": "eyJ...", "nonce": "…", "authorizationCode": "…" }
{ "provider": "google_play", "serverAuthCode": "4/0A…" }
// 200
{ "profile": { ... }, "linked": [ { "provider": "apple", "linkedAt": 1793670800 } ] }
```

Errors: `PROVIDER_TOKEN_INVALID` (400), `PROVIDER_ALREADY_LINKED` (409; this platform identity belongs to another `playerId`; `details.otherSummary` carries that account's save summary so the client can run the 01 section 8.13 step 4 choice), `ACCOUNT_ALREADY_HAS_PROVIDER` (409).

**`POST /auth/link-login`** -- auth `none` -- not idempotent -- 10/min per IP. Same body as `/auth/link`. Returns the same shape as `/auth/guest` for the account that owns that platform identity, and registers the calling device (`deviceId`, `deviceKey`) under it. Error `NO_ACCOUNT_FOR_PROVIDER` (404) -> client offers "create new" (guest + link in one go).

**`POST /auth/merge`** -- auth `jwt+device` -- idempotent -- 2/hour. Resolves `PROVIDER_ALREADY_LINKED`: `{ "provider", "identityToken"|"serverAuthCode", "keep": "this"|"other" }`. `keep: "other"` re-issues tokens for the other account and archives this guest (status `merged_away`, save blob retained 30 days). `keep: "this"` moves the platform link to this account and archives the other. Both write `moderation_actions` with `action = account_merge`.

**`POST /auth/logout`** -- auth `jwt` -- idempotent -- 10/min. `{ "refreshToken": "…" }` -> `204`. Revokes that family only.

**`POST /devices`** -- auth `jwt` -- idempotent -- 5/min. Registers or rotates a device key: `{ "deviceId", "deviceKey", "model", "os", "clientVersion", "pushToken"? }` -> `204`. Key rotation is only accepted from a request whose save HMAC verified under the old key or within 24 h of `/auth/guest` for that device.

**`GET /time`** -- auth `none` -- 60/min per IP. `{ "serverTime": 1793670800 }`. Also every response's `X-Server-Time`.

### 2.3 Profile and account

**`GET /profile`** -- `jwt` -- 60/min -> `Profile` (02 section 4).

**`PATCH /profile`** -- `jwt` -- idempotent -- 10/min. `{ "displayName"?, "avatarId"?, "ageGate"? }`. Display names: 2-16 chars after NFC normalisation, no control characters, filtered against a blocklist (`DISPLAY_NAME_BLOCKED` 400), unique not required. `ageGate` can be set only when currently `0` (`AGE_GATE_LOCKED` 409); changing it afterwards is a support action.

**`POST /account/delete`** -- `jwt+device` -- idempotent -- 1/hour. `{}` -> `{ "graceUntil": 1794880000 }`. 02 section 13.2.

**`POST /account/delete/cancel`** -- `jwt` -- idempotent. `{}` -> `204`; `DELETE_NOT_PENDING` 409; `GRACE_EXPIRED` 410.

**`POST /account/export`** -- `jwt` -- idempotent -- 1 per 7 days (`EXPORT_RATE_LIMITED` 429 with `retryAfterSec`). `{}` -> `{ "status": "queued", "estimatedMinutes": 10 }`; the link appears in `GET /account/export` -> `{ "status": "ready", "url": "https://…", "expiresAt": … }`.

## 3. Save, Inbox, Dungeon seed, Config endpoints

### 3.1 Save

**`GET /save/meta`** -- `jwt` -- 60/min -> the metadata object of 02 section 9.1 plus `etag`, or `404 NO_SAVE`.

**`GET /save`** -- `jwt` -- 10/min -> `200` with `ETag` header, metadata in `X-Save-Meta` (JSON), body = the raw save file (header + JSON body) as `application/octet-stream`. Served by redirect to a 5-minute presigned S3 URL when `sizeBytes > 64 KiB`, inline otherwise.

**`PUT /save`** -- `jwt+device` -- idempotent -- 6/min, 400/day (a 2 h session at one upload per minute is 120).

Request: headers `If-Match: <etag>` (or `*` for first upload), `X-Save-Meta: <json>` (02 section 9.1), `Content-Type: application/octet-stream`, body = the save file (max `SAVE_BLOB_MAX_BYTES`).

```json
// 200
{ "etag": "W/\"1875-0f9e\"", "saveSeq": 1875, "serverTime": 1793670900, "flags": [], "diamondReconcile": null }
// 200 with reconciliation (section 7.4)
{ "etag": "...", "saveSeq": 1875, "serverTime": ..., "flags": ["DIAMOND_LEDGER_MISMATCH"], "diamondReconcile": { "clientBalance": 9999, "serverBalance": 1240, "applied": true } }
// 409
{ "code": "SAVE_CONFLICT", "details": { "serverMeta": { ...02 section 9.1... } } }
```

Server steps, in order: (1) parse header, verify `len`/CRC (`SAVE_MALFORMED` 400); (2) verify HMAC against `devices.hmac_key_enc` for `X-Device-Id` (record `hmac_valid`; not a rejection); (3) `If-Match` vs `saves.etag` (`SAVE_CONFLICT` 409 unless `*` and no row); (4) `saveSeq` must exceed the stored one for the same `deviceId` (`SEQ_REGRESSION` flag, accepted but flagged; a lower seq from the *same* device is rejected `SAVE_STALE` 409 because the device itself knows better); (5) `lastSavedServerTime <= now + CLOCK_MAX_SKEW_ACCEPT_S` (`CLOCK_AHEAD` flag); (6) plausibility checks (section 7) asynchronously *except* `DIAMOND_LEDGER_MISMATCH`, which runs inline because its reconciliation is in the response; (7) write blob to S3 `saves/{playerId}/{saveSeq}.sav`, upsert `saves`, insert `save_history`, trim to `SAVE_HISTORY_KEEP`; (8) P1: update leaderboard ZSETs from `summary`.

**`GET /save/history`** -- `jwt` -- 10/min -> `[{ "saveSeq", "deviceId", "uploadedAt", "summary" }]` (last 10). Restoring one is a support action (`POST /admin/save/restore`), not self-service, because a self-service rollback is a free re-roll of anything client-side.

### 3.2 Inbox

**`GET /inbox`** -- `jwt` -- 30/min.

```json
// 200
{ "items": [
    { "inboxItemId": "ibx_01HZ…", "kind": "iap", "titleKey": "inbox.iap.title", "bodyKey": "inbox.iap.body", "params": { "sku": "diamonds_3" },
      "grants": [ { "kind": "currency", "id": "DIAMOND", "amount": 2400 } ], "createdAt": 1793670000, "expiresAt": null },
    { "inboxItemId": "ibx_01HZ…", "kind": "compensation", "titleKey": "inbox.comp.title", "bodyKey": "inbox.comp.2026_11_03", "params": {},
      "grants": [ { "kind": "currency", "id": "DIAMOND", "amount": 300 }, { "kind": "currency", "id": "TICKET_SUMMON_WEAPON", "amount": 5 } ],
      "createdAt": 1793600000, "expiresAt": 1796200000 } ],
  "serverTime": 1793670900 }
```

Unclaimed items only; expired items are excluded and purged nightly. Inbox items never contain diamonds that were not first written to `diamond_ledger` with `ref_type = inbox_item` at claim time (not at creation), so an unclaimed item is not a balance.

**`POST /inbox/claim`** -- `jwt` -- idempotent -- 30/min. `{ "inboxItemId" }` ->

```json
{ "inboxItemId": "…", "grants": [ ... ], "balances": { "DIAMOND": 3640 }, "reason": "IAP", "serverTime": … }
```

Writes `diamond_ledger` (`+amount`, reason `IAP`/`MAIL`/`ADMIN`, `ref_type = inbox_item`), sets `claimed_at`, in one transaction. Errors: `INBOX_NOT_FOUND` 404, `INBOX_ALREADY_CLAIMED` 409 (response still carries the original `grants` so the client can reconcile `Inbox.ClaimedIds`), `INBOX_EXPIRED` 410. Item grants (`kind: item`) are returned for the client to apply through `ApplyInboxGrant`; the server also appends them to a per-player `granted_items` ledger used by `ITEM_WITHOUT_PULL` (section 7).

**`POST /inbox/claim-all`** -- `jwt` -- idempotent -- 10/min. `{}` -> `{ "claimed": [ ...per-item results... ], "balances": {...} }`. Claims up to 50 items oldest-first; a partial failure (an item expiring between list and claim) is skipped and reported, not an error.

### 3.3 Dungeon seed

**`POST /dungeon/seed`** -- `jwt` -- idempotent -- 20/min, 60/day.

```json
{ "kind": "FISH_COIN", "floor": 7, "clientContentVersion": "2026.11.03" }
// 200
{ "grantId": "a4d1…", "seed": "9f86d081884c7d65", "expiresAt": 1793671500, "serverTime": 1793670900 }
```

Checks: `floor` in `[1, maxFloor(kind)]` (`VALIDATION`); the player's last uploaded save has `BestFloor[kind] >= floor - 1` (`DUNGEON_LOCKED` 403; the client's state may be ahead of the last upload, so a mismatch by one floor is tolerated: `floor <= BestFloor + 2`); outstanding unexpired seeds for the same `(kind, dayKey)` count against a daily cap of `DUNGEON_SEEDS_PER_DAY = 10` per kind (`DUNGEON_SEED_CAP` 429). Seed = 64 random bits from the server CSPRNG, stored in `dungeon_seeds` with `expires_at = now + DUNGEON_SEED_TTL_S`. The result is filled from the next save upload (section 7.3 correlates `Stats.DungeonClears` and `BestFloor` with issued grants).

### 3.4 Config

**`GET /config`** -- `none` (optionally `jwt`, which enables the rollout bucket) -- 30/min per IP. Section 6 gives the body. Served with `Cache-Control: max-age=60`; the client caches for `CONFIG_CACHE_MAX_AGE_S` (01).

Content bundles are **not** served by the API; `config.content.url` points to the CDN (`https://cdn.whiskerhero.example/content/{version}/manifest.json`), immutable per version, `Cache-Control: public, max-age=31536000`.

## 4. Summon service (server-authoritative, MVP)

### 4.1 Rate table and pity rules (00 section 6.3, restated as data)

The rate table for a banner at level `L` comes from `banner_levels` (09). The algorithm never changes the Legendary and Mythic probabilities across banner levels, so the published odds for the two rarities players care about are constant; levels redistribute weight from C to R/E (04 defines the schedule). The table at level 1 is the 00 base: C .60 / R .28 / E .09 / L .025 / M .005.

```csharp
public sealed record RateTable(double C, double R, double E, double L, double M)   // sums to 1 within 1e-9 (09 validator)
{
    public double LPlus => L + M;                                 // 0.03 at every level
    public double MShare => M / (L + M);                          // M_SHARE_OF_LPLUS = 1/6 at every level
}
```

Pity: `pity` = pulls since the last L+ result, held server-side in `banner_pity`. For the pull with index `n = pity + 1`:

```
pLPlus(n) = n <= PITY_SOFT_START            ? table.LPlus                                   // pulls 1..50: 0.03
          : n >= PITY_HARD                   ? 1.0                                           // pull 80: forced
          : min(1.0, table.LPlus + PITY_SOFT_STEP * (n - PITY_SOFT_START))                  // pull 51..79: 0.03 + 0.0325*(n-50)
```

Worked: `n = 51 -> 0.0625`, `n = 60 -> 0.355`, `n = 70 -> 0.68`, `n = 79 -> 0.9725`, `n = 80 -> 1.0`. The formula reaches 1.0 at `n = 80` on its own (`0.03 + 0.0325*30 = 1.005`), so `PITY_HARD` is a guarantee consistent with the curve, not an override of it.

Below-L+ rarities on a pull with `pLPlus > table.LPlus` are scaled so the total is still 1: `pC = table.C * (1 - pLPlus) / (1 - table.LPlus)`, same for R and E.

### 4.2 The pull as a pure function

The algorithm is a pure function in `Whisker.Sim.Summon` shared by the server, `LocalSummonService` and the Python model (04). Only the source of the uniforms differs: the server draws from a CSPRNG, the local service from the `summon_<banner>` stream, the model from `random`. Three uniforms per pull, always consumed, even when unused, so stream counters stay `3 * TotalPulls` (02 section 3.4).

```csharp
public static class SummonAlgorithm
{
    public readonly record struct PullInput(int Pity, double U1, double U2, double U3);
    public readonly record struct PullOutput(Rarity Rarity, int PoolIndex, int PityAfter, bool Guarantee);

    // One pull. u1 picks the rarity, u2 picks M vs L inside L+, u3 picks the item within the rarity pool.
    public static PullOutput Pull(RateTable t, int poolSizeOfRarity(Rarity), PullInput x, bool forceEplus = false)
    {
        int n = x.Pity + 1;
        double pLPlus = n <= PITY_SOFT_START ? t.LPlus : n >= PITY_HARD ? 1.0 : Math.Min(1.0, t.LPlus + PITY_SOFT_STEP * (n - PITY_SOFT_START));
        double scale  = (1.0 - pLPlus) / (1.0 - t.LPlus);
        double pC = t.C * scale, pR = t.R * scale, pE = t.E * scale;

        Rarity r; bool guarantee = false;
        if (forceEplus)
        {   // slot-10 reroll from the E+ sub-distribution: E, L, M in their natural proportions at this pity
            double eplus = pE + pLPlus;
            if (x.U1 * eplus < pE) r = Rarity.EPIC;
            else r = x.U2 < t.MShare ? Rarity.MYTHIC : Rarity.LEGENDARY;
            guarantee = true;
        }
        else if (x.U1 < pLPlus)              r = x.U2 < t.MShare ? Rarity.MYTHIC : Rarity.LEGENDARY;
        else if (x.U1 < pLPlus + pE)         r = Rarity.EPIC;
        else if (x.U1 < pLPlus + pE + pR)    r = Rarity.RARE;
        else                                 r = Rarity.COMMON;

        int pityAfter = r >= Rarity.LEGENDARY ? 0 : x.Pity + 1;
        int idx = (int)Math.Floor(x.U3 * poolSizeOfRarity(r));      // uniform within the rarity's pool; 04 may weight items, in which case this is a CDF walk
        return new PullOutput(r, idx, pityAfter, guarantee);
    }

    // A 1x or 10x batch. The 10-pull guarantee: if slots 1-9 produced nothing >= EPIC, slot 10 is rerolled from the E+ sub-distribution.
    public static PullOutput[] Batch(RateTable t, Func<Rarity,int> pool, int pity, int count, Func<double> next)
    {
        var out = new PullOutput[count]; bool anyEplus = false;
        for (int i = 0; i < count; i++)
        {
            bool force = count == 10 && i == 9 && !anyEplus;
            out[i] = Pull(t, pool, new PullInput(pity, next(), next(), next()), force);
            pity = out[i].PityAfter; anyEplus |= out[i].Rarity >= Rarity.EPIC;
        }
        return out;
    }
}
```

Notes: `forceEplus` reuses the same three uniforms (no extra draw), so the guarantee changes the outcome but not the stream consumption. Pity continues to advance correctly through a forced E (not L+), and resets on a forced L/M. The order "u1 rarity, u2 M-vs-L, u3 item" is fixed so the test vectors are meaningful.

Test vectors (`Fixtures/Summon/vectors.json`, shared by `Whisker.Sim.Tests`, `Whisker.Server.Tests` and `tools/gacha_model.py`): 500 rows of `(table, pity, u1, u2, u3, forceEplus) -> (rarity, pityAfter, guarantee)` plus 20 batch rows. Server and client must reproduce every row.

### 4.3 Handler

**`POST /summon/pull`** -- `jwt+device` -- idempotent -- 20/min, and at most one in flight per player.

```json
// request
{ "banner": "WEAPON", "count": 10, "payment": "DIAMOND", "clientContentVersion": "2026.11.03" }
// 200
{ "requestId": "c1f0…", "banner": "WEAPON", "count": 10,
  "pulls": [ { "itemId": "wpn_017", "rarity": "R", "isNew": false, "levelBefore": 1, "levelAfter": 1, "guaranteeApplied": false }, "...9 more" ],
  "pityBefore": 23, "pityAfter": 33, "bannerLevelBefore": 4, "bannerLevelAfter": 4, "bannerExpAfter": 22,
  "debits": [ { "currency": "DIAMOND", "amount": -1000 } ],
  "balances": { "DIAMOND": 240, "TICKET_SUMMON_WEAPON": 3 },
  "rateTableHash": "3f9a…", "serverTime": 1793670900 }
```

```
Handle(player, req):
  acquire Redis lock summon:lock:{player} (10 s) else 409 IDEMPOTENCY_IN_FLIGHT
  idempotency lookup by (player, X-Request-Id): replay if final
  validate: banner open per schedule (BANNER_CLOSED 409); count in {1,10} (VALIDATION); payment in {DIAMOND, TICKET}
  content = ContentDb for req.clientContentVersion (CONTENT_TOO_OLD 409 if below min)
  tx:
    pity = select ... from banner_pity for update (insert default if absent)
    table = content.BannerLevels[banner][pity.level]
    cost  = payment == DIAMOND ? PULL_COST[banner] * count : count tickets
    if payment == DIAMOND: balance = sum(diamond_ledger) ; if balance < cost: 402 INSUFFICIENT_FUNDS {need, have}
    else: tickets = server ticket ledger (06 keeps summon tickets server-side from MVP); if tickets < count: 402 INSUFFICIENT_FUNDS
    if payment == DIAMOND and tickets >= count: 409 PAYMENT_MISMATCH {expected: "TICKET"}   // 06 rule: tickets first; client out of date
    outputs = SummonAlgorithm.Batch(table, pool, pity.pity, count, csprng.NextDouble)
    items   = outputs.Select(o => content.Pool(banner, o.Rarity)[o.PoolIndex])
    insert count rows into summon_log (one per pull, with roll_u = u1 of that pull, pity_before/after, guarantee, rate_table_hash)
    insert diamond_ledger (-cost, SUMMON, ref summon, request_id) or decrement ticket ledger
    update banner_pity: pity = last pityAfter, exp += BANNER_EXP_PER_PULL * count, level = levelFor(exp) (04 thresholds), total_pulls += count
    compute isNew/levelBefore/levelAfter from the player's item ledger (granted_items + summon_log aggregates), append items
    insert idempotency(final, response)
  release lock; return response
```

Latency target p99 under 150 ms. The response is the `SummonResultPayload` of 01 section 2.5; the client applies it with `ApplySummonResult`, replacing `DIAMOND` with `balances.DIAMOND`.

Errors: `INSUFFICIENT_FUNDS` 402, `PAYMENT_MISMATCH` 409, `BANNER_CLOSED` 409, `CONTENT_TOO_OLD` 409, `IDEMPOTENCY_IN_FLIGHT` 409, `SUMMON_DISABLED` 503 (kill switch).

### 4.4 Audit log

One `summon_log` row per pull, never per batch, so a regulator or support agent can reproduce any pull: given `rate_table_hash` (which identifies the exact published table), `pity_before` and `roll_u`, `SummonAlgorithm.Pull` yields the logged rarity. The row also records `guarantee` so 10-pull rerolls are visible. Retention 7 years (02 section 13). `GET /summon/history?banner=WEAPON&limit=100` -- `jwt` -- 10/min -> the player's own rows (item, rarity, pity, time; not `roll_u`).

### 4.5 Published rates

**`GET /summon/rates?banner=WEAPON&level=4`** -- `none` -- 60/min per IP -- `Cache-Control: max-age=3600`.

```json
{ "banner": "WEAPON", "level": 4, "contentVersion": "2026.11.03", "rateTableHash": "3f9a…",
  "rates": { "C": 0.52, "R": 0.33, "E": 0.12, "L": 0.025, "M": 0.005 },
  "pity": { "softStart": 50, "softStepPerPull": 0.0325, "hard": 80, "lplusAtSoftStart": 0.03 },
  "tenPullGuarantee": "EPIC_OR_BETTER",
  "effective": { "lplusPerPullAveraged": 0.0412, "expectedPullsToLplus": 24.3, "p99PullsToLplus": 62 },
  "items": [ { "itemId": "wpn_001", "rarity": "C", "weight": 1 }, "..." ] }
```

`effective` numbers are computed by `tools/gacha_model.py` at export time and stored in `banner_levels` so the server serves exactly what the model printed. `SCR_SUMMON` renders this endpoint's body (07); the same JSON is exported to the store listing's odds page (section 10.3).

**`GET /summon/state`** -- `jwt` -- 30/min -> `{ "banners": { "WEAPON": { "pity": 23, "level": 4, "exp": 12, "totalPulls": 412 }, ... } }`. The client calls it at boot to overwrite `Banners[b]` mirrors.

### 4.6 M0 note

`LocalSummonService` (Runtime) runs `SummonAlgorithm.Batch` with `next = () => streams["summon_" + banner].NextDouble()`, charges the local `Wallet`, and writes no log. The bot harness uses the same class. Because the function is shared, the bot's realised rates predict live rates exactly, and the server's test suite asserts the vectors file against the same assembly.

## 5. IAP and receipt validation

### 5.1 Catalog

**`GET /shop/catalog`** -- `jwt` -- 30/min -> the `shop_products` and `shop_packages` rows (09) filtered by region, age gate and kill switches, with per-player state (`firstPurchaseDoubleAvailable`, `dailyFreeClaimed`, package purchase counts). Prices shown in the client come from the store SDK, not from this endpoint; the endpoint carries `appleProductId`, `googleProductId` and the grant.

### 5.2 Validate

**`POST /iap/validate`** -- `jwt+device` -- idempotent by `X-Request-Id` **and** by `(platform, transactionId)` -- 10/min.

```json
// request (Apple)
{ "platform": "apple", "sku": "diamonds_3", "transactionId": "2000000123456789", "signedTransaction": "eyJ…", "appAccountToken": "6b1f2c40-…" }
// request (Google)
{ "platform": "google_play", "sku": "diamonds_3", "purchaseToken": "opaque…", "orderId": "GPA.1234-…", "obfuscatedAccountId": "sha256(playerId)" }
// 200
{ "status": "GRANTED", "inboxItemId": "ibx_01HZ…", "grants": [ { "kind": "currency", "id": "DIAMOND", "amount": 2400 } ], "firstPurchaseDoubled": true, "serverTime": … }
// 200 replay
{ "status": "ALREADY_GRANTED", "inboxItemId": "ibx_01HZ…", "grants": [ ... ] }
```

**Apple (StoreKit 2 / App Store Server API).** The client sends the JWS `signedTransaction` from `Transaction.jwsRepresentation`. Server: verify the JWS chain against Apple's root CA (bundled, pinned by fingerprint), check `bundleId`, `environment` matches the server env (`Sandbox` accepted only on dev/stage), `productId == sku`, `appAccountToken == playerId` (set by the client at purchase so a receipt cannot be replayed onto another account; mismatch -> `RECEIPT_ACCOUNT_MISMATCH` 403 and a flag). Then confirm server-to-server with `GET /inApps/v1/transactions/{transactionId}` (App Store Server API, JWT-signed with our key) so a forged JWS with a stolen intermediate cannot pass; 5xx from Apple -> retry 3x then `STORE_UNAVAILABLE` 503 (the client keeps its pending receipt). `revocationDate` present -> `RECEIPT_REVOKED` 410.

**Google (Play Developer API + RTDN).** Server calls `purchases.products.get(packageName, sku, purchaseToken)` with a service account; requires `purchaseState == 0` (purchased), `consumptionState == 0`, `obfuscatedExternalAccountId == sha256(playerId)` (mismatch -> `RECEIPT_ACCOUNT_MISMATCH`), `orderId` matches. Then the server itself calls `purchases.products.acknowledge` after the grant (client-side acknowledge is also allowed; whichever first). Diamonds are consumables, so the client consumes after the 200 (01 section 8.9 step 4).

**Grant** (one transaction): insert `purchases(status = granted)`, insert `inbox_items(kind = iap, grants = product.grant * (firstPurchase ? 2 : 1))`, set `accounts.first_purchase_done`, insert `idempotency`. Diamonds are **not** written to `diamond_ledger` here; that happens at `/inbox/claim`, so an unclaimed purchase is visible in the inbox and not spendable. Raw receipt payload to S3 `receipts/{playerId}/{purchaseId}.json` (7-year retention).

Errors: `RECEIPT_INVALID` 400 (signature/fields), `RECEIPT_ACCOUNT_MISMATCH` 403, `RECEIPT_REVOKED` 410, `SKU_UNKNOWN` 400, `SKU_NOT_AVAILABLE` 409 (region/age/kill switch), `STORE_UNAVAILABLE` 503.

### 5.3 Restore

**`POST /iap/restore`** -- `jwt+device` -- idempotent -- 2/min. `{ "platform", "receipts": [ ...up to 50 of the validate shapes... ] }` -> per-item results. Each runs the validate path; duplicates return `ALREADY_GRANTED`. For Apple the client sends `Transaction.currentEntitlements` plus unfinished transactions; for Google, `queryPurchasesAsync`. Non-consumable packages (P1 remove-ads) restore here too.

### 5.4 Refunds, chargebacks, voided purchases

Webhooks, both `auth none` with signature verification, idempotent on the store's notification id:

- **`POST /webhooks/apple`**: App Store Server Notifications V2 (`signedPayload` JWS, verified like a transaction). Handle `REFUND`, `REVOKE`, `CONSUMPTION_REQUEST` (answer with our consumption data: `POST /inApps/v1/transactions/consumption/{transactionId}` with `consumptionStatus` derived from whether the inbox item was claimed and the diamonds spent), `TEST`.
- **`POST /webhooks/google`**: Pub/Sub push with the RTDN `oneTimeProductNotification` (`ONE_TIME_PRODUCT_CANCELED`) and a nightly poll of `purchases.voidedpurchases.list` as a backstop.

On refund of purchase `P` granting `N` diamonds:

```
tx:
  purchases.status = refunded; refunded_at = now
  if inbox item unclaimed: inbox_items.expires_at = now (vanishes)          // nothing to claw back
  else:
    insert diamond_ledger(-N, REFUND, ref purchase P)
    balance = sum(ledger)
    if balance < 0: insert diamond_ledger(+(-balance), ADMIN, ref "refund_debt_clamp"); accounts.refund_debt += -balance
  insert inbox_items(kind = refund, grants = [], titleKey inbox.refund.title, params {sku, diamonds: N})   // informational, shown once
  moderation: refunds_90d += 1; if >= REFUND_ABUSE_COUNT (3): soft_flag; if >= 5: ban review queue
```

The client learns on the next `PUT /save` response (`diamondReconcile`) or `GET /inbox`; 01 section 8.9 step 8 shows the one-time notice. A player whose items from a refunded pull remain keeps them (clawing back items is a support decision), which is why refund abuse escalates to the action matrix rather than being silently absorbed.

### 5.5 M0 note

`DisabledReceiptValidator` throws `Unsupported`; the shop is not in M0. `FakeReceiptValidator` (debug builds) returns `GRANTED` from the local catalog so `SCR_SHOP` can be exercised.

## 6. Remote config and content versioning

### 6.1 Config body

**`GET /config`** returns the active `config_versions.payload` for the environment, with the rollout bucket applied when a JWT is present:

```json
{
  "configVersion": 318,
  "serverTime": 1793670900,
  "minClientVersion": "1.4.0",
  "latestClientVersion": "1.4.2",
  "storeUrls": { "ios": "https://apps.apple.com/…", "android": "https://play.google.com/…" },
  "content": {
    "version": "2026.11.10",
    "url": "https://cdn.whiskerhero.example/content/2026.11.10/manifest.json",
    "sha256": "ab12…",
    "sizeBytes": 1843200,
    "minClientVersion": "1.4.1",
    "rolloutPct": 25,
    "inBucket": true,
    "hotApplyTables": ["localization", "shop_products", "shop_packages", "inbox_templates"]
  },
  "minContentVersionByClient": { "1.4.0": "2026.10.20", "1.4.1": "2026.11.03", "1.4.2": "2026.11.03" },
  "killSwitches": { "shopEnabled": true, "summonEnabled": { "WEAPON": true, "SKILL": true, "COMPANION": true, "RUNE": false }, "dungeonsEnabled": true, "cloudSaveEnabled": true, "adsEnabled": false, "guildEnabled": false, "leaderboardEnabled": false },
  "maintenance": { "active": false, "messageKey": null, "until": null },
  "banners": [ { "banner": "WEAPON", "openFrom": 0, "openUntil": null }, { "banner": "COMPANION", "openFrom": 1793000000, "openUntil": null } ],
  "events": [],
  "limits": { "summonPerMinute": 20, "saveUploadsPerDay": 400 },
  "features": { "stageSelect": true, "tutorialV2": false }
}
```

- `inBucket = hash(playerId || content.version) % 100 < rolloutPct`. Stable per version; raising `rolloutPct` adds players, never removes them.
- Content bundles apply only at next cold boot except `hotApplyTables` (01 section 8.12).
- `minContentVersionByClient` lets the server refuse sim-sensitive requests from a client running stale content (`CONTENT_TOO_OLD`) without blocking the client entirely.
- Kill switches are read by view-models at every config refresh; none touches `GameState`.

### 6.2 Publishing

Config and content are published through the admin API (`POST /admin/config` with the payload, `created_by` from SSO), stored as a new `config_versions` row, activated with `POST /admin/config/{id}/activate`. Activation is a single row flip, so rollback is `activate` on the previous id (section 8.5 runbook in 10). Content bundles are uploaded to the CDN bucket by the exporter pipeline (09) under their version path before the config that references them is activated; the server verifies the manifest's `sha256` at activation (`CONTENT_HASH_MISMATCH` 400) so a config can never point at a bundle that is not there.

Version rules: client semver + build; content `YYYY.MM.DD[.n]`; save schema integer; API `v1` in the path, additive changes only within `v1`, a breaking change is `v2` beside it (10 compatibility matrix).

## 7. Anti-cheat

### 7.1 Principles

The client sim is authoritative for everything except summons, diamonds from money, and time (00 section 4). Anti-cheat therefore does not try to prevent client-side editing; it bounds what editing can gain, detects it from uploads, and reserves hard action for cases that touch other players or money. Nothing here blocks gameplay in real time; checks run after upload.

### 7.2 Plausibility checks on `PUT /save`

Each check yields `(flag, severity, evidence)`; severity `info | suspicious | certain`. Named in 02 section 10.2; bounds here.

| Flag | Rule | Severity |
|---|---|---|
| `HMAC_INVALID` | Envelope fails against the device key. | `suspicious` (a restored OS backup from another device also fails; two in a row from the same device is `certain`) |
| `SEQ_REGRESSION` | `saveSeq` below the stored value for another device of the same player. | `info` (normal with two devices) |
| `CLOCK_AHEAD` | `lastSavedServerTime > now + CLOCK_MAX_SKEW_ACCEPT_S`. | `suspicious` |
| `STAGE_VS_TIME` | `HighestCleared > botStage(daysSinceCreated) * STAGE_BOUND_MULT` where `botStage` is the whale-policy curve from the 10 acceptance table (the fastest legitimate path), `STAGE_BOUND_MULT = 1.5`. | `suspicious`; `certain` above `3x` |
| `DPS_VS_STAGE` | Re-derive DPS with `Whisker.Sim.Power.Dps(state, db)` from the uploaded state. If `DPS * BOSS_TIME_TICKS / TICK_HZ < BossHp(HighestCleared) * DPS_BOUND_MARGIN` with `DPS_BOUND_MARGIN = 0.5` (allowing for fish missiles and tap zap the formula underestimates), the player could not have cleared it. | `certain` |
| `GOLD_DELTA_BOUND` | `Stats.GoldEarned - prev.GoldEarned > maxGoldRate(HighestCleared, DPS) * elapsedSec * GOLD_BOUND_MULT`, `GOLD_BOUND_MULT = 2` (covers `GOLD_GAIN` training and boss multipliers). | `suspicious`; `certain` above `10x` |
| `DIAMOND_LEDGER_MISMATCH` | `Wallet.Diamond > serverLedgerBalance + clientSideAllowance` where `clientSideAllowance = DIAMONDS_PER_DAY_F2P * daysSinceLastUpload + TUTORIAL_DIAMONDS (once) + firstClearDiamondsPossible(HighestCleared)`. Client-side diamond sources (quests, login, first clears) are bounded per day; anything above is impossible. | `certain` |
| `ITEM_WITHOUT_PULL` | Any collection `Copies` exceeds `summon_log` count + `granted_items` for that id. | `certain` |
| `PITY_MISMATCH` | Client pity differs from `banner_pity`. | `info` |
| `DUPLICATE_REQUEST_ID` | `X-Request-Id` previously used by another player. | `certain` |
| `OFFLINE_WINDOW_OVERLAP` | `Offline.LifetimeOfflineSecCredited` grew by more than the wall-clock gap between the last two uploads plus `OFFLINE_CAP_H * 3600`. | `suspicious` |
| `STREAM_COUNTER_MISMATCH` | 02 P15 invariants fail on the uploaded body (the client's own validator should have fixed them; failing means the client is modified). | `certain` |
| `TAINTED` | `DebugTainted` true on a release client version. | `certain` |
| `REPLAY_DIVERGENCE` | Spot check (section 7.3): re-simulating the command log from the previous upload does not reproduce the uploaded state hash. | `certain` |
| `RESEARCH_WITHOUT_COINS` | `sum(research cost paid) > Stats.FishCoinEarned`. Same pattern for Rock Candy vs awakening cost. | `certain` |

### 7.3 Re-simulation spot check

The server references `Whisker.Sim`, so it can replay. Sampling: 1% of uploads at random plus 100% of uploads carrying any `suspicious` flag. The server requests the command log (`GET` is impossible server-side; the next `PUT /save` response carries `"requestCmdLog": true` and the client includes `saves/cmdlog.ndjson` in its next upload as a second part). Replay: load previous blob, apply `ApplyServerTime`/`Advance`/commands in log order with `LocalSummonService` replaced by a replayer that injects the logged `ApplySummonResult` payloads, compare the resulting state hash to the uploaded one. Divergence -> `REPLAY_DIVERGENCE`. Cost: one replay of a 2 h session is ~72,000 ticks, under 2 s on one core; at 1% sampling and 100k DAU that is ~2,000 replays a day, negligible.

Dungeon correlation: for every `dungeon_seeds` row with `result null` older than its TTL, compare `BestFloor[kind]` and `Stats.DungeonClears` between the two uploads bracketing the issue time; a `BestFloor` increase with no issued seed for that floor is `DUNGEON_WITHOUT_SEED` (`certain`).

### 7.4 Action matrix

| Trigger | Action | Player-visible effect | Record |
|---|---|---|---|
| Any `info` | `log` | none | `saves.flags` |
| First `suspicious` | `log` | none | `moderation_actions(log)` |
| 3 `suspicious` in 7 days, or 1 `certain` of `STAGE_VS_TIME`/`GOLD_DELTA_BOUND`/`OFFLINE_WINDOW_OVERLAP` | `soft_flag` | Excluded from leaderboards (P1) and from tuning datasets; inbox notice "Unusual activity detected on your account" with appeal link | `moderation_actions(soft_flag)` |
| `certain` `DIAMOND_LEDGER_MISMATCH` | `reconcile` | `Wallet.Diamond` is overwritten by the ledger on the next load (`diamondReconcile` in the `PUT` response, applied by the client as `Wallet.Apply(delta, ADMIN)`); no other penalty the first time | `diamond_ledger(ADMIN, "reconcile")` |
| `certain` `ITEM_WITHOUT_PULL`, `DPS_VS_STAGE`, `REPLAY_DIVERGENCE`, `STREAM_COUNTER_MISMATCH`, `RESEARCH_WITHOUT_COINS`, `DUNGEON_WITHOUT_SEED` | `rollback` | Save of record reverts to the last unflagged `save_history` entry; client gets `409 SAVE_ROLLED_BACK` on next upload and must download; inbox notice with appeal link | `moderation_actions(rollback)` |
| 2 rollbacks in 30 days, or `DUPLICATE_REQUEST_ID`, or `TAINTED` on a release build, or refund abuse >= 5 | `ban` | `accounts.status = banned`; all routes return `403 ACCOUNT_BANNED` with appeal url; purchases remain refundable through the store | `moderation_actions(ban)` |

Never automatic: deleting an account, clawing back items, charging money. A ban is reviewed by a human within 72 h (queue in the admin UI); an unreviewed ban auto-downgrades to `soft_flag` after 72 h.

### 7.5 Appeal path

`SCR_SETTINGS` -> "Appeal" opens a web form (support site) prefilled with the support code. Support sees in the admin UI: the flags with evidence, the `save_history` diff, the replay result, purchase history. Outcomes: `appeal_granted` (restore the rolled-back save from history, or unban), `appeal_denied`. Response SLA 5 business days, stated on the form. All actions and outcomes in `moderation_actions`.

## 8. Live ops

### 8.1 Event scheduling data (P1 events, MVP banner schedule)

Schedules are config, not code. `config.banners[]` and `config.events[]` are arrays of windows evaluated against `serverTime`:

```json
{ "id": "evt_2026_12_catmas", "kind": "RATE_UP", "titleKey": "event.catmas.title",
  "startAt": 1796400000, "endAt": 1797004800, "showFrom": 1796313600,
  "params": { "banner": "WEAPON", "featuredItemId": "wpn_029", "featuredWeightMult": 3.0, "rateTableOverrideId": "bl_catmas" },
  "regions": null, "minStage": 50 }
```

Kinds (P1): `RATE_UP` (featured item weight inside its rarity; L/M totals unchanged), `LOGIN_CALENDAR` (grants by day index), `DOUBLE_DUNGEON` (reward multiplier by kind), `SEASON_PASS`. `showFrom < startAt` lets the client show a teaser. The server enforces windows on `/summon/pull` (`BANNER_CLOSED`); the client only displays. Event-specific rate tables are `banner_levels` rows with an `eventId` (09) and are published through `/summon/rates?event=…` with their own `rateTableHash`.

### 8.2 Mail blasts

`POST /admin/mail` with a `mail_blasts` row: audience, grants, keys, `sendAt`, `expiresAt`. A worker materialises `inbox_items` for the audience in batches of 1,000 at `sendAt` (`sent_count` tracks progress; idempotent by `(mail_blast_id, player_id)` unique index). Audiences: all, region list, player id list (CSV upload), `minStage`/`maxStage`, `createdBefore`, `hasPurchased`. Preview mode returns the audience count without sending. Grants are capped per blast by a two-person rule: a blast granting more than `MAIL_GRANT_APPROVAL_DIAMONDS = 1000` per player, or to more than 10,000 players, needs a second admin's `approve` before the worker runs.

### 8.3 Compensation flow

1. Incident declared (10 runbooks) with an incident id.
2. Affected audience computed by query (e.g. players with a `PUT /save` 5xx between `t0` and `t1`, or players who pulled on a misconfigured banner).
3. Compensation is a mail blast referencing the incident id in `source_ref`, `expiresAt` 30 days, body key written per incident (localised before send).
4. For summon misconfiguration specifically: pulls made under the wrong table are listed from `summon_log` by `rate_table_hash`; compensation is a refund of the diamonds (`REFUND` ledger rows via inbox) and the items are kept.
5. Post-incident, the blast's `sent_count` and claim rate are reported in the postmortem.

### 8.4 Maintenance mode

`POST /admin/maintenance { "active": true, "messageKey": "maint.db_upgrade", "until": 1793680000 }` sets the Redis `maint` key. Effects: every route except `/config` and `/time` returns `503 MAINTENANCE`; the client shows the block screen with a countdown and polls `/config` every `MAINT_POLL_S`. Players already in-game keep playing offline (client-authoritative sim) and their uploads queue; summons and shop are blocked. Scheduled maintenance is announced via `config.maintenance.until` set in advance with `active: false`, which the client shows as a banner 1 h before.

### 8.5 Admin API and UI

Internal only (`admin` auth: VPN + SSO, every call logged with actor). Routes: `/admin/players/{id}` (profile, flags, save summary, purchases, ledger, summon history), `/admin/players/{id}/save/restore {saveSeq}`, `/admin/players/{id}/grant {grants, reasonKey}` (writes an inbox item, two-person rule above 1,000 diamonds), `/admin/players/{id}/moderate {action, reason}`, `/admin/players/{id}/link-device` (recovery), `/admin/config`, `/admin/config/{id}/activate`, `/admin/mail`, `/admin/maintenance`, `/admin/coupons` (P1). The UI is a thin internal web app over these routes; it is not a product and ships as a static SPA behind the same SSO.

## 9. Infrastructure

### 9.1 Topology

```mermaid
flowchart LR
    Client -->|HTTPS| CF[CloudFront CDN]
    CF -->|/content/*| S3C[(S3 content bucket)]
    Client -->|HTTPS api.| ALB[Application Load Balancer]
    ALB --> API1[ECS Fargate: Whisker.Server x N]
    API1 --> RDS[(RDS PostgreSQL 15 Multi-AZ)]
    API1 --> Redis[(ElastiCache Redis, replica)]
    API1 --> S3S[(S3 saves + receipts, SSE-KMS)]
    API1 --> Apple[App Store Server API]
    API1 --> Google[Play Developer API]
    Apple -->|notifications v2| ALB
    Google -->|Pub/Sub push| ALB
    Worker[ECS Fargate: Whisker.Server --worker] --> RDS
    Worker --> S3S
    API1 --> OTel[OpenTelemetry collector]
    OTel --> Metrics[CloudWatch / Grafana]
    OTel --> Logs[CloudWatch Logs -> OpenSearch]
    Warehouse[(Analytics warehouse: Firebase export + events table)] --- RDS
```

- One container image, two roles by command-line flag: `api` (stateless, horizontally scaled on CPU + request latency) and `worker` (mail blasts, plausibility replays, purges, refund polls; one instance, leader lock in Redis).
- PostgreSQL via Npgsql with connection pooling (RDS Proxy at 100k DAU). Migrations are SQL files applied by the deploy pipeline before the new image receives traffic (expand/contract pattern: never drop a column in the same release that stops writing it).
- S3 buckets per environment: `whisker-saves-{env}` (versioning on, SSE-KMS, lifecycle: non-current versions expire after 7 days), `whisker-receipts-{env}` (object lock, 7 years), `whisker-content-{env}` (public read via CloudFront OAC only).
- Redis is a cache and a lock service only; losing it loses rate-limit counters and in-flight summon locks (the idempotency table in Postgres still prevents double rolls).

### 9.2 Environments

| Env | Purpose | Data | Store side |
|---|---|---|---|
| `dev` | Every merge to `main` deploys here. Shared by the team. | Synthetic; reset weekly | Apple Sandbox, Google license testers |
| `stage` | Release candidates; closed beta; load tests. Mirrors prod sizing at 1/4. | Anonymised copy of prod schema, no prod data | Apple Sandbox, Google internal testing track |
| `prod` | Players. | Real | Production |

Each env is a separate AWS account (billing isolation and blast radius). Config and content are promoted `dev -> stage -> prod` by activating the same `config_versions` payload; content bundle versions are identical bytes across envs (hash checked).

### 9.3 Secrets

AWS Secrets Manager, read at container start into memory, rotated by schedule: database credentials (RDS-managed rotation, 30 days), JWT signing key (RSA, two active keys for rotation, `kid` in the JWT header, 90 days), Apple App Store Server API private key (`.p8`, manual rotation, yearly), Google service account JSON (yearly), provider pepper for `account_links` hashing (never rotated; rotating would orphan links), device HMAC keys (in Postgres, column-encrypted with a KMS data key), Redis auth token. No secret in an environment variable, image layer, or log. The client has no secrets at all (store validation is server-side).

### 9.4 Logging and metrics

Structured JSON logs via `Microsoft.Extensions.Logging` + OpenTelemetry: `ts, level, route, status, latencyMs, playerIdHash, deviceId, requestId, code, traceId`. Never logged: tokens, receipts, HMAC keys, display names, IP beyond `/24`. Retention 30 days hot. Traces sampled 10% (100% for 5xx).

Metrics (Prometheus naming), by route where applicable:

| Metric | Type |
|---|---|
| `http_requests_total{route,status}` | counter |
| `http_request_duration_seconds{route}` | histogram |
| `summon_pulls_total{banner,rarity}` | counter (realised rates dashboard) |
| `summon_lplus_rate_5m{banner}` | gauge (derived) |
| `iap_validate_total{platform,status}` | counter |
| `iap_webhook_total{platform,type}` | counter |
| `save_upload_bytes` | histogram |
| `save_flags_total{flag,severity}` | counter |
| `moderation_actions_total{action}` | counter |
| `idempotency_replays_total{route}` | counter |
| `db_pool_in_use`, `db_query_duration_seconds{name}` | gauge, histogram |
| `redis_lock_wait_seconds` | histogram |
| `worker_job_duration_seconds{job}`, `worker_job_failures_total{job}` | histogram, counter |
| `config_active_version`, `content_active_version_info` | gauge |

### 9.5 Alarm list

Each alarm has an owner rotation (the on-call) and a runbook id (10).

| Alarm | Condition | Severity | Runbook |
|---|---|---|---|
| API 5xx rate | `5xx / total > 1%` over 5 min | page | RB-API-5XX |
| API p99 latency | `p99 > 1 s` on `/summon/pull` or `/save` PUT over 10 min | page | RB-LATENCY |
| Summon L+ rate drift | realised `lplus_rate_1h` outside `[0.028, 0.055]` with >= 2,000 pulls in the hour (expected ~0.041 with pity) | page | RB-SUMMON-RATES |
| Summon errors | `INSUFFICIENT_FUNDS` share `> 20%` of pulls over 15 min (client balance display broken) | ticket | RB-CLIENT-BALANCE |
| IAP validation failures | `iap_validate_total{status=invalid}/total > 5%` over 30 min, or `STORE_UNAVAILABLE > 10` in 5 min | page | RB-IAP |
| IAP grant lag | any `purchases.status = pending` older than 10 min | page | RB-IAP |
| Refund spike | refunds in 24 h `> 3x` the 7-day median | ticket | RB-REFUND |
| Save conflicts | `409 SAVE_CONFLICT / PUT > 5%` over 1 h | ticket | RB-SAVE-CONFLICT |
| Save rejections | `SAVE_MALFORMED > 0.1%` of PUTs over 1 h (a client build writing bad files) | page | RB-SAVE-CORRUPT |
| Plausibility flood | `save_flags_total{severity=certain} > 50/h` (a cheat tool, or a false-positive bound after a content change) | page | RB-ANTICHEAT |
| Rollbacks | `moderation_actions_total{action=rollback} > 20/h` | page | RB-ANTICHEAT |
| DB | CPU `> 80%` 10 min; free storage `< 20%`; replica lag `> 30 s`; pool exhaustion events `> 0` | page | RB-DB |
| Redis | memory `> 80%`; evictions `> 0`; primary failover event | ticket / page | RB-REDIS |
| Worker | any job failing 3 consecutive runs; mail blast `sent_count` stalled 15 min | ticket | RB-WORKER |
| Config | active config changed (informational), content `sha256` mismatch at activation | notify / page | RB-CONFIG |
| Certificate / key expiry | TLS, Apple `.p8`, JWT key `< 14 days` | ticket | RB-SECRETS |
| Cost | daily AWS spend `> 1.5x` 30-day average | ticket | RB-COST |
| Client-side (from analytics) | `save_corrupt` events `> 0.1%` of sessions/day; crash-free sessions `< 99.5%` | page | RB-SAVE-CORRUPT / RB-CLIENT-CRASH |

### 9.6 Cost estimate

Assumptions: 2 sessions/day/DAU; per session 1 `/config`, 1 `/auth/refresh`, 1 `/save/meta`, 1 `/summon/state`, 1 `/inbox`, ~12 `PUT /save` (2 h active per day across 2 sessions at one coalesced upload per 60 s is 120/day; most players are far below the 2 h assumption, so 24/day average), 3 summon batches/day, 1 dungeon seed. Roughly 40 API calls/DAU/day, 60% of them save uploads of ~30 KB. Prices are US-East list prices as understood in late 2026 and need re-checking before budgeting; treat as order of magnitude.

| Item | 10k DAU | 100k DAU |
|---|---|---|
| Requests/day | ~400k (avg 5 rps, peak ~25 rps) | ~4M (avg 46 rps, peak ~250 rps) |
| ECS Fargate API (0.5 vCPU / 1 GB tasks) | 2 tasks, ~$60 | 8 tasks, ~$240 |
| ECS Fargate worker | 1 task, ~$30 | 1 task (1 vCPU / 2 GB), ~$60 |
| RDS PostgreSQL Multi-AZ | db.t4g.medium, 100 GB gp3, ~$130 | db.r6g.large + RDS Proxy, 500 GB, ~$520 |
| ElastiCache Redis (primary + replica) | cache.t4g.small x2, ~$50 | cache.m6g.large x2, ~$300 |
| S3 saves (PUT requests dominate: 240k/day -> 7.2M/mo at $0.005/1k; storage 10k x 10 x 30 KB = 3 GB) | ~$40 | ~$380 (72M PUTs) |
| S3 receipts, content; CloudFront (bundle 2 MB, ~1 download/DAU/week) | ~$10 | ~$60 |
| ALB | ~$25 | ~$60 |
| NAT gateway (store API egress) | ~$40 | ~$90 |
| CloudWatch logs + metrics (10% trace sampling) | ~$40 | ~$250 |
| Secrets Manager, KMS, Route53, WAF | ~$30 | ~$60 |
| Firebase Analytics | free | free (BigQuery export ~$50) |
| **Total / month** | **~$455** | **~$2,070** |
| Per DAU per month | ~$0.046 | ~$0.021 |

Levers if cost matters: raise `CLOUD_UPLOAD_MIN_SEC` to 300 (cuts S3 PUTs and API load by ~4x; the price is up to 5 min of lost progress on a device loss, not a save loss, since the local save is unaffected); drop replica Redis at 10k; Graviton for Fargate (-20%).

## 10. Compliance

### 10.1 COPPA and age gate

A neutral age screen at first launch (`SCR_TITLE`, before any account call): "When were you born?" month/year picker with no default and no hint of a threshold. Under 13 (or the regional digital-consent age: 16 in several EU states, 14 in Korea; table in `compliance_ages` content) sets `ageGate = 1`:

| Area | `ageGate = 1` behaviour |
|---|---|
| Account linking | Allowed (Apple/Google handle parental consent on their side); no email collection. |
| Analytics | Firebase in `analytics_storage = denied` mode; backend economy events still recorded (needed for the service) with `ageGate` in the envelope; no advertising id. |
| Ads (P1) | Rewarded ads disabled. |
| Chat (P1) | Guild chat read-only. |
| Display name | Auto-generated only, not editable. |
| Push notifications (P1) | Off. |
| IAP | Allowed; the stores enforce parental controls. |

The age gate value is never shown after it is set and cannot be changed by the player (`AGE_GATE_LOCKED`); support can change it with proof.

### 10.2 GDPR / CCPA

- Lawful basis: contract (saves, purchases, summons), legitimate interest (anti-cheat, analytics on hashed ids), consent (Firebase advertising features, off by default in the EU/UK/CA). Consent is recorded in `accounts` (`consent_analytics_at`, P1 column) and sent to the Firebase SDK before initialisation.
- Export and delete: 02 section 13. `SCR_SETTINGS` -> Privacy -> "Download my data" / "Delete my account". Delete completes within `GDPR_DELETE_GRACE_DAYS + 1`, well inside the 30-day requirement. CCPA "Do not sell" is moot (nothing is sold) but the toggle exists and sets the Firebase ads flag off.
- Processors list in the privacy policy: AWS (hosting, US-East with EU replica P1), Google (Firebase Analytics, Play), Apple (App Store), the crash reporter.
- Data minimisation: no email, no phone, no precise location; IP truncated to /24 in logs; `providerUserId` hashed with a pepper.

### 10.3 Loot-box odds disclosure

Summons are paid random rewards. Rules by jurisdiction, with what we do:

| Jurisdiction | Requirement | Implementation |
|---|---|---|
| Apple App Store (Guideline 3.1.1) | Disclose odds of receiving each type of item before purchase. | `SCR_SUMMON` "Rates" tab shows `/summon/rates` for the current banner level; the pull button is never reachable without the tab being one tap away. Pity and the 10-pull guarantee are stated in plain words. |
| Google Play (Developer Policy, Loot boxes) | Disclose odds prior to purchase. Age rating questionnaire (IARC) answered "yes" to paid random items. | Same screen. The IARC answer is in the store checklist (10). |
| Japan (JOGA / CESA guidelines; Act against Unjustifiable Premiums) | **Complete gacha (kompu gacha) prohibited**: a reward conditioned on collecting a specific set of random items. Publish rates; show expected cost to obtain a specific item; recommended monthly spend guidance for minors. | The weapon collection passive (`WEAPON_PASSIVE_PCT`) pays per owned item, never for completing a set; achievements reward *count* of collected items, never a named set (06 must keep this). Rates page shows "expected pulls to Legendary+" from `effective`. Display `PITY_HARD` cost in diamonds and local currency. |
| China (2017 MIIT rules, 2024 draft) | Publish names, odds and count of random items; no pay-to-reroll; minors' spend and time limits; real-name verification. | Not a launch market (00 section 11 soft launch markets exclude it). `/summon/rates` already provides the data; real-name and minor limits would be P2 if China is ever targeted. |
| Belgium (Gaming Commission, 2018) | Paid loot boxes are gambling and require a licence. | Region `BE` (from store account country): diamond-paid summons disabled (`summonEnabled` kill switch by region in config; the server enforces via `BANNER_CLOSED` with `details.reason = REGION_RESTRICTED`); ticket summons from quests remain (not paid). Diamond packs remain purchasable for shop and research P1 sinks; if that is judged too close, the simpler option is to not list in Belgium. Decision at soft launch (open question 5). |
| Netherlands | 2022 court ruling reversed the 2020 fine; currently permitted with disclosure. | Treated as default EU. |
| South Korea (Game Industry Promotion Act amendment, 2024) | Odds disclosure in-game, on the store page, and in ads; pity systems must be disclosed; per-item odds. | Rates page per item (the `items[]` list with weights), pity text, and the store listing's odds page generated from the same JSON. |
| UK / EU generally | Disclosure plus age rating (PEGI "In-Game Purchases (Includes Random Items)"). | PEGI/IARC answers in the store checklist. |

Technical commitments that make the above checkable: every pull is logged with the `rate_table_hash` it used (4.4); the published rates endpoint serves the exact table in content with the same hash; a nightly job computes realised rarity frequencies per banner level over the trailing 30 days and alarms if any rarity deviates from the published rate by more than 3 standard errors (`RB-SUMMON-RATES`).

### 10.4 Privacy-policy data inventory

| Data | Purpose | Storage | Retention | Shared with |
|---|---|---|---|---|
| `playerId`, `deviceId`, device model/OS | Account, cloud save, anti-cheat | Postgres | Account lifetime + grace | none |
| Platform identity (hashed) | Login | Postgres | Account lifetime | Apple/Google (they issue it) |
| Device HMAC key | Save integrity | Postgres (encrypted) | Device lifetime | none |
| Save blobs | Cloud save | S3 | Current + last 10 | none |
| Purchase records, receipts | Grant, refunds, tax | Postgres, S3 | 7 years | Apple/Google (verification) |
| Summon log | Audit, odds compliance | Postgres | 7 years | none |
| Inbox items | Grants | Postgres | Until claimed/expired + 90 days | none |
| Moderation actions | Anti-cheat, appeals | Postgres | Account lifetime | none |
| Analytics events (hashed id) | Balancing, product analytics | Firebase, warehouse | 25 months raw | Google (Firebase) |
| Crash reports | Stability | Crash reporter | 90 days | the crash reporter vendor |
| Server logs (IP /24, request ids) | Operations, security | CloudWatch | 30 days | none |
| Age gate value | Compliance | Postgres, JWT | Account lifetime | none |
| Display name, avatar | Profile, guild, leaderboard (P1) | Postgres | Account lifetime | other players (P1) |

## 11. P1 service outlines

Shapes fixed now so MVP code leaves room; no detailed design.

- **Guild**: `POST /guild`, `POST /guild/{id}/join`, `POST /guild/{id}/leave`, `GET /guild/{id}`, `GET /guild/{id}/chat?since=`, `POST /guild/{id}/chat` (rate 10/min, profanity filter, `ageGate = 1` read-only), guild boss as a shared HP pool decremented by server-validated dungeon-style runs (seed per attempt from `/dungeon/seed` with `kind = GUILD_BOSS`). `GUILD_COIN` in `diamond_ledger`-style `guild_coin_ledger`.
- **Leaderboard**: no submit endpoint; boards are derived from `saves.summary` on upload (3.1 step 8) so the only input is the plausibility-checked save. `GET /leaderboard/{board}?around=me&limit=50`. Soft-flagged players are excluded; a rollback removes the entry. Weekly reset at `WeekKey` change; top-100 rewards by mail blast.
- **Coupon**: `POST /coupon/redeem { code }` -> inbox item. Codes in `coupons(code, grants, max_redemptions, per_player_limit, expires_at)`; `coupon_redemptions(code, player_id)` unique. Rate 5/min, 20/day; brute-force protection by per-IP limit and 12-char codes.
- **Rewarded Ads**: server-side verification callbacks from the ad network (SSV) -> `ad_rewards(player_id, network_tx_id unique, reward)` -> inbox or direct grant through `/inbox/claim`. Daily cap in config. `ageGate = 1` disabled.
- **Push notifications**: `devices.push_token`; worker sends "Offline rewards are capped" at `OFFLINE_CAP_H` and event starts; opt-in in `SCR_SETTINGS`.

## 12. M0 note

M0 has no server. Every service above is reached through an interface in `Whisker.Runtime.Services` with a local implementation (01 section 1.2): `ServerClock(api: null)`, `LocalSummonService` (section 4.6), `LocalSaveStore`, `LocalInboxService` (empty list, `claim` -> `INBOX_NOT_FOUND`), `DisabledReceiptValidator`, `LocalConfigService` (bundled manifest, all switches on, no maintenance), `LocalDungeonSeedService`. The bot harness uses the same set with `FakeClock`. Nothing in the client branches on "local or remote"; the composition root picks. When the server arrives, the only client change is `BackendMode.Remote` and the base URL.

## Open questions

1. **Summon tickets server-side.** Section 4.3 assumes summon tickets are a server ledger from MVP (so `PAYMENT_MISMATCH` can be decided server-side). 06 owns ticket sources; if it keeps tickets client-side, the server must trust the client's ticket count on `/summon/pull`, bounded by `TICKETS_PER_DAY_MAX`. Owner: 06. Default: server ledger for summon tickets; dungeon tickets stay client-side.
2. **`DPS_BOUND_MARGIN = 0.5`.** Fish missiles, tap zap and companion burst are not in the closed-form DPS the server re-derives. 0.5 may be too loose or too tight. Owner: 03 (provides `Power.Dps` and a "max burst multiplier" bound). Default: 0.5 until 10's bot harness reports the realised ratio of effective to formula DPS.
3. **Refund item clawback.** Section 5.4 keeps items from a refunded pull. Owner: product. Default: keep items, escalate repeat refunds.
4. **Diamond reconciliation on first mismatch** silently overwrites the client balance. Should the client show a notice? Owner: 07. Default: show a one-line toast with the delta the first time, nothing afterwards.
5. **Belgium**: disable paid summons or do not list. Owner: product/legal at soft launch. Default: do not list in Belgium for soft launch; decide for global launch.
6. **Age-of-consent table** (`compliance_ages`) and whether to apply the regional age rather than 13 everywhere. Owner: legal. Default: 13 everywhere except where the region's digital-consent age is higher.
7. **Sampling rate of re-simulation** (1%). Owner: ops after measuring worker cost. Default: 1% + all suspicious.
8. **Per-region day rollover** (00 decision 8 names it as a P1 question). Server-side implication: `WeekKey`/`DayKey` would need a per-account timezone; leaderboards and daily resets would then differ by region. Owner: 06/08 at P1. Default: stay on 00:00 UTC.
9. **Guest cloud save before any server contact** (01 open question 7): the first `/auth/guest` call must adopt a save that already has `DeviceId` set and `PlayerId = Guid.Empty`; the server assigns `playerId`, the client rewrites the save root and uploads with `If-Match: *`. Default: supported as described.

## Acceptance criteria

1. `SummonAlgorithm` reproduces every row of `Fixtures/Summon/vectors.json` in `Whisker.Sim.Tests`, `Whisker.Server.Tests` and `tools/gacha_model.py` (10 CI gate).
2. One million simulated pulls at level 1 through `SummonAlgorithm.Batch` with pity realise L+ in `[0.040, 0.043]` and never exceed 80 pulls between L+ results; the per-pull L+ probability at `pity = 79` is exactly 1.0.
3. 10-pull batches: in 100,000 simulated batches with slots 1-9 forced below EPIC, slot 10 is EPIC or better 100% of the time, and the stream/CSPRNG consumption is exactly 30 uniforms per batch.
4. `/summon/pull` replay: a request that times out after commit and is retried with the same `X-Request-Id` produces one `diamond_ledger` row, `count` `summon_log` rows, and a byte-identical response with `X-Idempotent-Replay: true`.
5. Two concurrent `/summon/pull` requests for the same player with different request ids produce one `200` and one `409 IDEMPOTENCY_IN_FLIGHT` (or two sequential `200`s), never two rolls against the same `pity_before`.
6. `/summon/pull` with `payment = DIAMOND` while the player holds enough tickets returns `409 PAYMENT_MISMATCH` and charges nothing.
7. Every `summon_log` row can be reproduced: `Pull(tableFor(rate_table_hash), pity_before, roll_u, u2, u3)` yields the logged rarity (test over 10,000 random rows in stage).
8. `/summon/rates` returns a table whose rarities sum to 1 within 1e-9 and whose `L` and `M` equal the level-1 values at every level.
9. Apple: a valid sandbox JWS grants once; the same `transactionId` again returns `ALREADY_GRANTED` with the same `inboxItemId`; a JWS with a mismatched `appAccountToken` returns `RECEIPT_ACCOUNT_MISMATCH` and writes a `moderation_actions(log)` row.
10. Google: a `purchaseState = 1` (canceled) token returns `RECEIPT_INVALID`; a valid token is acknowledged within the handler; the RTDN `ONE_TIME_PRODUCT_CANCELED` path writes the `REFUND` ledger row and clamps a negative balance to 0 with the `refund_debt` recorded.
11. `/inbox/claim` twice returns `409 INBOX_ALREADY_CLAIMED` carrying the original grants and writes exactly one ledger row.
12. `PUT /save` with a stale `If-Match` returns `409 SAVE_CONFLICT` with `serverMeta`; with a lower `saveSeq` from the same device returns `409 SAVE_STALE`; with an invalid HMAC returns `200` with `HMAC_INVALID` in `flags`.
13. Plausibility: a fixture save with `HighestCleared = 2000` at day 1 is flagged `STAGE_VS_TIME certain`; a fixture whose DPS cannot clear its highest boss within `BOSS_TIME_TICKS / DPS_BOUND_MARGIN` is flagged `DPS_VS_STAGE`; a fixture with `Wallet.Diamond = 99999` and an empty ledger returns `diamondReconcile.applied = true` and the next `GET /save/meta` summary shows the ledger balance.
14. Re-simulation of a 2 h bot session's command log from its previous upload reproduces the uploaded state hash; mutating one `TrainStat` in the log produces `REPLAY_DIVERGENCE`.
15. Action matrix: three `suspicious` flags within 7 days on a test account produce one `soft_flag` and an inbox notice; a `certain` `ITEM_WITHOUT_PULL` produces a `rollback` whose next `PUT` returns `409 SAVE_ROLLED_BACK` and whose `GET /save` serves the previous history entry.
16. `GET /config` without a JWT omits `inBucket`; with a JWT, 1,000 synthetic players at `rolloutPct = 25` land in the bucket between 20% and 30% of the time, and the same player is in the same bucket on repeated calls.
17. Kill switch `summonEnabled.WEAPON = false` makes `/summon/pull` return `503 SUMMON_DISABLED` within 60 s of activation.
18. Maintenance mode returns `503 MAINTENANCE` on `/summon/pull` and `200` on `/config` and `/time`.
19. Refresh-token reuse: using a rotated-out refresh token returns `401 TOKEN_REUSED` and revokes the whole family (the current token also stops working).
20. GDPR: after the grace period the nightly job leaves no row referencing a deleted non-purchaser's `playerId` in any table; for a purchaser, exactly `purchases`, `summon_log` and `diamond_ledger` rows remain under the tombstone id.
21. Load (10): `/summon/pull` at 500 rps and `PUT /save` at 200 rps on stage sizing hold p99 under 300 ms and 500 ms respectively for 10 minutes with zero 5xx.
22. The realised-rate nightly job on 30 days of stage data reports every rarity within 3 standard errors of the published table; a deliberately mis-set `banner_levels` row in stage triggers `RB-SUMMON-RATES` within one job run.
23. Every error `code` named in this spec has a localization key (09 validator), and every endpoint has an OpenAPI entry generated from the controllers that matches the request/response JSON shown here (contract test).
24. A Local-mode client build makes zero network calls during a 20-minute PlayMode session (network interceptor assertion), proving the M0 note.

## Constants introduced

Engineering/operational constants, code- or config-resident (`Whisker.Server` `appsettings` or `config_versions.payload.limits`), not `ContentDb`. `I` = same role in the sibling project; `S` = new. The tuning tool does not validate these; 10's load, contract and fault tests and the alarm thresholds are the check.

| Name | Value | Tag | Section |
|---|---|---|---|
| `IDEMPOTENCY_TTL_H` | 24 | I (00 section 8.13; sibling used 48) | 1.1 |
| `ACCESS_TOKEN_TTL_S` | 3600 | S | 2.1 |
| `REFRESH_TOKEN_TTL_DAYS` | 60 | S | 2.1 |
| `SUMMON_LOCK_TTL_S` | 10 | S | 4.3 |
| `DUNGEON_SEEDS_PER_DAY` | 10 per kind | S | 3.3 |
| `DUNGEON_SEED_TTL_S` | 600 | S (declared in 01) | 3.3 |
| `REFUND_ABUSE_COUNT` | 3 soft flag / 5 ban review, per 90 days | S | 5.4 |
| `MAIL_GRANT_APPROVAL_DIAMONDS` | 1000 | S | 8.2 |
| `STAGE_BOUND_MULT` | 1.5 suspicious / 3 certain | S | 7.2 |
| `DPS_BOUND_MARGIN` | 0.5 | S | 7.2 |
| `GOLD_BOUND_MULT` | 2 suspicious / 10 certain | S | 7.2 |
| `RESIM_SAMPLE_PCT` | 1 | S | 7.3 |
| `BAN_REVIEW_SLA_H` | 72 | S | 7.4 |
| `CLOCK_MAX_SKEW_ACCEPT_S` | 120 | S (declared in 02) | 3.1 |
| `SAVE_BLOB_MAX_BYTES` | 262144 | S (declared in 02) | 3.1 |
| `SAVE_HISTORY_KEEP` | 10 | S (declared in 02) | 3.1 |
| Rate limits | per endpoint, sections 2-5 | S | |
| Alarm thresholds | section 9.5 | S | |
| `RATE_DRIFT_SIGMA` | 3 | S | 10.3 |
| Age of digital consent default | 13 | S | 10.1 |

Used from 00 by name: base rates, `PITY_SOFT_START`, `PITY_SOFT_STEP`, `PITY_HARD`, `M_SHARE_OF_LPLUS`, `BANNER_EXP_PER_PULL`, `PULL_COST[*]`, `DIAMONDS_PER_DAY_F2P`, `TUTORIAL_DIAMONDS`, `LOGIN_DIAMONDS`, `OFFLINE_CAP_H`, `BOSS_TIME_TICKS`, `TICK_HZ`, `DAY_SECONDS`, `WEAPON_PASSIVE_PCT`. From 01: `CONFIG_CACHE_MAX_AGE_S`, `MAINT_POLL_S`, `CLOUD_UPLOAD_MIN_SEC`. From 02: `GDPR_DELETE_GRACE_DAYS`, `RETENTION_INACTIVE_DAYS`.
