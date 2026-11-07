using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Http.Features;

namespace HeroCat.Server.Observability;

/// <summary>The request fields of a log line (specs/08 section 9.4) as a request computes them.</summary>
public static class LogFields
{
    /// <summary>
    /// The log's <c>playerIdHash</c>: the first 16 hex digits of SHA-256 of the player id (<c>D</c> format). Not the
    /// analytics <c>playerIdHash</c> (salted, 02 section 14.1): logs and events are not meant to join.
    /// </summary>
    public static string PlayerIdHash(Guid playerId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(playerId.ToString("D"))))[..16].ToLowerInvariant();

    /// <summary>The route label of logs and metrics: the endpoint's method and template (<c>put/save</c>), or <c>unmatched</c>.</summary>
    public static string Route(HttpContext ctx) =>
        ctx.GetEndpoint() is RouteEndpoint e && e.RoutePattern.RawText is { } t ? RateLimitRoutes.Of(ctx.Request.Method, t) : Unmatched;

    /// <summary>The label of a request no endpoint matched, so a scan of random paths cannot grow the label set.</summary>
    public const string Unmatched = "unmatched";

    /// <summary>The device of the verified token, else a well-formed <c>X-Device-Id</c>.</summary>
    public static string? DeviceId(HttpContext ctx) =>
        ctx.DeviceId() ?? (Guid.TryParse(ctx.Request.Headers[ApiHeaders.DeviceId].ToString(), out var d) ? d.ToString("D") : null);
}

/// <summary>
/// The outermost observability step (specs/08 section 9.4): opens the request's log scope (<c>requestId</c>,
/// <c>route</c>, <c>playerIdHash</c>, <c>deviceId</c>, <c>traceId</c>, read when a line is written, so lines logged
/// after authentication carry the player), records <c>http_requests_total</c> and <c>http_request_duration_seconds</c>,
/// writes one access line per request (<c>status</c>, <c>latencyMs</c>, <c>code</c>), and keeps the trace of a request
/// that ended 5xx although head sampling dropped it (<see cref="TraceSampling"/>).
/// </summary>
public sealed class RequestTelemetryMiddleware
{
    readonly RequestDelegate _next;
    readonly ILogger<RequestTelemetryMiddleware> _log;

    public RequestTelemetryMiddleware(RequestDelegate next, ILogger<RequestTelemetryMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        long start = Stopwatch.GetTimestamp();
        using var scope = _log.BeginScope(new RequestScope(ctx));
        bool threw = false;
        try
        {
            await _next(ctx).ConfigureAwait(false);
        }
        catch
        {
            threw = true;
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(start);
            int status = threw && !ctx.Response.HasStarted ? 500 : ctx.Response.StatusCode;
            string route = LogFields.Route(ctx);
            HttpMetrics.Record(route, status, elapsed.TotalSeconds);
            if (status >= 500) TraceSampling.KeepOnError(ctx.Features.Get<IHttpActivityFeature>()?.Activity ?? Activity.Current);
            var level = status >= 500 ? LogLevel.Error : LogLevel.Information;
            // The access line; its code (an error's) comes from the scope.
            _log.Log(level, "{Route} {Status} in {LatencyMs} ms", route, status, Math.Round(elapsed.TotalMilliseconds, 1));
        }
    }

    /// <summary>The request fields, evaluated on every read.</summary>
    sealed class RequestScope : IReadOnlyList<KeyValuePair<string, object?>>
    {
        readonly HttpContext _ctx;

        public RequestScope(HttpContext ctx) => _ctx = ctx;

        IEnumerable<KeyValuePair<string, object?>> Items()
        {
            yield return new("requestId", RequestContext.RequestId(_ctx));
            yield return new("route", LogFields.Route(_ctx));
            if (_ctx.PlayerId() is Guid p) yield return new("playerIdHash", LogFields.PlayerIdHash(p));
            if (LogFields.DeviceId(_ctx) is { } d) yield return new("deviceId", d);
            if ((_ctx.Features.Get<IHttpActivityFeature>()?.Activity ?? Activity.Current) is { } a) yield return new("traceId", a.TraceId.ToHexString());
            if (RequestContext.ErrorCode(_ctx) is { } code) yield return new("code", code);
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => Items().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => Items().Count();
        public KeyValuePair<string, object?> this[int index] => Items().ElementAt(index);
        public override string ToString() => "request " + RequestContext.RequestId(_ctx);
    }
}
