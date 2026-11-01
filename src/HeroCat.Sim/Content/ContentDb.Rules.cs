using System;
using System.Collections.Generic;
using System.Globalization;
using HeroCat.Sim.Content.Dto;

namespace HeroCat.Sim.Content
{
    public sealed partial class ContentDb
    {
        /// <summary>A double as the exporter prints it (Python repr, 09 section 8.1).</summary>
        internal static string FormatDouble(double v) => Builder.PyRepr(v);

        // Loader-scope validation (09 sections 7.2, 8.4, 8.6; issue #126): the exporter's table rules over the tables
        // the sim reads, with byte-identical messages (acceptance 20). Each rule mirrors the function of the same name in
        // tools/export_content.py and runs in the same order; errors are then sorted by table (ContentRules.TableOrder),
        // so tests/content_fixtures/ yields the same first line from both validators (ContentDbFixtureTests).
        //
        // Not here, by design: E07x (strings never reach the Sim), E09x (no previous build), the warnings, E039
        // (a budget check for the designer, not a sim guard), and the rules over tables the sim does not read
        // (enemies/bosses E057, shop E031/E046/E059, inbox). TODO(#170, #171, #173): quests (E054 filter, E064),
        // achievements (E031, E056, E060, E065) and dungeons (E034, E048, E054, E059, E060) join when those tables
        // are built into ContentDb.
        sealed partial class Builder
        {
            // literal-ok-begin: validation bounds and tolerances 09 section 5 states (not balance), percent display
            const double RateSumTolerance = 1e-9;
            const double RateEqualTolerance = 1e-12;
            const double OverrideEpsilon = 1e-12;
            const int SkillCooldownMin = 30, SkillCooldownMax = 300, CompanionCooldownMin = 20, CompanionCooldownMax = 600;
            const int BoardCellsMax = 64;
            const double Percent = 100.0;
            // literal-ok-end

            static readonly string[] RateCols = { "rate_c", "rate_r", "rate_e", "rate_l", "rate_m" };
            static readonly string[] PoolCols = { "pool_c", "pool_r", "pool_e", "pool_l", "pool_m" };

            /// <summary>A DTO row as the exporter's rules see it: column name to boxed int, long, double, bool, string or null.</summary>
            sealed class Row : Dictionary<string, object>
            {
                public Row(string loc) : base(StringComparer.Ordinal) { Loc = loc; }
                public string Loc { get; }
                public string S(string col) => this[col] as string;
            }

            Dictionary<string, List<Row>> _rows;
            Dictionary<string, object> _k;

            List<Row> T(string table) => _rows.TryGetValue(table, out var r) ? r : new List<Row>();
            bool Has(string table) => _rows.ContainsKey(table);

            // ------------------------------------------------------------------------------------- formatting (8.1)

            internal static string Fmt(object v)
            {
                switch (v)
                {
                    case null: return "empty";
                    case bool b: return b ? "true" : "false";
                    case double d: return PyRepr(d);
                    case int i: return i.ToString(CultureInfo.InvariantCulture);
                    case long l: return l.ToString(CultureInfo.InvariantCulture);
                    case int[] a:
                        var parts = new string[a.Length];
                        for (int j = 0; j < a.Length; j++) parts[j] = a[j].ToString(CultureInfo.InvariantCulture);
                        return "[" + string.Join(", ", parts) + "]";
                    default: return Convert.ToString(v, CultureInfo.InvariantCulture);
                }
            }

