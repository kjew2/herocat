using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HeroCat.Runtime.Account;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Boot
{
    /// <summary>The steps of 01 section 8.1, reported to SCR_BOOT.</summary>
    public enum BootStep { Content, Config, Auth, Save, CatchUp, Handoff }

    public sealed class BootOptions
    {
        /// <summary>
        /// Content sources in priority order (step 1): a verified cached newer bundle first (TODO(#212)), then the bundled
        /// one. Each builds a ContentDb or throws; the first that succeeds wins. All failing is Fatal CONTENT_CORRUPT.
        /// </summary>
        public IReadOnlyList<Func<ContentDb>> Content { get; set; }

        /// <summary>The composition root (01 section 1.2), called once the content is known. Typically GameServices.Build.</summary>
        public Func<ContentDb, Func<GameState>, GameServices> Services { get; set; }

        /// <summary>This build's version for config's minClientVersion gate (null skips the gate).</summary>
        public string ClientVersion { get; set; }

        /// <summary>
        /// The playerId when GameServices.Account knows none (tests, bots); the account's own id wins. Guid.Empty: a local guest.
        /// </summary>
        public Guid PlayerId { get; set; }

        /// <summary>New profiles only: tests and bots pin RngRoot; null is a fresh random root (02 section 3.4).</summary>
        public Guid? NewProfileRngRoot { get; set; }

        /// <summary>The session id the support id is derived from (01 section 7).</summary>
        public Guid SessionId { get; set; } = Guid.NewGuid();

        public SessionRunnerOptions Runner { get; set; }

        /// <summary>The command log the runner records into (saves/cmdlog.jsonl on device, in memory in bots).</summary>
        public ICommandLog CommandLog { get; set; }

        /// <summary>Wall-time source for the step timings logged to analytics (01 AC 15 is measured on device).</summary>
        public IMonoClock Mono { get; set; }

        /// <summary>
        /// MVP: builds the content updater for the loaded content (01 section 8.12, issue #144); every landed config is
        /// checked against it. Null (Local builds, bots): no content updates.
        /// </summary>
        public Func<ContentDb, ContentUpdater> ContentUpdates { get; set; }
    }

    public enum BootOutcome { Ready, Blocked, Fatal }

    public sealed record BootStepTiming(BootStep Step, double Ms);

    public sealed class BootResult
    {
        public BootOutcome Outcome { get; internal set; }

        /// <summary>Set when Outcome is Fatal or Blocked: what SCR_BOOT / SCR_ERROR shows (CONTENT_CORRUPT, SAVE_TOO_NEW, MAINTENANCE, ...).</summary>
        public UserError Error { get; internal set; }

        public ContentDb Content { get; internal set; }
        public GameServices Services { get; internal set; }
        public ClientConfig Config { get; internal set; }

        /// <summary>The config cadence (01 section 8.12), also set as <see cref="SessionRunner.Config"/>.</summary>
        public ConfigRefresher ConfigRefresher { get; internal set; }

        /// <summary>The content update flow when <see cref="BootOptions.ContentUpdates"/> is set; null otherwise.</summary>
        public ContentUpdater ContentUpdates { get; internal set; }
        public Simulation Sim { get; internal set; }
        public EventBus Bus { get; internal set; }
        public SessionRunner Runner { get; internal set; }

        /// <summary>06 section 13's <c>currency_changed</c> rows from the bus; null when analytics is NullAnalytics.</summary>
        public CurrencyAnalytics CurrencyAnalytics { get; internal set; }

        /// <summary>The feature specs' other analytics rows from the bus (03, 04, 05, 06 section 13); null when analytics is NullAnalytics.</summary>
        public FeatureAnalytics FeatureAnalytics { get; internal set; }

        /// <summary>
        /// Step 3 (01 section 8.1): how the account signed in. SignInRequired does not stop the boot (the local save stays
        /// playable); the client offers SCR_TITLE's sign-in. Null only when the boot stopped before step 3.
        /// </summary>
        public SignInResult SignIn { get; internal set; }

        /// <summary>
        /// The loaded save was a local guest's (<c>PlayerId = Guid.Empty</c>) and now belongs to the signed-in account: it is
        /// that player's first upload (01 open question 7; the cloud client sends it with <c>If-Match: *</c>, 08 open question 9).
        /// </summary>
        public bool AdoptedLocalSave { get; internal set; }

        public LoadStatus LoadStatus { get; internal set; }
        public bool IsNewProfile { get; internal set; }

        /// <summary>
        /// Set on the boot that replaced an unreadable save with a new profile: SCR_MAIN shows it once, acknowledge-only,
        /// with the support id (02 section 8.3, 07 section 4.27; 00 decision 103). Null otherwise. The next boot loads the new
        /// save, so the notice is never shown twice.
        /// </summary>
        public Notice SaveResetNotice { get; internal set; }

        /// <summary>Step 6: SCR_MAIN opens SCR_OFFLINE_REWARD first (the runner holds ticks until ClaimOfflineReward).</summary>
        public bool OpenOfflineReward { get; internal set; }

        /// <summary>Step 6: then the tutorial overlay when Tutorial.Step &lt; TUTORIAL_DONE (row count of tutorial_steps).</summary>
        public bool ShowTutorial { get; internal set; }

        public List<BootStepTiming> Timings { get; } = new List<BootStepTiming>();
    }

    /// <summary>
    /// Cold boot (01 section 8.1; issue #70), engine-free so HeroCat.Runtime.Tests can drive it. Steps: content (the
    /// only fatal step), config, auth, save load or new profile, ApplyServerTime + ComputeOfflineCatchup + save, handoff.
    /// Config and auth degrade per section 6 and are hooks the MVP issues fill (Local config returns at once). No tick
    /// runs during boot: ticks start on the first SessionRunner.OnFrame after SCR_MAIN shows, and stay off while
    /// SCR_OFFLINE_REWARD is open, so the modal's numbers are exactly what gets credited.
    /// </summary>
    public static class BootFlow
    {
        public static BootResult Run(BootOptions o, Action<BootStep> progress = null)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (o.Content == null || o.Content.Count == 0) throw new ArgumentException("BootOptions.Content is empty");
            if (o.Services == null) throw new ArgumentException("BootOptions.Services is null");
            var mono = o.Mono ?? StopwatchMonoClock.Instance;
            var r = new BootResult();
            long t0 = mono.Ticks;
            void Done(BootStep s)
            {
                long t = mono.Ticks;
                r.Timings.Add(new BootStepTiming(s, (t - t0) * 1000.0 / mono.Frequency));
                t0 = t;
            }

            // 1. Content. Only this step is fatal.
            progress?.Invoke(BootStep.Content);
            var problems = new List<string>();
            foreach (var loadContent in o.Content)
            {
                try
                {
                    r.Content = loadContent();
                    if (r.Content != null) break;
                }
                catch (Exception e)
                {
                    problems.Add(e.Message);
                }
            }
            if (r.Content == null) return Fail(r, BootOutcome.Fatal, ErrorClassifier.CONTENT_CORRUPT, o, new SystemClock().NowServerSeconds);
            Done(BootStep.Content);

            GameState state = null;
            r.Services = o.Services(r.Content, () => state);
            var svc = r.Services;
            var analytics = svc.Analytics ?? NullAnalytics.Instance;

            // 2. Config. LocalConfigService answers at once; a remote fetch degrades to cache or defaults (section 6) and
            // never holds boot longer than CONFIG_FETCH_TIMEOUT_S (the ConfigClient bounds itself; this wait is the backstop).
            progress?.Invoke(BootStep.Config);
            r.Config = FetchConfig(svc.Config);
            if (r.Config != null)
            {
                if (o.ClientVersion != null && !string.IsNullOrEmpty(r.Config.MinClientVersion) &&
                    ContentLoader.CompareSemver(r.Config.MinClientVersion, o.ClientVersion) > 0)
                    return Fail(r, BootOutcome.Blocked, "CLIENT_TOO_OLD", o, svc.Clock.NowServerSeconds);
                // Only a live answer blocks: offline, a cached maintenance flag is stale news, and players keep playing
                // offline through a maintenance window anyway (08 section 8.4).
                if (r.Config.Maintenance != null && r.Config.Maintenance.Active && r.Config.Source != ConfigSource.Cached)
                    return Fail(r, BootOutcome.Blocked, "MAINTENANCE", o, svc.Clock.NowServerSeconds);
            }
            if (svc.Config != null)
            {
                r.ConfigRefresher = new ConfigRefresher(svc.Config, svc.Clock, o.Runner?.Mono, o.ClientVersion, o.SessionId);
                r.ConfigRefresher.Start(r.Config);
            }
            r.ContentUpdates = o.ContentUpdates?.Invoke(r.Content);
            if (r.ContentUpdates != null)
            {
                // A newer bundle downloads in the background and applies at the next cold boot (8.12); never this ContentDb.
                r.ContentUpdates.Check(r.Config);
                if (r.ConfigRefresher != null) r.ConfigRefresher.Landed += c => r.ContentUpdates.Check(c);
            }
            Done(BootStep.Config);

            // 3. Auth: refresh, or a guest for this device (MVP; Local answers at once). Offline continues with the cached
            // account or a local guest; a refused session continues too and the client offers sign-in (section 6).
            progress?.Invoke(BootStep.Auth);
            r.SignIn = SignIn(svc.Account);
            Guid playerId = r.SignIn.PlayerId != Guid.Empty ? r.SignIn.PlayerId : o.PlayerId;
            // 01 section 8.1 step 2: after auth, config is re-fetched once in the background with the fresh JWT (the server
            // applies the rollout bucket only then). It lands on a later frame; any newer bundle applies at the next boot.
            if (r.SignIn.Status == SignInStatus.SignedIn) r.ConfigRefresher?.RefreshNow();
            Done(BootStep.Auth);

            // 4. Save load, or a new profile with the profile-created grant (TUTORIAL_DIAMONDS + TUTORIAL_WEAPON_ID, tutorial step 0).
            progress?.Invoke(BootStep.Save);
            long now = svc.Clock.NowServerSeconds;
            try { svc.Archives?.Prune(now, RuntimeConfig.ARCHIVE_KEEP_DAYS); }   // logout archives (01 section 8.13)
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            if (svc.Saves is LocalSaveStore local)   // a new profile is this boot's guest (or MVP account), seeded as asked
                local.NewProfileFactory = (db, t) => ProfileFactory.Create(db, playerId, t, svc.InstallId, o.NewProfileRngRoot, analytics);
            // 02 section 8.3's recovery order; the store floors the clock at the loaded LastSavedServerTime (11.2 rule 4).
            var load = svc.Saves.LoadAsync(now, r.Content, CancellationToken.None).GetAwaiter().GetResult();
            r.LoadStatus = load.Status;
            if (load.Status == LoadStatus.TooNew)
                return Fail(r, BootOutcome.Fatal, ErrorClassifier.SAVE_TOO_NEW, o, now);   // nothing is written over a newer save
            // TODO(#152): LoadStatus.Conflict (cloud, MVP) opens the 02 section 9.4 conflict modal before SCR_MAIN.
            state = load.State;
            if (state != null && !load.IsNewProfile && state.PlayerId == Guid.Empty && playerId != Guid.Empty)
            {
                state = StateCopy.DeepClone(state, playerId);   // the first auth adopts the local guest save (open question 7)
                r.AdoptedLocalSave = true;
            }
            if (load.IsNewProfile)
            {
                // No save, or every copy bad (quarantined): a new profile (02 section 8.3 step 4). An unreadable save is not
                // fatal: boot continues and shows a one-time notice with the support id (00 decision 103, issue #248).
                r.IsNewProfile = true;
                analytics.Track(new AnalyticsEvents.ProfileCreated());   // 02 section 14.3: the funnel step, never the RngRoot seed (issue #261)
                if (load.ReplacedUnreadableSave)
                {
                    var sid = new SupportId(o.SessionId, now).Format();
                    r.SaveResetNotice = new Notice(ErrorClassifier.SAVE_CORRUPT_ALL, sid);
                    analytics.Track(new AnalyticsEvents.SaveCorruptAll(sid));
                }
            }
            r.Sim = new Simulation(state, r.Content);
            r.Bus = new EventBus();
            r.Runner = new SessionRunner(r.Sim, svc, r.Bus, o.CommandLog, o.Runner ?? new SessionRunnerOptions()) { Config = r.ConfigRefresher };
            if (!(analytics is NullAnalytics))   // before Start, so the first rollover's LOGIN row (and day_rollover) is counted
            {
                r.CurrencyAnalytics = new CurrencyAnalytics(r.Bus, analytics, () => r.Sim.State, r.Content.Constants.TickHz);
                r.FeatureAnalytics = new FeatureAnalytics(r.Bus, analytics, r.Content, () => r.Sim.State, () => r.Sim.Query.Power);
            }
            Done(BootStep.Save);

            // 5. Rollover first, then the catch-up compute (stores Offline.Pending, stamps LastTickServerTime), save now.
            progress?.Invoke(BootStep.CatchUp);
            r.Runner.Start();
            Done(BootStep.CatchUp);

            // 6. Handoff to SCR_MAIN.
            progress?.Invoke(BootStep.Handoff);
            r.OpenOfflineReward = state.Offline.Pending != null;
            r.ShowTutorial = !state.Tutorial.Skipped && state.Tutorial.Step < r.Content.TutorialSteps.Count;
            // The runner holds ticks while Offline.Pending is set (SessionRunner.TicksHeldForOfflineReward, decision 53).
            r.Outcome = BootOutcome.Ready;
            Done(BootStep.Handoff);

            double bootMs = 0;
            var stepMs = new double[Enum.GetValues(typeof(BootStep)).Length];
            foreach (var t in r.Timings)
            {
                bootMs += t.Ms;
                stepMs[(int)t.Step] += t.Ms;
            }
            analytics.Track(new AnalyticsEvents.BootMs(bootMs, stepMs[(int)BootStep.Content], stepMs[(int)BootStep.Config], stepMs[(int)BootStep.Auth],
                                                      stepMs[(int)BootStep.Save], stepMs[(int)BootStep.CatchUp], stepMs[(int)BootStep.Handoff]));   // 10 section 5
            var pending = state.Offline.Pending;   // 02 section 14.3: the stage is the envelope's
            analytics.Track(new AnalyticsEvents.SessionStart(pending?.ElapsedSec ?? 0, pending?.CreditedSec ?? 0, ColdBoot: true, bootMs));
            return r;
        }

        static SignInResult SignIn(IAccountService account)
        {
            if (account == null) return SignInResult.LocalGuest;
            try { return account.SignInAsync(CancellationToken.None).GetAwaiter().GetResult() ?? SignInResult.LocalGuest; }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // AuthClient never throws for a failed call; anything else degrades like offline (section 6).
                return new SignInResult(SignInStatus.Offline, account.PlayerId, Code: ErrorClassifier.SRV_UNEXPECTED);
            }
        }

        /// <summary>Boot step 2's fetch, bounded by CONFIG_FETCH_TIMEOUT_S whatever the service does.</summary>
        static ClientConfig FetchConfig(IConfigService config)
        {
            if (config == null) return null;
            try
            {
                var t = config.FetchAsync();
                if (t.Wait(TimeSpan.FromSeconds(RuntimeConfig.CONFIG_FETCH_TIMEOUT_S))) return t.Result ?? config.Current;
            }
            catch (AggregateException)
            {
                // the contract says FetchAsync never throws; a broken one degrades like a timeout
            }
            return config.Current;
        }

        static BootResult Fail(BootResult r, BootOutcome outcome, string code, BootOptions o, long now)
        {
            r.Outcome = outcome;
            r.Error = ErrorClassifier.ForCode(code, new SupportId(o.SessionId, now));
            return r;
        }
    }
}
