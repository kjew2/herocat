using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

namespace HeroCat.Sim.Events
{
    /// <summary>
    /// A fact the sim emitted (01 section 2.1), named NounPastTense from 00 section 5.1's vocabulary. Records with
    /// value equality. <see cref="Tick"/> is stamped by the emitter, so constructors never take it.
    /// </summary>
    public abstract record Event
    {
        /// <summary>Sim tick at emission. Set by the emitter (C# 9 has no other way to stamp an init property late).</summary>
        public long Tick { get; internal set; }
    }

    /// <summary>Where sim code sends events. Simulation buffers them; a profile being built has no sink (null).</summary>
    public interface IEventEmitter
    {
        void Emit(Event e);
    }

    // ---------------------------------------------------------------------------------------- economy (06)

    /// <summary>Every Ledger.Apply emits exactly one (06 AC 22). Delta is signed; BalanceAfter is the new balance (Hero.Exp for HERO_EXP).</summary>
    public sealed record CurrencyChanged(Currency Currency, double Delta, double BalanceAfter, CurrencyReason Reason) : Event;

    public sealed record DayRolledOver(int DayKey) : Event;

    public sealed record WeekRolledOver(int WeekKey) : Event;

    /// <summary>ApplyServerTime carried a time behind the virtual clock. Grants nothing (06 section 4.2 rule 3).</summary>
    public sealed record ClockWentBackwards(long DeltaSeconds) : Event;

    // ---------------------------------------------------------------------------------------- combat (03)

    /// <summary>03 section 2.6. TODO(#81): emitted by the stage loop.</summary>
    public sealed record EnemyKilled(int Stage, bool IsBoss, double Gold, double Exp) : Event;

    /// <summary>
    /// 01 section 2.3: once the buffer passes EVENT_COALESCE_AT within one Advance, further EnemyKilled and
    /// CurrencyChanged(KILL) events are summed here. The totals grow while the batch is open.
    /// </summary>
    public sealed record EnemyKilledBatch : Event
    {
        public int Count { get; internal set; }
        public double GoldTotal { get; internal set; }
        public double ExpTotal { get; internal set; }
        public int FromStage { get; internal set; }
        public int ToStage { get; internal set; }
    }
}
