namespace HeroCat.Server.Features.Iap;

/// <summary>
/// The Iap feature folder (specs/08 section 1 service inventory; section 5): catalog, receipt validation, restore, store webhooks, refunds.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class IapFeature
{
    // TODO(#202, #203): the Iap endpoints and their services.
    public static IServiceCollection AddIapFeature(this IServiceCollection services) => services;
}
