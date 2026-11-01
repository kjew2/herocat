using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using HeroCat.Runtime.Save;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Gacha;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Web
{
    /// <summary>Positional JSON argument list ("[]", "[3]", "[{\"tier\":0},null]"). Missing and null both read as absent.</summary>
    internal sealed class Args
    {
        private readonly JsonElement[] _a;

        public Args(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) { _a = Array.Empty<JsonElement>(); return; }
            using var doc = JsonDocument.Parse(json);
            _a = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray()
                : Array.Empty<JsonElement>();
        }

        public bool Has(int i) => i < _a.Length && _a[i].ValueKind != JsonValueKind.Null && _a[i].ValueKind != JsonValueKind.Undefined;
        public double Num(int i) => Has(i) && _a[i].ValueKind == JsonValueKind.Number ? _a[i].GetDouble() : throw new ArgumentException("argument " + i + " must be a number");
        public int Int(int i) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Floor(Num(i))));
        public string Str(int i) => Has(i) && _a[i].ValueKind == JsonValueKind.String ? _a[i].GetString() : throw new ArgumentException("argument " + i + " must be a string");
        public JsonElement El(int i) => Has(i) ? _a[i] : throw new ArgumentException("argument " + i + " missing");
    }

    /// <summary>
    /// The adapter between the JS-shaped bridge (web/wasm/BRIDGE.md) and the existing C# core. Every mapping is listed in
    /// MAPPING.md. Wall time enters only through Init(nowMs) and SyncWallClock(nowMs); nothing here reads a clock.
    /// </summary>
    internal sealed class Host
    {
        private const int MaxQueuedEvents = 100000;

        private readonly ContentDb _db;
        private readonly Simulation _sim;
        private readonly GameState _st;
        private readonly SaveMeta _meta;
        private readonly Constants _k;
        private readonly Queue<string> _events = new Queue<string>();
        private readonly Sink _sink;
        private readonly Dictionary<string, string> _toBridge = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _toCs = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _questLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        private long _lplusSeen;
        private string _pendingFirstClear;

        public string ContentVersion => _db.Version;

        // ------------------------------------------------------------------ construction

        public Host(string contentJson, string saveJson, string profileSeedHex, double nowMs, string profileId)
        {
            string pack = PackSource.LooksLikePack(contentJson) ? contentJson : EmbeddedPack();
            if (pack == null) throw new InvalidOperationException("content pack missing: pass the bundled Content/*.json pack (web/wasm/pack-content.js)");
            _db = ContentDb.Load(new PackSource(pack), verifyHashes: true);
            _k = _db.Constants;
            SaveSerializer.Indented = false;

            foreach (var h in _db.HeroList) { var b = BridgeId(h.Id); _toBridge[h.Id] = b; _toCs[b] = h.Id; }
            foreach (var q in _db.DailyQuests) _questLabel[q.Id] = _db.Text(q.NameKey);

            _sink = new Sink(this);
            long nowSec = FloorSec(nowMs);
            if (!string.IsNullOrEmpty(saveJson))
            {
                var (state, meta, _) = SaveSystem.DecodeFull(Encoding.UTF8.GetBytes(saveJson), _db);
                if (!_db.IsCompatibleWith(state.ContentVersion)) throw new InvalidOperationException("save is from incompatible content version " + state.ContentVersion);
                _st = state;
                _meta = meta ?? new SaveMeta();
                _sim = new Simulation(_db, _st, _sink);
            }
            else
            {
                Guid id = ParseProfileId(profileId);
                ulong seed = string.IsNullOrWhiteSpace(profileSeedHex)
                    ? GameState.SeedFromProfileId(id)
                    : ulong.Parse(profileSeedHex.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                _st = GameState.NewProfile(_db, id, seed, nowSec);
                _meta = new SaveMeta { DeviceInstallId = id, LastSavedAtServerSec = nowSec };
                _sim = new Simulation(_db, _st, _sink);
                _sim.ApplyTutorialGrants();
                _sim.EnsureDay(nowSec);       // new profile: first-day login and daily free pulls
            }
        }

        private static string EmbeddedPack()
        {
            var asm = typeof(Host).Assembly;
            using var s = asm.GetManifestResourceStream("content-pack.json");
            if (s == null) return null;
            using var r = new System.IO.StreamReader(s, new UTF8Encoding(false));
            return r.ReadToEnd();
        }

        private static long FloorSec(double ms) => (long)Math.Floor(ms / 1000.0);

        /// <summary>Profile ids from JS are free-form strings; the core wants a Guid. A real Guid is kept, anything else is hashed (FNV-1a 128-bit style, two lanes).</summary>
        private static Guid ParseProfileId(string s)
        {
            if (!string.IsNullOrEmpty(s) && Guid.TryParse(s, out var g)) return g;
            s ??= "";
            ulong a = Fnv1a64.Hash(s), b = Fnv1a64.Hash("#" + s);
            var bytes = new byte[16];
            BitConverter.GetBytes(a).CopyTo(bytes, 0);
            BitConverter.GetBytes(b).CopyTo(bytes, 8);
            return new Guid(bytes);
        }

        // hero_c_01 -> C01 (the id the JS content and sprites use). Anything not matching passes through.
        private static string BridgeId(string id) =>
            id.Length >= 8 && id.StartsWith("hero_", StringComparison.Ordinal) && id[6] == '_'
                ? char.ToUpperInvariant(id[5]) + id.Substring(7)
                : id;

        private string Br(string csId) => csId == null ? null : _toBridge.TryGetValue(csId, out var b) ? b : csId;
        private string Cs(string bridgeId) => bridgeId != null && _toCs.TryGetValue(bridgeId, out var c) ? c : bridgeId;

        // ------------------------------------------------------------------ time

        private long NowMsVirtual() => _st.Clock.LastSyncServerMs + (_st.Tick - _st.Clock.TickAtLastSync) * (1000L / _k.TickHz);

        public void Advance(int ticks) { if (ticks > 0) _sim.Advance(ticks); }

        public void SetEmitHits(bool on)
        {
            _sink.Mask = on ? EventKinds.Default | EventKinds.HitLanded | EventKinds.EnemySpawned : EventKinds.Default | EventKinds.EnemySpawned;
        }

        // ------------------------------------------------------------------ state

        private static string BossName(BossPhase b) => b == BossPhase.Pending ? "pending" : b == BossPhase.Walled ? "walled" : "none";

        private static void Piece(JsonOut o, GearPiece p)
        {
            if (p == null) { o.Null(); return; }
            o.Obj().P("tier", p.Tier).P("rarity", (int)p.Rarity).P("enh", p.Enhance).EndObj();
        }

        public string State()
        {
            var s = _st;
            var o = new JsonOut().Obj();
            o.P("schema", s.SchemaVersion).P("contentVersion", s.ContentVersion).P("profileId", s.ProfileId.ToString())
             .P("createdUtcMs", s.CreatedAtServerSec * 1000L).P("tick", s.Tick);
            o.Key("stage").Obj().P("stage", s.Stage.Stage).P("killsInStage", s.Stage.KillsInStage).P("boss", BossName(s.Stage.Boss))
             .P("bossFailsOnStage", s.Stage.BossFailsOnStage).EndObj();
            var c = s.Combat;
            o.Key("combat").Obj().P("phase", c.Phase == CombatPhase.Fighting ? "fighting" : "spawning").P("spawnTimer", c.SpawnTimer)
             .P("enemyHp", c.EnemyHp).P("enemyMaxHp", c.EnemyMaxHp).P("enemyIsBoss", c.EnemyIsBoss).P("attackTimer", c.AttackTimer)
             .P("bossTimer", c.BossTimer).EndObj();
            var cu = s.Currencies;
            o.Key("cur").Obj().P("gold", cu.Gold).P("gems", cu.Gems).P("shards", cu.Shards).P("freePullsDaily", cu.FreePullsDaily)
             .P("freePullsBanked", cu.FreePullsBanked).EndObj();
            o.P("statLevel", s.Progression.StatLevel);
            o.Key("gear").Arr();
            foreach (var p in s.Gear.Slots) Piece(o, p);
            o.EndArr();
            o.Key("heroes").Obj().Key("owned").Obj();
            foreach (var kv in s.Heroes.Copies.Where(kv => kv.Value > 0).Select(kv => new KeyValuePair<string, int>(Br(kv.Key), kv.Value))
                         .OrderBy(kv => kv.Key, StringComparer.Ordinal))
                o.P(kv.Key, kv.Value);
            o.EndObj().Key("party").Arr();
            foreach (var id in s.Heroes.Equipped) o.Str(Br(id));
            o.EndArr().EndObj();
            o.Key("banner").Obj().P("pity", s.HeroBanner.PityCounter).P("totalPulls", s.HeroBanner.TotalPulls).EndObj();
            o.P("classAdvances", s.Progression.ClassAdvances);
            o.Key("daily").Obj().P("dayKey", s.Daily.DayKey).Key("progress").Arr();
            foreach (var v in s.Daily.Progress) o.Int(v);
            o.EndArr().Key("rewarded").Arr();
            foreach (var v in s.Daily.Rewarded) o.Bool(v);
            o.EndArr().EndObj();
            o.Key("prog").Obj().P("highestFirstClearBoss", s.Progression.HighestFirstClearBossStage)
             .P("chestTimerStartMs", s.Progression.ChestTimerStart * 1000L).P("tutorialGranted", s.Progression.TutorialGranted).EndObj();
            o.Key("clock").Obj().P("lastSyncUtcMs", s.Clock.LastSyncServerMs).P("tickAtLastSync", s.Clock.TickAtLastSync).EndObj();
            var l = s.Lifetime;
            o.Key("life").Obj().P("kills", l.Kills).P("bossKills", l.BossKills).P("bossFails", l.BossFails).P("gearDrops", l.GearDrops)
             .P("enhAttempts", l.EnhanceAttempts).P("enhSuccesses", l.EnhanceSuccesses).P("goldEarned", l.GoldEarned)
             .P("offlineGold", l.OfflineGoldEarned).P("firstWallStage", l.FirstWallStage).P("pulls", s.HeroBanner.TotalPulls)
             .P("lplus", _lplusSeen).EndObj();
            return o.EndObj().ToString();
        }

        // ------------------------------------------------------------------ events

        public string Drain()
        {
            if (_events.Count == 0) return "[]";
            var sb = new StringBuilder("[");
            bool first = true;
            while (_events.Count > 0) { if (!first) sb.Append(','); first = false; sb.Append(_events.Dequeue()); }
            return sb.Append(']').ToString();
        }

        public void PushEngineError(string message) =>
            Enqueue(new JsonOut().Obj().P("type", "engineError").P("message", message).P("tick", _st.Tick).EndObj().ToString());

        private void Enqueue(string json)
        {
            if (_events.Count >= MaxQueuedEvents) _events.Dequeue();
            _events.Enqueue(json);
        }

        private sealed class Sink : IEventSink
        {
            private readonly Host _h;
            public Sink(Host h) { _h = h; Mask = EventKinds.Default | EventKinds.EnemySpawned; }
            public EventKinds Mask { get; set; }
            public void Publish(SimEvent e) => _h.Convert(e);
        }

        // Converts at publication time so derived fields (class advance dps) see the moment of the event.
        private void Convert(SimEvent e)
        {
            var o = new JsonOut().Obj();
            switch (e)
            {
                case HitLanded x: o.P("type", "hit").P("dmg", x.Damage); break;
                case EnemySpawned x: o.P("type", "spawn").P("boss", x.IsBoss).P("stage", x.Stage); break;
                case EnemyKilled x: o.P("type", "kill").P("stage", x.Stage).P("boss", x.IsBoss).P("gold", x.Gold); break;
                case BossCleared x:
                    // The core publishes FirstClear before BossCleared; the UI feed expects bossCleared, then firstClear.
                    o.P("type", "bossCleared").P("stage", x.Stage).P("tick", e.Tick).EndObj();
                    Enqueue(o.ToString());
                    if (_pendingFirstClear != null) { Enqueue(_pendingFirstClear); _pendingFirstClear = null; }
                    return;
                case BossFailed x: o.P("type", "bossFailed").P("stage", x.Stage).P("attempt", x.FailsOnStage); break;
                case FirstClear x:
                    _pendingFirstClear = new JsonOut().Obj().P("type", "firstClear").P("stage", x.Stage).P("gems", x.Gems).P("tick", e.Tick).EndObj().ToString();
                    return;
                case StageAdvanced x: o.P("type", "stageAdvanced").P("from", x.FromStage).P("to", x.ToStage).P("viaBoss", x.ViaBoss); break;
                case StageCapReached _: o.P("type", "stageCap"); break;
                case ClassAdvanced x:
                {
                    double after = _sim.Query.Dps;
                    double stepMult = _db.ClassMult(x.Advances) / _db.ClassMult(x.Advances - 1);
                    o.P("type", "classAdvanced").P("advances", x.Advances).P("stage", x.Stage).P("dpsBefore", after / stepMult).P("dpsAfter", after);
                    break;
                }
                case GearDropped x:
                {
                    o.P("type", "gearDropped").P("slot", x.Slot).Key("piece"); Piece(o, x.Piece);
                    o.P("equipped", x.Equipped).Key("replaced"); Piece(o, x.Equipped ? x.Replaced : null);
                    o.Key("current"); Piece(o, x.Equipped ? null : _st.Gear.Slots[x.Slot]);
                    o.P("soldFor", x.SoldFor);
                    break;
                }
                case QuestCompleted x:
                    o.P("type", "questCompleted").P("id", x.QuestId).P("label", _questLabel.TryGetValue(x.QuestId, out var lb) ? lb : x.QuestId).P("gems", x.Gems);
                    break;
                case IdleChestClaimed x: o.P("type", "chestClaimed").P("gems", x.Gems); break;
                case DayRolledOver x: o.P("type", "dayRolledOver").P("dayKey", x.DayKey); break;
                case OfflineGoldCredited x:
                    o.P("type", "offlineGold").P("elapsedMs", x.ElapsedSec * 1000L).P("creditedS", x.CreditedSec).P("gold", x.Gold);
                    break;
                case ClockWentBackwards x: o.P("type", "clockWentBackwards").P("byMs", x.ByMs); break;
                case StatLeveled x: o.P("type", "statLeveled").P("from", x.NewLevel - x.Count).P("to", x.NewLevel).P("cost", x.Cost); break;
                case EnhanceAttempted x: o.P("type", "enhanced").P("slot", x.Slot).P("success", x.Success).P("level", x.NewLevel).P("cost", x.Cost); break;
                default: return;   // HeroPulled (synthesised by the pull command), HeroEquipped (no JS equivalent)
            }
            o.P("tick", e.Tick).EndObj();
            Enqueue(o.ToString());
        }

        // ------------------------------------------------------------------ queries

        private GearPiece PieceArg(Args a, int i)
        {
            if (!a.Has(i)) return null;
            var e = a.El(i);
            return new GearPiece(0, (Rarity)e.GetProperty("rarity").GetInt32(), e.GetProperty("tier").GetInt32(), e.GetProperty("enh").GetInt32());
        }

        private static string N(double v) => new JsonOut().Num(v).ToString();
        private static string B(bool v) => v ? "true" : "false";

        private double BulkCost(int n, int m)
        {
            double t = 0;
            for (int i = 0; i < m; i++) t += _db.StatCost(n + i);
            return t;
        }

        private int MaxAffordable(double gold)
        {
            int n = _st.Progression.StatLevel, m = 0;
            double acc = 0;
            while (m < 100000)
            {
                double c = _db.StatCost(n + m);
                if (acc + c > gold) break;
                acc += c; m++;
            }
            return m;
        }

        private List<string> BestParty()
        {
            var q = _sim.Query;
            return _st.Heroes.Copies.Where(kv => kv.Value > 0 && _db.Heroes.ContainsKey(kv.Key)).Select(kv => kv.Key)
                .OrderByDescending(id => q.HeroPct(id)).ThenBy(id => id, StringComparer.Ordinal).Take(_k.HeroSlots).ToList();
        }

        public string Query(string name, string argsJson)
        {
            var a = new Args(argsJson);
            var q = _sim.Query;
            switch (name)
            {
                case "hp": return N(q.Hp(a.Int(0)));
                case "bossHp": return N(q.BossHp(a.Int(0)));
                case "gold": return N(q.Gold(a.Int(0)));
                case "statCost": return N(q.StatCost(a.Int(0)));
                case "statMult": return N(a.Has(0) ? 1.0 + _k.StatPerLvl * a.Int(0) : q.StatMult);
                case "gearTier": return N(_k.TierOf(a.Int(0)));
                case "gearBasePct": { var p = PieceArg(a, 0); return N(p == null ? 0 : q.GearBasePct(p)); }
                case "gearEffPct": { var p = PieceArg(a, 0); return N(p == null ? 0 : q.GearEffPct(p)); }
                case "gearSlotMult": return N(q.GearSlotMult(a.Int(0)));
                case "gearMult": return N(q.GearMult);
                case "heroLevel": return N(q.HeroLevel(a.Int(0)));
                case "heroPct": return N(q.HeroPct(Cs(a.Str(0))));
                case "heroMult": return N(q.HeroMult);
                case "classMult": return N(a.Has(0) ? _db.ClassMult(a.Int(0)) : q.ClassMult);
                case "dps": return N(q.Dps);
                case "hitDamage": return N(q.HitDamage);
                case "hitsToKill": return N(q.HitsToKill(a.Num(0)));
                case "killTimeSeconds": return N(q.KillTimeSeconds(a.Int(0)));
                case "isBossStage": return B(q.IsBossStage);
                case "bossClearable": return B(q.BossClearable);
                case "needDps":
                {
                    int stage = _st.Stage.Stage;
                    var rule = _db.BossRuleFor(stage);
                    double hits = (double)rule.TimeTicks / _k.AttackIntervalTicks;
                    return N(q.BossHp(stage) / hits * _k.TickHz / _k.AttackIntervalTicks);
                }
                case "enhanceCost": return N(q.EnhanceCost);
                case "enhanceSuccess": return N(q.EnhanceSuccess(a.Int(0)));
                case "pityRate": return N(q.PityRate(a.Int(0)));
                case "freePullsRemaining": return N(q.FreePullsRemaining);
                case "chestReadyAtMs": return N(q.ChestReadyAtServerSec * 1000.0);
                case "chestReady": return B(q.IsChestReady);
                case "bulkCost": return N(BulkCost(a.Int(0), a.Int(1)));
                case "maxAffordable": return N(a.Has(0) ? MaxAffordable(a.Num(0)) : q.MaxAffordableStatLevels());
                case "pullPrice": return PullPrice(a.Has(0) ? a.Int(0) : 1);
                case "bestParty": { var o = new JsonOut().Arr(); foreach (var id in BestParty()) o.Str(Br(id)); return o.EndArr().ToString(); }
                case "autoEquipWouldChange":
                {
                    double cur = 0, best = 0;
                    foreach (var id in _st.Heroes.Equipped) if (id != null) cur += q.HeroPct(id);
                    foreach (var id in BestParty()) best += q.HeroPct(id);
                    return B(best > cur + 1e-9);
                }
                case "nowMs": return N(NowMsVirtual());
                case "validate":
                {
                    var o = new JsonOut().Arr();
                    try { _sim.Validate(); } catch (InvariantViolationException e) { o.Str(e.Message); }
                    return o.EndArr().ToString();
                }
                case "consolidatedRates": return ConsolidatedRates();
                default: return "null";
            }
        }

        private string PullPrice(int count)
        {
            var cur = _st.Currencies;
            var b = _db.HeroBanner;
            var o = new JsonOut();
            if (count == 1)
            {
                if (cur.FreePullsDaily > 0) return o.Obj().P("with", "freeDaily").EndObj().ToString();
                if (cur.FreePullsBanked > 0) return o.Obj().P("with", "freeBanked").EndObj().ToString();
                if (cur.Gems >= b.PullCostGems) return o.Obj().P("with", "gems").P("gems", b.PullCostGems).EndObj().ToString();
                return "null";
            }
            int n = b.GuaranteeBatchSize;
            if (cur.FreePullsBanked >= n) return o.Obj().P("with", "freeBanked").EndObj().ToString();
            if (cur.Gems >= (long)n * b.PullCostGems) return o.Obj().P("with", "gems").P("gems", (long)n * b.PullCostGems).EndObj().ToString();
            return "null";
        }

        private string ConsolidatedRates()
        {
            var b = _db.HeroBanner;
            double S = 1, E = 0;
            for (int n = 1; n <= b.PityHard; n++)
            {
                double qn = b.PityRate(n);
                E += n * S * qn;
                S *= 1 - qn;
            }
            double lplus = 1 / E;
            return new JsonOut().Obj().P("expectedPulls", E).P("lplus", lplus).P("m", lplus * b.MythicShareOfLplus)
                .P("l", lplus * (1 - b.MythicShareOfLplus)).EndObj().ToString();
        }

        // ------------------------------------------------------------------ commands

        private static string Ok() => "{\"ok\":true}";
        private static string Fail(string reason) => new JsonOut().Obj().P("ok", false).P("reason", reason).EndObj().ToString();

        private static string ReasonName(Reject r)
        {
            switch (r)
            {
                case Reject.NotEnoughGold: return "notEnoughGold";
                case Reject.NotEnoughGems: return "cannotAfford";
                case Reject.EmptySlot: return "emptySlot";
                case Reject.EnhanceMaxed: return "maxed";
                case Reject.HeroNotOwned: return "heroNotOwned";
                case Reject.HeroAlreadyEquipped: return "heroAlreadyEquipped";
                case Reject.NoBossToAttempt: return "noBossToAttempt";
                case Reject.BossAlreadyPending: return "bossAlreadyPending";
                case Reject.ChestNotReady: return "notReady";
                case Reject.InvalidCount: return "invalid";
                default: return r.ToString();
            }
        }

        public string Cmd(string name, string argsJson)
        {
            var a = new Args(argsJson);
            switch (name)
            {
                case "buyStatLevels":
                {
                    double mRaw = a.Has(0) ? a.Num(0) : 0;
                    if (!(mRaw > 0)) return Fail("invalid");
                    int m = (int)Math.Min(mRaw, 100000);
                    double cost = _sim.Query.BulkStatCost(m);
                    return _sim.BuyStatLevels(m) ? new JsonOut().Obj().P("ok", true).P("cost", cost).EndObj().ToString() : Fail("notEnoughGold");
                }
                case "buyStatLevelsMax": return N(_sim.BuyStatLevelsMax());
                case "enhance":
                {
                    var r = _sim.Enhance(a.Int(0));
                    if (!r.Ok) return Fail(ReasonName(r.Reason));
                    return new JsonOut().Obj().P("ok", true).P("success", r.Success).P("level", r.Level).P("cost", r.Cost).EndObj().ToString();
                }
                case "attemptBoss": { var r = _sim.AttemptBoss(); return r.Ok ? Ok() : Fail(ReasonName(r.Reason)); }
                case "pull": return Pull(a.Has(0) && a.Int(0) == 10 ? 10 : 1);
                case "equipHero":
                {
                    string id = Cs(a.Str(1));
                    if (!_db.Heroes.ContainsKey(id)) return Fail("heroNotOwned");
                    var r = _sim.EquipHero(a.Int(0), id);
                    return r.Ok ? Ok() : Fail(ReasonName(r.Reason));
                }
                case "unequipHero": { var r = _sim.UnequipHero(a.Int(0)); return r.Ok ? Ok() : Fail(ReasonName(r.Reason)); }
                case "autoEquip": return N(_sim.AutoEquipBestHeroes());
                case "claimChest": { var r = _sim.ClaimIdleChest(); return r.Ok ? Ok() : Fail(ReasonName(r.Reason)); }
                case "syncWallClock": _sim.SyncWallClock(FloorSec(a.Num(0))); return "null";
                case "debugGrantGold": _sim.Debug.GrantGold(a.Num(0)); return "null";
                case "debugGrantGems": _sim.Debug.GrantGems((long)a.Num(0)); return "null";
                case "debugSetStage": _sim.Debug.SetStage(a.Int(0)); return "null";
                default: return "{\"error\":\"unknown\"}";
            }
        }

        private string Pull(int count)
        {
            var banner = _db.HeroBanner;
            long pityBefore = _st.HeroBanner.PityCounter;
            var r = count == 10 ? _sim.Pull10() : _sim.Pull();
            if (!r.Ok) return Fail(ReasonName(r.Reason));
            string with = r.PaidWith == PaidWith.FreeDaily ? "freeDaily" : r.PaidWith == PaidWith.FreeBanked ? "freeBanked" : "gems";
            bool guarantee = count >= banner.GuaranteeBatchSize && banner.GuaranteeBatchSize >= 2;
            var res = new JsonOut().Arr();
            long pity = pityBefore;
            bool hit = false;
            for (int i = 0; i < r.Results.Length; i++)
            {
                var x = r.Results[i];
                pity += 1;                                                  // pity value at the moment of this pull (PullOne increments first)
                bool lplus = x.Tier >= Rarity.L;
                bool forced = guarantee && i == r.Results.Length - 1 && !hit && !lplus;
                res.Obj().P("id", Br(x.Hero)).P("name", _db.Text(_db.Heroes[x.Hero].NameKey)).P("tier", (int)x.Tier).P("isNew", x.IsNew)
                   .P("levelBefore", x.LevelBefore).P("levelAfter", x.LevelAfter).P("shards", x.ShardsGained).P("pity", pity).P("forced", forced).EndObj();
                if (x.Tier >= banner.GuaranteeMinTier) hit = true;
                if (lplus) { pity = 0; _lplusSeen++; }
            }
            res.EndArr();
            string results = res.ToString();
            Enqueue(new JsonOut().Obj().P("type", "pulled").Key("results").Raw(results).P("paidWith", with).P("tick", _st.Tick).EndObj().ToString());
            return new JsonOut().Obj().P("ok", true).Key("results").Raw(results).P("paidWith", with).EndObj().ToString();
        }

        // ------------------------------------------------------------------ save

        // ------------------------------------------------------------------ UI content
        // Labels and display numbers the browser UI needs, read from the same ContentDb the sim uses, in the shape the UI
        // consumes (UPPER_SNAKE keys grouped like specs/00 section 2). Display only: no game rule is evaluated in JS from these.
        public string UiContent()
        {
            var o = new JsonOut().Obj();
            o.P("version", _db.Version);
            string[] letters = { "C", "R", "E", "L", "M" };
            o.Key("rarities").Arr(); foreach (var l in letters) o.Str(l); o.EndArr();
            o.Key("rarityNames").Obj();
            foreach (var t in _db.HeroTiers) o.P(letters[(int)t.Tier], _db.Text(t.NameKey));
            o.EndObj();

            BossRuleDef boss = _db.BossRules[_db.BossRules.Count - 1];   // the rule that applies from its stage onward
            o.Key("combat").Obj().P("TICK_HZ", _k.TickHz).P("ATTACK_INTERVAL_TICKS", _k.AttackIntervalTicks)
                .P("KILLS_PER_STAGE", _k.KillsPerStage).P("BOSS_EVERY", _k.BossEvery).P("BOSS_TIME_TICKS", boss.TimeTicks).EndObj();

            var bn = _db.HeroBanner;
            o.Key("economy").Obj().P("STAT_PER_LVL", _k.StatPerLvl).P("STAT_COST_G", _db.Curves[CurveId.StatCost].Growth)
                .P("PULL_COST_GEMS", bn.PullCostGems).P("FREE_PULLS_PER_DAY", bn.FreePullsPerDay)
                .P("IDLE_CHEST_GEMS", _k.IdleChestGems).P("IDLE_CHEST_HOURS", _k.IdleChestHours)
                .P("OFFLINE_RATE", _k.OfflineRate).P("OFFLINE_CAP_H", _k.OfflineCapH).P("DAY_SECONDS", _k.DaySeconds);
            o.Key("STAT_BULK_STEPS").Arr().Int(1).Int(10).Int(100).EndArr();      // specs/04 section 4.2 buttons; not a content table yet
            o.EndObj();

            o.Key("quests").Arr();
            foreach (var q in _db.DailyQuests)
                o.Obj().P("id", q.Id).P("kind", q.Type.ToString()).P("target", q.Target).P("gems", q.RewardGems).P("label", _db.Text(q.NameKey)).EndObj();
            o.EndArr();

            o.Key("gear").Obj();
            o.Key("SLOTS").Arr(); foreach (var g in _db.GearSlots.OrderBy(x => x.SortOrder)) o.Str(_db.Text(g.NameKey)); o.EndArr();
            o.P("DROP_P", _k.GearDropP).P("BOSS_DROP_P", boss.GearDropP).P("ENH_MAX", _k.EnhMax).EndObj();

            o.Key("banner").Obj().P("PITY_HARD", bn.PityHard).P("PITY_SOFT_START", bn.PitySoftStart).P("PITY_SOFT_STEP", bn.PitySoftStep)
                .P("BASE_LPLUS", bn.BaseLplus).P("M_SHARE_OF_LPLUS", bn.MythicShareOfLplus);
            o.Key("BASE_RATES").Arr(); foreach (var r in bn.Rates) o.Num(r); o.EndArr();
            o.EndObj();

            o.Key("heroes").Obj().P("SLOTS", _k.HeroSlots);
            o.Key("LVL_CUM").Arr(); foreach (var c in _db.HeroLevelThresholds) o.Int(c); o.EndArr();
            o.Key("pool").Arr();
            for (int t = 0; t < bn.Pool.Count; t++)
            {
                o.Arr();
                foreach (var id in bn.Pool[t])
                {
                    string name = _db.Text(_db.Heroes[id].NameKey);
                    int sp = name.IndexOf(' ');
                    o.Obj().P("id", Br(id)).P("tier", letters[t]).P("name", name).P("short", sp > 0 ? name.Substring(0, sp) : name).EndObj();
                }
                o.EndArr();
            }
            o.EndArr().EndObj();

            var adv = _db.ClassAdvances.OrderBy(a => a.Index).ToList();
            o.Key("class").Obj().P("MULT", adv.Count > 0 ? adv[0].Mult : 1.0);
            o.Key("STAGES").Arr(); foreach (var a in adv) o.Int(a.Stage); o.EndArr();
            o.Key("NAMES").Arr().Str(_db.Text("class.base.name")); foreach (var a in adv) o.Str(_db.Text(a.NameKey)); o.EndArr();
            o.EndObj();

            return o.EndObj().ToString();
        }

        public string Export()
        {
            long nowSec = NowMsVirtual() / 1000;
            SaveSystem.Stamp(_st, _meta, nowSec, _db.Version);
            return Encoding.UTF8.GetString(SaveSerializer.Encode(_st, _meta));
        }
    }
}
