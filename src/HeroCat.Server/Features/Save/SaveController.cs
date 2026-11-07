using System.Security.Cryptography;
using System.Text.Json;
using HeroCat.Server.Features.Auth;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Blobs;
using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Save;

/// <summary>
/// The cloud-save endpoints (specs/08 section 3.1, the canonical cloud-save wire; specs/02 sections 9-10; issue #198):
/// <c>GET /save/meta</c>, <c>GET /save</c>, <c>PUT /save</c>, <c>GET /save/history</c>.
/// </summary>
[ApiController]
public sealed class SaveController : ControllerBase
{
    public const string IfMatchHeader = "If-Match";
    public const string SaveMetaHeaderName = "X-Save-Meta";
    public const string OctetStream = "application/octet-stream";

    /// <summary><c>GET /save</c> serves a file above this many bytes by redirect to a presigned URL (section 3.1: 64 KiB).</summary>
    public const int InlineMaxBytes = 64 * 1024;

    /// <summary>The presigned URL's lifetime (section 3.1: 5 minutes).</summary>
    public static readonly TimeSpan PresignLifetime = TimeSpan.FromMinutes(5);

    readonly SaveStore _store;
    readonly IBlobStore _blobs;
    readonly AuthService _auth;
    readonly ISavePlausibility _plausibility;
    readonly IResimSampler _sampler;
    readonly IPlausibilityFlagSink _flags;
    readonly TimeProvider _clock;
    readonly ServerOptions _options;
    readonly ILogger<SaveController> _log;

    public SaveController(SaveStore store, IBlobStore blobs, AuthService auth, ISavePlausibility plausibility, IResimSampler sampler,
                          IPlausibilityFlagSink flags, TimeProvider clock, IOptions<ServerOptions> options, ILogger<SaveController> log)
    {
        _store = store;
        _blobs = blobs;
        _auth = auth;
        _plausibility = plausibility;
        _sampler = sampler;
        _flags = flags;
        _clock = clock;
        _options = options.Value;
        _log = log;
    }

    string Bucket => string.IsNullOrWhiteSpace(_options.Blobs.SavesBucket)
        ? throw new InvalidOperationException("HeroCat:Blobs:SavesBucket is not configured")
        : _options.Blobs.SavesBucket;

    int MaxBytes => _options.Limits.SaveBlobMaxBytes > 0 ? _options.Limits.SaveBlobMaxBytes : 262_144;
    int HistoryKeep => _options.Limits.SaveHistoryKeep > 0 ? _options.Limits.SaveHistoryKeep : 10;

    /// <summary>The S3 key of a save blob (specs/08 section 3.1 step 7): <c>saves/{playerId}/{saveSeq}.sav</c>.</summary>
    public static string BlobKey(Guid playerId, long saveSeq) => $"saves/{playerId:D}/{saveSeq}.sav";

    /// <summary><c>saves.blob_key</c>: <c>s3://{bucket}/{key}</c> (specs/02 section 12).</summary>
    public static string BlobUri(string bucket, string key) => $"s3://{bucket}/{key}";

    static (string bucket, string key) ParseBlobUri(string uri)
    {
        const string scheme = "s3://";
        int slash = uri.IndexOf('/', scheme.Length);
        if (!uri.StartsWith(scheme, StringComparison.Ordinal) || slash < 0) throw new InvalidOperationException("bad saves.blob_key");
        return (uri[scheme.Length..slash], uri[(slash + 1)..]);
    }

    /// <summary>A new opaque ETag: weak, the save's <c>saveSeq</c> and 8 random hex, new on every write (<c>W/"1875-0f9e3a1b"</c>).</summary>
    public static string NewEtag(long saveSeq) => $"W/\"{saveSeq}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}\"";

    Guid Player => HttpContext.PlayerId()!.Value;

