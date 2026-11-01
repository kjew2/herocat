using System;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

// ============================================================================================================
// SaveBridge: the ONLY file in the client that knows how a profile is created, loaded, saved or deleted.
// Everything else gets a ready Simulation from ISaveBridge.Open and calls Save on pause / quit / autosave.
// TODO(#77): 02 section 8's LocalSaveStore and its recovery order replace RuntimeSaveBridge's direct use of SaveSystem.
// ============================================================================================================

namespace HeroCat.Client.Core
{
    public interface ISaveBridge
    {
        /// <summary>Shown in the debug panel, e.g. "runtime save (Loaded)".</summary>
        string Description { get; }

        /// <summary>
        /// Load the saved profile, or create a new one (ProfileFactory, with the profile-created grant). Must NOT apply
        /// ApplyServerTime or ComputeOfflineCatchup: the caller does, so the view-model sees the resulting events.
        /// </summary>
        Simulation Open(ContentDb content, IClock clock);

        /// <summary>Persist the current state (synchronous; main thread, between ticks).</summary>
        void Save(Simulation sim, bool ticking);

        /// <summary>Delete the profile; the next Open creates a new one.</summary>
        void DeleteProfile();
    }

    public static class SaveBridgeFactory
    {
        /// <param name="dataDir">Application.persistentDataPath.</param>
        public static ISaveBridge Create(string dataDir) =>
            new RuntimeSaveBridge(new FileSaveStore(System.IO.Path.Combine(dataDir, "saves")));

        /// <summary>
        /// In-memory save round trip (HeroCat.Runtime SaveSelfCheck). False means this build loses save data, most
        /// likely IL2CPP stripping (see Assets/link.xml). Touches no files.
        /// </summary>
        public static bool SelfCheck(ContentDb content, out string report)
        {
            var r = SaveSelfCheck.Run(content);
            report = r.ToString();
            return r.Ok;
        }
    }

    /// <summary>Persists through HeroCat.Runtime (02): CRC-checked save file, .prev backup, quarantine of bad files.</summary>
    public sealed class RuntimeSaveBridge : ISaveBridge
    {
        readonly ISaveStore _store;
        readonly Guid _installId;
        ContentDb _content;
        IClock _clock;

        /// <param name="installId">This install's DeviceId. TODO(#73): persisted install_id file (02 section 8.4).</param>
        public RuntimeSaveBridge(ISaveStore store, Guid? installId = null)
        {
            _store = store;
            _installId = installId ?? Guid.NewGuid();
        }

        public string Description { get; private set; } = "runtime (not opened yet)";

        public Simulation Open(ContentDb content, IClock clock)
        {
            _content = content;
            _clock = clock;
            long now = clock.NowServerSeconds;
            LoadResult load = SaveSystem.Load(_store, content, now);   // SaveTooNewException propagates: never overwrite a newer save
            if (load.HasState)
            {
                Description = "runtime save (" + load.Status + ")";
                return new Simulation(load.State, content);
            }
            var state = ProfileFactory.Create(content, Guid.Empty, now, _installId);
            Description = "runtime save (new profile, " + load.Status + ")";
            var sim = new Simulation(state, content);
            Save(sim, ticking: false);
            return sim;
        }

        public void Save(Simulation sim, bool ticking)
        {
            if (_content == null) return;
            SaveSystem.Write(_store, sim.State, _clock.NowServerSeconds, _content, _installId, ticking);
        }

        public void DeleteProfile() => _store.DeleteAll();
    }

    /// <summary>No persistence: every launch (and every "Reset profile") starts a new profile.</summary>
    public sealed class EphemeralSaveBridge : ISaveBridge
    {
        public string Description => "ephemeral (new profile every launch, saves are no-ops)";

        public Simulation Open(ContentDb content, IClock clock) =>
            new Simulation(ProfileFactory.Create(content, Guid.Empty, clock.NowServerSeconds, Guid.NewGuid()), content);

        public void Save(Simulation sim, bool ticking) { }

        public void DeleteProfile() { }
    }
}
