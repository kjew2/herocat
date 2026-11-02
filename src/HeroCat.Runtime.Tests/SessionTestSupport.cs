using System;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Replay;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>A booted session over in-memory services with a fake wall clock and a fake monotonic clock.</summary>
    internal sealed class TestSession
    {
        public FakeClock Clock;
        public FakeMonoClock Mono;
        public ISaveSlotStore Store;
        public MemoryAnalytics Analytics;
        public BootResult Boot;
        public ListCommandLog Log;

        public SessionRunner Runner => Boot.Runner;
        public HeroCat.Sim.Simulation Sim => Boot.Sim;
        public EventBus Bus => Boot.Bus;

        /// <summary>Boots (load or create, sync, catch-up, save). Asserts the boot is Ready unless <paramref name="expectReady"/> is false.</summary>
        public static TestSession Open(FakeClock clock = null, ISaveSlotStore store = null, MemoryAnalytics analytics = null,
                                       Action<SessionRunnerOptions> configure = null, bool expectReady = true,
                                       INetTransport network = null, IClock clockOverride = null, Guid? installId = null,
                                       FakeMonoClock mono = null, HeroCat.Sim.Content.ContentDb content = null,
                                       Func<GameServices, GameServices> services = null)
        {
            var t = new TestSession
            {
                Clock = clock ?? new FakeClock(TestContent.T0),
                Mono = mono ?? new FakeMonoClock(),
                Store = store ?? new MemorySaveStore(),
                Analytics = analytics ?? new MemoryAnalytics(),
                Log = new ListCommandLog(),
            };
            var ro = new SessionRunnerOptions { Mono = t.Mono, TickBudgetMs = 0 };
            configure?.Invoke(ro);
            t.Boot = BootFlow.Run(new BootOptions
            {
                Content = new Func<HeroCat.Sim.Content.ContentDb>[] { () => content ?? TestContent.Db },
                Services = (db, state) =>
                {
                    var built = GameServices.Build(BackendMode.Local, new ServicesOptions
                    {
                        Clock = clockOverride ?? t.Clock, Slots = t.Store, Analytics = t.Analytics, State = state, Content = db,
                        Network = network, InstallId = installId ?? TestContent.Device,
                    });
                    return services != null ? services(built) : built;   // tests swap one seam (a fake remote service)
                },
                NewProfileRngRoot = TestContent.Root,
                Runner = ro,
                CommandLog = t.Log,
                Mono = t.Mono,
            });
            if (expectReady) Assert.That(t.Boot.Outcome, Is.EqualTo(BootOutcome.Ready), t.Boot.Error?.Code);
            return t;
        }

        /// <summary>Real time passes: both clocks move, then one frame runs. Returns the ticks run.</summary>
        public int Frame(double seconds)
        {
            Mono.Advance(seconds);
            Clock.Advance((long)Math.Floor(seconds));
            return Runner.OnFrame();
        }

        /// <summary>Plays <paramref name="seconds"/> in frames of <paramref name="frameSec"/> (the clock moves in whole seconds).</summary>
        public long Play(double seconds, double frameSec = 1.0)
        {
            long ticks = 0;
            for (double done = 0; done < seconds - 1e-9; done += frameSec) ticks += Frame(frameSec);
            return ticks;
        }
    }
}