    /// <summary>The 02 section 9.1 metadata of the save of record plus its <c>etag</c>; <c>404 NO_SAVE</c> when there is none.</summary>
    [HttpGet("v1/save/meta"), Auth(AuthPolicy.Jwt), RateLimit(60)]
    [ProducesResponseType(typeof(SaveMetaDto), StatusCodes.Status200OK)]
    public async Task<SaveMetaDto> Meta(CancellationToken ct)
    {
        var row = await _store.GetAsync(Player, ct) ?? throw NoSave();
        return row.ToMeta();
    }

    /// <summary>
    /// The save of record as the raw file (<c>application/octet-stream</c>) with its <c>ETag</c> and the 9.1 metadata in
    /// <c>X-Save-Meta</c>; above 64 KiB a redirect to a 5-minute presigned URL instead, where the blob store can sign one.
    /// A download ends a pending rollback (section 7.4).
    /// </summary>
    [HttpGet("v1/save"), Auth(AuthPolicy.Jwt), RateLimit(10)]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK, OctetStream)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Download(CancellationToken ct)
    {
        var row = await _store.GetAsync(Player, ct) ?? throw NoSave();
        var (bucket, key) = ParseBlobUri(row.BlobKey);
        Response.Headers.ETag = row.Etag;
        Response.Headers[SaveMetaHeaderName] = JsonSerializer.Serialize(row.ToMeta(), SaveJson.Options);

        IActionResult result;
        Uri? url = row.SizeBytes > InlineMaxBytes ? await _blobs.PresignGetAsync(bucket, key, PresignLifetime, ct) : null;
        if (url != null) result = Redirect(url.AbsoluteUri);
        else
        {
            byte[] file = await _blobs.GetAsync(bucket, key, ct)
                ?? throw new InvalidOperationException($"save blob {row.BlobKey} is missing");
            result = File(file, OctetStream);
        }
        if (row.RollbackPending) await _store.ClearRollbackAsync(Player, ct);
        return result;
    }

    /// <summary>The newest <c>SAVE_HISTORY_KEEP</c> uploads, newest first. Restoring one is a support action.</summary>
    [HttpGet("v1/save/history"), Auth(AuthPolicy.Jwt), RateLimit(10)]
    [ProducesResponseType(typeof(List<SaveHistoryEntryDto>), StatusCodes.Status200OK)]
    public Task<List<SaveHistoryEntryDto>> History(CancellationToken ct) => _store.HistoryAsync(Player, HistoryKeep, ct);

    /// <summary>
    /// Uploads the save (specs/08 section 3.1), in the spec's step order: (1) header, <c>len</c> and CRC
    /// (<c>400 SAVE_MALFORMED</c>; over <c>SAVE_BLOB_MAX_BYTES</c> is <c>413 SAVE_TOO_LARGE</c>); (2) HMAC against the
    /// device key (recorded, flagged <c>HMAC_INVALID</c>, never a rejection); a pending rollback is <c>409 SAVE_ROLLED_BACK</c>;
    /// (3) <c>If-Match</c> (<c>409 SAVE_CONFLICT</c> with <c>details.serverMeta</c>); (4) <c>saveSeq</c>: not above this
    /// device's own last upload is <c>409 SAVE_STALE</c>, below another device's save of record is flagged
    /// <c>SEQ_REGRESSION</c>; (5) <c>CLOCK_AHEAD</c>; (5a, 6) the plausibility hook inline; (7) blob, <c>saves</c>,
    /// <c>save_history</c> (trimmed to <c>SAVE_HISTORY_KEEP</c>), all in one transaction with the idempotency row.
    /// </summary>
    [HttpPut("v1/save"), Auth(AuthPolicy.JwtDevice), RateLimit(6), RateLimit(400, RateLimitAttribute.Day), Idempotent]
    [Consumes(OctetStream)]
    [ProducesResponseType(typeof(PutSaveResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        Guid player = Player;
        Guid device = Guid.Parse(HttpContext.DeviceId()!);
        var now = _clock.GetUtcNow();

        // (1) the file
        byte[] file = await ReadBodyAsync(MaxBytes, ct);
        Observability.SaveMetrics.RecordUpload(file.Length);
        SaveEnvelope env;
        try { env = SaveEnvelope.Parse(file); }
        catch (SaveMalformedException e) { throw new ApiException(400, SaveErrors.SAVE_MALFORMED, "The save file is malformed: " + e.Message + "."); }
        if (env.PlayerId != player) throw Validation("The save belongs to another player.", "PlayerId");
        if (env.DeviceId != device) throw Validation("The save's DeviceId must be X-Device-Id.", "DeviceId");
        var meta = ParseMetaHeader();
        if (meta.PlayerId is Guid mp && mp != player) throw Validation("X-Save-Meta playerId does not match the save.", "playerId");
        if (meta.DeviceId is Guid md && md != device) throw Validation("X-Save-Meta deviceId does not match the save.", "deviceId");
        if (meta.SaveSeq is long ms && ms != env.SaveSeq) throw Validation("X-Save-Meta saveSeq does not match the save.", "saveSeq");
        if (meta.ClockAuthoritative is not bool clockAuthoritative) throw Validation("X-Save-Meta needs clockAuthoritative.", "clockAuthoritative");
        if (meta.ProgressScore is not double progress || !double.IsFinite(progress)) throw Validation("X-Save-Meta needs a finite progressScore.", "progressScore");
        if (meta.Summary is not { ValueKind: JsonValueKind.Object } summary) throw Validation("X-Save-Meta needs a summary object.", "summary");
        string clientVersion = meta.ClientVersion ?? Request.Headers[ApiHeaders.ClientVersion].ToString();

        await using var c = await _store.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SaveStore.LockPlayerAsync(c, tx, player, ct);

        // (2) HMAC against the device's registered key: recorded, not a rejection
        byte[]? keyEnc = await SaveStore.DeviceKeyEncAsync(c, tx, player, device, ct);
        bool hmacValid = keyEnc != null && env.HmacValid(file, _auth.UnprotectDeviceKey(device, keyEnc));
        var flags = new List<PlausibilityFlag>();
        if (!hmacValid) flags.Add(new PlausibilityFlag(player, SaveFlags.HMAC_INVALID, PlausibilityFlag.Suspicious, new { deviceId = device }));

        var current = await SaveStore.GetAsync(c, tx, player, ct);
        if (current is { RollbackPending: true })
            throw new ApiException(409, SaveErrors.SAVE_ROLLED_BACK, "This save was rolled back; download it before uploading.",
                                   new { serverMeta = current.ToMeta() });

        // (3) If-Match
        string? ifMatch = Request.Headers[IfMatchHeader].ToString().Trim() is { Length: > 0 } im ? im : null;
        if (ifMatch == null) throw ApiException.Of(ErrorCodes.VALIDATION, "If-Match is required (an etag, or * for a first upload).", new { header = IfMatchHeader });
        bool matches = ifMatch == "*" ? current == null : current != null && string.Equals(ifMatch, current.Etag, StringComparison.Ordinal);
        if (!matches)
            throw new ApiException(409, SaveErrors.SAVE_CONFLICT, "Another upload replaced the save this device last saw.",
                                   new { serverMeta = current?.ToMeta() });

        // (4) saveSeq
        long? ownLast = await SaveStore.LastSeqOfDeviceAsync(c, tx, player, device, ct);
        if (ownLast is long own && env.SaveSeq <= own)
            throw new ApiException(409, SaveErrors.SAVE_STALE, "This device already uploaded a newer save.", new { saveSeq = own });
        if (current != null && current.DeviceId != device && env.SaveSeq < current.SaveSeq)
            flags.Add(new PlausibilityFlag(player, SaveFlags.SEQ_REGRESSION, SaveFlags.Info, new { saveSeq = env.SaveSeq, storedSaveSeq = current.SaveSeq }));

        // (5) clock
        long nowSec = now.ToUnixTimeSeconds();
        if (env.LastSavedServerTime > nowSec + _options.Limits.ClockMaxSkewAcceptSeconds)
            flags.Add(new PlausibilityFlag(player, SaveFlags.CLOCK_AHEAD, PlausibilityFlag.Suspicious,
                                           new { lastSavedServerTime = env.LastSavedServerTime, serverTime = nowSec }));

        string bucket = Bucket, key = BlobKey(player, env.SaveSeq);
        var row = new StoredSave(player, env.SaveSeq, device, env.SchemaVersion, env.ContentVersion, clientVersion, env.LastSavedServerTime,
                                 clockAuthoritative, file.Length, env.Sha256, env.HmacHex, hmacValid, BlobUri(bucket, key), NewEtag(env.SaveSeq),
                                 progress, summary.GetRawText(), Array.Empty<string>(), false, false, now);
        var upload = new SaveUpload(player, device, env.SaveSeq, file, env, row.ToMeta(), current, now);

        // (5a, 6) crediting and the inline plausibility check
        var inline = await _plausibility.RunInlineAsync(upload, c, tx, ct);
        flags.AddRange(inline.Flags);
        bool requestCmdLog = (current?.CmdlogRequested ?? false) || _sampler.Sample(flags);
        string[] flagCodes = flags.Select(f => f.Code).Distinct().ToArray();
        row = row with { Flags = flagCodes, CmdlogRequested = requestCmdLog };

        // (7) blob, rows, idempotency row, one commit
        await _blobs.PutAsync(bucket, key, file, OctetStream, ct);
        await SaveStore.WriteAsync(c, tx, row, ct);
        var body = new PutSaveResponse(row.Etag, row.SaveSeq, nowSec, flagCodes, inline.DiamondReconcile, requestCmdLog);
        IActionResult result = HttpContext.Features.Get<IIdempotencyFeature>() is { } idem
            ? await idem.CompleteWithinAsync(c, tx, StatusCodes.Status200OK, body, ct)
            : Ok(body);
        await tx.CommitAsync(ct);

        foreach (var f in flags)
        {
            Observability.SaveMetrics.RecordFlag(f.Code, f.Severity);
            try { await _flags.RaiseAsync(f, CancellationToken.None); }
            catch (Exception e) { _log.LogError(e, "could not raise plausibility flag {Code}", f.Code); }
        }
        try { await _plausibility.AfterCommitAsync(upload, flags, CancellationToken.None); }
        catch (Exception e) { _log.LogError(e, "could not hand save {SaveSeq} to the plausibility checks", row.SaveSeq); }
        return result;
    }

    async Task<byte[]> ReadBodyAsync(int max, CancellationToken ct)
    {
        if (Request.ContentLength is long declared && declared > max) throw TooLarge(max);
        using var ms = new MemoryStream();
        byte[] buf = new byte[16 * 1024];
        int n;
        while ((n = await Request.Body.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw TooLarge(max);
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    SaveMetaHeader ParseMetaHeader()
    {
        string raw = Request.Headers[SaveMetaHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(raw)) throw ApiException.Of(ErrorCodes.VALIDATION, "X-Save-Meta is required.", new { header = SaveMetaHeaderName });
        try
        {
            return JsonSerializer.Deserialize<SaveMetaHeader>(raw, SaveJson.Options)
                ?? throw ApiException.Of(ErrorCodes.VALIDATION, "X-Save-Meta must be a JSON object.", new { header = SaveMetaHeaderName });
        }
        catch (JsonException)
        {
            throw ApiException.Of(ErrorCodes.VALIDATION, "X-Save-Meta must be the 02 section 9.1 metadata as JSON.", new { header = SaveMetaHeaderName });
        }
    }

    static ApiException NoSave() => new(404, SaveErrors.NO_SAVE, "This player has no cloud save.");

    static ApiException TooLarge(int max) =>
        new(413, SaveErrors.SAVE_TOO_LARGE, "The save is larger than SAVE_BLOB_MAX_BYTES.", new { maxBytes = max });

    static ApiException Validation(string message, string field) => ApiException.Of(ErrorCodes.VALIDATION, message, new { field });
}
