using System;
using System.Threading.Tasks;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Session;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// What SCR_MAIN's maintenance banner shows (01 section 8.12 step 4, 08 section 8.4): during maintenance a countdown to
    /// <see cref="UntilServerSeconds"/>; before it, an announcement from MAINT_ANNOUNCE_S ahead of a window set in advance
    /// with <c>active: false</c>. <see cref="SecondsLeft"/> is 0 when the window has no end.
    /// </summary>
    public sealed record MaintenanceBanner(bool Active, string MessageKey, long UntilServerSeconds, long SecondsLeft);

    /// <summary>
    /// The config cadence of 01 section 8.12 (issue #144), engine-free and main-thread only: the SessionRunner calls
    /// <see cref="Update"/> every frame and <see cref="OnResume"/> on resume. It re-fetches every CONFIG_REFRESH_S in the
    /// foreground, on every resume, every MAINT_POLL_S while maintenance is active and once at the window's <c>until</c>.
    /// A fetch runs off the main thread (the ConfigClient never throws) and lands on the next frame: then
    /// <see cref="Landed"/> fires (the content updater listens) and the <c>minClientVersion</c> gate is re-checked. A
    /// mid-session CLIENT_TOO_OLD does not interrupt play: <see cref="TryBlock"/> hands it over once SCR_MAIN is idle, after
    /// a save (step 3). Kill switches need nothing from here: view-models read <see cref="IConfigService.Flags"/>, which
    /// the ConfigClient swaps as soon as an answer arrives.
    /// </summary>
    public sealed class ConfigRefresher
    {
        readonly IConfigService _config;
        readonly IClock _clock;
        readonly IMonoClock _mono;
        readonly string _clientVersion;
        readonly Guid _sessionId;
        Task<ClientConfig> _inFlight;
        long _nextDueMono;

        /// <param name="clientVersion">This build's version for the minClientVersion gate; null skips the gate.</param>
        public ConfigRefresher(IConfigService config, IClock clock, IMonoClock mono, string clientVersion, Guid sessionId)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _mono = mono ?? StopwatchMonoClock.Instance;
            _clientVersion = clientVersion;
            _sessionId = sessionId;
            _nextDueMono = _mono.Ticks + Sec(RuntimeConfig.CONFIG_REFRESH_S);
        }

        public ClientConfig Current => _config.Current;

        /// <summary>A landed fetch, on the main thread (inside <see cref="Update"/>).</summary>
        public event Action<ClientConfig> Landed;

        public bool InFlight => _inFlight != null;

        /// <summary>Fetches started by this refresher (tests).</summary>
        public int Fetches { get; private set; }

        /// <summary>Seconds of mono time until the next scheduled fetch (diagnostics, tests).</summary>
        public double SecondsUntilDue => (_nextDueMono - _mono.Ticks) / (double)_mono.Frequency;

        /// <summary>Set when the last landed config gates this client out (CLIENT_TOO_OLD); null otherwise.</summary>
        public UserError ClientTooOld { get; private set; }

        /// <summary>The boot fetch's answer: checks the gate and schedules the next fetch from it.</summary>
        public void Start(ClientConfig bootConfig)
        {
            if (bootConfig != null) Apply(bootConfig, raise: false);
        }

        /// <summary>A fetch now unless one is in flight (resume; boot's re-fetch after auth with the fresh JWT, 01 8.1 step 2).</summary>
        public void RefreshNow()
        {
            if (_inFlight != null) return;
            Fetches++;
            Task<ClientConfig> t;
            try
            {
                t = _config.FetchAsync();
            }
            catch (Exception)
            {
                t = Task.FromResult(_config.Current);   // the contract says never throws; a broken service must not stop the frame
            }
            _inFlight = t ?? Task.FromResult(_config.Current);
            Update();   // a synchronous service lands at once
        }

        /// <summary>01 section 8.12 step 1: config is re-fetched on every resume.</summary>
        public void OnResume() => RefreshNow();

        /// <summary>Per frame on the main thread: lands a finished fetch, then starts the next when due.</summary>
        public void Update()
        {
            var t = _inFlight;
            if (t != null)
            {
                if (!t.IsCompleted) return;
                _inFlight = null;
                Apply(t.Status == TaskStatus.RanToCompletion && t.Result != null ? t.Result : _config.Current, raise: true);
            }
            if (_mono.Ticks >= _nextDueMono) RefreshNow();
        }

        /// <summary>
        /// 01 section 8.12 step 3: the pending CLIENT_TOO_OLD block, handed over only when <paramref name="mainIdle"/>
        /// (SCR_MAIN on top, no modal, no flow in flight), after <paramref name="save"/> ran. Null while nothing blocks.
        /// </summary>
        public UserError TryBlock(bool mainIdle, Func<bool> save)
        {
            if (ClientTooOld == null || !mainIdle) return null;
            save?.Invoke();
            return ClientTooOld;
        }

        /// <summary>The maintenance banner at <paramref name="nowServerSeconds"/>, or null when there is nothing to show.</summary>
        public MaintenanceBanner Banner(long nowServerSeconds) => BannerFor(_config.Current?.Maintenance, nowServerSeconds);

        public static MaintenanceBanner BannerFor(MaintenanceInfo m, long now)
        {
            if (m == null) return null;
            long left = m.UntilServerSeconds > now ? m.UntilServerSeconds - now : 0;
            if (m.Active) return new MaintenanceBanner(true, m.MessageKey, m.UntilServerSeconds, left);
            if (m.UntilServerSeconds > now && left <= RuntimeConfig.MAINT_ANNOUNCE_S)
                return new MaintenanceBanner(false, m.MessageKey, m.UntilServerSeconds, left);
            return null;
        }

        void Apply(ClientConfig c, bool raise)
        {
            ClientTooOld = _clientVersion != null && !string.IsNullOrEmpty(c.MinClientVersion) &&
                           ContentLoader.CompareSemver(c.MinClientVersion, _clientVersion) > 0
                ? ErrorClassifier.ForCode("CLIENT_TOO_OLD", new SupportId(_sessionId, _clock.NowServerSeconds))
                : null;

            long now = _clock.NowServerSeconds;
            long wait = RuntimeConfig.CONFIG_REFRESH_S;
            var m = c.Maintenance;
            if (m != null && m.Active) wait = RuntimeConfig.MAINT_POLL_S;
            if (m != null && m.UntilServerSeconds > now) wait = Math.Min(wait, Math.Max(1, m.UntilServerSeconds - now));   // re-fetch at until
            _nextDueMono = _mono.Ticks + Sec(wait);
            if (raise) Landed?.Invoke(c);
        }

        long Sec(double s) => (long)(s * _mono.Frequency);
    }
}
