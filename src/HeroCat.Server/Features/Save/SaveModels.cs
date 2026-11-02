using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeroCat.Server.Features.Save;

/// <summary>The Save endpoints' own error codes (specs/08 section 3.1, 7.4; 01 section 5.2 classifies them).</summary>
public static class SaveErrors
{
    /// <summary>404: the player has no cloud save yet.</summary>
    public const string NO_SAVE = "NO_SAVE";
    /// <summary>400: the header, <c>len</c>, CRC or body root fields do not parse (step 1).</summary>
    public const string SAVE_MALFORMED = "SAVE_MALFORMED";
    /// <summary>413: the file is over <c>SAVE_BLOB_MAX_BYTES</c> (as built, issue #198).</summary>
    public const string SAVE_TOO_LARGE = "SAVE_TOO_LARGE";
    /// <summary>409: <c>If-Match</c> does not name the save of record; <c>details.serverMeta</c> carries it.</summary>
    public const string SAVE_CONFLICT = "SAVE_CONFLICT";
    /// <summary>409: a <c>saveSeq</c> not above this device's own last upload.</summary>
    public const string SAVE_STALE = "SAVE_STALE";
    /// <summary>409: an anti-cheat rollback is pending; the client must download (section 7.4).</summary>
    public const string SAVE_ROLLED_BACK = "SAVE_ROLLED_BACK";
}

/// <summary>Flags the upload steps raise themselves (specs/08 section 7.2); the rest come from the plausibility module.</summary>
public static class SaveFlags
{
    public const string HMAC_INVALID = "HMAC_INVALID";
    public const string SEQ_REGRESSION = "SEQ_REGRESSION";
    public const string CLOCK_AHEAD = "CLOCK_AHEAD";
    public const string Info = "info";
}

/// <summary>The <c>summary</c> object of 02 section 9.1, kept as the client sent it (an object; its keys are 02's).</summary>
public sealed record SaveMetaDto(
    Guid PlayerId, Guid DeviceId, long SaveSeq, long LastSavedServerTime, int SchemaVersion, string ContentVersion,
    string ClientVersion, int SizeBytes, string Sha256, string Hmac, bool ClockAuthoritative, double ProgressScore,
    JsonElement Summary, string Etag);

/// <summary>The <c>X-Save-Meta</c> request header (02 section 9.1): every field optional on the wire, checked by the handler.</summary>
public sealed class SaveMetaHeader
{
    public Guid? PlayerId { get; set; }
    public Guid? DeviceId { get; set; }
    public long? SaveSeq { get; set; }
    public string? ClientVersion { get; set; }
    public bool? ClockAuthoritative { get; set; }
    public double? ProgressScore { get; set; }
    public JsonElement? Summary { get; set; }
}

/// <summary>The <c>diamondReconcile</c> object of a <c>PUT /save</c> response (specs/08 section 3.1, 7.4).</summary>
public sealed record DiamondReconcileDto(long ClientBalance, long ServerBalance, bool Applied);

/// <summary><c>PUT /save</c> 200 (specs/08 section 3.1).</summary>
public sealed record PutSaveResponse(string Etag, long SaveSeq, long ServerTime, IReadOnlyList<string> Flags,
                                     DiamondReconcileDto? DiamondReconcile, bool RequestCmdLog);

/// <summary>One <c>GET /save/history</c> entry.</summary>
public sealed record SaveHistoryEntryDto(long SaveSeq, Guid DeviceId, long UploadedAt, JsonElement Summary);

static class SaveJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
