using System.ComponentModel.DataAnnotations;
using HeroCat.Server.Infrastructure.Idempotency;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Idempotency;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HeroCat.Server.Tests.Pipeline;

/// <summary>
/// The real api pipeline over stand-in routes (<see cref="FakeRoutesController"/>), with every seam replaced by a
/// controllable fake: clock, maintenance flag, version floors, token validator, bans, rate-limit store. The server's
/// own controllers are removed so the stand-ins never collide with feature endpoints as they land.
/// </summary>
sealed class PipelineHost : IDisposable
{
    public readonly FakeClock Clock = new(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
    public readonly InMemoryMaintenanceFlag Maintenance = new();
    public readonly MutableGateConfig Gates = new();
    public readonly FakeTokens Tokens = new();
    public readonly FakeBans Bans = new();
    public readonly FakeAdmins Admins = new();
    public readonly InMemoryRateLimitStore Limits = new();
    public IRateLimitStore? LimitStoreOverride;
    public readonly InMemoryIdempotencyStore Idempotency = new(TimeSpan.FromHours(24));
    public IIdempotencyStore? IdempotencyOverride;
    public readonly RecordingFlags Flags = new();
    public readonly IdempotencyProbe Probe = new();

    readonly WebApplicationFactory<Program> _factory;

    public PipelineHost()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            s.AddSingleton<IMaintenanceFlag>(Maintenance);
            s.AddSingleton<IClientGateConfigSource>(Gates);
            s.AddSingleton<IAccessTokenValidator>(Tokens);
            s.AddSingleton<IAccountStatusSource>(Bans);
            s.AddSingleton<IAdminAuthenticator>(Admins);
            s.AddSingleton<IRateLimitStore>(_ => LimitStoreOverride ?? Limits);
            s.AddSingleton<IIdempotencyStore>(_ => IdempotencyOverride ?? Idempotency);
            s.AddSingleton<IPlausibilityFlagSink>(Flags);
            s.AddSingleton(Probe);
            s.AddControllers().ConfigureApplicationPartManager(m =>
            {
                foreach (var part in m.ApplicationParts.OfType<AssemblyPart>().ToList()) m.ApplicationParts.Remove(part);
                m.ApplicationParts.Add(new AssemblyPart(typeof(FakeRoutesController).Assembly));
            });
        }));
    }

    public IServiceProvider Services => _factory.Services;

    public HttpClient Client() => _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    public const string ClientVersion = "1.4.2+318";
    public const string ContentVersion = "2026.11.03";
    public const string DeviceId = "0f9e8d7c-0000-4000-8000-000000000001";
    public static readonly Guid Player = Guid.Parse("6b1f2c40-0000-4000-8000-000000000001");

    /// <summary>A request with the client headers of 01 section 5.1 and a valid token for <see cref="Player"/>.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string path, Guid? player = null, string? clientVersion = ClientVersion,
                                             string? contentVersion = ContentVersion, string? deviceId = DeviceId, string? token = null)
    {
        var r = new HttpRequestMessage(method, path);
        if (clientVersion != null) r.Headers.Add(ApiHeaders.ClientVersion, clientVersion);
        if (contentVersion != null) r.Headers.Add(ApiHeaders.ContentVersion, contentVersion);
        if (deviceId != null) r.Headers.Add(ApiHeaders.DeviceId, deviceId);
        token ??= FakeTokens.For(player ?? Player, DeviceId);
        if (token.Length > 0) r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            r.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            r.Headers.Add(ApiHeaders.RequestId, Guid.NewGuid().ToString("N")); // a fresh id per mutating request, as the client makes
        }
        return r;
    }
}

