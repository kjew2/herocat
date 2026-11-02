-- 0006_ledger_indexes.sql (specs/02 section 12 "Ledger and lookup indexes"; issue #294). ticket_ledger's unique
-- (player_id, request_id) index allowed one row per request, but an inbox claim with two ticket lines, claim-all and the
-- PUT /save step 5a credit (one row per reason) write several; it now guards only the summon charge, the one row a request
-- writes at most once. config_versions keeps at most one active row per env (activation clears the old row first, under
-- its advisory lock), and summon_log gets the (player_id, banner, item_id) index the per-pull collection query reads.
drop index ticket_ledger_player_id_request_id_idx;
create unique index on ticket_ledger (player_id, request_id) where request_id is not null and ref_type = 'summon';
create unique index on config_versions (env) where active;
create index on summon_log (player_id, banner, item_id);
