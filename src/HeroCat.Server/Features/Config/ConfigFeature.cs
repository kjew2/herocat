namespace HeroCat.Server.Features.Config;

/// <summary>
/// The Config feature folder (specs/08 section 1 service inventory; section 3.4 and 6): remote config, kill switches, content manifest pointer, min versions, maintenance.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class ConfigFeature
{
    // TODO(#204): the Config endpoints and their services.
    public static IServiceCollection AddConfigFeature(this IServiceCollection services) => services;
}
