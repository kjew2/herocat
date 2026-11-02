using System;
using System.Collections.Generic;

namespace HeroCat.Runtime.Services
{
    /// <summary>05 section 13's Fish Board events (issue #168).</summary>
    public static partial class AnalyticsEvents
    {
        /// <summary>05 section 13: one per ReleaseFish (ViaButton false: the tank tap) or per ReleaseMax (ViaButton true), with the count.</summary>
        public sealed record FishRelease(int Count, int StockBefore, int StockAfter, int BoardOccupancyAfter, bool ViaButton) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.FISH_RELEASE;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["count"] = Count, ["stockBefore"] = StockBefore, ["stockAfter"] = StockAfter, ["boardOccupancyAfter"] = BoardOccupancyAfter,
                ["viaButton"] = ViaButton,
            };
        }

        /// <summary>One <c>levelsCreated[]</c> bucket of <see cref="FishMerge"/>: Count merges created a fish of Level.</summary>
        public sealed record FishLevelCount(int Level, int Count);

        /// <summary>
        /// 05 section 13: one per MergeFish (ViaButton false: drag or tap) or per MergeAll (ViaButton true). LevelsCreated is
        /// the histogram of FishMerged.NewLevel ({level, count} by level), Coins the merges' coins as paid (a discovering
        /// merge pays the discovery coins), Discoveries the levels discovered, DuringBoss a boss was being fought.
        /// </summary>
        public sealed record FishMerge(int Count, IReadOnlyList<FishLevelCount> LevelsCreated, double Coins, IReadOnlyList<int> Discoveries,
                                       bool ViaButton, bool DuringBoss) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.FISH_MERGE;

            public override Dictionary<string, object> Props()
            {
                var levels = new List<Dictionary<string, object>>(LevelsCreated?.Count ?? 0);
                if (LevelsCreated != null)
                    foreach (var l in LevelsCreated) levels.Add(new Dictionary<string, object> { ["level"] = l.Level, ["count"] = l.Count });
                return new Dictionary<string, object>
                {
                    ["count"] = Count, ["levelsCreated"] = levels, ["coins"] = Coins,
                    ["discoveries"] = Discoveries ?? Array.Empty<int>(), ["viaButton"] = ViaButton, ["duringBoss"] = DuringBoss,
                };
            }
        }

        /// <summary>05 section 13: one per FishDiscovered (from a release or a merge).</summary>
        public sealed record FishDiscovered(int Level, double Coins) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.FISH_DISCOVERED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["level"] = Level, ["coins"] = Coins };
        }

        /// <summary>05 section 13: one per DiscardFish.</summary>
        public sealed record FishDiscard(int Level, double Coins, int BoardOccupancyBefore) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.FISH_DISCARD;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["level"] = Level, ["coins"] = Coins, ["boardOccupancyBefore"] = BoardOccupancyBefore,
            };
        }

        /// <summary>
        /// 05 section 13: once at session end (pause or quit). The board, its multiplier inputs, and the session's totals
        /// (releases, merges, discoveries, Fish Coins from the board, active seconds) since the session opened.
        /// </summary>
        public sealed record FishBoardSnapshot(IReadOnlyList<int> Cells, double FishBoardDpsPct, double FishMult, int TankCap, double TankRefillS,
                                               int FishMaxLvl, bool AutoMerge, long Releases, long Merges, long Discoveries, double CoinsEarned,
                                               long ActiveSec) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.FISH_BOARD_SNAPSHOT;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["cells"] = Cells ?? Array.Empty<int>(), ["fishBoardDpsPct"] = FishBoardDpsPct, ["fishMult"] = FishMult, ["tankCap"] = TankCap,
                ["tankRefillS"] = TankRefillS, ["fishMaxLvl"] = FishMaxLvl, ["autoMerge"] = AutoMerge, ["releases"] = Releases,
                ["merges"] = Merges, ["discoveries"] = Discoveries, ["coinsEarned"] = CoinsEarned, ["activeSec"] = ActiveSec,
            };
        }
    }
}
