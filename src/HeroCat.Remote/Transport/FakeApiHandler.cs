using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using Newtonsoft.Json;

namespace HeroCat.Remote.Transport
{
    /// <summary>What the fake server answers: a status, a JSON (or raw) body and extra headers.</summary>
    public sealed class FakeReply
    {
        public FakeReply(int status, string body = null, byte[] bytes = null)
        {
            Status = status;
            Body = body;
            Bytes = bytes;
        }

        public int Status { get; }

        /// <summary>JSON text (sent as application/json), or null.</summary>
        public string Body { get; }

        /// <summary>A raw octet-stream body instead of <see cref="Body"/>.</summary>
        public byte[] Bytes { get; }

        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public FakeReply With(string header, string value)
        {
            Headers[header] = value;
            return this;
        }

        public static FakeReply Json(int status, object body) =>
            new FakeReply(status, body == null ? null : body as string ?? JsonConvert.SerializeObject(body));

        /// <summary>08 section 1.1's error body: all five fields, plus <c>Retry-After</c> when <paramref name="retryAfterSec"/> is set.</summary>
        public static FakeReply Error(int status, string code, int? retryAfterSec = null, object details = null, string message = null)
        {
            var r = Json(status, new { code, message = message ?? code, requestId = "fake", retryAfterSec, details });
            if (retryAfterSec is int s) r.With(ApiHeaders.RetryAfter, s.ToString(CultureInfo.InvariantCulture));
            return r;
        }
    }

