using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HeroCat.Server.Features.Dungeon;

/// <summary>
/// The Dungeon feature folder (specs/08 section 1 service inventory; section 3.3): dungeon seed issue and result correlation.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// <c>PUT /save</c> calls <see cref="DungeonCorrelation"/> inside its transaction (section 7.3).
/// </summary>
public static class DungeonFeature
{
    public static IServiceCollection AddDungeonFeature(this IServiceCollection services)
    {
        services.TryAddSingleton<IDungeonSeedSource, CsprngDungeonSeedSource>();
        services.AddSingleton<DungeonStore>();
        services.AddSingleton<DungeonSeedService>();
        services.AddSingleton<DungeonCorrelation>();
        return services;
    }
}
