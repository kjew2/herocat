using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Services;

namespace HeroCat.Client.Boot
{
    public enum BackendMode { Local, Remote }

    /// <summary>
    /// Build-time backend selection (specs/08 section 1). Local by default; add the scripting define symbol
    /// HEROCAT_BACKEND_REMOTE (Player Settings > Other Settings > Scripting Define Symbols) for a Remote build.
    /// Nothing else in the client branches on the mode.
    /// </summary>
    public static class Backend
    {
#if HEROCAT_BACKEND_REMOTE
        public const BackendMode Mode = BackendMode.Remote;
#else
        public const BackendMode Mode = BackendMode.Local;
#endif

        /// <param name="dataDir">Application.persistentDataPath.</param>
        public static GameServices Create(string dataDir)
        {
#if HEROCAT_BACKEND_REMOTE
            // No vendor yet (UGS vs Nakama spike, specs/08 section 10): the offline transport makes every remote call
            // fail as Offline, so the game runs on the device clock with local saves and a queued upload.
            var services = RemoteGameServices.Create(dataDir, OfflineTransport.Instance);
            ((ServerClock)services.Clock).TrySync();
            return services;
#else
            return GameServices.Local(dataDir);
#endif
        }
    }
}
