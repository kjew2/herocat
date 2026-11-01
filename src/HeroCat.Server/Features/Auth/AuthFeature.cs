namespace HeroCat.Server.Features.Auth;

/// <summary>
/// The Auth feature folder (specs/08 section 1 service inventory; section 2.1-2.2): guest creation, RS256 JWT issue/refresh, platform linking, logout, recovery, device registration, server time.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class AuthFeature
{
    // TODO(#195, #196): the Auth endpoints and their services.
    public static IServiceCollection AddAuthFeature(this IServiceCollection services) => services;
}
