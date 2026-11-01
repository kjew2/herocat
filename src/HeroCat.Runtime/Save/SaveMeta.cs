using System;

namespace HeroCat.Runtime.Save
{
    /// <summary>
    /// Save bookkeeping that is not sim state (specs/06 ProfileHeader fields the sim's GameState does not carry).
    /// Written next to the GameState in the save body: { "SchemaVersion": 1, "Meta": {...}, "State": {...} }.
    /// </summary>
    public sealed class SaveMeta
    {
        /// <summary>IClock time of the last write; the offline catch-up anchor on load (specs/06 section 9.2).</summary>
        public long LastSavedAtServerSec { get; set; }

        /// <summary>+1 per write. Cloud-save tiebreak later (specs/08 section 5).</summary>
        public long SaveSequence { get; set; }

        /// <summary>Per install; cloud-save lineage only.</summary>
        public Guid DeviceInstallId { get; set; }

        /// <summary>Set by any debug/cheat command; excludes the profile from tuning data.</summary>
        public bool DebugTainted { get; set; }

        public SaveMeta Clone() => (SaveMeta)MemberwiseClone();
    }
}
