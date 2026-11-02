using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HeroCat.Server.Pipeline;

namespace HeroCat.Server.Features.Config;

/// <summary>The Config feature's endpoint codes (specs/08 section 6.2).</summary>
public static class ConfigErrors
{
    /// <summary>400 on activation: a content manifest's bytes on the CDN do not hash to the config's <c>sha256</c>, or it is not there.</summary>
    public const string CONTENT_HASH_MISMATCH = "CONTENT_HASH_MISMATCH";
}

/// <summary>A <c>config_versions</c> row (specs/02 section 12).</summary>
public sealed record ConfigVersionRow(int Id, string Env, JsonObject Payload, bool Active, DateTimeOffset CreatedAt, string CreatedBy);

/// <summary>The columns a stored payload denormalises (<c>min_client_version</c>, <c>content_version</c>, <c>rollout_pct</c>).</summary>
public sealed record ConfigColumns(string MinClientVersion, string ContentVersion, int RolloutPct);

/// <summary>
/// The active <c>config_versions</c> row as the api reads it: the stored payload (never mutated; responses clone it), the
/// gates' view of it, and the kill switches the features read (specs/08 section 6.1).
/// </summary>
public sealed class ActiveConfig
{
    public ActiveConfig(int id, JsonObject payload)
    {
        Id = id;
        Payload = payload;
        Gates = new ClientGateConfig(Str(payload["minClientVersion"]), StringMap(payload["minContentVersionByClient"]), Limits(payload["limits"]));
    }

    /// <summary>The row id, served as <c>configVersion</c>.</summary>
    public int Id { get; }

    /// <summary>The stored payload. Read-only by convention: clone before changing.</summary>
    public JsonObject Payload { get; }

    public ClientGateConfig Gates { get; }

    /// <summary>A boolean kill switch (<c>shopEnabled</c>, <c>dungeonsEnabled</c>, ...); absent or not a bool means enabled.</summary>
    public bool Enabled(string killSwitch) =>
        !(Payload["killSwitches"] is JsonObject ks && ks[killSwitch] is JsonValue v && v.TryGetValue<bool>(out bool b)) || b;

    /// <summary>
    /// <c>killSwitches.summonEnabled</c> for one banner (<c>WEAPON</c>, ...): a per-banner object, or one bool for all
    /// banners; absent means enabled. <c>/summon/pull</c> answers <c>503 SUMMON_DISABLED</c> when this is false (acceptance 17).
    /// </summary>
    public bool SummonEnabled(string banner)
    {
        if (Payload["killSwitches"] is not JsonObject ks) return true;
        return ks["summonEnabled"] switch
        {
            JsonValue all when all.TryGetValue<bool>(out bool b) => b,
            JsonObject per when per[banner] is JsonValue v && v.TryGetValue<bool>(out bool b) => b,
            _ => true,
        };
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static IReadOnlyDictionary<string, string> StringMap(JsonNode? n)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (n is JsonObject o)
            foreach (var (k, v) in o)
                if (Str(v) is string s) d[k] = s;
        return d;
    }

    static RemoteLimits? Limits(JsonNode? n) =>
        n is JsonObject o ? new RemoteLimits(Int(o["summonPerMinute"]), Int(o["saveUploadsPerDay"])) : null;

    static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out int i) ? i : null;
}

/// <summary>
/// The admin payload of <c>POST /admin/config</c> (specs/08 section 6.1-6.2): checked, stripped of what the server
/// computes per request, and reduced to its denormalised columns.
/// </summary>
public static partial class ConfigPayload
{
    /// <summary>Fields the server fills per request; a published payload's copies are dropped.</summary>
    static readonly string[] ServerFields = { "configVersion", "serverTime" };

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();

    /// <summary>Validates the payload in place (removing server-computed fields) and returns its columns, or throws <c>400 VALIDATION</c>.</summary>
    public static ConfigColumns Normalise(JsonObject payload)
    {
        foreach (string f in ServerFields) payload.Remove(f);

        string minClient = RequiredString(payload, "minClientVersion");
        if (!ClientVersion.TryParse(minClient, out _)) throw Invalid("minClientVersion", "must be a client semver.");
        if (payload["latestClientVersion"] is JsonNode latest && !ClientVersion.TryParse(Str(latest), out _))
            throw Invalid("latestClientVersion", "must be a client semver.");

        var content = payload["content"] as JsonObject ?? throw Invalid("content", "is required.");
        string version = Content(content, "content");
        int pct = 100;
        if (content["rolloutPct"] is JsonNode p)
        {
            if (p is not JsonValue pv || !pv.TryGetValue<int>(out pct) || pct is < 0 or > 100)
                throw Invalid("content.rolloutPct", "must be an integer 0-100.");
        }
        else content["rolloutPct"] = 100;

        if (payload["defaultContent"] is JsonNode d)
        {
            if (d is not JsonObject dc) throw Invalid("defaultContent", "must be an object.");
            Content(dc, "defaultContent");
            dc.Remove("inBucket");
        }
        else if (pct < 100) throw Invalid("defaultContent", "is required while content.rolloutPct is below 100: callers outside the bucket get it.");

        if (payload["minContentVersionByClient"] is JsonNode m)
        {
            if (m is not JsonObject mo) throw Invalid("minContentVersionByClient", "must be an object.");
            foreach (var (k, v) in mo)
                if (!ClientVersion.TryParse(k, out _) || !ContentVersion.TryParse(Str(v), out _))
                    throw Invalid("minContentVersionByClient", $"entry '{k}' must map a client semver to a content version.");
        }

        if (payload["limits"] is JsonNode l)
        {
            if (l is not JsonObject lo) throw Invalid("limits", "must be an object.");
            foreach (string name in new[] { "summonPerMinute", "saveUploadsPerDay" })
                if (lo[name] is JsonNode n && (n is not JsonValue nv || !nv.TryGetValue<int>(out int i) || i < 1))
                    throw Invalid("limits." + name, "must be a positive integer.");
        }

        foreach (string obj in new[] { "killSwitches", "maintenance", "features", "storeUrls" })
            if (payload[obj] is JsonNode o && o is not JsonObject) throw Invalid(obj, "must be an object.");
        foreach (string arr in new[] { "banners", "events" })
            if (payload[arr] is JsonNode a && a is not JsonArray) throw Invalid(arr, "must be an array.");

        return new ConfigColumns(minClient, version, pct);
    }

