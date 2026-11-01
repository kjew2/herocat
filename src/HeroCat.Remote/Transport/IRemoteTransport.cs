using System;

namespace HeroCat.Remote.Transport
{
    /// <summary>One call to the backend. Vendor-neutral: an adapter maps (Method, Path) onto an HTTP route, a UGS
    /// Cloud Code endpoint or a Nakama RPC id. Body is JSON text or null.</summary>
    public sealed class RemoteRequest
    {
        public RemoteRequest(string method, string path, string body = null)
        {
            Method = method ?? throw new ArgumentNullException(nameof(method));
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Body = body;
        }

        public string Method { get; }
        public string Path { get; }
        public string Body { get; }

        public override string ToString() => Method + " " + Path;
    }

    /// <summary>HTTP-shaped reply. Adapters for non-HTTP SDKs translate their status into these codes.</summary>
    public sealed class RemoteResponse
    {
        public RemoteResponse(int statusCode, string body = null) { StatusCode = statusCode; Body = body; }

        public int StatusCode { get; }
        public string Body { get; }

        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
    }

    /// <summary>
    /// The only thing a backend vendor (UGS or Nakama, decided by the specs/08 section 10 spike) has to implement.
    /// Synchronous, like the seams it serves (IGachaService, ISaveStore). Implementations either return a response
    /// (any status) or throw: <see cref="TransportException"/> for no connectivity or timeout, or whatever their SDK
    /// throws, which <see cref="RemoteErrors.FromException"/> maps. Callers never see either: RemoteClient turns
    /// everything into ServiceException (specs/08 section 9).
    /// </summary>
    public interface IRemoteTransport
    {
        RemoteResponse Send(RemoteRequest request, TimeSpan timeout);
    }

    public enum TransportFailure { Unreachable, Timeout }

    /// <summary>Thrown by transports when no response arrived at all.</summary>
    public sealed class TransportException : Exception
    {
        public TransportException(TransportFailure failure, string message = null, Exception inner = null)
            : base(message ?? failure.ToString(), inner)
        {
            Failure = failure;
        }

        public TransportFailure Failure { get; }
    }

    /// <summary>The transport used until a vendor is chosen: every call fails as if the device were offline.</summary>
    public sealed class OfflineTransport : IRemoteTransport
    {
        public static readonly OfflineTransport Instance = new OfflineTransport();

        public RemoteResponse Send(RemoteRequest request, TimeSpan timeout) =>
            throw new TransportException(TransportFailure.Unreachable, "No backend configured (" + request + ")");
    }
}
