using HeroCat.Runtime.Save;
using HeroCat.Sim.Content;

// ============================================================================================================
// SaveBridge: the client's build-health check for saves. Loading, creating and writing the profile is Runtime's
// (BootFlow and SessionRunner over 02 section 8's LocalSaveStore, issues #65, #70, #77); the client never touches the
// save files itself.
// ============================================================================================================

namespace HeroCat.Client.Core
{
    public static class SaveBridgeFactory
    {
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
}
