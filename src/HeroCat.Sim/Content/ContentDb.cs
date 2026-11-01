using System;
using System.Collections.Generic;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Content
{
    /// <summary>
    /// Immutable, validated content (09 section 9). Built once per process by <see cref="Build"/> from DTOs the
    /// Runtime's ContentLoader filled; the Sim never sees JSON or a file (00 contract 1, 09 principle 5). Share one
    /// instance freely, across threads included: nothing in it mutates after Build.
    /// </summary>
    public sealed partial class ContentDb
    {
        ContentDb() { }

        public string Version { get; private set; }            // manifest.content_version
        public string ContentHash { get; private set; }        // manifest.content_hash
        public string MinClientVersion { get; private set; }
        public Tier BuildTier { get; private set; }

        public Constants Constants { get; private set; }
        public IReadOnlyDictionary<string, CurveDef> Curves { get; private set; }
        public CurveDef EnemyHp { get; private set; }
        public CurveDef GoldPerKill { get; private set; }
        public CurveDef ExpPerKill { get; private set; }

        /// <summary>Index == (int)Stat; all eight rows.</summary>
        public IReadOnlyList<StatDef> Stats { get; private set; }
        public IReadOnlyDictionary<Stat, TrainingCurveDef> TrainingCurves { get; private set; }

        /// <summary>By Index (row order).</summary>
        public IReadOnlyList<ChapterDef> Chapters { get; private set; }

        public IReadOnlyDictionary<Rarity, RarityDef> Rarities { get; private set; }
        public IReadOnlyDictionary<string, WeaponDef> Weapons { get; private set; }
        public IReadOnlyDictionary<string, SkillDef> Skills { get; private set; }
        public IReadOnlyDictionary<Banner, BannerDef> Banners { get; private set; }

        /// <summary>[0] is level 1's cumulative copies (DUPE_LVL_CUM).</summary>
        public IReadOnlyList<int> DupeLevels { get; private set; }

        /// <summary>By Rank. MVP rows; empty in an M0 build.</summary>
        public IReadOnlyList<AwakeningDef> Awakening { get; private set; }
        public HeroLevelsDef HeroLevels { get; private set; }

        /// <summary>[0] is level 1. Its count is FISH_MAX_LVL_HARD (05 section 2.2).</summary>
        public IReadOnlyList<FishLevelDef> FishLevels { get; private set; }
        public TankDef Tank { get; private set; }

        /// <summary>By Order. TUTORIAL_DONE is this count (issue #46).</summary>
        public IReadOnlyList<TutorialStepDef> TutorialSteps { get; private set; }

        /// <summary>By Day (00 section 6.6 as data).</summary>
        public IReadOnlyList<ProgressTargetDef> ProgressTargets { get; private set; }
        public IReadOnlyDictionary<string, FeatureUnlockDef> FeatureUnlocks { get; private set; }

        /// <summary>By Rank; tools and bots only.</summary>
        public IReadOnlyList<AwakeningTargetDef> AwakeningTargets { get; private set; }

        /// <summary>TUTORIAL_WEAPON_ID: the item grant on the PROFILE_CREATED step (tut_welcome); null if absent.</summary>
        public string TutorialWeaponId { get; private set; }

        /// <summary>The PROFILE_CREATED step's grants (TUTORIAL_DIAMONDS and the starter weapon); empty if absent.</summary>
        public IReadOnlyList<Grant> ProfileCreatedGrants { get; private set; }

        /// <summary>Research nodes by id (09 section 5.18). Empty when the table is absent or filtered out by tier (M0).</summary>
        public IReadOnlyDictionary<string, ResearchDef> Research { get; private set; }

        /// <summary>The node with <paramref name="effect"/> (one per effect, E046), or null.</summary>
        public ResearchDef ResearchFor(ResearchEffect effect)
        {
            foreach (var r in Research.Values) if (r.Effect == effect) return r;
            return null;
        }

        /// <summary>quests_daily rows by SortOrder (09 section 5.21), bonus rows included. Empty in an M0 build (tier MVP rows).</summary>
        public IReadOnlyList<QuestDef> QuestsDaily { get; private set; }

        /// <summary>quests_weekly rows by SortOrder (09 section 5.22). Empty in an M0 build.</summary>
        public IReadOnlyList<QuestDef> QuestsWeekly { get; private set; }

        IReadOnlyDictionary<string, QuestDef> _dailyById, _weeklyById;

        /// <summary>The rows of one kind by SortOrder. ACHIEVEMENT rows are issue #171's (empty until then).</summary>
        public IReadOnlyList<QuestDef> QuestsOf(QuestKind kind) =>
            kind == QuestKind.DAILY ? QuestsDaily : kind == QuestKind.WEEKLY ? QuestsWeekly : (IReadOnlyList<QuestDef>)Array.Empty<QuestDef>();

        /// <summary>A daily row, else a weekly row, with this id; null if neither table (after the tier filter) has it.</summary>
        public QuestDef Quest(string id)
        {
            if (id == null) return null;
            if (_dailyById.TryGetValue(id, out var d)) return d;
            return _weeklyById.TryGetValue(id, out var w) ? w : null;
        }

        // ------------------------------------------------------------------------------------------- accessors

        /// <summary>Gold for <paramref name="count"/> levels of <paramref name="s"/> from level <paramref name="fromLevel"/> (closed form).</summary>
        public double TrainCost(Stat s, int fromLevel, int count) => TrainingCurves[s].Cost.EvalSum(fromLevel, count);

        public StatDef Stat(Stat s) => Stats[(int)s];

        /// <summary>HERO_LVL_EXP(l): EXP from level l to l + 1.</summary>
        public double HeroLvlExp(int level) => HeroLevels.Exp.Eval(level);

        /// <summary>HERO_LVL_MULT^level from the cached table (bit-identical to repeated multiplication).</summary>
        public double HeroMult(int level) => HeroLevels.Mult != null ? HeroLevels.Mult.Eval(level) : 1.0;

        /// <summary>The chapter row for a stage; stages past the last chapter reuse it (09 section 5.5).</summary>
        public ChapterDef ChapterFor(int stage)
        {
            int index = Constants.ChapterOf(stage < 1 ? 1 : stage);
            if (index > Chapters.Count) index = Chapters.Count;
            return Chapters[index - 1];
        }

        /// <summary>Count of DUPE_LVL_CUM thresholds &lt;= copies; 0 if none (04 section 5.1).</summary>
        public int DupeLevel(int copies)
        {
            int n = 0;
            for (int i = 0; i < DupeLevels.Count; i++) if (DupeLevels[i] <= copies) n++;
            return n;
        }

        public double LevelMult(int level) => 1.0 + Constants.DupeLvlBonus * (level - 1);

        public IReadOnlyList<string> Pool(Banner b, Rarity r) => Banners[b].Pool[r];

        /// <summary>The one feature-gate evaluator (09 section 5.34). A feature with no row (filtered out by tier) is locked.</summary>
        public bool IsUnlocked(string feature, GameState s)
        {
            if (!FeatureUnlocks.TryGetValue(feature, out var f)) return false;
            switch (f.Condition)
            {
                case FeatureCondition.ALWAYS: return true;
                case FeatureCondition.STAGE_HIGHEST: return s.Progress.HighestCleared >= f.Threshold.GetValueOrDefault();
                case FeatureCondition.AWAKEN_RANK: return s.Hero.AwakenRank >= f.Threshold.GetValueOrDefault();
                default: return false;
            }
        }
    }
}
