using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using HeroCat.Sim.Content.Dto;

namespace HeroCat.Sim.Content
{
    public sealed partial class ContentDb
    {
        /// <summary>
        /// Validates the loader-scope rules this skeleton implements, filters rows by tier, builds derived data and returns
        /// an immutable db (09 section 9.1). Order: enums (E021), keys (E020), refs (E022), constants (E040-E042), tier
        /// filter, construct. Throws <see cref="ContentBuildException"/> with every error found.
        /// The loader-scope table rules live in ContentDb.Rules.cs with exporter message parity (issue #126).
        /// </summary>
        public static ContentDb Build(ContentDtos dtos, Tier buildTier)
        {
            if (dtos == null) throw new ArgumentNullException(nameof(dtos));
            var b = new Builder(dtos, buildTier);
            var db = b.Run();
            if (b.Errors.Count > 0) throw new ContentBuildException(b.Errors.AsReadOnly());
            return db;
        }

        sealed partial class Builder
        {
            readonly ContentDtos _d;
            readonly Tier _tier;
            public readonly List<ContentError> Errors = new List<ContentError>();

            public Builder(ContentDtos d, Tier tier) { _d = d; _tier = tier; }

            void Err(string code, string loc, string msg) => Errors.Add(new ContentError(code, loc, msg));

            static T[] Rows<T>(T[] rows) => rows ?? Array.Empty<T>();

            bool Keep(string table, string key, string tier)
            {
                var t = Enum<Tier>(tier, $"{table}[{key}].tier");
                return t <= _tier;
            }

            TEnum Enum<TEnum>(string value, string loc) where TEnum : struct
            {
                if (value != null && System.Enum.TryParse(value, false, out TEnum e) && System.Enum.IsDefined(typeof(TEnum), e) &&
                    !char.IsDigit(value[0]))
                    return e;
                Err("E021", loc, $"'{value}' is not a valid {typeof(TEnum).Name} (allowed: {string.Join(", ", System.Enum.GetNames(typeof(TEnum)))})");
                return default;
            }

            void Unique<T>(string table, T[] rows, Func<T, string> key)
            {
                var first = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < rows.Length; i++)
                {
                    string k = key(rows[i]);
                    if (first.TryGetValue(k, out int at)) Err("E020", $"{table}[{k}]", $"duplicate key '{k}' (first seen at row {at})");
                    else first[k] = i + 1;
                }
            }

            void Required<T>(string table, T[] rows)
            {
                if (rows == null) Err("E001", table, $"missing required table '{table}'");
            }

