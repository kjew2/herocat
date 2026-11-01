namespace HeroCat.Server.Features.Inbox;

/// <summary>
/// The Inbox feature folder (specs/08 section 1 service inventory; section 3.2): inbox list, claim, claim-all, expiry.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class InboxFeature
{
    // TODO(#201): the Inbox endpoints and their services.
    public static IServiceCollection AddInboxFeature(this IServiceCollection services) => services;
}
