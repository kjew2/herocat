-- 0010_dungeon_progress.sql (specs/02 section 12 "Dungeon seed correlation"; issue #205): POST /dungeon/seed checks the
-- floor against the last uploaded save's Dungeons.BestFloor, and PUT /save compares each upload's BestFloor with the
-- previous one (specs/08 sections 3.3, 7.3). dungeon_progress keeps, per player and kind, the BestFloor of the last upload
-- that carried Dungeons, so neither reads a save blob. The index serves the daily cap's count and the correlation.
create table dungeon_progress (
  player_id  uuid not null references accounts on delete cascade,
  kind       text not null,                                -- Dungeon name: FISH_COIN | ROCK_CANDY | EXP
  best_floor integer not null default 0,
  save_seq   bigint not null,                              -- the upload it came from
  updated_at timestamptz not null default now(),
  primary key (player_id, kind)
);
create index on dungeon_seeds (player_id, kind, day_key);
