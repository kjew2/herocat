using System.Security.Claims;

namespace HeroCat.Server.Pipeline;

/// <summary>The auth column of specs/08's endpoint lines (section 1.1).</summary>
public enum AuthPolicy
{
    /// <summary><c>none</c>: anyone. A valid access token, if sent, still identifies the player (<c>/config</c>'s rollout bucket).</summary>
    None,
    /// <summary><c>jwt</c>: any valid access token.</summary>
    Jwt,
    /// <summary><c>jwt+device</c>: a valid access token whose <c>dev</c> claim equals <c>X-Device-Id</c>.</summary>
    JwtDevice,
    /// <summary><c>admin</c>: internal network + SSO (section 8.5).</summary>
    Admin,
}

public static class AuthPolicies
{
    /// <summary>The spec's spelling (<c>none</c>, <c>jwt</c>, <c>jwt+device</c>, <c>admin</c>), also the OpenAPI <c>x-herocat-auth</c> value.</summary>
    public static string Name(AuthPolicy p) => p switch
    {
        AuthPolicy.None => "none",
        AuthPolicy.Jwt => "jwt",
        AuthPolicy.JwtDevice => "jwt+device",
        AuthPolicy.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>
    /// The endpoint's declared policy. A <c>/v1</c> endpoint that declares none is <c>jwt</c> (fail closed); one outside
    /// <c>/v1</c> (<c>/health</c>, the OpenAPI document) is <c>none</c>.
    /// </summary>
    public static AuthPolicy Of(Endpoint? endpoint, PathString path) =>
        Resolve(endpoint?.Metadata.GetMetadata<AuthAttribute>(), path);

    /// <summary>The declared policy, else the default for the path (<c>admin</c> under <c>/v1/admin</c>).</summary>
    public static AuthPolicy Resolve(AuthAttribute? declared, PathString path) =>
        declared?.Policy
        ?? (RouteRules.IsAdminRoute(path) ? AuthPolicy.Admin : RouteRules.IsClientRoute(path) ? AuthPolicy.Jwt : AuthPolicy.None);
}

/// <summary>Declares an endpoint's auth policy (the spec's auth column), on the action or its controller.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AuthAttribute : Attribute
{
    public AuthAttribute(AuthPolicy policy) => Policy = policy;
    public AuthPolicy Policy { get; }
}

/// <summary>The claims of a verified access token (specs/08 section 2.1: <c>sub</c>, <c>dev</c>, <c>age</c>).</summary>
public sealed record AccessTokenClaims(Guid PlayerId, string DeviceId, int AgeGate);

public enum AccessTokenStatus { Valid, Expired, Invalid }

public sealed record AccessTokenResult(AccessTokenStatus Status, AccessTokenClaims? Claims)
{
    public static readonly AccessTokenResult Invalid = new(AccessTokenStatus.Invalid, null);
    public static readonly AccessTokenResult Expired = new(AccessTokenStatus.Expired, null);
    public static AccessTokenResult Valid(AccessTokenClaims c) => new(AccessTokenStatus.Valid, c);
}

/// <summary>Verifies an access JWT (RS256, specs/08 section 2.1). The Auth feature owns the keys and the implementation.</summary>
public interface IAccessTokenValidator
{
    ValueTask<AccessTokenResult> ValidateAsync(string token, CancellationToken ct);
}

/// <summary>Accepts no token: the api's default until the Auth feature registers the RS256 validator.</summary>
public sealed class RejectingAccessTokenValidator : IAccessTokenValidator
{
    // TODO(#195): the Auth feature registers the RS256 IAccessTokenValidator with its signing keys.
    public ValueTask<AccessTokenResult> ValidateAsync(string token, CancellationToken ct) => ValueTask.FromResult(AccessTokenResult.Invalid);
}

/// <summary>Authenticates an <c>admin</c> request (internal network + SSO, specs/08 section 8.5); returns the actor or null.</summary>
public interface IAdminAuthenticator
{
    ValueTask<string?> AuthenticateAsync(HttpContext ctx, CancellationToken ct);
}

/// <summary>Admits nobody: the api's default until the Admin feature wires SSO.</summary>
public sealed class DenyingAdminAuthenticator : IAdminAuthenticator
{
    // TODO(#208): the Admin feature registers the SSO-backed IAdminAuthenticator.
    public ValueTask<string?> AuthenticateAsync(HttpContext ctx, CancellationToken ct) => ValueTask.FromResult<string?>(null);
}

/// <summary>A ban on an account (specs/08 section 7.4: <c>accounts.status = banned</c>).</summary>
public sealed record AccountBan(string? AppealUrl);

/// <summary>Answers whether a player is banned, for the <c>403 ACCOUNT_BANNED</c> check on authenticated routes.</summary>
public interface IAccountStatusSource
{
    ValueTask<AccountBan?> GetBanAsync(Guid playerId, CancellationToken ct);
}

/// <summary>Bans nobody: the api's default until <c>accounts.status</c> is read.</summary>
public sealed class NoBansAccountStatusSource : IAccountStatusSource
{
    // TODO(#206): read accounts.status (set by the action matrix) through a short-lived cache.
    public ValueTask<AccountBan?> GetBanAsync(Guid playerId, CancellationToken ct) => ValueTask.FromResult<AccountBan?>(null);
}

/// <summary>The authenticated caller, as handlers read it.</summary>
public static class Caller
{
    public const string AuthenticationType = "HeroCat.AccessToken";
    public const string AdminAuthenticationType = "HeroCat.Admin";
    public const string SubClaim = "sub";
    public const string DeviceClaim = "dev";
    public const string AgeClaim = "age";

