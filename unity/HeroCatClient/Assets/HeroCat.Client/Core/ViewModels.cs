using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using HeroCat.Sim.Time;

namespace HeroCat.Client.Core
{
    // Plain-value snapshots the screens read (specs/07 section 7.2). Filled by GameViewModel.Refresh().

    public sealed class HudVm
    {
        public double Gold;
        public long Gems, Shards;
        public int FreePullsDaily, FreePullsBanked;
    }

    public sealed class CombatVm
    {
        public int Stage, KillsInStage, KillsPerStage, BossFailsOnStage, ClassIndex;
        public bool IsBossStage, EnemyIsBoss, Spawning, IsWalled, BossClearable, BossPending, ShowBossButton;
        public BossPhase BossPhase;
        public double EnemyHp, EnemyMaxHp, BossTimerS, BossTimeMaxS, Dps, HitDamage, KillTimeS, NeedDps, NeedDpsRatio;
        public int HitsToKill;
        public float EnemyHue;   // degrees 0..360
        public readonly string[] PartyGlyphs = new string[6];
        public bool ChestReady;
        public long ChestReadyInSec;
        public int ChestGems;
        public long LifetimeKills, LifetimeBossKills, LifetimeBossFails, LifetimeDrops;
        public int FirstWallStage;
    }

    public sealed class StatRow
    {
        public string Label;
        public int Count;
        public double Cost, DeltaPct, ResultMult;
        public bool Affordable;
    }

    public sealed class StatsVm
    {
        public int Level;
        public double StatMult;
        public readonly StatRow One = new StatRow { Label = "x1" };
        public readonly StatRow Ten = new StatRow { Label = "x10" };
        public readonly StatRow Max = new StatRow { Label = "Max" };
    }

    public sealed class GearSlotRow
    {
        public string Name;
        public bool IsEmpty, Maxed;
        public Rarity Rarity;
        public int Tier, Enhance;
        public double BasePct, EffPct, SlotMult;
    }

    public sealed class GearVm
    {
        public GearSlotRow[] Slots = new GearSlotRow[0];
        public int SelectedSlot;
        public double EnhanceCost, EnhanceChance, GearMult;
        public bool EnhanceAffordable, EnhanceMaxed, SelectedEmpty;
    }

    public sealed class HeroRow
    {
        public string Id, Glyph, Name;
        public Rarity Tier;
        public int Copies, Level, NextLevelCopies, EquippedSlot = -1;
        public double Pct;
        public bool Owned => Copies > 0;
    }

    public sealed class HeroesVm
    {
        public HeroRow[] All = new HeroRow[0];
        public HeroRow[] Equipped = new HeroRow[0];   // per party slot, null = empty
        public double HeroMult;
        public string SelectedHero;
        public bool AutoEquipOn;

        /// <summary>Bumped whenever rows are rebuilt (copies/party changed) so screens rebuild only then.</summary>
        public int Version;
    }

    public sealed class PullCard
    {
        public string Glyph, Name;
        public Rarity Tier;
        public bool IsNew;
        public int LevelBefore, LevelAfter;
        public long Shards;
    }

    public sealed class GachaVm
    {
        public string BannerName;
        public int PityCounter, PitySoftStart, PityHard, FreePullsDaily, FreePullsBanked, FreePullsPerDay;
        public long TotalPulls, Gems;
        public bool CanPull, CanPull10;
        public string Pull1Label, Pull10Label;
        public double NextPullLplus;
        public PullCard[] LastResults = new PullCard[0];
        public string LastSummary = "";
        public string RatesText = "";
        public int ResultsVersion;
    }

    public sealed class ClassMilestone
    {
        public int Stage;
        public string Name;
        public double Mult;
        public bool Reached;
    }

    public sealed class ClassVm
    {
        public int Advances, NextStage, MaxAdvances;
        public string ClassName;
        public double ClassMult, Progress01;
        public ClassMilestone[] Milestones = new ClassMilestone[0];
    }

    /// <summary>Wall clock plus a debug offset (specs/07 section 6.7). Offset is never persisted.</summary>
    public sealed class OffsetClock : IClock
    {
        readonly IClock _inner;

        public OffsetClock(IClock inner) { _inner = inner; }

        public long OffsetSeconds { get; set; }
        public long NowServerSeconds => _inner.NowServerSeconds + OffsetSeconds;
        public bool IsAuthoritative => _inner.IsAuthoritative;
    }
}
