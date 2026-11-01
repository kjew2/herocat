using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Bots.Measure;
using HeroCat.Bots.Policies;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;

namespace HeroCat.Bots
{
    /// <summary>Where the bot's diamonds come from (10 section 3.3, 00 decision 25).</summary>
    public enum DiamondMode
    {
        /// <summary>The sim's own quests, first clears and login pay (default).</summary>
        Content,
        /// <summary>DIAMONDS_PER_DAY_F2P granted at day start, for parity with tools/power_curve_model.py.</summary>
        Flat,
    }

    /// <summary>The StateHash after one day's first catch-up (10 section 2: the golden seed7-hashes.txt rows).</summary>
    public sealed record DayHash(int Day, long Tick, string StateHash);

    public sealed class RunResult
    {
        public string Bot { get; set; }
        public ulong Seed { get; set; }
        public int Days { get; set; }
        public string ContentVersion { get; set; }
        public string ContentHash { get; set; }
        public DiamondMode Diamonds { get; set; }
        public DayRow[] Rows { get; set; }
        public IReadOnlyList<Wall> Walls { get; set; }
        public IReadOnlyList<SpendRecord> Spend { get; set; }
        public IReadOnlyDictionary<(int day, CurrencyReason reason), long> DiamondsByReason { get; set; }
        public IReadOnlyList<DayHash> DayHashes { get; set; }
        public long FinalTick { get; set; }
        public long FinalServerSec { get; set; }
        public string FinalHash { get; set; }
        public int TickHz { get; set; }
    }

    /// <summary>
    /// The save seam for bots: writes nothing. A real store stamps <c>LastTickServerTime</c> and <c>SaveSeq</c> outside
    /// Simulation.Apply on every save (02 section 8.2), which the command log cannot replay; with this store every state
    /// change of a bot run is in the log, so the golden replay (10 section 2) reproduces the day hashes exactly. The save
    /// round trip is exercised by <c>save-fixture</c> instead.
    /// </summary>
    public sealed class NullSaveStore : ISaveStore
    {
        public Task<LoadResult> LoadAsync(long nowServerSec, ContentDb db, CancellationToken ct) =>
            throw new NotSupportedException("the bot harness creates profiles, it never loads one");

        public void Save(GameState state, SaveTrigger trigger) { }

        public Task ResolveConflictAsync(GameState chosen, GameState rejected, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Plays N bot days (10 section 3.3): per day two sessions, each a resume through Runtime's
    /// <see cref="SessionRunner"/> (logged ApplyServerTime + ComputeOfflineCatchup: rollover, catch-up), the policy's
    /// session start (which claims the offline reward), 60 decisions of DecisionIntervalSeconds of ticks, the session end, a suspend (OnPause), then the
    /// alternating gap. The runner is the only tick driver (issue #65); every mutation goes through Simulation.Apply.
    /// </summary>
    public sealed class BotRunner
    {
        readonly ContentDb _content;

        public BotRunner(ContentDb content) { _content = content ?? throw new ArgumentNullException(nameof(content)); }

        public DiamondMode Diamonds { get; set; } = DiamondMode.Content;

        /// <summary>Optional hook called after each day's snapshot (tests, the save fixture).</summary>
        public Action<int, BotContext> AfterDay { get; set; }

        /// <summary>Optional command log (replay tests, golden --record). Null: the no-op log.</summary>
        public ICommandLog CommandLog { get; set; }

        public static long EpochSeconds => HarnessConstants.Epoch.ToUnixTimeSeconds();

        /// <summary>The state a run for <paramref name="seed"/> starts from; replaying its command log on this reproduces it.</summary>
        public static GameState Genesis(ContentDb content, ulong seed)
        {
            var id = HarnessConstants.PlayerIdFor(seed);
            return ProfileFactory.Create(content, id, EpochSeconds, HarnessConstants.BotDeviceId, id);
        }

        /// <summary>A Simulation on the seed's genesis as the harness configures it (coalesced kills, 01 section 2.3).</summary>
        public static Simulation NewSimulation(ContentDb content, ulong seed) => new Simulation(Genesis(content, seed), content) { ForceCoalesce = true };

        /// <summary>
        /// The harness wiring for one profile (10 section 3.3): the seed's genesis at the epoch, a FakeClock, the Local
        /// seams (LocalSummonService and friends, a save store that writes nothing), an EventBus with the sink attached,
        /// and the SessionRunner that owns the Simulation. Not started: <see cref="Run"/> (or a test) calls Start.
        /// </summary>
        public static BotContext CreateContext(ContentDb content, ulong seed, ICommandLog log = null)
        {
            var clock = new FakeClock(EpochSeconds);
            var sim = NewSimulation(content, seed);
            var services = GameServices.Build(BackendMode.Local, new ServicesOptions
            {
                Clock = clock,
                Saves = new NullSaveStore(),
                Analytics = NullAnalytics.Instance,
                State = () => sim.State,
                Content = content,
                InstallId = HarnessConstants.BotDeviceId,
            });
            var bus = new EventBus();
            var runner = new SessionRunner(sim, services, bus, log, new SessionRunnerOptions { Mono = new FakeMonoClock(), TickBudgetMs = 0 });
            var sink = new BotEventSink();
            sink.Attach(bus, sim);
            sink.CountGenesisDiamonds(sim.State.Wallet.Diamond);
            return new BotContext(runner, clock, sink, seed);
        }

        public RunResult Run(IBotPolicy policy, ulong seed, int days)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));
            var k = _content.Constants;
            var ctx = CreateContext(_content, seed, CommandLog);
            var clock = ctx.Clock;
            var sim = ctx.Sim;
            var runner = ctx.Runner;
            var sink = ctx.Sink;
            var rows = new DayRow[days];
            var hashes = new List<DayHash>(days);
            int ticksPerDecision = HarnessConstants.DecisionIntervalSeconds * k.TickHz;   // 600
            int decisionsPerSession = HarnessConstants.DecisionsPerSession;               // 60

