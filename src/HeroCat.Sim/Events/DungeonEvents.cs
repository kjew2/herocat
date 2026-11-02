using HeroCat.Sim.Content;

namespace HeroCat.Sim.Events
{
    // Dungeon events (06 sections 6.2 and 13, 00 section 5.1; issues #172, #173). The analytics row `dungeon_run` (kind, floor,
    // outcome, ticksUsed, rewards, grantId) is built from DungeonCleared, DungeonFailed, DungeonSwept and DungeonAbandoned.

    /// <summary>StartDungeon accepted: Combat.Mode is Dungeon and the floor's boss is up (SCR_DUNGEON_RUN opens).</summary>
    public sealed record DungeonStarted(Dungeon Dungeon, int Floor, ulong Seed) : Event;

    /// <summary>
    /// The run's boss died within the time limit. The ticket debit and the reward (reason DUNGEON) are the CurrencyChanged rows
    /// before it, and DungeonFloorFirstCleared too on a new best floor.
    /// </summary>
    public sealed record DungeonCleared(Dungeon Dungeon, int Floor, Currency RewardCurrency, double RewardAmount, ulong Seed,
                                        int TicksUsed, string GrantId) : Event;

    /// <summary>The time limit ran out: nothing spent, nothing granted. RemainingFraction is the boss's HP left in [0, 1], as BossFailed.</summary>
    public sealed record DungeonFailed(Dungeon Dungeon, int Floor, double RemainingFraction, ulong Seed, int TicksUsed, string GrantId) : Event;

    /// <summary>A clear raised Dungeons.BestFloor to <see cref="Floor"/> (no separate first-clear bonus in MVP).</summary>
    public sealed record DungeonFloorFirstCleared(Dungeon Dungeon, int Floor) : Event;

    /// <summary>SweepDungeon accepted: the ticket debit and the reward (reason DUNGEON) follow, as for an exchange.</summary>
    public sealed record DungeonSwept(Dungeon Dungeon, int Floor, Currency RewardCurrency, double RewardAmount) : Event;

    /// <summary>
    /// A save held a run in progress and the load abandoned it (02 rule P16): nothing spent, nothing refunded. Raised by
    /// Runtime from the save validator's report, not by a tick.
    /// </summary>
    public sealed record DungeonAbandoned(Dungeon Dungeon, int Floor, string GrantId) : Event;
}
