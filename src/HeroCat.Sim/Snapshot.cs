using System;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>One damage number for the combat viewport (01 section 2.4). TODO(#113): filled by the combat loop (#81).</summary>
    public readonly struct DamageNumber
    {
        public DamageNumber(double amount, bool crit) { Amount = amount; Crit = crit; }

        public double Amount { get; }
        public bool Crit { get; }
    }

    /// <summary>
    /// What the HUD and combat viewport poll (01 section 2.4, 00 contract 8): a value copy refreshed at the end of every
    /// Advance and Apply. Nothing in it references GameState; the damage-number span views a buffer the Simulation
    /// owns. Counted currencies are long (00 contract 9), not 01's doubles (issue #64 note).
    /// </summary>
    public readonly struct Snapshot
    {
        readonly DamageNumber[] _damage;
        readonly int _damageCount;

        internal Snapshot(DamageNumber[] damage, int damageCount) : this()
        {
            _damage = damage;
            _damageCount = damageCount;
        }

        public long Tick { get; init; }
        /// <summary>The frontier: Combat.Stage, never the selected farm stage.</summary>
        public int Stage { get; init; }

        /// <summary>Combat.Selected, the side stage being farmed; null = fighting the frontier (01 section 2.4, issue #254).</summary>
        public int? SelectedStage { get; init; }

        public int KillsInStage { get; init; }

        /// <summary>Combat.Mode. In Dungeon mode BossTicksLeft, EnemyHp and EnemyHpMax are the run's boss (06 section 6.1).</summary>
        public CombatMode Mode { get; init; }

        public int Chapter { get; init; }
        public BossPhase BossPhase { get; init; }
        public int BossTicksLeft { get; init; }
        public double EnemyHp { get; init; }
        public double EnemyHpMax { get; init; }

        /// <summary>The 00 section 7 rated DPS. TODO(#85): 0 until the power formula lands.</summary>
        public double HeroDps { get; init; }

        public double Gold { get; init; }
        public long Diamonds { get; init; }
        public double FishCoin { get; init; }
        public long RockCandy { get; init; }
        public double HeroExp { get; init; }
        public double HeroExpToNext { get; init; }
        public int HeroLevel { get; init; }
        public int AwakenRank { get; init; }
        public double Mp { get; init; }
        public double MpMax { get; init; }
        public int TankFish { get; init; }
        public int TankCap { get; init; }
        public int TankRefillTicksLeft { get; init; }

        /// <summary>Set by Runtime, not the sim (01 section 2.7).</summary>
        public int PendingInboxCount { get; init; }

        /// <summary>Up to DMG_NUMBERS_PER_TICK_MAX damage numbers from the last tick.</summary>
        public ReadOnlySpan<DamageNumber> DamageNumbersThisTick =>
            _damage == null ? ReadOnlySpan<DamageNumber>.Empty : new ReadOnlySpan<DamageNumber>(_damage, 0, _damageCount);

        /// <summary>A copy with Runtime's inbox count injected.</summary>
        public Snapshot WithPendingInboxCount(int n) => new Snapshot(_damage, _damageCount)
        {
            Tick = Tick,
            Stage = Stage,
            SelectedStage = SelectedStage,
            KillsInStage = KillsInStage,
            Mode = Mode,
            Chapter = Chapter,
            BossPhase = BossPhase,
            BossTicksLeft = BossTicksLeft,
            EnemyHp = EnemyHp,
            EnemyHpMax = EnemyHpMax,
            HeroDps = HeroDps,
            Gold = Gold,
            Diamonds = Diamonds,
            FishCoin = FishCoin,
            RockCandy = RockCandy,
            HeroExp = HeroExp,
            HeroExpToNext = HeroExpToNext,
            HeroLevel = HeroLevel,
            AwakenRank = AwakenRank,
            Mp = Mp,
            MpMax = MpMax,
            TankFish = TankFish,
            TankCap = TankCap,
            TankRefillTicksLeft = TankRefillTicksLeft,
            PendingInboxCount = n,
        };
    }
}
