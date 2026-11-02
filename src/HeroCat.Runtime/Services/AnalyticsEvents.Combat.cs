using System.Collections.Generic;
using HeroCat.Sim.Content;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// 03 section 11's combat and progression events (issue #274); <c>awakened</c> is with 06's rows in
    /// AnalyticsEvents.Economy.cs. Emitted by <see cref="HeroCat.Runtime.Session.FeatureAnalytics"/> from the sim's events.
    /// The event's own stage is <c>clearedStage</c>, <c>bossStage</c> or <c>wallStage</c>: the envelope's <c>stage</c> is
    /// the current one, and an owner field may not reuse an envelope name (02 section 14.1).
    /// </summary>
    public static partial class AnalyticsEvents
    {
        /// <summary>
        /// 03 section 11: the frontier left <see cref="ClearedStage"/>. Attempts: boss fights on the stage including the
        /// clearing one (null, and omitted, for a normal stage). TicksOnStage: null (omitted) when the stage was entered
        /// before this process attached. Dps: Query.RatedDps when the row is sent.
        /// </summary>
        public sealed record StageCleared(int ClearedStage, bool IsBoss, bool FirstClear, long DiamondsGranted, int? Attempts, long? TicksOnStage,
                                          double Dps) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.STAGE_CLEARED;

            public override Dictionary<string, object> Props()
            {
                var p = new Dictionary<string, object>
                {
                    ["clearedStage"] = ClearedStage, ["isBoss"] = IsBoss, ["firstClear"] = FirstClear, ["diamondsGranted"] = DiamondsGranted,
                    ["dps"] = Dps,
                };
                if (Attempts is int a) p["attempts"] = a;
                if (TicksOnStage is long t) p["ticksOnStage"] = t;
                return p;
            }
        }

        /// <summary>03 section 11: a boss timer ran out (03 section 2.7). BossHpLeftPct is percentage points (0..100).</summary>
        public sealed record BossFailed(int BossStage, double BossHpLeftPct, int Attempt) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.BOSS_FAILED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["bossStage"] = BossStage, ["bossHpLeftPct"] = BossHpLeftPct, ["attempt"] = Attempt,
            };
        }

        /// <summary>
        /// 03 section 11: a walled boss stage was cleared. Attempts: boss fights on the stage including the clearing one.
        /// WallSec (server seconds, offline gaps included), ActiveSecDuringWall (ticked seconds) and LayersChangedDuringWall
        /// (the <c>PowerLayer</c> names, Base excluded, whose factor changed) are null, and omitted, when the wall opened
        /// before this process attached.
        /// </summary>
        public sealed record WallBroken(int WallStage, long? WallSec, double? ActiveSecDuringWall, int Attempts,
                                        IReadOnlyList<string> LayersChangedDuringWall) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.WALL_BROKEN;

            public override Dictionary<string, object> Props()
            {
                var p = new Dictionary<string, object> { ["wallStage"] = WallStage, ["attempts"] = Attempts };
                if (WallSec is long w) p["wallSec"] = w;
                if (ActiveSecDuringWall is double a) p["activeSecDuringWall"] = a;
                if (LayersChangedDuringWall != null) p["layersChangedDuringWall"] = LayersChangedDuringWall;
                return p;
            }
        }

        /// <summary>03 section 11: one TrainStat / TrainStatMax command (one row per command, not per level; 01).</summary>
        public sealed record Training(Stat Stat, int LevelFrom, int LevelTo, double GoldCost) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.TRAINING;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["stat"] = Stat.ToString(), ["levelFrom"] = LevelFrom, ["levelTo"] = LevelTo, ["goldCost"] = GoldCost,
            };
        }

        /// <summary>03 section 11: Hero Level rose to HeroLevel (one row per level, 03 section 5.2).</summary>
        public sealed record LevelUp(int HeroLevel) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.LEVEL_UP;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["heroLevel"] = HeroLevel };
        }

        /// <summary>03 section 11: Research bought one level; Level is the level after.</summary>
        public sealed record ResearchBought(string ResearchId, int Level, double FishCoinCost) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.RESEARCH_BOUGHT;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["researchId"] = ResearchId, ["level"] = Level, ["fishCoinCost"] = FishCoinCost,
            };
        }
    }
}