            public ContentDb Run()
            {
                var d = _d;
                var db = new ContentDb { BuildTier = _tier };
                if (d.Manifest == null) Err("E901", "manifest", "manifest missing");
                db.Version = d.Manifest?.ContentVersion ?? "";
                db.ContentHash = d.Manifest?.ContentHash ?? "";
                db.MinClientVersion = d.Manifest?.MinClientVersion ?? "";

                Required("constants", d.Constants); Required("curves", d.Curves); Required("stats", d.Stats);
                Required("training_curves", d.TrainingCurves); Required("chapters", d.Chapters);
                Required("rarities", d.Rarities); Required("weapons", d.Weapons); Required("skills", d.Skills);
                Required("banners", d.Banners); Required("banner_levels", d.BannerLevels);
                Required("dupe_levels", d.DupeLevels); Required("hero_levels", d.HeroLevels);
                Required("fish_levels", d.FishLevels); Required("tank", d.Tank);
                Required("tutorial_steps", d.TutorialSteps); Required("progress_targets", d.ProgressTargets);
                Required("feature_unlocks", d.FeatureUnlocks);
                if (Errors.Count > 0) return null;

                // Loader-scope validation (ContentDb.Rules.cs, issue #126): keys first, then constants, refs and the
                // table rules sorted by table, exactly as the exporter reports them. Nothing is built from invalid rows.
                ValidateKeys(d);
                int ruleErrors = Errors.Count;
                db.Constants = BuildConstants(d.Constants);
                ValidateRules(d);
                SortByTable(ruleErrors);
                if (Errors.Count > 0) return null;
                var k = db.Constants;

                int heroCap = d.HeroLevels.Length > 0 ? d.HeroLevels[0].LevelCap : 0;
                int precompute = Math.Max(k.StageCap + k.BossEvery, heroCap) + 1;

                // curves
                Unique("curves", d.Curves, r => r.Id);
                var curves = new Dictionary<string, CurveDef>(StringComparer.Ordinal);
                foreach (var r in d.Curves) curves[r.Id] = new CurveDef(r.Id, r.Base, r.Growth, r.XOffset, precompute);
                db.Curves = new ReadOnlyDictionary<string, CurveDef>(curves);
                db.EnemyHp = RequiredCurve(curves, "enemy_hp");
                db.GoldPerKill = RequiredCurve(curves, "gold_per_kill");
                db.ExpPerKill = RequiredCurve(curves, "exp_per_kill");

                // stats and training
                Unique("stats", d.Stats, r => r.Id);
                var stats = new StatDef[System.Enum.GetValues(typeof(Stat)).Length];
                foreach (var r in d.Stats)
                {
                    var id = Enum<Stat>(r.Id, $"stats[{r.Id}].id");
                    if (!Keep("stats", r.Id, r.Tier)) continue;
                    stats[(int)id] = new StatDef(id, r.Trainable, r.PerLvl, r.Cap, r.Base, Enum<Tier>(r.Tier, $"stats[{r.Id}].tier"),
                                                 r.NameKey, r.DescKey, r.Icon);
                }
                for (int i = 0; i < stats.Length; i++)
                    if (stats[i] == null) Err("E044", "stats", $"missing row for Stat value '{(Stat)i}'");
                db.Stats = Array.AsReadOnly(stats);

                Unique("training_curves", d.TrainingCurves, r => r.Stat);
                var training = new Dictionary<Stat, TrainingCurveDef>();
                foreach (var r in d.TrainingCurves)
                {
                    if (!HasStatRow(d.Stats, r.Stat)) { Err("E022", $"training_curves[{r.Stat}].stat", $"'{r.Stat}' does not exist in stats"); continue; }
                    var s = Enum<Stat>(r.Stat, $"training_curves[{r.Stat}].stat");
                    training[s] = new TrainingCurveDef(s, new CurveDef("train_" + r.Stat.ToLowerInvariant(), r.Cost0, r.CostG, 0, precompute));
                }
                db.TrainingCurves = new ReadOnlyDictionary<Stat, TrainingCurveDef>(training);

                // chapters
                Unique("chapters", d.Chapters, r => r.Id);
                var chapters = new List<ChapterDef>();
                foreach (var r in d.Chapters)
                    if (Keep("chapters", r.Id, r.Tier))
                        chapters.Add(new ChapterDef(r.Id, r.Index, r.FromStage, r.NameKey, r.Background, r.Music, Enum<Tier>(r.Tier, $"chapters[{r.Id}].tier")));
                if (chapters.Count == 0) Err("E044", "chapters", "missing row for chapter index '1'");
                db.Chapters = chapters.AsReadOnly();

                // rarities, collectibles, banners
                Unique("rarities", d.Rarities, r => r.Id);
                var rarities = new Dictionary<Rarity, RarityDef>();
                foreach (var r in d.Rarities)
                {
                    var id = Enum<Rarity>(r.Id, $"rarities[{r.Id}].id");
                    rarities[id] = new RarityDef(id, r.Code, r.Color, r.ShapeIcon, r.WeaponAtkPct, r.WeaponPassivePct, r.SkillDpsPct,
                                                 r.CompanionDpsPct, r.RuneStatPct, r.LimitBreakCost, r.DupeOverflowRockCandy,
                                                 r.OverrideTolerance, r.NameKey);
                }
                foreach (Rarity rr in System.Enum.GetValues(typeof(Rarity)))
                    if (!rarities.ContainsKey(rr)) Err("E044", "rarities", $"missing row for Rarity value '{rr}'");
                db.Rarities = new ReadOnlyDictionary<Rarity, RarityDef>(rarities);

                Unique("weapons", d.Weapons, r => r.Id);
                var weapons = new Dictionary<string, WeaponDef>(StringComparer.Ordinal);
                foreach (var r in d.Weapons)
                    if (Keep("weapons", r.Id, r.Tier))
                        weapons[r.Id] = new WeaponDef(r.Id, Enum<Rarity>(r.Rarity, $"weapons[{r.Id}].rarity"), r.AtkPct, r.PassivePct,
                                                      Enum<Element>(r.Element, $"weapons[{r.Id}].element"), Enum<Tier>(r.Tier, $"weapons[{r.Id}].tier"),
                                                      r.NameKey, r.DescKey, r.Icon, r.Sprite);
                db.Weapons = new ReadOnlyDictionary<string, WeaponDef>(weapons);

                Unique("skills", d.Skills, r => r.Id);
                var skills = new Dictionary<string, SkillDef>(StringComparer.Ordinal);
                foreach (var r in d.Skills)
                {
                    if (!Keep("skills", r.Id, r.Tier)) continue;
                    var cast = Enum<CastType>(r.CastType, $"skills[{r.Id}].cast_type");
                    double perCast = 0, perTick = 0;
                    if (cast == CastType.INSTANT) perCast = r.DpsPct * r.CooldownTicks / k.AttackIntervalTicks;
                    else if (cast == CastType.DOT && r.DurationTicks.GetValueOrDefault() > 0)
                        perTick = r.DpsPct * r.CooldownTicks / r.DurationTicks.Value / k.AttackIntervalTicks;
                    skills[r.Id] = new SkillDef(r.Id, Enum<Rarity>(r.Rarity, $"skills[{r.Id}].rarity"), r.DpsPct, cast, r.CooldownTicks,
                                                r.DurationTicks, r.BuffPct, perCast, perTick, Enum<Tier>(r.Tier, $"skills[{r.Id}].tier"),
                                                r.NameKey, r.DescKey, r.Icon, r.Vfx);
                }
                db.Skills = new ReadOnlyDictionary<string, SkillDef>(skills);

                db.Banners = BuildBanners(d, weapons, skills);

                // dupes, awakening, hero levels
                var dupes = new List<int>();
                foreach (var r in d.DupeLevels) dupes.Add(r.CumCopies);
                db.DupeLevels = dupes.AsReadOnly();

                var awakening = new List<AwakeningDef>();
                foreach (var r in Rows(d.Awakening))
                    if (Keep("awakening", r.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Tier))
                        awakening.Add(new AwakeningDef(r.Rank, r.StageGate, r.RockCandyCost, r.Mult, r.CompanionSlotsAfter,
                                                       r.RuneSlotsAfter, Enum<Tier>(r.Tier, $"awakening[{r.Rank}].tier"), r.NameKey, r.Icon));
                db.Awakening = awakening.AsReadOnly();

                if (d.HeroLevels.Length != 1) Err("E045", "hero_levels", $"exactly one row required, found {d.HeroLevels.Length}");
                else
                {
                    var h = d.HeroLevels[0];
                    var mult = h.MultPerLevel.HasValue ? new CurveDef("hero_mult", 1.0, h.MultPerLevel.Value, 0, precompute) : null;
                    db.HeroLevels = new HeroLevelsDef(new CurveDef("hero_lvl_exp", h.Exp0, h.ExpG, 0, precompute), h.AtkPctPerLevel,
                                                      h.MultPerLevel, h.LevelCap, mult);
                }

                // fish board
                var fish = new List<FishLevelDef>();
                foreach (var r in d.FishLevels)
                    fish.Add(new FishLevelDef(r.Level, r.DiscoveryCoins, r.MissilePct, r.DiscoveryCoins * k.FishMergeCoinsPct,
                                              k.FishBoardDpsFactor * r.MissilePct, r.Sprite, r.Tint, r.NameKey));
                db.FishLevels = fish.AsReadOnly();
                if (d.Tank.Length != 1) Err("E045", "tank", $"exactly one row required, found {d.Tank.Length}");
                else
                {
                    var t = d.Tank[0];
                    double ticks = t.RefillS0 * k.TickHz;
                    int refillTicks = (int)Math.Round(ticks, MidpointRounding.AwayFromZero);
                    if (refillTicks != ticks)
                        Err("E043", "tank[default].refill_s0", $"value {t.RefillS0.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} out of range (refill_s0 * TICK_HZ must be a whole number of ticks)");
                    db.Tank = new TankDef(t.Cap0, t.RefillS0, refillTicks, t.ReleaseLevel, t.RefillFloorS);
                }

                // tutorial, targets, unlocks
                Unique("tutorial_steps", d.TutorialSteps, r => r.Id);
                var steps = new List<TutorialStepDef>();
                db.ProfileCreatedGrants = Array.Empty<Grant>();
                foreach (var r in d.TutorialSteps)
                {
                    if (!Keep("tutorial_steps", r.Id, r.Tier)) continue;
                    var trig = Enum<TutorialTrigger>(r.Trigger, $"tutorial_steps[{r.Id}].trigger");
                    var grants = Grants(r.Grants, $"tutorial_steps[{r.Id}].grants", weapons, skills);
                    steps.Add(new TutorialStepDef(r.Id, r.Order, trig, r.TriggerValue, r.Screen, r.Highlight, r.TextKey, r.ExpectEvent,
                                                  r.ExpectCount, grants, r.Skippable, Enum<Tier>(r.Tier, $"tutorial_steps[{r.Id}].tier")));
                    if (trig == TutorialTrigger.PROFILE_CREATED)
                    {
                        db.ProfileCreatedGrants = grants;
                        foreach (var g in grants)
                            if (g.ItemKind == "WEAPON" && db.TutorialWeaponId == null) db.TutorialWeaponId = g.ItemId;
                    }
                }
                db.TutorialSteps = steps.AsReadOnly();

                var targets = new List<ProgressTargetDef>();
                foreach (var r in d.ProgressTargets) targets.Add(new ProgressTargetDef(r.Day, r.Stage, r.Tolerance));
                db.ProgressTargets = targets.AsReadOnly();

                Unique("feature_unlocks", d.FeatureUnlocks, r => r.Feature);
                var unlocks = new Dictionary<string, FeatureUnlockDef>(StringComparer.Ordinal);
                foreach (var r in d.FeatureUnlocks)
                    if (Keep("feature_unlocks", r.Feature, r.Tier))
                        unlocks[r.Feature] = new FeatureUnlockDef(r.Feature, Enum<FeatureCondition>(r.Condition, $"feature_unlocks[{r.Feature}].condition"),
                                                                  r.Threshold, r.Screen, r.LockedTextKey, Enum<Tier>(r.Tier, $"feature_unlocks[{r.Feature}].tier"));
                db.FeatureUnlocks = new ReadOnlyDictionary<string, FeatureUnlockDef>(unlocks);

                var at = new List<AwakeningTargetDef>();
                foreach (var r in Rows(d.AwakeningTargets)) at.Add(new AwakeningTargetDef(r.Rank, r.MaxDaysAfterGate));
                db.AwakeningTargets = at.AsReadOnly();

                // research (optional until #158 ships the rows; tier-filtered like every tiered table)
                var research = Rows(d.Research);
                Unique("research", research, r => r.Id);
                var nodes = new Dictionary<string, ResearchDef>(StringComparer.Ordinal);
                foreach (var r in research)
                    if (Keep("research", r.Id, r.Tier))
                        nodes[r.Id] = new ResearchDef(r.Id, Enum<ResearchEffect>(r.Effect, $"research[{r.Id}].effect"), r.EffectPerLevel,
                                                      r.EffectFloor, r.MaxLevel, r.Cost0, r.CostG, r.UnlockAwakenRank,
                                                      Enum<Tier>(r.Tier, $"research[{r.Id}].tier"));
                db.Research = new ReadOnlyDictionary<string, ResearchDef>(nodes);
                return db;
            }