    /// <summary>Checks a content pointer (<c>version</c>, <c>url</c>, <c>sha256</c>) and drops its per-request <c>inBucket</c>.</summary>
    static string Content(JsonObject c, string field)
    {
        c.Remove("inBucket");
        string version = RequiredString(c, "version", field);
        if (!ContentVersion.TryParse(version, out _)) throw Invalid(field + ".version", "must be YYYY.MM.DD[.N].");
        string url = RequiredString(c, "url", field);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            throw Invalid(field + ".url", "must be an absolute http(s) URL.");
        if (!Sha256Hex().IsMatch(RequiredString(c, "sha256", field))) throw Invalid(field + ".sha256", "must be 64 hex characters.");
        if (c["minClientVersion"] is JsonNode mc && !ClientVersion.TryParse(Str(mc), out _))
            throw Invalid(field + ".minClientVersion", "must be a client semver.");
        return version;
    }

    static string RequiredString(JsonObject o, string name, string? parent = null) =>
        Str(o[name]) is { Length: > 0 } s ? s : throw Invalid(parent == null ? name : parent + "." + name, "is required.");

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static ApiException Invalid(string field, string why) => ApiException.Of(ErrorCodes.VALIDATION, $"{field} {why}", new { field });
}

/// <summary>
/// The content rollout bucket (specs/08 section 6.1, issue #31): <c>inBucket = hash(playerId || content.version) % 100 &lt; rolloutPct</c>,
/// computed by the server only. The hash is the first 8 bytes, big-endian, of SHA-256 over the UTF-8 of the player id
/// (lowercase, hyphenated) followed by the content version. Stable per version, so raising <c>rolloutPct</c> only adds players.
/// </summary>
public static class RolloutBucket
{
    public static int Bucket(Guid playerId, string contentVersion)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(playerId.ToString("D") + contentVersion));
        return (int)(BinaryPrimitives.ReadUInt64BigEndian(h) % 100);
    }

    public static bool InBucket(Guid playerId, string contentVersion, int rolloutPct) => Bucket(playerId, contentVersion) < rolloutPct;
}

/// <summary>Builds the <c>GET /config</c> body (specs/08 section 6.1) from the active payload.</summary>
public static class ConfigResponse
{
    /// <summary>
    /// <c>configVersion</c> and <c>serverTime</c> first, then the payload with <c>content</c> chosen by the rollout bucket:
    /// with a player, <c>content</c> and <c>inBucket: true</c> inside the bucket, else <c>defaultContent</c> and
    /// <c>inBucket: false</c>; without one, the non-rollout default (<c>defaultContent</c> while <c>rolloutPct</c> is
    /// below 100, else <c>content</c>) and no <c>inBucket</c>. <c>defaultContent</c> itself is never served. A live
    /// maintenance flag (the Redis <c>maint</c> key) replaces the payload's <c>maintenance</c> announcement.
    /// </summary>
    public static JsonObject Build(ActiveConfig config, Guid? player, MaintenanceState? maintenance, long serverTime)
    {
        var body = new JsonObject { ["configVersion"] = config.Id, ["serverTime"] = serverTime };
        foreach (var (k, v) in config.Payload)
        {
            if (k is "configVersion" or "serverTime" or "defaultContent") continue;
            body[k] = v?.DeepClone();
        }

        if (config.Payload["content"] is JsonObject content)
        {
            var fallback = config.Payload["defaultContent"] as JsonObject;
            int pct = content["rolloutPct"] is JsonValue p && p.TryGetValue<int>(out int i) ? i : 100;
            string version = content["version"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            bool? inBucket = player is Guid id ? RolloutBucket.InBucket(id, version, pct) : null;
            bool rollout = inBucket ?? pct >= 100;
            var served = (JsonObject)(rollout || fallback == null ? content : fallback).DeepClone();
            served.Remove("inBucket");
            if (inBucket is bool b) served["inBucket"] = b;
            body["content"] = served;
        }

        if (maintenance is { Active: true } m)
            body["maintenance"] = new JsonObject { ["active"] = true, ["messageKey"] = m.MessageKey, ["until"] = m.Until };
        else if (body["maintenance"] is null)
            body["maintenance"] = new JsonObject { ["active"] = false, ["messageKey"] = null, ["until"] = null };
        return body;
    }
}
