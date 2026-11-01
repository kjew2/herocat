-- specs/02 sections 12 and 13.1: diamond_ledger and summon_log have one partition per month, events one per day.
-- A row outside every partition fails its insert (there is no default partition, so a missing partition is loud and a
-- later range partition never collides with rows parked in a default one). ensure_partitions creates the partitions
-- whose range touches [p_from, p_until], in UTC, named <parent>_pYYYYMM (month) or <parent>_pYYYYMMDD (day), and
-- returns how many it created; rerunning it is harmless. This migration creates the current period plus a horizon;
-- the worker's partition job keeps the horizon ahead of now (specs/02 section 12). TODO(#262): that worker job.

create function ensure_partitions(p_parent regclass, p_grain text, p_from timestamptz, p_until timestamptz)
returns integer language plpgsql as $$
declare
  v_step  interval;
  v_fmt   text;
  v_lo    timestamp;
  v_hi    timestamp;
  v_stop  timestamp := p_until at time zone 'UTC';
  v_base  text;
  v_part  text;
  v_made  integer := 0;
begin
  if p_grain = 'month' then
    v_step := interval '1 month'; v_fmt := 'YYYYMM';
  elsif p_grain = 'day' then
    v_step := interval '1 day'; v_fmt := 'YYYYMMDD';
  else
    raise exception 'ensure_partitions: grain must be month or day, not %', p_grain;
  end if;
  select c.relname into v_base from pg_class c where c.oid = p_parent;
  v_lo := date_trunc(p_grain, p_from at time zone 'UTC');
  while v_lo <= v_stop loop
    v_hi := v_lo + v_step;
    v_part := v_base || '_p' || to_char(v_lo, v_fmt);
    if to_regclass(quote_ident(v_part)) is null then
      execute format('create table %I partition of %s for values from (%L) to (%L)',
                     v_part, p_parent,
                     to_char(v_lo, 'YYYY-MM-DD HH24:MI:SS') || '+00',
                     to_char(v_hi, 'YYYY-MM-DD HH24:MI:SS') || '+00');
      v_made := v_made + 1;
    end if;
    v_lo := v_hi;
  end loop;
  return v_made;
end
$$;

select ensure_partitions('diamond_ledger', 'month', now(), now() + interval '3 months');
select ensure_partitions('summon_log', 'month', now(), now() + interval '3 months');
select ensure_partitions('events', 'day', now(), now() + interval '14 days');
