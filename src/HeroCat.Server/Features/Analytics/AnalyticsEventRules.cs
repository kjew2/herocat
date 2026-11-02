using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeroCat.Sim.Economy;

namespace HeroCat.Server.Features.Analytics;

/// <summary>One row of the backend <c>events</c> table (specs/02 section 12, migrations 0001 and 0005).</summary>
public sealed record EventRow(
    Guid EventId, DateTimeOffset Ts, string Name, byte[] PlayerIdHash, Guid? SessionId, int? DayIndex, int? Stage, string PropsJson,
    string Env, string Source, Guid? DeviceId, string? ClientVersion, string? ContentVersion, int? SchemaVersion, bool? DebugTainted,
    bool? ClockAuthoritative, short AgeGate);

/// <summary>The per-event refusal codes of <c>POST /analytics/events</c> (specs/08 section 3.5). The batch itself still succeeds.</summary>
public static class AnalyticsEventCodes
{
    /// <summary>Not an object, or a field of the wrong type (eventId not 32 hex, ts not an integer, props not an object, ...).</summary>
    public const string EVENT_MALFORMED = "EVENT_MALFORMED";
    /// <summary>Not an economy event of the specs/02 section 14.4 index (only those come to the backend).</summary>
    public const string EVENT_NAME_INVALID = "EVENT_NAME_INVALID";
    /// <summary>A <c>reason</c> outside the closed CurrencyReason enum (00 contract 6), or a <c>currency_changed</c> without one.</summary>
    public const string EVENT_REASON_INVALID = "EVENT_REASON_INVALID";
    /// <summary><c>ts</c> older than ANALYTICS_EVENT_MAX_AGE_DAYS, ahead of now by more than CLOCK_MAX_SKEW_ACCEPT_S, or on a day without a partition.</summary>
    public const string EVENT_TS_OUT_OF_RANGE = "EVENT_TS_OUT_OF_RANGE";
    /// <summary><c>props</c> serialised larger than ANALYTICS_PROPS_MAX_BYTES.</summary>
    public const string EVENT_TOO_LARGE = "EVENT_TOO_LARGE";
}

/// <summary>
/// The rules the backend applies to analytics events (specs/02 section 14, specs/08 section 3.5): which names it stores,
/// the closed reason enum, the server-side player hash and the envelope field names. HeroCat.Server never references
/// HeroCat.Runtime (01 section 1.2), so the economy name set is mirrored here and a test diffs it against
/// <c>HeroCat.Runtime.Services.AnalyticsNames.Economy</c>.
/// </summary>
public static class AnalyticsEventRules
{
    public const string SourceClient = "client";
    public const string SourceServer = "server";
    public const string CurrencyChanged = "currency_changed";
    public const string ReasonProp = "reason";

    /// <summary>02 section 14.1: economy events (a currency, purchase, summon, dungeon or inbox payload).</summary>
    public static readonly IReadOnlySet<string> Economy = new HashSet<string>(StringComparer.Ordinal)
    {
        "currency_changed", "quest_claimed", "dungeon_run", "offline_claimed", "daily_gift_claimed", "exchange",
        "iap_initiated", "iap_completed", "iap_failed", "iap_status", "inbox_claimed",
        "summon", "summon_reconciled", "item_acquired", "limit_break", "awakened",
    };

