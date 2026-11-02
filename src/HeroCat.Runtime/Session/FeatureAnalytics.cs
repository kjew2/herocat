using System;
using System.Collections.Generic;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Session
{
    /// <summary>
    /// The feature specs' analytics rows from the sim's events on the session's <see cref="EventBus"/> (02 section 14; the
    /// owner sections define the fields): 06 section 13's quest, offline, rollover and shop events and 03 section 11's
    /// <c>awakened</c> (issue #177). <c>currency_changed</c> is <see cref="CurrencyAnalytics"/>'. Rows that group one
    /// command's events (the <c>quest_claimed</c> bulk flag) read <see cref="CommandApplied"/>; the rest map one sim event to
    /// one row. Attached by the boot flow whenever analytics is not <see cref="NullAnalytics"/>. Main thread, like the bus.
    /// </summary>
    public sealed class FeatureAnalytics : IDisposable
    {
        readonly IAnalytics _analytics;
        readonly ContentDb _db;
        readonly Func<GameState> _state;
        readonly List<IDisposable> _subs = new List<IDisposable>();
        double _rockCandyAfterAwaken;

        public FeatureAnalytics(EventBus bus, IAnalytics analytics, ContentDb db, Func<GameState> state)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            _analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _state = state ?? throw new ArgumentNullException(nameof(state));

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

            // 03 section 11: Awaken debits first, so its CurrencyChanged(AWAKENING) arrives just before Awakened.
            _subs.Add(bus.Subscribe<CurrencyChanged>(e =>
            {
                if (e.Reason == CurrencyReason.AWAKENING && e.Currency == Currency.ROCK_CANDY) _rockCandyAfterAwaken = e.BalanceAfter;
            }));
            _subs.Add(bus.Subscribe<Awakened>(OnAwakened));
        }

        /// <summary>Rows emitted so far (diagnostics).</summary>
        public long RowsEmitted { get; private set; }

        void OnCommandApplied(CommandApplied n)
        {
            bool bulk = n.Command is ClaimAllQuests;
            foreach (var e in n.Events)
                if (e is QuestClaimed q) OnQuestClaimed(q, bulk);
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
        }
    }
}
