using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Summon;

/// <summary>
/// The Summon feature folder (specs/08 section 1 service inventory; section 4): server-authoritative pulls, pity state, rate table, history, published rates.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class SummonFeature
{
    public static IServiceCollection AddSummonFeature(this IServiceCollection services)
    {
        services.AddSummonContent();
        services.TryAddSingleton<ISummonConfigSource, OpenSummonConfigSource>();
        services.TryAddSingleton(CsprngUniformSource.Factory);
        services.AddSingleton<SummonStore>();
        services.AddSingleton<SummonService>();
        return services;
    }

    /// <summary>The content sets the server rolls with and publishes rates from (<c>HeroCat:Content</c>).</summary>
    public static IServiceCollection AddSummonContent(this IServiceCollection services)
    {
        services.AddOptions<ContentSetOptions>().BindConfiguration(ContentSetOptions.Section);
        services.TryAddSingleton<IContentSets>(sp => new DirectoryContentSets(sp.GetRequiredService<IOptions<ContentSetOptions>>()));
        return services;
    }

    /// <summary>The worker's nightly realised-rate job (specs/08 section 10.3) and its default alarm sink.</summary>
    public static IServiceCollection AddRateDriftJob(this IServiceCollection services)
    {
        services.AddSummonContent();
        services.TryAddSingleton<IRateDriftAlarm, LogRateDriftAlarm>();
        services.TryAddSingleton<RateDriftJob>();
        services.AddHostedService(sp => sp.GetRequiredService<RateDriftJob>());
        return services;
    }
}
