using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Auth;

/// <summary>An issued access token and its expiry (unix seconds).</summary>
public sealed record IssuedAccessToken(string Token, long ExpiresAt);

/// <summary>
/// Access JWTs (specs/08 section 2.1): RS256, header <c>kid</c>, claims <c>sub</c> (playerId), <c>dev</c> (deviceId),
/// <c>age</c> (age gate), <c>iat</c>, <c>exp</c> (<c>ACCESS_TOKEN_TTL_S</c> after <c>iat</c>) and <c>jti</c>. Signed with
/// the ring's first key; verified with any active key, so a rotation keeps tokens signed by the outgoing key valid.
/// It is the api's <see cref="IAccessTokenValidator"/>.
/// </summary>
public sealed class AccessTokenService : IAccessTokenValidator
{
    public const string Algorithm = "RS256";

    readonly Lazy<ServerKeyRing> _keys;
    readonly TimeProvider _clock;
    readonly int _ttlSeconds;

    public AccessTokenService(Lazy<ServerKeyRing> keys, TimeProvider clock, IOptions<ServerOptions> options)
    {
        _keys = keys;
        _clock = clock;
        int ttl = options.Value.Limits.AccessTokenTtlSeconds;
        _ttlSeconds = ttl > 0 ? ttl : 3600;
    }

    public IssuedAccessToken Issue(Guid playerId, string deviceId, int ageGate)
    {
        var key = _keys.Value.Signer;
        long iat = _clock.GetUtcNow().ToUnixTimeSeconds();
        long exp = iat + _ttlSeconds;

        string header = Json(w =>
        {
            w.WriteString("alg", Algorithm);
            w.WriteString("kid", key.Kid);
            w.WriteString("typ", "JWT");
        });
        string payload = Json(w =>
        {
            w.WriteString(Caller.SubClaim, playerId.ToString("D"));
            w.WriteString(Caller.DeviceClaim, deviceId);
            w.WriteNumber(Caller.AgeClaim, ageGate);
            w.WriteNumber("iat", iat);
            w.WriteNumber("exp", exp);
            w.WriteString("jti", Guid.NewGuid().ToString("N"));
        });
        string signingInput = B64(header) + "." + B64(payload);
        byte[] sig = key.Rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new IssuedAccessToken(signingInput + "." + WebEncoders.Base64UrlEncode(sig), exp);
    }

    public ValueTask<AccessTokenResult> ValidateAsync(string token, CancellationToken ct) => ValueTask.FromResult(Validate(token));

    public AccessTokenResult Validate(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return AccessTokenResult.Invalid;
            using var header = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != Algorithm) return AccessTokenResult.Invalid;
            if (!header.RootElement.TryGetProperty("kid", out var kid) || kid.ValueKind != JsonValueKind.String) return AccessTokenResult.Invalid;
            var key = _keys.Value.Find(kid.GetString()!);
            if (key == null) return AccessTokenResult.Invalid;
            byte[] sig = WebEncoders.Base64UrlDecode(parts[2]);
            if (!key.Rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return AccessTokenResult.Invalid;

            using var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            var p = payload.RootElement;
            if (!p.TryGetProperty(Caller.SubClaim, out var sub) || !Guid.TryParse(sub.GetString(), out var player)) return AccessTokenResult.Invalid;
            if (!p.TryGetProperty(Caller.DeviceClaim, out var dev) || dev.ValueKind != JsonValueKind.String) return AccessTokenResult.Invalid;
            if (!p.TryGetProperty(Caller.AgeClaim, out var age) || !age.TryGetInt32(out int ageGate)) return AccessTokenResult.Invalid;
            if (!p.TryGetProperty("exp", out var expEl) || !expEl.TryGetInt64(out long exp)) return AccessTokenResult.Invalid;
            if (_clock.GetUtcNow().ToUnixTimeSeconds() >= exp) return AccessTokenResult.Expired;
            return AccessTokenResult.Valid(new AccessTokenClaims(player, dev.GetString()!, ageGate));
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or InvalidOperationException)
        {
            return AccessTokenResult.Invalid;
        }
    }

    static string B64(string json) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(json));

    static string Json(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>
/// Refresh tokens (specs/08 section 2.1): 256 random bits, written <c>r1.</c> + base64url (the <c>r1</c> is the format
/// version); only <c>sha256(token)</c> is stored (<c>refresh_tokens.token_hash</c>).
/// </summary>
public static class RefreshTokens
{
    public const string Prefix = "r1.";

    public static string New() => Prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static bool LooksValid(string? token) => token != null && token.StartsWith(Prefix, StringComparison.Ordinal) && token.Length == Prefix.Length + 43;
}

/// <summary>
/// The support code shown in <c>SCR_SETTINGS</c> (specs/08 section 2.1): <c>base32(hmac(playerId, serverKey))[:12]</c>, with
/// HMAC-SHA256 over the lowercase 36-character playerId and RFC 4648 base32 (A-Z, 2-7).
/// </summary>
public static class SupportCodes
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public const int Length = 12;

    public static string For(Guid playerId, byte[] serverKey)
    {
        byte[] mac = HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes(playerId.ToString("D")));
        return Base32(mac)[..Length];
    }

    internal static string Base32(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
