using HeroCat.Sim.Content;

namespace HeroCat.Sim.Commands
{
    // Quest commands (06 section 5.5, 01 section 2.1). Handlers are in Simulation.Quests.cs. Both run EnsureDay first,
    // then the QUESTS feature gate, then their own checks; a reject changes nothing and emits nothing.

    /// <summary>Claims one COMPLETED daily or weekly quest: CLAIMED, its reward with reason QUEST, QuestClaimed.</summary>
    public sealed record ClaimQuest(string QuestId) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleClaimQuest(this);
    }

    /// <summary>Claims every COMPLETED quest of <see cref="Kind"/> in sort order, then one QuestsBulkClaimed for the toast.</summary>
    public sealed record ClaimAllQuests(QuestKind Kind) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleClaimAllQuests(this);
    }

    /// <summary>06 section 1.3's quest reject reasons, localized as <c>reject.&lt;reason_lower&gt;</c>.</summary>
    public static class QuestRejectReasons
    {
        /// <summary>(InvalidTarget) ClaimQuest with an id in neither quest table; ClaimAllQuests with an undefined kind.</summary>
        public const string UNKNOWN_ID = "UNKNOWN_ID";

        /// <summary>(NotAvailable) the quest is IN_PROGRESS (a missing entry reads as IN_PROGRESS).</summary>
        public const string QUEST_NOT_COMPLETED = "QUEST_NOT_COMPLETED";

        /// <summary>(AlreadyClaimed) the quest is CLAIMED, including a reward-0 row such as d_login.</summary>
        public const string QUEST_ALREADY_CLAIMED = "QUEST_ALREADY_CLAIMED";

        /// <summary>(NotAvailable) ClaimAllQuests found no COMPLETED quest of the kind.</summary>
        public const string NOTHING_TO_CLAIM = "NOTHING_TO_CLAIM";

        /// <summary>(Locked) the feature_unlocks row QUESTS is not met yet (issue #45); progress still counts.</summary>
        public const string QUESTS_LOCKED = "QUESTS_LOCKED";
    }
}
