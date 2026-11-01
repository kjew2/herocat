namespace HeroCat.Server.Features.Save;

/// <summary>
/// The Save feature folder (specs/08 section 1 service inventory; section 3.1 and 7): save upload/download, metadata, history, plausibility hook, crediting client-earned currency.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class SaveFeature
{
    // TODO(#198, #206, #207): the Save endpoints and their services.
    public static IServiceCollection AddSaveFeature(this IServiceCollection services) => services;
}