            /// <summary>
            /// Python's repr(float): the shortest round-trip digits, fixed notation for decimal exponents -4..15 (always with
            /// a fraction, `10.0`), else `1e-05` / `1.5e+16`. The exporter prints doubles this way (09 section 8.1).
            /// </summary>
            internal static string PyRepr(double v)
            {
                if (double.IsNaN(v)) return "nan";
                if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
                string r = v.ToString("R", CultureInfo.InvariantCulture);
                bool neg = r[0] == '-';
                if (neg) r = r.Substring(1);
                int exp = 0;
                int ei = r.IndexOfAny(new[] { 'E', 'e' });
                if (ei >= 0)
                {
                    exp = int.Parse(r.Substring(ei + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    r = r.Substring(0, ei);
                }
                int dot = r.IndexOf('.');
                string intPart = dot < 0 ? r : r.Substring(0, dot);
                string all = intPart + (dot < 0 ? "" : r.Substring(dot + 1));
                string digits = all.TrimStart('0');
                int lead = all.Length - digits.Length;
                digits = digits.TrimEnd('0');
                if (digits.Length == 0) return neg ? "-0.0" : "0.0";
                int e10 = intPart.Length + exp - lead - 1;   // decimal exponent of the first significant digit
                string body;
                // literal-ok-begin: Python's float_repr_style switch points (repr uses exponent form outside 1e-4 .. 1e16)
                if (e10 < -4 || e10 >= 16)
                    body = digits.Substring(0, 1) + (digits.Length > 1 ? "." + digits.Substring(1) : "") + "e" + (e10 < 0 ? "-" : "+") +
                           Math.Abs(e10).ToString("00", CultureInfo.InvariantCulture);
                // literal-ok-end
                else if (e10 < 0)
                    body = "0." + new string('0', -e10 - 1) + digits;
                else
                {
                    int ip = e10 + 1;
                    body = digits.Length <= ip ? digits + new string('0', ip - digits.Length) + ".0"
                                               : digits.Substring(0, ip) + "." + digits.Substring(ip);
                }
                return neg ? "-" + body : body;
            }

            static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

            static double N(object v) => Convert.ToDouble(v, CultureInfo.InvariantCulture);

            // ----------------------------------------------------------------------------------- DTOs as rule rows

            static void Add<T>(Dictionary<string, List<Row>> into, string table, T[] rows, Func<T, string> key, Action<T, Row> fill)
            {
                if (rows == null) return;
                var list = new List<Row>(rows.Length);
                foreach (var r in rows)
                {
                    var row = new Row(table + "[" + key(r) + "]");
                    fill(r, row);
                    list.Add(row);
                }
                into[table] = list;
            }

            static void Fold<TV>(Row row, string[] cols, Dictionary<string, TV> folded)
            {
                var names = System.Enum.GetNames(typeof(Rarity));
                for (int i = 0; i < cols.Length; i++)
                {
                    TV v = default;
                    bool has = folded != null && folded.TryGetValue(names[i], out v);
                    row[cols[i]] = has ? (object)v : null;
                }
            }

            Dictionary<string, List<Row>> RowsOf(ContentDtos d)
            {
                var t = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
                Add(t, "curves", d.Curves, r => r.Id, (r, x) => { x["id"] = r.Id; x["base"] = r.Base; x["growth"] = r.Growth; });
                Add(t, "stats", d.Stats, r => r.Id, (r, x) =>
                {
                    x["id"] = r.Id; x["trainable"] = r.Trainable; x["per_lvl"] = r.PerLvl; x["cap"] = r.Cap; x["tier"] = r.Tier;
                });
                Add(t, "training_curves", d.TrainingCurves, r => r.Stat, (r, x) => { x["stat"] = r.Stat; x["cost0"] = r.Cost0; x["cost_g"] = r.CostG; });
                Add(t, "chapters", d.Chapters, r => r.Id, (r, x) => { x["id"] = r.Id; x["index"] = r.Index; x["from_stage"] = r.FromStage; });
                Add(t, "rarities", d.Rarities, r => r.Id, (r, x) =>
                {
                    x["id"] = r.Id; x["weapon_atk_pct"] = r.WeaponAtkPct; x["weapon_passive_pct"] = r.WeaponPassivePct;
                    x["skill_dps_pct"] = r.SkillDpsPct; x["companion_dps_pct"] = r.CompanionDpsPct; x["rune_stat_pct"] = r.RuneStatPct;
                    x["limit_break_cost"] = r.LimitBreakCost; x["dupe_overflow_rock_candy"] = r.DupeOverflowRockCandy;
                    x["override_tolerance"] = r.OverrideTolerance;
                });
                Add(t, "weapons", d.Weapons, r => r.Id, (r, x) =>
                {
                    x["id"] = r.Id; x["rarity"] = r.Rarity; x["atk_pct"] = r.AtkPct; x["passive_pct"] = r.PassivePct; x["tier"] = r.Tier;
                });
                Add(t, "skills", d.Skills, r => r.Id, (r, x) =>
                {
                    x["id"] = r.Id; x["rarity"] = r.Rarity; x["dps_pct"] = r.DpsPct; x["cast_type"] = r.CastType;
                    x["cooldown_ticks"] = r.CooldownTicks; x["duration_ticks"] = r.DurationTicks; x["buff_pct"] = r.BuffPct; x["tier"] = r.Tier;
                });
                Add(t, "companions", d.Companions, r => r.Id, (r, x) =>
                {
                    x["id"] = r.Id; x["rarity"] = r.Rarity; x["ability_type"] = r.AbilityType; x["dps_pct"] = r.DpsPct; x["mp_cost"] = r.MpCost;
                    x["cooldown_ticks"] = r.CooldownTicks; x["duration_ticks"] = r.DurationTicks; x["tier"] = r.Tier;
                });
                Add(t, "runes", d.Runes, r => r.Id, (r, x) =>
                {
                    x["id"] = r.Id; x["rarity"] = r.Rarity; x["stat"] = r.Stat; x["stat_pct"] = r.StatPct; x["slot_mask"] = r.SlotMask; x["tier"] = r.Tier;
                });
                Add(t, "banners", d.Banners, r => r.Id, (r, x) =>
                {
                    x["id"] = r.Id; x["enabled"] = r.Enabled; x["tier"] = r.Tier; x["pull_cost_diamonds"] = r.PullCostDiamonds;
                    x["ticket_currency"] = r.TicketCurrency; Fold(x, PoolCols, r.PoolSize); x["pity_soft_start"] = r.PitySoftStart;
                    x["pity_soft_step"] = r.PitySoftStep; x["pity_hard"] = r.PityHard; x["m_share_of_lplus"] = r.MShareOfLplus;
                    x["guarantee_batch_size"] = r.GuaranteeBatchSize; x["guarantee_min_rarity"] = r.GuaranteeMinRarity;
                    x["exp_per_pull"] = r.ExpPerPull; x["sort_order"] = r.SortOrder;
                });
                Add(t, "banner_levels", d.BannerLevels, r => r.Banner + "," + I(r.Level), (r, x) =>
                {
                    x["banner"] = r.Banner; x["level"] = r.Level; x["exp_required"] = r.ExpRequired; Fold(x, RateCols, r.Rates);
                });
                Add(t, "dupe_levels", d.DupeLevels, r => I(r.Level), (r, x) => { x["level"] = r.Level; x["cum_copies"] = r.CumCopies; });
                Add(t, "awakening", d.Awakening, r => I(r.Rank), (r, x) =>
                {
                    x["rank"] = r.Rank; x["stage_gate"] = r.StageGate; x["rock_candy_cost"] = r.RockCandyCost; x["mult"] = r.Mult;
                    x["companion_slots_after"] = r.CompanionSlotsAfter; x["rune_slots_after"] = r.RuneSlotsAfter; x["tier"] = r.Tier;
                });
                Add(t, "hero_levels", d.HeroLevels, r => r.Id, (r, x) =>
                {
                    x["exp0"] = r.Exp0; x["exp_g"] = r.ExpG; x["atk_pct_per_level"] = r.AtkPctPerLevel; x["mult_per_level"] = r.MultPerLevel;
                    x["level_cap"] = r.LevelCap;
                });
                Add(t, "research", d.Research, r => r.Id, (r, x) =>
                {
                    x["id"] = r.Id; x["effect"] = r.Effect; x["effect_per_level"] = r.EffectPerLevel; x["effect_floor"] = r.EffectFloor;
                    x["max_level"] = r.MaxLevel; x["cost0"] = r.Cost0; x["cost_g"] = r.CostG; x["unlock_awaken_rank"] = r.UnlockAwakenRank;
                    x["layout_row"] = r.LayoutRow; x["layout_col"] = r.LayoutCol; x["tier"] = r.Tier;
                });
                Add(t, "fish_levels", d.FishLevels, r => I(r.Level), (r, x) =>
                {
                    x["level"] = r.Level; x["discovery_coins"] = r.DiscoveryCoins; x["missile_pct"] = r.MissilePct;
                });
                Add(t, "tank", d.Tank, r => r.Id, (r, x) => { x["cap0"] = r.Cap0; x["refill_s0"] = r.RefillS0; x["refill_floor_s"] = r.RefillFloorS; });
                Add(t, "tutorial_steps", d.TutorialSteps, r => r.Id, (r, x) =>
                {
                    x["order"] = r.Order; x["trigger"] = r.Trigger; x["trigger_value"] = r.TriggerValue; x["expect_event"] = r.ExpectEvent;
                    x["expect_count"] = r.ExpectCount;
                });
                Add(t, "progress_targets", d.ProgressTargets, r => I(r.Day), (r, x) => { x["day"] = r.Day; x["stage"] = r.Stage; });
                Add(t, "feature_unlocks", d.FeatureUnlocks, r => r.Feature, (r, x) =>
                {
                    x["feature"] = r.Feature; x["condition"] = r.Condition; x["threshold"] = r.Threshold; x["locked_text_key"] = r.LockedTextKey;
                });
                Add(t, "awakening_targets", d.AwakeningTargets, r => I(r.Rank), (r, x) => { x["rank"] = r.Rank; x["max_days_after_gate"] = r.MaxDaysAfterGate; });
                Add(t, "plausibility_stage_bound", d.PlausibilityStageBound, r => I(r.Day), (r, x) => { x["day"] = r.Day; x["stage"] = r.Stage; });
                return t;
            }

            // ------------------------------------------------------------------------------------------ the passes

            /// <summary>E020 for every table, in table order (the exporter finds these while reading, before any rule).</summary>
            void ValidateKeys(ContentDtos d)
            {
                Unique("constants", Rows(d.Constants), r => r.Key);
                Unique("curves", Rows(d.Curves), r => r.Id);
                Unique("stats", Rows(d.Stats), r => r.Id);
                Unique("training_curves", Rows(d.TrainingCurves), r => r.Stat);
                Unique("chapters", Rows(d.Chapters), r => r.Id);
                Unique("rarities", Rows(d.Rarities), r => r.Id);
                Unique("weapons", Rows(d.Weapons), r => r.Id);
                Unique("skills", Rows(d.Skills), r => r.Id);
                Unique("companions", Rows(d.Companions), r => r.Id);
                Unique("runes", Rows(d.Runes), r => r.Id);
                Unique("banners", Rows(d.Banners), r => r.Id);
                Unique("banner_levels", Rows(d.BannerLevels), r => r.Banner + "," + I(r.Level));
                Unique("dupe_levels", Rows(d.DupeLevels), r => I(r.Level));
                Unique("awakening", Rows(d.Awakening), r => I(r.Rank));
                Unique("hero_levels", Rows(d.HeroLevels), r => r.Id);
                Unique("research", Rows(d.Research), r => r.Id);
                Unique("fish_levels", Rows(d.FishLevels), r => I(r.Level));
                Unique("tank", Rows(d.Tank), r => r.Id);
                Unique("tutorial_steps", Rows(d.TutorialSteps), r => r.Id);
                Unique("progress_targets", Rows(d.ProgressTargets), r => I(r.Day));
                Unique("feature_unlocks", Rows(d.FeatureUnlocks), r => r.Feature);
                Unique("awakening_targets", Rows(d.AwakeningTargets), r => I(r.Rank));
                Unique("plausibility_stage_bound", Rows(d.PlausibilityStageBound), r => I(r.Day));
            }

            /// <summary>E043 for one constants row; called from BuildConstants right after the value parses (export_content.check_constant_range).</summary>
            void ConstantRange(string key, string declared, Constants.Value v, string loc)
            {
                object value = Boxed(declared, v);
                void Bad(string rule) => Err("E043", loc, $"value {Fmt(value)} out of range ({rule})");
                bool numeric = value is int || value is long || value is double;
                double x = numeric ? N(value) : 0;
                if (numeric && Array.IndexOf(ContentRules.Positive, key) >= 0 && !(x > 0)) Bad("> 0");
                if (numeric && Array.IndexOf(ContentRules.NonNegative, key) >= 0 && !(x >= 0)) Bad(">= 0");
                if (numeric && Array.IndexOf(ContentRules.UnitInterval, key) >= 0 && !(x >= 0 && x <= 1)) Bad("in [0, 1]");
                if (key == "CRIT_DMG_BASE" && numeric && !(x >= 1)) Bad(">= 1");
                if (key == "TRAIN_BULK_STEPS" && value is int[] steps)
                {
                    bool ok = steps.Length > 0 && steps[0] == 1;
                    for (int i = 1; i < steps.Length; i++) if (steps[i] <= steps[i - 1]) ok = false;
                    if (!ok) Bad("strictly increasing with first element 1");
                }
            }

            /// <summary>FISH_BOARD_W * FISH_BOARD_H fits the fixed board array (02); after E040, as the exporter does.</summary>
            void ConstantBoard(Dictionary<string, Constants.Value> values)
            {
                if (values.TryGetValue("FISH_BOARD_W", out var w) && values.TryGetValue("FISH_BOARD_H", out var h) && (long)w.Int * h.Int > BoardCellsMax)
                    Err("E043", "constants[FISH_BOARD_W]", $"value {I(w.Int)} out of range (FISH_BOARD_W * FISH_BOARD_H <= 64)");
            }

            static object Boxed(string type, Constants.Value v)
            {
                switch (type)
                {
                    case "int": return v.Int;
                    case "long": return v.Long;
                    case "double": return v.Double;
                    case "bool": return v.Bool;
                    case "intlist": return v.IntList;
                    default: return null;
                }
            }

            /// <summary>The refs and table rules (export_content.resolve_refs and RULES), errors appended unsorted.</summary>
            void ValidateRules(ContentDtos d)
            {
                _rows = RowsOf(d);
                _k = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var r in Rows(d.Constants))
                {
                    string req = Content.Constants.RequiredType(r.Key);
                    if (req != null && r.Type == req && TryValue(req, r.Value, out var v)) _k[r.Key] = Boxed(req, v);
                }
                ResolveRefs(d);
                RuleE044EnumTablesComplete();
                RuleE045OneRowTables();
                RuleE060IndexColumns();
                RuleE043TableRanges();
                RuleBannerLevels();
                RuleMonotonicColumns();
                RuleE034Counts();
                RuleE035OverrideTolerance();
                RuleE036RuneBanner();
                RuleE038FishLevelsCoverCap();
                RuleE046UniqueColumns();
                RuleBannerPityAndGuarantee();
                RuleE054ConditionalColumns();
                RuleE055TrainingCurves();
                RuleE056DerivedEqualities();
                RuleE058DurationWithinCooldown();
                RuleE059CurrencyAllowlists();
                RuleE061DupeLevelOne();
                RuleE062ProfileCreatedStep();
                RuleE063FeatureGates();
            }

            /// <summary>Stable-sorts the errors from <paramref name="from"/> on by table (09 acceptance 7).</summary>
            void SortByTable(int from)
            {
                var tail = Errors.GetRange(from, Errors.Count - from);
                Errors.RemoveRange(from, Errors.Count - from);
                var keyed = new List<KeyValuePair<int, ContentError>>(tail.Count);
                foreach (var e in tail) keyed.Add(new KeyValuePair<int, ContentError>(TableIndex(e.Location), e));
                // List.Sort is not stable: sort on (table, original position).
                var idx = new int[keyed.Count];
                for (int i = 0; i < idx.Length; i++) idx[i] = i;
                Array.Sort(idx, (a, b) => keyed[a].Key != keyed[b].Key ? keyed[a].Key.CompareTo(keyed[b].Key) : a.CompareTo(b));
                foreach (int i in idx) Errors.Add(keyed[i].Value);
            }

            static int TableIndex(string loc)
            {
                int end = loc.IndexOfAny(new[] { '[', '.', ' ', ':' });
                string table = end < 0 ? loc : loc.Substring(0, end);
                int i = Array.IndexOf(ContentRules.TableOrder, table);
                return i < 0 ? ContentRules.TableOrder.Length : i;
            }

            // ------------------------------------------------------------------------------------------- helpers

            static Dictionary<string, List<Row>> Groups(List<Row> rows, string col)
            {
                var g = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
                foreach (var r in rows)
                {
                    string k = Fmt(r[col]);
                    if (!g.TryGetValue(k, out var l)) g[k] = l = new List<Row>();
                    l.Add(r);
                }
                return g;   // Dictionary preserves insertion order when nothing is removed
            }

            static bool TierLe(Row r, Tier tier = Tier.MVP)
            {
                if (!r.TryGetValue("tier", out var t) || t == null) return true;
                return System.Enum.TryParse((string)t, false, out Tier v) && v <= tier;
            }

            void StrictlyIncreasing(string code, List<Row> rows, string col)
            {
                for (int i = 1; i < rows.Count; i++)
                {
                    object prev = rows[i - 1][col], cur = rows[i][col];
                    if (cur != null && prev != null && !(N(cur) > N(prev)))
                        Err(code, rows[i].Loc + "." + col,
                            $"{col} must be strictly increasing; {rows[i].Loc} has {Fmt(cur)} <= previous {Fmt(prev)}");
                }
            }

            void NonDecreasing(List<Row> rows, string col, string order, bool decreasing = false)
            {
                for (int i = 1; i < rows.Count; i++)
                {
                    object prev = rows[i - 1][col], cur = rows[i][col];
                    if (cur == null || prev == null) continue;
                    if (decreasing && N(cur) > N(prev))
                        Err("E048", rows[i].Loc + "." + col, $"{col} must be non-increasing in {order}; {rows[i].Loc} has {Fmt(cur)} > previous {Fmt(prev)}");
                    else if (!decreasing && N(cur) < N(prev))
                        Err("E048", rows[i].Loc + "." + col, $"{col} must be non-decreasing in {order}; {rows[i].Loc} has {Fmt(cur)} < previous {Fmt(prev)}");
                }
            }

            void ExactlyOneToN(string loc, string label, List<int> values, int? n = null)
            {
                int count = n ?? values.Count;
                bool ok = values.Count == count;
                for (int i = 0; ok && i < count; i++) ok = values[i] == i + 1;
                if (ok) return;
                var parts = new string[values.Count];
                for (int i = 0; i < parts.Length; i++) parts[i] = I(values[i]);
                Err("E060", loc, $"{label} must be exactly 1..{I(count)} in row order, got {string.Join(", ", parts)}");
            }

            static List<int> Ints(List<Row> rows, string col)
            {
                var l = new List<int>(rows.Count);
                foreach (var r in rows) l.Add((int)r[col]);
                return l;
            }

            void Rng(string table, string col, Func<double, bool> test, string rule, List<Row> rows = null)
            {
                foreach (var r in rows ?? T(table))
                {
                    var v = r[col];
                    if (v != null && !test(N(v))) Err("E043", r.Loc + "." + col, $"value {Fmt(v)} out of range ({rule})");
                }
            }

            void Cond(Row r, string col, bool mustSet, string condCol, object condVal)
            {
                bool isSet = r[col] != null;
                if (isSet != mustSet)
                    Err("E054", r.Loc + "." + col, $"{col} must be {(mustSet ? "set" : "empty")} when {condCol} = {Fmt(condVal)}");
            }

            int? KInt(string key) => _k.TryGetValue(key, out var v) && v is int i ? i : (int?)null;

            // -------------------------------------------------------------------------------------------- rules

            void ResolveRefs(ContentDtos d)
            {
                var stats = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in Rows(d.Stats)) stats.Add(s.Id);
                foreach (var r in Rows(d.TrainingCurves))
                    if (!stats.Contains(r.Stat)) Err("E022", $"training_curves[{r.Stat}].stat", $"'{r.Stat}' does not exist in stats");
                foreach (var r in Rows(d.Runes))
                    if (!stats.Contains(r.Stat)) Err("E022", $"runes[{r.Id}].stat", $"'{r.Stat}' does not exist in stats");
                var banners = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in Rows(d.Banners)) banners.Add(b.Id);
                foreach (var r in Rows(d.BannerLevels))
                    if (!banners.Contains(r.Banner)) Err("E022", $"banner_levels[{r.Banner},{I(r.Level)}].banner", $"'{r.Banner}' does not exist in banners");
                var items = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
                {
                    ["WEAPON"] = Ids(d.Weapons, w => w.Id), ["SKILL"] = Ids(d.Skills, s => s.Id),
                    ["COMPANION"] = Ids(d.Companions, c => c.Id), ["RUNE"] = Ids(d.Runes, r => r.Id),
                };
                foreach (var r in Rows(d.TutorialSteps))
                    foreach (var g in r.Grants ?? Array.Empty<GrantDto>())
                        if (g.Currency == null && g.Kind != null && items.TryGetValue(g.Kind, out var have) && !have.Contains(g.Id ?? ""))
                            Err("E022", $"tutorial_steps[{r.Id}].grants", $"'{g.Id}' does not exist in {g.Kind.ToLowerInvariant()}s");
            }

