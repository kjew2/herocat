using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Auth;

/// <summary>
/// The Auth feature folder (specs/08 section 1 service inventory; section 2.1-2.2): guest creation, RS256 JWT issue/refresh, platform linking, logout, recovery, device registration, server time.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class AuthFeature
{
    // TODO(#196): platform linking, link-login and merge.
    public static IServiceCollection AddAuthFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp => new AccessTokenService(sp.GetRequiredService<Lazy<ServerKeyRing>>(), sp.GetRequiredService<TimeProvider>(),
                                                           sp.GetRequiredService<IOptions<ServerOptions>>()));
        // The pipeline's token check (registered before AddRequestPipeline, whose default only fills the gap).
        services.AddSingleton<IAccessTokenValidator>(sp => sp.GetRequiredService<AccessTokenService>());
        services.AddSingleton<AuthService>();
        services.AddSingleton<AuthStore>();
        return services;
    }
}
