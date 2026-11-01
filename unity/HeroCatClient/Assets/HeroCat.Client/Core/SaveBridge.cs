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
            new RuntimeSaveBridge(new FileSaveStore(System.IO.Path.Combine(dataDir, "saves")), InstallId.LoadOrCreate(dataDir));

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

    /// <summary>
    /// Persists through HeroCat.Runtime's LocalSaveStore (02 section 8): CRC-checked save file, .prev backup, repairs,
    /// quarantine of bad files, the recovery order of 8.3.
    /// </summary>
    public sealed class RuntimeSaveBridge : ISaveBridge
    {
        readonly ISaveSlotStore _slots;
        readonly Guid _installId;
        LocalSaveStore _store;

        /// <param name="installId">This install's DeviceId (the install_id file, 02 section 8.4). Null: a fresh one.</param>
        public RuntimeSaveBridge(ISaveSlotStore slots, Guid? installId = null)
        {
            _slots = slots;
            _installId = installId ?? Guid.NewGuid();
        }

        public string Description { get; private set; } = "runtime (not opened yet)";

        public Simulation Open(ContentDb content, IClock clock)
        {
            _store = new LocalSaveStore(_slots, clock, _installId);
            LoadResult load = _store.Load(clock.NowServerSeconds, content);
            // Never overwrite a newer save: the store refuses writes after TooNew and the caller shows SAVE_TOO_NEW.
            if (load.Status == LoadStatus.TooNew) throw new SaveTooNewException(load.TooNewVersion, SaveSchema.Current);
            var sim = new Simulation(load.State, content);
            if (load.IsNewProfile)
            {
                Description = "runtime save (new profile)";
                Save(sim, ticking: false);
            }
            else Description = "runtime save (" + load.Status + ")";
            return sim;
        }

        public void Save(Simulation sim, bool ticking)
        {
            if (_store == null) return;
            _store.Save(sim.State, ticking ? SaveTrigger.Pause : SaveTrigger.Resume);
        }

        public void DeleteProfile() => _slots.DeleteAll();
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
