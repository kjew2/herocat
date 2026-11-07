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

    /// <summary>Every Ledger.Apply emits exactly one (06 AC 22). Delta is signed; BalanceAfter is the new balance (Hero.Exp for PARTY_EXP).</summary>
    public sealed record CurrencyChanged(Currency Currency, double Delta, double BalanceAfter, CurrencyReason Reason) : Event;

    /// <summary>
    /// EnsureDay moved to <see cref="DayKey"/> (06 section 4.2). SkippedDays: day keys passed over without a rollover (0 for
    /// consecutive days and for a new profile); SecondsSinceLastTick: the rollover's now minus Time.LastTickServerTime
    /// before it, at least 0 (analytics day_rollover, 06 section 13).
    /// </summary>
    public sealed record DayRolledOver(int DayKey, int SkippedDays = 0, long SecondsSinceLastTick = 0) : Event;

    public sealed record WeekRolledOver(int WeekKey) : Event;

    /// <summary>ApplyServerTime carried a time behind the virtual clock. Grants nothing (06 section 4.2 rule 3).</summary>
    public sealed record ClockWentBackwards(long DeltaSeconds) : Event;

    // ---------------------------------------------------------------------------------------- combat (03)

    /// <summary>03 section 2.6. TODO(#81): emitted by the stage loop.</summary>
    public sealed record EnemyKilled(int Stage, bool IsBoss, double Gold, double Exp) : Event;

    /// <summary>
    /// 01 section 2.3: once the buffer passes EVENT_COALESCE_AT within one Advance, further EnemyKilled and
    /// CurrencyChanged(KILL) events are summed here. The totals grow while the batch is open. GoldTotal and ExpTotal sum
    /// the folded kills (boss kills included, whose CurrencyChanged(BOSS) stays in the buffer); KillGold and KillExp sum
    /// only the folded CurrencyChanged(KILL) amounts, which is what analytics' currency_changed needs (issue #155).
    /// </summary>
    public sealed record EnemyKilledBatch : Event
    {
        public int Count { get; internal set; }
        public double GoldTotal { get; internal set; }
        public double ExpTotal { get; internal set; }
        public int FromStage { get; internal set; }
        public int ToStage { get; internal set; }
        public double KillGold { get; internal set; }
        public double KillExp { get; internal set; }
    }

    // ---------------------------------------------------------------------------------------- collections (04)

    /// <summary>One per summoned item (04 section 10). OverflowRockCandy &gt; 0 when the copy past the cap converted.</summary>
    public sealed record ItemAcquired(Banner Banner, string ItemId, Rarity Rarity, bool IsNew, int LevelBefore, int LevelAfter,
                                      long OverflowRockCandy) : Event;

    /// <summary>
    /// A loadout slot changed (04 sections 6.3, 7.2; analytics item_equipped). Weapons use slot 0. ItemId null: the slot
    /// was emptied (UnequipSkill). Auto: chosen by the sim after a summon, not by a player command (the client's toast).
    /// </summary>
    public sealed record ItemEquipped(Banner Banner, int Slot, string ItemId, string PreviousItemId, bool Auto) : Event;

    /// <summary>A summon raised the banner level (04 section 10).</summary>
    public sealed record BannerLeveledUp(Banner Banner, int Level) : Event;

    /// <summary>LimitBreak succeeded (04 section 5.2; analytics limit_break). Rank is LimitBreaks after; Cost is the Rock Candy spent.</summary>
    public sealed record ItemLimitBroken(string ItemId, int Rank, long Cost) : Event;

    // ---------------------------------------------------------------------------------------- fish board (05 section 10)

    /// <summary>A tank fish landed in <see cref="Cell"/>; StockAfter is TankCount after the release.</summary>
    public sealed record FishReleased(int Cell, int Level, int StockAfter) : Event;

    /// <summary>Source emptied, Target holds NewLevel. Coins as paid; MissilePct is FISH_MISSILE_PCT[NewLevel] (% of one hit).</summary>
    public sealed record FishMerged(int Source, int Target, int NewLevel, double Coins, double MissilePct, bool Discovered) : Event;

    /// <summary>The first fish of Level was created; Coins is FISH_DISCOVERY_COINS[Level].</summary>
    public sealed record FishDiscovered(int Level, double Coins) : Event;

    public sealed record FishDiscarded(int Cell, int Level, double Coins) : Event;

    /// <summary>MoveFish (05 section 4.5, MVP).</summary>
    public sealed record FishMoved(int Source, int Target, int Level) : Event;

    /// <summary>Opt-in (Simulation.EmitTankFishReady); the HUD reads the snapshot instead.</summary>
    public sealed record TankFishReady(int TankCount) : Event;
}
