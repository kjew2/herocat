using System.Globalization;
using System.Text.RegularExpressions;

namespace HeroCat.Server.Pipeline;

/// <summary>The request headers and response headers of specs/08 section 1.1 and 01 section 5.1, by name.</summary>
public static class ApiHeaders
{
    public const string RequestId = "X-Request-Id";
    public const string ServerTime = "X-Server-Time";
    public const string ClientVersion = "X-Client-Version";
    public const string ContentVersion = "X-Content-Version";
    public const string DeviceId = "X-Device-Id";
}

/// <summary>Per-request values the pipeline sets for handlers.</summary>
public static class RequestContext
{
    const string RequestIdItem = "HeroCat.RequestId";
    const string ErrorCodeItem = "HeroCat.ErrorCode";

    /// <summary>The request's id: the client's <c>X-Request-Id</c> when it sent a usable one, else one the server made.</summary>
    public static string RequestId(HttpContext ctx) =>
        ctx.Items.TryGetValue(RequestIdItem, out var v) && v is string s ? s : ctx.TraceIdentifier;

    /// <summary>The error body's <c>code</c> when this request answered with one (the log's <c>code</c>, specs/08 section 9.4).</summary>
    public static string? ErrorCode(HttpContext ctx) => ctx.Items.TryGetValue(ErrorCodeItem, out var v) ? v as string : null;

    internal static void SetErrorCode(HttpContext ctx, string code) => ctx.Items[ErrorCodeItem] = code;

    internal static void SetRequestId(HttpContext ctx, string id)
    {
        ctx.Items[RequestIdItem] = id;
        ctx.TraceIdentifier = id;
    }
}

/// <summary>
/// The outermost step (specs/08 section 1.1): echoes <c>X-Request-Id</c> and stamps <c>X-Server-Time</c> (unix seconds)
/// on every response, and turns every error into the error body: an <see cref="ApiException"/> into its code, any other
/// exception into <c>500 INTERNAL</c>, and an error status that left with no body (an unmatched route, a bare
/// <c>NotFound()</c>) into the code for that status.
/// </summary>
public sealed partial class RequestContextMiddleware
{
    readonly RequestDelegate _next;
    readonly TimeProvider _clock;
    readonly ILogger<RequestContextMiddleware> _log;

    public RequestContextMiddleware(RequestDelegate next, TimeProvider clock, ILogger<RequestContextMiddleware> log)
    {
        _next = next;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// An id the server echoes as-is: what the client sends (32 hex chars for mutating requests, a GUID for reads; 01
    /// section 5.1), or anything else short and header-safe. The 32-hex requirement on mutating routes is the idempotency
    /// store's check (#194), not this one.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex EchoableId();

    public async Task InvokeAsync(HttpContext ctx)
    {
        string? sent = ctx.Request.Headers[ApiHeaders.RequestId];
        string id = sent != null && EchoableId().IsMatch(sent) ? sent : Guid.NewGuid().ToString("N");
        RequestContext.SetRequestId(ctx, id);

        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[ApiHeaders.RequestId] = id;
            ctx.Response.Headers[ApiHeaders.ServerTime] = _clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });

        try
        {
            await _next(ctx).ConfigureAwait(false);
        }
        catch (ApiException e) when (!ctx.Response.HasStarted)
        {
            ctx.Response.Clear();
            await ApiErrorWriter.WriteAsync(ctx, e).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return; // the client went away; nobody reads a body
        }
        catch (Exception e) when (!ctx.Response.HasStarted)
        {
            _log.LogError(e, "unhandled exception on {Method} {Path} (request {RequestId})", ctx.Request.Method, ctx.Request.Path, id);
            ctx.Response.Clear();
            await ApiErrorWriter.WriteAsync(ctx, 500, ErrorCodes.INTERNAL).ConfigureAwait(false);
            return;
        }

        int status = ctx.Response.StatusCode;
        if (status >= 400 && !ctx.Response.HasStarted && ctx.Response.ContentLength is null or 0)
            await ApiErrorWriter.WriteAsync(ctx, status, ErrorCodes.ForBareStatus(status)).ConfigureAwait(false);
    }
}
