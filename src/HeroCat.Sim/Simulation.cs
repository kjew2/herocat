using System;
using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Gacha;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>
    /// The one simulation (specs/03). Everything observable is a pure function of (ContentDb, GameState) plus
    /// the sequence of ticks/commands/SyncWallClock calls. Single-threaded; not thread-safe. Holds no state of
    /// its own besides caches rebuilt from GameState in the constructor.
    /// </summary>
    public sealed partial class Simulation
    {
        readonly Constants _k;
        readonly IEventSink _sink;
        readonly IGachaService _gacha;
        readonly BannerDef _banner;
        readonly long _msPerTick;
        readonly int[][] _questsByType;

        RngStream _gearDrop, _gearRarity, _enhance, _gachaHero;

        bool _dpsDirty = true;
        double _dps, _hit, _statMult, _gearMult, _heroMult, _classMult;
        long _nextDayTick;

        public Simulation(ContentDb content, GameState state, IEventSink events = null, IGachaService gacha = null)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            State = state ?? throw new ArgumentNullException(nameof(state));
            _sink = events ?? NullEventSink.Instance;
            _gacha = gacha ?? new LocalGachaService();
            _k = content.Constants;
            _banner = content.HeroBanner;
            _msPerTick = 1000 / _k.TickHz;

            _questsByType = new int[4][];
            for (int t = 0; t < 4; t++)
            {
                var l = new List<int>();
                for (int i = 0; i < content.DailyQuests.Count; i++)
                    if ((int)content.DailyQuests[i].Type == t) l.Add(i);
                _questsByType[t] = l.ToArray();
            }

            NormalizeShape();
            Query = new SimQuery(this);
            Debug = new DebugCommands(this);
            RecomputeNextDayTick();
        }

        public ContentDb Content { get; }
        public GameState State { get; }
        public SimQuery Query { get; }
        public DebugCommands Debug { get; }

        /// <summary>Make arrays/streams match content (new profile, or a save from older content).</summary>
        void NormalizeShape()
        {
            var s = State;
            s.Rng ??= new RngState();
            s.Rng.Streams ??= new Dictionary<string, RngStream>(StringComparer.Ordinal);
            foreach (var name in StreamNames.All)
                if (!s.Rng.Streams.ContainsKey(name))
                    s.Rng.Streams[name] = new RngStream(name, StreamNames.StreamSeed(s.Rng.ProfileSeed, name));
            _gearDrop = s.Rng.Streams[StreamNames.GearDrop];
            _gearRarity = s.Rng.Streams[StreamNames.GearRarity];
            _enhance = s.Rng.Streams[StreamNames.Enhance];
            _gachaHero = s.Rng.Streams[StreamNames.GachaHero];

            if (s.Gear.Slots == null || s.Gear.Slots.Length != Content.GearSlotCount)
                s.Gear.Slots = Resize(s.Gear.Slots, Content.GearSlotCount);
            if (s.Heroes.Equipped == null || s.Heroes.Equipped.Length != _k.HeroSlots)
                s.Heroes.Equipped = Resize(s.Heroes.Equipped, _k.HeroSlots);
            s.Heroes.Copies ??= new Dictionary<string, int>(StringComparer.Ordinal);
            int nq = Content.DailyQuests.Count;
            if (s.Daily.Progress == null || s.Daily.Progress.Length != nq) s.Daily.Progress = Resize(s.Daily.Progress, nq);
            if (s.Daily.Rewarded == null || s.Daily.Rewarded.Length != nq) s.Daily.Rewarded = Resize(s.Daily.Rewarded, nq);
            if (s.Combat.Phase == CombatPhase.Spawning && s.Combat.SpawnTimer <= 0) s.Combat.SpawnTimer = _k.SpawnDelayTicks;
        }

        static T[] Resize<T>(T[] a, int n)
        {
            var r = new T[n];
            if (a != null) Array.Copy(a, r, Math.Min(a.Length, n));
            return r;
        }

        bool Wants(EventKinds k) => (_sink.Mask & k) != 0;

        // ================================================================ tick loop (specs/03 section 7)

        /// <summary>Advance exactly one tick (1 / TICK_HZ seconds).</summary>
        public void Tick()
        {
            var st = State;
            st.Tick += 1;
            if (st.Tick >= _nextDayTick) RollDayFromTicks();

            var cb = st.Combat;
            if (cb.Phase == CombatPhase.Spawning)
            {
                cb.SpawnTimer -= 1;
                if (cb.SpawnTimer <= 0) Spawn();
                return;
            }

            cb.AttackTimer -= 1;
            if (cb.AttackTimer <= 0)
            {
                cb.AttackTimer = _k.AttackIntervalTicks;
                double dmg = HitDamageCached;
                cb.EnemyHp -= dmg;
                if (Wants(EventKinds.HitLanded)) _sink.Publish(new HitLanded(st.Tick, dmg, Math.Max(0, cb.EnemyHp)));
                if (cb.EnemyHp <= 0) { Kill(); return; }
            }
            if (cb.EnemyIsBoss)
            {
                cb.BossTimer -= 1;
                if (cb.BossTimer <= 0) BossFail();
            }
        }

        /// <summary>Tick() repeated.</summary>
        public void Advance(long ticks)
        {
            for (long i = 0; i < ticks; i++) Tick();
        }

        /// <summary>Convenience: advance by simulated seconds (seconds * TICK_HZ ticks).</summary>
        public void AdvanceSeconds(double seconds) => Advance((long)(seconds * _k.TickHz));

        void Spawn()
        {
            var stg = State.Stage;
            var cb = State.Combat;
            int s = stg.Stage;
            bool isBoss = stg.Boss == BossPhase.Pending;
            double hp = Content.EnemyHp(s);
            if (isBoss)
            {
                var rule = Content.BossRuleFor(s);
                hp = rule.HpMult * hp;
                cb.BossTimer = rule.TimeTicks;
                QuestProgress(QuestType.ClearBoss, 1);   // boss fights STARTED count (specs/00 section 7.2)
            }
            else cb.BossTimer = 0;
            cb.EnemyIsBoss = isBoss;
            cb.EnemyHp = cb.EnemyMaxHp = hp;
            cb.AttackTimer = _k.AttackIntervalTicks;
            cb.Phase = CombatPhase.Fighting;
            if (Wants(EventKinds.EnemySpawned)) _sink.Publish(new EnemySpawned(State.Tick, s, isBoss, hp));
        }

        void Kill()
        {
            var st = State;
            var stg = st.Stage;
            var cb = st.Combat;
            int s = stg.Stage;
            bool isBoss = cb.EnemyIsBoss;
            BossRuleDef rule = isBoss ? Content.BossRuleFor(s) : null;

            double gold = Content.GoldPerKill(s);
            if (isBoss) gold *= rule.GoldMult;
            AddGold(gold);
            st.Lifetime.Kills += 1;
            if (isBoss) st.Lifetime.BossKills += 1;
            if (Wants(EventKinds.EnemyKilled)) _sink.Publish(new EnemyKilled(st.Tick, s, isBoss, gold));
            QuestProgress(QuestType.KillMobs, 1);

            RollGearDrop(isBoss, s, rule);

            if (isBoss)
            {
                FirstClearCheck(s, rule);
                if (Wants(EventKinds.BossCleared)) _sink.Publish(new BossCleared(st.Tick, s));
                AdvanceStage(viaBoss: true);
            }
            else if (stg.Boss == BossPhase.Walled)
            {
                // farming a walled stage: no kill credit toward advance
            }
            else
            {
                stg.KillsInStage += 1;
                if (stg.KillsInStage == _k.KillsPerStage - 1 && _k.IsBossStage(s))
                    stg.Boss = BossPhase.Pending;                 // the 10th enemy of a boss stage is the boss
                else if (stg.KillsInStage >= _k.KillsPerStage)
                    AdvanceStage(viaBoss: false);
            }

            cb.Phase = CombatPhase.Spawning;
            cb.SpawnTimer = _k.SpawnDelayTicks;
            cb.EnemyIsBoss = false;
            cb.EnemyHp = 0;
            cb.BossTimer = 0;
        }

        void AdvanceStage(bool viaBoss)
        {
            var stg = State.Stage;
            int from = stg.Stage;
            if (from >= _k.StageCap)
            {
                stg.KillsInStage = 0;
                stg.Boss = BossPhase.None;
                stg.BossFailsOnStage = 0;
                if (Wants(EventKinds.StageCapReached)) _sink.Publish(new StageCapReached(State.Tick, from));
                return;
            }
            stg.Stage = from + 1;
            stg.KillsInStage = 0;
            stg.Boss = BossPhase.None;
            stg.BossFailsOnStage = 0;
            if (Wants(EventKinds.StageAdvanced)) _sink.Publish(new StageAdvanced(State.Tick, from, from + 1, viaBoss));
            ApplyClassAdvances();
        }

        /// <summary>Reaching CLASS_STAGES[k] grants advance k+1 on arrival (specs/04 section 6).</summary>
        void ApplyClassAdvances()
        {
            var p = State.Progression;
            var adv = Content.ClassAdvances;
            while (p.ClassAdvances < adv.Count && State.Stage.Stage >= adv[p.ClassAdvances].Stage)
            {
                p.ClassAdvances += 1;
                MarkDpsDirty();
                if (Wants(EventKinds.ClassAdvanced))
                    _sink.Publish(new ClassAdvanced(State.Tick, p.ClassAdvances, State.Stage.Stage, Content.ClassMult(p.ClassAdvances)));
            }
        }

        void BossFail()
        {
            var stg = State.Stage;
            var cb = State.Combat;
            int s = stg.Stage;
            stg.Boss = BossPhase.Walled;
            stg.BossFailsOnStage += 1;
            State.Lifetime.BossFails += 1;
            if (State.Lifetime.FirstWallStage == 0) State.Lifetime.FirstWallStage = s;
            if (Wants(EventKinds.BossFailed)) _sink.Publish(new BossFailed(State.Tick, s, stg.BossFailsOnStage));
            cb.Phase = CombatPhase.Spawning;
            cb.SpawnTimer = _k.SpawnDelayTicks;
            cb.EnemyIsBoss = false;
            cb.EnemyHp = 0;
            cb.BossTimer = 0;
        }

        // ================================================================ economy hooks

        void AddGold(double gold)
        {
            State.Currencies.Gold += gold;
            State.Lifetime.GoldEarned += gold;
        }

        void AddGems(long gems)
        {
            State.Currencies.Gems += gems;
            State.Lifetime.GemsEarned += gems;
        }

        void FirstClearCheck(int stage, BossRuleDef rule)
        {
            var p = State.Progression;
            if (stage <= p.HighestFirstClearBossStage) return;
            p.HighestFirstClearBossStage = stage;
            AddGems(rule.FirstClearGems);
            if (Wants(EventKinds.FirstClear)) _sink.Publish(new FirstClear(State.Tick, stage, rule.FirstClearGems));
        }

        void QuestProgress(QuestType type, int delta)
        {
            var idx = _questsByType[(int)type];
            if (idx.Length == 0) return;
            var d = State.Daily;
            for (int j = 0; j < idx.Length; j++)
            {
                int i = idx[j];
                d.Progress[i] += delta;
                if (!d.Rewarded[i] && d.Progress[i] >= Content.DailyQuests[i].Target)
                {
                    d.Rewarded[i] = true;
                    var q = Content.DailyQuests[i];
                    AddGems(q.RewardGems);
                    if (Wants(EventKinds.QuestCompleted)) _sink.Publish(new QuestCompleted(State.Tick, q.Id, q.RewardGems));
                }
            }
        }

        /// <summary>specs/03 section 8.2 + specs/04 sections 5.2-5.5 (auto-equip if strictly better base, else sell).</summary>
        void RollGearDrop(bool isBoss, int stage, BossRuleDef rule)
        {
            double p = isBoss ? rule.GearDropP : _k.GearDropP;
            if (_gearDrop.NextDouble() >= p) return;
            double u1 = _gearRarity.NextDouble(), u2 = _gearRarity.NextDouble();
            var rarity = SampleGearRarity(u1, isBoss ? rule.GearMinRarity : Rarity.C);
            int slot = (int)(u2 * Content.GearSlotCount);
            var piece = new GearPiece(slot, rarity, _k.TierOf(stage), 0);
            State.Lifetime.GearDrops += 1;

            var slots = State.Gear.Slots;
            var cur = slots[slot];
            bool equip = cur == null || GearBasePct(piece) > GearBasePct(cur);
            double sold;
            GearPiece replaced = null;
            if (equip)
            {
                slots[slot] = piece;
                replaced = cur;
                sold = cur == null ? 0 : SellValue(cur);
                MarkDpsDirty();
            }
            else sold = SellValue(piece);
            if (sold > 0) AddGold(sold);
            if (Wants(EventKinds.GearDropped)) _sink.Publish(new GearDropped(State.Tick, slot, piece, equip, replaced, sold));
        }

        Rarity SampleGearRarity(double u, Rarity min)
        {
            var w = Content.GearRarity;
            double total = 0;
            for (int r = (int)min; r <= (int)Rarity.M; r++) total += w[r].Weight;
            double acc = 0;
            for (int r = (int)min; r <= (int)Rarity.M; r++)
            {
                acc += w[r].Weight / total;
                if (u < acc) return (Rarity)r;
            }
            return Rarity.M;
        }

        internal double GearBasePct(GearPiece p) => Content.GearSlotPct(p.Tier) * Content.GearRarity[(int)p.Rarity].Mult;

        internal double SellValue(GearPiece p) =>
            _k.GearSellKills * Content.GoldPerKill(p.Tier) * Content.GearRarity[(int)p.Rarity].Mult;

        // ================================================================ DPS (specs/03 section 8.4)

        internal void MarkDpsDirty() => _dpsDirty = true;

        internal double HitDamageCached
        {
            get
            {
                if (_dpsDirty) RecomputeDps();
                return _hit;
            }
        }

        internal double DpsCached { get { if (_dpsDirty) RecomputeDps(); return _dps; } }
        internal double StatMultCached { get { if (_dpsDirty) RecomputeDps(); return _statMult; } }
        internal double GearMultCached { get { if (_dpsDirty) RecomputeDps(); return _gearMult; } }
        internal double HeroMultCached { get { if (_dpsDirty) RecomputeDps(); return _heroMult; } }
        internal double ClassMultCached { get { if (_dpsDirty) RecomputeDps(); return _classMult; } }

        void RecomputeDps()
        {
            var s = State;
            _statMult = 1.0 + _k.StatPerLvl * s.Progression.StatLevel;
            double gear = 1.0;
            foreach (var p in s.Gear.Slots)
                if (p != null) gear *= 1.0 + GearBasePct(p) * (1.0 + _k.EnhPerLvl * p.Enhance);
            _gearMult = gear;
            double heroSum = 0;
            foreach (var id in s.Heroes.Equipped)
                if (id != null) heroSum += HeroPct(id);
            _heroMult = 1.0 + heroSum / 100.0;
            _classMult = Content.ClassMult(s.Progression.ClassAdvances);
            _dps = _k.BaseAtk * _statMult * _gearMult * _heroMult * _classMult;
            _hit = _dps * _k.AttackIntervalTicks / _k.TickHz;
            _dpsDirty = false;
        }

        internal double HeroPct(string id)
        {
            if (id == null || !State.Heroes.Copies.TryGetValue(id, out int copies) || copies <= 0) return 0;
            if (!Content.Heroes.TryGetValue(id, out var def)) return 0;
            return Content.HeroPct(def, Content.HeroLevel(copies));
        }
    }
}
