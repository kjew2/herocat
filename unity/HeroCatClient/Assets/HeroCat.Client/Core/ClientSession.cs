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
    /// <summary>Client-side options for <see cref="ClientSession.Start"/> (all optional).</summary>
    public sealed class ClientOptions
    {
        public ILocalizer Localizer { get; set; }
        public IClientPrefs Prefs { get; set; }
        public string BuildHash { get; set; }
    }

    /// <summary>
    /// The client's engine-free session host (07 section 17.3; issue #65): runs Runtime's BootFlow (driving
    /// <see cref="BootVm"/> for SCR_BOOT), then forwards the Unity callbacks to the SessionRunner, the one tick driver shared
    /// with bots and tests. GameRoot is a thin forwarder over this. The composition root wraps its clock in an
    /// <see cref="OffsetClock"/> (ServicesOptions.WrapClock) for the debug clock cheats, so saves, summons and the runner all
    /// read the same time. Ticks stay off while SCR_OFFLINE_REWARD is open (01 section 8.7): the runner holds them while
    /// Offline.Pending is set (decision 53) and the modal's Claim sends ClaimOfflineReward, which clears it.
    /// </summary>
    public sealed class ClientSession
    {
        ClientSession(BootResult boot, OffsetClock clock, BootVm bootVm, IMonoClock mono, ClientOptions options)
        {
            Boot = boot;
            Clock = clock;
            BootVm = bootVm;
            if (boot.Outcome != BootOutcome.Ready)
            {
                BootError = new ErrorVm(options?.Localizer, null);
                BootError.Show(boot.Error, bootFailure: true);
                return;
            }
            BootVm.OnDone();
            Vm = new GameViewModel(boot.Runner, clock, new GameVmOptions
            {
                Localizer = options?.Localizer,
                Prefs = options?.Prefs,
                Now = () => mono.Ticks / (double)mono.Frequency,
                SaveDescription = "runtime save (" + (boot.IsNewProfile ? "new profile, " : "") + boot.LoadStatus + ")",
            });
        }

        /// <summary>The boot outcome; on Fatal or Blocked, <see cref="BootResult.Error"/> says what SCR_BOOT shows.</summary>
        public BootResult Boot { get; }

        /// <summary>SCR_BOOT's progress, step key and version caption.</summary>
        public BootVm BootVm { get; }

        /// <summary>The blocking SCR_ERROR for a failed boot (code, Retry, Update or Contact support); null when ready.</summary>
        public ErrorVm BootError { get; }

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
                                          ICommandLog commandLog = null, SessionRunnerOptions runner = null, string clientVersion = null,
                                          ClientOptions options = null)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            OffsetClock clock = null;
            Func<IClock, IClock> wrap = inner => clock = new OffsetClock(inner);
            var bootVm = new BootVm(options?.Localizer);
            IMonoClock mono = runner?.Mono ?? StopwatchMonoClock.Instance;
            var boot = BootFlow.Run(new BootOptions
            {
                Content = content,
                Services = (db, state) => services(db, state, wrap),
                CommandLog = commandLog,
                Runner = runner,
                ClientVersion = clientVersion,
            }, bootVm.OnStep);
            bootVm.SetVersions(clientVersion, boot.Content?.Version, options?.BuildHash);
            return new ClientSession(boot, clock, bootVm, mono, options);
        }

        /// <summary>Once per rendered frame. Returns the ticks run.</summary>
        public int Frame()
        {
            if (!IsReady) return 0;
            int ticks = Runner.OnFrame();
            if (Vm.ConsumeSaveRequest()) Runner.SaveNow(SaveTrigger.Debug);
            Vm.Frame();
            if (ticks > 0 | Vm.ConsumeDirty()) Vm.Refresh();
            return ticks;
        }

        public void OnPause()
        {
            if (!IsReady) return;
            Vm.OnPause();
            Runner.OnPause();
        }

        public void OnResume()
        {
            if (!IsReady) return;
            Runner.OnResume();
            Vm.OnResume();
        }

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
