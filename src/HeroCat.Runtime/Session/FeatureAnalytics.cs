using System;
using System.Collections.Generic;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Fish;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Session
{
    /// <summary>
    /// The feature specs' analytics rows from the sim's events on the session's <see cref="EventBus"/> (02 section 14; the
    /// owner sections define the fields): 06 section 13's quest, offline, rollover and shop events and 03 section 11's
    /// <c>awakened</c> (issue #177), 04 section 13's <c>item_equipped</c> (issue #163) and 05 section 13's fish events
    /// (issue #168), and 03 section 11's combat rows (issue #274, FeatureAnalytics.Combat.cs). <c>currency_changed</c> is
    /// <see cref="CurrencyAnalytics"/>'. Rows that group one command's events (the
    /// <c>quest_claimed</c> bulk flag, <c>fish_release</c>, <c>fish_merge</c>, <c>fish_discard</c>) read
    /// <see cref="CommandApplied"/>; the rest map one sim event to one row; <c>fish_board_snapshot</c> goes out on the
    /// analytics flush of pause or quit. Attached by the boot flow whenever analytics is not <see cref="NullAnalytics"/>.
    /// Main thread, like the bus.
    /// </summary>
    public sealed partial class FeatureAnalytics : IDisposable
    {
        readonly IAnalytics _analytics;
        readonly ContentDb _db;
        readonly Func<GameState> _state;
        readonly List<IDisposable> _subs = new List<IDisposable>();
        readonly IAnalyticsFlushHook _hook;
        double _rockCandyAfterAwaken;

        // 05 section 13's fish_board_snapshot session totals, since the session opened (boot, or the first command after a snapshot).
        bool _sessionOpen;
        long _activeSecAtOpen, _releases, _merges, _discoveries;
        double _boardCoins;

        /// <param name="power">The current power breakdown (the boot flow passes the sim's cached <c>Query.Power</c>);
        /// null computes it from the state.</param>
        public FeatureAnalytics(EventBus bus, IAnalytics analytics, ContentDb db, Func<GameState> state,
                                Func<HeroCat.Sim.Power.PowerBreakdown> power = null)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            _analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _power = power;
            AttachCombat(bus);   // 03 section 11 (issue #274)

            _subs.Add(bus.SubscribeNotice<CommandApplied>(OnCommandApplied));

            // 06 section 13 (issue #177)
            _subs.Add(bus.Subscribe<QuestCompleted>(OnQuestCompleted));
            _subs.Add(bus.Subscribe<DayRolledOver>(e => Track(new AnalyticsEvents.DayRollover(e.DayKey, e.SkippedDays, e.SecondsSinceLastTick))));
            _subs.Add(bus.Subscribe<OfflineRewardComputed>(e =>
            {
                var p = e.Pending;
                Track(new AnalyticsEvents.OfflineCatchup(p.ElapsedSec, p.CreditedSec, p.Capped, p.Gold, p.Exp, p.TankFill, p.Stage));
            }));
            _subs.Add(bus.Subscribe<OfflineRewardClaimed>(e =>
            {
                var r = e.Reward;
                Track(new AnalyticsEvents.OfflineClaimed(r.ElapsedSec, r.CreditedSec, r.Capped, r.Gold, r.Exp, r.TankFill, X2: false));
            }));
            _subs.Add(bus.Subscribe<DailyGiftClaimed>(_ => Track(new AnalyticsEvents.DailyGiftClaimed())));
            _subs.Add(bus.Subscribe<ItemExchanged>(e => Track(new AnalyticsEvents.Exchange(e.OfferId, e.Qty, e.Diamonds))));
            // dungeon_run (issues #172, #173); the abandoned row is LocalSaveStore's, from the save validator's report (issue #146).
            _subs.Add(bus.Subscribe<DungeonCleared>(e =>
                Track(new AnalyticsEvents.DungeonRun(e.Dungeon, e.Floor, "cleared", e.TicksUsed, e.RewardCurrency, e.RewardAmount, e.GrantId))));
            _subs.Add(bus.Subscribe<DungeonFailed>(e =>
                Track(new AnalyticsEvents.DungeonRun(e.Dungeon, e.Floor, "failed", e.TicksUsed, null, 0, e.GrantId))));
            _subs.Add(bus.Subscribe<DungeonSwept>(e =>
                Track(new AnalyticsEvents.DungeonRun(e.Dungeon, e.Floor, "swept", 0, e.RewardCurrency, e.RewardAmount, ""))));

            // 03 section 11: Awaken debits first, so its CurrencyChanged(AWAKENING) arrives just before Awakened.
            _subs.Add(bus.Subscribe<CurrencyChanged>(e =>
            {
                if (e.Reason == CurrencyReason.AWAKENING && e.Currency == Currency.ROCK_CANDY) _rockCandyAfterAwaken = e.BalanceAfter;
            }));
            _subs.Add(bus.Subscribe<Awakened>(OnAwakened));

            // 04 section 13 (issue #163). summon and item_acquired come from SummonFlow, which knows the request.
            _subs.Add(bus.Subscribe<ItemEquipped>(e => Track(new AnalyticsEvents.ItemEquipped(e.Banner, e.Slot, e.ItemId, e.PreviousItemId, e.Auto))));

            // 05 section 13 (issue #168). fish_release, fish_merge and fish_discard group one command's events (OnCommandApplied).
            _subs.Add(bus.Subscribe<FishDiscovered>(e =>
            {
                _discoveries++;
                Track(new AnalyticsEvents.FishDiscovered(e.Level, e.Coins));
            }));
            _subs.Add(bus.Subscribe<FishReleased>(_ => _releases++));
            _subs.Add(bus.Subscribe<FishMerged>(_ => _merges++));
            _subs.Add(bus.Subscribe<CurrencyChanged>(e =>
            {
                if (e.Currency == Currency.FISH_COIN && (e.Reason == CurrencyReason.FISH_MERGE || e.Reason == CurrencyReason.FISH_DISCOVERY))
                    _boardCoins += e.Delta;
            }));
            _hook = analytics as IAnalyticsFlushHook;
            if (_hook != null) _hook.Flushing += OnSessionEnd;
            OpenSession();
        }

        /// <summary>Rows emitted so far (diagnostics).</summary>
        public long RowsEmitted { get; private set; }

        void OnCommandApplied(CommandApplied n)
        {
            OpenSession();
            bool bulk = n.Command is ClaimAllQuests;
            int released = 0, stockAfter = 0;
            int merged = 0;
            double mergeCoins = 0;
            SortedDictionary<int, int> levels = null;
            List<int> discovered = null;
            foreach (var e in n.Events)
            {
                switch (e)
                {
                    case QuestClaimed q:
                        OnQuestClaimed(q, bulk);
                        break;
                    case FishReleased r:
                        released++;
                        stockAfter = r.StockAfter;
                        break;
                    case FishMerged m:
                        merged++;
                        mergeCoins += m.Coins;
                        levels ??= new SortedDictionary<int, int>();
                        levels.TryGetValue(m.NewLevel, out int c);
                        levels[m.NewLevel] = c + 1;
                        if (m.Discovered) (discovered ??= new List<int>()).Add(m.NewLevel);
                        break;
                    case FishDiscarded d:
                        Track(new AnalyticsEvents.FishDiscard(d.Level, d.Coins, Occupancy() + 1));   // the state is just after this command
                        break;
                }
            }
            if (released > 0)   // a ReleaseMax that released nothing sends no row
                Track(new AnalyticsEvents.FishRelease(released, stockAfter + released, stockAfter, Occupancy(), ViaButton: n.Command is ReleaseMax));
            if (merged > 0)
            {
                var hist = new List<AnalyticsEvents.FishLevelCount>(levels.Count);
                foreach (var kv in levels) hist.Add(new AnalyticsEvents.FishLevelCount(kv.Key, kv.Value));
                var fight = _state()?.Combat.Fight;
                Track(new AnalyticsEvents.FishMerge(merged, hist, mergeCoins, (IReadOnlyList<int>)discovered ?? Array.Empty<int>(),
                                                    ViaButton: n.Command is MergeAll, DuringBoss: fight != null && fight.EnemyIsBoss));
            }
        }

        int Occupancy()
        {
            var cells = _state()?.Fish.Cells;
            if (cells == null) return 0;
            int n = 0;
            foreach (int c in cells) if (c != 0) n++;
            return n;
        }

        void OpenSession()
        {
            if (_sessionOpen) return;
            _sessionOpen = true;
            _activeSecAtOpen = _state()?.Stats.ActiveSeconds ?? 0;
            _releases = _merges = _discoveries = 0;
            _boardCoins = 0;
        }

        /// <summary>
        /// 05 section 13's <c>fish_board_snapshot</c>, once per session: on the analytics flush of pause or quit (a quit
        /// right after a pause sends nothing more, since no command reopened the session).
        /// </summary>
        public void OnSessionEnd()
        {
            if (!_sessionOpen) return;
            _sessionOpen = false;
            var s = _state();
            if (s == null) return;
            double dpsPct = FishBoard.BoardDpsPct(s, _db);
            Track(new AnalyticsEvents.FishBoardSnapshot(
                (int[])s.Fish.Cells.Clone(), dpsPct, 1 + dpsPct / 100, FishBoard.TankCap(s, _db), FishBoard.TankRefillSeconds(s, _db),
                FishBoard.FishMaxLvl(s, _db), s.Fish.AutoMergeEnabled, _releases, _merges, _discoveries, _boardCoins,
                s.Stats.ActiveSeconds - _activeSecAtOpen));
        }

        void OnQuestCompleted(QuestCompleted e)
        {
            var def = _db.Quest(e.QuestId);
            if (def == null) return;
            if (def.Kind == QuestKind.ACHIEVEMENT) Track(new AnalyticsEvents.AchievementUnlocked(def.Id, def.Rung));
            else Track(new AnalyticsEvents.QuestCompleted(def.Id, def.Kind));
        }

        void OnQuestClaimed(QuestClaimed e, bool bulk)
        {
            var def = _db.Quest(e.QuestId);
            var kind = def?.Kind ?? QuestKind.DAILY;
            Track(new AnalyticsEvents.QuestClaimed(e.QuestId, kind, e.Currency, e.Amount, bulk));
        }

        void OnAwakened(Awakened e)
        {
            int i = e.Rank - 1;
            long cost = i >= 0 && i < _db.Awakening.Count ? _db.Awakening[i].RockCandyCost : 0;
            Track(new AnalyticsEvents.Awakened(e.Rank, cost, _rockCandyAfterAwaken));
        }

        void Track(AnalyticsEvent e)
        {
            _analytics.Track(e);
            RowsEmitted++;
        }

        public void Dispose()
        {
            foreach (var d in _subs) d.Dispose();
            _subs.Clear();
            if (_hook != null) _hook.Flushing -= OnSessionEnd;
        }
    }
}
