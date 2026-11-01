using System;
using System.IO;
using HeroCat.Runtime.Save;
using HeroCat.Sim.Gacha;
using HeroCat.Sim.Time;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// Composition root for the backend seams (specs/08 section 1). The sim depends on interfaces only; the
    /// client or bot picks implementations here. P1 adds a Remote(...) factory in a separate assembly with
    /// ServerClock, RemoteGachaService, CloudSaveStore and a remote analytics sink: no change to the sim.
    ///
    /// Reused from HeroCat.Sim: IClock / SystemClock / FakeClock (HeroCat.Sim.Time) and IGachaService /
    /// LocalGachaService (HeroCat.Sim.Gacha). Remote config and receipt validation are P1 and not modelled.
    /// </summary>
    public sealed class GameServices
    {
        public IClock Clock { get; set; }
        public IGachaService Gacha { get; set; }
        public ISaveStore SaveStore { get; set; }
        public IAnalytics Analytics { get; set; }

        /// <summary>P0 device build: wall clock, local gacha, saves under {dataDir}/saves.</summary>
        public static GameServices Local(string dataDir, IAnalytics analytics = null) => new GameServices
        {
            Clock = new SystemClock(),
            Gacha = new LocalGachaService(),
            SaveStore = new FileSaveStore(Path.Combine(dataDir ?? throw new ArgumentNullException(nameof(dataDir)), "saves")),
            Analytics = analytics ?? NullAnalytics.Instance,
        };

        /// <summary>Tests and the bot harness: fake clock, in-memory save, in-memory analytics.</summary>
        public static GameServices InMemory(FakeClock clock, MemorySaveStore store = null, IAnalytics analytics = null) => new GameServices
        {
            Clock = clock ?? throw new ArgumentNullException(nameof(clock)),
            Gacha = new LocalGachaService(),
            SaveStore = store ?? new MemorySaveStore(),
            Analytics = analytics ?? new MemoryAnalytics(),
        };
    }

    /// <summary>specs/08 shared error kinds. Only remote implementations (P1) raise these; local ones never do.</summary>
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