            static bool HasStatRow(StatRowDto[] stats, string id)
            {
                foreach (var s in stats) if (string.Equals(s.Id, id, StringComparison.Ordinal)) return true;
                return false;
            }

            CurveDef RequiredCurve(Dictionary<string, CurveDef> curves, string id)
            {
                if (curves.TryGetValue(id, out var c)) return c;
                Err("E044", "curves", $"missing row for curve '{id}'");
                return null;
            }

            Constants BuildConstants(ConstantRowDto[] rows)
            {
                var values = new Dictionary<string, Constants.Value>(StringComparer.Ordinal);
                foreach (var r in rows)
                {
                    string loc = $"constants[{r.Key}]";
                    string req = Content.Constants.RequiredType(r.Key);
                    if (req == null)
                    {
                        Err("E042", loc, $"unknown constant '{r.Key}' (add it to REQUIRED_CONSTANTS and ContentDb.Constants first)");
                        continue;
                    }
                    if (!string.Equals(r.Type, req, StringComparison.Ordinal))
                    {
                        Err("E041", loc + ".type", $"declared type '{r.Type}' does not match required type '{req}'");
                        continue;
                    }
                    if (TryValue(req, r.Value, out var v))
                    {
                        values[r.Key] = v;
                        ConstantRange(r.Key, req, v, loc + ".value");
                    }
                    else Err("E010", loc + ".value", $"expected {req}, got '{r.Value}'");
                }
                foreach (var key in Content.Constants.RequiredKeys)
                    if (!values.ContainsKey(key) && Content.Constants.RequiredType(key) != null && !HasKey(rows, key))
                        Err("E040", "constants", $"missing required constant '{key}'");
                ConstantBoard(values);
                return Content.Constants.From(values);
            }

