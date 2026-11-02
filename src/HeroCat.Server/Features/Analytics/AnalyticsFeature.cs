namespace HeroCat.Server.Features.Analytics;

/// <summary>
/// The Analytics feature folder (specs/08 section 1 service inventory; section 3.5): the backend economy-events ingest
/// and the server-side economy events of ledger writes (specs/02 section 14; issue #209).
/// </summary>
public static class AnalyticsFeature
{
    public static IServiceCollection AddAnalyticsFeature(this IServiceCollection services)
    {
        services.AddSingleton<AnalyticsEventStore>();
        services.AddSingleton<ServerEconomyEvents>();
        return services;
    }
}
