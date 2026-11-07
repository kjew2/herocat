using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using HeroCat.Server.Infrastructure.Idempotency;
using HeroCat.Server.Infrastructure.Secrets;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace HeroCat.Server.Pipeline;

/// <summary>What an idempotent route's rows are keyed by (specs/02 section 12 <c>idempotency.player_id</c>).</summary>
public enum IdempotencyKeyKind
{
    /// <summary>The caller's <c>playerId</c> (every authenticated route).</summary>
    Player,
    /// <summary>A UUID derived from <c>X-Device-Id</c> (<see cref="IdempotencyKeys.ForDevice"/>): <c>/auth/guest</c>, which has no player yet.</summary>
    Device,
}

/// <summary>
/// Marks a mutating endpoint idempotent by <c>X-Request-Id</c> (specs/08 section 1.1, the "idempotent" segment of its
/// endpoint line). <see cref="IdempotencyMiddleware"/> then requires a 32-hex id, replays a stored response and refuses
/// an id in flight or used by another player.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class IdempotentAttribute : Attribute
{
    public IdempotentAttribute(IdempotencyKeyKind key = IdempotencyKeyKind.Player) => Key = key;
    public IdempotencyKeyKind Key { get; }

    /// <summary>
    /// Store the response body encrypted under the data key (specs/08 section 9.3), for a response that carries
    /// credentials (<c>/auth/guest</c>'s tokens), so a database read does not yield live tokens.
    /// </summary>
    public bool ProtectStoredBody { get; set; }
}

/// <summary>
/// The running request's idempotency row, for a handler that writes the final row inside its own transaction
/// (specs/08 section 4.3). Without it the middleware stores the response after the handler returns.
/// </summary>
public interface IIdempotencyFeature
{
    Guid Key { get; }
    string RequestId { get; }
    bool Completed { get; }

    /// <summary>
    /// Serializes <paramref name="body"/> once, stores it as the final row in <paramref name="transaction"/>, and returns
    /// the result that writes exactly those bytes, so the first response and every replay are byte-identical.
    /// </summary>
    Task<IActionResult> CompleteWithinAsync(DbConnection connection, DbTransaction transaction, int statusCode, object body, CancellationToken ct);
}

/// <summary>
/// The idempotency store in front of every <see cref="IdempotentAttribute"/> endpoint (specs/08 section 1.1, 00 section
/// 8.13, issue #194). Runs after the gates, so the player is known. Per request:
/// <list type="bullet">
/// <item>no 32-hex <c>X-Request-Id</c>: <c>400 VALIDATION</c> (<c>details.header</c>);</item>
/// <item>an <c>in_flight</c> row is inserted first; a final row is replayed with <c>X-Idempotent-Replay: true</c>; an
/// <c>in_flight</c> row is <c>409 IDEMPOTENCY_IN_FLIGHT</c> with <c>retryAfterSec: 1</c>;</item>
/// <item>an id under another player is <c>403 FORBIDDEN</c> (<c>details.reason = DUPLICATE_REQUEST_ID</c>) and raises that flag;</item>
/// <item>the same id for a different method, route or body is <c>400 VALIDATION</c> (<c>details.reason = REQUEST_ID_REUSED</c>).</item>
/// </list>
/// Every response below 500 is stored, error bodies included, except a <c>409 IDEMPOTENCY_IN_FLIGHT</c> the handler
/// throws (the summon lock); a 5xx or an unhandled exception deletes the <c>in_flight</c> row so a retry runs fresh.
/// </summary>
public sealed partial class IdempotencyMiddleware
{
    public const string ReplayHeader = "X-Idempotent-Replay";
    public const string ReasonDuplicate = "DUPLICATE_REQUEST_ID";
    public const string ReasonReused = "REQUEST_ID_REUSED";

    /// <summary>Response headers a replay repeats besides <c>Content-Type</c>.</summary>
    static readonly string[] ReplayedHeaders = { "ETag", "Location", "X-Save-Meta" };

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex RequestIdFormat();

    readonly RequestDelegate _next;
    readonly IIdempotencyStore _store;
    readonly IPlausibilityFlagSink _flags;
    readonly TimeProvider _clock;
    readonly ILogger<IdempotencyMiddleware> _log;

