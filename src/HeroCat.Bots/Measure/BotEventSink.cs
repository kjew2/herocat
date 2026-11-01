using System;
using System.Collections.Generic;
using HeroCat.Runtime.Session;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;

namespace HeroCat.Bots.Measure
{
    /// <summary>
    /// A wall (10 section 3.5): opens on the first BossFailed at a stage, closes on the StageCleared that leaves it.
    /// Wall-clock hours include the gaps between sessions (what the player feels); active minutes count ticks only.
    /// </summary>
    public sealed class Wall
    {
        public int Stage { get; internal set; }
        public int OpenedDay { get; internal set; }
        public long OpenTick { get; internal set; }
        public long OpenServerSec { get; internal set; }
        public int ClosedDay { get; internal set; }        // 0 while open
        public long CloseTick { get; internal set; }
        public long CloseServerSec { get; internal set; }
        public int Fails { get; internal set; }
        /// <summary>The first layer whose multiplier rose by more than 10% in the active hour before the clear (10 section 3.7); "-" if none.</summary>
        public string UnblockedBy { get; internal set; }

        public bool IsOpen => ClosedDay == 0;

        public double ActiveMinutes(long nowTick, int tickHz) => ((IsOpen ? nowTick : CloseTick) - OpenTick) / (double)tickHz / 60.0;

        public double WallClockHours(long nowServerSec) => ((IsOpen ? nowServerSec : CloseServerSec) - OpenServerSec) / 3600.0;
    }

    /// <summary>A purchase (10 section 3.4): the acceptance table's "diamonds spent" separates earned from bought.</summary>
    public sealed record SpendRecord(int Day, string Sku, double Usd, long Diamonds);

    /// <summary>
    /// Everything the harness measures from events (10 section 3.5): walls, pulls, L+ count, highest rarity, gold spent,
    /// diamonds earned by reason and spent, and a per-active-minute history of the power factors for the unblock report.
    /// Subscribed to the session's EventBus, so it sees exactly what the client would (00 contract 5).
    /// </summary>
    public sealed class BotEventSink
    {
        readonly List<Wall> _walls = new List<Wall>();
        readonly List<SpendRecord> _spend = new List<SpendRecord>();
        readonly Dictionary<(int day, CurrencyReason reason), long> _diamondsByReason = new Dictionary<(int, CurrencyReason), long>();
        readonly List<PowerBreakdown> _minutes = new List<PowerBreakdown>();
        Simulation _sim;
        Wall _open;

        public const int UnblockWindowMinutes = 60;
        public const double UnblockRise = 1.10;

        public IReadOnlyList<Wall> Walls => _walls;
        public IReadOnlyList<SpendRecord> Spend => _spend;

        /// <summary>Diamonds earned per (bot day, reason); sums to the day rows' DiamondsEarned.</summary>
        public IReadOnlyDictionary<(int day, CurrencyReason reason), long> DiamondsByReason => _diamondsByReason;

        public int Day { get; set; }
        public int ItemsAcquired { get; private set; }
        public int LPlus { get; private set; }
        public Rarity? HighestRarity { get; private set; }
        public double GoldSpent { get; private set; }
        public long DiamondsEarned { get; private set; }
        public long DiamondsSpent { get; private set; }
        public double UsdSpent { get; private set; }

        public void Attach(EventBus bus, Simulation sim)
        {
            _sim = sim;
            bus.Subscribe<Event>(e => OnEvent(e));
        }

        /// <summary>
        /// The profile-created grant (TUTORIAL_DIAMONDS, reason TUTORIAL) is applied by ProfileFactory before a Simulation
        /// exists, so no event reports it; the runner counts the genesis balance as day-1 TUTORIAL income here.
        /// </summary>
        public void CountGenesisDiamonds(long diamonds)
        {
            if (diamonds <= 0) return;
            DiamondsEarned += diamonds;
            _diamondsByReason.TryGetValue((1, CurrencyReason.TUTORIAL), out long had);
            _diamondsByReason[(1, CurrencyReason.TUTORIAL)] = had + diamonds;
        }

        /// <summary>Logs a purchase (10 section 3.4).</summary>
        public void Purchase(string sku, double usd, long diamonds)
        {
            _spend.Add(new SpendRecord(Day, sku, usd, diamonds));
            UsdSpent += usd;
        }

        /// <summary>Called once per active minute (after the policy's decision) with the current factors.</summary>
        public void OnActiveMinute(PowerBreakdown p) => _minutes.Add(p);

        public void OnEvent(Event e) => OnEvent(e, e.Tick);

        /// <summary>Handles <paramref name="e"/> as emitted at <paramref name="tick"/> (tests script events with their own ticks).</summary>
        public void OnEvent(Event e, long tick)
        {
            switch (e)
            {
                case BossFailed f:
                    if (_open == null || _open.Stage != f.Stage)
                    {
                        _open = new Wall { Stage = f.Stage, OpenedDay = Day, OpenTick = tick, OpenServerSec = ServerSecAt(tick) };
                        _walls.Add(_open);
                    }
                    _open.Fails++;
                    break;
                case StageCleared c:
                    if (_open != null && c.From == _open.Stage)
                    {
                        _open.ClosedDay = Day;
                        _open.CloseTick = tick;
                        _open.CloseServerSec = ServerSecAt(tick);
                        _open.UnblockedBy = Unblocker();
                        _open = null;
                    }
                    break;
                case ItemAcquired a:
                    ItemsAcquired++;
                    if (a.Rarity >= Rarity.LEGENDARY) LPlus++;
                    if (!HighestRarity.HasValue || a.Rarity > HighestRarity.Value) HighestRarity = a.Rarity;
                    break;
                case CurrencyChanged cc:
                    if (cc.Currency == Currency.GOLD && cc.Delta < 0) GoldSpent -= cc.Delta;
                    else if (cc.Currency == Currency.DIAMOND)
                    {
                        long d = (long)Math.Round(cc.Delta);
                        if (d < 0) DiamondsSpent -= d;
                        else if (d > 0 && cc.Reason != CurrencyReason.IAP)
                        {
                            DiamondsEarned += d;
                            _diamondsByReason.TryGetValue((Day, cc.Reason), out long had);
                            _diamondsByReason[(Day, cc.Reason)] = had + d;
                        }
                    }
                    break;
            }
        }

        /// <summary>The wall still open, if any.</summary>
        public Wall OpenWall => _open;

        /// <summary>Server time at a tick of the current session: the virtual clock (01 section 3.1).</summary>
        long ServerSecAt(long tick)
        {
            var t = _sim.State.Time;
            return t.AnchorServerTime + (tick - t.AnchorTick) / _sim.Db.Constants.TickHz;
        }

        string Unblocker()
        {
            if (_minutes.Count == 0) return "-";
            var now = _sim.Query.Power;
            var then = _minutes[Math.Max(0, _minutes.Count - UnblockWindowMinutes)];
            foreach (var name in LayerShares.Names)
            {
                if (name == "Base") continue;
                double a = LayerShares.Factor(then, name), b = LayerShares.Factor(now, name);
                if (a > 0 && b / a > UnblockRise) return name;
            }
            return "-";
        }
    }
}