            static bool HasKey(ConstantRowDto[] rows, string key)
            {
                foreach (var r in rows) if (string.Equals(r.Key, key, StringComparison.Ordinal)) return true;
                return false;
            }

            /// <summary>The loader hands values as long, double, bool or an array of longs/ints (never a JSON type).</summary>
            static bool TryValue(string type, object raw, out Constants.Value v)
            {
                v = default;
                switch (type)
                {
                    case "int":
                        if (raw is long li && li >= int.MinValue && li <= int.MaxValue) { v.Int = (int)li; return true; }
                        if (raw is int ii) { v.Int = ii; return true; }
                        return false;
                    case "long":
                        if (raw is long ll) { v.Long = ll; return true; }
                        if (raw is int il) { v.Long = il; return true; }
                        return false;
                    case "double":
                        if (raw is double dd) { v.Double = dd; return true; }
                        if (raw is long dl) { v.Double = dl; return true; }
                        if (raw is int di) { v.Double = di; return true; }
                        return false;
                    case "bool":
                        if (raw is bool bb) { v.Bool = bb; return true; }
                        return false;
                    case "intlist":
                        if (raw is int[] ia) { v.IntList = (int[])ia.Clone(); return ia.Length > 0; }
                        if (raw is long[] la)
                        {
                            var r = new int[la.Length];
                            for (int i = 0; i < la.Length; i++)
                            {
                                if (la[i] < int.MinValue || la[i] > int.MaxValue) return false;
                                r[i] = (int)la[i];
                            }
                            v.IntList = r;
                            return r.Length > 0;
                        }
                        return false;
                    default:
                        return false;
                }
            }

