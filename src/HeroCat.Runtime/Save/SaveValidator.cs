using System;
using System.Collections.Generic;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Save
{
    /// <summary>Inputs to <see cref="SaveValidator.Run"/> beyond the state, the time and the content.</summary>
    public sealed class SaveValidatorOptions
    {
        /// <summary>This install's id: P20 sets an empty <c>DeviceId</c> to it. Guid.Empty leaves an empty DeviceId alone.</summary>
        public Guid InstallId { get; set; }

        /// <summary>
        /// Remote mode (MVP): the summon stream counters are not checked (02 section 3.4; the server owns the pulls).
        /// Local mode (M0, bots): <c>summon_x.Counter == 3 * Banners[x].TotalPulls</c> (P15).
        /// </summary>
        public bool RemoteMode { get; set; }
    }

    /// <summary>What <see cref="SaveValidator.Run"/> did besides the repairs (logged, not repair hits).</summary>
    public sealed class SaveValidationReport
    {
        /// <summary>Distinct rule ids that fired, in rule order ("P1", "P9b", ...). Each is one <c>save_repaired{rule}</c>.</summary>
        public List<string> Repairs { get; } = new List<string>();

        /// <summary>Informational lines, e.g. an unknown Rng stream dropped (02 section 6.3).</summary>
        public List<string> Log { get; } = new List<string>();

        /// <summary>P16 found a dungeon run in the save and abandoned it. TODO(#172): the caller emits DungeonAbandoned.</summary>
        public bool DungeonAbandoned { get; internal set; }
    }

    /// <summary>
    /// 02 section 6.4: the reject rules R2-R5 and the repair rules P1-P21 (with P9b), run on a materialized, migrated
    /// GameState against the loaded ContentDb and <c>now</c>. This is the only content reconcile on load (issue #56):
    /// the content-dependent rules fix what a content change left behind, and nothing is refunded. Every
    /// content-dependent limit is read from ContentDb (00 contract 3). Reads no clock other than <c>now</c>.
    ///
    /// Rejects throw <see cref="SaveInvalidException"/>. A repair fixes the field and records its rule id once; more than
    /// <see cref="SaveConfig.SAVE_REPAIR_MAX"/> distinct rule hits is a reject. Left alone on purpose:
    /// <c>Time.DayKey &gt; dayKey(now)</c>, <c>LastSavedServerTime &gt; now</c> and <c>CreatedServerTime &gt; now</c>
    /// (backwards clock, 02 section 11).
    /// </summary>
    public static class SaveValidator
    {
        /// <summary>Rule ids in the order Run evaluates them (02 section 6.4).</summary>
        public static readonly string[] RepairRules =
        {
            "P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P9", "P9b", "P10", "P11", "P12", "P13", "P14", "P15", "P16",
            "P17", "P18", "P19", "P20", "P21",
        };

        /// <summary>Rejects, then repairs. Returns the distinct repair rule ids that fired. Mutates <paramref name="s"/>.</summary>
        public static IReadOnlyList<string> Run(GameState s, long now, ContentDb db, SaveValidatorOptions options = null) =>
            RunWithReport(s, now, db, options).Repairs;

        public static SaveValidationReport RunWithReport(GameState s, long now, ContentDb db, SaveValidatorOptions options = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            options ??= new SaveValidatorOptions();
            Reject(s, db);
            var ctx = new Ctx(s, now, db, options);
            try { ctx.Run(); }
            catch (Exception e) when (!(e is SaveInvalidException))
            {
                // A shape the rules did not expect (a null nested collection, say): quarantine, never crash the load.
                throw new SaveInvalidException($"validator failed: {e.GetType().Name}: {e.Message}", e);
            }
            if (ctx.Report.Repairs.Count > SaveConfig.SAVE_REPAIR_MAX)
                throw new SaveInvalidException(
                    $"{ctx.Report.Repairs.Count} repair rules fired (> SAVE_REPAIR_MAX {SaveConfig.SAVE_REPAIR_MAX}): " +
                    string.Join(",", ctx.Report.Repairs));
            return ctx.Report;
        }

        // ------------------------------------------------------------------------------------------------ rejects

        /// <summary>R2-R5 (R1 is Decode's). Throws <see cref="SaveInvalidException"/>; changes nothing.</summary>
        public static void Reject(GameState s, ContentDb db)
        {
            if (s == null) throw new SaveInvalidException("state is null");
            if (s.SchemaVersion < 1) throw new SaveInvalidException("R2: SchemaVersion missing or < 1");
            if (s.Time == null || s.Hero == null || s.Training == null || s.Wallet == null || s.Weapons == null || s.Skills == null ||
                s.Companions == null || s.Runes == null || s.Loadout == null || s.Fish == null || s.Banners == null || s.Combat == null ||
                s.Progress == null || s.Quests == null || s.Dungeons == null || s.Shop == null || s.Research == null || s.Inbox == null ||
                s.Offline == null || s.Pending == null || s.Tutorial == null || s.Rng == null || s.Stats == null)
                throw new SaveInvalidException("R3: a root section is null");
            if (s.RngRoot == Guid.Empty) throw new SaveInvalidException("R4: RngRoot is empty");
            foreach (var name in RngStreams.All)
            {
                if (!s.Rng.TryGetValue(name, out var st) || st == null) throw new SaveInvalidException($"R4: rng stream '{name}' missing");
                if (!RngStreams.TryParseSeed(st.Seed, out _)) throw new SaveInvalidException($"R4: rng stream '{name}' seed is not 16 hex chars");
                if (st.Counter < 0) throw new SaveInvalidException($"R4: rng stream '{name}' counter is negative");
            }
            var k = db.Constants;
            if (s.Training.Level == null || s.Training.Level.Length != 8) throw new SaveInvalidException("R5: Training.Level length != 8");
            if (s.Fish.Cells == null || s.Fish.Cells.Length != k.FishBoardW * k.FishBoardH)
                throw new SaveInvalidException("R5: Fish.Cells length != FISH_BOARD_W * FISH_BOARD_H");
            if (s.Wallet.DungeonTicket == null || s.Wallet.DungeonTicket.Length != 3) throw new SaveInvalidException("R5: Wallet.DungeonTicket length != 3");
            if (s.Wallet.SummonTicket == null || s.Wallet.SummonTicket.Length != 4) throw new SaveInvalidException("R5: Wallet.SummonTicket length != 4");
        }

        // ------------------------------------------------------------------------------------------------ repairs

        sealed class Ctx
        {
            readonly GameState s;
            readonly long now;
            readonly ContentDb db;
            readonly Constants k;
            readonly SaveValidatorOptions opt;
            public readonly SaveValidationReport Report = new SaveValidationReport();

            public Ctx(GameState s, long now, ContentDb db, SaveValidatorOptions opt)
            {
                this.s = s; this.now = now; this.db = db; this.opt = opt; k = db.Constants;
            }

            void Hit(string rule)
            {
                if (!Report.Repairs.Contains(rule)) Report.Repairs.Add(rule);
            }

            public void Run()
            {
                // Stage first: P5 and P6 are relative to it, P1's Selected check is relative to P5's result.
                P1Stage();
                P5(); P6();
                P1Rest();
                P2(); P3(); P4(); P7();
                P10(); P14();      // collections before the loadout that points into them
                P8();
                P9(); P9b(); P11(); P12(); P13(); P15(); P16(); P17(); P18(); P19(); P20(); P21();
                DropUnknownStreams();
                Report.Repairs.Sort((a, b) => Array.IndexOf(RepairRules, a).CompareTo(Array.IndexOf(RepairRules, b)));
            }

            // P1: the stage loop.
            void P1Stage()
            {
                var c = s.Combat;
                if (c.Stage < 1) { c.Stage = 1; Hit("P1"); }
                if (c.Stage > k.StageCap) { c.Stage = k.StageCap; Hit("P1"); }
                if (c.KillsInStage < 0) { c.KillsInStage = 0; Hit("P1"); }
                if (c.KillsInStage > k.KillsPerStage - 1) { c.KillsInStage = k.KillsPerStage - 1; Hit("P1"); }
                if (c.Boss != BossPhase.None && !k.IsBossStage(c.Stage)) { c.Boss = BossPhase.None; Hit("P1"); }
                if (c.BossFailsOnStage < 0) { c.BossFailsOnStage = 0; Hit("P1"); }
            }

            void P1Rest()
            {
                var c = s.Combat;
                if (c.Selected is int sel && (sel < 1 || sel > s.Progress.HighestCleared)) { c.Selected = null; Hit("P1"); }
                if (ClampNonNegative(c.SkillCooldowns)) Hit("P1");
                if (ClampNonNegative(c.CompanionCooldowns)) Hit("P1");
                if (c.MissileQueue.RemoveAll(m => !IsFiniteNonNegative(m)) > 0) Hit("P1");
                // Mp is a double like the wallet's: a NaN would poison every tick (decision in the #75 change; 02 P1).
                if (!IsFiniteNonNegative(c.Mp)) { c.Mp = 0; Hit("P1"); }
            }

            // P2: wallet balances and Hero.Exp.
            void P2()
            {
                var w = s.Wallet;
                if (!IsFiniteNonNegative(w.Gold)) { w.Gold = 0; Hit("P2"); }
                if (!IsFiniteNonNegative(w.FishCoin)) { w.FishCoin = 0; Hit("P2"); }
                if (w.Diamond < 0) { w.Diamond = 0; Hit("P2"); }
                if (w.RockCandy < 0) { w.RockCandy = 0; Hit("P2"); }
                if (w.GuildCoin < 0) { w.GuildCoin = 0; Hit("P2"); }
                if (ClampNonNegative(w.DungeonTicket)) Hit("P2");
                if (ClampNonNegative(w.SummonTicket)) Hit("P2");
                if (!IsFiniteNonNegative(s.Hero.Exp)) { s.Hero.Exp = 0; Hit("P2"); }
            }

            // P3: hero level and awakening rank (AWAKEN_STAGES.Length = the awakening row count; 0 in an M0 build).
            void P3()
            {
                var h = s.Hero;
                if (h.Level < 1) { h.Level = 1; Hit("P3"); }
                int maxRank = db.Awakening?.Count ?? 0;
                if (h.AwakenRank < 0) { h.AwakenRank = 0; Hit("P3"); }
                if (h.AwakenRank > maxRank) { h.AwakenRank = maxRank; Hit("P3"); }
            }

            // P4: training levels within [0, TRAIN_CAP[stat]]; a null cap is uncapped.
            void P4()
            {
                var lv = s.Training.Level;
                for (int i = 0; i < lv.Length; i++)
                {
                    if (lv[i] < 0) { lv[i] = 0; Hit("P4"); }
                    int? cap = i < db.Stats.Count ? db.Stats[i].Cap : null;
                    if (cap is int c && lv[i] > c) { lv[i] = c; Hit("P4"); }
                }
            }

            // P5: HighestCleared within [Stage - 1, Stage].
            void P5()
            {
                var p = s.Progress;
                int stage = s.Combat.Stage;
                if (p.HighestCleared < stage - 1 || p.HighestCleared > stage) { p.HighestCleared = stage - 1; Hit("P5"); }
            }

            // P6: HighestFirstClearBoss a non-negative multiple of BOSS_EVERY, <= HighestCleared.
            void P6()
            {
                var p = s.Progress;
                int every = k.BossEvery;
                int v = p.HighestFirstClearBoss;
                if (v < 0) v = 0;
                if (v > p.HighestCleared) v = p.HighestCleared;
                v -= v % every;
                if (v != p.HighestFirstClearBoss) { p.HighestFirstClearBoss = v; Hit("P6"); }
            }

            // P7: loadout slot arrays sized to the slot counts. The per-slot cooldown arrays follow their slots (an index
            // guard for the tick loop; recorded under P7, decision in the #75 change).
            void P7()
            {
                var l = s.Loadout;
                var c = s.Combat;
                if (Resize(l.Skills, k.SkillSlots, out var a)) { l.Skills = a; Hit("P7"); }
                if (Resize(l.Companions, k.CompanionSlots, out var b)) { l.Companions = b; Hit("P7"); }
                if (Resize(l.Runes, k.RuneSlots, out var r)) { l.Runes = r; Hit("P7"); }
                if (Resize(c.SkillCooldowns, k.SkillSlots, out var sc)) { c.SkillCooldowns = sc; Hit("P7"); }
                if (Resize(c.CompanionCooldowns, k.CompanionSlots, out var cc)) { c.CompanionCooldowns = cc; Hit("P7"); }
            }

            // P8: equipped ids owned, known, of the right kind, not duplicated; a null weapon auto-equips.
            void P8()
            {
                var l = s.Loadout;
                if (l.Weapon != null && (!db.Weapons.ContainsKey(l.Weapon) || !s.Weapons.Items.ContainsKey(l.Weapon)))
                {
                    l.Weapon = null;
                    Hit("P8");
                }
                if (l.Weapon == null && s.Weapons.Items.Count > 0)
                {
                    l.Weapon = BestOwnedWeapon();
                    Hit("P8");
                }
                if (CleanSlots(l.Skills, s.Skills, db.Skills)) Hit("P8");
                // TODO(#161): companion content (unknown or wrong-kind ids); ownership and duplicates are checked now.
                if (CleanSlots<object>(l.Companions, s.Companions, null)) Hit("P8");
                // TODO(#227): rune content (P1).
                if (CleanSlots<object>(l.Runes, s.Runes, null)) Hit("P8");
            }

            /// <summary>
            /// 04's AutoEquipBestWeapon choice: highest WEAPON_ATK_PCT * LevelMult, ties by rarity then id ordinal.
            /// TODO(#93): call the sim's AutoEquipBestWeapon once it lands, so there is one implementation.
            /// </summary>
            string BestOwnedWeapon()
            {
                string best = null;
                double bestScore = double.NegativeInfinity;
                Rarity bestRarity = Rarity.COMMON;
                foreach (var kv in s.Weapons.Items)
                {
                    if (!db.Weapons.TryGetValue(kv.Key, out var w)) continue;
                    double score = w.AtkPct * db.LevelMult(db.DupeLevel(kv.Value.Copies));
                    bool better = best == null || score > bestScore ||
                                  (score == bestScore && (w.Rarity > bestRarity ||
                                                          (w.Rarity == bestRarity && string.CompareOrdinal(kv.Key, best) < 0)));
                    if (better) { best = kv.Key; bestScore = score; bestRarity = w.Rarity; }
                }
                return best;
            }

            static bool CleanSlots<TDef>(string[] slots, CollectionState owned, IReadOnlyDictionary<string, TDef> content)
            {
                bool changed = false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < slots.Length; i++)
                {
                    var id = slots[i];
                    if (id == null) continue;
                    bool ok = owned.Items.ContainsKey(id) && (content == null || content.ContainsKey(id)) && seen.Add(id);
                    if (!ok) { slots[i] = null; changed = true; }
                }
                return changed;
            }

            // P9: board cells within [0, FISH_MAX_LVL_HARD] (never the reachable cap: a fish above a lowered cap stays,
            // issue #41); DiscoveredLevels covers the board; HighestLevel recomputed.
            void P9()
            {
                var f = s.Fish;
                int hard = db.FishLevels.Count;   // FISH_MAX_LVL_HARD (05 section 2.2)
                for (int i = 0; i < f.Cells.Length; i++)
                {
                    if (f.Cells[i] < 0) { f.Cells[i] = 0; Hit("P9"); }
                    if (f.Cells[i] > hard) { f.Cells[i] = hard; Hit("P9"); }
                    if (f.Cells[i] > 0 && f.DiscoveredLevels.Add(f.Cells[i])) Hit("P9");
                }
                int highest = 0;
                foreach (var lv in f.DiscoveredLevels) if (lv > highest) highest = lv;
                if (f.HighestLevel != highest) { f.HighestLevel = highest; Hit("P9"); }
            }

            // P9b: the Magic Tank.
            void P9b()
            {
                var f = s.Fish;
                int cap = db.Tank.Cap0;                 // TODO(#167): + the TANK_CAP Research effect
                int refillTicks = db.Tank.RefillTicks0; // TODO(#167): the TANK_REFILL Research effect
                int maxRelease = db.Tank.ReleaseLevel;  // TODO(#231): the content max of the P1 release-level upgrade
                if (f.TankCount < 0) { f.TankCount = 0; Hit("P9b"); }
                if (f.TankCount > cap) { f.TankCount = cap; Hit("P9b"); }
                if (f.TankRefillTick < 0) { f.TankRefillTick = 0; Hit("P9b"); }
                if (refillTicks > 0 && f.TankRefillTick > refillTicks - 1) { f.TankRefillTick = refillTicks - 1; Hit("P9b"); }
                if (f.TankReleaseLevel < 1) { f.TankReleaseLevel = 1; Hit("P9b"); }
                if (maxRelease >= 1 && f.TankReleaseLevel > maxRelease) { f.TankReleaseLevel = maxRelease; Hit("P9b"); }
            }

            // P10: unknown item ids and Copies <= 0 are removed, no refund (issue #56).
            void P10()
            {
                if (PruneItems(s.Weapons, db.Weapons)) Hit("P10");
                if (PruneItems(s.Skills, db.Skills)) Hit("P10");
                if (PruneItems<object>(s.Companions, null)) Hit("P10");   // TODO(#161): companion ids against content
                if (PruneItems<object>(s.Runes, null)) Hit("P10");        // TODO(#227): rune ids against content
            }

            static bool PruneItems<TDef>(CollectionState c, IReadOnlyDictionary<string, TDef> content)
            {
                List<string> drop = null;
                foreach (var kv in c.Items)
                    if (kv.Value == null || kv.Value.Copies <= 0 || (content != null && !content.ContainsKey(kv.Key)))
                        (drop ??= new List<string>()).Add(kv.Key);
                if (drop == null) return false;
                foreach (var id in drop) c.Items.Remove(id);
                return true;
            }

            // P11: banner pity and EXP; a banner the content defines and enables gets a default entry.
            void P11()
            {
                var keys = new List<Banner>(s.Banners.Keys);
                foreach (var b in keys)
                {
                    var st = s.Banners[b];
                    if (st == null) { s.Banners[b] = new BannerState(); Hit("P11"); continue; }
                    if (st.Pity < 0) { st.Pity = 0; Hit("P11"); }
                    if (db.Banners.TryGetValue(b, out var def) && def.PityHard > 0 && st.Pity > def.PityHard - 1)
                    {
                        st.Pity = def.PityHard - 1;
                        Hit("P11");
                    }
                    if (st.Exp < 0) { st.Exp = 0; Hit("P11"); }
                    if (st.TotalPulls < 0) { st.TotalPulls = 0; Hit("P11"); }
                }
                foreach (var def in db.Banners.Values)
                    if (def.Enabled && !s.Banners.ContainsKey(def.Id)) { s.Banners[def.Id] = new BannerState(); Hit("P11"); }
            }

            // P12: quest progress. TODO(#170): drop quest ids the content does not define (no quest tables in ContentDb yet).
            void P12()
            {
                if (CleanQuests(s.Quests.Daily)) Hit("P12");
                if (CleanQuests(s.Quests.Weekly)) Hit("P12");
                if (CleanQuests(s.Quests.Achievement)) Hit("P12");
            }

            static bool CleanQuests(Dictionary<string, QuestProgress> map)
            {
                bool changed = false;
                List<string> drop = null;
                foreach (var kv in map)
                {
                    if (kv.Value == null) { (drop ??= new List<string>()).Add(kv.Key); continue; }
                    if (kv.Value.Progress < 0) { kv.Value.Progress = 0; changed = true; }
                }
                if (drop != null) { foreach (var id in drop) map.Remove(id); changed = true; }
                return changed;
            }

            // P13: research levels. A value < 1 is removed (absent reads as level 0).
            // TODO(#158): unknown research ids and the per-node maxLevel (no research table in ContentDb yet).
            void P13()
            {
                List<string> drop = null;
                foreach (var kv in s.Research.Level)
                    if (kv.Value < 1) (drop ??= new List<string>()).Add(kv.Key);
                if (drop == null) return;
                foreach (var id in drop) s.Research.Level.Remove(id);
                Hit("P13");
            }

            // P14: Copies <= DUPE_LVL_CUM[last]; LimitBreaks within [0, LIMIT_BREAK_RANKS]. No Rock Candy on load (04).
            void P14()
            {
                int maxCopies = db.DupeLevels.Count > 0 ? db.DupeLevels[db.DupeLevels.Count - 1] : int.MaxValue;
                foreach (var c in new[] { s.Weapons, s.Skills, s.Companions, s.Runes })
                    foreach (var item in c.Items.Values)
                    {
                        if (item.Copies > maxCopies) { item.Copies = maxCopies; Hit("P14"); }
                        if (item.LimitBreaks < 0) { item.LimitBreaks = 0; Hit("P14"); }
                        if (item.LimitBreaks > k.LimitBreakRanks) { item.LimitBreaks = k.LimitBreakRanks; Hit("P14"); }
                    }
            }

            // P15: stream counters follow the player-visible stats (02 section 3.4).
            void P15()
            {
                var st = s.Stats;
                SetCounter(RngStreams.Combat, 0);
                SetCounter(RngStreams.Crit, st.HeroHits);
                SetCounter(RngStreams.Loot, st.BossKills);
                SetCounter(RngStreams.FishSpawn, st.FishReleased);
                if (!opt.RemoteMode)
                {
                    SetCounter(RngStreams.SummonWeapon, 3 * PullsOf(Banner.WEAPON));
                    SetCounter(RngStreams.SummonSkill, 3 * PullsOf(Banner.SKILL));
                    SetCounter(RngStreams.SummonCompanion, 3 * PullsOf(Banner.COMPANION));
                }
            }

            long PullsOf(Banner b) => s.Banners.TryGetValue(b, out var st) && st != null ? st.TotalPulls : 0;

            void SetCounter(string stream, long expected)
            {
                var r = s.Rng[stream];
                if (expected < 0) expected = 0;
                if (r.Counter != expected) { r.Counter = expected; Hit("P15"); }
            }

            // P16: a dungeon run in a save was killed mid-run: abandon it. No refund (tickets are debited on clear only).
            void P16()
            {
                var c = s.Combat;
                if (c.DungeonRun == null && c.Mode == CombatMode.Stage) return;
                c.Mode = CombatMode.Stage;
                c.DungeonRun = null;
                Report.DungeonAbandoned = true;   // TODO(#172): emit DungeonAbandoned once the dungeon events exist
                Hit("P16");
            }

            // P17: stale pending summon / dungeon intents. Receipts are never auto-cleared.
            void P17()
            {
                long stale = (long)SaveConfig.PENDING_STALE_DAYS * k.DaySeconds;
                var p = s.Pending;
                if (p.Summon != null && now - p.Summon.RequestedServerTime > stale) { p.Summon = null; Hit("P17"); }
                if (p.Dungeon != null && now - p.Dungeon.RequestedServerTime > stale) { p.Dungeon = null; Hit("P17"); }
            }

            // P18: an offline reward computed in the future (the clock went backwards after the compute).
            void P18()
            {
                if (s.Offline.Pending != null && s.Offline.Pending.ComputedServerTime > now) { s.Offline.Pending = null; Hit("P18"); }
            }

            // P19: empty content stamps.
            void P19()
            {
                if (string.IsNullOrEmpty(s.ContentVersion)) { s.ContentVersion = db.Version ?? ""; Hit("P19"); }
                if (string.IsNullOrEmpty(s.ContentHash)) { s.ContentHash = db.ContentHash ?? ""; Hit("P19"); }
            }

            // P20: SaveSeq and DeviceId.
            void P20()
            {
                if (s.SaveSeq < 0) { s.SaveSeq = 0; Hit("P20"); }
                if (s.DeviceId == Guid.Empty && opt.InstallId != Guid.Empty) { s.DeviceId = opt.InstallId; Hit("P20"); }
            }

            // P21: tutorial step within [0, TUTORIAL_DONE]; RecentRuns keeps the newest DUNGEON_RECENT_RUNS_MAX.
            void P21()
            {
                var t = s.Tutorial;
                int done = db.TutorialSteps?.Count ?? 0;   // TUTORIAL_DONE
                if (t.Step < 0) { t.Step = 0; Hit("P21"); }
                if (t.Step > done) { t.Step = done; Hit("P21"); }
                var runs = s.Dungeons.RecentRuns;
                int max = SimConfig.DUNGEON_RECENT_RUNS_MAX;
                if (runs.Count > max) { runs.RemoveRange(0, runs.Count - max); Hit("P21"); }
                if (runs.RemoveAll(r => r == null) > 0) Hit("P21");
            }

            // 02 section 6.3: unknown Rng names are dropped with a log, not a repair hit.
            void DropUnknownStreams()
            {
                List<string> drop = null;
                foreach (var name in s.Rng.Keys)
                    if (Array.IndexOf(RngStreams.All, name) < 0) (drop ??= new List<string>()).Add(name);
                if (drop == null) return;
                foreach (var name in drop)
                {
                    s.Rng.Remove(name);
                    Report.Log.Add($"dropped unknown rng stream '{name}'");
                }
            }

            static bool IsFiniteNonNegative(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;

            static bool ClampNonNegative(int[] a)
            {
                if (a == null) return false;
                bool changed = false;
                for (int i = 0; i < a.Length; i++) if (a[i] < 0) { a[i] = 0; changed = true; }
                return changed;
            }

            static bool Resize<T>(T[] a, int n, out T[] result)
            {
                if (a != null && a.Length == n) { result = a; return false; }
                result = new T[n];
                if (a != null) Array.Copy(a, result, Math.Min(a.Length, n));
                return true;
            }
        }
    }
}