    public static ClaimsPrincipal Principal(AccessTokenClaims c) => new(new ClaimsIdentity(new[]
    {
        new Claim(SubClaim, c.PlayerId.ToString("D")),
        new Claim(DeviceClaim, c.DeviceId),
        new Claim(AgeClaim, c.AgeGate.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    }, AuthenticationType, SubClaim, null));

    /// <summary>The player of a verified access token, else null.</summary>
    public static Guid? PlayerId(this HttpContext ctx) =>
        ctx.User.Identity?.AuthenticationType == AuthenticationType && Guid.TryParse(ctx.User.FindFirstValue(SubClaim), out var id) ? id : null;

    public static string? DeviceId(this HttpContext ctx) =>
        ctx.User.Identity?.AuthenticationType == AuthenticationType ? ctx.User.FindFirstValue(DeviceClaim) : null;
}

/// <summary>
/// Enforces the endpoint's <see cref="AuthPolicy"/>: a missing or invalid token is <c>401 UNAUTHENTICATED</c>, an
/// expired one <c>401 TOKEN_EXPIRED</c>; a <c>jwt+device</c> token whose <c>dev</c> differs from <c>X-Device-Id</c> is
/// <c>403 FORBIDDEN</c>; a banned player on a <c>jwt</c> or <c>jwt+device</c> route is <c>403 ACCOUNT_BANNED</c> with the
/// appeal url (section 7.4); an <c>admin</c> request the authenticator rejects is <c>403 FORBIDDEN</c>.
/// </summary>
public static class AuthGate
{
    public static async Task<ApiException?> CheckAsync(HttpContext ctx, AuthPolicy policy, IAccessTokenValidator tokens,
                                                       IAdminAuthenticator admins, IAccountStatusSource accounts)
    {
        var ct = ctx.RequestAborted;
        if (policy == AuthPolicy.Admin)
        {
            string? actor = await admins.AuthenticateAsync(ctx, ct).ConfigureAwait(false);
            if (actor == null) return ApiException.Of(ErrorCodes.FORBIDDEN);
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(Caller.SubClaim, actor) }, Caller.AdminAuthenticationType));
            return null;
        }

        string? token = BearerToken(ctx);
        var result = token == null ? AccessTokenResult.Invalid : await tokens.ValidateAsync(token, ct).ConfigureAwait(false);
        if (result.Status == AccessTokenStatus.Valid && result.Claims != null)
            ctx.User = Caller.Principal(result.Claims);

        if (policy == AuthPolicy.None) return null;
        if (result.Status == AccessTokenStatus.Expired) return ApiException.Of(ErrorCodes.TOKEN_EXPIRED);
        if (result.Claims == null) return ApiException.Of(ErrorCodes.UNAUTHENTICATED);

        if (policy == AuthPolicy.JwtDevice)
        {
            string? device = ctx.Request.Headers[ApiHeaders.DeviceId];
            if (string.IsNullOrEmpty(device) || !string.Equals(device, result.Claims.DeviceId, StringComparison.OrdinalIgnoreCase))
                return ApiException.Of(ErrorCodes.FORBIDDEN, "The token's device does not match X-Device-Id.", new { reason = "DEVICE_MISMATCH" });
        }

        var ban = await accounts.GetBanAsync(result.Claims.PlayerId, ct).ConfigureAwait(false);
        if (ban != null) return ApiException.Of(ErrorCodes.ACCOUNT_BANNED, details: new { appealUrl = ban.AppealUrl });
        return null;
    }

    static string? BearerToken(HttpContext ctx)
    {
        string? h = ctx.Request.Headers.Authorization;
        const string prefix = "Bearer ";
        if (h == null || !h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string t = h[prefix.Length..].Trim();
        return t.Length == 0 ? null : t;
    }
}
