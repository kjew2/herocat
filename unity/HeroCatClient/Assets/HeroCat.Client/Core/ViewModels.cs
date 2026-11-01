using HeroCat.Runtime.Services;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    // Plain-value snapshots the screens read (07 section 3). Filled by GameViewModel.Refresh() from the sim's Snapshot.
    // M0 skeleton: HUD and stage only. TODO(#109): the per-screen view-model layer with change-only binding.

    public sealed class HudVm
    {
        public double Gold, FishCoin;
        public long Diamonds;
        public int HeroLevel;
    }

    public sealed class CombatVm
    {
        public int Stage, Chapter, KillsInStage, KillsPerStage, BossTicksLeft;
        public bool IsBossStage, IsWalled;
        public BossPhase BossPhase;
        public double EnemyHp, EnemyMaxHp;
    }

    /// <summary>Wall clock plus a debug offset (07 section 17.3). The offset is never persisted.</summary>
    public sealed class OffsetClock : IClock, IClockFloor
    {
        readonly IClock _inner;

        public OffsetClock(IClock inner) { _inner = inner; }

        public long OffsetSeconds { get; set; }
        public long NowServerSeconds => _inner.NowServerSeconds + OffsetSeconds;
        public bool IsAuthoritative => _inner.IsAuthoritative;

        /// <summary>Forwards boot's floor to the wrapped clock (02 section 11.2 rule 4).</summary>
        public void SetFloor(long lastSavedServerTime) => (_inner as IClockFloor)?.SetFloor(lastSavedServerTime);
    }
}
