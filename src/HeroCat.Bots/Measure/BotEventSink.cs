using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Bots.Measure
{
    /// <summary>A wall: opens on the first BossFailed of a stage, closes on the StageAdvanced off that stage (specs/09 section 6.1).</summary>
    public sealed class WallRecord
    {
        public int Stage { get; set; }
        public long OpenTick { get; set; }
        public long CloseTick { get; set; } = -1;
        public long OpenUtcMs { get; set; }
        public long CloseUtcMs { get; set; } = -1;
        /// <summary>Bot day on which the wall opened (1-based).</summary>
        public int OpenDay { get; set; }

        public bool IsOpen => CloseTick < 0;
        public double WallClockHours(long nowUtcMs) => ((IsOpen ? nowUtcMs : CloseUtcMs) - OpenUtcMs) / 3_600_000.0;
        public double ActiveHours(long nowTick, int tickHz) => ((IsOpen ? nowTick : CloseTick) - OpenTick) / (double)tickHz / 3600.0;
    }

    /// <summary>Counts L+ pulls, tracks walls and offline credits. Reads state only to stamp wall-clock times.</summary>
    public sealed class BotEventSink : IEventSink
    {
        readonly GameState _state;
        readonly int _tickHz;
        readonly Dictionary<int, WallRecord> _openByStage = new Dictionary<int, WallRecord>();

        public BotEventSink(GameState state, int tickHz)
        {
            _state = state;
            _tickHz = tickHz;
        }

        public EventKinds Mask => EventKinds.BossFailed | EventKinds.BossCleared | EventKinds.StageAdvanced
                                | EventKinds.HeroPulled | EventKinds.OfflineGoldCredited;

        public int LPlus { get; private set; }
        public List<WallRecord> Walls { get; } = new List<WallRecord>();
        public int OfflineCredits { get; private set; }
        public int Day { get; set; }

        /// <summary>Wall-clock ms of the sim at tick t: LastSyncServerMs + (t - TickAtLastSync) * 1000 / TICK_HZ.</summary>
        public long UtcMsAt(long tick) => _state.Clock.LastSyncServerMs + (tick - _state.Clock.TickAtLastSync) * 1000L / _tickHz;

        public void Publish(SimEvent e)
        {
            switch (e)
            {
                case HeroPulled hp:
                    if (hp.Result.Tier >= Rarity.L) LPlus++;
                    break;
                case BossFailed bf:
                    if (bf.FailsOnStage == 1 && !_openByStage.ContainsKey(bf.Stage))
                    {
                        var w = new WallRecord { Stage = bf.Stage, OpenTick = bf.Tick, OpenUtcMs = UtcMsAt(bf.Tick), OpenDay = Day };
                        Walls.Add(w);
                        _openByStage[bf.Stage] = w;
                    }
                    break;
                case StageAdvanced sa:
                    if (_openByStage.TryGetValue(sa.FromStage, out var open))
                    {
                        open.CloseTick = sa.Tick;
                        open.CloseUtcMs = UtcMsAt(sa.Tick);
                        _openByStage.Remove(sa.FromStage);
                    }
                    break;
                case OfflineGoldCredited _:
                    OfflineCredits++;
                    break;
            }
        }
    }
}
