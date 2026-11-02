using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Api
{
    /// <summary>The request and response headers of 01 section 5.1 and 08 section 1.1, by name.</summary>
    public static class ApiHeaders
    {
        public const string Authorization = "Authorization";
        public const string RequestId = "X-Request-Id";
        public const string ClientVersion = "X-Client-Version";
        public const string ContentVersion = "X-Content-Version";
        public const string DeviceId = "X-Device-Id";
        public const string AcceptLanguage = "Accept-Language";
        public const string ServerTime = "X-Server-Time";
        public const string IdempotentReplay = "X-Idempotent-Replay";
        public const string RetryAfter = "Retry-After";
    }

    /// <summary>
    /// One call (01 section 5.1). <paramref name="Path"/> is relative to 08's <c>/v1</c> base URL. <paramref name="Body"/>
    /// is serialized as JSON (Newtonsoft), except a <c>byte[]</c>, which goes as <c>application/octet-stream</c> (the save
    /// blob). A mutating request (anything but GET/HEAD) must carry the caller's <paramref name="RequestId"/> (32 hex,
    /// 00 section 8.13): the flow persists it before sending, and every retry and replay reuses it. A read gets a fresh
    /// GUID per call (tracing only). <paramref name="Idempotent"/> lets offline and timeout failures retry (5.2).
    /// </summary>
    public sealed record ApiRequest(HttpMethod Method, string Path, object Body = null, bool Idempotent = false, string RequestId = null,
                                    bool RequiresAuth = true)
    {
        static readonly Regex Hex32 = new Regex("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);

        /// <summary>Per-attempt timeout; null: <see cref="ApiClientOptions.Timeout"/> (HTTP_TIMEOUT_S).</summary>
        public TimeSpan? Timeout { get; init; }

        /// <summary>
        /// Never retried or backed off (a 401 TOKEN_EXPIRED still refreshes and replays once): for calls whose caller has
        /// its own cadence, such as the clock's <c>GET /time</c> (02 section 11.1) or a boot fetch with a short budget.
        /// </summary>
        public bool NoRetry { get; init; }

        /// <summary>Extra request headers (e.g. <c>If-Match</c> and <c>X-Save-Meta</c> on <c>PUT /save</c>).</summary>
        public IReadOnlyDictionary<string, string> Headers { get; init; }

        public bool IsMutation => Method != HttpMethod.Get && Method != HttpMethod.Head;

        /// <summary>A read: idempotent, so offline and timeout failures retry with backoff.</summary>
        public static ApiRequest Get(string path, bool requiresAuth = true) =>
            new ApiRequest(HttpMethod.Get, path, null, Idempotent: true, RequestId: null, RequiresAuth: requiresAuth);

        /// <summary>A server-idempotent mutation (08 section 1.1): retried only with the same <paramref name="requestId"/>.</summary>
        public static ApiRequest Mutation(HttpMethod method, string path, object body, string requestId, bool requiresAuth = true) =>
            new ApiRequest(method, path, body, Idempotent: true, RequestId: requestId, RequiresAuth: requiresAuth);

        /// <summary>A fresh 32-hex request id for a new mutation.</summary>
        public static string NewRequestId() => Guid.NewGuid().ToString("N");

        internal void Validate()
        {
            if (Method == null) throw new ArgumentException("Method is required", nameof(Method));
            if (string.IsNullOrEmpty(Path) || Path[0] != '/') throw new ArgumentException("Path must start with '/': " + Path, nameof(Path));
            if (IsMutation && (RequestId == null || !Hex32.IsMatch(RequestId)))
                throw new ArgumentException($"{Method} {Path}: a mutation needs the caller's 32-hex requestId (00 section 8.13)", nameof(RequestId));
        }
    }

    /// <summary>
    /// A failed call (01 section 5.1). <see cref="Code"/> is the body's 08 wire code when the server sent one, else the
    /// client code of the classifier row (<c>NET_OFFLINE</c>, <c>NET_TIMEOUT</c>, <c>HTTP_502</c>, ...).
    /// <see cref="RetryAfterSec"/> is the body's <c>retryAfterSec</c>, else the <c>Retry-After</c> header (issue #30).
    /// </summary>
    public sealed record ApiError(string Code, string Message, int? RetryAfterSec)
    {
        /// <summary>The 01 section 5.2 row that classified it: class, retry policy and presentation.</summary>
        public ErrorRule Rule { get; init; }

        /// <summary>The body's <c>details</c> (appeal url, minClientVersion, maintenance until, ...); null when absent.</summary>
        public JToken Details { get; init; }

        public ErrorClass Class => Rule?.Class ?? ErrorClass.ServerError;
    }

    /// <summary>
    /// The result of <see cref="ApiClient.SendAsync{T}"/>. <see cref="Ok"/> is a 2xx whose body parsed; a replayed 200
    /// (<c>X-Idempotent-Replay</c>) is an ordinary Ok (5.2). <see cref="Status"/> is 0 when no response arrived.
    /// <see cref="ServerTimeSec"/> is the last response's <c>X-Server-Time</c>, 0 when none arrived.
    /// </summary>
    public sealed record ApiResponse<T>(bool Ok, int Status, T Body, ApiError Error, long ServerTimeSec)
    {
        /// <summary>The <c>X-Request-Id</c> every attempt carried.</summary>
        public string RequestId { get; init; }

        /// <summary>How many times the request went out (first send, retries and the post-refresh replay).</summary>
        public int Attempts { get; init; }

        /// <summary>The body on success; otherwise throws the matching <see cref="ServiceException"/>.</summary>
        public T EnsureOk() => Ok ? Body : throw ToServiceException();

        /// <summary>The <see cref="ServiceException"/> the service seams throw for this failure (01 section 7).</summary>
        public ServiceException ToServiceException()
        {
            if (Ok) throw new InvalidOperationException("an Ok response is not an error");
            return new ServiceException(Error.Class, Error.Code, Error.Message, Status == 0 ? (int?)null : Status);
        }
    }

    /// <summary>
    /// The access token and its refresh (01 section 5.2 row 401 TOKEN_EXPIRED; the AuthClient of issue #148 implements
    /// it over <c>POST /auth/refresh</c> with rotation). Called from the thread pool, so implementations are thread-safe.
    /// The ApiClient never runs two refreshes at once: a 401 that arrives while one is in flight joins it, and a 401 for a
    /// token that has since been replaced replays without refreshing (a second refresh of a rotated token would be
    /// <c>TOKEN_REUSED</c>).
    /// </summary>
    public interface IApiCredentials
    {
        /// <summary>The current access JWT, or null when signed out (the request then goes without Authorization).</summary>
        string AccessToken { get; }

        /// <summary>Refreshes the access token. True when a new one is stored; false sends the caller to re-login.</summary>
        Task<bool> RefreshAsync(CancellationToken ct);
    }

    /// <summary>Construction options of <see cref="ApiClient"/>; the defaults are production values.</summary>
    public sealed class ApiClientOptions
    {
        /// <summary>08 section 1.1: <c>https://api.{env}.whiskerhero.example/v1</c>. Route paths are appended to it.</summary>
        public string BaseUrl { get; set; } = "https://api.dev.whiskerhero.example/v1";

        /// <summary>The innermost handler. Null: a platform <see cref="HttpClientHandler"/>. Tests pass FakeApiHandler.</summary>
        public HttpMessageHandler Handler { get; set; }

        /// <summary><c>X-Client-Version</c>: semver plus build number, <c>1.4.2+318</c>.</summary>
        public string ClientVersion { get; set; } = "0.0.0";

        /// <summary><c>X-Content-Version</c>: the active ContentDb version, read per request (a hot update changes it).</summary>
        public Func<string> ContentVersion { get; set; } = () => "";

        /// <summary><c>X-Device-Id</c>: the install id (02 section 8.4).</summary>
        public string DeviceId { get; set; }

        /// <summary><c>Accept-Language</c>: the device locale, read per request.</summary>
        public Func<string> Locale { get; set; } = () => "en";

        /// <summary>Access token and refresh; null: no Authorization header and a 401 is final.</summary>
        public IApiCredentials Credentials { get; set; }

        /// <summary>Receives every response's X-Server-Time; the composition root sets the ServerClock here.</summary>
        public IServerTimeObserver ServerTime { get; set; }

        /// <summary>Measures the round trip handed to <see cref="IServerTimeObserver.Observe"/>. Default Stopwatch.</summary>
        public IMonoClock Mono { get; set; }

        /// <summary>Per-attempt timeout (HTTP_TIMEOUT_S).</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(RuntimeConfig.HTTP_TIMEOUT_S);

        /// <summary>Full-jitter source in [0, 1). Default a thread-safe Random; tests seed it.</summary>
        public Func<double> Jitter { get; set; }

        /// <summary>Waits between attempts. Default Task.Delay; tests record the delays and return at once.</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; }
    }
}