    public IdempotencyMiddleware(RequestDelegate next, IIdempotencyStore store, IPlausibilityFlagSink flags, TimeProvider clock,
                                 ILogger<IdempotencyMiddleware> log)
    {
        _next = next;
        _store = store;
        _flags = flags;
        _clock = clock;
        _log = log;
    }

    public static bool IsIdempotent(Endpoint? endpoint) => endpoint?.Metadata.GetMetadata<IdempotentAttribute>() != null;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var attr = ctx.GetEndpoint()?.Metadata.GetMetadata<IdempotentAttribute>();
        if (attr == null)
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        string? requestId = ctx.Request.Headers[ApiHeaders.RequestId];
        if (requestId == null || !RequestIdFormat().IsMatch(requestId))
            throw ApiException.Of(ErrorCodes.VALIDATION, "X-Request-Id must be 32 hex characters on a mutating request.",
                                  new { header = ApiHeaders.RequestId });

        Guid key;
        Guid? player = ctx.PlayerId();
        if (attr.Key == IdempotencyKeyKind.Player && player is Guid p) key = p;
        else
        {
            string? device = ctx.Request.Headers[ApiHeaders.DeviceId];
            if (string.IsNullOrWhiteSpace(device))
                throw ApiException.Of(ErrorCodes.VALIDATION, "X-Device-Id is required.", new { header = ApiHeaders.DeviceId });
            key = IdempotencyKeys.ForDevice(device);
        }

        ctx.Request.EnableBuffering();
        using var requestBody = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(requestBody, ctx.RequestAborted).ConfigureAwait(false);
        ctx.Request.Body.Position = 0;
        string route = RateLimitGate.RouteOf(ctx);
        string hash = IdempotencyKeys.RequestHash(ctx.Request.Method, ctx.Request.Path.Value ?? "", requestBody.GetBuffer().AsSpan(0, (int)requestBody.Length));

        var begin = await _store.BeginAsync(key, requestId, route, hash, _clock.GetUtcNow(), ctx.RequestAborted).ConfigureAwait(false);
        switch (begin.Outcome)
        {
            case IdempotencyOutcome.Replay:
                var stored = begin.Response!;
                if (stored.Protected)
                    stored = stored with { Body = Protector(ctx).Unprotect(stored.Body, Aad(key, requestId)), Protected = false };
                await WriteReplayAsync(ctx, stored).ConfigureAwait(false);
                Observability.IdempotencyMetrics.RecordReplay(route);
                return;
            case IdempotencyOutcome.InFlight:
                throw ApiException.Of(ErrorCodes.IDEMPOTENCY_IN_FLIGHT, retryAfterSec: 1);
            case IdempotencyOutcome.OtherPlayer:
                if (player is Guid flagged)
                    await _flags.RaiseAsync(new PlausibilityFlag(flagged, PlausibilityFlag.DuplicateRequestId, PlausibilityFlag.Certain,
                                                                 new { requestId, route }), CancellationToken.None).ConfigureAwait(false);
                throw ApiException.Of(ErrorCodes.FORBIDDEN, "This request id belongs to another player.", new { reason = ReasonDuplicate });
            case IdempotencyOutcome.Mismatch:
                throw ApiException.Of(ErrorCodes.VALIDATION, "This request id was already used for a different request.",
                                      new { header = ApiHeaders.RequestId, reason = ReasonReused });
        }

