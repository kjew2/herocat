using System;
using System.IO;
using HeroCat.Runtime.Save;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Services
{
    /// <summary>Chosen at build time (01 section 1.2). Local = M0 and any build with no server configured.</summary>
    public enum BackendMode { Local, Remote }

    /// <summary>Inputs of the composition root. Everything except <see cref="State"/> is optional.</summary>
    public sealed class ServicesOptions
    {
        /// <summary>Application.persistentDataPath (saves under {DataDir}/saves). Null: an in-memory save store.</summary>
        public string DataDir { get; set; }

        /// <summary>The active content (LocalSummonService draws from it; LocalConfigService reports its version).</summary>
        public ContentDb Content { get; set; }

        /// <summary>
        /// The live state for services that read it (LocalSummonService, LocalDungeonSeedService). A function
        /// because the services are built before the save is loaded. They read it and never write it (issue #6).
        /// </summary>
        public Func<GameState> State { get; set; }

        /// <summary>Overrides: the bot harness and tests pass a FakeClock and a MemorySaveStore (as <see cref="Slots"/>).</summary>
        public IClock Clock { get; set; }

        /// <summary>The raw slot store under the LocalSaveStore. Null: files under {DataDir}/saves, else in memory.</summary>
        public ISaveSlotStore Slots { get; set; }

        /// <summary>Replaces the whole 02 section 8 store (tests; Remote wraps the local one in its cloud client).</summary>
        public ISaveStore Saves { get; set; }

        public IAnalytics Analytics { get; set; }

        /// <summary>This install's id (02 section 8.4). Guid.Empty: the persisted install_id under DataDir, else a fresh one.</summary>
        public Guid InstallId { get; set; }

        /// <summary>HMAC device key for the save envelope (02 section 10), if any.</summary>
        public byte[] DeviceKey { get; set; }

        /// <summary>
        /// Platform secure storage for <c>device_key</c> and <c>tokens.json</c> (02 section 8.4; the Keychain / Keystore
        /// stores are issue #151). Null in Remote mode: a FileSecureStore under {DataDir}/secure (not secure), else in memory.
        /// </summary>
        public HeroCat.Runtime.Account.ISecureStore SecureStore { get; set; }

        /// <summary>
        /// Debug builds: wraps the clock before any service captures it (the client's OffsetClock for the clock cheats), so
        /// saves, summons and the runner all read the same time.
        /// </summary>
        public Func<IClock, IClock> WrapClock { get; set; }

        /// <summary>The network door Local mode must never use (08 AC 24); Remote's ApiClient (HeroCat.Remote) has its own HttpClient.</summary>
        public INetTransport Network { get; set; }

        /// <summary>Debug builds: receipts are granted from this catalog into the local inbox (FakeReceiptValidator).</summary>
        public Func<string, GrantLine[]> DebugReceiptCatalog { get; set; }

        /// <summary>
        /// Remote mode wiring, supplied by HeroCat.Remote (Runtime cannot reference it): RemoteGameServices.Wire builds the
        /// ApiClient (issue #143), ServerClock(api), the cloud slots and the ConfigClient (#144); the other ApiClient-based
        /// clients of 01 section 1.2 land with their issues (SummonClient #145, DungeonSeedClient (#146, wired), receipts and inbox #147,
        /// AuthClient #148 (GameServices.Account), CloudSaveClient #152, analytics uploader #209).
        /// </summary>
        public Func<ServicesOptions, GameServices> Remote { get; set; }
    }

    /// <summary>
    /// The composition root's product (01 section 1.2's <c>Services</c>; issue #66): the seven server-sensitive seams of
    /// 00 contract 11 plus analytics. Named GameServices because a type called Services inside the HeroCat.Runtime.Services
    /// namespace would be shadowed by the namespace in every other HeroCat.Runtime.* file. Nothing outside
    /// <see cref="Build"/> branches on Local vs Remote.
    /// </summary>
    public sealed record GameServices
    {
        public IClock Clock { get; init; }

        /// <summary>04 section 12: LocalSummonService in Local mode, TODO(#145) SummonClient in Remote. Null without content.</summary>
        public HeroCat.Runtime.Summons.ISummonService Summon { get; init; }

        /// <summary>02 section 8's store: LocalSaveStore over <see cref="Slots"/> in Local mode.</summary>
        public ISaveStore Saves { get; init; }

        /// <summary>The raw slots under <see cref="Saves"/> (debug "Reset profile" deletes them), when known.</summary>
        public ISaveSlotStore Slots { get; init; }

        /// <summary>This install's id (02 section 8.4), stamped into DeviceId by every save.</summary>
        public Guid InstallId { get; init; }

        public IInboxService Inbox { get; init; }
        public IReceiptValidator Receipts { get; init; }
        public IConfigService Config { get; init; }
        public IDungeonSeedService Dungeons { get; init; }
        public IAnalytics Analytics { get; init; }

        /// <summary>Boot step 3 and 01 section 8.13: LocalAccountService in Local mode, the AuthClient (#148) in Remote.</summary>
        public HeroCat.Runtime.Account.IAccountService Account { get; init; }

        /// <summary>The logout archive of {DataDir}/saves (01 section 8.13); null without a DataDir.</summary>
        public HeroCat.Runtime.Account.SaveArchive Archives { get; init; }

        /// <summary>The mode this set was built for (diagnostics only; nothing may branch on it).</summary>
        public BackendMode Mode { get; init; }

        /// <summary>01 section 1.2 Build: picks the implementations for <paramref name="mode"/>.</summary>
        public static GameServices Build(BackendMode mode, ServicesOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (mode == BackendMode.Remote)
            {
                if (options.Remote == null)
                    throw new NotSupportedException("Remote backend not wired: pass ServicesOptions.Remote (HeroCat.Remote.Services.RemoteGameServices.Wire)");
                var remote = options.Remote(options);
                return remote with { Mode = BackendMode.Remote };
            }

            var state = options.State ?? (() => null);
            IClock clock = options.Clock ?? new ServerClock();   // device clock floored at the last save; IsAuthoritative = false
            if (options.WrapClock != null) clock = options.WrapClock(clock);
            var analytics = options.Analytics ?? NullAnalytics.Instance;
            var installId = options.InstallId != Guid.Empty ? options.InstallId
                : options.DataDir != null ? HeroCat.Runtime.Profile.InstallId.LoadOrCreate(options.DataDir) : Guid.NewGuid();
            var slots = options.Slots ?? (options.DataDir != null
                ? new FileSaveStore(Path.Combine(options.DataDir, "saves"))
                : (ISaveSlotStore)new MemorySaveStore());
            var inbox = new LocalInboxService(() => clock.NowServerSeconds);
            return new GameServices
            {
                Mode = BackendMode.Local,
                Clock = clock,
                Summon = options.Content != null
                    ? new HeroCat.Runtime.Summons.LocalSummonService(state, options.Content, () => clock.NowServerSeconds)
                    : null,
                Saves = options.Saves ?? new LocalSaveStore(slots, clock, installId, analytics, options.DeviceKey),
                Slots = options.Saves == null ? slots : options.Slots,
                InstallId = installId,
                Inbox = inbox,
                Receipts = options.DebugReceiptCatalog != null
                    ? new FakeReceiptValidator(inbox, options.DebugReceiptCatalog, clock)
                    : (IReceiptValidator)new DisabledReceiptValidator(),
                Config = new LocalConfigService(options.Content?.Version),
                Dungeons = new LocalDungeonSeedService(state),
                Analytics = analytics,
                Account = new HeroCat.Runtime.Account.LocalAccountService(),
                Archives = options.DataDir != null ? new HeroCat.Runtime.Account.SaveArchive(options.DataDir) : null,
            };
        }

        /// <summary>Device Local build: ServerClock, saves under {dataDir}/saves.</summary>
        public static GameServices Local(string dataDir, Func<GameState> state = null, ContentDb content = null, IAnalytics analytics = null) =>
            Build(BackendMode.Local, new ServicesOptions
            {
                DataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir)), State = state, Content = content, Analytics = analytics,
            });

        /// <summary>Tests and the bot harness: the Local set with a fake clock, an in-memory save and in-memory analytics.</summary>
        public static GameServices InMemory(FakeClock clock, MemorySaveStore store = null, IAnalytics analytics = null,
                                            Func<GameState> state = null, ContentDb content = null) =>
            Build(BackendMode.Local, new ServicesOptions
            {
                Clock = clock ?? throw new ArgumentNullException(nameof(clock)),
                Slots = store ?? new MemorySaveStore(),
                Analytics = analytics ?? new MemoryAnalytics(),
                State = state,
                Content = content,
            });
    }
}
