using HeroCat.Runtime.Save;
using HeroCat.Sim.Content;

// ============================================================================================================
// SaveBridge: the client's build-health check for saves. Loading, creating and writing the profile is Runtime's
// (BootFlow and SessionRunner, issues #65 and #70); the client never touches the save files itself.
// TODO(#77): 02 section 8's LocalSaveStore and its recovery order replace SaveSystem inside Runtime.
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
