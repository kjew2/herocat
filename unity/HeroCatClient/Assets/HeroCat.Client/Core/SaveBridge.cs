using System;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using HeroCat.Sim.Time;
using HeroCat.Runtime.Save;

// ============================================================================================================
// SaveBridge: the ONLY file in the client that knows how a profile is created, loaded, saved or deleted.
// Everything else gets a ready Simulation from ISaveBridge.Open and calls Save on pause / quit / autosave /
// after pulls. To wire HeroCat.Runtime (specs/06 FileSaveStore / GameSession) in:
//   1. add "HeroCat.Runtime" to Core/HeroCat.Client.Core.asmdef "references" (and the package to Packages/manifest.json);
//   2. implement ISaveBridge with the Runtime store and return it from SaveBridgeFactory.Create.
// Nothing outside this file needs to change.
// ============================================================================================================

namespace HeroCat.Client.Core
{
    public interface ISaveBridge
    {
        /// <summary>Shown in the debug panel, e.g. "ephemeral (new profile every launch)".</summary>
        string Description { get; }

        /// <summary>
        /// Load the saved profile, or create a new one (with the profile-created tutorial grants applied), and return
        /// a Simulation that publishes into <paramref name="events"/>. Must NOT call SyncWallClock: the caller does
        /// that right after so the offline-earnings popup sees the OfflineGoldCredited event.
        /// </summary>
        Simulation Open(ContentDb content, IEventSink events, IClock clock);

        /// <summary>Persist the current state (synchronous; called on the main thread between ticks).</summary>
        void Save(Simulation sim);

        /// <summary>Delete the profile; the next Open creates a new one.</summary>
        void DeleteProfile();
    }

    public static class SaveBridgeFactory
    {
        /// <param name="dataDir">Application.persistentDataPath.</param>
        public static ISaveBridge Create(string dataDir) =>
            new RuntimeSaveBridge(new FileSaveStore(System.IO.Path.Combine(dataDir, "saves")));

        /// <summary>
        /// In-memory save round trip (HeroCat.Runtime SaveSelfCheck). False means this build loses save data,
        /// most likely IL2CPP stripping (see Assets/link.xml). Touches no files.
        /// </summary>
        public static bool SelfCheck(ContentDb content, out string report)
        {
            var r = SaveSelfCheck.Run(content);
            report = r.ToString();
            return r.Ok;
        }
    }

    /// <summary>
    /// Persists through HeroCat.Runtime (specs/06): CRC-checked save file, .prev backup, quarantine of bad files,
    /// migrations. Offline catch-up is left to the caller's SyncWallClock: the restored sim's virtual time stops at
    /// the save, so the gap to "now" is credited there.
    /// </summary>
    public sealed class RuntimeSaveBridge : ISaveBridge
    {
        readonly ISaveStore _store;
        ContentDb _content;
        IClock _clock;
        SaveMeta _meta;

        public RuntimeSaveBridge(ISaveStore store) { _store = store; }

        public string Description { get; private set; } = "runtime (not opened yet)";

        public Simulation Open(ContentDb content, IEventSink events, IClock clock)
        {
            _content = content;
            _clock = clock;
            long now = clock.NowServerSeconds;
            LoadResult load;
            try { load = SaveSystem.Load(_store, content, now); }
            catch (SaveTooNewException) { throw; }   // a newer build wrote this save; never overwrite it
            catch (Exception) { load = null; }

            if (load != null && load.HasState)
            {
                _meta = load.Meta;
                Description = "runtime save (" + load.Status + ")";
                return new Simulation(content, load.State, events);
            }

            var state = GameState.NewProfile(content, Guid.NewGuid(), now);
            var sim = new Simulation(content, state, events);
            sim.ApplyTutorialGrants();
            _meta = new SaveMeta { LastSavedAtServerSec = now, DeviceInstallId = Guid.NewGuid() };
            Description = "runtime save (new profile" + (load != null ? ", " + load.Status : "") + ")";
            Save(sim);
            return sim;
        }

        public void Save(Simulation sim)
        {
            if (_content == null || _meta == null) return;
            SaveSystem.Write(_store, sim.State, _meta, _clock.NowServerSeconds, _content.Version);
        }

        public void DeleteProfile()
        {
            _store.DeleteAll();
            _meta = null;
        }
    }

    /// <summary>POC placeholder: no persistence. Every launch (and every "Reset profile") starts a new profile.</summary>
    public sealed class EphemeralSaveBridge : ISaveBridge
    {
        public string Description => "ephemeral (new profile every launch, saves are no-ops)";

        public Simulation Open(ContentDb content, IEventSink events, IClock clock)
        {
            var state = GameState.NewProfile(content, Guid.NewGuid(), clock.NowServerSeconds);
            var sim = new Simulation(content, state, events);
            sim.ApplyTutorialGrants();
            return sim;
        }

        public void Save(Simulation sim) { }

        public void DeleteProfile() { }
    }
}