sealed class FakeClock : TimeProvider
{
    DateTimeOffset _now;
    public FakeClock(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

sealed class MutableGateConfig : IClientGateConfigSource
{
    public ClientGateConfig Config { get; set; } = ClientGateConfig.None;
    public ValueTask<ClientGateConfig> GetAsync(CancellationToken ct) => ValueTask.FromResult(Config);
}

/// <summary>Tokens are <c>ok|{playerId}|{deviceId}</c>; <c>expired</c> is an expired one; anything else is invalid.</summary>
sealed class FakeTokens : IAccessTokenValidator
{
    public static string For(Guid player, string device) => $"ok|{player:D}|{device}";

    public ValueTask<AccessTokenResult> ValidateAsync(string token, CancellationToken ct)
    {
        if (token == "expired") return ValueTask.FromResult(AccessTokenResult.Expired);
        var p = token.Split('|');
        return ValueTask.FromResult(p.Length == 3 && p[0] == "ok" && Guid.TryParse(p[1], out var id)
            ? AccessTokenResult.Valid(new AccessTokenClaims(id, p[2], 18))
            : AccessTokenResult.Invalid);
    }
}

sealed class RecordingFlags : IPlausibilityFlagSink
{
    public readonly List<PlausibilityFlag> Raised = new();
    public Task RaiseAsync(PlausibilityFlag flag, CancellationToken ct)
    {
        lock (Raised) Raised.Add(flag);
        return Task.CompletedTask;
    }
}

sealed class FakeBans : IAccountStatusSource
{
    public readonly HashSet<Guid> Banned = new();
    public ValueTask<AccountBan?> GetBanAsync(Guid playerId, CancellationToken ct) =>
        ValueTask.FromResult(Banned.Contains(playerId) ? new AccountBan("https://support.example/appeal") : null);
}

sealed class FakeAdmins : IAdminAuthenticator
{
    public const string Header = "X-Test-Admin";
    public ValueTask<string?> AuthenticateAsync(HttpContext ctx, CancellationToken ct) =>
        ValueTask.FromResult<string?>(ctx.Request.Headers[Header].FirstOrDefault());
}

/// <summary>Stand-ins on the spec's routes with the spec's auth and limits; their bodies are not the features'.</summary>
[ApiController]
public sealed class FakeRoutesController : ControllerBase
{
    [HttpGet("v1/time"), Auth(AuthPolicy.None), RateLimit(60)]
    public object Time() => new { serverTime = 1 };

    [HttpGet("v1/config"), Auth(AuthPolicy.None), RateLimit(30)]
    public object Config() => new { player = HttpContext.PlayerId() };

    [HttpPost("v1/summon/pull"), Auth(AuthPolicy.JwtDevice), RateLimit(20), Idempotent]
    public object Pull() => new { ok = true };

    [HttpPost("v1/dungeon/seed"), Auth(AuthPolicy.Jwt), RateLimit(20), RateLimit(60, RateLimitAttribute.Day), Idempotent]
    public object Seed() => new { ok = true };

    [HttpGet("v1/save/meta"), Auth(AuthPolicy.Jwt), RateLimit(60)]
    public object SaveMeta() => new { player = HttpContext.PlayerId() };

    [HttpPut("v1/save"), Auth(AuthPolicy.JwtDevice), RateLimit(6), RateLimit(400, RateLimitAttribute.Day), Idempotent]
    public object PutSave() => new { ok = true };

    [HttpPost("v1/auth/guest"), Auth(AuthPolicy.None), RateLimit(5), RateLimit(20, RateLimitAttribute.Day), Idempotent(IdempotencyKeyKind.Device)]
    [ProducesResponseType(typeof(GuestResponse), StatusCodes.Status201Created)]
    public IActionResult Guest(GuestRequest request) =>
        StatusCode(StatusCodes.Status201Created, new GuestResponse(Guid.NewGuid(), "a", "r", 1, new { }, 1));

    [HttpGet("v1/_test/undeclared")]
    public object Undeclared() => new { ok = true };

    [HttpGet("v1/_test/throw"), Auth(AuthPolicy.None)]
    public object Throw() => throw new InvalidOperationException("boom");

    [HttpGet("v1/_test/api-error"), Auth(AuthPolicy.None)]
    public object ApiError() => throw new ApiException(402, "INSUFFICIENT_FUNDS", "Not enough diamonds.", new { need = 1000, have = 640 });

    [HttpGet("v1/_test/bare-404"), Auth(AuthPolicy.None)]
    public IActionResult Bare404() => NotFound();

    [HttpGet("v1/admin/ping")]
    public object AdminPing() => new { actor = User.Identity?.Name ?? User.FindFirst("sub")?.Value };
}

public sealed class GuestRequest
{
    [Required] public string? DeviceId { get; set; }
    public string? DeviceKey { get; set; }
    public string? Model { get; set; }
    public string? Os { get; set; }
    public string? ClientVersion { get; set; }
    public string? Region { get; set; }
}

public sealed record GuestResponse(Guid PlayerId, string AccessToken, string RefreshToken, long AccessExpiresAt, object Profile, long ServerTime);
