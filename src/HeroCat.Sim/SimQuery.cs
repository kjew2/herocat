using System;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>Read-only derived values (specs/03 section 14). One implementation of each formula.</summary>
    public sealed class SimQuery
    {
        readonly Simulation _sim;

        internal SimQuery(Simulation sim) { _sim = sim; }

        ContentDb C => _sim.Content;
        Constants K => _sim.Content.Constants;
        GameState S => _sim.State;

        // ---- power
        public double Dps => _sim.DpsCached;
        public double HitDamage => _sim.HitDamageCached;
        public double StatMult => _sim.StatMultCached;
        public double GearMult => _sim.GearMultCached;
        public double HeroMult => _sim.HeroMultCached;
        public double ClassMult => _sim.ClassMultCached;

        // ---- stage / combat
        public int Stage => S.Stage.Stage;
        public double Hp(int stage) => C.EnemyHp(stage);
        public double BossHp(int stage) => C.BossRuleFor(stage).HpMult * C.EnemyHp(stage);
        public double Gold(int stage) => C.GoldPerKill(stage);
        public int HitsToKill(double hp) => Math.Max(1, (int)Math.Min(int.MaxValue, Math.Ceiling(hp / HitDamage)));

        /// <summary>Normal mob at <paramref name="stage"/>: (hits * ATTACK_INTERVAL_TICKS + SPAWN_DELAY_TICKS) / TICK_HZ.</summary>
        public double KillTimeSeconds(int stage) =>
            ((double)HitsToKill(Hp(stage)) * K.AttackIntervalTicks + K.SpawnDelayTicks) / K.TickHz;

        public bool IsBossStage => K.IsBossStage(S.Stage.Stage);

        /// <summary>Would the boss of the current stage die within its timer at current DPS?</summary>
        public bool BossClearable =>
            (double)HitsToKill(BossHp(S.Stage.Stage)) * K.AttackIntervalTicks <= C.BossRuleFor(S.Stage.Stage).TimeTicks;

        public bool IsWalled => S.Stage.Boss == BossPhase.Walled;

        /// <summary>In-app gold per second at the current stage (normal mobs).</summary>
        public double GoldPerSecond => Gold(S.Stage.Stage) / KillTimeSeconds(S.Stage.Stage);

        // ---- stat levels
        public int StatLevel => S.Progression.StatLevel;
        public double StatCost(int level) => C.StatCost(level);
        public double NextStatCost => C.StatCost(S.Progression.StatLevel);

        /// <summary>Cost of levels n..n+m-1 from the current level, summed from the same table the purchase uses.</summary>
        public double BulkStatCost(int m)
        {
            int n = S.Progression.StatLevel;
            double total = 0;
            for (int i = 0; i < m; i++) total += C.StatCost(n + i);
            return total;
        }

        public int MaxAffordableStatLevels()
        {
            double gold = S.Currencies.Gold;
            int n = S.Progression.StatLevel;
            int m = 0;
            double total = 0;
            while (true)
            {
                double next = C.StatCost(n + m);
                if (total + next > gold) return m;
                total += next;
                m++;
            }
        }

        /// <summary>Relative DPS gain of buying m more levels.</summary>
        public double StatDeltaPct(int m) => K.StatPerLvl * m / (1.0 + K.StatPerLvl * S.Progression.StatLevel);

        // ---- gear
        public double EnhanceCost => K.EnhCostGoldMult * C.GoldPerKill(S.Stage.Stage);
        public double EnhanceSuccess(int level) => K.EnhSuccess(level);
        public double GearBasePct(GearPiece piece) => _sim.GearBasePct(piece);
        public double GearEffPct(GearPiece piece) => _sim.GearBasePct(piece) * (1.0 + K.EnhPerLvl * piece.Enhance);
        public double GearSellValue(GearPiece piece) => _sim.SellValue(piece);

        public double GearSlotMult(int slot)
        {
            var p = S.Gear.Slots[slot];
            return p == null ? 1.0 : 1.0 + GearEffPct(p);
        }

        /// <summary>The bot's enhancement target: lowest Enhance among non-empty slots (lowest index on ties), or -1.</summary>
        public int WeakestGearSlot()
        {
            int best = -1;
            for (int i = 0; i < S.Gear.Slots.Length; i++)
            {
                var p = S.Gear.Slots[i];
                if (p == null) continue;
                if (best < 0 || p.Enhance < S.Gear.Slots[best].Enhance) best = i;
            }
            return best;
        }

        // ---- heroes / gacha
        public int HeroLevel(int copies) => C.HeroLevel(copies);
        public int HeroCopies(string hero) => hero != null && S.Heroes.Copies.TryGetValue(hero, out int c) ? c : 0;
        public int HeroLevelOf(string hero) => C.HeroLevel(HeroCopies(hero));

        /// <summary>Percentage points (40 = +40%); 0 if not owned.</summary>
        public double HeroPct(string hero) => _sim.HeroPct(hero);

        public int FreePullsDaily => S.Currencies.FreePullsDaily;
        public int FreePullsBanked => S.Currencies.FreePullsBanked;

        /// <summary>Single pulls payable without gems right now (daily + banked).</summary>
        public int FreePullsRemaining => S.Currencies.FreePullsDaily + S.Currencies.FreePullsBanked;

        public int PullCostGems => C.HeroBanner.PullCostGems;
        public int PityCounter => S.HeroBanner.PityCounter;
        public double PityRate(int n) => C.HeroBanner.PityRate(n);

        /// <summary>L+ chance of the next pull.</summary>
        public double NextPullLplusRate => C.HeroBanner.PityRate(S.HeroBanner.PityCounter + 1);

        public bool CanPull => FreePullsRemaining > 0 || S.Currencies.Gems >= C.HeroBanner.PullCostGems;

        // ---- time / dailies
        /// <summary>The sim's virtual server time (anchored by SyncWallClock, advanced by ticks).</summary>
        public long NowServerSec => _sim.NowServerMs / 1000;

        public long DayKey => S.Daily.DayKey;
        public long ChestReadyAtServerSec => S.Progression.ChestTimerStart + (long)(K.IdleChestHours * 3600);
        public bool IsChestReady => NowServerSec >= ChestReadyAtServerSec;

        /// <summary>Gold that ApplyOfflineCatchUp would credit for <paramref name="elapsedSec"/> seconds away.</summary>
        public double OfflineGoldFor(long elapsedSec)
        {
            if (elapsedSec <= 0) return 0;
            long credited = Math.Min(elapsedSec, (long)(K.OfflineCapH * 3600));
            return credited * K.OfflineRate * C.GoldPerKill(S.Stage.Stage) / KillTimeSeconds(S.Stage.Stage);
        }
    }
}