            for (int day = 1; day <= days; day++)
            {
                ctx.Day = day;
                sink.Day = day;
                for (int s = 0; s < HarnessConstants.SessionsPerDay; s++)
                {
                    ctx.Session = s;
                    if (day == 1 && s == 0) runner.Start();   // boot: ApplyServerTime (first rollover, login) + catch-up
                    else runner.OnResume();                 // resume: re-anchor, rollover, ComputeOfflineCatchup (01 section 8.2)
                    if (s == 0)
                    {
                        hashes.Add(new DayHash(day, sim.Tick, sim.StateHash()));
                        if (Diamonds == DiamondMode.Flat) GrantFlatDiamonds(ctx, day);
                    }
                    policy.OnSessionStart(ctx);
                    // SCR_OFFLINE_REWARD is modal and the runner holds ticks until it is claimed (01 section 8.7): a policy
                    // that leaves the reward pending gets it claimed here, as closing the modal would, so no session ticks
                    // with a reward pending.
                    if (sim.State.Offline.Pending != null) ctx.Apply(new ClaimOfflineReward());
                    for (int d = 0; d < decisionsPerSession; d++)
                    {
                        runner.DebugAdvance(ticksPerDecision);   // ticks and publishes the events (headless driver)
                        clock.Advance(HarnessConstants.DecisionIntervalSeconds);
                        policy.Decide(ctx);
                        sink.OnActiveMinute(sim.Query.Power);
                    }
                    policy.OnSessionEnd(ctx);
                    runner.OnPause();   // suspend: drain, publish, save (a no-op store)
                    clock.Advance(HarnessConstants.SessionGapSeconds(day, s));
                }
                rows[day - 1] = Snapshot(day, sim, sink, clock);
                AfterDay?.Invoke(day, ctx);
            }

            return new RunResult
            {
                Bot = policy.Name, Seed = seed, Days = days, ContentVersion = _content.Version, ContentHash = _content.ContentHash,
                Diamonds = Diamonds, Rows = rows, Walls = sink.Walls, Spend = sink.Spend, DiamondsByReason = sink.DiamondsByReason,
                DayHashes = hashes, FinalTick = sim.Tick, FinalServerSec = clock.NowServerSeconds, FinalHash = sim.StateHash(), TickHz = k.TickHz,
            };
        }

        /// <summary>
        /// --diamonds flat: DIAMONDS_PER_DAY_F2P at day start through the logged ApplyInboxGrant (the only grant door).
        /// TODO(#176): ApplyInboxGrant's body is MVP; until it lands the grant is rejected and the run stops with that reason.
        /// </summary>
        static void GrantFlatDiamonds(BotContext ctx, int day)
        {
            var grant = new InboxGrantPayload("bot-flat-day" + day, new[] { new GrantLine("currency", Currency.DIAMOND.ToString(), ctx.Content.Constants.DiamondsPerDayF2p) },
                                              CurrencyReason.MAIL);
            var r = ctx.Apply(new ApplyInboxGrant(grant));
            if (!r.Ok) throw new NotSupportedException("--diamonds flat needs ApplyInboxGrant, which the sim rejects (" + r + "; TODO(#176))");
        }

        static DayRow Snapshot(int day, Simulation sim, BotEventSink sink, FakeClock clock)
        {
            var s = sim.State;
            var p = sim.Query.Power;
            long pulls = 0;
            foreach (var b in s.Banners.Values) if (b != null) pulls += b.TotalPulls;
            double longest = 0, current = 0;
            long nowSec = clock.NowServerSeconds;
            foreach (var w in sink.Walls)
            {
                double h = w.WallClockHours(nowSec);
                if (h > longest) longest = h;
                if (w.IsOpen) current = h;
            }
            return new DayRow(day, s.Combat.Stage, s.Progress.HighestCleared, p.RatedDps,
                              s.Wallet.Gold, s.Wallet.Gold + sink.GoldSpent, sink.GoldSpent, s.Wallet.Diamond, sink.DiamondsEarned, sink.DiamondsSpent,
                              sink.UsdSpent, (int)pulls, sink.LPlus, sink.HighestRarity, s.Hero.Level, s.Hero.AwakenRank,
                              s.Fish.HighestLevel, s.Wallet.FishCoin, s.Wallet.RockCandy,
                              s.Stats.FirstWallStage, longest, current, LayerShares.From(p), sim.StateHash());
        }
    }
}
