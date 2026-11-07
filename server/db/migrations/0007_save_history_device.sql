-- 0007_save_history_device.sql (specs/02 section 12 "Save history per device"; issue #295). Two devices of one player can
-- upload the same saveSeq (an accepted SEQ_REGRESSION upload, specs/08 section 3.1 step 4), so a history entry is keyed by
-- the device too, as its blob key is (saves/{playerId}/{saveSeq}-{deviceId8}.sav), and neither upload replaces the other.
-- The trim still keeps SAVE_HISTORY_KEEP = 10 rows per player, now told apart by (save_seq, device_id).
alter table save_history drop constraint save_history_pkey, add primary key (player_id, save_seq, device_id);

create or replace function save_history_trim() returns trigger language plpgsql as $$
begin
  delete from save_history h
  where h.player_id = new.player_id
    and (h.save_seq, h.device_id) not in (
      select k.save_seq, k.device_id from save_history k
      where k.player_id = new.player_id
      order by k.uploaded_at desc, k.save_seq desc, k.device_id desc
      limit 10);
  return null;
end
$$;
