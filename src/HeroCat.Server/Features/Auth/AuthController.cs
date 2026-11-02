using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Auth;

/// <summary>The Auth service's shared steps: profile building, device-key handling, token issue.</summary>
public sealed class AuthService
{
    readonly AccessTokenService _tokens;
    readonly Lazy<ServerKeyRing> _keys;
    readonly SecretProtector _protector;
    readonly TimeProvider _clock;
    readonly ServerLimits _limits;

    public AuthService(AccessTokenService tokens, Lazy<ServerKeyRing> keys, SecretProtector protector, TimeProvider clock, IOptions<ServerOptions> options)
    {
        _tokens = tokens;
        _keys = keys;
        _protector = protector;
        _clock = clock;
        _limits = options.Value.Limits;
    }

    public DateTimeOffset Now => _clock.GetUtcNow();
    public DateTimeOffset RefreshExpiry(DateTimeOffset now) => now.AddDays(_limits.RefreshTokenTtlDays > 0 ? _limits.RefreshTokenTtlDays : 60);
    public TimeSpan RefreshReuseGrace => TimeSpan.FromSeconds(_limits.RefreshReuseGraceSeconds);
    public IssuedAccessToken Issue(Guid playerId, Guid deviceId, int ageGate) => _tokens.Issue(playerId, deviceId.ToString("D"), ageGate);

    public ProfileDto Profile(AccountRow a) => new(a.PlayerId, a.DisplayName, a.AvatarId, a.CreatedAt.ToUnixTimeSeconds(), a.Links.ToArray(),
        a.Links.Count == 0, a.Region, a.AgeGate, (a.LastLoginAt ?? a.CreatedAt).ToUnixTimeSeconds(), SupportCodes.For(a.PlayerId, _keys.Value.RecoveryKey));

    /// <summary>A device HMAC key at rest: AES-GCM under the data key, bound to its device id (specs/08 section 9.3).</summary>
    public byte[] ProtectDeviceKey(Guid deviceId, byte[] key) => _protector.Protect(key, DeviceAad(deviceId));
    public byte[] UnprotectDeviceKey(Guid deviceId, byte[] enc) => _protector.Unprotect(enc, DeviceAad(deviceId));
    static byte[] DeviceAad(Guid deviceId) => Encoding.UTF8.GetBytes("device/" + deviceId.ToString("D"));

    /// <summary>02 section 4: <c>"Cat#"</c> + the last 4 characters of the player id.</summary>
    public static string DefaultDisplayName(Guid playerId) => "Cat#" + playerId.ToString("N")[^4..].ToUpperInvariant();

    public static Guid ParseDeviceId(string? s, string field) =>
        Guid.TryParse(s, out var g) ? g : throw ApiException.Of(ErrorCodes.VALIDATION, $"{field} must be a GUID.", new { field });

    public static byte[] ParseDeviceKey(string? s)
    {
        try
        {
            byte[] k = Convert.FromBase64String(s ?? "");
            if (k.Length == 32) return k;
        }
        catch (FormatException) { }
        throw ApiException.Of(ErrorCodes.VALIDATION, "deviceKey must be base64 of 32 bytes.", new { field = "deviceKey" });
    }
}

/// <summary>
/// <c>POST /auth/guest</c>, <c>/auth/refresh</c>, <c>/auth/logout</c> (specs/08 section 2.2, issue #195).
/// </summary>
[ApiController]
public sealed partial class AuthController : ControllerBase
{
    readonly AuthService _auth;
    readonly AuthStore _store;
    readonly ILogger<AuthController> _log;

    public AuthController(AuthService auth, AuthStore store, ILogger<AuthController> log)
    {
        _auth = auth;
        _store = store;
        _log = log;
    }

    [GeneratedRegex("^[A-Za-z]{2}$")]
    private static partial Regex RegionFormat();

