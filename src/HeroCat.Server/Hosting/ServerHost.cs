using HeroCat.Server.Features.Admin;
using HeroCat.Server.Features.Auth;
using HeroCat.Server.Features.Config;
using HeroCat.Server.Features.Dungeon;
using HeroCat.Server.Features.Iap;
using HeroCat.Server.Features.Inbox;
using HeroCat.Server.Features.Profile;
using HeroCat.Server.Features.Save;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Infrastructure.Blobs;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Infrastructure.Db.Migrations;
using HeroCat.Server.Infrastructure.Idempotency;
using HeroCat.Server.Infrastructure.Redis;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Worker;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Hosting;

/// <summary>Builds and runs the process for its role (specs/08 section 9.1).</summary>
public static class ServerHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        ServerRole role;
        string[] rest;
        try { (role, rest) = ServerRoles.Parse(args); }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 2;
        }

        switch (role)
        {
            case ServerRole.Api:
            {
                var app = BuildApi(rest);
                if (app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.Database.MigrateOnStartup)
                    await ApplyMigrationsAsync(app.Services, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
                await app.RunAsync().ConfigureAwait(false);
                return 0;
            }
            case ServerRole.Worker:
                await BuildWorker(rest).RunAsync().ConfigureAwait(false);
                return 0;
            case ServerRole.Migrate:
                return await MigrateAsync(rest).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(role));
        }
    }

    /// <summary>The api role. Tests build it through WebApplicationFactory&lt;Program&gt;.</summary>
    public static WebApplication BuildApi(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        AddCore(builder.Services, builder.Configuration, ServerRole.Api);
        builder.Services.AddControllers();
        builder.Services
            .AddAuthFeature()
            .AddProfileFeature()
            .AddSaveFeature()
            .AddSummonFeature()
            .AddInboxFeature()
            .AddIapFeature()
            .AddConfigFeature()
            .AddDungeonFeature()
            .AddAdminFeature();
        builder.Services.AddRequestPipeline();

        var app = builder.Build();
        app.UseRequestPipeline();
        app.MapControllers();
        return app;
    }

    /// <summary>The worker role: a plain host with the job runner and no HTTP.</summary>
    public static IHost BuildWorker(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        AddCore(builder.Services, builder.Configuration, ServerRole.Worker);
        builder.Services.AddHostedService<WorkerService>();
        builder.Services.AddHostedService<IdempotencyPurgeService>();
        return builder.Build();
    }

    /// <summary>Services every role shares: options, the clock, Postgres, Redis, blob storage, the idempotency store, locks, the migration runner.</summary>
    public static void AddCore(IServiceCollection services, IConfiguration configuration, ServerRole role)
    {
        services.AddOptions<ServerOptions>().Bind(configuration.GetSection(ServerOptions.Section));
        services.AddSingleton(new ServerRoleInfo(role));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
        services.AddSingleton<IRedisConnection, RedisConnection>();
        services.AddSingleton<IBlobStore>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ServerOptions>>();
            return o.Value.Blobs.Provider switch
            {
                BlobProviders.S3 => new S3BlobStore(o),
                BlobProviders.Memory => new InMemoryBlobStore(),
                var p => throw new InvalidOperationException($"HeroCat:Blobs:Provider '{p}': expected {BlobProviders.S3} or {BlobProviders.Memory}"),
            };
        });
        // Postgres and Redis when configured; otherwise per-process state (tests, a local run), as the blob store does.
        services.AddSingleton<IIdempotencyStore>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
            var ttl = IdempotencyPurgeService.IdempotencyTtl(o);
            return string.IsNullOrWhiteSpace(o.Database.ConnectionString)
                ? new InMemoryIdempotencyStore(ttl)
                : new PostgresIdempotencyStore(sp.GetRequiredService<IDbConnectionFactory>(), ttl);
        });
        services.AddSingleton<IDistributedLock>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<ServerOptions>>().Value.Redis.Configuration)
                ? new InMemoryDistributedLock(sp.GetRequiredService<TimeProvider>())
                : ActivatorUtilities.CreateInstance<RedisDistributedLock>(sp));
        services.AddSingleton<SummonLock>();
        services.AddSingleton<IMigrationJournal, NpgsqlMigrationJournal>();
        services.AddSingleton<MigrationRunner>();
    }

    static async Task<int> MigrateAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        AddCore(builder.Services, builder.Configuration, ServerRole.Migrate);
        using var host = builder.Build();
        try
        {
            await ApplyMigrationsAsync(host.Services, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (MigrationException e)
        {
            Console.Error.WriteLine("migrate: " + e.Message);
            return 1;
        }
    }

    public static async Task<IReadOnlyList<MigrationScript>> ApplyMigrationsAsync(IServiceProvider services, CancellationToken ct)
    {
        var o = services.GetRequiredService<IOptions<ServerOptions>>().Value.Database;
        string dir = Path.IsPathRooted(o.MigrationsPath) ? o.MigrationsPath : Path.Combine(AppContext.BaseDirectory, o.MigrationsPath);
        var scripts = MigrationScript.LoadDirectory(dir);
        return await services.GetRequiredService<MigrationRunner>().RunAsync(scripts, ct).ConfigureAwait(false);
    }
}
