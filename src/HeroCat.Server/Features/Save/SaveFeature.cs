using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HeroCat.Server.Features.Save;

/// <summary>
/// The Save feature folder (specs/08 section 1 service inventory; section 3.1 and 7): save upload/download, metadata, history, plausibility hook, crediting client-earned currency.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class SaveFeature
{
    // TODO(#206, #207): the plausibility checks and the command-log replay behind ISavePlausibility.
    public static IServiceCollection AddSaveFeature(this IServiceCollection services)
    {
        services.AddSingleton<SaveStore>();
        services.TryAddSingleton<ISavePlausibility, NoSavePlausibility>();
        services.TryAddSingleton<IResimSampler, ResimSampler>();
        return services;
    }
}
