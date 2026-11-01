using HeroCat.Sim;
using HeroCat.Sim.Events;

namespace HeroCat.Bots.Measure
{
    /// <summary>
    /// The Fish Board columns of a bot day (10 section 3.5, 05 section 8.2; issue #105), on tools/fish_model.py's definitions:
    /// fish released, merges, discards, Fish Coins by source, the day's missile hits, and the end-of-day
    /// <see cref="FishMult"/> = 1 + (FISH_BOARD_DPS_PCT + missileDpsPct) / 100, where missileDpsPct is the day's missile
    /// hits per hero attack over the day's active time (speed factor 1, as the model).
    /// </summary>
    public sealed record FishDayStats(
        int Released, int Merges, int Discards, double CoinsDiscovery, double CoinsMerge, double CoinsDiscard,
        double MissileHits, double BoardDpsPct, double MissileDpsPct, int HighestLevel)
    {
        public double CoinsBoard => CoinsDiscovery + CoinsMerge + CoinsDiscard;
        public double FishMult => 1.0 + (BoardDpsPct + MissileDpsPct) / 100.0;
    }

    /// <summary>Counts one bot day's board events (the sink feeds it; <see cref="Take"/> closes the day).</summary>
    public sealed class FishDayCounter
    {
        int _released, _merges, _discards;
        double _discovery, _merge, _discard, _missile;

        public void On(Event e)
        {
            switch (e)
            {
                case FishReleased _: _released++; break;
                case FishMerged m:
                    _merges++;
                    _missile += m.MissilePct / 100.0;   // one hero hit of MissilePct percent per merge (05 section 5.1)
                    if (!m.Discovered) _merge += m.Coins;   // a discovering merge pays discovery coins instead (FishDiscovered)
                    break;
                case FishDiscovered d: _discovery += d.Coins; break;
                case FishDiscarded x: _discards++; _discard += x.Coins; break;
            }
        }

        /// <param name="activeSeconds">The day's active play, for missileDpsPct.</param>
        public FishDayStats Take(Simulation sim, double activeSeconds)
        {
            var k = sim.Db.Constants;
            double attacks = activeSeconds * k.TickHz / k.AttackIntervalTicks;
            double missilePct = attacks > 0 ? 100.0 * _missile / attacks : 0.0;
            var day = new FishDayStats(_released, _merges, _discards, _discovery, _merge, _discard, _missile, sim.FishBoardDpsPct, missilePct,
                                       sim.State.Fish.HighestLevel);
            _released = _merges = _discards = 0;
            _discovery = _merge = _discard = _missile = 0;
            return day;
        }
    }
}