    /// <summary>The 02 section 14.1 envelope keys (and the per-event <c>name</c>, <c>eventId</c>, <c>props</c>) of a queued line.</summary>
    public static readonly IReadOnlySet<string> EnvelopeFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "ts", "sessionId", "playerIdHash", "deviceId", "clientVersion", "contentVersion", "schemaVersion", "debugTainted",
        "clockAuthoritative", "dayIndex", "stage", "ageGate",
    };

    /// <summary>
    /// Advertising identifiers never reach the events table (08 section 10.1: none for <c>ageGate = 1</c>; no economy event
    /// defines one, so they are stripped for everyone).
    /// </summary>
    public static readonly IReadOnlySet<string> AdvertisingIdProps = new HashSet<string>(StringComparer.Ordinal)
    {
        "advertisingId", "adId", "idfa", "idfv", "gaid", "aaid",
    };

    /// <summary>The closed reason list of 00 contract 6, by exact name.</summary>
    public static readonly IReadOnlySet<string> Reasons = new HashSet<string>(Enum.GetNames<CurrencyReason>(), StringComparer.Ordinal);

    public static bool IsReason(string? s) => s != null && Reasons.Contains(s);

    /// <summary>
    /// 02 section 14.1's <c>playerIdHash</c>: SHA-256 of UTF-8 <c>salt + ":" + playerId</c> (playerId as the lowercase
    /// 36-character GUID), the same value the client's AnalyticsEnvelope sends in hex, so Firebase rows join these.
    /// </summary>
    public static byte[] HashPlayerId(Guid playerId, string salt) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + playerId.ToString("D")));

    /// <summary>08 section 10.1's envelope value: 1 for an under-age player, else 0 (the token's <c>age</c> is 0 unknown, 1 under-13, 2 adult).</summary>
    public static short EnvelopeAgeGate(int accountAgeGate) => (short)(accountAgeGate == 1 ? 1 : 0);

    /// <summary>The <c>events</c> partition a row at <paramref name="ts"/> lands in (<c>events_pYYYYMMDD</c>, UTC; 02 section 12).</summary>
    public static string PartitionOf(DateTimeOffset ts) => "events_p" + ts.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Property names: camelCase ASCII, letters and digits only, lowercase first (02 section 14.1).</summary>
    public static bool IsCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] < 'a' || name[0] > 'z') return false;
        foreach (char c in name)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
        return true;
    }
}

/// <summary>What the ingest needs besides the event: the caller, the clock and the limits.</summary>
public sealed record IngestContext(byte[] PlayerIdHash, short AgeGate, string Env, DateTimeOffset Now, TimeSpan MaxAge, TimeSpan MaxAhead, int PropsMaxBytes);

/// <summary>One event's outcome: a row to store, or a refusal code.</summary>
public sealed record ParsedEvent(EventRow? Row, string? Code, string? EventId);

