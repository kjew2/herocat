using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Runtime;
using HeroCat.Runtime.Account;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Session;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Services
{
    /// <summary>08 section 2's auth routes, relative to the <c>/v1</c> base URL.</summary>
    public static class AuthRoutes
    {
        public const string Guest = "/auth/guest";            // POST, auth none, idempotent by X-Request-Id -> 201
        public const string Refresh = "/auth/refresh";        // POST, auth none, not idempotent (rotation) -> 200
        public const string Logout = "/auth/logout";          // POST, jwt, idempotent -> 204
        public const string AccountDelete = "/account/delete"; // POST, jwt+device, idempotent -> {"graceUntil"}
        public const string Devices = "/devices";             // POST, jwt, idempotent -> 204 (registers or rotates the device key)
    }

    /// <summary>Outcome of <see cref="AuthClient.EnsureDeviceRegisteredAsync"/>.</summary>
    public enum DeviceRegistration
    {
        /// <summary>The server holds this install's current key for the session's player.</summary>
        Registered,

        /// <summary>No session yet: <c>POST /auth/guest</c> registers the key when it creates one.</summary>
        NoSession,

        /// <summary>
        /// The server refused with a final answer (<c>403 KEY_ROTATION_NOT_ALLOWED</c>, <c>409 DEVICE_ALREADY_BOUND</c>, ...).
        /// Not retried again in this process for this session and key; uploads go ahead and the server records
        /// <c>hmacValid = false</c> (02 section 5.1: an HMAC mismatch is never a rejection).
        /// </summary>
        Refused,
    }

    /// <summary>Construction options of <see cref="AuthClient"/>; the defaults are production values.</summary>
    public sealed class AuthClientOptions
    {
        /// <summary><c>model</c> and <c>os</c> of <c>POST /auth/guest</c> (the device row); null sends nothing.</summary>
        public Func<string> Model { get; set; }
        public Func<string> Os { get; set; }

        /// <summary>ISO 3166-1 alpha-2 for <c>/auth/guest</c>; null or empty: the server's <c>ZZ</c>.</summary>
        public Func<string> Region { get; set; }

        /// <summary>Measures the refresh retry window. Default Stopwatch.</summary>
        public IMonoClock Mono { get; set; }

        /// <summary>Waits between refresh retries. Default Task.Delay; tests record and return at once.</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; }

        /// <summary>Full-jitter source in [0, 1) for the refresh backoff. Default 0.5.</summary>
        public Func<double> Jitter { get; set; }
    }

    /// <summary>
    /// The AuthClient of 01 sections 5.1 and 8.1 step 3 (issue #148) over 08 section 2's wire contract: the
    /// <see cref="IApiCredentials"/> the <see cref="ApiClient"/> refreshes through, and the Remote
    /// <see cref="IAccountService"/>. Tokens persist in an <see cref="ITokenStore"/> (<c>tokens.json</c> in secure storage,
    /// 02 section 8.4) before they are used.
    /// <list type="bullet">
    /// <item><b>Guest</b> (<c>POST /auth/guest</c>): registers this install's <c>X-Device-Id</c> and HMAC key. Its
    /// request id is stored before the call, so a response lost to a timeout or a kill is replayed by the server's
    /// idempotency store on the next attempt (within IDEMPOTENCY_TTL_H) instead of answering
    /// <c>DEVICE_ALREADY_BOUND</c>. A final 4xx drops the id (the server stored that answer and would replay it).</item>
    /// <item><b>Refresh</b> (<c>POST /auth/refresh</c>, rotation): one at a time (a 401 that arrives while one runs joins
    /// it). Offline, timeout, 5xx and 429 keep the presented token and retry it, with backoff, only while a retry can
    /// still land inside the server's <c>REFRESH_REUSE_GRACE_S</c>: a retry starts at most
    /// <c>REFRESH_REUSE_GRACE_S - HTTP_TIMEOUT_S</c> after the first send. That is the lost-response case of 08 section 2.1
    /// (issue #273): the server rotates the family's live token and answers a fresh pair. A 401 (<c>TOKEN_REUSED</c>,
    /// <c>TOKEN_REVOKED</c>, <c>TOKEN_EXPIRED</c>, <c>UNAUTHENTICATED</c>) or <c>403 DEVICE_MISMATCH</c> ends the session:
    /// the tokens are cleared and <see cref="SessionLost"/> fires.</item>
    /// <item><b>Device registration</b> (<c>POST /devices</c>, 02 section 10.1): the server must hold this install's HMAC
    /// key before a save upload, or every upload is flagged <c>HMAC_INVALID</c>. The guest call registers it; the tokens
    /// record the registered key's <see cref="DeviceKey.KeyId"/>, and <see cref="EnsureDeviceRegisteredAsync"/> sends
    /// <c>POST /devices</c> when the current key is not the recorded one (a key replaced after the guest call, tokens from a
    /// build before this record). Idempotent: the same request id is reused for the same session and key until it lands,
    /// and the server answers the same key again with 204. The cloud save awaits it before each upload. TODO(#196): a
    /// <c>/auth/link-login</c> success registers the key too and should store <c>RegisteredKeyId</c> like the guest call.</item>
    /// <item><b>Logout</b>: one best-effort <c>POST /auth/logout {refreshToken}</c>, then the tokens are cleared whatever
    /// the answer (the server's 204 also covers an unknown token).</item>
    /// <item><b>Delete</b>: <c>POST /account/delete {}</c>; tokens are cleared only on success.</item>
    /// </list>
    /// Linking (<c>/auth/link</c>, <c>/auth/link-login</c>, <c>/auth/merge</c>) is issue #196. Thread-safe.
    /// </summary>
    public sealed class AuthClient : IApiCredentials, IAccountService
    {
        readonly ApiClient _client;
        readonly ITokenStore _store;
        readonly byte[] _deviceKey;
        readonly string _keyId;
        readonly string _deviceId;
        readonly AuthClientOptions _o;
        readonly IMonoClock _mono;
        readonly Func<TimeSpan, CancellationToken, Task> _delay;
        readonly Func<double> _jitter;
        readonly object _lock = new object();
        AuthTokens _tokens;
        Task<SignInResult> _refreshing;
        Task<SignInResult> _guesting;
        Task<DeviceRegistration> _registering;
        string _registerFor, _registerRequestId, _refusedFor;   // scoped "{playerId}:{keyId}"

        public AuthClient(ApiClient client, ITokenStore store, byte[] deviceKey, AuthClientOptions options = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (deviceKey == null || deviceKey.Length != DeviceKey.KeyBytes)
                throw new ArgumentException($"the device key is {DeviceKey.KeyBytes} bytes (02 section 10)", nameof(deviceKey));
            _deviceKey = (byte[])deviceKey.Clone();
            _keyId = DeviceKey.KeyId(_deviceKey);
            if (!Guid.TryParse(client.Options.DeviceId, out var device) || device == Guid.Empty)
                throw new ArgumentException("ApiClientOptions.DeviceId must be the install id (a GUID)", nameof(client));
            _deviceId = device.ToString("D");
            _o = options ?? new AuthClientOptions();
            _mono = _o.Mono ?? StopwatchMonoClock.Instance;
            _delay = _o.Delay ?? Task.Delay;
            _jitter = _o.Jitter ?? (() => 0.5);
            _tokens = store.Load();
        }

        /// <summary>The latest refresh retry may start this long after the first send (see the class summary).</summary>
        public TimeSpan RefreshRetryWindow =>
            TimeSpan.FromSeconds(RuntimeConfig.REFRESH_REUSE_GRACE_S) - _client.Options.Timeout;

        public string AccessToken => Volatile.Read(ref _tokens)?.AccessToken;

        public Guid PlayerId => Volatile.Read(ref _tokens)?.PlayerId ?? Guid.Empty;

        public bool HasSession => Volatile.Read(ref _tokens)?.HasSession ?? false;

        /// <summary>The access token's expiry (unix seconds), 0 when none.</summary>
        public long AccessExpiresAt => Volatile.Read(ref _tokens)?.AccessExpiresAt ?? 0;

        public event Action<SignInResult> SessionLost;

        // ------------------------------------------------------------------------------------- sign-in (8.1 step 3)

        /// <summary>
        /// Refresh or guest, shared with any call already running. Bounded by the ApiClient's retry budget (guest) or the
        /// refresh window, so <paramref name="ct"/> is not observed: a half-done rotation must finish and be stored.
        /// </summary>
        public async Task<SignInResult> SignInAsync(CancellationToken ct = default)
        {
            var t = Volatile.Read(ref _tokens);
            if (t != null && t.HasSession) return await Shared(ref _refreshing, RefreshCoreAsync).ConfigureAwait(false);
            return await Shared(ref _guesting, GuestCoreAsync).ConfigureAwait(false);
        }

        /// <summary>IApiCredentials: the ApiClient's 401 TOKEN_EXPIRED path. True when a new access token is stored.</summary>
        public async Task<bool> RefreshAsync(CancellationToken ct)
        {
            if (!HasSession) return false;
            var r = await Shared(ref _refreshing, RefreshCoreAsync).ConfigureAwait(false);
            return r.Status == SignInStatus.SignedIn;
        }

        /// <summary>Single flight: a call while one runs joins it. The shared task ignores any one caller's cancellation.</summary>
        Task<SignInResult> Shared(ref Task<SignInResult> slot, Func<Task<SignInResult>> start)
        {
            lock (_lock)
            {
                if (slot == null || slot.IsCompleted) slot = Task.Run(start);
                return slot;
            }
        }

        async Task<SignInResult> RefreshCoreAsync()
        {
            var presented = Volatile.Read(ref _tokens);
            if (presented == null || !presented.HasSession) return new SignInResult(SignInStatus.SignInRequired, Guid.Empty, Code: "UNAUTHENTICATED");
            string requestId = ApiRequest.NewRequestId();   // not idempotent server-side; the id is for tracing only
            var body = new JObject { ["refreshToken"] = presented.RefreshToken, ["deviceId"] = _deviceId };
            long start = _mono.Ticks;
            for (int retry = 0; ; retry++)
            {
                var req = ApiRequest.Mutation(HttpMethod.Post, AuthRoutes.Refresh, body, requestId, requiresAuth: false) with { NoRetry = true };
                var r = await _client.SendAsync<JToken>(req, CancellationToken.None).ConfigureAwait(false);
                string code;
                int? retryAfter = null;
                if (r.Ok)
                {
                    if (TryReadTokens(r.Body, out string access, out string refresh, out long exp, out _))
                    {
                        // a registration that finished meanwhile keeps its record
                        string registered = Volatile.Read(ref _tokens)?.RegisteredKeyId ?? presented.RegisteredKeyId;
                        Store(presented with { AccessToken = access, RefreshToken = refresh, AccessExpiresAt = exp, RegisteredKeyId = registered });
                        return new SignInResult(SignInStatus.SignedIn, presented.PlayerId);
                    }
                    code = ErrorClassifier.SRV_UNEXPECTED;   // the server rotated, but the pair is unreadable: like a lost response
                }
                else
                {
                    var e = r.Error;
                    if (r.Status == 401 || (r.Status == 403 && e.Code == "DEVICE_MISMATCH"))
                    {
                        var lost = new SignInResult(SignInStatus.SignInRequired, Guid.Empty, Code: e.Code);
                        if (ClearIf(presented)) SessionLost?.Invoke(lost);
                        return lost;
                    }
                    if (!IsTransient(e)) return Offline(presented, e.Code);
                    code = e.Code;
                    retryAfter = e.RetryAfterSec;
                }
                if (retry >= RuntimeConfig.HTTP_RETRY_MAX) return Offline(presented, code);
                var wait = retryAfter is int s && s >= 0 ? TimeSpan.FromSeconds(s) : ApiClient.BackoffDelay(retry, _jitter());
                var elapsed = TimeSpan.FromSeconds((_mono.Ticks - start) / (double)_mono.Frequency);
                if (elapsed + wait > RefreshRetryWindow) return Offline(presented, code);
                await _delay(wait, CancellationToken.None).ConfigureAwait(false);
            }
        }

        async Task<SignInResult> GuestCoreAsync()
        {
            var before = Volatile.Read(ref _tokens);
            if (before != null && before.HasSession) return await RefreshCoreAsync().ConfigureAwait(false);
            var pending = (before ?? new AuthTokens()) with { PendingGuestRequestId = before?.PendingGuestRequestId ?? ApiRequest.NewRequestId() };
            TrySave(pending);   // before the call: a lost 201 is replayed on the next attempt
            Volatile.Write(ref _tokens, pending);

            var body = new JObject
            {
                ["deviceId"] = _deviceId,
                ["deviceKey"] = Convert.ToBase64String(_deviceKey),
                ["clientVersion"] = _client.Options.ClientVersion,
            };
            AddIfSet(body, "model", _o.Model);
            AddIfSet(body, "os", _o.Os);
            AddIfSet(body, "region", _o.Region);
            var req = ApiRequest.Mutation(HttpMethod.Post, AuthRoutes.Guest, body, pending.PendingGuestRequestId, requiresAuth: false);
            var r = await _client.SendAsync<JToken>(req, CancellationToken.None).ConfigureAwait(false);
            if (r.Ok)
            {
                if (!TryReadTokens(r.Body, out string access, out string refresh, out long exp, out Guid player) || player == Guid.Empty)
                    return Offline(pending, ErrorClassifier.SRV_UNEXPECTED);
                Store(new AuthTokens { PlayerId = player, AccessToken = access, RefreshToken = refresh, AccessExpiresAt = exp, RegisteredKeyId = _keyId });
                return new SignInResult(SignInStatus.SignedIn, player, NewAccount: true);
            }

            var e = r.Error;
            bool storedByServer = r.Status >= 400 && r.Status < 500 && r.Status != 429 && e.Code != "IDEMPOTENCY_IN_FLIGHT";
            if (storedByServer)
            {
                var dropped = pending with { PendingGuestRequestId = null };
                TrySave(dropped);
                Volatile.Write(ref _tokens, dropped);
            }
            // DEVICE_ALREADY_BOUND: this install has an account but no tokens for it (lost, or a logout whose POST /auth/logout
            // never reached the server; a delivered logout releases the device, 00 decision 117).
            if (e.Code == "DEVICE_ALREADY_BOUND") return new SignInResult(SignInStatus.SignInRequired, Guid.Empty, Code: e.Code);
            return Offline(pending, e.Code);
        }

        static void AddIfSet(JObject body, string name, Func<string> value)
        {
            string v = value?.Invoke();
            if (!string.IsNullOrEmpty(v)) body[name] = v;
        }

        /// <summary>Offline, timeout, 5xx, 429 and 409 in-flight: the server may answer the same request later.</summary>
        static bool IsTransient(ApiError e) =>
            e.Class == ErrorClass.Offline || e.Class == ErrorClass.Timeout || e.Class == ErrorClass.ServerError || e.Class == ErrorClass.Retryable;

        static SignInResult Offline(AuthTokens t, string code) => new SignInResult(SignInStatus.Offline, t?.PlayerId ?? Guid.Empty, Code: code);

        /// <summary>08 section 2.2's guest 201 / refresh 200: <c>accessToken</c>, <c>refreshToken</c>, <c>accessExpiresAt</c>, guest <c>playerId</c>.</summary>
        static bool TryReadTokens(JToken body, out string access, out string refresh, out long expiresAt, out Guid playerId)
        {
            access = refresh = null;
            expiresAt = 0;
            playerId = Guid.Empty;
            if (!(body is JObject o)) return false;
            access = o["accessToken"]?.Type == JTokenType.String ? (string)o["accessToken"] : null;
            refresh = o["refreshToken"]?.Type == JTokenType.String ? (string)o["refreshToken"] : null;
            if (o["accessExpiresAt"]?.Type == JTokenType.Integer) expiresAt = (long)o["accessExpiresAt"];
            if (o["playerId"]?.Type == JTokenType.String) Guid.TryParse((string)o["playerId"], out playerId);
            return !string.IsNullOrEmpty(access) && !string.IsNullOrEmpty(refresh);
        }

        // ------------------------------------------------------------------------------------- device registration (02 section 10.1)

        /// <summary>
        /// Makes sure the server holds this install's HMAC key for the session's player: nothing to do when the tokens
        /// record this key as registered, else <c>POST /devices {deviceId, deviceKey, model, os, clientVersion}</c> (08
        /// section 2.2), shared with any call already running. Throws <see cref="ServiceException"/> on a transient failure
        /// (offline, timeout, 5xx, 429, 401) once the ApiClient's retries are spent: the caller waits and asks again later,
        /// and the retry reuses the request id. <paramref name="ct"/> is not observed (the record must be stored).
        /// </summary>
        public Task<DeviceRegistration> EnsureDeviceRegisteredAsync(CancellationToken ct = default)
        {
            var t = Volatile.Read(ref _tokens);
            if (t == null || !t.HasSession) return Task.FromResult(DeviceRegistration.NoSession);
            if (t.RegisteredKeyId == _keyId) return Task.FromResult(DeviceRegistration.Registered);
            string scope = t.PlayerId.ToString("N") + ":" + _keyId;
            lock (_lock)
            {
                if (_refusedFor == scope) return Task.FromResult(DeviceRegistration.Refused);
                if (_registering == null || _registering.IsCompleted) _registering = Task.Run(() => RegisterCoreAsync(t.PlayerId, scope));
                return _registering;
            }
        }

        async Task<DeviceRegistration> RegisterCoreAsync(Guid player, string scope)
        {
            string requestId;
            lock (_lock)
            {
                if (_registerFor != scope)
                {
                    _registerFor = scope;
                    _registerRequestId = ApiRequest.NewRequestId();
                }
                requestId = _registerRequestId;
            }
            var body = new JObject
            {
                ["deviceId"] = _deviceId,
                ["deviceKey"] = Convert.ToBase64String(_deviceKey),
                ["clientVersion"] = _client.Options.ClientVersion,
            };
            AddIfSet(body, "model", _o.Model);
            AddIfSet(body, "os", _o.Os);
            var r = await _client.SendAsync<JToken>(ApiRequest.Mutation(HttpMethod.Post, AuthRoutes.Devices, body, requestId), CancellationToken.None)
                                 .ConfigureAwait(false);
            if (!r.Ok && (IsTransient(r.Error) || r.Status == 401)) throw r.ToServiceException();

            AuthTokens marked = null;
            lock (_lock)
            {
                _registerFor = _registerRequestId = null;
                if (!r.Ok)
                {
                    _refusedFor = scope;
                    return DeviceRegistration.Refused;
                }
                var now = Volatile.Read(ref _tokens);
                if (now != null && now.HasSession && now.PlayerId == player)
                {
                    marked = now with { RegisteredKeyId = _keyId };
                    Volatile.Write(ref _tokens, marked);
                }
            }
            if (marked != null) TrySave(marked);
            return DeviceRegistration.Registered;
        }

        // ------------------------------------------------------------------------------------- logout, delete (8.13)

        public async Task<bool> LogoutAsync(CancellationToken ct = default)
        {
            var t = Volatile.Read(ref _tokens);
            bool revoked = false;
            if (t != null && t.HasSession)
            {
                var req = ApiRequest.Mutation(HttpMethod.Post, AuthRoutes.Logout, new JObject { ["refreshToken"] = t.RefreshToken },
                                              ApiRequest.NewRequestId()) with { NoRetry = true };
                var r = await _client.SendAsync<JToken>(req, ct).ConfigureAwait(false);
                revoked = r.Ok;
            }
            Clear();
            return revoked;
        }

        public async Task<long> DeleteAccountAsync(CancellationToken ct = default)
        {
            var req = ApiRequest.Mutation(HttpMethod.Post, AuthRoutes.AccountDelete, new JObject(), ApiRequest.NewRequestId());
            var r = await _client.SendAsync<JToken>(req, ct).ConfigureAwait(false);
            var body = r.EnsureOk();
            if (!(body is JObject o) || o["graceUntil"]?.Type != JTokenType.Integer)
                throw new ServiceException(ErrorClass.ServerError, ErrorClassifier.SRV_UNEXPECTED, "account/delete: no graceUntil in the body", r.Status);
            Clear();
            return (long)o["graceUntil"];
        }

        // ------------------------------------------------------------------------------------- persistence

        /// <summary>Stores new tokens before anyone can use them. A failed write keeps them in memory for this process.</summary>
        void Store(AuthTokens t)
        {
            TrySave(t);
            lock (_lock) Volatile.Write(ref _tokens, t);
        }

        void TrySave(AuthTokens t)
        {
            try { _store.Save(t); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void Clear()
        {
            lock (_lock) Volatile.Write(ref _tokens, null);
            try { _store.Clear(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Clears only when the session is still the one that was refused (a concurrent sign-in may have replaced it).</summary>
        bool ClearIf(AuthTokens presented)
        {
            lock (_lock)
            {
                var now = Volatile.Read(ref _tokens);
                if (now != null && now.RefreshToken != presented.RefreshToken) return false;
                Volatile.Write(ref _tokens, null);
            }
            try { _store.Clear(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return true;
        }
    }
}
