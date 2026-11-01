using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;

namespace HeroCat.Sim
{
    /// <summary>Training (03 section 5.1) and Hero Level (03 section 5.2).</summary>
    public sealed partial class Simulation
    {
        // ================================================================================================ Training

        bool IsStat(Stat s) => (int)s >= 0 && (int)s < Db.Stats.Count;

        internal ApplyResult HandleTrainStat(TrainStat cmd)
        {
            if (!IsStat(cmd.Stat)) return ApplyResult.Reject(RejectCode.BadArgument, CombatRejectReasons.INVALID_STAT);
            if (cmd.Count < 1) return ApplyResult.Reject(RejectCode.BadArgument, CombatRejectReasons.INVALID_COUNT);
            var def = Db.Stat(cmd.Stat);
            if (!def.Trainable || !Db.TrainingCurves.TryGetValue(cmd.Stat, out var curve))
                return ApplyResult.Reject(RejectCode.Locked, CombatRejectReasons.STAT_NOT_TRAINABLE);
            int level = Query.TrainLevel(cmd.Stat);
            if (def.Cap.HasValue && (long)level + cmd.Count > def.Cap.Value)   // explicit overshoot is rejected (01 section 8)
                return ApplyResult.Reject(RejectCode.Capped, CombatRejectReasons.STAT_AT_CAP);
            // MaxAffordable first, so a huge count never walks the cost curve past what any finite budget buys.
            if (cmd.Count > curve.Cost.MaxAffordable(level, State.Wallet.Gold))
                return ApplyResult.Reject(RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_GOLD);
            return BuyTraining(cmd.Stat, level, cmd.Count, curve.Cost.EvalSum(level, cmd.Count));
        }

        internal ApplyResult HandleTrainStatMax(TrainStatMax cmd)
        {
            if (!IsStat(cmd.Stat)) return ApplyResult.Reject(RejectCode.BadArgument, CombatRejectReasons.INVALID_STAT);
            var def = Db.Stat(cmd.Stat);
            if (!def.Trainable || !Db.TrainingCurves.TryGetValue(cmd.Stat, out var curve))
                return ApplyResult.Reject(RejectCode.Locked, CombatRejectReasons.STAT_NOT_TRAINABLE);
            int level = Query.TrainLevel(cmd.Stat);
            if (def.Cap.HasValue && level >= def.Cap.Value) return ApplyResult.Reject(RejectCode.Capped, CombatRejectReasons.STAT_AT_CAP);
            int m = Query.MaxAffordableLevels(cmd.Stat);
            if (m <= 0) return ApplyResult.Reject(RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_GOLD);
            return BuyTraining(cmd.Stat, level, m, curve.Cost.EvalSum(level, m));
        }

        /// <summary>Charges the quoted closed-form cost with reason TRAINING, raises the level, publishes one StatTrained.</summary>
        ApplyResult BuyTraining(Stat stat, int level, int count, double cost)
        {
            if (!Ledger.Apply(State, new CurrencyDelta(Currency.GOLD, -cost), CurrencyReason.TRAINING, this))
                return ApplyResult.Reject(RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_GOLD);
            State.Training.Level[(int)stat] = level + count;
            State.Stats.TrainingLevelsBought += count;
            MarkDpsDirty();
            Emit(new StatTrained(stat, level, level + count, cost));
            QuestsProgress(QuestGoal.TRAIN_LEVELS, count);   // w_train: levels bought, any stat
            return ApplyResult.Accepted;
        }

        // ================================================================================================ Hero Level

        /// <summary>
        /// 03 section 5.2, called by <see cref="Ledger.Apply"/> after every HERO_EXP delta (the only EXP path, so kills,
        /// offline catch-up and the EXP Dungeon all level here). One LevelUp per level; stops at hero_levels.level_cap,
        /// and an overflowing threshold (infinity) never loops.
        /// </summary>
        internal void CheckHeroLevel()
        {
            var h = State.Hero;
            int cap = Db.HeroLevels.LevelCap;
            int from = h.Level;
            while (h.Level < cap)
            {
                double need = Db.HeroLvlExp(h.Level);
                if (!(h.Exp >= need)) break;
                h.Exp -= need;
                h.Level += 1;
                MarkDpsDirty();
                Emit(new LevelUp(h.Level));
            }
            if (h.Level > from) AchievementsSetMax(QuestGoal.REACH_HERO_LEVEL, h.Level);   // 06 section 5.4, after the LevelUps
        }
    }
}
