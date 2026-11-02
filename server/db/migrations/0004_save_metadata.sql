-- 0004_save_metadata.sql (specs/02 section 12 "Save metadata columns"; issue #198): what the cloud-save endpoints return
-- that 0001 did not keep. saves.hmac is the upload header's hmac field (64 lowercase hex, or '-'), returned in the 9.1
-- metadata; save_history keeps each upload's 9.1 summary for GET /save/history and its plausibility flags, so a rollback
-- (08 section 7.4) can find the last entry without any.
alter table saves add column hmac text not null default '-';
alter table save_history add column summary jsonb not null default '{}';
alter table save_history add column flags text[] not null default '{}';
