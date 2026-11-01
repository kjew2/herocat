-- specs/02 section 13.1: save_history keeps the last SAVE_HISTORY_KEEP = 10 blobs per player. A trigger on insert
-- deletes the player's older rows (oldest by uploaded_at, then save_seq); S3 lifecycle removes the orphaned blob keys
-- after 7 days. The 10 is SAVE_HISTORY_KEEP (a test pins it to specs/02's constants table).

create function save_history_trim() returns trigger language plpgsql as $$
begin
  delete from save_history h
  where h.player_id = new.player_id
    and h.save_seq not in (
      select k.save_seq from save_history k
      where k.player_id = new.player_id
      order by k.uploaded_at desc, k.save_seq desc
      limit 10);
  return null;
end
$$;

create trigger save_history_trim after insert on save_history
  for each row execute function save_history_trim();
