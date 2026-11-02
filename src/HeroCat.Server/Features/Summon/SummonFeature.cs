using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Summon;

/// <summary>
/// The Summon feature folder (specs/08 section 1 service inventory; section 4): server-authoritative pulls, pity state, rate table, history, published rates.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class SummonFeature
{
    // TODO(#200): the published rates endpoint (/summon/rates), built on RateTableHash.
    public static IServiceCollection AddSummonFeature(this IServiceCollection services)
    {
        services.AddOptions<ContentSetOptions>().BindConfiguration(ContentSetOptions.Section);
        services.TryAddSingleton<IContentSets>(sp => new DirectoryContentSets(sp.GetRequiredService<IOptions<ContentSetOptions>>()));
        services.TryAddSingleton<ISummonConfigSource, OpenSummonConfigSource>();
        services.TryAddSingleton(CsprngUniformSource.Factory);
        services.AddSingleton<SummonStore>();
        services.AddSingleton<SummonService>();
        return services;
    }
}
