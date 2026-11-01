using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace HeroCat.Server.Pipeline;

/// <summary>
/// The OpenAPI document generated from the controllers (specs/08 acceptance 23; the contract test compares it with the
/// spec, and specs/10's compatibility test reads it). Every operation carries its auth policy as <c>x-herocat-auth</c>,
/// its rate limits as <c>x-herocat-rate-limits</c> (<c>5/60s</c>), and the error body as its <c>default</c> response.
/// Served at <see cref="DocumentPath"/> in Development only.
/// </summary>
public static class OpenApi
{
    public const string DocumentName = "v1";
    public const string DocumentPath = "/openapi/v1.json";
    public const string AuthExtension = "x-herocat-auth";
    public const string RateLimitsExtension = "x-herocat-rate-limits";

    public static IServiceCollection AddHeroCatOpenApi(this IServiceCollection services)
    {
        services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc(DocumentName, new OpenApiInfo { Title = "HeroCat API", Version = DocumentName });
            o.OperationFilter<PipelineOperationFilter>();
            o.CustomSchemaIds(t => t.FullName?.Replace("+", ".", StringComparison.Ordinal));
        });
        return services;
    }

    sealed class PipelineOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var action = context.ApiDescription.ActionDescriptor as ControllerActionDescriptor;
            var attrs = action == null
                ? Array.Empty<object>()
                : action.MethodInfo.GetCustomAttributes(true).Concat(action.ControllerTypeInfo.GetCustomAttributes(true)).ToArray();

            string path = "/" + (context.ApiDescription.RelativePath ?? "").Split('?')[0].Trim('/');
            var policy = AuthPolicies.Resolve(attrs.OfType<AuthAttribute>().FirstOrDefault(), path);
            operation.Extensions[AuthExtension] = new OpenApiString(AuthPolicies.Name(policy));

            var limits = new OpenApiArray();
            foreach (var r in attrs.OfType<RateLimitAttribute>().Select(a => a.Rule).Distinct())
                limits.Add(new OpenApiString(r.ToString()));
            operation.Extensions[RateLimitsExtension] = limits;

            var error = context.SchemaGenerator.GenerateSchema(typeof(ErrorBody), context.SchemaRepository);
            operation.Responses["default"] = new OpenApiResponse
            {
                Description = "Error body (specs/08 section 1.1)",
                Content = { ["application/json"] = new OpenApiMediaType { Schema = error } },
            };
        }
    }
}
