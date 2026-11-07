using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Session;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Api
{
    /// <summary>
    /// The client network layer of 01 section 5 (issue #143) over 08's REST API. One instance (one HttpClient) per
    /// process, built by the Remote composition root.
    /// <list type="bullet">
    /// <item>Every request carries the 5.1 header set; every response's <c>X-Server-Time</c>, success or error, goes to
    /// <see cref="ApiClientOptions.ServerTime"/> with the measured round trip.</item>
    /// <item>Responses are classified by (status, code) through <see cref="ErrorClassifier"/> (5.2, issue #30), which picks
    /// the retry: offline and timeout retry only when <see cref="ApiRequest.Idempotent"/>; <c>Backoff</c> rows honour
    /// <c>retryAfterSec</c> / <c>Retry-After</c>, else full-jitter backoff; 500 retries once; 401 <c>TOKEN_EXPIRED</c>
    /// refreshes once and replays once. Retries share one budget of HTTP_RETRY_MAX after the first send.</item>
    /// <item>A mutation is only ever re-sent with the same <c>X-Request-Id</c>; the server replays its stored result
    /// (<c>X-Idempotent-Replay: true</c>), which is treated exactly like a first 200.</item>
    /// <item>All of this runs on the thread pool (<c>ConfigureAwait(false)</c>); the returned task completes there, and the
    /// caller's own <c>await</c> resumes on its context, the Unity main thread (section 4). The client holds no reference
    /// to GameState or Simulation, so nothing it runs can touch them.</item>
    /// </list>
    /// <see cref="SendAsync{T}"/> never throws for a failed call (it returns <see cref="ApiResponse{T}.Ok"/> false with an
    /// <see cref="ApiError"/>); only the caller's cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public sealed class ApiClient : IDisposable
    {
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        readonly HttpClient _http;
        readonly ApiClientOptions _o;
        readonly string _baseUrl;
        readonly IMonoClock _mono;
        readonly Func<double> _jitter;
        readonly Func<TimeSpan, CancellationToken, Task> _delay;
        readonly object _refreshLock = new object();
        Task<bool> _refreshing;

        public ApiClient(ApiClientOptions options)
        {
            _o = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.BaseUrl)) throw new ArgumentException("BaseUrl is required", nameof(options));
            _baseUrl = options.BaseUrl.TrimEnd('/');
            _mono = options.Mono ?? StopwatchMonoClock.Instance;
            _jitter = options.Jitter ?? SharedJitter;
            _delay = options.Delay ?? Task.Delay;
            // Timeouts are per attempt (a linked CancellationTokenSource), so the HttpClient's own timeout is off.
            _http = new HttpClient(options.Handler ?? new HttpClientHandler(), disposeHandler: options.Handler == null)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
        }

        /// <summary>
        /// Raised after every attempt with whether the server answered at all (any status: true; no connection: false;
        /// a timeout is not reported). <see cref="NetworkMonitor"/> reads it. Raised on the thread pool.
        /// </summary>
        public event Action<bool> ServerReached;

        public ApiClientOptions Options => _o;

        // ------------------------------------------------------------------------------------- backoff (5.2)

        /// <summary>
        /// The full-jitter delay before retry <paramref name="retry"/> (0-based): <c>jitter * min(HTTP_RETRY_CAP_MS,
        /// HTTP_RETRY_BASE_MS * 2^retry)</c> ms with jitter in [0, 1). Retries 0..3 bound the total wait at 1+2+4+8 = 15 s.
        /// </summary>
        public static TimeSpan BackoffDelay(int retry, double jitter)
        {
            if (retry < 0) throw new ArgumentOutOfRangeException(nameof(retry));
            double ceiling = Math.Min(RuntimeConfig.HTTP_RETRY_CAP_MS, RuntimeConfig.HTTP_RETRY_BASE_MS * Math.Pow(2, Math.Min(retry, 30)));
            double j = jitter < 0 ? 0 : jitter >= 1 ? 1 : jitter;
            return TimeSpan.FromMilliseconds(Math.Floor(j * ceiling));
        }

        static readonly Random Rng = new Random();

        static double SharedJitter()
        {
            lock (Rng) return Rng.NextDouble();
        }

        // ------------------------------------------------------------------------------------- send

        /// <summary>
        /// Sends <paramref name="req"/> with the 5.2 retry policy. The whole exchange (serialization, IO, parsing, backoff)
        /// starts on the thread pool, so nothing of it runs on the caller's (main) thread. See the class summary.
        /// </summary>
        public Task<ApiResponse<T>> SendAsync<T>(ApiRequest req, CancellationToken ct = default)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            return Task.Run(() => SendCoreAsync<T>(req, ct));
        }

        async Task<ApiResponse<T>> SendCoreAsync<T>(ApiRequest req, CancellationToken ct)
        {
            req.Validate();
            string requestId = req.IsMutation ? req.RequestId : req.RequestId ?? Guid.NewGuid().ToString("N");
            byte[] body = SerializeBody(req.Body, out string contentType);

            int attempts = 0, retries = 0;
            bool refreshed = false, retriedServerError = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                attempts++;
                string token = req.RequiresAuth ? _o.Credentials?.AccessToken : null;
                var raw = await AttemptAsync(req, requestId, body, contentType, token, ct).ConfigureAwait(false);
                var result = Interpret<T>(raw, requestId, attempts);
                if (result.Ok) return result;

                var rule = result.Error.Rule;
                TimeSpan? wait = null;
                switch (rule.Retry)
                {
                    case RetryPolicy.RefreshOnce:
                        if (req.RequiresAuth && _o.Credentials != null && !refreshed)
                        {
                            refreshed = true;
                            if (await RefreshAsync(token, ct).ConfigureAwait(false)) continue;   // the replay, once
                        }
                        return result;
                    case RetryPolicy.IfIdempotent:
                        if (req.Idempotent) wait = BackoffDelay(retries, _jitter());
                        break;
                    case RetryPolicy.Backoff:
                        wait = result.Error.RetryAfterSec is int s && s >= 0 ? TimeSpan.FromSeconds(s) : BackoffDelay(retries, _jitter());
                        break;
                    case RetryPolicy.Once:
                        if (!retriedServerError)
                        {
                            retriedServerError = true;
                            wait = BackoffDelay(retries, _jitter());
                        }
                        break;
                }
                if (wait == null || req.NoRetry || retries >= RuntimeConfig.HTTP_RETRY_MAX) return result;
                retries++;
                await _delay(wait.Value, ct).ConfigureAwait(false);
            }
        }

        /// <summary>One refresh at a time; a 401 for a token another refresh already replaced just replays.</summary>
        async Task<bool> RefreshAsync(string tokenUsed, CancellationToken ct)
        {
            Task<bool> task;
            lock (_refreshLock)
            {
                string current = _o.Credentials.AccessToken;
                if (current != null && current != tokenUsed) return true;
                if (_refreshing == null || _refreshing.IsCompleted) _refreshing = SafeRefresh();
                task = _refreshing;
            }
            return await task.ConfigureAwait(false);

            async Task<bool> SafeRefresh()
            {
                try { return await _o.Credentials.RefreshAsync(ct).ConfigureAwait(false); }
                catch (Exception) { return false; }
            }
        }

        // ------------------------------------------------------------------------------------- one attempt

        sealed class RawResult
        {
            public int Status;              // 0: no response
            public bool TimedOut;
            public string Failure;          // transport failure text
            public byte[] Body;
            public long ServerTimeSec;
            public int? RetryAfterHeaderSec;
        }

        async Task<RawResult> AttemptAsync(ApiRequest req, string requestId, byte[] body, string contentType, string token,
                                           CancellationToken ct)
        {
            bool api = !req.IsAbsolute;   // a CDN GET goes out bare (ApiRequest.Cdn)
            using var msg = new HttpRequestMessage(req.Method, api ? _baseUrl + req.Path : req.Path);
            if (api) AddApiHeaders(msg, req, requestId, token);
            if (body != null)
            {
                msg.Content = new ByteArrayContent(body);
                msg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(req.Timeout ?? _o.Timeout);
            long t0 = _mono.Ticks;
            var raw = new RawResult();
            try
            {
                using var response = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                long rttMs = (long)((_mono.Ticks - t0) * 1000.0 / _mono.Frequency);
                raw.Status = (int)response.StatusCode;
                raw.ServerTimeSec = api ? ReadServerTime(response) : 0;
                if (raw.ServerTimeSec > 0) _o.ServerTime?.Observe(raw.ServerTimeSec, rttMs);
                raw.RetryAfterHeaderSec = ReadRetryAfter(response);
                raw.Body = response.Content == null ? null : await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (api) ServerReached?.Invoke(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // the caller gave up: no result, no retry
            }
            catch (OperationCanceledException e)
            {
                raw = new RawResult { TimedOut = true, Failure = e.Message, ServerTimeSec = raw.ServerTimeSec, Status = 0 };
            }
            catch (Exception e) when (IsConnectionFailure(e))
            {
                raw = new RawResult { Failure = e.GetType().Name + ": " + e.Message, ServerTimeSec = raw.ServerTimeSec, Status = 0 };
                if (api) ServerReached?.Invoke(false);
            }
            return raw;
        }

        void AddApiHeaders(HttpRequestMessage msg, ApiRequest req, string requestId, string token)
        {
            if (token != null) msg.Headers.TryAddWithoutValidation(ApiHeaders.Authorization, "Bearer " + token);
            msg.Headers.TryAddWithoutValidation(ApiHeaders.RequestId, requestId);
            msg.Headers.TryAddWithoutValidation(ApiHeaders.ClientVersion, _o.ClientVersion ?? "");
            string content = _o.ContentVersion?.Invoke();
            if (!string.IsNullOrEmpty(content)) msg.Headers.TryAddWithoutValidation(ApiHeaders.ContentVersion, content);
            if (!string.IsNullOrEmpty(_o.DeviceId)) msg.Headers.TryAddWithoutValidation(ApiHeaders.DeviceId, _o.DeviceId);
            string locale = _o.Locale?.Invoke();
            if (!string.IsNullOrEmpty(locale)) msg.Headers.TryAddWithoutValidation(ApiHeaders.AcceptLanguage, locale);
            if (req.Headers != null)
                foreach (var h in req.Headers) msg.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        static bool IsConnectionFailure(Exception e) =>
            e is HttpRequestException || e is IOException || e is SocketException || e is TimeoutException;

        static long ReadServerTime(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues(ApiHeaders.ServerTime, out var values)) return 0;
            string v = values.FirstOrDefault();
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) && t > 0 ? t : 0;
        }

        static int? ReadRetryAfter(HttpResponseMessage response)
        {
            var ra = response.Headers.RetryAfter;
            if (ra?.Delta is TimeSpan d) return (int)Math.Ceiling(Math.Max(0, d.TotalSeconds));
            return null;   // an HTTP-date Retry-After falls back to the backoff schedule
        }

        // ------------------------------------------------------------------------------------- interpretation

        ApiResponse<T> Interpret<T>(RawResult raw, string requestId, int attempts)
        {
            if (raw.Status == 0)
            {
                var rule = ErrorClassifier.ClassifyTransport(raw.TimedOut);
                return Fail<T>(raw, new ApiError(rule.Code, raw.Failure, null) { Rule = rule }, requestId, attempts);
            }

            if (raw.Status >= 200 && raw.Status < 300)
            {
                try
                {
                    return new ApiResponse<T>(true, raw.Status, Deserialize<T>(raw.Body), null, raw.ServerTimeSec)
                    {
                        RequestId = requestId,
                        Attempts = attempts,
                    };
                }
                catch (Exception e) when (e is JsonException || e is FormatException || e is InvalidCastException || e is ArgumentException)
                {
                    var bad = ErrorClassifier.Find(ErrorClassifier.SRV_UNEXPECTED);
                    return Fail<T>(raw, new ApiError(bad.Code, "unreadable response body: " + e.Message, null) { Rule = bad }, requestId, attempts);
                }
            }

            ParseErrorBody(raw.Body, out string code, out string message, out int? retryAfterBody, out JToken details);
            var r = ErrorClassifier.Classify(raw.Status, code);
            var error = new ApiError(code ?? r.Code, message ?? ("HTTP " + raw.Status.ToString(CultureInfo.InvariantCulture)),
                                     retryAfterBody ?? raw.RetryAfterHeaderSec)
            {
                Rule = r,
                Details = details,
            };
            return Fail<T>(raw, error, requestId, attempts);
        }

        static ApiResponse<T> Fail<T>(RawResult raw, ApiError error, string requestId, int attempts) =>
            new ApiResponse<T>(false, raw.Status, default, error, raw.ServerTimeSec) { RequestId = requestId, Attempts = attempts };

        /// <summary>08 section 1.1's error body; every field optional here (a gateway's HTML page has none).</summary>
        static void ParseErrorBody(byte[] body, out string code, out string message, out int? retryAfterSec, out JToken details)
        {
            code = null;
            message = null;
            retryAfterSec = null;
            details = null;
            if (body == null || body.Length == 0) return;
            JObject o;
            try { o = JToken.Parse(Utf8.GetString(body)) as JObject; }
            catch (JsonException) { return; }
            if (o == null) return;
            if (o["code"] is JValue c && c.Type == JTokenType.String) code = (string)c;
            if (o["message"] is JValue m && m.Type == JTokenType.String) message = (string)m;
            if (o["retryAfterSec"] is JValue ra && (ra.Type == JTokenType.Integer || ra.Type == JTokenType.Float))
                retryAfterSec = (int)Math.Ceiling(Math.Max(0, ra.Value<double>()));
            var d = o["details"];
            if (d != null && d.Type != JTokenType.Null) details = d;
        }

        static T Deserialize<T>(byte[] body)
        {
            if (typeof(T) == typeof(byte[])) return (T)(object)(body ?? Array.Empty<byte>());
            string text = body == null || body.Length == 0 ? null : Utf8.GetString(body);
            if (typeof(T) == typeof(string)) return (T)(object)text;
            if (text == null) return default;
            if (typeof(JToken).IsAssignableFrom(typeof(T)))
            {
                var token = JToken.Parse(text);
                return token is T t ? t : throw new FormatException("expected " + typeof(T).Name + ", got " + token.Type);
            }
            return JsonConvert.DeserializeObject<T>(text);
        }

        static byte[] SerializeBody(object body, out string contentType)
        {
            switch (body)
            {
                case null:
                    contentType = null;
                    return null;
                case byte[] bytes:
                    contentType = "application/octet-stream";
                    return bytes;
                case JToken token:
                    contentType = "application/json; charset=utf-8";
                    return Utf8.GetBytes(token.ToString(Formatting.None));
                default:
                    contentType = "application/json; charset=utf-8";
                    return Utf8.GetBytes(JsonConvert.SerializeObject(body));
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
