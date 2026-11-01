using System;
using System.Collections.Generic;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Session
{
    /// <summary>
    /// 06 section 13's <c>currency_changed</c> from the sim's CurrencyChanged events (issue #155; 00 contract 6). Every reason
    /// but KILL and BOSS is one row per apply. KILL and BOSS deltas are summed per (currency, reason) over a window of
    /// CURRENCY_AGG_SEC of sim time that opens at the first such apply, and emitted once, with <c>balanceAfter</c> read from
    /// the state at emission: when an event arrives past the window, and when the analytics sink flushes (pause, quit;
    /// <see cref="IAnalyticsFlushHook"/>). Coalesced kills (01 section 2.3) count through EnemyKilledBatch's KillGold and
    /// KillExp, so the rows' deltas sum to the wallet delta under coalescing too. Main thread, like the EventBus.
    /// </summary>
    public sealed class CurrencyAnalytics : IDisposable
    {
        sealed class Sum
        {
            public Currency Currency;
            public CurrencyReason Reason;
            public double Delta;
        }

        readonly IAnalytics _analytics;
        readonly Func<GameState> _state;
        readonly long _windowTicks;
        readonly List<Sum> _sums = new List<Sum>();
        readonly List<IDisposable> _subs = new List<IDisposable>();
        readonly IAnalyticsFlushHook _hook;
        bool _open;
        long _openTick;

        /// <param name="tickHz">TICK_HZ, to turn CURRENCY_AGG_SEC into ticks.</param>
        public CurrencyAnalytics(EventBus bus, IAnalytics analytics, Func<GameState> state, int tickHz,
                                 int aggregateSeconds = RuntimeConfig.CURRENCY_AGG_SEC)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            _analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            if (tickHz <= 0 || aggregateSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(tickHz));
            _windowTicks = (long)tickHz * aggregateSeconds;
            _subs.Add(bus.Subscribe<CurrencyChanged>(OnCurrencyChanged));
            _subs.Add(bus.Subscribe<EnemyKilledBatch>(OnBatch));
            _hook = analytics as IAnalyticsFlushHook;
            if (_hook != null) _hook.Flushing += Flush;
        }

        /// <summary>Rows emitted so far (diagnostics).</summary>
        public long RowsEmitted { get; private set; }

        public static bool IsAggregated(CurrencyReason r) => r == CurrencyReason.KILL || r == CurrencyReason.BOSS;

        void OnCurrencyChanged(CurrencyChanged e)
        {
            if (IsAggregated(e.Reason))
            {
                Add(e.Currency, e.Reason, e.Delta, e.Tick);
                return;
            }
            CloseIfDue(e.Tick);
            Emit(new AnalyticsEvents.CurrencyChanged(e.Currency, e.Delta, e.BalanceAfter, e.Reason));
        }

        void OnBatch(EnemyKilledBatch b)
        {
            if (b.KillGold != 0) Add(Currency.GOLD, CurrencyReason.KILL, b.KillGold, b.Tick);
            if (b.KillExp != 0) Add(Currency.HERO_EXP, CurrencyReason.KILL, b.KillExp, b.Tick);
        }

        void Add(Currency c, CurrencyReason r, double delta, long tick)
        {
            CloseIfDue(tick);
            if (!_open)
            {
                _open = true;
                _openTick = tick;
            }
            foreach (var s in _sums)
                if (s.Currency == c && s.Reason == r)
                {
                    s.Delta += delta;
                    return;
                }
            _sums.Add(new Sum { Currency = c, Reason = r, Delta = delta });
        }

        void CloseIfDue(long tick)
        {
            if (_open && tick - _openTick >= _windowTicks) Flush();
        }

        /// <summary>Emits the open window's rows now (one per currency and reason) and closes it.</summary>
        public void Flush()
        {
            if (!_open) return;
            _open = false;
            var s = _state();
            foreach (var sum in _sums)
            {
                double balance = s == null ? 0 : sum.Currency == Currency.HERO_EXP ? s.Hero.Exp : s.Wallet.Get(sum.Currency);
                Emit(new AnalyticsEvents.CurrencyChanged(sum.Currency, sum.Delta, balance, sum.Reason));
            }
            _sums.Clear();
        }

        void Emit(AnalyticsEvents.CurrencyChanged row)
        {
            _analytics.Track(row);
            RowsEmitted++;
        }

        public void Dispose()
        {
            Flush();
            foreach (var d in _subs) d.Dispose();
            _subs.Clear();
            if (_hook != null) _hook.Flushing -= Flush;
        }
    }
}
