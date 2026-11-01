using System;
using System.Collections.Generic;

namespace HeroCat.Remote.Transport
{
    /// <summary>
    /// In-memory transport for tests: canned routes keyed by "METHOD path", a log of every request, and a switch
    /// that makes calls fail as offline or timed out. Unrouted calls answer 404 {"code":"NoRoute"}.
    /// </summary>
    public sealed class FakeTransport : IRemoteTransport
    {
        readonly Dictionary<string, Func<RemoteRequest, RemoteResponse>> _routes =
            new Dictionary<string, Func<RemoteRequest, RemoteResponse>>(StringComparer.Ordinal);

        public List<RemoteRequest> Requests { get; } = new List<RemoteRequest>();

        /// <summary>When set, every call throws <see cref="TransportException"/> with this failure.</summary>
        public TransportFailure? FailWith { get; set; }

        public FakeTransport On(string method, string path, Func<RemoteRequest, RemoteResponse> handler)
        {
            _routes[Key(method, path)] = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        public FakeTransport On(string method, string path, int statusCode, string body = null) =>
            On(method, path, _ => new RemoteResponse(statusCode, body));

        public RemoteResponse Send(RemoteRequest request, TimeSpan timeout)
        {
            Requests.Add(request);
            if (FailWith.HasValue) throw new TransportException(FailWith.Value, "fake " + FailWith.Value);
            return _routes.TryGetValue(Key(request.Method, request.Path), out var handler)
                ? handler(request)
                : new RemoteResponse(404, "{\"code\":\"NoRoute\"}");
        }

        static string Key(string method, string path) => method.ToUpperInvariant() + " " + path;
    }
}
