using System;
using HeroCat.Remote.Api;
using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Client.Boot
{
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

        /// <summary>The composition root call (01 section 1.2): GameServices.Build for this build's mode.</summary>
        /// <param name="dataDir">Application.persistentDataPath.</param>
        /// <param name="wrapClock">Debug builds: the client's OffsetClock wrapper (ServicesOptions.WrapClock).</param>
        public static GameServices Create(string dataDir, ContentDb content, Func<GameState> state, Func<IClock, IClock> wrapClock = null)
        {
            var options = new ServicesOptions { DataDir = dataDir, Content = content, State = state, WrapClock = wrapClock };
#if HEROCAT_BACKEND_REMOTE
            // No deployed server yet: the offline handler makes every ApiClient call fail as NET_OFFLINE, so the game runs
            // on the device clock with local saves and a queued upload. A real build drops Handler (platform HttpClientHandler)
            // and sets BaseUrl per environment, ContentVersion and Credentials (AuthClient, #148), plus options.AnalyticsSalt and
            // options.SessionId (BootOptions.SessionId) so Wire builds the analytics sink (specs/02 section 14.2 item 8, #297).
            var api = new ApiClientOptions
            {
                Handler = new OfflineHandler(),
                ClientVersion = string.IsNullOrEmpty(UnityEngine.Application.version) ? "0.0.0" : UnityEngine.Application.version,
                Locale = () => System.Globalization.CultureInfo.CurrentUICulture.Name,
            };
            options.Remote = o => RemoteGameServices.Wire(o, api, syncNow: true);
            return GameServices.Build(Mode, options);
#else
            return GameServices.Build(Mode, options);
#endif
        }
    }
}
