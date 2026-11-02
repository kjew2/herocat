using System;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Runtime;
using HeroCat.Runtime.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Services
{
    /// <summary>
    /// The ConfigClient of 01 sections 8.1 step 2 and 8.12 (issue #144): <see cref="IConfigService"/> over 08 section 3.4's
    /// <c>GET /config</c> (auth <c>none</c>; the cached access token is sent when there is one, so the server can apply the
    /// rollout bucket, issue #31; the client never computes a bucket). One attempt, never retried, bounded by
    /// CONFIG_FETCH_TIMEOUT_S: the refresh cadence is the retry. A good answer replaces <see cref="Current"/> at once (the
    /// kill switches apply without a restart, 8.12 step 1) and is written to the <see cref="IConfigCache"/>. Any failure
    /// (offline, timeout, an error status, an unreadable body) keeps the last answer while it is under
    /// CONFIG_CACHE_MAX_AGE_S old, else falls back to the defaults: everything enabled, no gate. Never throws for a network
    /// failure; only the caller's own cancellation throws.
    /// </summary>
    public sealed class RemoteConfigService : IConfigService
    {
        public const string Route = "/config";

        readonly ApiClient _client;
        readonly IConfigCache _cache;
        readonly Func<long> _now;
        readonly Func<string> _bundledVersion;
        readonly TimeSpan _timeout;
        volatile ClientConfig _current;

        /// <param name="cache">The persisted last answer; null keeps it in memory only.</param>
        /// <param name="nowServerSeconds">Server time for the cache's age (the ServerClock).</param>
        /// <param name="bundledContentVersion">The running content's version, the ContentVersion of a body without <c>content</c> and of the defaults.</param>
        /// <param name="timeout">The fetch budget; null is CONFIG_FETCH_TIMEOUT_S (tests shorten it).</param>
        public RemoteConfigService(ApiClient client, IConfigCache cache, Func<long> nowServerSeconds, Func<string> bundledContentVersion = null,
                                   TimeSpan? timeout = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _cache = cache ?? new MemoryConfigCache();
            _now = nowServerSeconds ?? throw new ArgumentNullException(nameof(nowServerSeconds));
            _bundledVersion = bundledContentVersion ?? (() => "");
            _timeout = timeout ?? TimeSpan.FromSeconds(RuntimeConfig.CONFIG_FETCH_TIMEOUT_S);
            _current = FromCache() ?? ClientConfig.Defaults(_bundledVersion());
        }

        public ClientConfig Current => _current;

        public KillSwitches Flags => _current.Flags;

        /// <summary>Fetches run so far that got a usable answer (diagnostics, tests).</summary>
        public int Successes => _successes;

        int _successes;

        public async Task<ClientConfig> FetchAsync(CancellationToken ct = default)
        {
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                budget.CancelAfter(_timeout);
                try
                {
                    var req = ApiRequest.Get(Route) with { NoRetry = true, Timeout = _timeout };
                    var r = await _client.SendAsync<JToken>(req, budget.Token).ConfigureAwait(false);
                    if (r.Ok && r.Body is JObject body)
                    {
                        string json = body.ToString(Formatting.None);
                        long at = ConfigBody.ServerTimeOf(json);
                        if (at <= 0) at = r.ServerTimeSec > 0 ? r.ServerTimeSec : _now();
                        var parsed = ConfigBody.Parse(json, _bundledVersion(), ConfigSource.Fetched, at);
                        _current = parsed;
                        Interlocked.Increment(ref _successes);
                        try { _cache.Write(new CachedConfig(json, at)); }
                        catch (Exception) { /* a full disk loses the cache, not the answer */ }
                        return parsed;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // our own CONFIG_FETCH_TIMEOUT_S budget ran out: degrade below
                }
                catch (FormatException)
                {
                    // an unreadable body degrades like no answer
                }
            }
            Degrade();
            return _current;
        }

        /// <summary>No answer: keep the in-memory or persisted copy while fresh, else the defaults (01 section 8.1 step 2).</summary>
        void Degrade()
        {
            long now = _now();
            var mem = _current;
            if ((mem.Source == ConfigSource.Fetched || mem.Source == ConfigSource.Cached) && ConfigBody.IsFresh(mem.FetchedAtServerSeconds, now)) return;
            _current = FromCache() ?? ClientConfig.Defaults(_bundledVersion());
        }

        ClientConfig FromCache()
        {
            CachedConfig c;
            try { c = _cache.Read(); }
            catch (Exception) { return null; }
            if (c == null || !ConfigBody.IsFresh(c.FetchedAtServerSeconds, _now())) return null;
            try { return ConfigBody.Parse(c.Body, _bundledVersion(), ConfigSource.Cached, c.FetchedAtServerSeconds); }
            catch (FormatException) { return null; }
        }
    }
}
