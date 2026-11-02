using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Pipeline;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Config;

/// <summary>
/// The Config feature folder (specs/08 section 1 service inventory; section 3.4 and 6): remote config, kill switches, content manifest pointer, min versions, maintenance.
/// Its controllers, handlers and stores live in this folder; this class is where the feature registers its services.
/// </summary>
public static class ConfigFeature
{
    public static IServiceCollection AddConfigFeature(this IServiceCollection services)
    {
        // Postgres when configured; otherwise per-process rows (tests, a local run), as the idempotency store does.
        services.AddSingleton<IConfigStore>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<ServerOptions>>().Value.Database.ConnectionString)
                ? new InMemoryConfigStore()
                : new PostgresConfigStore(sp.GetRequiredService<IDbConnectionFactory>()));
        services.AddSingleton<ActiveConfigCache>();
        services.AddSingleton<IActiveConfigSource>(sp => sp.GetRequiredService<ActiveConfigCache>());
        // The pipeline's version floors and limit overrides (registered before AddRequestPipeline, whose default only fills the gap).
        services.AddSingleton<IClientGateConfigSource>(sp => sp.GetRequiredService<ActiveConfigCache>());
        services.AddHttpClient(HttpContentManifestFetcher.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IContentManifestFetcher, HttpContentManifestFetcher>();
        services.AddSingleton<ContentManifestVerifier>();
        return services;
    }
}