    /// <summary>One request as the fake server saw it.</summary>
    public sealed class FakeApiCall
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; }
        public string ContentType { get; set; }

        /// <summary>The managed thread the handler ran on (threading tests).</summary>
        public int ThreadId { get; set; }

        public string BodyText => Body == null ? null : Encoding.UTF8.GetString(Body);

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

        public override string ToString() => Method + " " + Path;
    }

    public enum FakeFaultKind
    {
        /// <summary>The connection drops before the request reaches the server (HttpRequestException).</summary>
        Drop,

        /// <summary>The request times out before the server handles it.</summary>
        TimeoutBeforeCommit,

        /// <summary>The server handles (commits) the request, then the client times out waiting for the reply.</summary>
        TimeoutAfterCommit,

        /// <summary>The request hangs until the client's per-attempt timeout (or cancellation) fires; real waiting.</summary>
        Hang,

        /// <summary>The server answers <see cref="FakeFault.Reply"/> without handling the request (5xx, 409 in-flight, 429).</summary>
        Reply,
    }

    /// <summary>A fault for the next request (consumed in order, one per request, whatever its route).</summary>
    public sealed class FakeFault
    {
        FakeFault(FakeFaultKind kind, FakeReply reply = null)
        {
            Kind = kind;
            Reply = reply;
        }

        public FakeFaultKind Kind { get; }
        public FakeReply Reply { get; }

        public static FakeFault Drop => new FakeFault(FakeFaultKind.Drop);
        public static FakeFault TimeoutBeforeCommit => new FakeFault(FakeFaultKind.TimeoutBeforeCommit);
        public static FakeFault TimeoutAfterCommit => new FakeFault(FakeFaultKind.TimeoutAfterCommit);
        public static FakeFault Hang => new FakeFault(FakeFaultKind.Hang);
        public static FakeFault With(FakeReply reply) => new FakeFault(FakeFaultKind.Reply, reply ?? throw new ArgumentNullException(nameof(reply)));
        public static FakeFault Status(int status, string code, int? retryAfterSec = null) => With(FakeReply.Error(status, code, retryAfterSec));

        /// <summary>08 section 1.1: the request id is still being handled: <c>409 IDEMPOTENCY_IN_FLIGHT</c>, retry after 1 s.</summary>
        public static FakeFault InFlight => Status(409, "IDEMPOTENCY_IN_FLIGHT", 1);
    }

    /// <summary>
    /// A fault-injecting fake of 08's API as an <see cref="HttpMessageHandler"/> under the real <see cref="ApiClient"/>
    /// (issue #143; it replaces the old synchronous FakeTransport). Routes are keyed by "METHOD /path" (the path after the
    /// <c>/v1</c> base). Like the real pipeline (issue #193) every response carries <c>X-Server-Time</c> and the echoed
    /// <c>X-Request-Id</c>, errors carry the 08 error body, and an unrouted call is <c>404 NOT_FOUND</c>. Like 08's
    /// idempotency store, a mutation whose <c>X-Request-Id</c> was already handled is not handled again: its stored reply
    /// is replayed with <c>X-Idempotent-Replay: true</c>. Scripted <see cref="Faults"/> model timeouts after commit,
    /// dropped connections and 5xx / 409 / 429 answers. Thread-safe.
    /// </summary>
    public sealed class FakeApiHandler : HttpMessageHandler
    {
        readonly object _lock = new object();
        readonly Dictionary<string, Func<FakeApiCall, FakeReply>> _routes =
            new Dictionary<string, Func<FakeApiCall, FakeReply>>(StringComparer.Ordinal);
        readonly Dictionary<string, FakeReply> _idempotency = new Dictionary<string, FakeReply>(StringComparer.Ordinal);
        readonly Queue<FakeFault> _faults = new Queue<FakeFault>();
        readonly List<FakeApiCall> _calls = new List<FakeApiCall>();

        /// <summary>The server clock stamped into X-Server-Time; null: no header (an old proxy that strips it).</summary>
        public Func<long> ServerTime { get; set; } = () => 1_800_000_000;

        /// <summary>Path prefix stripped before routing (the /v1 of 08's base URL).</summary>
        public string BasePath { get; set; } = "/v1";

        /// <summary>Every request that reached the handler, in order (faulted ones included).</summary>
        public IReadOnlyList<FakeApiCall> Calls
        {
            get { lock (_lock) return _calls.ToList(); }
        }

        /// <summary>Requests the server actually handled (route handler ran), replays excluded.</summary>
        public int Handled { get; private set; }

        /// <summary>Replies served from the idempotency store.</summary>
        public int Replays { get; private set; }

        public FakeApiHandler On(string method, string path, Func<FakeApiCall, FakeReply> handler)
        {
            lock (_lock) _routes[Key(method, path)] = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        public FakeApiHandler On(string method, string path, int status, object body = null) =>
            On(method, path, _ => FakeReply.Json(status, body));

        /// <summary>Queues faults for the next requests, one each, in order.</summary>
        public FakeApiHandler Then(params FakeFault[] faults)
        {
            lock (_lock) foreach (var f in faults) _faults.Enqueue(f);
            return this;
        }

        public int PendingFaults
        {
            get { lock (_lock) return _faults.Count; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = new FakeApiCall
            {
                Method = request.Method.Method.ToUpperInvariant(),
                Path = StripBase(request.RequestUri.AbsolutePath),
                ThreadId = Environment.CurrentManagedThreadId,
            };
            foreach (var h in request.Headers) call.Headers[h.Key] = string.Join(",", h.Value);
            if (request.Content != null)
            {
                call.Body = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                call.ContentType = request.Content.Headers.ContentType?.ToString();
            }

            FakeFault fault;
            lock (_lock)
            {
                _calls.Add(call);
                fault = _faults.Count > 0 ? _faults.Dequeue() : null;
            }

            switch (fault?.Kind)
            {
                case FakeFaultKind.Drop:
                    throw new HttpRequestException("fake: connection dropped");
                case FakeFaultKind.TimeoutBeforeCommit:
                    throw new TaskCanceledException("fake: timed out before the server handled it");
                case FakeFaultKind.TimeoutAfterCommit:
                    Handle(call);
                    throw new TaskCanceledException("fake: timed out after the server committed");
                case FakeFaultKind.Hang:
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    throw new TaskCanceledException();
                case FakeFaultKind.Reply:
                    return ToResponse(fault.Reply, call, replay: false);
            }
            var (reply, replay) = Handle(call);
            return ToResponse(reply, call, replay);
        }

        (FakeReply reply, bool replay) Handle(FakeApiCall call)
        {
            string id = call.Header(ApiHeaders.RequestId);
            bool mutation = call.Method != "GET" && call.Method != "HEAD";
            Func<FakeApiCall, FakeReply> route;
            lock (_lock)
            {
                if (mutation && id != null && _idempotency.TryGetValue(id, out var stored))
                {
                    Replays++;
                    return (stored, true);
                }
                _routes.TryGetValue(Key(call.Method, call.Path), out route);
            }
            var reply = route != null ? route(call) : FakeReply.Error(404, "NOT_FOUND");
            lock (_lock)
            {
                if (route != null) Handled++;
                // 08: only a final status is stored; a 5xx is not (the idempotency row is released for the retry).
                if (mutation && id != null && reply.Status < 500) _idempotency[id] = reply;
            }
            return (reply, false);
        }

        HttpResponseMessage ToResponse(FakeReply reply, FakeApiCall call, bool replay)
        {
            var response = new HttpResponseMessage((HttpStatusCode)reply.Status);
            if (reply.Bytes != null)
            {
                response.Content = new ByteArrayContent(reply.Bytes);
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            }
            else if (reply.Body != null)
            {
                response.Content = new StringContent(reply.Body, Encoding.UTF8, "application/json");
            }
            else
            {
                response.Content = new ByteArrayContent(Array.Empty<byte>());
            }
            string id = call.Header(ApiHeaders.RequestId);
            if (id != null) response.Headers.TryAddWithoutValidation(ApiHeaders.RequestId, id);
            var now = ServerTime;
            if (now != null) response.Headers.TryAddWithoutValidation(ApiHeaders.ServerTime, now().ToString(CultureInfo.InvariantCulture));
            if (replay) response.Headers.TryAddWithoutValidation(ApiHeaders.IdempotentReplay, "true");
            foreach (var h in reply.Headers) response.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return response;
        }

        string StripBase(string path) =>
            !string.IsNullOrEmpty(BasePath) && path.StartsWith(BasePath + "/", StringComparison.Ordinal) ? path.Substring(BasePath.Length) : path;

        static string Key(string method, string path) => method.ToUpperInvariant() + " " + path;
    }

    /// <summary>
    /// The handler used while no server exists for a build: every call fails as a dropped connection, so the ApiClient
    /// reports <c>NET_OFFLINE</c> and the game runs offline (local saves, queued upload, device clock floored at the save).
    /// </summary>
    public sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No backend configured (" + request.Method + " " + request.RequestUri?.AbsolutePath + ")"));
    }
}
