namespace HeroCat.Server.Features.Admin;

/// <summary>
/// The Admin feature folder (specs/08 section 1 service inventory; section 8): mail blasts, compensation, moderation actions, config publishing, support lookups.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class AdminFeature
{
    // TODO(#208): the Admin endpoints and their services.
    public static IServiceCollection AddAdminFeature(this IServiceCollection services) => services;
}
