using System.Text.Json;

namespace HeroCat.Server.Pipeline;

/// <summary>
/// The error body of every 4xx/5xx (specs/08 section 1.1):
/// <c>{ "code", "message", "requestId", "retryAfterSec", "details" }</c>. <c>code</c> is UPPER_SNAKE and is what the client
/// classifies on, with the status (01 section 5.2); <c>message</c> is English diagnostics only (the client localizes by code).
/// </summary>
public sealed record ErrorBody(string Code, string Message, string RequestId, int? RetryAfterSec, object? Details);

/// <summary>The global code set of specs/08 section 1.1 with their statuses. Endpoint-specific codes live with their features.</summary>
public static class ErrorCodes
{
    public const string UNAUTHENTICATED = "UNAUTHENTICATED";
    public const string TOKEN_EXPIRED = "TOKEN_EXPIRED";
    public const string FORBIDDEN = "FORBIDDEN";
    public const string ACCOUNT_BANNED = "ACCOUNT_BANNED";
    public const string NOT_FOUND = "NOT_FOUND";
    public const string CLIENT_TOO_OLD = "CLIENT_TOO_OLD";
    public const string CONTENT_TOO_OLD = "CONTENT_TOO_OLD";
    public const string RATE_LIMITED = "RATE_LIMITED";
    public const string MAINTENANCE = "MAINTENANCE";
    public const string VALIDATION = "VALIDATION";
    public const string IDEMPOTENCY_IN_FLIGHT = "IDEMPOTENCY_IN_FLIGHT";
    public const string INTERNAL = "INTERNAL";

    /// <summary>Each global code's status (specs/08 section 1.1).</summary>
    public static readonly IReadOnlyDictionary<string, int> Status = new Dictionary<string, int>
    {
        [UNAUTHENTICATED] = 401,
        [TOKEN_EXPIRED] = 401,
        [FORBIDDEN] = 403,
        [ACCOUNT_BANNED] = 403,
        [NOT_FOUND] = 404,
        [CLIENT_TOO_OLD] = 426,
        [CONTENT_TOO_OLD] = 409,
        [RATE_LIMITED] = 429,
        [MAINTENANCE] = 503,
        [VALIDATION] = 400,
        [IDEMPOTENCY_IN_FLIGHT] = 409,
        [INTERNAL] = 500,
    };

    /// <summary>
    /// The code for a response that left a handler or the framework with an error status and no body (a bare
    /// <c>NotFound()</c>, an unmatched route, a 415 from the formatter).
    /// </summary>
    public static string ForBareStatus(int status) => status switch
    {
        401 => UNAUTHENTICATED,
        403 => FORBIDDEN,
        404 or 405 => NOT_FOUND,
        426 => CLIENT_TOO_OLD,
        429 => RATE_LIMITED,
        503 => MAINTENANCE,
        >= 500 => INTERNAL,
        _ => VALIDATION,
    };

    public static string DefaultMessage(string code) => code switch
    {
        UNAUTHENTICATED => "Authentication required.",
        TOKEN_EXPIRED => "The access token has expired.",
        FORBIDDEN => "Not allowed.",
        ACCOUNT_BANNED => "This account is suspended.",
        NOT_FOUND => "No such route.",
        CLIENT_TOO_OLD => "This client version is no longer supported.",
        CONTENT_TOO_OLD => "This content version is too old for this request.",
        RATE_LIMITED => "Too many requests.",
        MAINTENANCE => "The service is under maintenance.",
        VALIDATION => "The request is not valid.",
        IDEMPOTENCY_IN_FLIGHT => "A request with this id is still in flight.",
        INTERNAL => "Internal error.",
        _ => code,
    };
}

/// <summary>
/// Thrown by a handler (or pipeline step) to answer with an error body; <see cref="RequestContextMiddleware"/> writes it.
/// Any other exception becomes <c>500 INTERNAL</c>.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int status, string code, string? message = null, object? details = null, int? retryAfterSec = null)
        : base(message ?? ErrorCodes.DefaultMessage(code))
    {
        Status = status;
        Code = code;
        Details = details;
        RetryAfterSec = retryAfterSec;
    }

    /// <summary>A global code at its specs/08 section 1.1 status.</summary>
    public static ApiException Of(string code, string? message = null, object? details = null, int? retryAfterSec = null) =>
        new(ErrorCodes.Status[code], code, message, details, retryAfterSec);

    public int Status { get; }
    public string Code { get; }
    public object? Details { get; }
    public int? RetryAfterSec { get; }
}

/// <summary>Writes error bodies (and <c>Retry-After</c> when the body carries <c>retryAfterSec</c>).</summary>
public static class ApiErrorWriter
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext ctx, int status, string code, string? message = null, object? details = null,
                                  int? retryAfterSec = null)
    {
        var body = new ErrorBody(code, message ?? ErrorCodes.DefaultMessage(code), RequestContext.RequestId(ctx), retryAfterSec, details);
        ctx.Response.StatusCode = status;
        RequestContext.SetErrorCode(ctx, code);
        if (retryAfterSec is int s) ctx.Response.Headers.RetryAfter = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ctx.Response.WriteAsJsonAsync(body, Json, contentType: "application/json; charset=utf-8", ctx.RequestAborted);
    }

    public static Task WriteAsync(HttpContext ctx, ApiException e) =>
        WriteAsync(ctx, e.Status, e.Code, e.Message, e.Details, e.RetryAfterSec);
}
