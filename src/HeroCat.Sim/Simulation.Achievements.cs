using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>
    /// Achievement ladders on the quest engine (06 section 5.4, 09 section 5.23, 00 pillar 4; issue #171).
    ///
    /// Each rung is an ACHIEVEMENT quest row keyed by its id in <see cref="QuestState.Achievement"/>: one-time, never reset
    /// by a day or week rollover, claimed through ClaimQuest / ClaimAllQuests (reason ACHIEVEMENT). Progress for
    /// REACH_STAGE, REACH_HERO_LEVEL, OWN_WEAPONS / OWN_SKILLS / OWN_COMPANIONS (distinct ids), FISH_LEVEL and AWAKEN_RANK
    /// is a max (<see cref="AchievementsSetMax"/>, called where the value changes); LIFETIME_MERGES and LIFETIME_SUMMONS
    /// are sums (<see cref="AchievementsAdd"/>, from the MERGE_FISH and SUMMON progress sites). Every rung a value crosses
    /// completes at once, so a jump from stage 9 to 30 completes rungs 10 and 30 together.
    ///
    /// On construction (a load, or a new content build) <see cref="RecomputeAchievements"/> raises every ladder to the
    /// value the state holds, so a rung added by content completes without waiting for the next change; the sum ladders
    /// read the lifetime counters Stats.Merges and Stats.Pulls, which the same sites increment.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Raises every ladder to the state's current value (06 section 5.4; issue #171). Idempotent.</summary>
        void RecomputeAchievements()
        {
            if (Db.Achievements.Count == 0) return;   // an M0 build: no rungs
            var s = State;
            AchievementsSetMax(QuestGoal.REACH_STAGE, s.Progress.HighestCleared);
            AchievementsSetMax(QuestGoal.REACH_HERO_LEVEL, s.Hero.Level);
            AchievementsSetMax(QuestGoal.OWN_WEAPONS, OwnedCount(s.Weapons));
            AchievementsSetMax(QuestGoal.OWN_SKILLS, OwnedCount(s.Skills));
            AchievementsSetMax(QuestGoal.OWN_COMPANIONS, OwnedCount(s.Companions));
            AchievementsSetMax(QuestGoal.FISH_LEVEL, s.Fish.HighestLevel);
            AchievementsSetMax(QuestGoal.AWAKEN_RANK, s.Hero.AwakenRank);
            AchievementsSetMax(QuestGoal.LIFETIME_MERGES, s.Stats.Merges);
            AchievementsSetMax(QuestGoal.LIFETIME_SUMMONS, s.Stats.Pulls);
        }

        /// <summary>Distinct ids owned: the OWN_* value (a count, never a named set; 00 pillar 4).</summary>
        static int OwnedCount(CollectionState c)
        {
            int n = 0;
            foreach (var kv in c.Items) if (kv.Value != null) n++;
            return n;
        }

        /// <summary>The OWN_* goal a banner's collection feeds; null for runes (no MVP ladder).</summary>
        static QuestGoal? OwnGoal(Banner b) =>
            b == Banner.WEAPON ? QuestGoal.OWN_WEAPONS : b == Banner.SKILL ? QuestGoal.OWN_SKILLS : b == Banner.COMPANION ? QuestGoal.OWN_COMPANIONS : (QuestGoal?)null;

        /// <summary>After a pull result: the banner's OWN_* ladder from the collection's distinct count.</summary>
        void AchievementsOnCollection(Banner banner, CollectionState col)
        {
            var goal = OwnGoal(banner);
            if (goal.HasValue && Db.AchievementsFor(goal.Value).Count > 0) AchievementsSetMax(goal.Value, OwnedCount(col));
        }

        /// <summary>
        /// <c>Quests.SetMax(goal, value)</c> of 06 section 5.4: every rung with <paramref name="goal"/> whose progress is below
        /// <paramref name="value"/> takes it, and the IN_PROGRESS ones that reach their target complete. Never lowers.
        /// </summary>
        internal void AchievementsSetMax(QuestGoal goal, long value)
        {
            if (value <= 0) return;   // a missing entry already reads as 0
            var defs = Db.AchievementsFor(goal);
            var map = State.Quests.Achievement;
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (!map.TryGetValue(def.Id, out var e) || e == null) map[def.Id] = e = new QuestProgress();
                if (value > e.Progress) e.Progress = value;
                if (e.State == QuestStatus.IN_PROGRESS && e.Progress >= def.Target) CompleteQuest(def, e);
            }
        }

        /// <summary>A sum goal (LIFETIME_MERGES, LIFETIME_SUMMONS): adds <paramref name="delta"/> to every rung, whatever its state.</summary>
        void AchievementsAdd(QuestGoal goal, long delta)
        {
            if (delta <= 0) return;
            var defs = Db.AchievementsFor(goal);
            var map = State.Quests.Achievement;
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (!map.TryGetValue(def.Id, out var e) || e == null) map[def.Id] = e = new QuestProgress();
                e.Progress += delta;
                if (e.State == QuestStatus.IN_PROGRESS && e.Progress >= def.Target) CompleteQuest(def, e);
            }
        }

        // ================================================================================================ SCR_QUESTS reads

        /// <summary>
        /// The lowest unclaimed rung of <paramref name="ladder"/>, the one row SCR_QUESTS shows for it (06 section 5.4, 07);
        /// null once every rung is claimed or for an unknown ladder.
        /// </summary>
        public QuestDef LowestUnclaimedRung(string ladder)
        {
            if (ladder == null || !Db.AchievementLadders.TryGetValue(ladder, out var rungs)) return null;
            foreach (var def in rungs)
                if (QuestStateOf(def) != QuestStatus.CLAIMED) return def;
            return null;
        }

        /// <summary>Rungs of <paramref name="ladder"/> claimed so far ("3 / 11 rungs", 07); 0 for an unknown ladder.</summary>
        public int ClaimedRungs(string ladder)
        {
            if (ladder == null || !Db.AchievementLadders.TryGetValue(ladder, out var rungs)) return 0;
            int n = 0;
            foreach (var def in rungs) if (QuestStateOf(def) == QuestStatus.CLAIMED) n++;
            return n;
        }

        /// <summary>The rows of the Achievements tab: each ladder's lowest unclaimed rung, in ladder order (fully claimed ladders drop out).</summary>
        public IReadOnlyList<QuestDef> AchievementRows()
        {
            var rows = new List<QuestDef>(Db.AchievementLadders.Count);
            foreach (var def in Db.Achievements)
                if (def.Rung == 1)
                {
                    var low = LowestUnclaimedRung(def.Ladder);
                    if (low != null) rows.Add(low);
                }
            return rows;
        }
    }
}
