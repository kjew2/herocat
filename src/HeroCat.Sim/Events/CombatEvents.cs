using System;
using HeroCat.Sim.Content;

namespace HeroCat.Sim.Events
{
    /// <summary>
    /// The per-tick combat events a consumer opts into (<see cref="Simulation.CombatEvents"/>; 03 section 1 goal 4, criterion
    /// 19). These are the cosmetic events the loop can emit every tick (a spawn, a hit, a cast, a missile, a tap); the loop
    /// checks the mask before it allocates one, so a tick with nothing masked in allocates nothing. Masking changes no state
    /// (the hash, quests and the ledger never read events). Milestone events (kills, stage, boss, currency) are not maskable.
    /// </summary>
    [Flags]
    public enum CombatEventMask
    {
        None = 0,
        EnemySpawned = 1 << 0,
        HitLanded = 1 << 1,
        SkillCast = 1 << 2,
        CompanionAbilityUsed = 1 << 3,   // literal-ok: flag bit, not balance
        FishMissileHit = 1 << 4,   // literal-ok: flag bit, not balance
        TapZapped = 1 << 5,   // literal-ok: flag bit, not balance

        /// <summary>
        /// What a Simulation starts with: one event per spawn and per player input. The events the loop raises on its own
        /// several times per kill (HitLanded, SkillCast, CompanionAbilityUsed) are opt-in: the client's damage numbers and
        /// cast VFX mask them in (<see cref="All"/>); bots and the server never need them (03 criterion 19).
        /// </summary>
        Default = EnemySpawned | FishMissileHit | TapZapped,

        All = EnemySpawned | HitLanded | SkillCast | CompanionAbilityUsed | FishMissileHit | TapZapped,
    }

    // Combat and progression events (03, 00 section 5.1's names). EnemyKilled and EnemyKilledBatch live in Events.cs.

    /// <summary>03 section 2.6. Stage is the fought stage (the selected side stage when farming one).</summary>
    public sealed record EnemySpawned(int Stage, bool IsBoss, double MaxHp) : Event;

    /// <summary>
    /// One hero auto-attack (03 section 2.2). Opt-in (<see cref="Simulation.EmitHitLanded"/>, <see cref="CombatEventMask.HitLanded"/>), off by default, so the bot
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

    /// <summary>An equipped skill auto-cast (03 section 2.3). Opt-in (<see cref="CombatEventMask.SkillCast"/>).</summary>
    public sealed record SkillCast(int Slot, string SkillId) : Event;

    /// <summary>
    /// An equipped companion's ability fired (03 section 2.4). Mp is the pool after the cast (and any MP_REGEN restore).
    /// Opt-in (<see cref="CombatEventMask.CompanionAbilityUsed"/>).
    /// </summary>
    public sealed record CompanionAbilityUsed(int Slot, string CompanionId, double Mp) : Event;

    /// <summary>One queued fish missile applied (03 section 2.5). Fraction is the hit fraction 05 enqueued.</summary>
    public sealed record FishMissileHit(double Fraction, double Damage) : Event;

    /// <summary>Pending taps applied this tick (03 section 2.5).</summary>
    public sealed record TapZapped(int Count, double Damage) : Event;

    /// <summary>One TrainStat / TrainStatMax command (03 section 5.1; the `training` analytics row, 03 section 11).</summary>
    public sealed record StatTrained(Stat Stat, int LevelFrom, int NewLevel, double GoldCost) : Event;

    /// <summary>Hero Level rose to <see cref="Level"/> (03 section 5.2); one per level.</summary>
    public sealed record LevelUp(int Level) : Event;

    /// <summary>Hero.AwakenRank rose to <see cref="Rank"/> through Awaken (03 section 5.3; issue #157).</summary>
    public sealed record Awakened(int Rank) : Event;

    /// <summary>
    /// Research bought one level (03 section 6; analytics research_bought, 03 section 11). NewLevel is the level after;
    /// Cost is the Fish Coins spent.
    /// </summary>
    public sealed record ResearchLeveled(string NodeId, int NewLevel, double Cost) : Event;
}
