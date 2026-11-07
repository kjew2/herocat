using System;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>The outcome of one dungeon run, real or previewed (06 sections 6.1-6.2).</summary>
    public sealed record DungeonRunResult(Dungeon Dungeon, int Floor, ulong Seed, bool Cleared, double RemainingFraction, int TicksUsed,
                                          long StreamCounter, double BossMaxHp);

    /// <summary>
    /// What <see cref="Simulation.PreviewDungeon(GameState, ContentDb, Dungeon, int, ulong?)"/> returns: the run's result plus the
    /// DPS multiple the floor needs, for SCR_DUNGEONS' "Clearable" / "Not clearable yet (need about x1.4 DPS)" (06 section 6.2).
    /// </summary>
    public sealed record DungeonPreview(DungeonRunResult Result, double DpsRatioNeeded)
    {
        public bool Cleared => Result.Cleared;

        /// <summary>The boss's HP left when time ran out, in [0, 1]; 0 when cleared.</summary>
        public double RemainingFraction => Result.RemainingFraction;
    }

    /// <summary>
    /// Dungeons (06 section 6; issues #172, #173). A run is ticked in the main loop (issue #42): StartDungeon validates, sets
    /// Combat.Mode = Dungeon and stores Combat.DungeonRun; each tick then runs 03 section 2.1's steps against the floor's single
    /// boss (no fish missiles, no Tap Zap) with the hero's crits drawn from the temporary `dungeon_run` stream seeded by the run
    /// seed; the frontier fight (Combat.Fight and the stage fields) stays frozen and resumes unchanged. The run ends Cleared when
    /// the boss dies or Failed when TicksLeft reaches 0; only a clear debits the ticket (00 decision 16) and grants
    /// RewardOf(dungeon, floor). SweepDungeon grants RewardOf(dungeon, bestFloor) for a ticket without a fight. Tickets top up
    /// to tickets_per_day at the rollover, never down (06 section 4.2).
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>The feature_unlocks row that gates every dungeon (09 section 5.34; STAGE_HIGHEST >= 30; absent in M0).</summary>
        const string DungeonsFeature = "DUNGEONS";

        /// <summary>The last run that ended in this Simulation (null before any): its outcome and `dungeon_run` stream counter.</summary>
        public DungeonRunResult LastDungeonRun { get; private set; }

        /// <summary>RewardOf(dungeon, floor) (06 section 6.3): the floor row's currency and amount (PARTY_EXP resolved at build).</summary>
        public static CurrencyDelta RewardOf(DungeonFloorDef floor) => new CurrencyDelta(floor.RewardCurrency, floor.RewardAmount);

        // ------------------------------------------------------------------------------------- rollover (06 section 4.2)

        partial void TopUpDungeonTickets(int dayKey)
        {
            var dg = State.Dungeons;
            Array.Clear(dg.EntriesToday, 0, dg.EntriesToday.Length);
            Array.Clear(dg.SweepsToday, 0, dg.SweepsToday.Length);
            foreach (var kind in DungeonKinds)
            {
                var def = Db.DungeonOf(kind);
                if (def?.TicketCurrency is not Currency ticket) continue;   // no row in this build (M0)
                double have = State.Wallet.Get(ticket);
                if (have < def.TicketsPerDay)   // max(current, tickets_per_day): bought and mailed tickets above it survive
                    Ledger.Apply(State, new CurrencyDelta(ticket, def.TicketsPerDay - have), CurrencyReason.DAILY_RESET, this);
            }
        }

        static readonly Dungeon[] DungeonKinds = (Dungeon[])Enum.GetValues(typeof(Dungeon));

        // ------------------------------------------------------------------------------------- commands (06 section 6.2)

        internal ApplyResult HandleStartDungeon(StartDungeon cmd)
        {
            EnsureDayNow();   // a run started on a new day sees the new day's tickets
            var c = State.Combat;
            if (!Db.IsUnlocked(DungeonsFeature, State)) return ApplyResult.Reject(RejectCode.Locked, DungeonRejectReasons.DUNGEON_LOCKED);
            var def = Db.DungeonOf(cmd.Dungeon);
            if (def?.TicketCurrency is not Currency ticket)
                return ApplyResult.Reject(RejectCode.BadArgument, DungeonRejectReasons.INVALID_DUNGEON);
            if (c.Mode == CombatMode.Dungeon) return ApplyResult.Reject(RejectCode.NotAvailable, DungeonRejectReasons.DUNGEON_RUN_IN_PROGRESS);
            if (c.Fight != null && c.Fight.Phase == FightPhase.Fighting && c.Fight.EnemyIsBoss)   // issue #42: finish the boss first
                return ApplyResult.Reject(RejectCode.NotAvailable, DungeonRejectReasons.BOSS_ATTEMPT_IN_PROGRESS);
            int best = State.Dungeons.BestFloor[(int)cmd.Dungeon];
            if (cmd.Floor < 1 || cmd.Floor > best + 1 || def.Floor(cmd.Floor) == null)   // past the last floor: "More floors coming" (06 section 6.4)
                return ApplyResult.Reject(RejectCode.Locked, DungeonRejectReasons.DUNGEON_FLOOR_NOT_UNLOCKED);
            if (!State.Wallet.CanAfford(ticket, 1))   // checked, not debited (00 decision 16)
                return ApplyResult.Reject(RejectCode.CannotAfford, CollectionRejectReasons.NotEnough(ticket));

            if (cmd.LocalSeed) RngStreams.NextUnit(State.Rng[RngStreams.Dungeon]);   // M0/bots: consume the draw LocalDungeonSeed peeked
            BeginDungeonRun(def, cmd.Floor, cmd.Seed, cmd.GrantId ?? "");
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleSweepDungeon(SweepDungeon cmd)
        {
            EnsureDayNow();
            if (!Db.IsUnlocked(DungeonsFeature, State)) return ApplyResult.Reject(RejectCode.Locked, DungeonRejectReasons.DUNGEON_LOCKED);
            var def = Db.DungeonOf(cmd.Dungeon);
            if (def?.TicketCurrency is not Currency ticket)
                return ApplyResult.Reject(RejectCode.BadArgument, DungeonRejectReasons.INVALID_DUNGEON);
            if (State.Combat.Mode == CombatMode.Dungeon)
                return ApplyResult.Reject(RejectCode.NotAvailable, DungeonRejectReasons.DUNGEON_RUN_IN_PROGRESS);
            int best = State.Dungeons.BestFloor[(int)cmd.Dungeon];
            var floor = def.Floor(best);
            if (best < 1 || floor == null) return ApplyResult.Reject(RejectCode.NotAvailable, DungeonRejectReasons.NOTHING_TO_SWEEP);
            if (!State.Wallet.CanAfford(ticket, 1))
                return ApplyResult.Reject(RejectCode.CannotAfford, CollectionRejectReasons.NotEnough(ticket));

            var reward = RewardOf(floor);
            State.Dungeons.SweepsToday[(int)cmd.Dungeon] += 1;
            Emit(new DungeonSwept(cmd.Dungeon, best, reward.Currency, reward.Amount));
            Ledger.Apply(State, new CurrencyDelta(ticket, -1), CurrencyReason.DUNGEON, this);
            Ledger.Apply(State, reward, CurrencyReason.DUNGEON, this);
            QuestsOnDungeonCleared(cmd.Dungeon);   // a sweep counts as a clear (d_dungeon_* with the filter, w_dungeons any)
            return ApplyResult.Accepted;
        }

        // ------------------------------------------------------------------------------------- the run (06 section 6.1)

        /// <summary>Switches to the floor's boss fight. Validation is the caller's (StartDungeon, or PreviewDungeon on a clone).</summary>
        void BeginDungeonRun(DungeonDef def, int floor, ulong seed, string grantId)
        {
            var k = Db.Constants;
            var fd = def.Floor(floor);
            double hp = k.BossHpMult * Query.EnemyHp(fd.StageEquiv);   // HP0 * HP_G^(floor_stage_step * f) * BOSS_HP_MULT, cached powers
            var c = State.Combat;
            c.Mode = CombatMode.Dungeon;
            c.DungeonRun = new DungeonRunState
            {
                Kind = def.Kind.GetValueOrDefault(), Floor = floor, Seed = seed, GrantId = grantId, StreamCounter = 0, EnemyIndex = 0,
                TicksLeft = k.BossTimeTicks,
                Fight = new FightState
                {
                    Phase = FightPhase.Fighting, EnemyHp = hp, EnemyMaxHp = hp, EnemyIsBoss = true, BossTicksLeft = k.BossTimeTicks,
                },
            };
            Emit(new DungeonStarted(def.Kind.GetValueOrDefault(), floor, seed));
        }

        /// <summary>One tick of the run: 03 section 2.1 steps 3-9 against the floor's boss, then the timer.</summary>
        void TickDungeonFight(DungeonRunState run)
        {
            var f = run.Fight;
            if (f == null)
            {
                // A state that reached the sim with a run but no fight (a save loaded without the P16 repair): the boss
                // restarts at full HP for the ticks left. Runtime's validator abandons such runs before this can happen.
                var fd = Db.DungeonOf(run.Kind)?.Floor(run.Floor);
                if (fd == null) { EndDungeonRun(false, 1.0); return; }
                double hp = Db.Constants.BossHpMult * Query.EnemyHp(fd.StageEquiv);
                run.Fight = f = new FightState { Phase = FightPhase.Fighting, EnemyHp = hp, EnemyMaxHp = hp, EnemyIsBoss = true, BossTicksLeft = run.TicksLeft };
            }
            if (DamageSteps(f, run)) { EndDungeonRun(true, 0.0); return; }   // 3.-7.
            run.TicksLeft -= 1;                                              // 8. timer last: a hit on the final tick counts
            f.BossTicksLeft = run.TicksLeft;
            if (run.TicksLeft <= 0) { EndDungeonRun(false, f.EnemyMaxHp > 0 ? f.EnemyHp / f.EnemyMaxHp : 0.0); return; }
            TickBuffs(f);                                                    // 9.
        }

        /// <summary>The hero's crit draw in a run: Derive(run.Seed, StreamCounter), then StreamCounter + 1 (02 section 3.4).</summary>
        static double NextDungeonRunUnit(DungeonRunState run)
        {
            ulong k = (ulong)run.StreamCounter;
            run.StreamCounter += 1;
            return SplitMix64.ToUnit(SplitMix64.Derive(run.Seed, k));
        }

        /// <summary>06 section 6.2 EndDungeonRun: back to the frozen frontier; a clear debits the ticket and pays the floor.</summary>
        void EndDungeonRun(bool cleared, double remainingFraction)
        {
            var c = State.Combat;
            var run = c.DungeonRun;
            var (d, floor) = (run.Kind, run.Floor);
            int ticksUsed = Db.Constants.BossTimeTicks - run.TicksLeft + (cleared ? 1 : 0);   // the clearing tick counts; a timeout used them all
            if (ticksUsed > Db.Constants.BossTimeTicks) ticksUsed = Db.Constants.BossTimeTicks;
            LastDungeonRun = new DungeonRunResult(d, floor, run.Seed, cleared, cleared ? 0.0 : remainingFraction, ticksUsed, run.StreamCounter,
                                                  run.Fight?.EnemyMaxHp ?? 0.0);
            c.DungeonRun = null;
            c.Mode = CombatMode.Stage;                                       // the same stage, kills and frontier fight
            var recent = State.Dungeons.RecentRuns;
            recent.Add(new DungeonRunRecord(run.GrantId, d, floor, cleared));   // 02; bounded list for 08's seed correlation (issue #52)
            while (recent.Count > SimConfig.DUNGEON_RECENT_RUNS_MAX) recent.RemoveAt(0);
            if (!cleared)
            {
                Emit(new DungeonFailed(d, floor, remainingFraction, run.Seed, ticksUsed, run.GrantId));   // costs nothing
                return;
            }

            var def = Db.DungeonOf(d);
            var fd = def?.Floor(floor);
            if (def?.TicketCurrency is not Currency ticket || fd == null) return;   // only a clone's preview of a row-less dungeon; unreachable from StartDungeon
            var reward = RewardOf(fd);
            // Debit on clear only (00 decision 16), from the new day's allotment after a rollover. Tickets cannot drop during a
            // run (StartDungeon and SweepDungeon are rejected while one is active), so the start-time check still holds.
            Ledger.Apply(State, new CurrencyDelta(ticket, -1), CurrencyReason.DUNGEON, this);
            Ledger.Apply(State, reward, CurrencyReason.DUNGEON, this);
            State.Dungeons.EntriesToday[(int)d] += 1;
            State.Stats.DungeonClears += 1;
            if (floor > State.Dungeons.BestFloor[(int)d])
            {
                State.Dungeons.BestFloor[(int)d] = floor;
                Emit(new DungeonFloorFirstCleared(d, floor));
            }
            QuestsOnDungeonCleared(d);
            Emit(new DungeonCleared(d, floor, reward.Currency, reward.Amount, run.Seed, ticksUsed, run.GrantId));
        }

        // ------------------------------------------------------------------------------------- preview (06 section 6.2)

        /// <summary>
        /// The run StartDungeon(dungeon, floor) would produce, without changing anything: the same ticked fight on a clone of
        /// <paramref name="state"/>, seeded with <paramref name="seed"/> or by default the next local seed (LocalDungeonSeed.Peek),
        /// so it agrees with the local run that follows. Tickets, the unlock and the ladder are not checked (SCR_DUNGEONS labels
        /// any floor). Null for a dungeon or floor this build has no row for.
        /// </summary>
        public static DungeonPreview PreviewDungeon(GameState state, ContentDb db, Dungeon dungeon, int floor, ulong? seed = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (db == null) throw new ArgumentNullException(nameof(db));
            var def = db.DungeonOf(dungeon);
            if (def == null || def.Floor(floor) == null) return null;
            var clone = StateCopy.DeepClone(state);
            clone.Combat.Mode = CombatMode.Stage;   // a preview from inside a run fights the floor as if that run had ended
            clone.Combat.DungeonRun = null;
            var sim = new Simulation(clone, db) { ForceCoalesce = true };
            sim.BeginDungeonRun(def, floor, seed ?? LocalDungeonSeed.Peek(state), "");
            sim._busy = true;
            try
            {
                while (clone.Combat.Mode == CombatMode.Dungeon)
                {
                    sim.TickOnce();
                    sim._events.Clear();
                    sim._batch = null;
                }
            }
            finally
            {
                sim._busy = false;
            }
            var r = sim.LastDungeonRun;
            return new DungeonPreview(r, DpsRatioNeeded(r, db.Constants.BossTimeTicks));
        }

        /// <summary>PreviewDungeon on this Simulation's state and content.</summary>
        public DungeonPreview PreviewDungeon(Dungeon dungeon, int floor, ulong? seed = null) => PreviewDungeon(State, Db, dungeon, floor, seed);

        /// <summary>
        /// The DPS multiple the floor needs, assuming damage scales linearly with DPS: for a failed run the boss HP over the damage
        /// dealt ("need about x1.4 DPS"; +inf when nothing landed), for a clear the share of the time limit used (at most 1).
        /// </summary>
        static double DpsRatioNeeded(DungeonRunResult r, int timeLimitTicks)
        {
            if (r.Cleared) return timeLimitTicks > 0 ? (double)r.TicksUsed / timeLimitTicks : 0.0;
            double dealt = 1.0 - r.RemainingFraction;
            return dealt > 0 ? 1.0 / dealt : double.PositiveInfinity;
        }
    }
}
