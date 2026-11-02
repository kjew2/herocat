using System.Collections.Generic;
using HeroCat.Sim.Content;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// 06 section 13's quest, offline, rollover and shop events, and 03 section 11's <c>awakened</c> (issue #177). Emitted by
    /// <see cref="HeroCat.Runtime.Session.FeatureAnalytics"/> from the sim's events.
    /// </summary>
    public static partial class AnalyticsEvents
    {
        /// <summary>06 section 13: a daily or weekly mission reached its target (an achievement rung sends <see cref="AchievementUnlocked"/>).</summary>
        public sealed record QuestCompleted(string QuestId, QuestKind Kind) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.QUEST_COMPLETED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["questId"] = QuestId, ["kind"] = Kind.ToString(),
            };
        }

        /// <summary>06 section 13: one claimed quest; Bulk when ClaimAllQuests claimed it.</summary>
        public sealed record QuestClaimed(string QuestId, QuestKind Kind, Currency RewardCurrency, long RewardAmount, bool Bulk) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.QUEST_CLAIMED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["questId"] = QuestId, ["kind"] = Kind.ToString(), ["rewardCurrency"] = RewardCurrency.ToString(),
                ["rewardAmount"] = RewardAmount, ["bulk"] = Bulk,
            };
        }

        /// <summary>06 section 13: an achievement rung completed (06 section 5.4).</summary>
        public sealed record AchievementUnlocked(string QuestId, int Rung) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.ACHIEVEMENT_UNLOCKED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["questId"] = QuestId, ["rung"] = Rung };
        }

        /// <summary>
        /// 06 section 13: ComputeOfflineCatchup stored or summed a gap into Offline.Pending. The fields are the pending record
        /// after this gap (the sum when a reward was already pending); PendingStage is its stage (the envelope's
        /// <c>stage</c> is the current one, and an owner field may not reuse an envelope name, 02 section 14.1).
        /// </summary>
        public sealed record OfflineCatchup(long ElapsedSec, long CreditedSec, bool Capped, double Gold, double Exp, int TankFill,
                                            int PendingStage) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.OFFLINE_CATCHUP;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["elapsedSec"] = ElapsedSec, ["creditedSec"] = CreditedSec, ["capped"] = Capped, ["gold"] = Gold, ["exp"] = Exp,
                ["tankFill"] = TankFill, ["pendingStage"] = PendingStage,
            };
        }

        /// <summary>06 section 13: ClaimOfflineReward credited the pending record. X2: the rewarded-ad doubling (P1; false until it exists).</summary>
        public sealed record OfflineClaimed(long ElapsedSec, long CreditedSec, bool Capped, double Gold, double Exp, int TankFill, bool X2)
            : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.OFFLINE_CLAIMED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["elapsedSec"] = ElapsedSec, ["creditedSec"] = CreditedSec, ["capped"] = Capped, ["gold"] = Gold, ["exp"] = Exp,
                ["tankFill"] = TankFill, ["x2"] = X2,
            };
        }

        /// <summary>06 section 13: DayRolledOver.</summary>
        public sealed record DayRollover(int DayKey, int SkippedDays, long SecondsSinceLastTick) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.DAY_ROLLOVER;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["dayKey"] = DayKey, ["skippedDays"] = SkippedDays, ["secondsSinceLastTick"] = SecondsSinceLastTick,
            };
        }

        /// <summary>06 section 13: ClaimDailyGift accepted. No fields; the gift itself is its <c>currency_changed</c> row.</summary>
        public sealed record DailyGiftClaimed : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.DAILY_GIFT_CLAIMED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>();
        }

        /// <summary>06 section 13: ExchangeItem accepted; Diamonds is the total spent.</summary>
        public sealed record Exchange(string OfferId, int Qty, long Diamonds) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.EXCHANGE;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["offerId"] = OfferId, ["qty"] = Qty, ["diamonds"] = Diamonds,
            };
        }

        /// <summary>
        /// 06 section 13: one dungeon outcome (issues #172, #173). Outcome is cleared, failed, swept or abandoned; Rewards is the
        /// floor reward ({currency, amount}), empty for failed and abandoned; TicksUsed is 0 for a sweep or an abandoned run.
        /// </summary>
        public sealed record DungeonRun(Dungeon Kind, int Floor, string Outcome, int TicksUsed, Currency? RewardCurrency, double RewardAmount,
                                        string GrantId) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.DUNGEON_RUN;

            public override Dictionary<string, object> Props()
            {
                var rewards = new List<Dictionary<string, object>>(1);
                if (RewardCurrency is Currency c)
                    rewards.Add(new Dictionary<string, object> { ["currency"] = c.ToString(), ["amount"] = RewardAmount });
                return new Dictionary<string, object>
                {
                    ["kind"] = Kind.ToString(), ["floor"] = Floor, ["outcome"] = Outcome, ["ticksUsed"] = TicksUsed, ["rewards"] = rewards,
                    ["grantId"] = GrantId ?? "",
                };
            }
        }

        /// <summary>06 section 13: SCR_SHOP opened on a tab (07 section 4.16). The client's shop screen tracks it; no sim event.</summary>
        public sealed record ShopView(string Tab) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.SHOP_VIEW;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["tab"] = Tab };
        }

        /// <summary>03 section 11: Awaken bought a rank. The stage is the envelope's (02 section 14.1).</summary>
        public sealed record Awakened(int Slot, int Rank, long RockCandyCost, double RockCandyAfter) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.AWAKENED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["slot"] = Slot, ["rank"] = Rank, ["rockCandyCost"] = RockCandyCost, ["rockCandyAfter"] = RockCandyAfter,
            };
        }
    }
}
