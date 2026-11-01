using System;
using System.Collections.Generic;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Content;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The client's engine-free session host (07 section 17.3; issue #65): runs Runtime's BootFlow, then forwards the
    /// Unity callbacks to the SessionRunner, the one tick driver shared with bots and tests. GameRoot is a thin forwarder
    /// over this. The composition root wraps its clock in an <see cref="OffsetClock"/> (ServicesOptions.WrapClock) for the
    /// debug clock cheats, so saves, summons and the runner all read the same time.
    /// </summary>
    public sealed class ClientSession
    {
        ClientSession(BootResult boot, OffsetClock clock)
        {
            Boot = boot;
            Clock = clock;
            if (boot.Outcome != BootOutcome.Ready) return;
            // TODO(#118, #108): SCR_OFFLINE_REWARD and ClaimOfflineReward. Until that screen exists nothing could turn the
            // ticks back on, so the session keeps ticking (the M0 sim stores no Offline.Pending yet).
            boot.Runner.TicksEnabled = true;
            Vm = new GameViewModel(boot.Runner, clock, "runtime save (" + (boot.IsNewProfile ? "new profile, " : "") + boot.LoadStatus + ")");
        }

        /// <summary>The boot outcome; on Fatal or Blocked, <see cref="BootResult.Error"/> says what SCR_BOOT shows.</summary>
        public BootResult Boot { get; }

        public bool IsReady => Boot.Outcome == BootOutcome.Ready;
        public OffsetClock Clock { get; }
        public SessionRunner Runner => Boot.Runner;
        public GameServices Services => Boot.Services;

        /// <summary>Null unless <see cref="IsReady"/>.</summary>
        public GameViewModel Vm { get; }

        /// <summary>
        /// Boots a session. <paramref name="services"/> is the composition root (GameServices.Build for the build's
        /// BackendMode) given the content, the state accessor and the clock wrapper to pass as ServicesOptions.WrapClock;
        /// <paramref name="content"/> lists the content sources in priority order.
        /// </summary>
        public static ClientSession Start(IReadOnlyList<Func<ContentDb>> content,
                                          Func<ContentDb, Func<GameState>, Func<IClock, IClock>, GameServices> services,
                                          ICommandLog commandLog = null, SessionRunnerOptions runner = null, string clientVersion = null)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            OffsetClock clock = null;
            Func<IClock, IClock> wrap = inner => clock = new OffsetClock(inner);
            var boot = BootFlow.Run(new BootOptions
            {
                Content = content,
                Services = (db, state) => services(db, state, wrap),
                CommandLog = commandLog,
                Runner = runner,
                ClientVersion = clientVersion,
            });
            return new ClientSession(boot, clock);
        }

        /// <summary>Once per rendered frame. Returns the ticks run.</summary>
        public int Frame()
        {
            if (!IsReady) return 0;
            int ticks = Runner.OnFrame();
            if (Vm.ConsumeSaveRequest()) Runner.SaveNow(SaveTrigger.Debug);
            if (ticks > 0 | Vm.ConsumeDirty()) Vm.Refresh();
            return ticks;
        }

        public void OnPause() { if (IsReady) Runner.OnPause(); }
        public void OnResume() { if (IsReady) Runner.OnResume(); }
        public void OnQuit() { if (IsReady) Runner.OnQuit(); }
        public void OnLowMemory() { if (IsReady) Runner.OnLowMemory(); }

        /// <summary>Debug "Reset profile": deletes the save; the caller then starts a new session.</summary>
        public void DeleteProfile()
        {
            Vm?.Dispose();
            Services?.Slots?.DeleteAll();
        }
    }
}