/// <summary>
/// Turns one queued client line (02 section 14.1: <c>{"name", "eventId", envelope..., "props"}</c>) into an
/// <see cref="EventRow"/> or a per-event refusal. The caller's identity, never the client's, gives <c>player_id_hash</c>
/// and <c>age_gate</c>; the client's <c>playerIdHash</c> and <c>ageGate</c> are ignored.
/// </summary>
public static class AnalyticsEventParser
{
    public static ParsedEvent Parse(JsonElement e, IngestContext ctx)
    {
        if (e.ValueKind != JsonValueKind.Object) return Fail(AnalyticsEventCodes.EVENT_MALFORMED, null);
        string? eventIdText = Str(e, "eventId");
        if (eventIdText is not { Length: 32 } || !Guid.TryParseExact(eventIdText, "N", out var eventId))
            return Fail(AnalyticsEventCodes.EVENT_MALFORMED, eventIdText);
        string id = eventId.ToString("N");

        string? name = Str(e, "name");
        if (name == null || !AnalyticsEventRules.Economy.Contains(name)) return Fail(AnalyticsEventCodes.EVENT_NAME_INVALID, id);

        if (!e.TryGetProperty("ts", out var tsEl) || tsEl.ValueKind != JsonValueKind.Number || !tsEl.TryGetInt64(out long tsSec))
            return Fail(AnalyticsEventCodes.EVENT_MALFORMED, id);
        if (tsSec < DateTimeOffset.MinValue.ToUnixTimeSeconds() || tsSec > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return Fail(AnalyticsEventCodes.EVENT_TS_OUT_OF_RANGE, id);
        var ts = DateTimeOffset.FromUnixTimeSeconds(tsSec);
        if (ts < ctx.Now - ctx.MaxAge || ts > ctx.Now + ctx.MaxAhead) return Fail(AnalyticsEventCodes.EVENT_TS_OUT_OF_RANGE, id);

        // props: an object of camelCase owner fields; never an envelope name or the raw playerId; advertising ids dropped.
        string propsJson = "{}";
        if (e.TryGetProperty("props", out var props) && props.ValueKind != JsonValueKind.Null)
        {
            if (props.ValueKind != JsonValueKind.Object) return Fail(AnalyticsEventCodes.EVENT_MALFORMED, id);
            var kept = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in props.EnumerateObject())
            {
                if (!AnalyticsEventRules.IsCamelCase(p.Name) || p.Name == "playerId" || AnalyticsEventRules.EnvelopeFields.Contains(p.Name))
                    return Fail(AnalyticsEventCodes.EVENT_MALFORMED, id);
                if (AnalyticsEventRules.AdvertisingIdProps.Contains(p.Name)) continue;
                kept[p.Name] = p.Value;
            }
            if (kept.TryGetValue(AnalyticsEventRules.ReasonProp, out var reason)
                && (reason.ValueKind != JsonValueKind.String || !AnalyticsEventRules.IsReason(reason.GetString())))
                return Fail(AnalyticsEventCodes.EVENT_REASON_INVALID, id);
            if (name == AnalyticsEventRules.CurrencyChanged && !kept.ContainsKey(AnalyticsEventRules.ReasonProp))
                return Fail(AnalyticsEventCodes.EVENT_REASON_INVALID, id);
            propsJson = JsonSerializer.Serialize(kept);
            if (Encoding.UTF8.GetByteCount(propsJson) > ctx.PropsMaxBytes) return Fail(AnalyticsEventCodes.EVENT_TOO_LARGE, id);
        }
        else if (name == AnalyticsEventRules.CurrencyChanged) return Fail(AnalyticsEventCodes.EVENT_REASON_INVALID, id);

        if (!OptGuid(e, "sessionId", out var session) || !OptGuid(e, "deviceId", out var device)
            || !OptInt(e, "dayIndex", out var dayIndex) || !OptInt(e, "stage", out var stage) || !OptInt(e, "schemaVersion", out var schema)
            || !OptBool(e, "debugTainted", out var tainted) || !OptBool(e, "clockAuthoritative", out var authoritative)
            || !OptStr(e, "clientVersion", out var clientVersion) || !OptStr(e, "contentVersion", out var contentVersion))
            return Fail(AnalyticsEventCodes.EVENT_MALFORMED, id);

        var row = new EventRow(eventId, ts, name, ctx.PlayerIdHash, session, dayIndex, stage, propsJson, ctx.Env, AnalyticsEventRules.SourceClient,
                               device, clientVersion, contentVersion, schema, tainted, authoritative, ctx.AgeGate);
        return new ParsedEvent(row, null, id);
    }

    static ParsedEvent Fail(string code, string? eventId) => new(null, code, eventId);

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Absent(JsonElement e, string name, out JsonElement v) =>
        !e.TryGetProperty(name, out v) || v.ValueKind == JsonValueKind.Null;

    static bool OptGuid(JsonElement e, string name, out Guid? value)
    {
        value = null;
        if (Absent(e, name, out var v)) return true;
        if (v.ValueKind != JsonValueKind.String || !Guid.TryParse(v.GetString(), out var g)) return false;
        value = g;
        return true;
    }

    static bool OptInt(JsonElement e, string name, out int? value)
    {
        value = null;
        if (Absent(e, name, out var v)) return true;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) return false;
        value = i;
        return true;
    }

    static bool OptBool(JsonElement e, string name, out bool? value)
    {
        value = null;
        if (Absent(e, name, out var v)) return true;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = v.GetBoolean();
        return true;
    }

    static bool OptStr(JsonElement e, string name, out string? value)
    {
        value = null;
        if (Absent(e, name, out var v)) return true;
        if (v.ValueKind != JsonValueKind.String) return false;
        value = v.GetString();
        return value!.Length <= 64;
    }
}