            static HashSet<string> Ids<TRow>(TRow[] rows, Func<TRow, string> id)
            {
                var s = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in Rows(rows)) s.Add(id(r));
                return s;
            }

            void RuleE044EnumTablesComplete()
            {
                void Complete<TEnum>(string table)
                {
                    var have = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var r in T(table)) have.Add(r.S("id"));
                    foreach (var v in System.Enum.GetNames(typeof(TEnum)))
                        if (!have.Contains(v)) Err("E044", table, $"missing row for {typeof(TEnum).Name} value '{v}'");
                }
                Complete<Stat>("stats");
                Complete<Rarity>("rarities");
                Complete<Banner>("banners");
                var curves = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in T("curves")) curves.Add(r.S("id"));
                foreach (var id in new[] { "enemy_hp", "gold_per_kill", "exp_per_kill" })
                    if (Has("curves") && !curves.Contains(id)) Err("E044", "curves", $"missing row for curve '{id}'");
            }

            void RuleE045OneRowTables()
            {
                foreach (var table in new[] { "hero_levels", "tank" })
                    if (Has(table) && T(table).Count != 1) Err("E045", table, $"exactly one row required, found {I(T(table).Count)}");
            }

            void RuleE060IndexColumns()
            {
                void EnumOrder<TEnum>(string table)
                {
                    var names = System.Enum.GetNames(typeof(TEnum));
                    var ids = new List<string>();
                    foreach (var r in T(table)) ids.Add(r.S("id"));
                    var sorted = new List<string>(ids);
                    sorted.Sort(StringComparer.Ordinal);
                    var want = new List<string>(names);
                    want.Sort(StringComparer.Ordinal);
                    if (string.Join(",", sorted) == string.Join(",", want) && string.Join(",", ids) != string.Join(",", names))
                        Err("E060", table, $"id must be exactly {names[0]}..{names[names.Length - 1]} in enum order, got {string.Join(", ", ids)}");
                }
                EnumOrder<Stat>("stats");
                EnumOrder<Rarity>("rarities");
                EnumOrder<Banner>("banners");
                foreach (var tc in new[] { ("chapters", "index"), ("dupe_levels", "level"), ("awakening", "rank"), ("fish_levels", "level"),
                                           ("tutorial_steps", "order"), ("plausibility_stage_bound", "day") })
                    if (Has(tc.Item1)) ExactlyOneToN(tc.Item1, tc.Item2, Ints(T(tc.Item1), tc.Item2));
                foreach (var g in Groups(T("banner_levels"), "banner"))
                    ExactlyOneToN("banner_levels", $"level for banner {g.Key}", Ints(g.Value, "level"));
                if (Has("awakening_targets"))
                    ExactlyOneToN("awakening_targets", "rank", Ints(T("awakening_targets"), "rank"), Has("awakening") ? T("awakening").Count : (int?)null);
            }

            void RuleE043TableRanges()
            {
                Func<double, bool> pos = v => v > 0, ge0 = v => v >= 0, ge1 = v => v >= 1, gt1 = v => v > 1, unit = v => v >= 0 && v <= 1;
                Rng("curves", "base", pos, "> 0");
                Rng("curves", "growth", gt1, "> 1");
                Rng("stats", "per_lvl", pos, "> 0");
                Rng("stats", "cap", ge1, ">= 1");
                Rng("training_curves", "cost0", pos, "> 0");
                Rng("training_curves", "cost_g", gt1, "> 1");
                Rng("rarities", "limit_break_cost", ge1, ">= 1");
                Rng("rarities", "dupe_overflow_rock_candy", ge0, ">= 0");
                Rng("skills", "cooldown_ticks", v => v >= SkillCooldownMin && v <= SkillCooldownMax, "in [30, 300]");
                Rng("skills", "buff_pct", pos, "> 0");
                Rng("companions", "mp_cost", ge1, ">= 1");
                Rng("companions", "cooldown_ticks", v => v >= CompanionCooldownMin && v <= CompanionCooldownMax, "in [20, 600]");
                Rng("runes", "slot_mask", pos, "> 0");
                Rng("banners", "pull_cost_diamonds", pos, "> 0");
                Rng("banners", "pity_soft_start", ge1, ">= 1");
                Rng("banners", "pity_soft_step", pos, "> 0");
                Rng("banners", "m_share_of_lplus", unit, "in [0, 1]");
                Rng("banners", "guarantee_batch_size", v => v >= 2, ">= 2");
                Rng("banners", "exp_per_pull", ge0, ">= 0");
                foreach (var col in RateCols) Rng("banner_levels", col, unit, "in [0, 1]");
                var level1 = T("banner_levels").FindAll(r => (int)r["level"] == 1);
                Rng("banner_levels", "exp_required", v => v == 0, "level 1 must be 0", level1);
                Rng("awakening", "mult", gt1, "> 1");
                int? cs = KInt("COMPANION_SLOTS"), rs = KInt("RUNE_SLOTS");
                Rng("awakening", "companion_slots_after", v => cs == null || v <= cs.Value, "<= COMPANION_SLOTS");
                Rng("awakening", "rune_slots_after", v => rs == null || v <= rs.Value, "<= RUNE_SLOTS");
                Rng("hero_levels", "exp0", pos, "> 0");
                Rng("hero_levels", "exp_g", gt1, "> 1");
                Rng("hero_levels", "level_cap", ge1, ">= 1");
                Rng("research", "effect_per_level", pos, "> 0");
                Rng("research", "max_level", ge1, ">= 1");
                Rng("research", "cost0", pos, "> 0");
                Rng("research", "cost_g", gt1, "> 1");
                Rng("research", "unlock_awaken_rank", ge0, ">= 0");
                Rng("tank", "cap0", ge1, ">= 1");
                Rng("tank", "refill_s0", pos, "> 0");
                int? hz = KInt("TICK_HZ");
                if (hz != null)
                    Rng("tank", "refill_s0", v => v * hz.Value == Math.Round(v * hz.Value, MidpointRounding.ToEven),
                        "refill_s0 * TICK_HZ must be a whole number of ticks");
                Rng("tutorial_steps", "expect_count", ge1, ">= 1");
                Rng("progress_targets", "stage", ge1, ">= 1");
                Rng("feature_unlocks", "threshold", ge1, ">= 1");
                Rng("awakening_targets", "max_days_after_gate", ge0, ">= 0");
                Rng("plausibility_stage_bound", "stage", ge1, ">= 1");
            }

            void RuleBannerLevels()
            {
                foreach (var g in Groups(T("banner_levels"), "banner"))
                {
                    var rows = g.Value;
                    foreach (var r in rows)
                    {
                        double s = 0.0;
                        foreach (var col in RateCols) s += N(r[col]);
                        if (Math.Abs(s - 1.0) > RateSumTolerance)
                            Err("E030", r.Loc + ".rates", $"rates sum to {PyRepr(s)} (abs error {PyRepr(Math.Abs(s - 1.0))}), expected 1 within 1e-9");
                    }
                    var b = rows[0];
                    for (int i = 1; i < rows.Count; i++)
                        foreach (var col in new[] { "rate_l", "rate_m" })
                            if (Math.Abs(N(rows[i][col]) - N(b[col])) > RateEqualTolerance)
                                Err("E037", rows[i].Loc + "." + col, $"{col} must not change with level; level {Fmt(rows[i]["level"])} has " +
                                    $"{Fmt(rows[i][col])}, level {Fmt(b["level"])} has {Fmt(b[col])}");
                    StrictlyIncreasing("E031", rows, "exp_required");
                    NonDecreasing(rows, "rate_c", "level", decreasing: true);
                    NonDecreasing(rows, "rate_r", "level");
                    NonDecreasing(rows, "rate_e", "level");
                }
            }

            void RuleMonotonicColumns()
            {
                foreach (var col in new[] { "weapon_atk_pct", "weapon_passive_pct", "skill_dps_pct", "companion_dps_pct", "rune_stat_pct",
                                            "limit_break_cost", "dupe_overflow_rock_candy" })
                    NonDecreasing(T("rarities"), col, "rarity");
                StrictlyIncreasing("E031", T("dupe_levels"), "cum_copies");
                StrictlyIncreasing("E032", T("awakening"), "stage_gate");
                StrictlyIncreasing("E032", T("awakening"), "rock_candy_cost");
                NonDecreasing(T("awakening"), "companion_slots_after", "rank");
                NonDecreasing(T("awakening"), "rune_slots_after", "rank");
                StrictlyIncreasing("E031", T("fish_levels"), "discovery_coins");
                StrictlyIncreasing("E031", T("fish_levels"), "missile_pct");
                StrictlyIncreasing("E032", T("progress_targets"), "day");
                NonDecreasing(T("progress_targets"), "stage", "day");
                NonDecreasing(T("plausibility_stage_bound"), "stage", "day");
            }

            static readonly Dictionary<string, string> PoolTables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WEAPON"] = "weapons", ["SKILL"] = "skills", ["COMPANION"] = "companions", ["RUNE"] = "runes",
            };

            void RuleE034Counts()
            {
                var rarities = System.Enum.GetNames(typeof(Rarity));
                foreach (var b in T("banners"))
                {
                    if (!PoolTables.TryGetValue(b.S("id"), out var table)) continue;
                    // A banner whose pool table is not authored yet is skipped (TODO(#161): companions), as in the exporter.
                    if (!(bool)b["enabled"] || !TierLe(b) || !Has(table)) continue;
                    for (int i = 0; i < rarities.Length; i++)
                    {
                        int n = 0;
                        foreach (var r in T(table)) if (TierLe(r) && r.S("rarity") == rarities[i]) n++;
                        var want = b[PoolCols[i]];
                        if (want == null || n != (int)want)
                            Err("E034", table, $"{table} has {I(n)} rows of {rarities[i]}, expected {Fmt(want)} from {b.Loc}.{PoolCols[i]}");
                    }
                }
                if (Has("awakening_targets") && Has("awakening") && T("awakening_targets").Count != T("awakening").Count)
                    Err("E034", "awakening_targets", $"awakening_targets has {I(T("awakening_targets").Count)} rows of rank, expected {I(T("awakening").Count)} from awakening");
            }

            void RuleE035OverrideTolerance()
            {
                var rar = new Dictionary<string, Row>(StringComparer.Ordinal);
                foreach (var r in T("rarities")) rar[r.S("id")] = r;
                foreach (var (table, col, defaultCol) in new[]
                         {
                             ("weapons", "atk_pct", "weapon_atk_pct"), ("weapons", "passive_pct", "weapon_passive_pct"),
                             ("skills", "dps_pct", "skill_dps_pct"), ("companions", "dps_pct", "companion_dps_pct"), ("runes", "stat_pct", "rune_stat_pct"),
                         })
                    foreach (var r in T(table))
                    {
                        if (!rar.TryGetValue(r.S("rarity") ?? "", out var rd) || !(N(rd[defaultCol]) > 0)) continue;
                        double d = N(rd[defaultCol]), tol = N(rd["override_tolerance"]), v = N(r[col]);
                        double dev = Math.Abs(v - d) / d;
                        if (dev > tol + OverrideEpsilon)
                            Err("E035", r.Loc + "." + col, $"{col} {PyRepr(v)} deviates {PyRepr(dev * Percent)}% from rarities default {PyRepr(d)} (tolerance {PyRepr(tol * Percent)}%)");
                    }
            }

            void RuleE036RuneBanner()
            {
                foreach (var b in T("banners"))
                    if (b.S("id") == "RUNE" && (bool)b["enabled"] && T("runes").Count == 0)
                        Err("E036", b.Loc + ".enabled", "banner RUNE is enabled but runes has no rows");
            }

            int? FishCapLevels()
            {
                int extra = 0;
                foreach (var r in T("research"))
                    if (r.S("effect") == "FISH_CAP") { extra = (int)Math.Ceiling(N(r["effect_per_level"]) * N(r["max_level"])); break; }
                int? lvl0 = KInt("FISH_MAX_LVL0");
                return lvl0 == null ? (int?)null : lvl0.Value + extra;
            }

            void RuleE038FishLevelsCoverCap()
            {
                int? needed = FishCapLevels();
                int n = T("fish_levels").Count;
                if (Has("fish_levels") && needed != null && n < needed.Value)
                    Err("E038", "fish_levels", $"fish_levels has {I(n)} rows but FISH_MAX_LVL0 + research cap needs {I(needed.Value)}");
            }

            void UniqueCols(List<Row> rows, params string[] cols)
            {
                var seen = new Dictionary<string, string>(StringComparer.Ordinal);
                string label = string.Join(",", cols);
                foreach (var r in rows)
                {
                    var parts = new string[cols.Length];
                    bool hasNull = false;
                    for (int i = 0; i < cols.Length; i++) { hasNull |= r[cols[i]] == null; parts[i] = Fmt(r[cols[i]]); }
                    if (hasNull) continue;
                    string v = string.Join(",", parts);
                    if (seen.TryGetValue(v, out var first)) Err("E046", r.Loc + "." + cols[0], $"duplicate {label} {v} (also at {first})");
                    else seen[v] = r.Loc;
                }
            }

            void RuleE046UniqueColumns()
            {
                UniqueCols(T("banners"), "sort_order");
                UniqueCols(T("research"), "effect");
                UniqueCols(T("research"), "layout_row", "layout_col");
            }

            void RuleBannerPityAndGuarantee()
            {
                var level1 = new Dictionary<string, Row>(StringComparer.Ordinal);
                foreach (var r in T("banner_levels")) if ((int)r["level"] == 1 && !level1.ContainsKey(r.S("banner"))) level1[r.S("banner")] = r;
                var rarities = System.Enum.GetNames(typeof(Rarity));
                foreach (var b in T("banners"))
                {
                    if (!((int)b["pity_hard"] > (int)b["pity_soft_start"]))
                        Err("E051", b.Loc + ".pity_hard", $"pity_hard ({Fmt(b["pity_hard"])}) must be greater than pity_soft_start ({Fmt(b["pity_soft_start"])})");
                    if (level1.TryGetValue(b.S("id"), out var lv))
                    {
                        int i = Array.IndexOf(rarities, b.S("guarantee_min_rarity"));
                        if (i < 0) continue;
                        double s = 0.0;
                        for (int j = i; j < RateCols.Length; j++) s += N(lv[RateCols[j]]);
                        if (!(s > 0))
                            Err("E052", b.Loc + ".guarantee_min_rarity", $"guarantee_min_rarity '{b.S("guarantee_min_rarity")}' has zero combined level-1 rate");
                    }
                }
            }

            void RuleE054ConditionalColumns()
            {
                foreach (var r in T("stats")) Cond(r, "per_lvl", (bool)r["trainable"], "trainable", r["trainable"]);
                foreach (var r in T("skills"))
                {
                    Cond(r, "duration_ticks", r.S("cast_type") != "INSTANT", "cast_type", r["cast_type"]);
                    Cond(r, "buff_pct", r.S("cast_type") == "BUFF", "cast_type", r["cast_type"]);
                }
                foreach (var r in T("companions"))
                    Cond(r, "duration_ticks", (r.S("ability_type") ?? "").StartsWith("BUFF_", StringComparison.Ordinal), "ability_type", r["ability_type"]);
                foreach (var r in T("hero_levels"))
                    if ((r["atk_pct_per_level"] == null) == (r["mult_per_level"] == null))
                        Cond(r, "mult_per_level", r["atk_pct_per_level"] == null, "atk_pct_per_level", r["atk_pct_per_level"]);
                foreach (var r in T("research"))
                    if (r.S("effect") == "TANK_REFILL") Cond(r, "effect_floor", true, "effect", r["effect"]);
                foreach (var r in T("tutorial_steps"))
                {
                    Cond(r, "trigger_value", r.S("trigger") == "STAGE_REACHED" || r.S("trigger") == "SCREEN_OPENED", "trigger", r["trigger"]);
                    Cond(r, "expect_count", r["expect_event"] != null, "expect_event", r["expect_event"]);
                }
                foreach (var r in T("feature_unlocks"))
                {
                    Cond(r, "threshold", r.S("condition") != "ALWAYS", "condition", r["condition"]);
                    Cond(r, "locked_text_key", r.S("condition") != "ALWAYS", "condition", r["condition"]);
                }
            }

            void RuleE055TrainingCurves()
            {
                if (!Has("training_curves") || !Has("stats")) return;
                var want = new List<string>();
                foreach (var r in T("stats")) if ((bool)r["trainable"]) want.Add(r.S("id"));
                var have = new List<string>();
                foreach (var r in T("training_curves")) have.Add(r.S("stat"));
                var missing = want.FindAll(s => !have.Contains(s));
                var extra = have.FindAll(s => !want.Contains(s));
                foreach (var s in extra) if (!System.Enum.IsDefined(typeof(Stat), s)) return;   // an unresolved stat is E022's
                if (missing.Count > 0 || extra.Count > 0)
                    Err("E055", "training_curves", "rows must be exactly the trainable stats; missing " +
                        (missing.Count > 0 ? string.Join(", ", missing) : "none") + ", extra " + (extra.Count > 0 ? string.Join(", ", extra) : "none"));
            }

            void RuleE056DerivedEqualities()
            {
                int? w = KInt("CHAPTER_WIDTH");
                if (w != null)
                    foreach (var r in T("chapters"))
                    {
                        int want = ((int)r["index"] - 1) * w.Value + 1;
                        if ((int)r["from_stage"] != want)
                            Err("E056", r.Loc + ".from_stage", $"from_stage must equal {I(want)} ((index - 1) * CHAPTER_WIDTH + 1), got {Fmt(r["from_stage"])}");
                    }
                Row node = T("research").Find(r => r.S("effect") == "TANK_REFILL");
                if (node != null && node["effect_floor"] != null)
                    foreach (var r in T("tank"))
                        if (N(r["refill_floor_s"]) != N(node["effect_floor"]))
                            Err("E056", r.Loc + ".refill_floor_s", $"refill_floor_s must equal {Fmt(node["effect_floor"])} ({node.Loc}.effect_floor), got {Fmt(r["refill_floor_s"])}");
            }

            void RuleE058DurationWithinCooldown()
            {
                foreach (var table in new[] { "skills", "companions" })
                    foreach (var r in T(table))
                    {
                        if (r["duration_ticks"] == null) continue;
                        int d = (int)r["duration_ticks"], c = (int)r["cooldown_ticks"];
                        if (!(d >= 1 && d <= c)) Err("E058", r.Loc + ".duration_ticks", $"duration_ticks {I(d)} must be in 1..cooldown_ticks {I(c)}");
                    }
            }

            void RuleE059CurrencyAllowlists()
            {
                foreach (var b in T("banners"))
                {
                    string want = "TICKET_SUMMON_" + b.S("id");
                    if (b.S("ticket_currency") != want)
                        Err("E059", b.Loc + ".ticket_currency", $"ticket_currency '{b.S("ticket_currency")}' is not an allowed currency here ({want} for banner {b.S("id")})");
                }
            }

            void RuleE061DupeLevelOne()
            {
                var rows = T("dupe_levels");
                if (rows.Count > 0 && (int)rows[0]["level"] == 1 && (int)rows[0]["cum_copies"] != 1)
                    Err("E061", rows[0].Loc + ".cum_copies", $"cum_copies for level 1 must be 1, got {Fmt(rows[0]["cum_copies"])}");
            }

            void RuleE062ProfileCreatedStep()
            {
                if (!Has("tutorial_steps")) return;
                var pc = T("tutorial_steps").FindAll(r => r.S("trigger") == "PROFILE_CREATED");
                if (pc.Count != 1 || (int)pc[0]["order"] != 1) Err("E062", "tutorial_steps", "exactly one PROFILE_CREATED step at order 1 required");
            }

            void RuleE063FeatureGates()
            {
                Row gate = T("feature_unlocks").Find(r => r.S("feature") == "RESEARCH");
                if (gate != null && gate.S("condition") == "AWAKEN_RANK" && gate["threshold"] != null)
                    foreach (var r in T("research"))
                        if ((int)r["unlock_awaken_rank"] < (int)gate["threshold"])
                            Err("E063", r.Loc + ".unlock_awaken_rank", $"research[{r.S("id")}] unlocks at {Fmt(r["unlock_awaken_rank"])}, before feature 'RESEARCH' ({Fmt(gate["threshold"])})");
                Row shop = T("feature_unlocks").Find(r => r.S("feature") == "SHOP");
                if (shop != null && shop.S("condition") != "ALWAYS") Err("E063", shop.Loc + ".condition", "feature 'SHOP' must be ALWAYS");
            }
        }
    }
}