        await RunAsync(ctx, key, requestId, attr.ProtectStoredBody).ConfigureAwait(false);
    }

    static SecretProtector Protector(HttpContext ctx) => ctx.RequestServices.GetRequiredService<SecretProtector>();

    /// <summary>Binds a protected body to its row.</summary>
    static byte[] Aad(Guid key, string requestId) => System.Text.Encoding.UTF8.GetBytes("idempotency/" + key.ToString("D") + "/" + requestId);

    async Task RunAsync(HttpContext ctx, Guid key, string requestId, bool protect)
    {
        var original = ctx.Features.Get<IHttpResponseBodyFeature>()!;
        using var buffer = new MemoryStream();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer, original));
        var feature = new Feature(_store, key, requestId);
        ctx.Features.Set<IIdempotencyFeature>(feature);

        try
        {
            try
            {
                await _next(ctx).ConfigureAwait(false);
            }
            catch (ApiException e) when (e.Status < 500 && e.Code != ErrorCodes.IDEMPOTENCY_IN_FLIGHT && !ctx.Response.HasStarted)
            {
                // A deterministic refusal (402 INSUFFICIENT_FUNDS, ...) is the request's answer: store it like a success.
                ctx.Response.Clear();
                await ApiErrorWriter.WriteAsync(ctx, e).ConfigureAwait(false);
            }

            int status = ctx.Response.StatusCode;
            if (status >= 400 && buffer.Length == 0)
                await ApiErrorWriter.WriteAsync(ctx, status, ErrorCodes.ForBareStatus(status)).ConfigureAwait(false);
        }
        catch
        {
            ctx.Features.Set(original);
            await AbandonAsync(key, requestId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            ctx.Features.Set(original);
        }

        byte[] body = buffer.ToArray();
        int code = ctx.Response.StatusCode;
        if (code >= 500) await AbandonAsync(key, requestId).ConfigureAwait(false);
        else if (!feature.Completed)
        {
            var headers = new Dictionary<string, string>();
            foreach (string h in ReplayedHeaders)
                if (ctx.Response.Headers.TryGetValue(h, out var v) && v.Count > 0) headers[h] = v.ToString();
            // Stored even if the client went away: the handler's writes are done, and the retry must replay them.
            var response = protect
                ? new StoredResponse(code, ctx.Response.ContentType, headers, Protector(ctx).Protect(body, Aad(key, requestId)), Protected: true)
                : new StoredResponse(code, ctx.Response.ContentType, headers, body);
            await _store.CompleteAsync(key, requestId, response, CancellationToken.None).ConfigureAwait(false);
        }

        if (body.Length > 0) await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted).ConfigureAwait(false);
    }

    async Task AbandonAsync(Guid key, string requestId)
    {
        try
        {
            await _store.AbandonAsync(key, requestId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The row stays in_flight until IDEMPOTENCY_TTL_H; the retry gets 409 until then. Loud, not fatal.
            _log.LogError(e, "could not release in_flight idempotency row for request {RequestId}", requestId);
        }
    }

    static async Task WriteReplayAsync(HttpContext ctx, StoredResponse r)
    {
        ctx.Response.StatusCode = r.StatusCode;
        if (r.ContentType != null) ctx.Response.ContentType = r.ContentType;
        foreach (var (name, value) in r.Headers) ctx.Response.Headers[name] = value;
        ctx.Response.Headers[ReplayHeader] = "true";
        ctx.Response.ContentLength = r.Body.Length;
        if (r.Body.Length > 0) await ctx.Response.Body.WriteAsync(r.Body, ctx.RequestAborted).ConfigureAwait(false);
    }

    sealed class Feature : IIdempotencyFeature
    {
        readonly IIdempotencyStore _store;

        public Feature(IIdempotencyStore store, Guid key, string requestId)
        {
            _store = store;
            Key = key;
            RequestId = requestId;
        }

        public Guid Key { get; }
        public string RequestId { get; }
        public bool Completed { get; private set; }

        public async Task<IActionResult> CompleteWithinAsync(DbConnection connection, DbTransaction transaction, int statusCode, object body, CancellationToken ct)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json);
            const string contentType = "application/json; charset=utf-8";
            await _store.CompleteAsync(connection, transaction, Key, RequestId,
                                       new StoredResponse(statusCode, contentType, new Dictionary<string, string>(), bytes), ct).ConfigureAwait(false);
            Completed = true;
            return new StoredBytesResult(statusCode, contentType, bytes);
        }
    }

    /// <summary>Writes stored bytes as they are.</summary>
    sealed class StoredBytesResult : IActionResult
    {
        readonly int _status;
        readonly string _contentType;
        readonly byte[] _bytes;

        public StoredBytesResult(int status, string contentType, byte[] bytes)
        {
            _status = status;
            _contentType = contentType;
            _bytes = bytes;
        }

        public async Task ExecuteResultAsync(ActionContext context)
        {
            var r = context.HttpContext.Response;
            r.StatusCode = _status;
            r.ContentType = _contentType;
            r.ContentLength = _bytes.Length;
            await r.Body.WriteAsync(_bytes, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