    /// <summary>
    /// Creates a guest account for a device, registers its HMAC key and returns tokens, the profile and server time. A
    /// device that already has an account is <c>409 DEVICE_ALREADY_BOUND</c> with <c>details.playerId</c>. The client
    /// may already hold a local save with this device id and <c>PlayerId = Guid.Empty</c>; it adopts it by rewriting the
    /// save root with the returned <c>playerId</c> and uploading with <c>If-Match: *</c> (open question 9).
    /// </summary>
    [HttpPost("v1/auth/guest"), Auth(AuthPolicy.None), RateLimit(5), RateLimit(20, RateLimitAttribute.Day)]
    [Idempotent(IdempotencyKeyKind.Device, ProtectStoredBody = true)]
    [ProducesResponseType(typeof(GuestResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Guest(GuestRequest request, CancellationToken ct)
    {
        Guid deviceId = AuthService.ParseDeviceId(request.DeviceId, "deviceId");
        if (!Guid.TryParse(Request.Headers[ApiHeaders.DeviceId], out var headerDevice) || headerDevice != deviceId)
            throw ApiException.Of(ErrorCodes.VALIDATION, "deviceId must equal X-Device-Id.", new { field = "deviceId" });
        byte[] deviceKey = AuthService.ParseDeviceKey(request.DeviceKey);
        string region = string.IsNullOrEmpty(request.Region) ? "ZZ"
            : RegionFormat().IsMatch(request.Region) ? request.Region.ToUpperInvariant()
            : throw ApiException.Of(ErrorCodes.VALIDATION, "region must be ISO 3166-1 alpha-2.", new { field = "region" });

        var now = _auth.Now;
        var playerId = Guid.NewGuid();
        string refresh = RefreshTokens.New();
        var device = new NewDevice(deviceId, _auth.ProtectDeviceKey(deviceId, deviceKey), request.Model, request.Os, request.ClientVersion);
        var result = await _store.CreateGuestAsync(playerId, AuthService.DefaultDisplayName(playerId), region, device, RefreshTokens.Hash(refresh),
                                                   now, _auth.RefreshExpiry(now), ct);
        if (result.Outcome == GuestOutcome.AlreadyBound)
            throw new ApiException(409, AuthErrors.DEVICE_ALREADY_BOUND, "This device already has an account; refresh instead.",
                                   new { playerId = result.PlayerId });

        var access = _auth.Issue(playerId, deviceId, result.Account!.AgeGate);
        return StatusCode(StatusCodes.Status201Created,
            new GuestResponse(playerId, access.Token, refresh, access.ExpiresAt, _auth.Profile(result.Account), now.ToUnixTimeSeconds()));
    }

    /// <summary>
    /// Rotates a refresh token: the presented one is revoked, a new one in the same family is returned with a fresh
    /// access token. Reusing a rotated-out token revokes the family (<c>401 TOKEN_REUSED</c>, logged as possible theft), except a same-device
    /// retry within <c>REFRESH_REUSE_GRACE_S</c> of the rotation, which rotates the family's live token (issue #273).
    /// </summary>
    [HttpPost("v1/auth/refresh"), Auth(AuthPolicy.None), RateLimit(30)]
    [ProducesResponseType(typeof(RefreshResponse), StatusCodes.Status200OK)]
    public async Task<RefreshResponse> Refresh(RefreshRequest request, CancellationToken ct)
    {
        Guid deviceId = AuthService.ParseDeviceId(request.DeviceId, "deviceId");
        if (!RefreshTokens.LooksValid(request.RefreshToken)) throw ApiException.Of(ErrorCodes.UNAUTHENTICATED, "Unknown refresh token.");

        var now = _auth.Now;
        string next = RefreshTokens.New();
        var r = await _store.RotateRefreshAsync(RefreshTokens.Hash(request.RefreshToken!), deviceId, RefreshTokens.Hash(next), now, _auth.RefreshExpiry(now),
                                                 _auth.RefreshReuseGrace, ct);
        switch (r.Outcome)
        {
            case RefreshOutcome.Rotated:
                var access = _auth.Issue(r.Account!.PlayerId, r.DeviceId, r.Account.AgeGate);
                return new RefreshResponse(access.Token, next, access.ExpiresAt, now.ToUnixTimeSeconds());
            case RefreshOutcome.Unknown:
                throw ApiException.Of(ErrorCodes.UNAUTHENTICATED, "Unknown refresh token.");
            case RefreshOutcome.Expired:
                throw ApiException.Of(ErrorCodes.TOKEN_EXPIRED, "The refresh token has expired.");
            case RefreshOutcome.Revoked:
                throw new ApiException(401, AuthErrors.TOKEN_REVOKED, "The refresh token was revoked.");
            case RefreshOutcome.Reused:
                _log.LogWarning("refresh token reuse on device {DeviceId}; family revoked (possible theft)", r.DeviceId);
                throw new ApiException(401, AuthErrors.TOKEN_REUSED, "The refresh token was already used; sign in again.");
            case RefreshOutcome.DeviceMismatch:
                throw new ApiException(403, AuthErrors.DEVICE_MISMATCH, "The refresh token belongs to another device.");
            default:
                throw new InvalidOperationException(r.Outcome.ToString());
        }
    }

    /// <summary>Revokes the family of the given refresh token (this device's session only). 204 also for an unknown token.</summary>
    [HttpPost("v1/auth/logout"), Auth(AuthPolicy.Jwt), RateLimit(10), Idempotent]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        if (RefreshTokens.LooksValid(request.RefreshToken))
            await _store.RevokeFamilyOfAsync(HttpContext.PlayerId()!.Value, RefreshTokens.Hash(request.RefreshToken!), _auth.Now, ct);
        return NoContent();
    }
}

/// <summary><c>POST /devices</c> (specs/08 section 2.2): registers the caller's device or rotates its key.</summary>
[ApiController]
public sealed class DevicesController : ControllerBase
{
    /// <summary>The window after <c>/auth/guest</c> in which a device may rotate its key without a verified save.</summary>
    public static readonly TimeSpan RotationWindow = TimeSpan.FromHours(24);

    readonly AuthService _auth;
    readonly AuthStore _store;

    public DevicesController(AuthService auth, AuthStore store)
    {
        _auth = auth;
        _store = store;
    }

    /// <summary>
    /// The body's <c>deviceId</c> must be the token's <c>dev</c> (else <c>403 FORBIDDEN</c>, <c>DEVICE_MISMATCH</c>), so a
    /// player cannot squat another install's id. A device bound to another player is <c>409 DEVICE_ALREADY_BOUND</c>. A new
    /// key is accepted within 24 h of the device's first registration, or when the player's save of record came from this
    /// device with a valid HMAC under the old key; otherwise <c>403 FORBIDDEN</c> with <c>KEY_ROTATION_NOT_ALLOWED</c>.
    /// </summary>
    [HttpPost("v1/devices"), Auth(AuthPolicy.Jwt), RateLimit(5), Idempotent]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Register(DeviceRequest request, CancellationToken ct)
    {
        Guid player = HttpContext.PlayerId()!.Value;
        Guid deviceId = AuthService.ParseDeviceId(request.DeviceId, "deviceId");
        if (!Guid.TryParse(HttpContext.DeviceId(), out var tokenDevice) || tokenDevice != deviceId)
            throw ApiException.Of(ErrorCodes.FORBIDDEN, "deviceId must be the token's device.", new { reason = AuthErrors.DEVICE_MISMATCH });
        byte[] key = AuthService.ParseDeviceKey(request.DeviceKey);
        var now = _auth.Now;

        byte[]? newKeyEnc = null;
        var stored = await _store.GetDeviceAsync(deviceId, ct);
        if (stored != null)
        {
            if (stored.PlayerId != player) throw new ApiException(409, AuthErrors.DEVICE_ALREADY_BOUND, "This device belongs to another account.");
            byte[] current = _auth.UnprotectDeviceKey(deviceId, stored.HmacKeyEnc);
            if (!CryptographicOperations.FixedTimeEquals(current, key))
            {
                bool allowed = now - stored.FirstSeenAt <= RotationWindow || await _store.SaveVerifiedFromAsync(player, deviceId, ct);
                if (!allowed)
                    throw ApiException.Of(ErrorCodes.FORBIDDEN, "Key rotation needs a save verified under the old key.",
                                          new { reason = AuthErrors.KEY_ROTATION_NOT_ALLOWED });
                newKeyEnc = _auth.ProtectDeviceKey(deviceId, key);
            }
        }

        var device = new NewDevice(deviceId, newKeyEnc ?? _auth.ProtectDeviceKey(deviceId, key), request.Model, request.Os, request.ClientVersion);
        if (!await _store.UpsertDeviceAsync(player, device, newKeyEnc, request.PushToken, now, ct))
            throw new ApiException(409, AuthErrors.DEVICE_ALREADY_BOUND, "This device belongs to another account.");
        return NoContent();
    }
}

/// <summary><c>GET /time</c> (specs/08 section 2.2): the server's unix seconds, also on every response as <c>X-Server-Time</c>.</summary>
[ApiController]
public sealed class TimeController : ControllerBase
{
    readonly TimeProvider _clock;
    public TimeController(TimeProvider clock) => _clock = clock;

    [HttpGet("v1/time"), Auth(AuthPolicy.None), RateLimit(60)]
    [ProducesResponseType(typeof(TimeResponse), StatusCodes.Status200OK)]
    public TimeResponse Time() => new(_clock.GetUtcNow().ToUnixTimeSeconds());
}
