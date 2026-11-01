using HeroCat.Sim.Content;

namespace HeroCat.Sim.Events
{
    // Combat and progression events (03, 00 section 5.1's names). EnemyKilled and EnemyKilledBatch live in Events.cs.

    /// <summary>03 section 2.6. Stage is the fought stage (the selected side stage when farming one).</summary>
    public sealed record EnemySpawned(int Stage, bool IsBoss, double MaxHp) : Event;

    /// <summary>
    /// One hero auto-attack (03 section 2.2). Opt-in (<see cref="Simulation.EmitHitLanded"/>), off by default, so the bot
    /// and server ticks do not allocate one per hit.
    /// </summary>
    public sealed record HitLanded(double Damage, bool IsCrit, double EnemyHpAfter) : Event;

    /// <summary>The frontier moved from <see cref="From"/> to <see cref="To"/> (03 section 2.6).</summary>
    public sealed record StageCleared(int From, int To, bool ViaBoss) : Event;

    /// <summary>A boss stage paid its first-clear diamonds (03 section 2.6, 06).</summary>
    public sealed record StageFirstCleared(int Stage, int Diamonds) : Event;

    public sealed record BossCleared(int Stage) : Event;

    /// <summary>Entering the first stage of a chapter (03 section 3). Cosmetic.</summary>
    public sealed record ChapterEntered(int Chapter) : Event;

    /// <summary>The boss timer ran out (03 section 2.7). RemainingFraction = enemy HP left / max, for the "so close" UI.</summary>
    public sealed record BossFailed(int Stage, int Attempt, double RemainingFraction) : Event;

    /// <summary>STAGE_CAP reached (03 section 2.6). Emitted once per profile.</summary>
    public sealed record StageCapReached(int Stage) : Event;

    /// <summary>The fought stage changed without an advance: AttemptBoss returning to the frontier, SelectStage (MVP, #159).</summary>
    public sealed record StageChanged(int Stage) : Event;

    /// <summary>An equipped skill auto-cast (03 section 2.3).</summary>
    public sealed record SkillCast(int Slot, string SkillId) : Event;

    /// <summary>One queued fish missile applied (03 section 2.5). Fraction is the hit fraction 05 enqueued.</summary>
    public sealed record FishMissileHit(double Fraction, double Damage) : Event;

    /// <summary>Pending taps applied this tick (03 section 2.5).</summary>
    public sealed record TapZapped(int Count, double Damage) : Event;

    /// <summary>One TrainStat / TrainStatMax command (03 section 5.1; the `training` analytics row, 03 section 11).</summary>
    public sealed record StatTrained(Stat Stat, int LevelFrom, int NewLevel, double GoldCost) : Event;

    /// <summary>Hero Level rose to <see cref="Level"/> (03 section 5.2); one per level.</summary>
    public sealed record LevelUp(int Level) : Event;
}
