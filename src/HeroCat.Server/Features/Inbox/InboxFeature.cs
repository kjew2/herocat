namespace HeroCat.Server.Features.Inbox;

/// <summary>
/// The Inbox feature folder (specs/08 section 1 service inventory; section 3.2): inbox list, claim, claim-all, expiry.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// The nightly expiry purge (<see cref="InboxPurgeService"/>) runs in the worker role.
/// </summary>
public static class InboxFeature
{
    public static IServiceCollection AddInboxFeature(this IServiceCollection services) => services.AddSingleton<InboxStore>();
}
