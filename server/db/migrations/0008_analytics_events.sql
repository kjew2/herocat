-- 0008_analytics_events.sql (specs/02 section 12 "Analytics ingest"; issue #209): the envelope columns POST
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
