using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HeroCat.Sim.Content
{
    /// <summary>
    /// Immutable content loaded from Content/*.json (specs/05 section 9). Load once, share freely
    /// (read-only, safe across threads). Code never hard-codes a balance number; it reads it here.
    /// </summary>
    public sealed class ContentDb
    {
        public const int SupportedSchemaVersion = 1;

        static readonly string[] TableNames =
        {
            "banners", "boss_rules", "class_advances", "constants", "curves", "daily_quests", "gear_rarity",
            "gear_slots", "hero_level_thresholds", "hero_tiers", "heroes", "progress_targets", "tutorial_grants",
        };

        // Curves are precomputed to this many steps (covers STAGE_CAP); beyond it Eval keeps multiplying.
        const int CurvePrecompute = 10000;

        double[] _classMult;     // [k] = product of the first k advance multipliers
        CurveDef _gearSlot;      // a = 1, g = GEAR_SLOT_G

        ContentDb() { }

        public string Version { get; private set; }
        public string ContentHash { get; private set; }
        public string SourceRevision { get; private set; }

        public Constants Constants { get; private set; }
        public IReadOnlyDictionary<CurveId, CurveDef> Curves { get; private set; }
        public IReadOnlyList<BossRuleDef> BossRules { get; private set; }

        /// <summary>Index == sort_order == the slot index the sim and the save use.</summary>
        public IReadOnlyList<GearSlotDef> GearSlots { get; private set; }

        /// <summary>Indexed by (int)Rarity.</summary>
        public IReadOnlyList<GearRarityDef> GearRarity { get; private set; }

        /// <summary>Indexed by (int)Rarity.</summary>
        public IReadOnlyList<HeroTierDef> HeroTiers { get; private set; }

        public IReadOnlyDictionary<string, HeroDef> Heroes { get; private set; }

        /// <summary>Heroes in CSV order (C..M).</summary>
        public IReadOnlyList<HeroDef> HeroList { get; private set; }

        /// <summary>[0] is level 1 (cumulative copies).</summary>
        public IReadOnlyList<int> HeroLevelThresholds { get; private set; }

        public IReadOnlyDictionary<string, BannerDef> Banners { get; private set; }

        /// <summary>The P0 hero banner (first enabled banner of kind Hero).</summary>
        public BannerDef HeroBanner { get; private set; }

        public IReadOnlyList<ClassAdvanceDef> ClassAdvances { get; private set; }
        public IReadOnlyList<DailyQuestDef> DailyQuests { get; private set; }
        public IReadOnlyList<TutorialGrantDef> TutorialGrants { get; private set; }
        public IReadOnlyList<ProgressTargetDef> ProgressTargets { get; private set; }
        public IReadOnlyDictionary<string, string> Strings { get; private set; }

        public int GearSlotCount => GearSlots.Count;

        // ---------------------------------------------------------------- derived (specs/03 section 4)

        public double EnemyHp(int stage) => Curves[CurveId.EnemyHp].Eval(stage);
        public double GoldPerKill(int stage) => Curves[CurveId.GoldPerKill].Eval(stage);
        public double StatCost(int level) => Curves[CurveId.StatCost].Eval(level);

        /// <summary>GEAR_SLOT_G^tier - 1, before rarity. A fraction (0.5 = +50%).</summary>
        public double GearSlotPct(int tier) => _gearSlot.Eval(tier) - 1.0;

        public double ClassMult(int advances) =>
            _classMult[Math.Max(0, Math.Min(advances, _classMult.Length - 1))];

        public BossRuleDef BossRuleFor(int stage)
        {
            var rules = BossRules;
            for (int i = rules.Count - 1; i > 0; i--)
                if (rules[i].FromStage <= stage) return rules[i];
            return rules[0];
        }

        /// <summary>Count of thresholds &lt;= copies (0 when copies == 0).</summary>
        public int HeroLevel(int copies)
        {
            int lvl = 0;
            var t = HeroLevelThresholds;
            for (int i = 0; i < t.Count; i++) if (copies >= t[i]) lvl = i + 1;
            return lvl;
        }

        /// <summary>Percentage points (40 = +40%).</summary>
        public double HeroPct(HeroDef hero, int level) =>
            level <= 0 ? 0.0 : hero.BasePct * (1.0 + Constants.HeroLvlBonus * (level - 1));

        public string Text(string key) => Strings.TryGetValue(key, out var s) ? s : key;

        /// <summary>True when MAJOR matches. False on malformed input.</summary>
        public bool IsCompatibleWith(string saveContentVersion)
        {
            int? Major(string v)
            {
                if (string.IsNullOrEmpty(v)) return null;
                var parts = v.Split('.');
                if (parts.Length != 2) return null;
                if (!int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out _)) return null;
                return a;
            }
            var mine = Major(Version);
            var theirs = Major(saveContentVersion);
            return mine.HasValue && theirs.HasValue && mine.Value == theirs.Value;
        }

        // ---------------------------------------------------------------- loading

        public static ContentDb LoadFromDirectory(string directory, bool verifyHashes = true) =>
            Load(new FileContentSource(directory), verifyHashes);

        /// <summary>Throws ContentLoadException listing every problem found.</summary>
        public static ContentDb Load(IContentSource source, bool verifyHashes = true)
        {
            var errors = new List<ContentError>();
            if (!source.Exists("manifest.json"))
                throw new ContentLoadException(new[] { new ContentError("E001", "manifest", "manifest.json not found") });

            Dictionary<string, object> manifest;
            try { manifest = (Dictionary<string, object>)MiniJson.Parse(source.ReadText("manifest.json")); }
            catch (Exception e)
            {
                throw new ContentLoadException(new[] { new ContentError("E010", "manifest", e.Message) });
            }

            var db = new ContentDb();
            var m = new Row(manifest, "manifest");
            if (m.Int("schema_version") != SupportedSchemaVersion)
                errors.Add(new ContentError("E901", "manifest", $"unsupported schema_version {m.Int("schema_version")}"));
            db.Version = m.Str("content_version");
            db.SourceRevision = m.Str("source_revision");
            db.ContentHash = m.Str("content_hash");
            var listed = m.Obj("tables");

            var tables = new Dictionary<string, List<Row>>();
            var allNames = TableNames.Concat(new[] { "strings" });
            foreach (var name in allNames)
            {
                bool optional = name == "strings";
                if (!listed.TryGetValue(name, out var entryObj))
                {
                    if (!optional) errors.Add(new ContentError("E001", name, "table not listed in manifest.json"));
                    continue;
                }
                var entry = new Row((Dictionary<string, object>)entryObj, "manifest." + name);
                string file = entry.Str("file");
                if (!source.Exists(file))
                {
                    errors.Add(new ContentError("E001", name, $"{file} not found"));
                    continue;
                }
                string text = source.ReadText(file);
                if (verifyHashes)
                {
                    string actual = Sha256Hex(Encoding.UTF8.GetBytes(text));
                    if (!string.Equals(actual, entry.Str("sha256"), StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new ContentError("E900", file, "sha256 does not match manifest (rebuild with tools/content_build.py)"));
                        continue;
                    }
                }
                try
                {
                    var doc = (Dictionary<string, object>)MiniJson.Parse(text);
                    var rows = (List<object>)doc["rows"];
                    tables[name] = rows.Select((r, i) => new Row((Dictionary<string, object>)r, $"{name}[{i}]")).ToList();
                }
                catch (Exception e)
                {
                    errors.Add(new ContentError("E010", file, e.Message));
                }
            }
            if (errors.Count > 0) throw new ContentLoadException(errors);

            try { db.Build(tables, errors); }
            catch (Exception e) when (e is FormatException || e is KeyNotFoundException || e is InvalidCastException)
            {
                errors.Add(new ContentError("E010", "content", e.Message));
            }
            if (errors.Count > 0) throw new ContentLoadException(errors);
            return db;
        }

        void Build(Dictionary<string, List<Row>> t, List<ContentError> errors)
        {
            // constants
            var kv = t["constants"].ToDictionary(r => r.Str("key"), r => r);
            double D(string k) { if (kv.TryGetValue(k, out var r)) return r.Dbl("value"); errors.Add(new ContentError("E040", $"constants[{k}]", "required constant missing")); return 0; }
            int I(string k) { if (kv.TryGetValue(k, out var r)) return r.Int("value"); errors.Add(new ContentError("E040", $"constants[{k}]", "required constant missing")); return 0; }
            Constants = new Constants
            {
                TickHz = I("TICK_HZ"), BaseAtk = D("BASE_ATK"), AttackIntervalTicks = I("ATTACK_INTERVAL_TICKS"),
                KillsPerStage = I("KILLS_PER_STAGE"), SpawnDelayTicks = I("SPAWN_DELAY_TICKS"), BossEvery = I("BOSS_EVERY"),
                StageCap = I("STAGE_CAP"), StatPerLvl = D("STAT_PER_LVL"), GemsPerDayF2p = I("GEMS_PER_DAY_F2P"),
                OfflineRate = D("OFFLINE_RATE"), OfflineCapH = D("OFFLINE_CAP_H"), OfflineMinGapS = I("OFFLINE_MIN_GAP_S"),
                DaySeconds = I("DAY_SECONDS"), GearDropP = D("GEAR_DROP_P"), TierWidth = I("TIER_WIDTH"),
                GearSlotG = D("GEAR_SLOT_G"), GearSellKills = D("GEAR_SELL_KILLS"), EnhMax = I("ENH_MAX"),
                EnhPerLvl = D("ENH_PER_LVL"), EnhSuccessFloor = D("ENH_SUCCESS_FLOOR"), EnhSuccessStep = D("ENH_SUCCESS_STEP"),
                EnhCostGoldMult = D("ENH_COST_GOLD_MULT"), HeroSlots = I("HERO_SLOTS"), HeroLvlBonus = D("HERO_LVL_BONUS"),
                IdleChestHours = D("IDLE_CHEST_HOURS"), IdleChestGems = I("IDLE_CHEST_GEMS"),
            };
            if (errors.Count > 0) return;

            // curves
            var curves = new Dictionary<CurveId, CurveDef>();
            foreach (var r in t["curves"])
            {
                var id = ParseCurveId(r.Str("id"));
                curves[id] = new CurveDef(id, r.Dbl("base"), r.Dbl("growth"), CurvePrecompute);
            }
            foreach (CurveId id in new[] { CurveId.EnemyHp, CurveId.GoldPerKill, CurveId.StatCost })
                if (!curves.ContainsKey(id)) errors.Add(new ContentError("E044", $"curves[{id}]", "curve missing"));
            Curves = new ReadOnlyDictionary<CurveId, CurveDef>(curves);
            _gearSlot = new CurveDef(CurveId.EnemyHp, 1.0, Constants.GearSlotG, CurvePrecompute);

            // boss rules
            BossRules = t["boss_rules"].Select(r => new BossRuleDef
            {
                Id = r.Str("id"), FromStage = r.Int("from_stage"), HpMult = r.Dbl("hp_mult"), TimeTicks = r.Int("time_ticks"),
                GoldMult = r.Dbl("gold_mult"), GearDropP = r.Dbl("gear_drop_p"), GearMinRarity = ParseRarity(r.Str("gear_min_rarity")),
                FirstClearGems = r.Int("first_clear_gems"),
            }).OrderBy(b => b.FromStage).ToList().AsReadOnly();
            if (BossRules.Count == 0 || BossRules[0].FromStage != 1)
                errors.Add(new ContentError("E045", "boss_rules", "first row must have from_stage = 1"));

            // gear
            GearSlots = t["gear_slots"].Select(r => new GearSlotDef
            {
                Id = r.Str("id"), SortOrder = r.Int("sort_order"), NameKey = r.Str("name_key"), IconId = r.Str("icon_id"),
            }).OrderBy(s => s.SortOrder).ToList().AsReadOnly();
            for (int i = 0; i < GearSlots.Count; i++)
                if (GearSlots[i].SortOrder != i) errors.Add(new ContentError("E047", "gear_slots", "sort_order must be exactly 0..N-1"));

            GearRarity = t["gear_rarity"].Select(r => new GearRarityDef
            {
                Rarity = ParseRarity(r.Str("rarity")), Weight = r.Dbl("weight"), Mult = r.Dbl("mult"),
                DisplayColor = r.Str("display_color"), NameKey = r.Str("name_key"),
            }).OrderBy(g => g.Rarity).ToList().AsReadOnly();
            CheckFiveTiers(GearRarity.Select(g => g.Rarity), "gear_rarity", errors);
            double wsum = GearRarity.Sum(g => g.Weight);
            if (Math.Abs(wsum - 1.0) > 1e-9) errors.Add(new ContentError("E030", "gear_rarity", $"weights sum to {wsum:R}"));

            // heroes
            HeroTiers = t["hero_tiers"].Select(r => new HeroTierDef
            {
                Tier = ParseRarity(r.Str("tier")), DisplayColor = r.Str("display_color"), NameKey = r.Str("name_key"),
                ShardValue = r.Int("shard_value"),
            }).OrderBy(h => h.Tier).ToList().AsReadOnly();
            CheckFiveTiers(HeroTiers.Select(h => h.Tier), "hero_tiers", errors);

            var heroList = t["heroes"].Select(r => new HeroDef
            {
                Id = r.Str("id"), Tier = ParseRarity(r.Str("tier")), PoolId = r.Str("pool_id"), NameKey = r.Str("name_key"),
                DescKey = r.Str("desc_key"), IconId = r.Str("icon_id"), BasePct = r.Dbl("base_pct"),
            }).ToList();
            HeroList = heroList.AsReadOnly();
            var heroes = new Dictionary<string, HeroDef>(StringComparer.Ordinal);
            foreach (var h in heroList)
            {
                if (heroes.ContainsKey(h.Id)) errors.Add(new ContentError("E020", $"heroes[{h.Id}]", "duplicate id"));
                heroes[h.Id] = h;
            }
            Heroes = new ReadOnlyDictionary<string, HeroDef>(heroes);

            var thresholds = t["hero_level_thresholds"].OrderBy(r => r.Int("level")).Select(r => r.Int("cum_copies")).ToList();
            for (int i = 1; i < thresholds.Count; i++)
                if (thresholds[i] <= thresholds[i - 1])
                    errors.Add(new ContentError("E031", $"hero_level_thresholds[{i + 1}]", "cum_copies must strictly increase"));
            if (thresholds.Count == 0 || thresholds[0] != 1)
                errors.Add(new ContentError("E061", "hero_level_thresholds[1]", "level 1 must need exactly 1 copy"));
            HeroLevelThresholds = thresholds.AsReadOnly();

            // banners
            var banners = new Dictionary<string, BannerDef>(StringComparer.Ordinal);
            foreach (var r in t["banners"])
            {
                var ratesObj = r.Obj("rates");
                var rates = new double[5];
                for (int i = 0; i < 5; i++) rates[i] = new Row(ratesObj, r.Location + ".rates").Dbl(((Rarity)i).ToString());
                var b = new BannerDef
                {
                    Id = r.Str("id"), Kind = r.Str("kind") == "pet" ? BannerKind.Pet : BannerKind.Hero, NameKey = r.Str("name_key"),
                    PoolId = r.Str("pool_id"), Rates = Array.AsReadOnly(rates), BaseLplus = rates[(int)Rarity.L] + rates[(int)Rarity.M],
                    PitySoftStart = r.Int("pity_soft_start"), PitySoftStep = r.Dbl("pity_soft_step"), PityHard = r.Int("pity_hard"),
                    MythicShareOfLplus = r.Dbl("mythic_share_of_lplus"), GuaranteeBatchSize = r.Int("guarantee_batch_size"),
                    GuaranteeMinTier = ParseRarity(r.Str("guarantee_min_tier")), PullCostGems = r.Int("pull_cost_gems"),
                    FreePullsPerDay = r.Int("free_pulls_per_day"), RngStream = r.Str("rng_stream"), Enabled = r.Bool("enabled"),
                };
                if (Math.Abs(rates.Sum() - 1.0) > 1e-9)
                    errors.Add(new ContentError("E030", $"banners[{b.Id}]", $"rates sum to {rates.Sum():R}"));
                var pool = new IReadOnlyList<string>[5];
                for (int i = 0; i < 5; i++)
                {
                    var tier = (Rarity)i;
                    var ids = heroList.Where(h => h.PoolId == b.PoolId && h.Tier == tier).Select(h => h.Id)
                        .OrderBy(id => id, StringComparer.Ordinal).ToList();
                    if (rates[i] > 0 && ids.Count == 0)
                        errors.Add(new ContentError("E033", $"banners[{b.Id}]", $"tier {tier} has a rate but no heroes in pool"));
                    pool[i] = ids.AsReadOnly();
                }
                b.Pool = Array.AsReadOnly(pool);
                banners[b.Id] = b;
            }
            Banners = new ReadOnlyDictionary<string, BannerDef>(banners);
            HeroBanner = banners.Values.FirstOrDefault(b => b.Enabled && b.Kind == BannerKind.Hero);
            if (HeroBanner == null) errors.Add(new ContentError("E022", "banners", "no enabled hero banner"));

            // class advances
            ClassAdvances = t["class_advances"].Select(r => new ClassAdvanceDef
            {
                Id = r.Str("id"), Index = r.Int("index"), Stage = r.Int("stage"), Mult = r.Dbl("mult"),
                NameKey = r.Str("name_key"), IconId = r.Str("icon_id"),
            }).OrderBy(c => c.Index).ToList().AsReadOnly();
            _classMult = new double[ClassAdvances.Count + 1];
            _classMult[0] = 1.0;
            for (int i = 0; i < ClassAdvances.Count; i++)
            {
                _classMult[i + 1] = _classMult[i] * ClassAdvances[i].Mult;
                if (i > 0 && ClassAdvances[i].Stage <= ClassAdvances[i - 1].Stage)
                    errors.Add(new ContentError("E032", $"class_advances[{ClassAdvances[i].Id}]", "stage must strictly increase"));
            }

            DailyQuests = t["daily_quests"].Select(r => new DailyQuestDef
            {
                Id = r.Str("id"), Type = ParseQuestType(r.Str("type")), Target = r.Int("target"), RewardGems = r.Int("reward_gems"),
                SortOrder = r.Int("sort_order"), NameKey = r.Str("name_key"),
            }).OrderBy(q => q.SortOrder).ToList().AsReadOnly();

            TutorialGrants = t["tutorial_grants"].Select(r => new TutorialGrantDef
            {
                Id = r.Str("id"), Trigger = ParseGrantTrigger(r.Str("trigger")), TriggerValue = r.NInt("trigger_value"),
                GrantType = ParseGrantType(r.Str("grant_type")), Amount = r.Int("amount"), GrantRef = r.NStr("grant_ref"),
                SortOrder = r.Int("sort_order"),
            }).OrderBy(g => g.SortOrder).ToList().AsReadOnly();

            ProgressTargets = t["progress_targets"].Select(r => new ProgressTargetDef { Day = r.Int("day"), Stage = r.Int("stage") })
                .OrderBy(p => p.Day).ToList().AsReadOnly();

            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            if (t.TryGetValue("strings", out var srows))
                foreach (var r in srows) strings[r.Str("key")] = r.Str("text");
            Strings = new ReadOnlyDictionary<string, string>(strings);
        }

        static void CheckFiveTiers(IEnumerable<Rarity> got, string table, List<ContentError> errors)
        {
            if (!got.SequenceEqual(new[] { Rarity.C, Rarity.R, Rarity.E, Rarity.L, Rarity.M }))
                errors.Add(new ContentError("E044", table, "must list C, R, E, L, M exactly once"));
        }

        static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(bytes);
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static Rarity ParseRarity(string s)
        {
            switch (s)
            {
                case "C": return Rarity.C;
                case "R": return Rarity.R;
                case "E": return Rarity.E;
                case "L": return Rarity.L;
                case "M": return Rarity.M;
                default: throw new FormatException($"'{s}' is not one of C, R, E, L, M");
            }
        }

        static CurveId ParseCurveId(string s)
        {
            switch (s)
            {
                case "enemy_hp": return CurveId.EnemyHp;
                case "gold_per_kill": return CurveId.GoldPerKill;
                case "stat_cost": return CurveId.StatCost;
                default: throw new FormatException($"unknown curve id '{s}'");
            }
        }

        static QuestType ParseQuestType(string s)
        {
            switch (s)
            {
                case "login": return QuestType.Login;
                case "kill_mobs": return QuestType.KillMobs;
                case "clear_boss": return QuestType.ClearBoss;
                case "do_pulls": return QuestType.DoPulls;
                default: throw new FormatException($"unknown quest type '{s}'");
            }
        }

        static GrantTrigger ParseGrantTrigger(string s)
        {
            switch (s)
            {
                case "profile_created": return GrantTrigger.ProfileCreated;
                case "stage_reached": return GrantTrigger.StageReached;
                case "first_boss_cleared": return GrantTrigger.FirstBossCleared;
                default: throw new FormatException($"unknown grant trigger '{s}'");
            }
        }

        static GrantType ParseGrantType(string s)
        {
            switch (s)
            {
                case "gems": return GrantType.Gems;
                case "free_pulls": return GrantType.FreePulls;
                case "gold": return GrantType.Gold;
                case "hero": return GrantType.Hero;
                default: throw new FormatException($"unknown grant type '{s}'");
            }
        }

        /// <summary>Typed accessor over one parsed JSON object; throws FormatException naming the location.</summary>
        sealed class Row
        {
            readonly Dictionary<string, object> _d;

            public Row(Dictionary<string, object> d, string location)
            {
                _d = d;
                Location = location;
            }

            public string Location { get; }

            object Get(string k)
            {
                if (!_d.TryGetValue(k, out var v)) throw new FormatException($"{Location}.{k}: missing");
                return v;
            }

            public string Str(string k) => Get(k) as string ?? throw new FormatException($"{Location}.{k}: expected string");
            public string NStr(string k) => _d.TryGetValue(k, out var v) ? v as string : null;

            public int Int(string k)
            {
                if (Get(k) is long l && l >= int.MinValue && l <= int.MaxValue) return (int)l;
                throw new FormatException($"{Location}.{k}: expected int");
            }

            public int? NInt(string k) => _d.TryGetValue(k, out var v) && v is long l ? (int)l : (int?)null;

            public double Dbl(string k)
            {
                var v = Get(k);
                if (v is double d) return d;
                if (v is long l) return l;
                throw new FormatException($"{Location}.{k}: expected number");
            }

            public bool Bool(string k) => Get(k) is bool b ? b : throw new FormatException($"{Location}.{k}: expected bool");

            public Dictionary<string, object> Obj(string k) =>
                Get(k) as Dictionary<string, object> ?? throw new FormatException($"{Location}.{k}: expected object");
        }
    }
}
