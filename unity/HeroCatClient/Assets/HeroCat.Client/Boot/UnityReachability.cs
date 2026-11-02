using HeroCat.Remote.Api;
using UnityEngine;

namespace HeroCat.Client.Boot
{
    /// <summary>
    /// The thin Unity adapter behind <see cref="NetworkMonitor"/> (01 section 5.3): the platform's reachability flag.
    /// Advisory only (Unity documents it as "can a connection be attempted", not "is the server up"); the monitor combines
    /// it with the last ApiClient outcome. Main thread.
    /// </summary>
    public sealed class UnityReachability : IReachability
    {
        public bool IsReachable => Application.internetReachability != NetworkReachability.NotReachable;
    }
}
