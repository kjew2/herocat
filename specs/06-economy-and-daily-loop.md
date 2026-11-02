# 06 - Economy and Daily Loop

Every currency in 00 section 5.2, where it comes from and where it goes, at what rate per day for the reference F2P player; the daily and weekly rhythm (quests, dungeons, login, rollover); Offline Catch-up; the Shop and Inbox as the diamond entry points; Awakening as the Rock Candy exit. Combat, Training and Research produce or consume these currencies but their rules live in 03; summons are the main diamond sink and live in 04; the Fish Board's coin income lives in 05. This spec owns the ledger, the schedule and the grants.

Tiers covered: M0 (Wallet, Offline Catch-up), MVP (Quests, Dungeons, Shop, Inbox, Awakening cadence), P1 (Tower, Rewarded Ads, Events, Guild Coins) in outline.

Depends on: 00 (constants, names, authority split), 02 (`GameState` fields this spec writes, `IClock`), 03 (`KillTimeSeconds`, `GoldPerKill`, `ExpPerKill`, Awakening gates, Research costs), 04 (pull pricing, ticket consumption), 05 (`CatchUpTank`, Fish Coin income), 08 (Inbox and IAP server contracts).

## 1. Currencies: sources and sinks

All balances live in `GameState.Wallet` (02's typed fields; `HERO_EXP` is `Hero.Exp`). Every mutation goes through `Wallet.Apply(CurrencyDelta, CurrencyReason)` with a reason from the closed enum in 00 section 8 item 6. There is no second path.

```csharp
// HeroCat.Sim
public enum Currency : byte
{
    GOLD, DIAMOND, FISH_COIN, ROCK_CANDY, HERO_EXP,
    TICKET_FISH_COIN, TICKET_ROCK_CANDY, TICKET_EXP,
    TICKET_SUMMON_WEAPON, TICKET_SUMMON_SKILL, TICKET_SUMMON_COMPANION,
    GUILD_COIN,          // P1, always 0 before P1
    TICKET_SUMMON_RUNE   // P1, always 0 before P1; appended last so earlier ordinals do not move (issue #26)
}

public enum CurrencyReason : byte   // the closed list of 00 section 8 item 6, in that order (issue #11)
{
    KILL, BOSS, OFFLINE, LOGIN, TUTORIAL, FIRST_CLEAR, QUEST, ACHIEVEMENT, DAILY_GIFT, DAILY_RESET,
    SUMMON, TRAINING, RESEARCH, AWAKENING, LIMIT_BREAK, IAP, MAIL, DUNGEON, FISH_DISCOVERY, FISH_MERGE,
    SHOP_EXCHANGE, REFUND, ADMIN
}

public readonly record struct CurrencyDelta(Currency Currency, double Amount);

// Storage is 02's typed Wallet (issue #7): double Gold, FishCoin; long Diamond, RockCandy, GuildCoin;
// int[] DungeonTicket by Dungeon ordinal; int[] SummonTicket by Banner ordinal (WEAPON, SKILL, COMPANION, RUNE).
// HERO_EXP is not a balance: it reads and writes Hero.Exp but still flows through Apply for the reason code (00 section 8 item 9).
public sealed partial class Wallet
{
    public bool CanAfford(Currency c, double amount) => Get(c) >= amount;   // Get(c) reads the typed field

    // Returns false and changes nothing when a debit would go negative: it rejects, it never clamps and never
    // partially applies. Also returns false for NaN/inf, and for a non-whole Amount on a counted (long/int) currency.
    public bool Apply(in CurrencyDelta d, CurrencyReason reason, EventSink sink)
    {
        if (!double.IsFinite(d.Amount) || (IsCounted(d.Currency) && d.Amount != Math.Floor(d.Amount))) return false;
        double next = Get(d.Currency) + d.Amount;
        if (next < 0) return false;
        Set(d.Currency, next);                // HERO_EXP: Hero.Exp = next; then runs 03's CheckHeroLevel (03 section 5.2)
        sink.Emit(new CurrencyChanged(d.Currency, d.Amount, next, reason));
        return true;
    }
}
```

### 1.1 Ledger

| Currency | Tier | Sources (reason) | Sinks (reason) | Daily F2P reference, mid-game |
|---|---|---|---|---|
| `GOLD` | M0 | Kills (`KILL`), boss kills x`BOSS_GOLD_MULT` (`BOSS`), Offline Catch-up (`OFFLINE`); no dungeon grants gold | Training (`TRAINING`) | 100% of income is spent; see section 11 |
| `DIAMOND` | M0 | Login (`LOGIN`), daily/weekly quests (`QUEST`), boss first clears (`FIRST_CLEAR`), achievements (`ACHIEVEMENT`), tutorial grant (`TUTORIAL`), IAP via Inbox (`IAP`), mail (`MAIL`) | Summons (`SUMMON`), ticket exchange (`SHOP_EXCHANGE`) | `DIAMONDS_PER_DAY_F2P` = 400 (section 2) |
| `FISH_COIN` | M0 source, MVP sink | Fish discovery (`FISH_DISCOVERY`), repeat merges and discards (`FISH_MERGE`, 05), Fish Coin Dungeon (`DUNGEON`) | Research (`RESEARCH`) | board income (05) plus `2 x FISH_COIN_FLOOR(f)`; per-day totals from the tool, section 11 |
| `ROCK_CANDY` | MVP | Rock Candy Dungeon (`DUNGEON`), boss kills by chance (`BOSS`, rate in 03), copies past `DUPE_LVL_CUM[10]` = 160 converted at pull time (`SUMMON`, 04 section 5.3) | Awakening (`AWAKENING`), limit breaks (`LIMIT_BREAK`, rules in 04) | 2 x `ROCK_CANDY_FLOOR(f)`, section 6; boss drops and overflow are small next to it |
| `HERO_EXP` | M0 | Kills (`KILL`), Offline Catch-up (`OFFLINE`), EXP Dungeon (`DUNGEON`) | Hero Level, consumed automatically by 03 | never spent manually |
| `TICKET_FISH_COIN`, `TICKET_ROCK_CANDY`, `TICKET_EXP` | MVP | Daily reset to `DUNGEON_TICKETS_PER_DAY` (`DAILY_RESET`), exchange (`SHOP_EXCHANGE`), mail | Dungeon clear or sweep (`DUNGEON`) | 2 each, reset not stacked |
| `TICKET_SUMMON_WEAPON` | MVP | Daily free gift (`DAILY_GIFT`), quests, weekly bonus, events, mail | One pull (`SUMMON`) | 1 per day from the gift |
| `TICKET_SUMMON_SKILL`, `TICKET_SUMMON_COMPANION` | MVP | Weekly complete-all bonus, achievements, events, mail | One pull (`SUMMON`) | about 0.15 per day each |
| `GUILD_COIN` | P1 | Guild activity | Guild shop | 0 before P1 |
| `TICKET_SUMMON_RUNE` | P1 | Events, mail (P1) | One Rune banner pull (`SUMMON`) | 0 before P1 (issue #26) |

Rules that hold for every row:

1. Diamonds are the only currency money buys (00 decision 7). Packages in section 8 are diamonds on a schedule, nothing else.
2. Tickets reset, they do not accumulate from the reset: at rollover `TICKET_*` dungeon balances are set to `max(current, DUNGEON_TICKETS_PER_DAY)`, so bought tickets survive but unused daily tickets do not stack. Summon tickets never expire.
3. Nothing converts between currencies except the exchange offers in section 8.4, each with a daily limit.
4. A debit that would go negative is rejected by `Wallet.Apply` before any state changes; commands check `CanAfford` first and return `Reject(CannotAfford, "NOT_ENOUGH_<CURRENCY>")` (section 1.3) so the UI can name the shortfall. Tickets follow the same pattern, e.g. `NOT_ENOUGH_TICKET_FISH_COIN`, `NOT_ENOUGH_TICKET_SUMMON_WEAPON` (issue #244).
5. **Sim-granted diamonds and tickets reach the server ledgers on upload** (issue #49). Login, quests, first clears, achievements, the tutorial grant, the daily gift and quest ticket grants are credited locally by the sim and work offline. Each `PUT /save` reports cumulative per-reason counters (`Stats.ClientDiamondsEarned[reason]`, `Stats.ClientTicketsEarned[kind][reason]`, plus the client-side spends such as `SHOP_EXCHANGE`; 02), and the server credits the delta since the last upload into `diamond_ledger` / `ticket_ledger` with `ref_type = save_reconcile`, clamped to the per-day allowance (08 section 3.1). A summon can only spend what has been uploaded, so the client uploads before a pull when it has unsynced earnings (01).
6. **`maxTicketsPerDay(ticket)`** is the per-day bound 08 section 3.1 clamps a summon ticket's upload credit to. It is derived from content, not hand-entered: that ticket's daily client-side grants (`DAILY_GIFT_WEAPON_TICKETS` and daily quest rows granting it) plus its weekly grants (weekly quest rows, the bonus rows `w_bonus_all_skill` / `w_bonus_all_companion` included) in full, since a weekly grant lands on one day. Achievement ticket rewards are bounded like first-clear diamonds, by the achievement rows the uploaded progress has reached. MVP values: Weapon 1, Skill 1, Companion 1.

### 1.2 M0 subset

M0 ships `GOLD`, `DIAMOND`, `FISH_COIN`, `HERO_EXP`; the tutorial grant (`TUTORIAL_DIAMONDS` plus the starter weapon `TUTORIAL_WEAPON_ID`, 09 `tut_welcome`, issue #46); Offline Catch-up; `LOGIN_DIAMONDS`; boss first-clear diamonds (`FIRST_CLEAR_DIAMONDS`, paid by 03's boss clear with `CurrencyReason.FIRST_CLEAR`). Quests, Dungeons, Shop, Inbox and Awakening are MVP. The M0 bot therefore earns diamonds only from login, first clears and the tutorial grant, which is why the M0 exit criterion in 00 section 11 is two weeks, not thirty days.

### 1.3 Reject reasons

Every economy command rejects through 01's `ApplyResult.Reject(RejectCode, reason)`: the `RejectCode` is 01's generic category (UI styling), and `ApplyResult.Reason` is an UPPER_SNAKE reason key from the closed list below, which the UI localizes as `reject.<reason_lower>` (issue #11; the key lowercases the reason, `reject.not_enough_gold`, issue #248). A rejection changes no state and emits no event. Tests assert both the code and the reason. This spec owns this list; a new reason is added here first.

| Reason (`ApplyResult.Reason`) | `RejectCode` | Raised by |
|---|---|---|
| `NOT_ENOUGH_<CURRENCY>` (e.g. `NOT_ENOUGH_DIAMOND`) | `CannotAfford` | any command that debits (rule 4) |
| `UNKNOWN_ID` | `InvalidTarget` | `ClaimQuest`, `ExchangeItem` with an id not in content (`shop_exchange` for `ExchangeItem`); `ClaimAllQuests` with a kind outside `QuestKind` |
| `BAD_QUANTITY` | `BadArgument` | `ExchangeItem` with `qty < 1` |
| `QUEST_NOT_COMPLETED` | `NotAvailable` | `ClaimQuest` |
| `QUEST_ALREADY_CLAIMED` | `AlreadyClaimed` | `ClaimQuest` |
| `NOTHING_TO_CLAIM` | `NotAvailable` | `ClaimAllQuests` |
| `QUESTS_LOCKED` | `Locked` | `ClaimQuest`, `ClaimAllQuests` before the `feature_unlocks` row `QUESTS` is met (section 5.1, issue #45) |
| `DUNGEON_LOCKED` | `Locked` | `StartDungeon`, `SweepDungeon` |
| `DUNGEON_FLOOR_NOT_UNLOCKED` | `Locked` | `StartDungeon` |
| `DUNGEON_RUN_IN_PROGRESS` | `NotAvailable` | `StartDungeon`, `SweepDungeon` |
| `BOSS_ATTEMPT_IN_PROGRESS` | `NotAvailable` | `StartDungeon` while a boss fight is active (issue #42); a sweep has no fight and is allowed |
| `NOTHING_TO_SWEEP` | `NotAvailable` | `SweepDungeon` |
| `SHOP_LOCKED` | `Locked` | `ClaimDailyGift`, `ExchangeItem` when the `feature_unlocks` row `SHOP` is not met: never in MVP (`ALWAYS`, 09 `E063`), always in an M0 build, which has no shop (section 1.2; issue #174) |
| `DAILY_GIFT_ALREADY_CLAIMED` | `AlreadyClaimed` | `ClaimDailyGift` |
| `EXCHANGE_LIMIT_REACHED` | `Capped` | `ExchangeItem` with `qty` above the offer's remaining daily limit |
| `NO_PENDING_OFFLINE_REWARD` | `NotAvailable` | `ClaimOfflineReward` |

`Awaken` is 03's command; its reject reasons are 03's.

## 2. Diamond budget (MVP)

The target `DIAMONDS_PER_DAY_F2P` = 400 (S) is a steady-state floor for a player who completes the free part of the daily loop. It is reached by construction:

| Source | Rule | Amount on the reference day |
|---|---|---|
| Login | `LOGIN_DIAMONDS` once per day key, auto-granted on first tick of the day | 50 |
| Daily missions | 10 paying mission rows at 20 diamonds each (`reward_amount`); `d_login` pays nothing (section 5.2). Reference day: `REF_DAILIES_DONE` = 8 of 10 completed (the Skill and Companion summon missions need diamond pulls and are skipped on a non-pull day) | 160 |
| Daily progress bonus | rows `d_bonus_5` (30) at 5 missions, `d_bonus_8` (70) at 8 missions | 100 |
| First clears | `FIRST_CLEAR_DIAMONDS` (10) per boss stage cleared for the first time; reference day assumes `REF_NEW_BOSSES` = 4 new boss stages | 40 |
| Weekly missions, amortised | 7 mission rows at 50 diamonds each = 350 per week = 50 per day | 50 |
| **Total** | | **400** |

`50 + 8 * 20 + (30 + 70) + 4 * 10 + 350 / 7 = 50 + 160 + 100 + 40 + 50 = 400`.

Quest rows are the single source of quest diamond amounts: there are no per-mission or bonus constants (issues #55, #244). The 09 exporter rule `E039` recomputes this reference day from the quest rows, `LOGIN_DIAMONDS`, `FIRST_CLEAR_DIAMONDS` and the two reference constants `REF_DAILIES_DONE` and `REF_NEW_BOSSES` (formula in 09), and fails when it is more than 10% from `DIAMONDS_PER_DAY_F2P`.

Income by day for the greedy F2P bot. **Model output** of `tools/power_curve_model.py --check` (20-seed medians, each column its own median, for content 2026.11.01.2 with the section 5.4 ladders from the spec rows until the `achievements` table ships, issue #171; issue #178). The tool pays every source by its rule: `LOGIN_DIAMONDS` at day start, the daily and weekly rows the bot's day actually completes (claimed at the end of the day), `FIRST_CLEAR_DIAMONDS` per boss stage the bot clears for the first time, and each achievement rung when the bot's state crosses it. Weeks are days 1-7, 8-14, ... (the bot epoch is a Monday, 00 decision 25); the weekly column is the week's weekly-row diamonds divided by 7, and day 30 (a week the run cuts short) uses the last whole week. Rerun the tool after any content change; the numbers move with the stage curve.

| Day | Stage | New boss stages | Dailies done (of 11) | Login | Dailies + bonus | First clears | Weekly (amortised) | Achievements (section 5.4, one-time) | Day total |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 210 | 20 | 10 | 50 | 280 | 200 | 50 | 1,070 (plus the 1,500 tutorial grant) | 3,190 |
| 3 | 360 | 4 | 11 | 50 | 300 | 40 | 50 | 100 | 575 |
| 7 | 430 | 1 | 11 | 50 | 300 | 10 | 50 | 200 | 610 |
| 14 | 510 | 1 | 9 | 50 | 260 | 10 | 36 | 100 | 471 |
| 30 | 590 | 0 | 10 | 50 | 280 | 0 | 36 | 0 | 366 |

Reading the table: diamonds are front-loaded on purpose (the first week is where the player learns that pulls feel good). The steady state, days 15-28 (the two whole weeks after day 14), is a median 380 a day, 5% under the target: the bot completes 9-10 dailies on most days (more than the reference day's 8, because it pulls on the Skill or Companion banner on many days), and that offsets the weekly rows it misses and the achievements running dry. From week 2 the bot misses `w_summons` (30 pulls; about 4 a day) and `w_train` (300 Training levels: costs grow faster than gold late, so the "far more" in section 5.3 holds only in week 1) on most seeds, and `w_merges` on some, so the weekly column is 36 rather than 50 and `w_bonus_all` would rarely pay (decision #266 item B). Whether to accept that taper or raise the weekly rows' reward is Open question 1. Over 30 days the median bot earns 1,500 login, 7,870 daily-quest, 1,200 weekly, 580 first-clear and 3,410 achievement diamonds.

What the tuning tool (`tools/power_curve_model.py`, spec 03) checks about `DIAMONDS_PER_DAY_F2P` (issue #178; report-only like the rest of `--check` until decision #249):

- **Budget:** the steady-state income (mean of days 15-28) is at least `DIAMONDS_PER_DAY_F2P` less 10% (the tolerance `E039` uses, 09; Open question 1 names no number). Model: median 380, below 360 on 4 of 20 seeds.
- **First clears:** the bot's first-clear diamonds over days 1, 2-3, 4-7, 8-14 and 15-30 are within 20% of what the 00 section 6.6 target curve gives (stages gained under the targets, interpolated linearly from stage 0 at day 0, divided by `BOSS_EVERY`, times `FIRST_CLEAR_DIAMONDS`). Model: fails on most seeds outside day 1, because the bot's stage curve is not the target curve (it runs ahead on days 2-3 and 8-30 and behind on days 4-7).
- **Affordability and pity:** each banner's pull cadence comes from this section's income (diamonds plus the ticket sources in section 1.1) and 03's bot policy; no spec hand-enters a pulls-per-day figure, and 04's `BANNER_LVL_EXP` thresholds are retuned from that output (issue #38). With the pull prices in 00 section 6.3 the steady-state income, plus that banner's tickets at its pull price, must afford one 10-pull every 2.5 days on the Weapon banner, every 5 days on the Skill banner and every 7.5 days on the Companion banner, and every banner's pity counter (`PITY_HARD` 80) must resolve (a Legendary or better) within the 30-day window at least once. If it does not, the number is too low for the summon layer to carry its 03 share of log-power. Model: Weapon every 2.1 days, Skill every 4.8, Companion every 7.0 on the median seed (Skill misses on 6 of 20 seeds, Companion on 4); the bot itself spreads its diamonds by value per diamond, so its actual cadence is 2.37 Weapon, 1.37 Skill and 0.58 Companion pulls a day, and the Companion banner gives no Legendary in 30 days on 7 of 20 seeds (Skill on 3).
- **Spending:** diamonds are spent within 2.5 days in steady state (the end-of-day balance on days 15-28 is never above 2.5 times the steady-state daily income). Model: passes.

`--diamonds flat` replaces every source above with a flat `DIAMONDS_PER_DAY_F2P` at day start (plus the tutorial grant), the parity mode the C# bot also keeps (10).

Diamond sinks on the reference day: the bot spends everything on pulls in the order Weapon 10-pull when `DIAMOND >= 1000`, else Skill 10-pull when `>= 2000` and Weapon banner level has reached its first threshold (04), else Companion 10-pull when `>= 3000`. It never holds diamonds across a day for a reason other than reaching a 10-pull. The exchange (section 8.4) is used only by the bot variant that is Fish Coin starved.

## 3. The daily loop for a 2-hour active player (MVP)

Assumed session shape, matching 00 section 6.6 and what the bot in spec 10 plays: two sessions a day, roughly 12 hours apart, about 60 minutes each. On alternate days the bot's evening session spans 00:00 UTC (10 section 3.3), so the in-session rollover (section 4.2 rule 2) is exercised (issue #29). The timeline below is the first session of a mid-game day; the second repeats the mid-session block.

| Phase | Minute | What happens | Commands |
|---|---|---|---|
| Login | 0:00 | `SCR_OFFLINE_REWARD` shows the overnight gold and EXP (section 7); the Magic Tank is full (`TANK_CAP0` or Research cap). Login diamonds landed on the first tick. Red dots: Quests if anything is claimable, Shop (daily gift), Inbox if anything arrived. | `ClaimOfflineReward` |
| First 5 minutes | 0:01 to 0:05 | Spend overnight gold: Training `Max` on ATK, then the cheapest of the other stats (bot policy in 03). Release the full tank (`ReleaseMax`), merge everything (`MergeAll`), which fires missiles and starts the merge mission. Claim the daily gift (1 `TICKET_SUMMON_WEAPON`), pull it. Claim claimable quests. | `TrainStat`, `ReleaseMax`, `MergeAll`, `ClaimDailyGift`, `Summon`, `ClaimAllQuests` |
| Dungeons | 0:05 to 0:12 | Run or sweep the three dungeons, two tickets each. Each run is a fixed fight of at most `BOSS_TIME_TICKS`, so six runs take at most 3 minutes plus result screens. Rock Candy from the dungeon goes straight into Awakening if the next rank is affordable and unlocked. | `StartDungeon`, `SweepDungeon`, `Awaken` |
| Mid-session | 0:12 to 0:55 | Combat runs; the player merges fish as the tank refills (`TANK_REFILL_S0` 6 s per fish, 10 per minute), taps bosses (`TapZap`), spends gold every few minutes. When `DIAMOND >= 1000`, Weapon 10-pull. Research purchases when Fish Coins cover the next node. | `MergeFish`, `TapZap`, `TrainStat`, `Summon`, `Research` (03) |
| Last minutes | 0:55 to 1:00 | Spend all gold (nothing carries value offline; offline gold is computed from stage, not banked gold). Claim all completed quests. Check that the daily progress bar is at 8 or more. Leave with the tank empty so refill time is not wasted. | `TrainStat`, `ClaimAllQuests`, `ReleaseMax` |
| Between sessions | 1:00 to 12:00 | Offline Catch-up accrues at `OFFLINE_RATE` of the kill rate; the tank refills to capacity in `TANK_CAP * TANK_REFILL_S` seconds (60 s at Research 0) and then idles. | none |

What each login shows, in order: `SCR_OFFLINE_REWARD` if the gap was at least `OFFLINE_MIN_S` and the reward is not yet claimed; then any `DayRolledOver` toast; then `SCR_INBOX` badge if unclaimed items exist. No other modal is auto-opened at login (07 section 14 rule: never stack two modals).

## 4. Day and week rollover (M0 login grant, MVP quests and tickets)

### 4.1 Keys

```
DayKey(serverSeconds)  = floor(serverSeconds / DAY_SECONDS)                      // 00: 00:00 UTC
WeekKey(serverSeconds) = floor((DayKey(serverSeconds) - 4) / 7)                   // Monday 00:00 UTC; day 0 (1970-01-01) was a Thursday, so day 4 is the first Monday. Same value as 02's WEEK_EPOCH_OFFSET form.
```

`serverSeconds` is the sim's virtual server time (issue #5). The sim never reads device time or `IClock`: the runtime issues a logged `ApplyServerTime(IClock.NowServerSeconds)` only at boot, on resume, on gap recovery and every `WALL_CLOCK_SYNC_SEC` (60 s, 01 section 3.1), which re-anchors the clock and runs `EnsureDay`; between those commands each tick advances the virtual time by `1 / TICK_HZ` s. Rollover is therefore a pure function of the command log. `GameState.Time` (02) holds `DayKey`, `WeekKey`, `LoginDayKey`, `LastTickServerTime`, `LastSavedServerTime`; these are the only day and week keys (issue #7).

### 4.2 EnsureDay

Runs in every `ApplyServerTime`, on every sim tick against the virtual clock (cheap integer compare), and at the top of every quest, dungeon, shop and inbox command.

```csharp
void EnsureDay(long now)
{
    int day = (int)(now / DAY_SECONDS);                     // int day keys as in 02 (Time.DayKey)
    if (day <= State.Time.DayKey) return;                   // <= : a backwards clock never re-grants a day
    State.Time.DayKey = day;

    State.Quests.ResetKind(QuestKind.DAILY);                // progress 0, state IN_PROGRESS; unclaimed rewards are forfeited (Open question 2)
    foreach (var t in DungeonTickets)                       // TICKET_FISH_COIN, TICKET_ROCK_CANDY, TICKET_EXP
        if (Wallet.Get(t) < DUNGEON_TICKETS_PER_DAY)
            Wallet.Apply(new(t, DUNGEON_TICKETS_PER_DAY - Wallet.Get(t)), CurrencyReason.DAILY_RESET, Sink);
    State.Shop.DailyGiftClaimed = false;
    State.Shop.ExchangeCounts.Clear();

    int week = FloorDiv(day - 4, 7);                        // floor((dayKey - 4) / 7), Monday 00:00 UTC (00 decision 21); same as WeekKey() above
    if (week > State.Time.WeekKey)
    {
        State.Time.WeekKey = week;
        State.Quests.ResetKind(QuestKind.WEEKLY);
        Sink.Emit(new WeekRolledOver(week));
    }
    Sink.Emit(new DayRolledOver(day, skippedDays, secondsSinceLastTick));   // skippedDays = day - oldDayKey - 1 (0 for a new profile); now - LastTickServerTime

    // Login grant: once per day key, on the first tick of the day the player is present for. This auto-grant is the
    // only payment of LOGIN_DIAMONDS; d_login only counts (section 5.2, issue #20).
    if (State.Time.LoginDayKey < day)
    {
        Wallet.Apply(new(Currency.DIAMOND, LOGIN_DIAMONDS), CurrencyReason.LOGIN, Sink);
        State.Time.LoginDayKey = day;
    }
    Quests.Progress(QuestKind.DAILY, QuestGoal.LOGIN, 1);
    Quests.Progress(QuestKind.WEEKLY, QuestGoal.LOGIN_DAYS, 1);
}
```

| Resets at day rollover | Persists across rollover |
|---|---|
| Daily mission progress and claim state; daily progress bonus state | Weekly mission progress (until week rollover) |
| Dungeon tickets topped up to `DUNGEON_TICKETS_PER_DAY` | Achievement progress and claims |
| Daily free gift claimed flag | Pity counters, banner levels, banner EXP |
| Exchange purchase counts | Dungeon best floors and the sweep eligibility they imply |
| Login grant eligibility | Inbox items (they have their own expiry, section 9) |
| | Offline pending reward (it is tied to `LastTickServerTime`, not the day) |

Rules:

1. **Lazy, single reset.** A player away three days gets one reset on return. Missed days' quests are gone; nothing stacks. Tickets are topped up, not added.
2. **App open at rollover.** The tick whose virtual time crosses `DAY_SECONDS` runs `EnsureDay` and emits `DayRolledOver`. The client (07) toasts "New day: missions and dungeon tickets reset" and refreshes `SCR_QUESTS` and `SCR_DUNGEONS` if they are open. A dungeon run in progress is not interrupted; if it clears, the ticket is debited from the new day's allotment and the reward is granted normally.
3. **Backwards clock.** An `ApplyServerTime` can carry a smaller value than `LastTickServerTime` after a failed re-sync or a server-side correction. `EnsureDay` is a no-op because `day <= DayKey`. Offline Catch-up clamps elapsed to 0 (section 7). `LastTickServerTime` is never moved backwards; when the gap exceeds `CLOCK_BACKWARDS_TOLERANCE_S` (5 s, 01 section 3.2) the sim emits `ClockWentBackwards(deltaSeconds)` once per occurrence for analytics, a smaller gap is resync jitter and emits nothing (issue #246), and the client shows nothing in release builds.
4. **New profile.** `DayKey = -1`, `WeekKey = -1`, `LoginDayKey = -1`, so the first `EnsureDay` grants the first login and seeds tickets. The tutorial grant (`TUTORIAL_DIAMONDS`, `CurrencyReason.TUTORIAL`) is separate and guarded by `Tutorial.TutorialDiamondsGranted` (02).
5. **Per-region rollover** is 00 decision 8 territory and is not reopened here. The only hook needed later is an offset added to `now` before the division; the server would supply it (08).

### 4.3 Events

| Event | Fields | Consumers |
|---|---|---|
| `DayRolledOver` | `dayKey` | 07 toast and badge refresh; analytics `day_rollover` |
| `WeekRolledOver` | `weekKey` | 07 badge refresh; analytics |
| `ClockWentBackwards` | `deltaSeconds` | analytics `clock_anomaly`; 08 plausibility counter |

## 5. Quests (MVP)

### 5.1 Model

```csharp
public enum QuestKind : byte { DAILY, WEEKLY, ACHIEVEMENT }
public enum QuestStatus { IN_PROGRESS, COMPLETED, CLAIMED }   // 02
public enum QuestGoal : byte
{
    LOGIN, LOGIN_DAYS, KILL_ENEMIES, ATTEMPT_BOSS, CLEAR_STAGES, SUMMON, RELEASE_FISH, MERGE_FISH,
    CLEAR_DUNGEON, TRAIN_LEVELS, DAILIES_COMPLETED, WEEKLIES_COMPLETED, REACH_STAGE, REACH_HERO_LEVEL, OWN_WEAPONS,
    OWN_SKILLS, OWN_COMPANIONS, LIFETIME_MERGES, FISH_LEVEL, AWAKEN_RANK, LIFETIME_SUMMONS, WATCH_AD /* P1 */
}

// Content row (spec 09 table quests_daily / quests_weekly / achievements)
public sealed record QuestDef(string Id, QuestKind Kind, QuestGoal Goal, string? Filter, long Target,
                              Currency RewardCurrency, double RewardAmount, int Order);
// Filter: banner code for SUMMON, dungeon code for CLEAR_DUNGEON, null otherwise. A null filter on a SUMMON or
// CLEAR_DUNGEON row matches any banner or dungeon (w_summons, w_dungeons).
// One reward per row, as 09's quest tables have it; the weekly complete-all bonus (section 5.3), which grants two tickets,
// is therefore two rows, w_bonus_all_skill and w_bonus_all_companion (issue #170).

public sealed class QuestProgress   // one per QuestDef, in GameState.Quests.Daily / .Weekly / .Achievement[questId] (02, issue #7)
{
    public long Progress;           // keeps counting past Target for display
    public QuestStatus State;
}
```

`Quests.Progress(kind, goal, delta, filter = null)` is called by the sim at the emitting site (03 on `EnemyKilled`, 04 on each pull result, 05 on `FishReleased`/`FishMerged`, this spec on dungeon clears). It increments every matching quest, whatever its state (progress keeps counting past `Target` for display, acceptance criterion 8), and flips an `IN_PROGRESS` one to `COMPLETED` when `Progress >= Target`, emitting `QuestCompleted(questId)`. A boss attempt (`ATTEMPT_BOSS`) is one boss spawn, so the automatic attempt on reaching a boss stage and every accepted `AttemptBoss` count once each. When a daily mission completes (any daily row except the `DAILIES_COMPLETED` rows), the sim calls `Progress(DAILY, DAILIES_COMPLETED, 1)` and `Progress(WEEKLY, DAILIES_COMPLETED, 1)`; when a weekly mission completes (any weekly row except the `WEEKLIES_COMPLETED` rows), `Progress(WEEKLY, WEEKLIES_COMPLETED, 1)`. The bonus rows' own completion counts toward nothing. A missing `QuestProgress` entry reads as `IN_PROGRESS` with progress 0, so the rollover reset clears the kind's entries (02 rule P12). Rewards are granted only by `ClaimQuest(questId)` or `ClaimAllQuests(kind)`, which emit `QuestClaimed(questId, currency, amount)` and apply the delta with `CurrencyReason.QUEST` (achievements use `CurrencyReason.ACHIEVEMENT`). A row with reward 0 (only `d_login` today) goes straight from `IN_PROGRESS` to `CLAIMED` when it completes: it emits `QuestCompleted`, pays nothing, raises no red dot and is never counted by `ClaimAllQuests`. Offline Catch-up advances no quest.

Unlock: the `feature_unlocks` row `QUESTS` (`STAGE_HIGHEST >= 10`, 09; issue #45) gates `SCR_QUESTS` and the claim commands, which reject `(Locked, QUESTS_LOCKED)` before it is met. Progress counts from profile creation, so missions completed before the gate are claimable once it opens.

Why a claim step rather than auto-grant: the reference game and the whole sub-genre use it, the Claim All button is the daily "pay-day" tap that `SCR_QUESTS` is built around (07), and the red dot it produces is the main reason players open the quests screen. The sibling prototype auto-granted and listed this as its open decision 7; this spec takes the claim step as the default.

### 5.2 Daily missions

Each paying row's `reward_amount` is 20 diamonds; the rows are the only source of the amount (issue #55). Order is display order on `SCR_QUESTS`.

| Id | Goal | Filter | Target | Why this target |
|---|---|---|---|---|
| `d_login` | `LOGIN` | | 1 | Auto-completes on the day's first tick and counts toward the bonus thresholds; reward 0, nothing to claim (issue #20) |
| `d_kills` | `KILL_ENEMIES` | | 300 | About 10 minutes of combat at a 2 s kill time; done in the first session without trying |
| `d_boss` | `ATTEMPT_BOSS` | | 3 | Counts attempts, not clears, so a walled player completes it. Auto-attempts on reaching a boss stage count |
| `d_summon_weapon` | `SUMMON` | `WEAPON` | 1 | Free with the daily gift ticket (section 8.3) |
| `d_summon_skill` | `SUMMON` | `SKILL` | 1 | Needs a 200-diamond pull or a ticket; expected on 10-pull days only |
| `d_summon_companion` | `SUMMON` | `COMPANION` | 1 | Same; 300 diamonds |
| `d_release` | `RELEASE_FISH` | | 30 | Three tank-fulls at Research 0; 3 minutes of refill |
| `d_merge` | `MERGE_FISH` | | 15 | One full board merged down |
| `d_dungeon_fish` | `CLEAR_DUNGEON` | `FISH_COIN` | 1 | Clear or sweep |
| `d_dungeon_candy` | `CLEAR_DUNGEON` | `ROCK_CANDY` | 1 | |
| `d_dungeon_exp` | `CLEAR_DUNGEON` | `EXP` | 1 | |

That is 11 rows including login, which is why the budget in section 2 counts "10 paying missions": `d_login` is a counting row with `reward_amount` 0. The day's `LOGIN_DIAMONDS` (50, a `constants` row in 09) are paid once by `EnsureDay`'s auto-grant with `CurrencyReason.LOGIN`, never by a claim (issue #20). The daily progress bonus counts all 11 rows; the thresholds are 5 and 8 completed (not claimed) missions:

| Bonus row | Threshold | Reward |
|---|---|---|
| `d_bonus_5` | 5 missions `COMPLETED` or `CLAIMED` | 30 diamonds (`reward_amount`) |
| `d_bonus_8` | 8 | 70 diamonds |

The two bonus rows are `QuestDef`s with goal `DAILIES_COMPLETED` so they reuse the claim path; their rows are the source of the bonus payouts (no constants), and the 09 validator checks both rows exist (issue #244). F2P reaches 8 without any diamond spend: login, kills, boss, weapon summon (gift ticket), release, merge, three dungeons = 9.

P1: `d_watch_ad` (`WATCH_AD`, target 1, reward 20) is added as a twelfth row and the bonus thresholds stay at 5 and 8, so ad-watching never becomes required for the full bonus (pillar 4).

### 5.3 Weekly missions

Each row's `reward_amount` is 50 diamonds (issue #55). All seven complete-able by a player who does the dailies six days out of seven.

| Id | Goal | Target | Per-day equivalent |
|---|---|---|---|
| `w_login` | `LOGIN_DAYS` | 5 | |
| `w_kills` | `KILL_ENEMIES` | 3000 | 430 per day, above `d_kills` so it needs the second session |
| `w_dailies` | `DAILIES_COMPLETED` | 40 | 8 per day for 5 days |
| `w_summons` | `SUMMON` (any banner) | 30 | Three 10-pulls, or two plus seven gift tickets |
| `w_merges` | `MERGE_FISH` | 100 | 15 per day |
| `w_dungeons` | `CLEAR_DUNGEON` (any) | 25 | 6 per day for 4 days |
| `w_train` | `TRAIN_LEVELS` | 300 | Training levels bought, any stat; the bot buys far more |

Weekly complete-all bonus: 1 `TICKET_SUMMON_SKILL` + 1 `TICKET_SUMMON_COMPANION` for completing all seven missions above. A `QuestDef` carries one reward, so the bonus is two rows (issue #170, the user's call):

| Id | Goal | Target | Reward |
|---|---|---|---|
| `w_bonus_all_skill` | `WEEKLIES_COMPLETED` | 7 | 1 `TICKET_SUMMON_SKILL` |
| `w_bonus_all_companion` | `WEEKLIES_COMPLETED` | 7 | 1 `TICKET_SUMMON_COMPANION` |

`WEEKLIES_COMPLETED` is in `QuestGoal` (section 5.1; not `DAILIES_COMPLETED`). Only the seven missions count toward it: the two bonus rows complete together on the seventh mission and do not count each other, so their progress tops out at 7 (section 5.1). Non-diamond on purpose so the section 2 arithmetic stays a diamond sum. `SCR_QUESTS` shows them as the weekly tab's bonus chips (07).

### 5.4 Achievement ladders

One-time, never reset, claimable in any order once completed. Each ladder is a sequence of `QuestDef` rows whose `Order` is the rung; `SCR_QUESTS` shows only the lowest unclaimed rung per ladder. Rewards are diamonds unless noted.

| Ladder | Goal | Rungs (target -> reward) | Total |
|---|---|---|---|
| Stage | `REACH_STAGE` | 10->20, 30->30, 50->50, 100->80, 150->100, 200->100, 300->150, 400->150, 500->200, 700->250, 1000->300 | 1,430 |
| Hero Level | `REACH_HERO_LEVEL` | 10->20, 25->30, 50->50, 75->80, 100->100, 150->150, 200->200 | 630 |
| Weapons owned | `OWN_WEAPONS` (distinct) | 5->30, 10->50, 20->100, 25->200 | 380 |
| Skills owned | `OWN_SKILLS` | 5->30, 10->50, 20->100, 30->150, 35->250 | 580 |
| Companions owned | `OWN_COMPANIONS` | 5->30, 10->50, 20->100, 35->150, 45->300 | 630 |
| Merges | `LIFETIME_MERGES` | 50->20, 200->30, 500->50, 1000->80, 3000->100, 10000->200 | 480 |
| Fish level | `FISH_LEVEL` | 5->30, 8->50, 10->100, 12->150 (needs Research), 15->300 | 630 |
| Awakening | `AWAKEN_RANK` | 1->50, 2->100, 3->150, 4->200, 5->300 | 800 |
| Summons | `LIFETIME_SUMMONS` | 10->20, 50->50, 100->80, 300->100, 500->150, 1000->300 | 700 |

How each goal reads the state (issue #171): `REACH_STAGE` is `Progress.HighestCleared` (the stage cleared at the frontier, the value the `QUESTS` gate reads, so rung 10 completes with the gate); `REACH_HERO_LEVEL` is `Hero.Level`; `OWN_WEAPONS` / `OWN_SKILLS` / `OWN_COMPANIONS` count the distinct ids in that collection's `Items` (copies and levels do not count), raised after each pull result; `FISH_LEVEL` is `Fish.HighestLevel`; `AWAKEN_RANK` is `Hero.AwakenRank`; `LIFETIME_MERGES` and `LIFETIME_SUMMONS` add one per merge and per pull at the `MERGE_FISH` and `SUMMON` progress sites. Every rung a value crosses completes in the same call, so a jump from stage 9 to 30 completes rungs 10 and 30 together, each claimable on its own in any order. Achievement entries are never cleared by `EnsureDay`. When the sim is constructed (a load, or a new content build) it raises every ladder to the value the state holds, the sum ladders from the lifetime counters `Stats.Merges` and `Stats.Pulls`, so a rung added by content completes without waiting for the next change; the raise never lowers progress and never touches a claimed rung's state. Hero levels gained when an offline reward is claimed count (the claim applies `HERO_EXP`); `ComputeOfflineCatchup` itself advances no rung. `SCR_QUESTS` reads `Simulation.AchievementRows()` (each ladder's lowest unclaimed rung, fully claimed ladders dropped), `LowestUnclaimedRung(ladder)` and `ClaimedRungs(ladder)`.

Total ladder value 6,260 diamonds, of which the day-30 F2P bot has claimed 3,410 (model output of `tools/power_curve_model.py`, 20-seed medians for content 2026.11.01.2, issue #178): stage 589, hero level 293, Awakening rank 4 (rank 4's stage-500 gate falls before day 30, section 6.5), fish level 14 and 132 summons. The by-day column in section 2 is this total spread by when the bot crosses each rung. Progress for `OWN_*`, `REACH_*`, `FISH_LEVEL` and `AWAKEN_RANK` is a max, not a sum: `Quests.SetMax(goal, value)` is called when the underlying value changes.

Collection rungs pay for a count, never for a complete set (pillar 4): every `OWN_*` rung's `target` is strictly less than the current pool size of that kind (00 section 6.3: 30 weapons, 40 skills, 50 companions), so the top rungs are 25, 35 and 45. 09 validates this on every content build, so a pool shrink or a rung edit cannot turn a rung into a set-completion reward (issue #44).

### 5.5 Commands and rejections

| Command | Validation | Effect |
|---|---|---|
| `ClaimQuest(questId)` | exists else `UNKNOWN_ID`; `State == COMPLETED` else `QUEST_NOT_COMPLETED` / `QUEST_ALREADY_CLAIMED` (section 1.3) | `State = CLAIMED`; `Wallet.Apply(reward)`; `QuestClaimed` |
| `ClaimAllQuests(kind)` | at least one `COMPLETED` else `NOTHING_TO_CLAIM` | Claims in `Order`; one `QuestClaimed` per quest plus one `QuestsBulkClaimed(kind, count, totals[])` for the toast |

Both run `EnsureDay` first (section 4.2), then the `QUESTS` gate (`QUESTS_LOCKED`), then the checks above. `ClaimQuest` looks the id up in `quests_daily`, then `quests_weekly`, then `achievements` (MVP ids are `d_*` / `w_*` / `ach_*`); a quest with no progress entry is `IN_PROGRESS`. An achievement claim pays with `CurrencyReason.ACHIEVEMENT`, and `ClaimAllQuests(ACHIEVEMENT)` claims every completed rung (issue #171). `ClaimAllQuests` with a `kind` outside `QuestKind` is rejected `(InvalidTarget, UNKNOWN_ID)`. Each claim emits `QuestClaimed` and then its `CurrencyChanged`; `QuestsBulkClaimed.totals` holds one `CurrencyDelta` per currency claimed, in `Currency` order.

## 6. Dungeons (MVP) and Tower (P1)

### 6.1 Shape

Three dungeons, each a ladder of floors. Floor `f` is one fixed fight: a single boss enemy with `HP = HP0 * HP_G^(DUNGEON_FLOOR_STAGE_STEP * f) * BOSS_HP_MULT` and a time limit of `BOSS_TIME_TICKS`, fought with the player's current loadout, skills, companions and the passive Fish Board DPS (no fish missiles, no Tap Zap, no fish merging: the board is hidden during a run, 07). `DUNGEON_FLOOR_STAGE_STEP` = 20, so floor `f` is as hard as the stage-`20f` boss. A player at stage `s` can clear about floor `floor(s / 20)`.

The fight is a deterministic sim run ticked in the main loop (issue #42): `StartDungeon` switches `Combat.Mode = Dungeon` and stores `Combat.DungeonRun { Kind, Floor, Seed, GrantId, EnemyIndex, TicksLeft }` (02). Each tick then runs the normal hero, skill and companion code (03) against the floor's boss on a *temporary* `dungeon_run` RNG stream seeded with `seed`; the persistent `dungeon` stream is untouched in MVP. The run ends `Cleared` when the boss dies or `Failed(remainingHpPct)` when `TicksLeft` reaches 0, and `Combat.Mode` returns to `Stage` at the same stage and kill count. One boss per floor, so `EnemyIndex` is always 0 in MVP. `seed` comes from the `IDungeonSeedService` seam (00 contract 11, issue #25): in MVP `RemoteDungeonSeedService` gets it from the server per run request (08 `POST /dungeon/seed`, with a `grantId`); in M0 and bots `LocalDungeonSeedService` derives it from one `dungeon` stream draw, `Rng.Derive(state.Streams.dungeon, dayKey, dungeon, floor)`. Server validation is correlation only (issue #52): the save keeps a short `Dungeons.RecentRuns[{GrantId, Kind, Floor, Cleared}]` list (02) that the server matches against issued seeds, flagging clears without a seed (08 `DUNGEON_WITHOUT_SEED`); together with the soft-currency bounds (`GOLD_DELTA_BOUND`, `RESEARCH_WITHOUT_COINS`) that is the MVP check. Replaying runs from a power snapshot is a P1 follow-up if soft-launch data shows abuse.

The Fish Board is hidden and frozen during a run (issue #40): the fight uses only the board's passive DPS, the tank keeps refilling, and board commands are rejected while `Combat.Mode == Dungeon` (`Locked`, reason `BOARD_LOCKED`, 05). No player input changes the outcome, which is what makes `PreviewDungeon` exact.

Unlock: the `feature_unlocks` row `DUNGEONS` (`STAGE_HIGHEST >= 30`, 09; issue #45) for all three; `SCR_DUNGEONS` is visible but locked before that. The row replaces the former `DUNGEON_UNLOCK_STAGE` constant, so the sim rejection and the client's Locked state read one value. The Tower (P1) later gates floor ranges: Tower floor 10 unlocks dungeon floors 11 to 20, and so on; in MVP all floors are open to whoever can clear the previous one.

### 6.2 Tickets, runs and sweeps

- `DUNGEON_TICKETS_PER_DAY` = 2 per dungeon, topped up at rollover (section 4.2). Extra tickets from the exchange (section 8.4) or mail stack above 2 and survive rollover.
- `StartDungeon(dungeon, floor, seed, grantId, localSeed)`: `floor` must be `bestFloor + 1` (the next uncleared) or any cleared floor. Rejections: `DUNGEON_LOCKED`, `DUNGEON_FLOOR_NOT_UNLOCKED`, `NOT_ENOUGH_TICKET_<DUNGEON>` (e.g. `NOT_ENOUGH_TICKET_FISH_COIN`, issue #244), `DUNGEON_RUN_IN_PROGRESS`, and `BOSS_ATTEMPT_IN_PROGRESS` while a boss fight is active: the player finishes the boss first (01 section 8.8; issue #42). The ticket is checked at start but not debited.
- **Ticket on clear only** (00 decision 16). The ticket is debited when the run ends in `Cleared`. A `Failed` run, and a run abandoned by a crash or kill mid-run (02 recovery rule P16, `DungeonAbandoned`), cost nothing and grant nothing. The sim is deterministic, so `SCR_DUNGEONS` runs `Simulation.PreviewDungeon(state, dungeon, floor)` (the same ticked fight run headlessly on a cloned state with a local seed, no state change) and labels the button "Clearable" or "Not clearable yet (need about x1.4 DPS)". A player therefore never spends a ticket to learn they are too weak; the run exists for the reward moment and the fight spectacle, not for risk.
- `SweepDungeon(dungeon)`: grants the reward of `bestFloor` instantly without a run or seed, debits one ticket, emits `DungeonSwept`. Rejections: `DUNGEON_LOCKED`, `NOTHING_TO_SWEEP` when `bestFloor` = 0, `NOT_ENOUGH_TICKET_<DUNGEON>`, `DUNGEON_RUN_IN_PROGRESS`. The reward is identical to re-clearing `bestFloor`. The bot always sweeps when it cannot clear `bestFloor + 1` and runs otherwise.
- Clearing floor `f` for the first time sets `bestFloor = f` and emits `DungeonFloorFirstCleared`; there is no separate first-clear bonus in MVP (the ladder itself is the progression).

```csharp
ApplyResult StartDungeon(Dungeon d, int floor, ulong seed, string grantId, bool localSeed)   // 01 section 2.1 fields
{
    EnsureDay(now);
    int best = State.Dungeons.BestFloor[(int)d];                     // 02 arrays by Dungeon ordinal (issue #7)
    if (!ContentDb.IsUnlocked("DUNGEONS", State))   return Reject(Locked, "DUNGEON_LOCKED");   // 09 feature_unlocks (issue #45)
    if (State.Combat.Mode == CombatMode.Dungeon)     return Reject(NotAvailable, "DUNGEON_RUN_IN_PROGRESS");
    if (State.Combat.Fight?.EnemyIsBoss == true)     return Reject(NotAvailable, "BOSS_ATTEMPT_IN_PROGRESS");   // issue #42
    if (floor < 1 || floor > best + 1)               return Reject(Locked, "DUNGEON_FLOOR_NOT_UNLOCKED");
    if (!Wallet.CanAfford(TicketOf(d), 1))           return Reject(CannotAfford, "NOT_ENOUGH_" + TicketOf(d));   // checked, not debited (issue #244)
    if (localSeed) Rng["dungeon"].Next();                             // M0/bots: consume the draw LocalDungeonSeedService peeked (01 section 8.8)
    State.Combat.Mode = CombatMode.Dungeon;
    State.Combat.DungeonRun = new DungeonRunState { Kind = d, Floor = floor, Seed = seed, GrantId = grantId,
                                                    StreamCounter = 0, EnemyIndex = 0, TicksLeft = BOSS_TIME_TICKS };
    Streams.ResetTemporary("dungeon_run", seed);                     // temporary stream, not in Rng; counter is DungeonRun.StreamCounter (02 section 3.4); `dungeon` untouched in MVP
    Sink.Emit(new DungeonStarted(d, floor, seed));
    return Accepted;
}

// Called by the tick loop when the run's boss dies (cleared) or TicksLeft reaches 0 (failed).
void EndDungeonRun(bool cleared, double remainingHpPct)
{
    var run = State.Combat.DungeonRun!;
    var (d, floor) = (run.Kind, run.Floor);
    State.Combat.DungeonRun = null;
    State.Combat.Mode = CombatMode.Stage;                            // back to the same stage and kills
    State.Dungeons.RecentRuns.Add(new(run.GrantId, d, floor, cleared));   // 02; bounded list for 08 correlation (issue #52)
    if (State.Dungeons.RecentRuns.Count > DUNGEON_RECENT_RUNS_MAX) State.Dungeons.RecentRuns.RemoveAt(0);   // trim oldest (02)
    if (!cleared) { Sink.Emit(new DungeonFailed(d, floor, remainingHpPct)); return; }   // costs nothing
    int best = State.Dungeons.BestFloor[(int)d];
    Wallet.Apply(new(TicketOf(d), -1), CurrencyReason.DUNGEON, Sink);   // debit on clear only (00 decision 16); from the new day's allotment after a rollover
    Wallet.Apply(RewardOf(d, floor), CurrencyReason.DUNGEON, Sink);
    if (floor > best) { State.Dungeons.BestFloor[(int)d] = floor; Sink.Emit(new DungeonFloorFirstCleared(d, floor)); }
    Quests.Progress(QuestKind.DAILY, QuestGoal.CLEAR_DUNGEON, 1, filter: d.ToString());
    Quests.Progress(QuestKind.WEEKLY, QuestGoal.CLEAR_DUNGEON, 1);
    Sink.Emit(new DungeonCleared(d, floor, RewardOf(d, floor), run.Seed));
}
```

### 6.3 Reward tables

| Dungeon | Reward of floor `f` | Floor 1 | Floor 4 | Floor 12 | Floor 27 |
|---|---|---|---|---|---|
| `FISH_COIN` | `FISH_COIN_FLOOR(f) = 50 * f` | 50 | 200 | 600 | 1,350 |
| `ROCK_CANDY` | `ROCK_CANDY_FLOOR(f) = 10 * f` | 10 | 40 | 120 | 270 |
| `EXP` | `EXP_FLOOR(f) = EXP_DUNGEON_KILLS * ExpPerKill(stage = 20 f)` with `EXP_DUNGEON_KILLS` = 300 | 300 x `EXP0 * EXP_G^20` = 300 x 5 x 1.025^20 = 2,458 | 300 x 5 x 1.025^80 = 10.8K | 300 x 5 x 1.025^240 = 562K | 300 x 5 x 1.025^540 = 927M |

The columns are formula vectors (`EXP0` = 5, `EXP_G` = 1.025, 00 section 6.2; `ExpPerKill` before `EXP_GAIN` training), not day markers. Where the bot stands: a floor needs about stage `20 f` to clear, and the ladder (6.2: the next floor only, two tickets a day) adds at most `DUNGEON_TICKETS_PER_DAY` = 2 floors per dungeon per day from the stage-30 unlock. Against the 00 section 6.6 targets the best floor is therefore about 2 at the end of day 1, 6 on day 3 and 14 on day 7; the stage limit `floor(s / 20)` (23 at day 12, 24 at day 14, 27 at day 30) takes over from about day 12. Illustrative; the tool tracks the real ladder, and whether the ladder rule stays is decision #249 item 3.

Linear tables for Fish Coins and Rock Candy are deliberate: both sinks (Research in 03, Awakening in 6.5) have cost ladders that grow faster than linearly, so the dungeon's share of each currency falls over time and the active sources (board, bosses) stay the main income. The tuning tool must check: (a) `2 * FISH_COIN_FLOOR(f)` is between 25% and 50% of the day's total Fish Coin income at days 1, 7, 30 (the rest from the board, 05; the days and the band are under review in decision #249 item 6, the curve in issue #165); (b) the Rock Candy cadence in 6.5 holds. `power_curve_model.py --check` runs (a) on the bot's own dungeon runs; `fish_model.py --research-schedule tools/out/power_curve_7.json --dungeon-curves` reports it per candidate curve against the attentive board (report only, open question 4).

### 6.4 Floors beyond content

`DUNGEON_FLOORS_MVP` = 40 floors per dungeon, enough for stage 800 (the last `AWAKEN_STAGES` entry). Clearing floor 40 shows "More floors coming" and sweeps floor 40. P1 extends the tables.

### 6.5 Awakening cost cadence and Rock Candy flow

Awakening rules (gates, multiplier) are 03's; this section checks that the Rock Candy economy can pay for them soon after the gate opens. The target is one content table read by both the tuning tool (03) and the bot sweep (10): `awakening_targets` (09; columns `rank`, `max_days_after_gate`), with `max_days_after_gate` = 2 for every rank, i.e. each rank is affordable within 1 to 2 days of the day the bot reaches its `AWAKEN_STAGES` gate. The stage gate is the pacing lever; Rock Candy is a check (issue #43). The table below is illustrative: gate days come from the 00 section 6.6 targets interpolated linearly, and Rock Candy income is the Rock Candy Dungeon under the 6.2 ladder (two clears a day, each the next floor, so day `d` pays floors `2d - 1` and `2d` until `floor(s / 20)` caps the floor from about day 12, 6.3) before any limit-break spend. Boss drops and overflow copies are left out (the tool measures boss drops at about 0.2% of Rock Candy income, decision #249). `tools/power_curve_model.py` and the bot run supply the actual days.

| Rank | `AWAKEN_STAGES` | Gate reached (00 section 6.6) | `AWAKEN_COST_ROCK_CANDY` | Cumulative cost | Cumulative dungeon Rock Candy | Affordable | Days after gate |
|---|---|---|---|---|---|---|---|
| 1 | 50 | day 1 | 100 | 100 | 100 by day 2 (30 + 70) | day 2 | 1 |
| 2 | 150 | day 1 | 300 | 400 | 360 by day 4, 550 by day 5 | day 5 | 4 |
| 3 | 300 | day 3 | 800 | 1,200 | 1,050 by day 7, 1,360 by day 8 | day 8 | 5 |
| 4 | 500 | day 19 | 2,000 | 3,200 | 2,530 by day 11, then 2 x floor 23 = 460 a day: 3,450 by day 13 | before the gate | 0 |
| 5 | 800 | beyond day 30 | 5,000 | 8,200 | | | |

Reading: the intended feel is that every rank lands at most `max_days_after_gate` (2) days after its stage gate ("I can see it, I will have it tomorrow"). Under the current ladder and `ROCK_CANDY_FLOOR` that holds for ranks 1 and 4 but not for ranks 2 and 3, which arrive about 4 and 5 days after their gates; `tools/power_curve_model.py --check` reports the same miss (AC 18). The fix (more Rock Candy per floor, a different ladder rule, or limit-break repricing) is decision #249 items 3 and 5, not this table. Once rank 4 is bought Rock Candy accumulates, and 04's limit breaks are the sink for the surplus. `Awaken` command: validation, reject reasons and effect in 03; debits Rock Candy with `CurrencyReason.AWAKENING`; emits `Awakened(rank)`; progress `AWAKEN_RANK` ladder.

### 6.6 Tower (P1 outline)

A fourth card on `SCR_DUNGEONS`. One ladder of 100 fixed boss fights with `TOWER_FLOOR_STAGE_STEP` = 10 (floor `f` = stage-`10f` boss), no tickets, one attempt at a time, each floor cleared once for a one-time reward row (diamonds every floor, a summon ticket every 5th, a `TICKET_*` bundle every 10th). Tower floors gate dungeon floors as in 6.1. State: `Tower.BestFloor`. Reuses the ticked dungeon run (6.1) with `Dungeon.TOWER`.

## 7. Offline Catch-up (M0)

### 7.1 Algorithm

Runs in `HeroCat.Sim.Catchup`, only inside `Simulation.Apply(ComputeOfflineCatchup(serverNow))`: a logged system command that `SessionRunner` (01) issues on session open and on resume from background, right after the `ApplyServerTime` that carries the same trusted server time (issue #6). Nothing else calls `Compute`, and `SessionRunner` never writes `Offline.Pending` or `LastTickServerTime` itself, so a replay of the command log reproduces catch-up exactly.

```csharp
public sealed record CatchupResult(long ElapsedSec, long CreditedSec, bool Capped, double Gold, double Exp,
                                   int TankFill, int Stage, double KillTimeSec, long ComputedServerTime);   // 02

public static CatchupResult? Compute(GameState s, ContentDb db, long serverNow)
{
    long gap = serverNow - s.Time.LastTickServerTime;
    if (gap < 0)                                                         // grants nothing; LastTick unchanged
    {
        if (-gap > db.CLOCK_BACKWARDS_TOLERANCE_S) s.Sink.Emit(new ClockWentBackwards(-gap));   // smaller is jitter (issue #246)
        return null;
    }
    if (gap < db.OFFLINE_MIN_S)                                          // no modal, no gold or EXP
    {
        var (added, refillAfter) = FishBoard.CatchUpTank(s, db, gap * TICK_HZ);   // 05 section 3: short gaps still refill the tank,
        s.Fish.TankCount += added; s.Fish.TankRefillTick = refillAfter;           // applied directly (issue #21; 02 AC 11 exception)
        s.Time.LastTickServerTime = serverNow;
        return null;
    }

    var  prev     = s.Offline.Pending;                                   // non-null when a reward from an earlier gap is unclaimed
    long capSec   = (long)(Query.OfflineCapH(s, db) * 3600);             // 03's Query: (OFFLINE_CAP_H + 2 h per RES_OFFLINE_CAP level) * 3600: 24 h to 36 h (00 decision 12, issue #21)
    long credited = Math.Max(0, Math.Min(gap, capSec - (prev?.CreditedSec ?? 0)));   // the cap applies to the stacked sum

    int    stage    = s.Combat.Stage;                                    // the stage being farmed, walled or not
    double killTime = Combat.KillTimeSeconds(s, db, stage, isBoss: false); // 03: hits-to-kill * attack interval + SPAWN_DELAY
    double killsPerSec = 1.0 / killTime;
    double gold = credited * db.OFFLINE_RATE * Economy.GoldPerKill(s, db, stage) * killsPerSec;   // GoldPerKill includes GOLD_GAIN training
    double exp  = credited * db.OFFLINE_RATE * Economy.ExpPerKill(s, db, stage)  * killsPerSec;   // includes EXP_GAIN training
    int tankFill = FishBoard.CatchUpTank(s, db, credited * TICK_HZ).added;  // 05 section 3 over credited ticks; read-only here, applied at claim (clamped to TankCap)

    long elapsedSum  = gap + (prev?.ElapsedSec ?? 0);
    long creditedSum = credited + (prev?.CreditedSec ?? 0);
    s.Offline.Pending = new CatchupResult(elapsedSum, creditedSum, creditedSum < elapsedSum,
                                          (prev?.Gold ?? 0) + gold, (prev?.Exp ?? 0) + exp, (prev?.TankFill ?? 0) + tankFill,
                                          stage, killTime, serverNow);   // Stage and KillTimeSec: latest gap, display only (issue #21)
    s.Time.LastTickServerTime = serverNow;                               // moved now, so a crash before claim cannot double-credit
    s.Sink.Emit(new OfflineRewardComputed(s.Offline.Pending));
    return s.Offline.Pending;
}

public static ApplyResult ClaimOfflineReward(GameState s, ContentDb db)
{
    var r = s.Offline.Pending;
    if (r is null) return Reject(NotAvailable, "NO_PENDING_OFFLINE_REWARD");
    s.Wallet.Apply(new(Currency.GOLD, r.Gold), CurrencyReason.OFFLINE, s.Sink);
    s.Wallet.Apply(new(Currency.HERO_EXP, r.Exp), CurrencyReason.OFFLINE, s.Sink);   // writes Hero.Exp; then runs 03's CheckHeroLevel (03 section 5.2)
    s.Fish.TankCount = Math.Min(Query.TankCap(s, db), s.Fish.TankCount + r.TankFill);   // 05 section 7; TankRefillTick per 05 section 3
    s.Offline.Pending = null;
    s.Sink.Emit(new OfflineRewardClaimed(r));
    return Accepted;
}
```

What it does not do, by design: no stage progress, no kills counted, no drops, no Rock Candy, no quest progress, no RNG stream advanced, no boss attempts. Offline time is a rate multiplied by a duration and nothing else, so it is cheap to re-derive on the server (08's plausibility checks `OFFLINE_WINDOW_OVERLAP`, credited offline seconds bounded by the wall-clock gap plus the effective cap including the `RES_OFFLINE_CAP` extension, and `GOLD_DELTA_BOUND`).

Pending-reward persistence: `Offline.Pending` is in the save. If the app dies between compute and claim, the next launch finds the pending reward and shows the modal again rather than recomputing from a moved `LastTickServerTime`. If a second gap occurs while a reward is pending (user backgrounds the app on the modal), the new gap is computed against the already-moved `LastTickServerTime` and added into the pending record (`Gold += ...`, `ElapsedSec += ...`, `TankFill += ...`; the cap applies to the summed `CreditedSec` and `Capped` is recomputed against the sum). `Stage` and `KillTimeSec` are the latest gap's and serve the display only (issue #21).

### 7.2 Worked numbers

Fixture with stubbed per-kill values (AC 9; `OfflineCatchupTests`): `killTime` = 3.5 s (a 3-hit kill: `3 * ATTACK_INTERVAL_TICKS / TICK_HZ + SPAWN_DELAY_TICKS / TICK_HZ = 3.0 + 0.5`) and `GoldPerKill` = 161.5. The 161.5 is a stub that keeps the arithmetic readable; it is not a stage's value under 00's constants (it was `2 * 1.05^90` under the retired `GOLD_G` of 1.05, 00 decision 9).

| Gap | Credited | Gold | Note |
|---|---|---|---|
| 45 s | 0 | 0 | below `OFFLINE_MIN_S`, no modal |
| 2 h | 7,200 s | 7200 x 0.5 x 161.5 / 3.5 = 166K | the between-sessions case |
| 12 h | 43,200 s | 997K | overnight |
| 30 h | 86,400 s (capped, Research 0) | 1.99M | modal shows "capped at 24 h"; with `RES_OFFLINE_CAP` at 6 levels the cap is 36 h and all 30 h are credited |

The in-app rate at the same state is 161.5 / 3.5 = 46 gold/s = 332K over 2 h, so offline earns exactly half of in-app farming, as `OFFLINE_RATE` says.

At the day-1 target, stage 240 (00 section 6.6), with the same 3.5 s kill time: `GoldPerKill = GOLD0 * GOLD_G^240 = 2 * 1.03^240 = 2,410` before `GOLD_GAIN`, so a 2 h gap credits 7200 x 0.5 x 2,410 / 3.5 = 2.48M and a 12 h gap 14.9M (illustrative; the kill time at the frontier comes from 03).

### 7.3 `SCR_OFFLINE_REWARD` content

The modal (layout in 07) shows, from the `CatchupResult` record only:

| Line | Source | Format |
|---|---|---|
| "Welcome back! Away for {elapsed}" | `ElapsedSec` | 07 `TimeText.Duration` |
| "Rewards for {credited} (capped at {cap} h)" | `CreditedSec`, `Capped`; `{cap}` is the effective cap including `RES_OFFLINE_CAP` | the parenthetical only when `Capped` |
| Gold `+{gold}` with icon | `Gold` | 07 suffix ladder |
| EXP `+{exp}` with icon | `Exp` | suffix ladder |
| "Magic Tank refilled (+{n} fish)" | `TankFill` | integer; hidden when 0 |
| "Farmed stage {stage} at {rate}/s" | `Stage`, `Gold / CreditedSec` | muted caption |
| `[ Claim ]` | | sends `ClaimOfflineReward` |
| `[ Claim x2 ]` (P1) | | rewarded ad or `OFFLINE_X2_DIAMONDS` = 100 diamonds; doubles `Gold` and `Exp` only, not tank fish. Hidden in MVP |

The modal cannot be dismissed without claiming (there is no reason to refuse free gold) and the Android back button is ignored on it. Ticks are paused while it is open (01 section 8.7), so the numbers shown are exactly what is credited; combat resumes on the stage shown after Claim (issue #21).

## 8. Shop (MVP)

All prices are content rows (09 `shop_products`, `shop_packages`, and `shop_exchange` for section 8.4). USD tiers map to Apple and Google price tiers in 08; other currencies use store-side tier conversion. Diamonds are the only thing money buys (00 decision 7).

### 8.1 Diamond packs

| SKU | USD | Base diamonds | Bonus | Total | Diamonds per USD | First purchase (one-time per SKU) |
|---|---|---|---|---|---|---|
| `dia_1` | 0.99 | 100 | 0% | 100 | 101 | 200 |
| `dia_2` | 4.99 | 500 | 10% | 550 | 110 | 1,100 |
| `dia_3` | 9.99 | 1,000 | 20% | 1,200 | 120 | 2,400 |
| `dia_4` | 19.99 | 2,000 | 30% | 2,600 | 130 | 5,200 |
| `dia_5` | 49.99 | 5,000 | 40% | 7,000 | 140 | 14,000 |
| `dia_6` | 99.99 | 10,000 | 50% | 15,000 | 150 | 30,000 |

First-purchase double: the first completed purchase of each diamond-pack SKU grants `2 * Total`; packages (8.2) are never doubled (issue #50). The badge "x2 first purchase" is shown per SKU until used. "Used" is server-derived: a SKU is used once a `purchases` row with `status` `granted`, `refunded` or `chargeback` exists for it (02, 08; no extra column), so refunding the doubled purchase does not re-arm the double, and `/shop/catalog` returns the flag as a per-SKU map.

### 8.2 Packages

Packages are diamonds delivered on a schedule, which is what makes them a better rate without selling anything else. Every scheduled portion is materialised as its own Inbox row at purchase time with a future `availableAt` (08 `inbox_items.available_at`; issue #51); a refund expires the portions not yet delivered (08).

| SKU | USD | Grant | Effective diamonds per USD | Limit |
|---|---|---|---|---|
| `pkg_starter` | 2.99 | 600 diamonds now | 201 | once per account |
| `pkg_weekly` | 4.99 | 300 now + 100 per day for 7 days = 1,000 | 200 | once per week key |
| `pkg_monthly` | 9.99 | 1,000 now + 100 per day for 30 days = 4,000 | 400 | once per 30 days from purchase |

The monthly card is the best value in the shop by a factor of 2.7 over the largest pack, and it requires logging in daily to collect, which is the retention contract behind it. Daily portions not claimed within `INBOX_EXPIRY_DAYS` of their `availableAt` expire like any inbox item.

### 8.3 Daily free gift

`ClaimDailyGift`: once per day key, grants 1 `TICKET_SUMMON_WEAPON` (`DAILY_GIFT_WEAPON_TICKETS` = 1) with `CurrencyReason.DAILY_GIFT`, sets `Shop.DailyGiftClaimed` and emits `DailyGiftClaimed(currency, amount)` before its `CurrencyChanged`. It runs `EnsureDay` (section 4.2), then the `SHOP` gate (`SHOP_LOCKED`), then rejects `(AlreadyClaimed, DAILY_GIFT_ALREADY_CLAIMED)` when the flag is set. This is what makes `d_summon_weapon` free and is one of the ticket sources the tuning tool feeds into each banner's pull cadence (section 2, issue #38). The grant is a sim grant like quest tickets; it reaches the server `ticket_ledger` on the next upload (section 1.1 rule 5). The bot claims it first thing.

### 8.4 Exchange

Diamonds for tickets, with daily limits, so a player who wants more dungeon runs has a sink and the daily reset remains the main source.

| Offer id | Price | Grants | Daily limit |
|---|---|---|---|
| `ex_ticket_fish` | 50 diamonds | 1 `TICKET_FISH_COIN` | 2 |
| `ex_ticket_candy` | 50 diamonds | 1 `TICKET_ROCK_CANDY` | 2 |
| `ex_ticket_exp` | 50 diamonds | 1 `TICKET_EXP` | 2 |

The offers are the rows of 09's `shop_exchange` (section 5.37; issue #174): `price_currency` must be `DIAMOND` and `grant_currency` a dungeon ticket (`E059`), so nothing else can be put on sale by a sheet edit.

`ExchangeItem(offerId, qty)`: `qty` in 1..remaining limit. It runs `EnsureDay`, then the `SHOP` gate (`SHOP_LOCKED`), then rejects in this order: `UNKNOWN_ID`, `BAD_QUANTITY` (`qty < 1`), `EXCHANGE_LIMIT_REACHED` (`qty` above `daily_limit - ExchangeCounts[offerId]`), `NOT_ENOUGH_DIAMOND` for `qty * price_amount` (section 1.3); a rejected purchase changes nothing, so there is no partial buy. Accepted, it adds `qty` to `Shop.ExchangeCounts[offerId]` (02 `ShopState`), emits `ItemExchanged(offerId, qty, diamonds)`, then debits `qty * price_amount` diamonds and grants `qty * grant_amount` tickets, both with `CurrencyReason.SHOP_EXCHANGE` (the debit is a client-side spend, counted in `Stats.ClientDiamondsSpent`, section 1.1 rule 5). Summon tickets are not sold here; a pull is already priced in diamonds on `SCR_SUMMON`. Fish Coins, Rock Candy and gold are not sold for diamonds either; see 8.5.

### 8.5 Never sold

Gold, Fish Coins, Rock Candy, EXP, any collectible, pity progress, banner level, Awakening ranks, Research nodes, dungeon floors. Each is reachable only by playing, which keeps the plausibility checks in 08 simple (every one of these has a play-rate upper bound) and keeps pillar 4 true.

### 8.6 Monetization funnel notes

- First purchase a payer makes: `pkg_starter` (2x the best pack rate, one-time, 2.99) in the first two days, usually right after the first hard pity on the Weapon banner teaches them what 1,000 diamonds buys. The shop surfaces it as the first card with a 48 h countdown from profile creation (cosmetic urgency only; it stays purchasable after).
- Second: `pkg_monthly`, surfaced after the starter is used. Converts the daily login habit into a 30-day commitment.
- Packs are for whales and for the "I want this 10-pull now" moment; `dia_3` with the first-purchase double is the sweet spot the UI highlights.
- IAP is never required to finish any mission or claim any bonus. No mission, ladder or bonus has an IAP goal.
- Conversion KPIs (10): day-7 payer conversion target 3%, ARPDAU target 0.08 USD at soft launch.

## 9. Inbox (MVP)

Server-authored grants awaiting claim. The client never creates inbox items; it lists and claims (08 `GET /inbox`, `POST /inbox/claim` and `POST /inbox/claim-all` with `requestId`). The JSON below is the client model; the wire format is 08 section 3.2's (`inboxItemId`, a lowercase `kind` for `type`, `params` for `bodyArgs`, grant lines `{ "kind": "currency", "id": "DIAMOND", "amount": 2400 }`).

```json
{
  "id": "ibx_01HZX...",
  "type": "IAP",                      // IAP | PACKAGE_DAILY | MAIL | COMPENSATION | COUPON (P1) | REFUND
  "titleKey": "inbox.iap.title",
  "bodyKey": "inbox.iap.body",
  "bodyArgs": { "sku": "dia_3" },
  "grants": [ { "currency": "DIAMOND", "amount": 2400 } ],
  "availableAt": 1767225600,          // server seconds; hidden until then (package daily portions)
  "expiresAt": 1769817600,            // availableAt + INBOX_EXPIRY_DAYS * 86400
  "claimed": false
}
```

Rules:

1. `INBOX_EXPIRY_DAYS` = 30 default; `IAP` items never expire (`expiresAt` null); compensation may set a shorter window.
2. `ClaimInbox(id)` and `ClaimAllInbox()` are server mutations with `requestId` idempotency (00 item 13). The sim applies the grant only after the server confirms; the local `Inbox.ClaimedIds` set (02) is a cache for offline rendering.
3. Claim-all claims every available, unexpired item in one request; the response lists per-item results so a partial failure is shown per row.
4. Grants may contain any currency including Rock Candy and Fish Coins (compensation), because the Inbox is not a sale.
5. Badge: count of available unclaimed items, shown on the HUD inbox icon (07).
6. Scheduled rows: `GET /inbox` hides rows whose `availableAt` is in the future (08). The client shows at most one upcoming portion per package (07), so a monthly card does not fill the list (issue #51).

As built (issue #176): Runtime's `InboxFlow` runs `ClaimInbox` / `ClaimAllInbox` over `IInboxService` and ends each confirmed row with `ApplyInboxGrant`. The grant's reason is the server's `reason`, else the type's: `IAP` and `PACKAGE_DAILY` are `IAP`, `REFUND` is `REFUND`, `ADMIN` is `ADMIN`, and `MAIL`, `COMPENSATION` and `COUPON` are `MAIL`. `ApplyInboxGrant` rejects, mutating nothing: `BadArgument`/`INBOX_BAD_GRANT` (no id, a reason outside those four, or a line that is not a known currency with a positive amount), `NotAvailable`/`INBOX_ITEM_GRANT_UNSUPPORTED` (an item line, issue #271), and `AlreadyClaimed`/`INBOX_ALREADY_APPLIED` (the id is already in `Inbox.ClaimedIds`). The flow does not issue a grant whose id is already there, so a replay or a reconciled `INBOX_ALREADY_CLAIMED` credits nothing twice. A grant with no lines (a refund notice) only records its id. Rows before `availableAt` or at or past `expiresAt` are neither listed as claimable nor counted in the badge. A network failure is retried with the same `requestId` while the row shows "Retrying..." (rule 12.8). After 10 s the claim fails as retryable, and the player's retry replays the same `requestId`.

## 10. Rewarded Ads and Events (P1 outline)

- Rewarded Ads: `d_watch_ad` mission (5.2), `Claim x2` on `SCR_OFFLINE_REWARD` (7.3), one free `TICKET_FISH_COIN` per day after an ad. Every ad reward is also purchasable with diamonds at the listed price so a player who cannot see ads is never locked out. Ad completion is a server-verified callback (08) that lands an Inbox item; the client never grants on the ad SDK's callback alone.
- Events: time-boxed `QuestDef` sets with kind `EVENT` and a schedule row (start, end, server-delivered), rewards into the Inbox at claim. Rate-up banners are 04/08. Season pass is a two-track event ladder with the paid track unlocked by a diamond purchase.
- Guild Coins: earned by guild check-in and guild boss damage, spent in a guild shop of `shop_products` rows priced in `GUILD_COIN`.

## 11. Sinks by day (greedy F2P bot)

**Model output** of `tools/power_curve_model.py --check`: 20-seed medians of what the greedy F2P bot earns and spends on the day, each column its own median, for content 2026.11.01.2 with the fallback rows the tool names (Research, dungeons, companions, achievements; issue #178). The tool prints this table and the section 2 table on every `--check` run; rerun it after a content change rather than editing the numbers by hand. Gold is kills plus Offline Catch-up; diamonds are section 2's sources; the Fish Coin columns split the board's income (05: discovery, merges, discards) from the Fish Coin Dungeon's; the dungeon floor is the 6.2 ladder's best floor at the end of the day (the same in all three dungeons).

| Day | Stage | Gold earned | Gold spent (Training) | Diamonds earned | Diamonds spent | Fish Coins: board | Fish Coins: dungeon (floor) | Fish Coins spent (Research) | Rock Candy earned (dungeon / boss) | Rock Candy spent | Awakening rank |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 210 | 3.1e6 | 3.1e6 | 3,240 (with the 1,500 tutorial grant) | 1,500 | 12,093 | 150 (floor 2) | 0: Research opens at Awakening rank 1, bought day 2 | 41 (30 / 11) | 0 (rank 1 bought day 2) | 0 |
| 7 | 430 | 4.6e10 | 4.6e10 | 560 | 500 | 5,031 | 1,350 (floor 14) | 7,017 | 271 (270 / 1) | 0: banking toward rank 3 (bought day 8) | 2 |
| 30 | 590 | 9.8e12 | 9.9e12 | 380 | 400 | 10,670 | 2,900 (floor 29) | 24,727 (from income and savings) | 580 (580 / 0) | 580, all to limit breaks (04) | 4 |

What the tool checks against this table (issue #178; report-only until decision #249):

1. Gold is fully spent: Training gets at least 99% of the gold earned over 30 days, so Training alone sets the gold layer's share. Model: 99.85%, passes.
2. Diamonds are spent within 2.5 days in steady state (section 2). Model: passes.
3. The Fish Coin Dungeon's share of the day's Fish Coin income sits in the 6.3 band (25% to 50% on days 1, 7 and 30). Model: 1.2%, 21.2% and 21.6% (medians), under the band on all three days, because the board (05) earns far more than this spec's earlier estimate. The reward curve is issue #165 and the band and its days are decision #249 item 6 (Open question 4).
4. The EXP Dungeon is 10% to 20% of the day's hero EXP (`EXP_DUNGEON_KILLS`, checked on days 7 and 30). Model: 0.02% and 0.5%. Two clears of `300 * ExpPerKill(20 f)` are small next to the day's kills and Offline Catch-up (about 23,000 kill-equivalents at the frontier), and on day 7 the ladder's floor 14 is the stage-280 boss against a frontier at 430. This is a balance call (the band or `EXP_DUNGEON_KILLS`), decision #266 item A.
5. The best dungeon floor on day 30 is 25 to 29 (Open question 5). Model: median 29, 30 on 5 of 20 seeds.
6. Rock Candy cadence (6.5, AC 18) and the boss-drop share are the Awakening checks 03 section 8.6 already runs; limit breaks take 75% of Rock Candy spend (decision #249 item 5).

## 12. Anti-frustration rules

1. Dungeon tickets are only consumed on a clear (6.2), and the UI tells the player whether a floor is clearable before they start.
2. Offline rewards are never lost: the pending record survives crashes and stacks a second gap (7.1). The cap is shown, not hidden.
3. Login diamonds are automatic; the only thing a player can forget is claiming completed missions, and `SCR_QUESTS` has one Claim All button with a red dot while anything is claimable (07).
4. Every diamond pack and package shows the per-diamond rate and the first-purchase double so the best offer is legible without a calculator.
5. Pity is server-held per banner and shown on `SCR_SUMMON` (04); the economy never asks the player to spend to "unlock" pity.
6. Summon tickets never expire; dungeon tickets reset to a floor, never below what the player bought.
7. No mission requires spending diamonds for the 8-of-11 bonus (5.2).
8. A failed server mutation (claim, exchange, purchase) is retried with the same `requestId` and shows "Retrying..." rather than an error for the first 10 s (01 retry policy); the player never sees a debit without the matching grant.
9. Awakening is confirmed with a cost dialog naming the exact Rock Candy balance after, because it is the single largest soft-currency spend in the game.

## 13. Analytics events

This section owns these events' fields (issue #245); every event also carries 02 section 14.1's envelope (`dayIndex`, `stage`; the day key is `ts / DAY_SECONDS`), and 02 section 14.4 indexes them. Other specs' `currency_changed` rows only name their reasons.

| Event | Fields | Emitted on |
|---|---|---|
| `currency_changed` | `currency` (code from 00 section 5.2), `delta` (double), `balanceAfter`, `reason` (`CurrencyReason`, 00 section 8 item 6), `refId` (item, quest, offer or banner id when applicable) | every `Wallet.Apply`, including the profile-created `TUTORIAL` grant, whose rows Runtime's `ProfileFactory` sends itself because no `Simulation` exists yet (issue #246); `KILL` and `BOSS` deltas are summed per currency over `CURRENCY_AGG_SEC` and emitted once (`balanceAfter` at emission), every other reason once per apply |
| `quest_completed` | `questId`, `kind` | `QuestCompleted` |
| `quest_claimed` | `questId`, `kind`, `rewardCurrency`, `rewardAmount`, `bulk` | `QuestClaimed` |
| `achievement_unlocked` | `questId`, `rung` | achievement `QuestCompleted` |
| `dungeon_run` | `kind`, `floor`, `outcome` (cleared/failed/swept/abandoned), `ticksUsed`, `rewards[]`, `grantId` | `DungeonCleared`, `DungeonFailed`, `DungeonSwept`, `DungeonAbandoned` |
| `offline_catchup` | `elapsedSec`, `creditedSec`, `capped`, `gold`, `exp`, `tankFill`, `pendingStage` (the pending record's stage; the envelope's `stage` is the current one) | `ComputeOfflineCatchup` that credits a gap of at least `OFFLINE_MIN_S` (section 7); a catch-up with no later `offline_claimed` is a reward cleared unclaimed (02 P18) |
| `offline_claimed` | `elapsedSec`, `creditedSec`, `capped`, `gold`, `exp`, `tankFill`, `x2` | `OfflineRewardClaimed` |
| `day_rollover` | `dayKey`, `skippedDays`, `secondsSinceLastTick` | `DayRolledOver` |
| `daily_gift_claimed` | | `DailyGiftClaimed` (`ClaimDailyGift`) |
| `exchange` | `offerId`, `qty`, `diamonds` | `ItemExchanged` (`ExchangeItem`) |
| `shop_view` | `tab` | `SCR_SHOP` opened (07 section 4.16) |
| `iap_initiated`, `iap_completed`, `iap_failed` | `sku`, `platform`, `priceTier`, `priceMinor`, `currency` (store ISO code), `firstPurchase`, `latencyMs`, `errorCode` (`iap_failed` only; `RECEIPT_INVALID` for a `400` from validate) | client purchase flow (01, 08 section 5): store sheet opened, store success, store or validate failure |
| `iap_status` | `sku`, `platform`, `status` (validated/granted/invalid/refunded/chargeback), `transactionIdHash`, `priceMinor`, `currency`, `firstPurchase` | server, on each `purchases` status change (08 sections 5.2 and 5.4) |
| `inbox_claimed` | `itemId`, `type`, `grants[]`, `bulk`, `ageSec` | claim confirmed |

`awakened` is 03 section 11's; `clock_anomaly` (with `kind = backwards` for `ClockWentBackwards`) is 02 section 14.3's.

`currency_changed` as built (`HeroCat.Runtime.Session.CurrencyAnalytics`, attached by the boot flow to the session's `EventBus` whenever analytics is not `NullAnalytics`; issue #155): `KILL` and `BOSS` rows are summed per currency and reason, so one window yields at most one `KILL` and one `BOSS` row per currency. The window is `CURRENCY_AGG_SEC` of sim time (ticks), opened by the first `KILL`/`BOSS` apply; it closes when a later `CurrencyChanged` (or `EnemyKilledBatch`) arrives past its end and whenever the analytics sink flushes (pause, quit). `balanceAfter` of an aggregated row is the balance in the state when the row is emitted (`Hero.Exp` for `HERO_EXP`). Kills folded into `EnemyKilledBatch` (01 section 2.3) count through its `KillGold` and `KillExp`. Deltas are summed as `double`, so the rows' deltas equal the wallet delta up to floating-point summation order. `refId` is omitted until the emitter of the apply supplies one.

The other rows as built (`HeroCat.Runtime.Session.FeatureAnalytics`, attached next to `CurrencyAnalytics`; issue #177): each maps one sim event to one row, except `quest_claimed`, which reads the runner's `CommandApplied` notice (the events of one accepted command) so `bulk` is true exactly for the rows of a `ClaimAllQuests`. `quest_completed` is sent for daily and weekly missions (a reward-0 row such as `d_login` included) and `achievement_unlocked` instead of it for an achievement rung. `offline_catchup` carries the pending record after the gap (the sum when a reward was already pending), so its `stage` is named `pendingStage`: 02 section 14.1's sink drops an owner field that reuses an envelope name. `offline_claimed.x2` is false until the P1 rewarded-ad doubling exists. `day_rollover`'s `skippedDays` and `secondsSinceLastTick` come from `DayRolledOver` (section 4.2). Not emitted yet: `dungeon_run` and `inbox_claimed` (their sim systems are not built), `shop_view` (the typed `AnalyticsEvents.ShopView` waits for `SCR_SHOP`) and the `iap_*` events (08's purchase flow).

## Open questions

1. **Late-game diamond taper.** Steady state (days 15-28) is a median 380 per day against the 400 target in `tools/power_curve_model.py` (section 2; the tool's check allows 10% under, `E039`'s tolerance). Owner: economy. Default: accept. Alternative: raise the seven weekly rows' `reward_amount` to 60 (adds 10 per day).
2. **Forfeit unclaimed daily rewards at rollover.** Owner: product. Default: forfeit (genre standard), with a P1 push notification one hour before rollover when anything is claimable.
3. **Daily free gift content.** One `TICKET_SUMMON_WEAPON` per day adds 100 diamonds of pull value per day (25% on top of the diamond budget); its effect on banner levels comes from the tool's cadence output (issue #38). Owner: economy with 04. Default: keep; the Weapon banner is the cheapest and the gift is what makes `d_summon_weapon` free.
4. **Fish Coin dungeon share.** Whether linear `FISH_COIN_FLOOR` keeps the dungeon inside the 25% to 50% band of daily Fish Coin income. This spec's earlier merge estimate had it overshooting by day 7; the current `tools/power_curve_model.py` run has it under the band on days 1, 7 and 30 (1%, 21%, 22%; section 11). Owner: economy with 05. Default: the tool output decides the curve (issue #165; `50 * f^0.7` was the candidate for an overshoot), and the band and the days it applies to are decision #249 item 6. Tool output (`fish_model.py --dungeon-curves`, seed-7 research and floor schedule, best floor 2 / 14 / 30): `50 * f` gives 1.6% / 21.7% / 22.3% on days 1 / 7 / 30 and `50 * f^0.7` gives 1.3% / 11.2% / 9.4%, so the sublinear curve moves away from the band. A linear `K * f` reaches 25% with K of about 60 on days 7 and 30; day 1 would need K of about 1,000, because the dungeon opens at stage 30 at floor 2 while the board pays about 12,000 discovery coins. Not decided (user sign-off, issue #165).
5. **Dungeon floor difficulty step.** `DUNGEON_FLOOR_STAGE_STEP` = 20 ties floor progress to stage progress 1:20. Owner: 03. Default: keep; the tool checks that the bot's best floor at day 30 is 25 to 29.
6. **Ticket consumed on clear only.** Removes all risk from dungeon runs. Owner: product. Default: keep (anti-frustration rule 1); if runs feel weightless in playtest, add a cosmetic "attempt" counter, not a ticket cost.

## Acceptance criteria

1. Scripted reference day (login, 8 dailies completed and claimed including both bonus rows, 4 new boss stages, one seventh of the weekly set) adds exactly 400 diamonds to `Wallet`.
2. `DayKey(DAY_SECONDS - 1) != DayKey(DAY_SECONDS)`; `WeekKey` changes between day 3 and day 4 of the epoch (Sunday to Monday) and not between day 4 and day 10.
3. `EnsureDay` with `now` smaller than the stored day key's start changes nothing and emits no `DayRolledOver`; a subsequent larger `now` on the same day key also grants nothing.
4. Rollover with `TICKET_FISH_COIN` = 0, 1, 2, 5 leaves 2, 2, 2, 5.
5. Rollover with a `COMPLETED` unclaimed daily mission forfeits it (state `IN_PROGRESS`, progress 0, no `CurrencyChanged`).
6. `d_boss` completes after three `AttemptBoss` commands on a walled stage with zero boss kills.
7. `d_bonus_8` becomes `COMPLETED` when the eighth daily row (login counted) completes, regardless of claim state of the others.
8. 100 `MergeFish` events advance `d_merge` to 100 (past target 15), `w_merges` to 100, `LIFETIME_MERGES` rung 50 to `COMPLETED`, and `d_merge` rewards exactly once on claim.
9. Offline (Research 0): elapsed 45 s grants no gold or EXP, leaves `Offline.Pending` null and refills the tank by 05's `CatchUpTank` over 450 ticks; elapsed 7,200 s at a state stubbed to `KillTimeSeconds` = 3.5 and `GoldPerKill` = 161.5 (section 7.2) yields `Gold` = 166,114 within 1e-6 relative; elapsed 30 h yields `CreditedSec` = 86,400 and `Capped` = true.
10. Offline compute followed by a crash (state saved) and reload shows the same pending reward; claiming grants it once; a second compute with a 2 h gap while pending sums into the record.
11. `Compute` with `serverNow < LastTickServerTime` grants nothing and leaves `LastTickServerTime` unchanged; it emits `ClockWentBackwards` only when the gap exceeds `CLOCK_BACKWARDS_TOLERANCE_S` (issue #246).
12. Offline catch-up advances no RNG stream counter, no quest progress, no `Combat.Stage`, no `Progress.HighestCleared`, no `Dungeons.BestFloor[*]`.
13. `StartDungeon` debits nothing and sets `Combat.Mode = Dungeon`; a run that ticks to `Failed` leaves the ticket balance unchanged, emits `DungeonFailed` and returns to `Mode = Stage`; a run that ends `Cleared` debits exactly one ticket and grants `RewardOf(d, floor)`. A save with a run in progress, reloaded, abandons it (`DungeonAbandoned`) with the ticket balance unchanged.
14. `StartDungeon(d, bestFloor + 2)` is rejected `(Locked, DUNGEON_FLOOR_NOT_UNLOCKED)`; `StartDungeon` at `Progress.HighestCleared` = 29 is rejected `(Locked, DUNGEON_LOCKED)`; with 0 tickets it is rejected `(CannotAfford, NOT_ENOUGH_TICKET_FISH_COIN)` for the Fish Coin dungeon; during a boss fight it is rejected `(NotAvailable, BOSS_ATTEMPT_IN_PROGRESS)`; while a run is active `StartDungeon` and `SweepDungeon` are rejected `(NotAvailable, DUNGEON_RUN_IN_PROGRESS)`; board commands during a run are rejected `(Locked, BOARD_LOCKED)` (05).
15. Same save, same seed, same floor: the ticked run and `PreviewDungeon` return identical outcomes and identical `dungeon_run` stream counters on x64 and ARM64 (golden test, 10); the persistent `dungeon` stream counter is unchanged by an MVP run.
16. `SweepDungeon` grants exactly `RewardOf(d, bestFloor)`, emits `DungeonSwept`, and is rejected `(NotAvailable, NOTHING_TO_SWEEP)` when `bestFloor` = 0.
17. `ROCK_CANDY_FLOOR(f)` = 10 f and `FISH_COIN_FLOOR(f)` = 50 f for f in {1, 4, 12, 27}; `EXP_FLOOR(4)` = 300 x 5 x 1.025^80 (`EXP_G` = 1.025, 00 section 6.2; about 10,814) within 1e-9 relative.
18. Awakening cadence: for every rank in `awakening_targets`, the bot run (10) buys the rank no more than `max_days_after_gate` (2) days after the day it first reaches that rank's `AWAKEN_STAGES` gate (issue #43).
19. `ClaimDailyGift` twice in one day key: second is rejected `(AlreadyClaimed, DAILY_GIFT_ALREADY_CLAIMED)`; after rollover it succeeds.
20. `ExchangeItem(ex_ticket_fish, 3)` is rejected `(Capped, EXCHANGE_LIMIT_REACHED)`; `(ex_ticket_fish, 2)` debits 100 diamonds and grants 2 tickets; a further `(ex_ticket_fish, 1)` the same day is rejected.
21. Every shop row: `Total` = `Base * (1 + Bonus)` exactly; the first granted purchase of each diamond-pack SKU grants `2 * Total` and a second purchase of the same SKU grants `Total`; no package is doubled; `pkg_monthly` creates 31 inbox rows summing to 4,000, of which only those with `availableAt <= now` are listed; a refund expires the undelivered ones (issues #50, #51).
22. Every `Wallet.Apply` emits exactly one `CurrencyChanged` with a `CurrencyReason`; a debit below zero returns false, leaves the balance unchanged (no clamp) and emits nothing.
23. Inbox claim with a replayed `requestId` returns the original result and grants nothing twice (08 contract, exercised from the client).
24. `ClaimAllQuests(DAILY)` with nothing completed is rejected `(NotAvailable, NOTHING_TO_CLAIM)`; with three completed emits three `QuestClaimed` and one `QuestsBulkClaimed`.
25. Analytics: a scripted day produces one `currency_changed` row per non-`KILL`/`BOSS` apply; over one simulated hour the `KILL` rows number at most `3600 / CURRENCY_AGG_SEC = 60` per currency and the sum of all rows' deltas equals the wallet delta (moved from 02 acceptance 17).
26. Day rollover grants exactly `LOGIN_DIAMONDS` once with `CurrencyReason.LOGIN`; `d_login` reaches `CLAIMED` with no `CurrencyChanged` and counts toward `d_bonus_5`/`d_bonus_8`; a second `ApplyServerTime` on the same day key grants nothing (issue #20).
27. Offline: a 2 h gap stores `TankFill` in `Offline.Pending` and leaves `Fish.TankCount` unchanged until `ClaimOfflineReward`, which adds it bounded by `TankCap`; with `RES_OFFLINE_CAP` at 6 levels a 40 h gap yields `CreditedSec` = 129,600; two stacked gaps of 20 h each at Research 0 yield `CreditedSec` = 86,400, `ElapsedSec` = 144,000, `Capped` = true and the second gap's `Stage` (issue #21).
28. Replaying the command log (`ApplyServerTime`, `ComputeOfflineCatchup`, `Advance`, commands) from the prior save reproduces the same `Offline.Pending`, day keys and `StateHash` as the live session (issues #5, #6).
29. Every economy rejection returns the `RejectCode` and `Reason` key listed in section 1.3, changes no state and emits no event (issue #11).
30. Before the `QUESTS` unlock row is met, `ClaimQuest` and `ClaimAllQuests` are rejected `(Locked, QUESTS_LOCKED)` while progress still counts; after it, the completed missions are claimable (issue #45).
31. Content build: every `OWN_*` achievement `target` is strictly less than that kind's pool size, so the shipped top rungs are 25, 35 and 45 (issue #44).
32. A scripted day of sim grants (login, quests, daily gift, first clears) increments `Stats.ClientDiamondsEarned[reason]` and `Stats.ClientTicketsEarned[kind][reason]` by exactly the amounts applied, so the next upload credits the server ledgers by that delta (issue #49).

## Constants introduced

| Name | Value | Kind | Tuning tool check |
|---|---|---|---|
| Quest rows' `reward_amount` (5.2, 5.3) | 20 daily, 30 / 70 bonus, 50 weekly | S | Single source of quest diamonds; `E039` reference day within 10% of 400 (issues #55, #244) |
| `REF_DAILIES_DONE` | 8 | S | Reference-day input to `E039` (issue #55) |
| `REF_NEW_BOSSES` | 4 | S | Reference-day input to `E039` (issues #55, #244) |
| `FIRST_CLEAR_DIAMONDS` | 10 | I (sibling `FIRST_CLEAR_GEMS`) | Bot's first-clear diamonds over days 1, 2-3, 4-7, 8-14 and 15-30 within 20% of what the 00 section 6.6 target curve gives (section 2). M0 (section 1.2) |
| `DUNGEON_TICKETS_PER_DAY` | 2 | S | Rock Candy cadence in 6.5 |
| `feature_unlocks` rows `DUNGEONS`, `QUESTS` | `STAGE_HIGHEST >= 30`, `>= 10` | S | Bot unlocks dungeons on day 1; replaces `DUNGEON_UNLOCK_STAGE` (issue #45) |
| `DUNGEON_FLOOR_STAGE_STEP` | 20 | S | Best floor at day 30 in 25 to 29 |
| `DUNGEON_FLOORS_MVP` | 40 | S | |
| `FISH_COIN_FLOOR(f)` | `50 * f` | S | 25% to 50% of daily Fish Coin income (Open question 4) |
| `ROCK_CANDY_FLOOR(f)` | `10 * f` | S | Every rank affordable within `awakening_targets.max_days_after_gate` (2) days of its gate (issue #43) |
| `EXP_DUNGEON_KILLS` | 300 | S | EXP dungeon is 10% to 20% of daily EXP on days 7 and 30 (section 11) |
| `TOWER_FLOOR_STAGE_STEP` (P1) | 10 | S | |
| `OFFLINE_X2_DIAMONDS` (P1) | 100 | S | |
| `DAILY_GIFT_WEAPON_TICKETS` | 1 | S | Open question 3; input to the tool's per-banner pull cadence (issue #38) |
| `awakening_targets.max_days_after_gate` | 2 per rank | S | AC 18 (issue #43) |
| `INBOX_EXPIRY_DAYS` | 30 | S | |
| Shop rows (8.1, 8.2, 8.4) | as tabled; 8.4's are 09 `shop_exchange` rows | S | Conversion KPIs in 10 |
| `CurrencyReason` | 00 section 8 item 6's list (section 1) | | Resolved, issue #11 |
| Reject reasons | section 1.3 | | |
| `QuestGoal` enum, `WEEKLIES_COMPLETED` | section 5.1 | | |
| `CURRENCY_AGG_SEC` | 60 | I | None; engineering. `KILL`/`BOSS` `currency_changed` aggregation window (section 13; moved from 02, issue #245) |
