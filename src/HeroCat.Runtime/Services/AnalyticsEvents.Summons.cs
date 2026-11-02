using System;
using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

namespace HeroCat.Runtime.Services
{
    /// <summary>04 section 13's summon and collection events (issue #163).</summary>
    public static partial class AnalyticsEvents
    {
        /// <summary>One <c>results[]</c> row of <see cref="Summon"/>.</summary>
        public sealed record SummonResultRow(string ItemId, Rarity Rarity, bool IsNew, int LevelBefore, int LevelAfter, long OverflowRockCandy,
                                             bool GuaranteeApplied)
        {
            public Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["itemId"] = ItemId, ["rarity"] = Rarity.ToString(), ["isNew"] = IsNew, ["levelBefore"] = LevelBefore,
                ["levelAfter"] = LevelAfter, ["overflowRockCandy"] = OverflowRockCandy, ["guaranteeApplied"] = GuaranteeApplied,
            };
        }

        /// <summary>
        /// 04 section 13: once per applied request, after ApplySummonResult (never for a replayed requestId). Payment is
        /// DIAMOND or TICKET, Source LOCAL or SERVER; GuaranteeApplied is true when any slot was forced.
        /// </summary>
        public sealed record Summon(string RequestId, Banner Banner, int Count, string Payment, long DiamondsSpent, long TicketsSpent,
                                    int PityBefore, int PityAfter, int BannerLevelBefore, int BannerLevelAfter,
                                    IReadOnlyList<SummonResultRow> Results, int LplusCount, bool GuaranteeApplied, string Source,
                                    double LatencyMs) : AnalyticsEvent
        {
            public const string LOCAL = "LOCAL";
            public const string SERVER = "SERVER";

            public override string Name => AnalyticsNames.SUMMON;

            public override Dictionary<string, object> Props()
            {
                var results = new List<Dictionary<string, object>>(Results?.Count ?? 0);
                if (Results != null) foreach (var r in Results) results.Add(r.Props());
                return new Dictionary<string, object>
                {
                    ["requestId"] = RequestId, ["banner"] = Banner.ToString(), ["count"] = Count, ["payment"] = Payment,
                    ["diamondsSpent"] = DiamondsSpent, ["ticketsSpent"] = TicketsSpent, ["pityBefore"] = PityBefore, ["pityAfter"] = PityAfter,
                    ["bannerLevelBefore"] = BannerLevelBefore, ["bannerLevelAfter"] = BannerLevelAfter, ["results"] = results,
                    ["lplusCount"] = LplusCount, ["guaranteeApplied"] = GuaranteeApplied, ["source"] = Source, ["latencyMs"] = LatencyMs,
                };
            }
        }

        /// <summary>
        /// 04 section 13: one per acquired item: each summon result (AcquireReason SUMMON, RequestId the summon's) and the
        /// non-summon grants (TUTORIAL: the profile-created weapon, RequestId null; MAIL with the inbox claim's requestId once
        /// the sim applies inbox item lines, TODO(#271)).
        /// </summary>
        public sealed record ItemAcquired(Banner Banner, string ItemId, Rarity Rarity, bool IsNew, int CopiesAfter, int LevelBefore,
                                          int LevelAfter, string AcquireReason, string RequestId) : AnalyticsEvent
        {
            public const string SUMMON = "SUMMON";
            public const string MAIL = "MAIL";
            public const string TUTORIAL = "TUTORIAL";

            public override string Name => AnalyticsNames.ITEM_ACQUIRED;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["banner"] = Banner.ToString(), ["itemId"] = ItemId, ["rarity"] = Rarity.ToString(), ["isNew"] = IsNew,
                ["copiesAfter"] = CopiesAfter, ["levelBefore"] = LevelBefore, ["levelAfter"] = LevelAfter,
                ["acquireReason"] = AcquireReason, ["requestId"] = RequestId,
            };
        }

        /// <summary>
        /// 04 section 13, Remote mode: the local balance minus the payload's debit differs from the server's balance after the
        /// pull (the server ledger wins, 00 section 4). LocalAfter is what the local wallet would have held; ServerAfter is
        /// the payload's BalancesAfter row, which ApplySummonResult snapped the wallet to.
        /// </summary>
        public sealed record SummonReconciled(string RequestId, Banner Banner, Currency Currency, double LocalAfter, double ServerAfter)
            : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.SUMMON_RECONCILED;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["requestId"] = RequestId, ["banner"] = Banner.ToString(), ["currency"] = Currency.ToString(), ["localAfter"] = LocalAfter,
                ["serverAfter"] = ServerAfter,
            };
        }

        /// <summary>04 section 13: on ItemLimitBroken. RankAfter is LimitBreaks after; RockCandySpent is this rank's cost.</summary>
        public sealed record LimitBreak(Banner Banner, string ItemId, Rarity Rarity, int RankAfter, long RockCandySpent) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.LIMIT_BREAK;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["banner"] = Banner.ToString(), ["itemId"] = ItemId, ["rarity"] = Rarity.ToString(), ["rankAfter"] = RankAfter,
                ["rockCandySpent"] = RockCandySpent,
            };
        }

        /// <summary>04 section 13: a loadout slot changed. ItemId null: the slot was emptied; Auto: the sim chose it after a summon.</summary>
        public sealed record ItemEquipped(Banner Banner, int Slot, string ItemId, string PreviousItemId, bool Auto) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.ITEM_EQUIPPED;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["banner"] = Banner.ToString(), ["slot"] = Slot, ["itemId"] = ItemId, ["previousItemId"] = PreviousItemId, ["auto"] = Auto,
            };
        }
    }
}
