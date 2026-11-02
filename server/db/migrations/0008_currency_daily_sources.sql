-- 0008_currency_daily_sources.sql (specs/02 section 12 "Currency dashboard sources"; issue #296). The server writes its
-- own currency_changed rows (source = 'server') for the ledgers it owns, and the client reports the same changes, so each
-- (currency, reason) is counted from one source only: a server-ledger currency (DIAMOND and the four summon tickets) with a
-- server-originated reason (IAP, MAIL, ADMIN, REFUND, SUMMON) from the server's rows, everything else from the client's.
-- A change only the client sees, such as Rock Candy from MAIL or from a summon's overflow, stays a client row.
drop materialized view currency_daily;
create materialized view currency_daily as                 -- rebuilt nightly for 06's source/sink dashboards
  select player_id_hash, day_index, props->>'currency' as currency, props->>'reason' as reason,
         sum((props->>'delta')::double precision) as sum_delta
  from events
  where name = 'currency_changed'
    and (source = 'server') = (props->>'currency' in ('DIAMOND', 'TICKET_SUMMON_WEAPON', 'TICKET_SUMMON_SKILL',
                                                      'TICKET_SUMMON_COMPANION', 'TICKET_SUMMON_RUNE')
                               and props->>'reason' in ('IAP', 'MAIL', 'ADMIN', 'REFUND', 'SUMMON'))
  group by 1, 2, 3, 4;
