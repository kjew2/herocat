using System.ComponentModel.DataAnnotations;

namespace HeroCat.Server.Features.Auth;

/// <summary><c>POST /auth/guest</c> body (specs/08 section 2.2).</summary>
public sealed class GuestRequest
{
    /// <summary><c>GameState.DeviceId</c>, a GUID; must equal <c>X-Device-Id</c>.</summary>
    [Required] public string? DeviceId { get; set; }
    /// <summary>The device's save HMAC key (02 section 10), base64 of 32 bytes.</summary>
    [Required] public string? DeviceKey { get; set; }
    [StringLength(128)] public string? Model { get; set; }
    [StringLength(128)] public string? Os { get; set; }
    [StringLength(64)] public string? ClientVersion { get; set; }
    /// <summary>ISO 3166-1 alpha-2; <c>ZZ</c> (unknown) when absent.</summary>
    [StringLength(2)] public string? Region { get; set; }
}

/// <summary><c>POST /auth/guest</c> 201.</summary>
public sealed record GuestResponse(Guid PlayerId, string AccessToken, string RefreshToken, long AccessExpiresAt, ProfileDto Profile, long ServerTime);

/// <summary><c>POST /auth/refresh</c> body.</summary>
public sealed class RefreshRequest
{
    [Required] public string? RefreshToken { get; set; }
    [Required] public string? DeviceId { get; set; }
}

/// <summary><c>POST /auth/refresh</c> 200.</summary>
public sealed record RefreshResponse(string AccessToken, string RefreshToken, long AccessExpiresAt, long ServerTime);

/// <summary><c>POST /auth/logout</c> body.</summary>
public sealed class LogoutRequest
{
    [Required] public string? RefreshToken { get; set; }
}

/// <summary><c>POST /devices</c> body.</summary>
public sealed class DeviceRequest
{
    [Required] public string? DeviceId { get; set; }
    [Required] public string? DeviceKey { get; set; }
    [StringLength(128)] public string? Model { get; set; }
    [StringLength(128)] public string? Os { get; set; }
    [StringLength(64)] public string? ClientVersion { get; set; }
    [StringLength(4096)] public string? PushToken { get; set; }
}

/// <summary><c>GET /time</c> 200.</summary>
public sealed record TimeResponse(long ServerTime);

/// <summary>
/// The account <c>Profile</c> of specs/02 section 4 on the wire, plus the support code (08 section 2.1) the server
/// computes and never stores. Owned by Auth until the Profile feature (#197) serves it from <c>GET /profile</c> too.
/// </summary>
public sealed record ProfileDto(Guid PlayerId, string DisplayName, string AvatarId, long CreatedServerTime, LinkedPlatformDto[] Links,
                                bool IsGuest, string Region, int AgeGate, long LastLoginServerTime, string SupportCode);

public sealed record LinkedPlatformDto(string Provider, string ProviderUserIdHash, long LinkedServerTime);

/// <summary>Auth-specific error codes (specs/08 section 2.2).</summary>
public static class AuthErrors
{
    public const string DEVICE_ALREADY_BOUND = "DEVICE_ALREADY_BOUND";
    public const string TOKEN_REVOKED = "TOKEN_REVOKED";
    public const string TOKEN_REUSED = "TOKEN_REUSED";
    public const string DEVICE_MISMATCH = "DEVICE_MISMATCH";

    /// <summary><c>details.reason</c> of the <c>403 FORBIDDEN</c> for a <c>/devices</c> key rotation outside the 08 rule.</summary>
    public const string KEY_ROTATION_NOT_ALLOWED = "KEY_ROTATION_NOT_ALLOWED";
}