            IReadOnlyDictionary<Banner, BannerDef> BuildBanners(ContentDtos d, Dictionary<string, WeaponDef> weapons,
                                                                Dictionary<string, SkillDef> skills)
            {
                Unique("banners", d.Banners, r => r.Id);
                var levelsByBanner = new Dictionary<string, List<BannerLevelDef>>(StringComparer.Ordinal);
                foreach (var r in d.BannerLevels)
                {
                    if (!HasBanner(d.Banners, r.Banner))
                    {
                        Err("E022", $"banner_levels[{r.Banner},{r.Level}].banner", $"'{r.Banner}' does not exist in banners");
                        continue;
                    }
                    var rates = new Dictionary<Rarity, double>();
                    if (r.Rates != null)
                        foreach (var kv in r.Rates) rates[Enum<Rarity>(kv.Key, $"banner_levels[{r.Banner},{r.Level}].rates")] = kv.Value;
                    if (!levelsByBanner.TryGetValue(r.Banner, out var list)) levelsByBanner[r.Banner] = list = new List<BannerLevelDef>();
                    list.Add(new BannerLevelDef(r.Level, r.ExpRequired, new ReadOnlyDictionary<Rarity, double>(rates)));
                }

                var banners = new Dictionary<Banner, BannerDef>();
                foreach (var r in d.Banners)
                {
                    var id = Enum<Banner>(r.Id, $"banners[{r.Id}].id");
                    var tier = Enum<Tier>(r.Tier, $"banners[{r.Id}].tier");
                    var pool = new Dictionary<Rarity, List<string>>();
                    foreach (Rarity rr in System.Enum.GetValues(typeof(Rarity))) pool[rr] = new List<string>();
                    if (id == Banner.WEAPON) foreach (var w in weapons.Values) pool[w.Rarity].Add(w.Id);
                    if (id == Banner.SKILL) foreach (var s in skills.Values) pool[s.Rarity].Add(s.Id);
                    // TODO(#161): the companion pool; TODO(#227): the rune pool.
                    var poolRo = new Dictionary<Rarity, IReadOnlyList<string>>();
                    foreach (var kv in pool)
                    {
                        kv.Value.Sort(StringComparer.Ordinal);
                        poolRo[kv.Key] = kv.Value.AsReadOnly();
                    }
                    var size = new Dictionary<Rarity, int>();
                    if (r.PoolSize != null)
                        foreach (var kv in r.PoolSize) size[Enum<Rarity>(kv.Key, $"banners[{r.Id}].pool_size")] = kv.Value;
                    levelsByBanner.TryGetValue(r.Id, out var levels);
                    // A banner above the build tier stays listed (screens show it as unavailable) but is never enabled (09 section 5.32).
                    bool enabled = r.Enabled && tier <= _tier;
                    banners[id] = new BannerDef(id, enabled, tier, r.PullCostDiamonds, Enum<Content.Currency>(r.TicketCurrency, $"banners[{r.Id}].ticket_currency"),
                                                new ReadOnlyDictionary<Rarity, int>(size), r.PitySoftStart, r.PitySoftStep, r.PityHard,
                                                r.MShareOfLplus, r.GuaranteeBatchSize, Enum<Rarity>(r.GuaranteeMinRarity, $"banners[{r.Id}].guarantee_min_rarity"),
                                                r.ExpPerPull, r.SortOrder, r.NameKey, r.Icon, r.BannerArt,
                                                (levels ?? new List<BannerLevelDef>()).AsReadOnly(),
                                                new ReadOnlyDictionary<Rarity, IReadOnlyList<string>>(poolRo));
                }
                foreach (Banner bb in System.Enum.GetValues(typeof(Banner)))
                    if (!banners.ContainsKey(bb)) Err("E044", "banners", $"missing row for Banner value '{bb}'");
                return new ReadOnlyDictionary<Banner, BannerDef>(banners);
            }

            static bool HasBanner(BannerRowDto[] banners, string id)
            {
                foreach (var b in banners) if (string.Equals(b.Id, id, StringComparison.Ordinal)) return true;
                return false;
            }

            IReadOnlyList<Grant> Grants(GrantDto[] grants, string loc, Dictionary<string, WeaponDef> weapons, Dictionary<string, SkillDef> skills)
            {
                if (grants == null || grants.Length == 0) return Array.Empty<Grant>();
                var list = new List<Grant>();
                foreach (var g in grants)
                {
                    if (g.Currency != null)
                    {
                        list.Add(new Grant(Enum<Content.Currency>(g.Currency, loc), null, null, g.Amount));
                        continue;
                    }
                    bool known = (g.Kind == "WEAPON" && weapons.ContainsKey(g.Id ?? "")) || (g.Kind == "SKILL" && skills.ContainsKey(g.Id ?? ""));
                    if (!known)
                    {
                        Err("E022", loc, $"'{g.Id}' does not exist in {(g.Kind ?? "item").ToLowerInvariant()}s");
                        continue;
                    }
                    list.Add(new Grant(null, g.Kind, g.Id, g.Amount));
                }
                return list.AsReadOnly();
            }
        }
    }
}
