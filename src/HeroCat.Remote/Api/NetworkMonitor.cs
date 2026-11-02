using System;
using System.Threading;

namespace HeroCat.Remote.Api
{
    /// <summary>
    /// The platform's reachability flag. The client's Unity adapter reads <c>Application.internetReachability</c>
    /// (UnityReachability in HeroCat.Client.Boot); tests use a settable fake. Read on the main thread.
    /// </summary>
    public interface IReachability
    {
        bool IsReachable { get; }
    }

    /// <summary>
    /// 01 section 5.3: publishes <see cref="ConnectivityChanged"/> from the platform reachability plus the last
    /// <see cref="ApiClient"/> outcome. The last outcome since reachability last changed decides: any HTTP response
    /// (whatever its status) means online, a connection failure means offline, and a timeout says nothing; with no outcome
    /// yet, reachability decides. A reachability change in either direction forgets the outcome, so a stale failure does
    /// not keep server-only buttons greyed after the network returns. Advisory: the real test is the request. The ApiClient
    /// reports from the thread pool; <see cref="Poll"/> runs on the main thread (once per frame) and is the only place the
    /// event is raised, so screens can subscribe directly.
    /// </summary>
    public sealed class NetworkMonitor : IDisposable
    {
        const int Unknown = 0, Reached = 1, Failed = 2;

        readonly IReachability _reachability;
        readonly ApiClient _client;
        int _lastOutcome = Unknown;
        bool _lastReachable;
        bool? _published;

        public NetworkMonitor(IReachability reachability, ApiClient client = null)
        {
            _reachability = reachability ?? throw new ArgumentNullException(nameof(reachability));
            _client = client;
            if (_client != null) _client.ServerReached += OnServerReached;
            _lastReachable = _reachability.IsReachable;
        }

        /// <summary>The last published state (true before the first <see cref="Poll"/> when the platform is reachable).</summary>
        public bool Online => _published ?? Compute(_lastReachable);

        /// <summary>Raised by <see cref="Poll"/> on the main thread when the state changes (and on the first poll).</summary>
        public event Action<bool> ConnectivityChanged;

        /// <summary>Feeds an outcome by hand (the ApiClient does this itself when passed to the constructor). Any thread.</summary>
        public void OnServerReached(bool reached) => Interlocked.Exchange(ref _lastOutcome, reached ? Reached : Failed);

        /// <summary>Main thread, once per frame: re-reads reachability and raises <see cref="ConnectivityChanged"/> on a change.</summary>
        public void Poll()
        {
            bool reachable = _reachability.IsReachable;
            if (reachable != _lastReachable) Interlocked.Exchange(ref _lastOutcome, Unknown);
            _lastReachable = reachable;
            bool online = Compute(reachable);
            if (_published == online) return;
            _published = online;
            ConnectivityChanged?.Invoke(online);
        }

        bool Compute(bool reachable)
        {
            int outcome = Volatile.Read(ref _lastOutcome);
            return outcome == Unknown ? reachable : outcome == Reached;
        }

        public void Dispose()
        {
            if (_client != null) _client.ServerReached -= OnServerReached;
        }
    }
}
