using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Pipeline;

/// <summary>
/// Registers and orders the request pipeline every endpoint shares (specs/08 section 1.1). Features replace the default
/// seams (<see cref="IAccessTokenValidator"/>, <see cref="IAdminAuthenticator"/>, <see cref="IAccountStatusSource"/>,
/// <see cref="IClientGateConfigSource"/>) by registering their own; the defaults are registered with TryAdd.
/// </summary>
public static class PipelineSetup
{
    public static IServiceCollection AddRequestPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton<IAccessTokenValidator, RejectingAccessTokenValidator>();
        services.TryAddSingleton<IAdminAuthenticator, DenyingAdminAuthenticator>();
        services.TryAddSingleton<IAccountStatusSource, NoBansAccountStatusSource>();
        services.TryAddSingleton<IClientGateConfigSource, UngatedClientGateConfigSource>();
        services.TryAddSingleton<IPlausibilityFlagSink, LoggingPlausibilityFlagSink>();

        // Redis when it is configured; otherwise per-process state (tests, a local run), as the blob store does.
        services.TryAddSingleton<IMaintenanceFlag>(sp => RedisConfigured(sp)
            ? new RedisMaintenanceFlag(sp.GetRequiredService<IRedisConnection>())
            : new InMemoryMaintenanceFlag());
        services.TryAddSingleton<IRateLimitStore>(sp => RedisConfigured(sp)
            ? new RedisRateLimitStore(sp.GetRequiredService<IRedisConnection>())
            : new InMemoryRateLimitStore());

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ServerOptions>>((o, server) => ForwardedNetwork.Apply(o, server.Value.Network));

        services.Configure<ApiBehaviorOptions>(o =>
        {
            // Bare error statuses get the error body from RequestContextMiddleware, not ProblemDetails.
            o.SuppressMapClientErrors = true;
            o.InvalidModelStateResponseFactory = ctx =>
            {
                var errors = ctx.ModelState
                    .Where(kv => kv.Value is { Errors.Count: > 0 })
                    .ToDictionary(kv => kv.Key, kv => kv.Value!.Errors.Select(e => e.ErrorMessage).ToArray());
                RequestContext.SetErrorCode(ctx.HttpContext, ErrorCodes.VALIDATION);
                var body = new ErrorBody(ErrorCodes.VALIDATION, ErrorCodes.DefaultMessage(ErrorCodes.VALIDATION),
                                         RequestContext.RequestId(ctx.HttpContext), null, new { errors });
                return new ObjectResult(body) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { "application/json" } };
            };
        });

        services.AddHeroCatOpenApi();
        return services;
    }

    static bool RedisConfigured(IServiceProvider sp) =>
        !string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<ServerOptions>>().Value.Redis.Configuration);

    /// <summary>
    /// The pipeline's middleware, outermost first: the trusted proxy's forwarded client address (issue #263), the request's
    /// log scope, metrics and access line (specs/08 section 9.4, issue #210), request id /
    /// server time / error body, routing, the gates, the idempotency store (after the gates, so it knows the player and
    /// never stores a gate's refusal).
    /// Call before mapping endpoints.
    /// </summary>
    public static WebApplication UseRequestPipeline(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<Observability.RequestTelemetryMiddleware>();
        app.UseMiddleware<RequestContextMiddleware>();
        if (app.Environment.IsDevelopment())
            app.UseSwagger(o => o.RouteTemplate = "openapi/{documentName}.json");
        app.UseRouting();
        app.UseMiddleware<ClientGateMiddleware>();
        app.UseMiddleware<IdempotencyMiddleware>();
        return app;
    }
}
