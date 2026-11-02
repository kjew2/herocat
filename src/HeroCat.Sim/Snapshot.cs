using System;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>One damage number for the combat viewport (01 section 2.4): its party slot, or -1 for a fish missile or Tap Zap.</summary>
    public readonly struct DamageNumber
    {
        public DamageNumber(double amount, bool crit, int slot) { Amount = amount; Crit = crit; Slot = slot; }

        public double Amount { get; }
        public bool Crit { get; }
        public int Slot { get; }
    }

    /// <summary>One party slot as the HUD polls it (01 section 2.4, 00 decision 109).</summary>
    public readonly struct SlotSnapshot
    {
        public SlotSnapshot(bool occupied, int awakenRank, double mp, double slotDps)
        {
            Occupied = occupied; AwakenRank = awakenRank; Mp = mp; SlotDps = slotDps;
        }

        public bool Occupied { get; }
        public int AwakenRank { get; }
        public double Mp { get; }
        public double SlotDps { get; }
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

        /// <summary>The 00 section 7 rated party DPS.</summary>
        public double PartyDps { get; init; }

        public double Gold { get; init; }
        public long Diamonds { get; init; }
        public double FishCoin { get; init; }
        public long RockCandy { get; init; }
        public double PartyExp { get; init; }
        public double PartyExpToNext { get; init; }
        public int PartyLevel { get; init; }
        /// <summary>MpPoolMax, the same for every pool.</summary>
        public double MpMax { get; init; }

        // Five fixed fields rather than an array, so the struct stays allocation-free (01 section 2.4).
        public SlotSnapshot Slot0 { get; init; }
        public SlotSnapshot Slot1 { get; init; }
        public SlotSnapshot Slot2 { get; init; }
        public SlotSnapshot Slot3 { get; init; }
        public SlotSnapshot Slot4 { get; init; }

        /// <summary>Party slot <paramref name="i"/> (0..4); default for any other index.</summary>
        public SlotSnapshot Slot(int i)
        {
            switch (i)
            {
                case 0: return Slot0;
                case 1: return Slot1;
                case 2: return Slot2;
                case 3: return Slot3;   // literal-ok: the fixed Slot0..Slot4 fields
                case 4: return Slot4;   // literal-ok: the fixed Slot0..Slot4 fields
                default: return default;
            }
        }
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
            PartyDps = PartyDps,
            Gold = Gold,
            Diamonds = Diamonds,
            FishCoin = FishCoin,
            RockCandy = RockCandy,
            PartyExp = PartyExp,
            PartyExpToNext = PartyExpToNext,
            PartyLevel = PartyLevel,
            MpMax = MpMax,
            Slot0 = Slot0,
            Slot1 = Slot1,
            Slot2 = Slot2,
            Slot3 = Slot3,
            Slot4 = Slot4,
            TankFish = TankFish,
            TankCap = TankCap,
            TankRefillTicksLeft = TankRefillTicksLeft,
            PendingInboxCount = n,
        };
    }
}
