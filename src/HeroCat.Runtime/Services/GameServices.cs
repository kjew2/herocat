using System;
using System.IO;
using HeroCat.Runtime.Save;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// The seams the M0 skeleton needs (clock, save store, analytics). TODO(#66): the full composition root of 01 section
    /// 1.2 with BackendMode and the seven server-sensitive interfaces (ISummonService, IInboxService, ...).
    /// </summary>
    public sealed class GameServices
    {
        public IClock Clock { get; set; }
        /// <summary>The raw slot store; the session builds its LocalSaveStore (02 section 8) on top.</summary>
        public ISaveSlotStore SaveStore { get; set; }
        public IAnalytics Analytics { get; set; }

        /// <summary>This install's id (02 section 8.4). Guid.Empty: the session uses a fresh one.</summary>
        public Guid InstallId { get; set; }
        /// <summary>The summon seam (04 section 12). Null: GameSession uses a LocalSummonService over its own state (M0).</summary>
        public HeroCat.Runtime.Summons.ISummonService Summon { get; set; }

        /// <summary>Device build: device clock, saves under {dataDir}/saves.</summary>
        public static GameServices Local(string dataDir, IAnalytics analytics = null) => new GameServices
        {
            Clock = new SystemClock(),
            SaveStore = new FileSaveStore(Path.Combine(dataDir ?? throw new ArgumentNullException(nameof(dataDir)), "saves")),
            Analytics = analytics ?? NullAnalytics.Instance,
            InstallId = HeroCat.Runtime.Profile.InstallId.LoadOrCreate(dataDir),
        };

        /// <summary>Tests and the bot harness: fake clock, in-memory save, in-memory analytics.</summary>
        public static GameServices InMemory(FakeClock clock, MemorySaveStore store = null, IAnalytics analytics = null) => new GameServices
        {
            Clock = clock ?? throw new ArgumentNullException(nameof(clock)),
            SaveStore = store ?? new MemorySaveStore(),
            Analytics = analytics ?? new MemoryAnalytics(),
        };
    }

    /// <summary>Shared error kinds. Only remote implementations raise these; local ones never do.</summary>
    public enum ServiceErrorKind { Offline, Timeout, Rejected, Conflict, Unsupported, Internal }

    public sealed class ServiceException : Exception
    {
        public ServiceException(ServiceErrorKind kind, string code, string message = null)
            : base(message ?? $"{kind}: {code}")
        {
            Kind = kind;
            Code = code;
        }

        public ServiceErrorKind Kind { get; }

        /// <summary>Machine-readable, e.g. "CannotAfford", "BadBanner".</summary>
        public string Code { get; }

        public bool Retryable => Kind == ServiceErrorKind.Offline || Kind == ServiceErrorKind.Timeout || Kind == ServiceErrorKind.Internal;
    }
}
