namespace HeroCat.Server.Features.Summon;

/// <summary>
/// The Summon feature folder (specs/08 section 1 service inventory; section 4): server-authoritative pulls, pity state, rate table, history, published rates.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class SummonFeature
{
    // TODO(#199, #200): the Summon endpoints and their services.
    public static IServiceCollection AddSummonFeature(this IServiceCollection services) => services;
}
