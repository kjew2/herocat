using System;
using System.Collections.Generic;

namespace HeroCat.Sim.Content
{
    /// <summary>C &lt; R &lt; E &lt; L &lt; M. Used for gear rarity and hero tier alike (order is load-bearing).</summary>
    public enum Rarity { C = 0, R = 1, E = 2, L = 3, M = 4 }

    public enum BannerKind { Hero, Pet }
    public enum CurveId { EnemyHp, GoldPerKill, StatCost }

    /// <summary>ClearBoss counts boss fights STARTED, not cleared (specs/00 section 7.2).</summary>
    public enum QuestType { Login = 0, KillMobs = 1, ClearBoss = 2, DoPulls = 3 }

    public enum GrantTrigger { ProfileCreated, StageReached, FirstBossCleared }
    public enum GrantType { Gems, FreePulls, Gold, Hero }

    /// <summary>Scalar constants (specs/05 section 9.2). Property name = PascalCase of the content key.</summary>
    public sealed class Constants
    {
        public int TickHz { get; internal set; }                 // TICK_HZ
        public double BaseAtk { get; internal set; }             // BASE_ATK
        public int AttackIntervalTicks { get; internal set; }    // ATTACK_INTERVAL_TICKS
        public int KillsPerStage { get; internal set; }          // KILLS_PER_STAGE
        public int SpawnDelayTicks { get; internal set; }        // SPAWN_DELAY_TICKS
        public int BossEvery { get; internal set; }              // BOSS_EVERY
        public int StageCap { get; internal set; }               // STAGE_CAP
        public double StatPerLvl { get; internal set; }          // STAT_PER_LVL
        public int GemsPerDayF2p { get; internal set; }          // GEMS_PER_DAY_F2P
        public double OfflineRate { get; internal set; }         // OFFLINE_RATE
        public double OfflineCapH { get; internal set; }         // OFFLINE_CAP_H
        public int OfflineMinGapS { get; internal set; }         // OFFLINE_MIN_GAP_S
        public int DaySeconds { get; internal set; }             // DAY_SECONDS
        public double GearDropP { get; internal set; }           // GEAR_DROP_P
        public int TierWidth { get; internal set; }              // TIER_WIDTH
        public double GearSlotG { get; internal set; }           // GEAR_SLOT_G
        public double GearSellKills { get; internal set; }       // GEAR_SELL_KILLS
        public int EnhMax { get; internal set; }                 // ENH_MAX
        public double EnhPerLvl { get; internal set; }           // ENH_PER_LVL
        public double EnhSuccessFloor { get; internal set; }     // ENH_SUCCESS_FLOOR
        public double EnhSuccessStep { get; internal set; }      // ENH_SUCCESS_STEP
        public double EnhCostGoldMult { get; internal set; }     // ENH_COST_GOLD_MULT
        public int HeroSlots { get; internal set; }              // HERO_SLOTS
        public double HeroLvlBonus { get; internal set; }        // HERO_LVL_BONUS
        public double IdleChestHours { get; internal set; }      // IDLE_CHEST_HOURS
        public int IdleChestGems { get; internal set; }          // IDLE_CHEST_GEMS

        public int TierOf(int stage) => (stage / TierWidth) * TierWidth;
        public bool IsBossStage(int stage) => stage % BossEvery == 0;
        public double EnhSuccess(int level) => Math.Max(EnhSuccessFloor, 1.0 - EnhSuccessStep * level);
    }

    /// <summary>
    /// base * growth^x, evaluated by repeated multiplication (never Math.Pow; specs/03 section 4).
    /// Precomputed at load so a shared ContentDb is read-only (thread-safe); beyond the table it keeps
    /// multiplying from the last entry, which gives the same bits.
    /// </summary>
    public sealed class CurveDef
    {
        readonly double[] _pow;

        internal CurveDef(CurveId id, double @base, double growth, int precompute)
        {
            Id = id;
            Base = @base;
            Growth = growth;
            _pow = new double[precompute + 1];
            _pow[0] = 1.0;
            for (int i = 1; i < _pow.Length; i++) _pow[i] = _pow[i - 1] * growth;
        }

        public CurveId Id { get; }
        public double Base { get; }
        public double Growth { get; }

        /// <summary>growth^x.</summary>
        public double Pow(int x)
        {
            if (x < _pow.Length) return _pow[x];
            double v = _pow[_pow.Length - 1];
            for (int i = _pow.Length - 1; i < x; i++) v *= Growth;
            return v;
        }

        public double Eval(int x) => Base * Pow(x);
    }

    public sealed class BossRuleDef
    {
        public string Id { get; internal set; }
        public int FromStage { get; internal set; }
        public double HpMult { get; internal set; }
        public int TimeTicks { get; internal set; }
        public double GoldMult { get; internal set; }
        public double GearDropP { get; internal set; }
        public Rarity GearMinRarity { get; internal set; }
        public int FirstClearGems { get; internal set; }
    }

    public sealed class GearSlotDef
    {
        public string Id { get; internal set; }
        public int SortOrder { get; internal set; }
        public string NameKey { get; internal set; }
        public string IconId { get; internal set; }
    }

