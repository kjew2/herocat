namespace HeroCat.Server.Features.Profile;

/// <summary>
/// The Profile feature folder (specs/08 section 1 service inventory; section 2.3): display name, avatar, age gate, account delete/export.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class ProfileFeature
{
    // TODO(#197): the Profile endpoints and their services.
    public static IServiceCollection AddProfileFeature(this IServiceCollection services) => services;
}
