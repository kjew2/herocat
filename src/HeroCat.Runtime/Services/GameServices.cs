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

        /// <summary>The active content (LocalConfigService reports its version). Optional.</summary>
        public ContentDb Content { get; set; }

        /// <summary>
        /// The live state for services that read it (LocalDungeonSeedService; LocalSummonService, TODO(#92)). A function
        /// because the services are built before the save is loaded. They read it and never write it (issue #6).
        /// </summary>
        public Func<GameState> State { get; set; }

        /// <summary>Overrides: the bot harness and tests pass a FakeClock and a MemorySaveStore.</summary>
        public IClock Clock { get; set; }
        public ISaveStore Saves { get; set; }
        public IAnalytics Analytics { get; set; }

        /// <summary>The network transport. Local mode never calls it (08 AC 24); Remote's ApiClient sits on it (TODO(#143)).</summary>
        public INetTransport Network { get; set; }

        /// <summary>Debug builds: receipts are granted from this catalog into the local inbox (FakeReceiptValidator).</summary>
        public Func<string, GrantLine[]> DebugReceiptCatalog { get; set; }

        /// <summary>
        /// Remote mode wiring, supplied by HeroCat.Remote (Runtime cannot reference it). TODO(#143): the ApiClient-based
        /// clients of 01 section 1.2 (ServerClock(api), SummonClient, CloudSaveClient, InboxClient, ReceiptClient,
        /// ConfigClient, DungeonSeedClient, AnalyticsSinks).
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

        // TODO(#92): ISummonService Summon (LocalSummonService in Local mode, SummonClient in Remote).

        public ISaveStore Saves { get; init; }
        public IInboxService Inbox { get; init; }
        public IReceiptValidator Receipts { get; init; }
        public IConfigService Config { get; init; }
        public IDungeonSeedService Dungeons { get; init; }
        public IAnalytics Analytics { get; init; }

        /// <summary>The mode this set was built for (diagnostics only; nothing may branch on it).</summary>
        public BackendMode Mode { get; init; }

        /// <summary>01 section 1.2 Build: picks the implementations for <paramref name="mode"/>.</summary>
        public static GameServices Build(BackendMode mode, ServicesOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (mode == BackendMode.Remote)
            {
                if (options.Remote == null)
                    throw new NotSupportedException("Remote backend not wired: pass ServicesOptions.Remote (TODO(#143))");
                var remote = options.Remote(options);
                return remote with { Mode = BackendMode.Remote };
            }

            var state = options.State ?? (() => null);
            var clock = options.Clock ?? new ServerClock();   // device clock floored at the last save; IsAuthoritative = false
            var inbox = new LocalInboxService();
            return new GameServices
            {
                Mode = BackendMode.Local,
                Clock = clock,
                Saves = options.Saves ?? (options.DataDir != null
                    ? new FileSaveStore(Path.Combine(options.DataDir, "saves"))
                    : (ISaveStore)new MemorySaveStore()),
                Inbox = inbox,
                Receipts = options.DebugReceiptCatalog != null
                    ? new FakeReceiptValidator(inbox, options.DebugReceiptCatalog, clock)
                    : (IReceiptValidator)new DisabledReceiptValidator(),
                Config = new LocalConfigService(options.Content?.Version),
                Dungeons = new LocalDungeonSeedService(state),
                Analytics = options.Analytics ?? NullAnalytics.Instance,
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
                                            Func<GameState> state = null) =>
            Build(BackendMode.Local, new ServicesOptions
            {
                Clock = clock ?? throw new ArgumentNullException(nameof(clock)),
                Saves = store ?? new MemorySaveStore(),
                Analytics = analytics ?? new MemoryAnalytics(),
                State = state,
            });
    }
}
