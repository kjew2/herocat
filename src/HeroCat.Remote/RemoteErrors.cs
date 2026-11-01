using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Services;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote
{
    /// <summary>
    /// specs/08 section 9 exception mapping. No connectivity or DNS failure -> Offline; timeout -> Timeout;
    /// 409 -> Conflict; other 4xx -> Rejected with the body's code; 5xx (and anything else unexpected) -> Internal.
    /// The sim and the client only ever see <see cref="ServiceException"/>.
    /// </summary>
    public static class RemoteErrors
    {
        public static ServiceException FromStatus(int statusCode, string body)
        {
            string code = CodeFromBody(body) ?? "Http" + statusCode;
            if (statusCode == 408) return new ServiceException(ServiceErrorKind.Timeout, code);
            if (statusCode == 409) return new ServiceException(ServiceErrorKind.Conflict, code);
            if (statusCode >= 400 && statusCode < 500) return new ServiceException(ServiceErrorKind.Rejected, code);
            return new ServiceException(ServiceErrorKind.Internal, code);
        }

        public static ServiceException FromException(Exception e)
        {
            switch (e)
            {
                case ServiceException se: return se;
                case TransportException te:
                    return te.Failure == TransportFailure.Timeout
                        ? new ServiceException(ServiceErrorKind.Timeout, "Timeout", te.Message)
                        : new ServiceException(ServiceErrorKind.Offline, "Offline", te.Message);
                case TimeoutException _:
                case OperationCanceledException _:   // HttpClient reports its timeout as TaskCanceledException
                    return new ServiceException(ServiceErrorKind.Timeout, "Timeout", e.Message);
                case HttpRequestException _:
                case SocketException _:
                case IOException _:
                    return new ServiceException(ServiceErrorKind.Offline, "Offline", e.Message);
                default:
                    return new ServiceException(ServiceErrorKind.Internal, "TransportError", e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Reads {"code": "..."} from an error body; null when absent or not JSON.</summary>
        static string CodeFromBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                return JToken.Parse(body) is JObject o && o["code"] is JValue v && v.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string)v
                    : null;
            }
            catch (Newtonsoft.Json.JsonException) { return null; }
        }
    }

    public sealed class RemoteOptions
    {
        /// <summary>Per-request timeout handed to the transport (specs/08 section 9: 5 s cloud fetch on load).</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>Wraps an <see cref="IRemoteTransport"/>: returns the body of a 2xx reply, throws ServiceException otherwise.</summary>
    public sealed class RemoteClient
    {
        readonly IRemoteTransport _transport;

        public RemoteClient(IRemoteTransport transport, RemoteOptions options = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Options = options ?? new RemoteOptions();
        }

        public RemoteOptions Options { get; }

        public string Send(string method, string path, string body = null)
        {
            RemoteResponse response;
            try { response = _transport.Send(new RemoteRequest(method, path, body), Options.RequestTimeout); }
            catch (Exception e) { throw RemoteErrors.FromException(e); }

            if (response == null) throw new ServiceException(ServiceErrorKind.Internal, "NoResponse");
            if (response.IsSuccess) return response.Body;
            throw RemoteErrors.FromStatus(response.StatusCode, response.Body);
        }
    }
}
