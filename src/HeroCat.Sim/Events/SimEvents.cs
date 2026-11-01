using System;
using System.Collections.Generic;
using HeroCat.Sim.Gacha;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Events
{
    /// <summary>Something that happened. Stamped with State.Tick at publication.</summary>
    public abstract record SimEvent(long Tick);

    public sealed record EnemySpawned(long Tick, int Stage, bool IsBoss, double Hp) : SimEvent(Tick);
    public sealed record HitLanded(long Tick, double Damage, double RemainingHp) : SimEvent(Tick);
    public sealed record EnemyKilled(long Tick, int Stage, bool IsBoss, double Gold) : SimEvent(Tick);
    public sealed record StageAdvanced(long Tick, int FromStage, int ToStage, bool ViaBoss) : SimEvent(Tick);
    public sealed record BossFailed(long Tick, int Stage, int FailsOnStage) : SimEvent(Tick);
    public sealed record BossCleared(long Tick, int Stage) : SimEvent(Tick);

    /// <summary>Equipped=false means the piece was sold immediately; SoldFor is the gold from whichever piece was sold.</summary>
    public sealed record GearDropped(long Tick, int Slot, GearPiece Piece, bool Equipped, GearPiece Replaced, double SoldFor) : SimEvent(Tick);

    public sealed record StatLeveled(long Tick, int NewLevel, int Count, double Cost) : SimEvent(Tick);
    public sealed record EnhanceAttempted(long Tick, int Slot, bool Success, int NewLevel, double Cost) : SimEvent(Tick);
    public sealed record HeroPulled(long Tick, PullResult Result, bool Free) : SimEvent(Tick);

    /// <summary>Hero null = slot cleared.</summary>
    public sealed record HeroEquipped(long Tick, int Slot, string Hero) : SimEvent(Tick);

    public sealed record ClassAdvanced(long Tick, int Advances, int Stage, double Mult) : SimEvent(Tick);
    public sealed record OfflineGoldCredited(long Tick, long ElapsedSec, long CreditedSec, bool Capped, double Gold) : SimEvent(Tick);
    public sealed record DayRolledOver(long Tick, long DayKey) : SimEvent(Tick);
    public sealed record ClockWentBackwards(long Tick, long ByMs) : SimEvent(Tick);
    public sealed record StageCapReached(long Tick, int Stage) : SimEvent(Tick);
    public sealed record QuestCompleted(long Tick, string QuestId, int Gems) : SimEvent(Tick);
    public sealed record FirstClear(long Tick, int Stage, int Gems) : SimEvent(Tick);
    public sealed record IdleChestClaimed(long Tick, int Gems) : SimEvent(Tick);

    [Flags]
    public enum EventKinds : uint
    {
        None = 0,
        EnemySpawned = 1 << 0, HitLanded = 1 << 1, EnemyKilled = 1 << 2, StageAdvanced = 1 << 3, BossFailed = 1 << 4,
        BossCleared = 1 << 5, GearDropped = 1 << 6, StatLeveled = 1 << 7, EnhanceAttempted = 1 << 8, HeroPulled = 1 << 9,
        HeroEquipped = 1 << 10, ClassAdvanced = 1 << 11, OfflineGoldCredited = 1 << 12, DayRolledOver = 1 << 13,
        ClockWentBackwards = 1 << 14, StageCapReached = 1 << 15, QuestCompleted = 1 << 16, FirstClear = 1 << 17,
        IdleChestClaimed = 1 << 18,
        All = (1u << 19) - 1,

        /// <summary>The two per-enemy events are opt-in.</summary>
        Default = All & ~(HitLanded | EnemySpawned),
    }

    public interface IEventSink
    {
        /// <summary>The sim checks the mask BEFORE allocating an event.</summary>
        EventKinds Mask { get; }

        void Publish(SimEvent e);
    }

    public sealed class NullEventSink : IEventSink
    {
        public static readonly NullEventSink Instance = new NullEventSink();
        public EventKinds Mask => EventKinds.None;
        public void Publish(SimEvent e) { }
    }

    /// <summary>Client outbox: the sim pushes, the client drains once per frame after ticking.</summary>
    public sealed class EventOutbox : IEventSink
    {
        readonly Queue<SimEvent> _q = new Queue<SimEvent>();
        readonly int _capacity;

        public EventOutbox(EventKinds mask = EventKinds.Default, int capacity = 65536)
        {
            Mask = mask;
            _capacity = capacity;
        }

        public EventKinds Mask { get; set; }
        public int Count => _q.Count;

        /// <summary>Oldest events dropped because the outbox was full.</summary>
        public long Dropped { get; private set; }

        public void Publish(SimEvent e)
        {
            if (_q.Count >= _capacity) { _q.Dequeue(); Dropped++; }
            _q.Enqueue(e);
        }

        /// <summary>Appends all queued events to <paramref name="into"/>, clears, returns count.</summary>
        public int Drain(List<SimEvent> into)
        {
            int n = _q.Count;
            while (_q.Count > 0) into.Add(_q.Dequeue());
            return n;
        }

        public List<SimEvent> Drain()
        {
            var l = new List<SimEvent>(_q.Count);
            Drain(l);
            return l;
        }
    }

    /// <summary>Invokes a callback synchronously for every published event (bots, tests).</summary>
    public sealed class CallbackEventSink : IEventSink
    {
        readonly Action<SimEvent> _cb;

        public CallbackEventSink(Action<SimEvent> callback, EventKinds mask = EventKinds.Default)
        {
            _cb = callback;
            Mask = mask;
        }

        public EventKinds Mask { get; set; }
        public void Publish(SimEvent e) => _cb(e);
    }
}
