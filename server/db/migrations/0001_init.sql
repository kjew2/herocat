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
