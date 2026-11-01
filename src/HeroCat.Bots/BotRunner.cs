using System;
using HeroCat.Bots.Measure;
using HeroCat.Bots.Policies;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Services;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;

namespace HeroCat.Bots
{
    public sealed class RunResult
    {
        public string Bot { get; set; }
        public ulong Seed { get; set; }
        public int Days { get; set; }
        public string ContentVersion { get; set; }
        public string ContentHash { get; set; }
        public DayRow[] Rows { get; set; }
    }

    /// <summary>
    /// Plays N bot days: 120 active minutes in 60 s decisions, then one night sync (ApplyServerTime and
    /// ComputeOfflineCatchup). Every mutation goes through Simulation.Apply / Advance. TODO(#134): 10 section 3.3's
    /// two-session day with alternating gaps.
    /// </summary>
    public sealed class BotRunner
    {
        readonly ContentDb _content;

        public BotRunner(ContentDb content) { _content = content; }

        /// <summary>Optional hook called after each day's snapshot (tests).</summary>
        public Action<int, BotContext> AfterDay { get; set; }

        /// <summary>Optional command log (replay tests, TODO(#138) golden replay). Null: the no-op log.</summary>
        public ICommandLog CommandLog { get; set; }

        /// <summary>The state a run for <paramref name="seed"/> starts from; replaying its command log on this reproduces it.</summary>
        public static GameState Genesis(ContentDb content, ulong seed) =>
            ProfileFactory.Create(content, Guid.Empty, HarnessConstants.Epoch.ToUnixTimeSeconds(), HarnessConstants.BotDeviceId,
                                  HarnessConstants.RngRootFor(seed));

        public RunResult Run(IBotPolicy policy, ulong seed, int days)
        {
            var k = _content.Constants;
            long epochSec = HarnessConstants.Epoch.ToUnixTimeSeconds();
            var clock = new FakeClock(epochSec);
            var sim = new Simulation(Genesis(_content, seed), _content) { CommandLog = CommandLog, ForceCoalesce = true };
            var ctx = new BotContext(sim, clock);
            var rows = new DayRow[days];

            sim.Apply(new ApplyServerTime(clock.NowServerSeconds));   // rolls day 1 and pays the first login
            int ticksPerDecision = HarnessConstants.DecisionIntervalSeconds * k.TickHz;
            int decisionsPerDay = HarnessConstants.ActiveMinutesPerDay * 60 / HarnessConstants.DecisionIntervalSeconds;

            for (int day = 1; day <= days; day++)
            {
                ctx.Day = day;
                long dayStart = epochSec + (long)(day - 1) * k.DaySeconds;
                policy.OnDayStart(ctx);
                for (int d = 0; d < decisionsPerDay; d++)
                {
                    sim.Advance(ticksPerDecision);
                    clock.Advance(HarnessConstants.DecisionIntervalSeconds);
                    policy.Decide(ctx);
                    sim.DrainEvents();
                }

                clock.Set(dayStart + k.DaySeconds);
                sim.Apply(new ApplyServerTime(clock.NowServerSeconds));     // re-anchors after the gap; rolls the day
                sim.Apply(new ComputeOfflineCatchup(clock.NowServerSeconds));
                sim.DrainEvents();
                rows[day - 1] = Snapshot(day, sim);
                AfterDay?.Invoke(day, ctx);
            }

            return new RunResult
            {
                Bot = policy.Name, Seed = seed, Days = days, ContentVersion = _content.Version, ContentHash = _content.ContentHash, Rows = rows,
            };
        }

        static DayRow Snapshot(int day, Simulation sim)
        {
            var s = sim.State;
            long pulls = 0;
            foreach (var b in s.Banners.Values) pulls += b.TotalPulls;
            return new DayRow(day, s.Combat.Stage, s.Progress.HighestCleared, s.Hero.Level, s.Wallet.Gold, s.Wallet.Diamond, s.Wallet.FishCoin,
                              pulls, sim.StateHash());
        }
    }
}
