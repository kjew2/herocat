using System;
using System.Collections.Generic;
using HeroCat.Runtime.Services;
using HeroCat.Sim;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Session
{
    /// <summary>
    /// 03 section 11's combat rows (issue #274): <c>stage_cleared</c>, <c>boss_failed</c>, <c>wall_broken</c>,
    /// <c>training</c>, <c>level_up</c> and <c>research_bought</c>. What the sim's events do not carry (the stage-entry
    /// tick, the wall's start and its power factors, the boss fights on a stage) is tracked here from the events, seeded
    /// from the state when attached; nothing is saved, so a stage or wall entered before this process reports no timing.
    /// </summary>
    public sealed partial class FeatureAnalytics
    {
        readonly Func<PowerBreakdown> _power;

        int _bossFights;              // BossFailed count on the frontier stage (Combat.BossFailsOnStage as last seen)
        int _highestCleared;          // Progress.HighestCleared as last seen: StageCleared above it is a first clear
        long _stageEnteredTick = -1;  // -1: the frontier stage was entered before this process attached
        int _firstClearStage;         // StageFirstCleared seen for this stage, not yet consumed by its StageCleared
        long _firstClearDiamonds;

        int _wallStage;               // 0: no open wall
        bool _wallKnown;              // false: the wall opened before this process attached
        long _wallOpenTick, _wallOpenServerSec;
        PowerBreakdown _wallPower;

        void AttachCombat(EventBus bus)
        {
            var s = _state();
            if (s != null)
            {
                _bossFights = s.Combat.BossFailsOnStage;
                _highestCleared = s.Progress.HighestCleared;
                if (s.Time.Tick == 0) _stageEnteredTick = 0;   // a new profile: the stage was entered at tick 0
                if (s.Combat.Boss == BossPhase.Walled) _wallStage = s.Combat.Stage;
            }
            _subs.Add(bus.Subscribe<StageFirstCleared>(e =>
            {
                _firstClearStage = e.Stage;
                _firstClearDiamonds = e.Diamonds;
            }));
            _subs.Add(bus.Subscribe<BossFailed>(OnBossFailed));
            _subs.Add(bus.Subscribe<StageCleared>(OnStageCleared));
            _subs.Add(bus.Subscribe<StatTrained>(e => Track(new AnalyticsEvents.Training(e.Stat, e.LevelFrom, e.NewLevel, e.GoldCost))));
            _subs.Add(bus.Subscribe<LevelUp>(e => Track(new AnalyticsEvents.LevelUp(e.Level))));
            _subs.Add(bus.Subscribe<ResearchLeveled>(e => Track(new AnalyticsEvents.ResearchBought(e.NodeId, e.NewLevel, e.Cost))));
        }

        PowerBreakdown Power() => _power != null ? _power() : PowerFormula.Compute(_db, _state());

        void OnBossFailed(BossFailed e)
        {
            _bossFights = e.Attempt;
            Track(new AnalyticsEvents.BossFailed(e.Stage, e.RemainingFraction * Units.Percent, e.Attempt));
            if (_wallStage == e.Stage) return;
            _wallStage = e.Stage;   // the first fail on a stage is the wall hit (03 section 2.8)
            _wallKnown = true;
            _wallOpenTick = e.Tick;
            _wallOpenServerSec = ServerSecAt(e.Tick);
            _wallPower = Power();
        }

        void OnStageCleared(StageCleared e)
        {
            int stage = e.From;
            int fights = _bossFights + 1;   // the failed fights and the clearing one
            bool first = stage > _highestCleared;
            if (first) _highestCleared = stage;
            long diamonds = _firstClearStage == stage ? _firstClearDiamonds : 0;
            long? ticks = _stageEnteredTick >= 0 ? e.Tick - _stageEnteredTick : (long?)null;
            var power = Power();
            Track(new AnalyticsEvents.StageCleared(stage, e.ViaBoss, first, diamonds, e.ViaBoss ? fights : (int?)null, ticks, power.RatedDps));

            if (_wallStage == stage)
            {
                if (_wallKnown)
                {
                    var changed = new List<string>();
                    foreach (PowerLayer l in Enum.GetValues(typeof(PowerLayer)))
                    {
                        if (l == PowerLayer.Base) continue;
                        double a = _wallPower.Factor(l), b = power.Factor(l);
                        if (Math.Abs(b - a) > 1e-12 * Math.Max(1.0, Math.Abs(a))) changed.Add(l.ToString());
                    }
                    Track(new AnalyticsEvents.WallBroken(stage, ServerSecAt(e.Tick) - _wallOpenServerSec,
                                                         (e.Tick - _wallOpenTick) / (double)_db.Constants.TickHz, fights, changed));
                }
                else Track(new AnalyticsEvents.WallBroken(stage, null, null, fights, null));
                _wallStage = 0;
                _wallKnown = false;
            }

            _bossFights = 0;
            _stageEnteredTick = e.Tick;
            _firstClearStage = 0;
            _firstClearDiamonds = 0;
        }

        /// <summary>Server seconds at a tick: the virtual clock (01 section 3.1).</summary>
        long ServerSecAt(long tick)
        {
            var t = _state()?.Time;
            if (t == null) return 0;
            return t.AnchorServerTime + (tick - t.AnchorTick) / _db.Constants.TickHz;
        }
    }
}
