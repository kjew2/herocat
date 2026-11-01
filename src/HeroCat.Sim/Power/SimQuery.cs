using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Power
{
    /// <summary>
    /// <c>Simulation.Query</c> (03 section 7): read-only derived numbers for SCR_HERO's breakdown, the wall check, the bots
    /// and the tools. The power breakdown is cached and recomputed lazily after <see cref="MarkDirty"/> (03 section 7.3);
    /// the tick loop only reads it. Simulation marks it dirty after every accepted command and every LevelUp, which covers
    /// 03 section 7.3's whole trigger list (Training, LevelUp, Awakened, ResearchLeveled, equips, copy gains, limit breaks
    /// and Fish Board commands) at most once per command. Code that mutates state outside Apply (tests) calls
    /// <see cref="MarkDirty"/> itself.
    /// </summary>
    public sealed class SimQuery
    {
        readonly ContentDb _db;
        readonly GameState _s;
        PowerBreakdown _p;
        bool _dirty = true;

        internal SimQuery(ContentDb db, GameState s)
        {
            _db = db;
            _s = s;
        }

        /// <summary>Invalidates the cached breakdown; the next read recomputes it.</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>Every factor and unit (03 sections 7.1-7.2), as a value.</summary>
        public PowerBreakdown Power
        {
            get
            {
                if (_dirty)
                {
                    _p = PowerFormula.Compute(_db, _s);
                    _dirty = false;
                }
                return _p;
            }
        }

        public double RatedDps => Power.RatedDps;
        public double HeroHit => Power.HeroHit;
        public double SkillUnit => Power.SkillUnit;
        public double CompanionUnit => Power.CompanionUnit;
        public double CritRate => Power.CritRate;
        public double CritDmg => Power.CritDmg;
        public double CritFactor => Power.Crit;
        public double SpeedFactor => Power.Speed;
        public double HeroMult => Power.Hero;
        public double AwakenMult => Power.Awakening;
        public double ResearchPct => Power.ResearchPct;
        public double GoldMult => Power.GoldMult;
        public double ExpMult => Power.ExpMult;
        public int AttackGainUnits => Power.AttackGainUnits;

        /// <summary>The rated DPS with a modelled <paramref name="missileDpsPct"/> (05 section 5.1; issue #15).</summary>
        public double RatedDpsWithMissiles(double missileDpsPct) =>
            RatedDps / Power.Fish * (1.0 + (Power.FishBoardDpsPct + missileDpsPct) / 100.0);

        // ------------------------------------------------------------------------------------------- stages (03 3)

        /// <summary>Hp(s) = HP0 * HP_G^s of a normal enemy.</summary>
        public double EnemyHp(int stage) => _db.EnemyHp.Eval(stage);

        public double GoldPerKill(int stage) => _db.GoldPerKill.Eval(stage);

        public double ExpPerKill(int stage) => _db.ExpPerKill.Eval(stage);

        /// <summary>
        /// The wall condition (03 section 2.8): the boss of <paramref name="stage"/> falls inside its timer at the rated DPS,
        /// <c>BOSS_HP_MULT * Hp(s) &lt;= RatedDps * BOSS_TIME_TICKS / TICK_HZ</c>, i.e. <c>Hp(s) / DPS(s) &lt;= 3 s</c>.
        /// </summary>
        public bool BossClearable(int stage)
        {
            var k = _db.Constants;
            return k.BossHpMult * EnemyHp(stage) <= RatedDps * k.BossTimeTicks / k.TickHz;
        }

        /// <summary>Rated kill time of a normal enemy of <paramref name="stage"/> in seconds (PowerFormula.KillTimeSeconds).</summary>
        public double KillTimeSeconds(int stage) => PowerFormula.KillTimeSeconds(_db, Power, EnemyHp(stage));

        /// <summary>The effective offline cap in hours, OFFLINE_CAP_H plus RES_OFFLINE_CAP (00 decision 12).</summary>
        public double OfflineCapH => PowerFormula.OfflineCapH(_db, _s);

        /// <summary>Bought level of the Research node with <paramref name="effect"/> (0 in M0 content).</summary>
        public int ResearchLevel(ResearchEffect effect) => PowerFormula.ResearchLevel(_db, _s, effect);

        // ------------------------------------------------------------------------------------------- training (03 5.1)

        public int TrainLevel(Stat stat) => PowerFormula.Level(_s, stat);

        /// <summary>Gold for the next <paramref name="count"/> levels of <paramref name="stat"/>: the closed form TrainStat charges.</summary>
        public double TrainCost(Stat stat, int count = 1) => _db.TrainCost(stat, TrainLevel(stat), count);

        /// <summary>
        /// Levels TrainStatMax would buy now: the most affordable with the current gold, clamped at TRAIN_CAP. 0 for a stat
        /// that is not trainable.
        /// </summary>
        public int MaxAffordableLevels(Stat stat)
        {
            var def = _db.Stat(stat);
            if (!def.Trainable || !_db.TrainingCurves.TryGetValue(stat, out var curve)) return 0;
            int level = TrainLevel(stat);
            int m = curve.Cost.MaxAffordable(level, _s.Wallet.Gold);
            if (def.Cap.HasValue && level + (long)m > def.Cap.Value) m = def.Cap.Value > level ? def.Cap.Value - level : 0;
            return m;
        }
    }
}