    public sealed class GearRarityDef
    {
        public Rarity Rarity { get; internal set; }
        public double Weight { get; internal set; }
        public double Mult { get; internal set; }
        public string DisplayColor { get; internal set; }
        public string NameKey { get; internal set; }
    }

    public sealed class HeroTierDef
    {
        public Rarity Tier { get; internal set; }
        public string DisplayColor { get; internal set; }
        public string NameKey { get; internal set; }
        public int ShardValue { get; internal set; }
    }

    public sealed class HeroDef
    {
        public string Id { get; internal set; }
        public Rarity Tier { get; internal set; }
        public string PoolId { get; internal set; }
        public string NameKey { get; internal set; }
        public string DescKey { get; internal set; }
        public string IconId { get; internal set; }
        public double BasePct { get; internal set; }
    }

    public sealed class BannerDef
    {
        public string Id { get; internal set; }
        public BannerKind Kind { get; internal set; }
        public string NameKey { get; internal set; }
        public string PoolId { get; internal set; }

        /// <summary>Indexed by (int)Rarity.</summary>
        public IReadOnlyList<double> Rates { get; internal set; }

        public double BaseLplus { get; internal set; }
        public int PitySoftStart { get; internal set; }
        public double PitySoftStep { get; internal set; }
        public int PityHard { get; internal set; }
        public double MythicShareOfLplus { get; internal set; }
        public int GuaranteeBatchSize { get; internal set; }
        public Rarity GuaranteeMinTier { get; internal set; }
        public int PullCostGems { get; internal set; }
        public int FreePullsPerDay { get; internal set; }
        public string RngStream { get; internal set; }
        public bool Enabled { get; internal set; }

        /// <summary>Hero ids per tier, ordinal-sorted. Indexed by (int)Rarity.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Pool { get; internal set; }

        /// <summary>L+ chance on the n-th pull since the last L+ (specs/01 section 3.1).</summary>
        public double PityRate(int n) =>
            n >= PityHard ? 1.0 : Math.Min(1.0, BaseLplus + PitySoftStep * Math.Max(0, n - PitySoftStart));
    }

    public sealed class ClassAdvanceDef
    {
        public string Id { get; internal set; }
        public int Index { get; internal set; }
        public int Stage { get; internal set; }
        public double Mult { get; internal set; }
        public string NameKey { get; internal set; }
        public string IconId { get; internal set; }
    }

    public sealed class DailyQuestDef
    {
        public string Id { get; internal set; }
        public QuestType Type { get; internal set; }
        public int Target { get; internal set; }
        public int RewardGems { get; internal set; }
        public int SortOrder { get; internal set; }
        public string NameKey { get; internal set; }
    }

    public sealed class TutorialGrantDef
    {
        public string Id { get; internal set; }
        public GrantTrigger Trigger { get; internal set; }
        public int? TriggerValue { get; internal set; }
        public GrantType GrantType { get; internal set; }
        public int Amount { get; internal set; }
        public string GrantRef { get; internal set; }
        public int SortOrder { get; internal set; }
    }

    public sealed class ProgressTargetDef
    {
        public int Day { get; internal set; }
        public int Stage { get; internal set; }
    }

    public readonly struct ContentError
    {
        public ContentError(string code, string location, string message)
        {
            Code = code;
            Location = location;
            Message = message;
        }

        public string Code { get; }
        public string Location { get; }
        public string Message { get; }
        public override string ToString() => $"CONTENT {Code} {Location}: {Message}";
    }

    public sealed class ContentLoadException : Exception
    {
        public ContentLoadException(IReadOnlyList<ContentError> errors)
            : base("Content failed to load:\n" + string.Join("\n", errors))
        {
            Errors = errors;
        }

        public IReadOnlyList<ContentError> Errors { get; }
    }

    public interface IContentSource
    {
        bool Exists(string fileName);
        string ReadText(string fileName);
    }

    /// <summary>Harness, tests, editor: reads Content/*.json from a directory.</summary>
    public sealed class FileContentSource : IContentSource
    {
        readonly string _dir;

        public FileContentSource(string directory) { _dir = directory; }

        public bool Exists(string fileName) => System.IO.File.Exists(System.IO.Path.Combine(_dir, fileName));

        public string ReadText(string fileName) =>
            System.IO.File.ReadAllText(System.IO.Path.Combine(_dir, fileName), new System.Text.UTF8Encoding(false));

        /// <summary>Walks up from <paramref name="startDirectory"/> looking for Content/manifest.json. Null if none.</summary>
        public static string FindContentDirectory(string startDirectory)
        {
            var dir = new System.IO.DirectoryInfo(startDirectory);
            while (dir != null)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, "Content");
                if (System.IO.File.Exists(System.IO.Path.Combine(candidate, "manifest.json"))) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }

    /// <summary>In-memory source (tests, Unity TextAssets already read into strings).</summary>
    public sealed class DictionaryContentSource : IContentSource
    {
        readonly IReadOnlyDictionary<string, string> _files;

        public DictionaryContentSource(IReadOnlyDictionary<string, string> files) { _files = files; }

        public bool Exists(string fileName) => _files.ContainsKey(fileName);
        public string ReadText(string fileName) => _files[fileName];
    }
}
