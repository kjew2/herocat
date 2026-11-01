using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

namespace HeroCat.Sim.Events
{
    // Quest events (06 sections 5.1 and 5.5, 00 section 5.1). The quest's kind, goal and reward row are
    // ContentDb.Quest(questId).

    /// <summary>A quest reached its target. A reward-0 row (d_login) is CLAIMED at once and pays nothing.</summary>
    public sealed record QuestCompleted(string QuestId) : Event;

    /// <summary>One claimed quest; its CurrencyChanged (reason QUEST) follows.</summary>
    public sealed record QuestClaimed(string QuestId, Currency Currency, long Amount) : Event;

    /// <summary>After a ClaimAllQuests: Count quests claimed, Totals one entry per currency in Currency order (the toast).</summary>
    public sealed record QuestsBulkClaimed(QuestKind Kind, int Count, CurrencyDelta[] Totals) : Event;
}
