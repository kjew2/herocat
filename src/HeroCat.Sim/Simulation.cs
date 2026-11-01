using System;
using System.Collections.Generic;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>
    /// The one simulation (01 section 2.2). Commands in, events out (00 contract 5): the client, bots and server
    /// re-simulator mutate state only through <see cref="Apply"/> and <see cref="Advance"/>, and observe it through
    /// <see cref="DrainEvents"/> and <see cref="GetSnapshot"/>. Everything observable is a pure function of
    /// (ContentDb, genesis GameState, command log). Single-threaded; not thread-safe.
    ///
    /// Advance ticks time forward (virtual server clock, day rollover) and the stage combat loop (Simulation.Combat.cs).
    /// Summons, the fish board and offline catch-up live in their own partial files (Simulation.Summons/Fish/Offline.cs).
    /// </summary>
    public sealed partial class Simulation : IEventEmitter
    {
        readonly List<Event> _events = new List<Event>(SimConfig.EVENT_BUFFER_MAX);
        readonly DamageNumber[] _damage = new DamageNumber[SimConfig.DMG_NUMBERS_PER_TICK_MAX];
        readonly int _tickHz;
        ICommandLog _log = NullCommandLog.Instance;
        EnemyKilledBatch _batch;
        Snapshot _snapshot;
        bool _busy;
        long _nextDayTick;

        public Simulation(GameState state, ContentDb db)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Db = db ?? throw new ArgumentNullException(nameof(db));
            _tickHz = db.Constants.TickHz;
            RecomputeNextDayTick();
            RefreshSnapshot();
        }

        /// <summary>Read by Runtime for saving; screens never see it (01 section 2.2).</summary>
        public GameState State { get; }

        public ContentDb Db { get; }

        public long Tick => State.Time.Tick;

        /// <summary>Where Apply records commands (before applying them). Null resets to the no-op log.</summary>
        public ICommandLog CommandLog
        {
            get => _log;
            set => _log = value ?? NullCommandLog.Instance;
        }

        /// <summary>Bot harness: coalesce kill events from the first one, keeping memory flat (01 section 2.3).</summary>
        public bool ForceCoalesce { get; set; }

        // ================================================================================================ the door

        /// <summary>
        /// Validate, mutate, emit (01 section 2.2). A reject changes nothing and emits nothing. Never throws for player
        /// input; a null command, or Apply re-entered from inside Apply or Advance, is a programmer error and throws.
        /// </summary>
        public ApplyResult Apply(Command cmd)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (_busy) throw new InvalidOperationException("Simulation.Apply re-entered: queue the command instead (01 section 2.3)");
            _busy = true;
            try
            {
                cmd = cmd with { IssuedAtTick = State.Time.Tick };
                _log.Append(cmd);
                var r = cmd.Execute(this);
                if (r.Ok) MarkDpsDirty();   // 03 section 7.3: DPS recomputed at most once per command, whatever it changed
                RefreshSnapshot();
                return r;
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Runs <paramref name="ticks"/> sim ticks (1 / TICK_HZ s each).</summary>
        public void Advance(int ticks) => AdvanceTicks(ticks);

        /// <summary>Advance for long gaps (replay).</summary>
        public void AdvanceTicks(long ticks)
        {
            if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
            if (_busy) throw new InvalidOperationException("Simulation.Advance re-entered");
            _busy = true;
            try
            {
                _batch = null;
                for (long i = 0; i < ticks; i++) TickOnce();
                RefreshSnapshot();
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Moves the buffered events out; the caller owns the returned list.</summary>
        public IReadOnlyList<Event> DrainEvents()
        {
            if (_events.Count == 0) return Array.Empty<Event>();
            var r = _events.ToArray();
            _events.Clear();
            _batch = null;
            return r;
        }

        /// <summary>The snapshot taken at the end of the last Advance or Apply. Allocation-free.</summary>
        public Snapshot GetSnapshot() => _snapshot;

        /// <summary>Lowercase hex SHA-256 of the canonical state encoding (00 contract 12).</summary>
        public string StateHash() => StateHasher.Hash(State);

        /// <summary>
        /// Replays <paramref name="log"/>: Advance to each command's IssuedAtTick, then Apply it. Then Advance to
        /// <paramref name="toTick"/> (-1: stop at the last command). Throws <see cref="ReplayException"/> on a record
        /// before the current tick.
        /// </summary>
        public void Replay(IReadOnlyList<Command> log, long toTick = -1)
        {
            foreach (var c in log)
            {
                if (c.IssuedAtTick < Tick) throw new ReplayException($"command {c} at tick {c.IssuedAtTick} is before the current tick {Tick}");
                AdvanceTicks(c.IssuedAtTick - Tick);
                Apply(c);
            }
            if (toTick >= 0)
            {
                if (toTick < Tick) throw new ReplayException($"end tick {toTick} is before the current tick {Tick}");
                AdvanceTicks(toTick - Tick);
            }
        }

        // ================================================================================================ tick

        void TickOnce()
        {
            var t = State.Time;
            t.Tick += 1;
            if (t.Tick % _tickHz == 0) State.Stats.ActiveSeconds += 1;
            if (t.Tick >= _nextDayTick && IsAnchored)
            {
                EnsureDay(VirtualNowServerSeconds);
                RecomputeNextDayTick();
            }
            StampLastTick();   // Simulation.Offline.cs: the catch-up anchor follows ticked time
            TickTank();     // 05 section 3: before combat, in every Combat.Mode
            TickCombat();   // 03 section 2.1 (Simulation.Combat.cs)
        }

        // ================================================================================================ events

        void IEventEmitter.Emit(Event e) => Emit(e);

        /// <summary>Buffers an event stamped with the current tick, coalescing kill events past EVENT_COALESCE_AT.</summary>
        internal void Emit(Event e)
        {
            e.Tick = State.Time.Tick;
            if (ForceCoalesce || _events.Count >= SimConfig.EVENT_COALESCE_AT)
            {
                if (e is EnemyKilled k)
                {
                    FoldKill(k.Stage, k.Gold, k.Exp);
                    return;
                }
                if (e is CurrencyChanged cc && cc.Reason == CurrencyReason.KILL)
                {
                    OpenBatch(State.Combat.Stage);   // the kill's gold and EXP are summed through its EnemyKilled
                    return;
                }
            }
            _events.Add(e);
        }

        EnemyKilledBatch OpenBatch(int stage)
        {
            if (_batch != null) return _batch;
            _batch = new EnemyKilledBatch { FromStage = stage, ToStage = stage };
            _events.Add(_batch);
            return _batch;
        }

        // ================================================================================================ snapshot

        void RefreshSnapshot()
        {
            var s = State;
            var f = s.Combat.Fight;
            var k = Db.Constants;
            _snapshot = new Snapshot(_damage, _damageCount)
            {
                Tick = s.Time.Tick,
                Stage = s.Combat.Stage,
                SelectedStage = s.Combat.Selected,
                KillsInStage = s.Combat.KillsInStage,
                Chapter = k.ChapterOf(s.Combat.Stage < 1 ? 1 : s.Combat.Stage),
                BossPhase = s.Combat.Boss,
                BossTicksLeft = f != null ? f.BossTicksLeft : 0,
                EnemyHp = f != null ? f.EnemyHp : 0,
                EnemyHpMax = f != null ? f.EnemyMaxHp : 0,
                HeroDps = Query.RatedDps,
                Gold = s.Wallet.Gold,
                Diamonds = s.Wallet.Diamond,
                FishCoin = s.Wallet.FishCoin,
                RockCandy = s.Wallet.RockCandy,
                HeroExp = s.Hero.Exp,
                HeroExpToNext = Db.HeroLvlExp(s.Hero.Level < 1 ? 1 : s.Hero.Level),
                HeroLevel = s.Hero.Level,
                AwakenRank = s.Hero.AwakenRank,
                Mp = s.Combat.Mp,
                MpMax = k.MpPool0,
                TankFish = s.Fish.TankCount,
                TankCap = FishTankCap,
                TankRefillTicksLeft = FishTankRefillTicks - s.Fish.TankRefillTick,
            };
        }
    }
}
