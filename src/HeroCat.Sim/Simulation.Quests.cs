using System.Collections.Generic;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>
    /// The quest engine for daily and weekly missions (06 sections 5.1-5.3 and 5.5, 00 decision 17; issue #170).
    ///
    /// Progress is keyed by quest id (never by position), so a content reorder changes nothing. A quest with no entry in
    /// <see cref="GameState.Quests"/> reads as IN_PROGRESS with progress 0 (02 rule P12): the rollover reset clears the
    /// kind's entries, which forfeits unclaimed rewards without a CurrencyChanged. <see cref="QuestsProgress"/> is called at
    /// the emitting sites (kills, boss spawns, stage clears, Training, summons, the Fish Board, dungeon clears); offline
    /// catch-up never reaches them. Rewards are paid only by the claim commands. Achievement ladders (never reset, reason
    /// ACHIEVEMENT) are in Simulation.Achievements.cs (issue #171).
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>The feature_unlocks row that gates the claim commands (09 section 5.34, issue #45).</summary>
        const string QuestsFeature = "QUESTS";

        // ================================================================================================ rollover hooks

        partial void ResetDailyQuests(int dayKey) => State.Quests.Daily.Clear();

        partial void ResetWeeklyQuests(int weekKey) => State.Quests.Weekly.Clear();

        partial void ProgressLoginQuests(int dayKey)
        {
            QuestProgressKind(QuestKind.DAILY, QuestGoal.LOGIN, 1, -1);         // d_login: counts, pays nothing (issue #20)
            QuestProgressKind(QuestKind.WEEKLY, QuestGoal.LOGIN_DAYS, 1, -1);   // w_login: once per day key
        }

        // ================================================================================================ progress

        /// <summary>
        /// <c>Quests.Progress(DAILY and WEEKLY, goal, delta, filter)</c> of 06 section 5.1. <paramref name="filter"/> is the
        /// event's Banner or Dungeon ordinal, -1 when the goal has none. Allocation-free once the day's entries exist.
        /// </summary>
        internal void QuestsProgress(QuestGoal goal, long delta, int filter = -1)
        {
            QuestProgressKind(QuestKind.DAILY, goal, delta, filter);
            QuestProgressKind(QuestKind.WEEKLY, goal, delta, filter);
            // The two sum ladders (06 section 5.4) count the same events; the max ladders are AchievementsSetMax's.
            if (goal == QuestGoal.MERGE_FISH) AchievementsAdd(QuestGoal.LIFETIME_MERGES, delta);
            else if (goal == QuestGoal.SUMMON) AchievementsAdd(QuestGoal.LIFETIME_SUMMONS, delta);
        }

        /// <summary>A dungeon clear or sweep (06 section 6.2, CLEAR_DUNGEON with the dungeon filter). TODO(#173): the dungeon issue calls this.</summary>
        internal void QuestsOnDungeonCleared(Dungeon dungeon) => QuestsProgress(QuestGoal.CLEAR_DUNGEON, 1, (int)dungeon);

        /// <summary>
        /// Adds <paramref name="delta"/> to every <paramref name="kind"/> row with <paramref name="goal"/> whose filter matches,
        /// whatever its state (progress counts past target for display, 06 AC 8), and completes the IN_PROGRESS ones that
        /// reach their target.
        /// </summary>
        void QuestProgressKind(QuestKind kind, QuestGoal goal, long delta, int filter)
        {
            if (delta <= 0) return;
            var defs = Db.QuestsOf(kind);
            if (defs.Count == 0) return;   // an M0 build: no quest rows, nothing to do per kill
            var map = QuestMap(kind);
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (def.Goal != goal || !def.Matches(filter)) continue;
                if (!map.TryGetValue(def.Id, out var e) || e == null) map[def.Id] = e = new QuestProgress();
                e.Progress += delta;
                if (e.State == QuestStatus.IN_PROGRESS && e.Progress >= def.Target) CompleteQuest(def, e);
            }
        }

        /// <summary>
        /// IN_PROGRESS -> COMPLETED (a reward-0 row goes straight to CLAIMED, 06 section 5.1), then the bonus counters: a
        /// daily mission counts toward the daily bonus rows and w_dailies (DAILIES_COMPLETED), a weekly mission toward
        /// w_bonus_all (WEEKLIES_COMPLETED). The bonus rows themselves count toward nothing, so this never recurses deeper.
        /// </summary>
        void CompleteQuest(QuestDef def, QuestProgress e)
        {
            e.State = def.RewardAmount == 0 ? QuestStatus.CLAIMED : QuestStatus.COMPLETED;
            Emit(new QuestCompleted(def.Id));
            if (def.Kind == QuestKind.DAILY && def.Goal != QuestGoal.DAILIES_COMPLETED)
            {
                QuestProgressKind(QuestKind.DAILY, QuestGoal.DAILIES_COMPLETED, 1, -1);
                QuestProgressKind(QuestKind.WEEKLY, QuestGoal.DAILIES_COMPLETED, 1, -1);
            }
            else if (def.Kind == QuestKind.WEEKLY && def.Goal != QuestGoal.WEEKLIES_COMPLETED)
            {
                QuestProgressKind(QuestKind.WEEKLY, QuestGoal.WEEKLIES_COMPLETED, 1, -1);
            }
        }

        Dictionary<string, QuestProgress> QuestMap(QuestKind kind) =>
            kind == QuestKind.DAILY ? State.Quests.Daily : kind == QuestKind.WEEKLY ? State.Quests.Weekly : State.Quests.Achievement;

        /// <summary>The quest's state; a missing entry is IN_PROGRESS (02 rule P12).</summary>
        public QuestStatus QuestStateOf(QuestDef def) =>
            QuestMap(def.Kind).TryGetValue(def.Id, out var e) && e != null ? e.State : QuestStatus.IN_PROGRESS;

        /// <summary>The quest's counter (it keeps counting past Target); 0 for a missing entry.</summary>
        public long QuestProgressOf(QuestDef def) =>
            QuestMap(def.Kind).TryGetValue(def.Id, out var e) && e != null ? e.Progress : 0;

        // ================================================================================================ commands

        internal ApplyResult HandleClaimQuest(ClaimQuest c)
        {
            EnsureDayNow();   // 06 section 4.2: a claim on a new day never sees yesterday's quests
            if (!Db.IsUnlocked(QuestsFeature, State)) return ApplyResult.Reject(RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED);
            var def = Db.Quest(c.QuestId);
            if (def == null) return ApplyResult.Reject(RejectCode.InvalidTarget, QuestRejectReasons.UNKNOWN_ID);
            var map = QuestMap(def.Kind);
            map.TryGetValue(def.Id, out var e);
            var state = e != null ? e.State : QuestStatus.IN_PROGRESS;
            if (state == QuestStatus.CLAIMED) return ApplyResult.Reject(RejectCode.AlreadyClaimed, QuestRejectReasons.QUEST_ALREADY_CLAIMED);
            if (state != QuestStatus.COMPLETED) return ApplyResult.Reject(RejectCode.NotAvailable, QuestRejectReasons.QUEST_NOT_COMPLETED);
            PayQuest(def, e);
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleClaimAllQuests(ClaimAllQuests c)
        {
            EnsureDayNow();
            if (!Db.IsUnlocked(QuestsFeature, State)) return ApplyResult.Reject(RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED);
            if (!System.Enum.IsDefined(typeof(QuestKind), c.Kind)) return ApplyResult.Reject(RejectCode.InvalidTarget, QuestRejectReasons.UNKNOWN_ID);
            var defs = Db.QuestsOf(c.Kind);
            var map = QuestMap(c.Kind);
            var claim = new List<QuestDef>();
            foreach (var def in defs)   // by SortOrder: claims are in Order (06 section 5.5)
                if (map.TryGetValue(def.Id, out var e) && e != null && e.State == QuestStatus.COMPLETED) claim.Add(def);
            if (claim.Count == 0) return ApplyResult.Reject(RejectCode.NotAvailable, QuestRejectReasons.NOTHING_TO_CLAIM);

            var totals = new long[System.Enum.GetValues(typeof(Currency)).Length];
            foreach (var def in claim)
            {
                PayQuest(def, map[def.Id]);
                totals[(int)def.RewardCurrency] += def.RewardAmount;
            }
            var lines = new List<CurrencyDelta>();
            for (int i = 0; i < totals.Length; i++)
                if (totals[i] != 0) lines.Add(new CurrencyDelta((Currency)i, totals[i]));
            Emit(new QuestsBulkClaimed(c.Kind, claim.Count, lines.ToArray()));
            return ApplyResult.Accepted;
        }

        /// <summary>
        /// COMPLETED -> CLAIMED, QuestClaimed, then the reward through the one Ledger path with reason QUEST (ACHIEVEMENT for
        /// an achievement rung, 06 section 5.1).
        /// </summary>
        void PayQuest(QuestDef def, QuestProgress e)
        {
            e.State = QuestStatus.CLAIMED;
            Emit(new QuestClaimed(def.Id, def.RewardCurrency, def.RewardAmount));
            var reason = def.Kind == QuestKind.ACHIEVEMENT ? CurrencyReason.ACHIEVEMENT : CurrencyReason.QUEST;
            Ledger.Apply(State, new CurrencyDelta(def.RewardCurrency, def.RewardAmount), reason, this);
        }
    }
}
