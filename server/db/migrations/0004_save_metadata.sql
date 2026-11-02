-- specs/02 section 12 (issue #198): what the cloud-save endpoints return that 0001 did not keep. saves.hmac is the
-- upload header's hmac field (64 hex, or '-'), returned in the 9.1 metadata; save_history keeps each upload's 9.1
-- summary for GET /save/history and its plausibility flags, so a rollback can find the last unflagged entry (08 7.4).

alter table saves add column hmac text not null default '-';

alter table save_history add column summary jsonb not null default '{}';

alter table save_history add column flags text[] not null default '{}';
