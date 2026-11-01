using System;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>Research, the Fish Coin sink (03 section 6, 00 decisions 11-12, 09 section 5.18; issue #158).</summary>
    public sealed partial class Simulation
    {
        /// <summary>
        /// 03 section 6's Research(nodeId): buys one level. Checks, in order: InvalidTarget/UNKNOWN_RESEARCH (no such node
        /// in this content tier), Locked/RESEARCH_LOCKED (feature_unlocks RESEARCH closed, or the node's
        /// unlock_awaken_rank not reached), Capped/RESEARCH_MAXED, CannotAfford/NOT_ENOUGH_FISH_COIN. Then one FISH_COIN
        /// debit with reason RESEARCH, the level, and one ResearchLeveled. Apply marks the DPS cache dirty after the
        /// accepted command (03 section 7.3), which covers the nodes that change DPS.
        /// </summary>
        internal ApplyResult HandleResearch(Research c)
        {
            var node = ResearchRules.Node(Db, c.NodeId);
            if (node == null) return ApplyResult.Reject(RejectCode.InvalidTarget, CombatRejectReasons.UNKNOWN_RESEARCH);
            if (!ResearchRules.IsOpen(Db, State, node)) return ApplyResult.Reject(RejectCode.Locked, CombatRejectReasons.RESEARCH_LOCKED);
            int level = ResearchRules.Level(State, node);
            if (level >= node.MaxLevel) return ApplyResult.Reject(RejectCode.Capped, CombatRejectReasons.RESEARCH_MAXED);
            double cost = node.Cost(level);
            if (!State.Wallet.CanAfford(Currency.FISH_COIN, cost) ||
                !Ledger.Apply(State, new CurrencyDelta(Currency.FISH_COIN, -cost), CurrencyReason.RESEARCH, this))
                return ApplyResult.Reject(RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_FISH_COIN);
            State.Research.Level[node.Id] = level + 1;
            MarkDpsDirty();
            Emit(new ResearchLeveled(node.Id, level + 1, cost));
            // No quest goal reads Research (06 section 5); an achievement ladder, if 06 adds one, hooks here (#171).
            return ApplyResult.Accepted;
        }
    }

    /// <summary>
    /// The Research rules and effects as pure functions over (ContentDb, GameState), shared by the Research command,
    /// <c>Query</c>, the bots and the tools' parity tests. A tier without the rows (M0) has no nodes: every level is 0 and
    /// every effect is its Research-0 value.
    /// </summary>
    public static class ResearchRules
    {
        /// <summary>The node with <paramref name="nodeId"/> in this content tier, or null.</summary>
        public static ResearchDef Node(ContentDb db, string nodeId) =>
            nodeId != null && db.Research.TryGetValue(nodeId, out var n) ? n : null;

        /// <summary>The bought level of <paramref name="node"/>, clamped to 0..max_level (02 rule P13 clamps a loaded save the same way).</summary>
        public static int Level(GameState s, ResearchDef node)
        {
            if (!s.Research.Level.TryGetValue(node.Id, out int lv) || lv <= 0) return 0;
            return lv > node.MaxLevel ? node.MaxLevel : lv;
        }

        /// <summary>The bought level of <paramref name="nodeId"/>; 0 for an unknown id.</summary>
        public static int Level(ContentDb db, GameState s, string nodeId)
        {
            var node = Node(db, nodeId);
            return node == null ? 0 : Level(s, node);
        }

        /// <summary>
        /// The node can be bought at all: Research's feature_unlocks gate is open (seeded at Awakening rank 1, 00 decision
        /// 11) and the rank reaches the node's own unlock_awaken_rank (09 section 5.18; E063 keeps it at or above the gate).
        /// </summary>
        public static bool IsOpen(ContentDb db, GameState s, ResearchDef node) =>
            db.IsUnlocked(AwakeningRules.ResearchFeature, s) && AwakeningRules.Rank(db, s) >= node.UnlockAwakenRank;

        /// <summary>Fish Coins the next Research(<paramref name="nodeId"/>) charges; 0 when unknown or maxed.</summary>
        public static double NextCost(ContentDb db, GameState s, string nodeId)
        {
            var node = Node(db, nodeId);
            if (node == null) return 0.0;
            int level = Level(s, node);
            return level >= node.MaxLevel ? 0.0 : node.Cost(level);
        }

        /// <summary>Research(<paramref name="nodeId"/>) would be accepted now: known, open, below max_level, affordable.</summary>
        public static bool CanResearch(ContentDb db, GameState s, string nodeId)
        {
            var node = Node(db, nodeId);
            if (node == null || !IsOpen(db, s, node)) return false;
            int level = Level(s, node);
            return level < node.MaxLevel && s.Wallet.CanAfford(Currency.FISH_COIN, node.Cost(level));
        }

        // ------------------------------------------------------------------------------------------- effects (03 section 6)

        /// <summary>effect_per_level * level of the node with <paramref name="effect"/>; 0 without the node.</summary>
        public static double EffectTotal(ContentDb db, GameState s, ResearchEffect effect) => PowerFormula.ResearchEffectTotal(db, s, effect);

        /// <summary>MpPoolMax = MP_POOL0 + RES_MP_POOL_PER_LVL * research[res_mp_pool] (03 section 2.4).</summary>
        public static double MpPoolMax(ContentDb db, GameState s) => db.Constants.MpPool0 + EffectTotal(db, s, ResearchEffect.MP_POOL);

        /// <summary>MP regen per second: MP_REGEN0 + RES_MP_REGEN_PER_LVL * research[res_mp_regen] (03 section 2.4).</summary>
        public static double MpRegenPerSecond(ContentDb db, GameState s) => db.Constants.MpRegen0 + EffectTotal(db, s, ResearchEffect.MP_REGEN);

        /// <summary>MpRegenPerTick = MpRegenPerSecond / TICK_HZ (03 section 2.4).</summary>
        public static double MpRegenPerTick(ContentDb db, GameState s) => MpRegenPerSecond(db, s) / db.Constants.TickHz;

        /// <summary>
        /// TankCap with Research: TANK_CAP0 + RES_TANK_CAP_PER_LVL * research[res_tank_cap] (05 section 3; 10..60 with the
        /// shipped rows). The Fish Board does not read this yet (TODO(#167)); FishBoard.TankCap is still TANK_CAP0.
        /// </summary>
        public static int TankCap(ContentDb db, GameState s) =>
            db.Tank.Cap0 + (int)Math.Round(EffectTotal(db, s, ResearchEffect.TANK_CAP), MidpointRounding.AwayFromZero);

        /// <summary>
        /// TankRefillTicks with Research: round(max(TANK_REFILL_FLOOR_S, TANK_REFILL_S0 - RES_TANK_REFILL_PER_LVL * level)
        /// * TICK_HZ), half away from zero (05 section 3; 60, 56, ..., 20 ticks, never below the 2.0 s floor). The floor is
        /// tank.refill_floor_s, which E056 keeps equal to the node's effect_floor. Not read by the board yet (TODO(#167)).
        /// </summary>
        public static int TankRefillTicks(ContentDb db, GameState s)
        {
            var t = db.Tank;
            double seconds = t.RefillS0 - EffectTotal(db, s, ResearchEffect.TANK_REFILL);
            if (seconds < t.RefillFloorS) seconds = t.RefillFloorS;
            return (int)Math.Round(seconds * db.Constants.TickHz, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// FishMaxLvl with Research: FISH_MAX_LVL0 + research[res_fish_cap], never above the fish_levels row count
        /// (05 section 4.3, E038). Not read by the board yet (TODO(#167)).
        /// </summary>
        public static int FishMaxLvl(ContentDb db, GameState s)
        {
            int cap = db.Constants.FishMaxLvl0 + (int)Math.Round(EffectTotal(db, s, ResearchEffect.FISH_CAP), MidpointRounding.AwayFromZero);
            int hard = db.FishLevels.Count;
            return cap > hard ? hard : cap;
        }

        /// <summary>The effective offline cap in hours, OFFLINE_CAP_H + 2 h per res_offline_cap level (00 decision 12; 36 h at level 6).</summary>
        public static double OfflineCapH(ContentDb db, GameState s) => PowerFormula.OfflineCapH(db, s);
    }
}
