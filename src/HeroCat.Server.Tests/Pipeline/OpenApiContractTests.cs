using System.Net;
using System.Text.Json;
using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using NUnit.Framework;
using Swashbuckle.AspNetCore.Swagger;

namespace HeroCat.Server.Tests.Pipeline;

/// <summary>
/// specs/08 acceptance 23, OpenAPI half: the document generated from the controllers agrees with the spec's endpoint
/// lines (auth, rate limits) and JSON examples (request and 2xx keys), and every spec endpoint is either implemented or
/// still owned by an open issue.
/// </summary>
[TestFixture]
public sealed class OpenApiContractTests
{
    /// <summary>
    /// Spec endpoints with no controller yet, by route prefix, and the issue that adds them. A feature PR removes its
    /// row when its last endpoint lands; the endpoint then has to match the spec.
    /// </summary>
    static readonly (string prefix, int issue)[] Pending =
    {
        ("/auth/link", 196), ("/auth/merge", 196),
        ("/profile", 197), ("/account/", 197),
        ("/save", 198),
        ("/summon/rates", 200), ("/summon/", 199),
        ("/inbox", 201),
        ("/shop/", 202), ("/iap/", 202),
        ("/config", 204),
        ("/dungeon/", 205),
    };

    static OpenApiDocument Document(IServiceProvider services) =>
        services.GetRequiredService<ISwaggerProvider>().GetSwagger(OpenApi.DocumentName);

    [Test]
    public void The_spec_endpoint_lines_parse()
    {
        var e = SpecContract.Endpoints();
        Assert.That(e.Count, Is.GreaterThanOrEqualTo(25));
        var guest = e.Single(x => x.Id == "POST /auth/guest");
        Assert.That(guest.Auth, Is.EqualTo("none"));
        Assert.That(guest.Idempotent, Is.True);
        Assert.That(e.Single(x => x.Id == "POST /auth/refresh").Idempotent, Is.False, "\"not idempotent\"");
        Assert.That(e.Single(x => x.Id == "GET /time").Idempotent, Is.False);
        Assert.That(e.Single(x => x.Id == "POST /iap/validate").Idempotent, Is.True);
        Assert.That(guest.Limits, Is.EquivalentTo(new[] { new RateLimitRule(5, 60), new RateLimitRule(20, 86400) }));
        Assert.That(guest.BodyKeys["request"], Is.EquivalentTo(new[] { "deviceId", "deviceKey", "model", "os", "clientVersion", "region" }));
        Assert.That(guest.BodyKeys["201"], Does.Contain("accessToken").And.Contain("serverTime"));
        Assert.That(e.Single(x => x.Id == "PUT /save").Limits, Is.EquivalentTo(new[] { new RateLimitRule(6, 60), new RateLimitRule(400, 86400) }));
        Assert.That(e.Single(x => x.Id == "POST /account/export").Limits, Is.EqualTo(new[] { new RateLimitRule(1, 7 * 86400) }));
        Assert.That(e.Single(x => x.Id == "POST /auth/merge").Limits, Is.EqualTo(new[] { new RateLimitRule(2, 3600) }));
        Assert.That(e.Single(x => x.Id == "POST /summon/pull").Auth, Is.EqualTo("jwt+device"));
        Assert.That(e.Single(x => x.Id == "GET /config").Auth, Is.EqualTo("none"));
        Assert.That(e.Single(x => x.Id == "POST /auth/link").BodyKeys["request"], Does.Contain("identityToken").And.Contain("serverAuthCode"));
    }

    [Test]
    public void The_error_body_schema_is_the_spec_example()
    {
        using var host = new PipelineHost();
        var doc = Document(host.Services);
        var op = doc.Paths["/v1/time"].Operations[OperationType.Get];
        var schema = op.Responses["default"].Content["application/json"].Schema;
        Assert.That(SpecContract.Properties(doc, schema), Is.EquivalentTo(SpecContract.ErrorBodyKeys()));
        Assert.That(SpecContract.ErrorBodyKeys(), Is.EquivalentTo(new[] { "code", "message", "requestId", "retryAfterSec", "details" }));
    }

    [Test]
    public void The_servers_operations_match_the_spec_and_every_spec_endpoint_is_implemented_or_pending()
    {
        using var factory = new WebApplicationFactory<Program>();
        var doc = Document(factory.Services);
        var spec = SpecContract.Endpoints();
        Assert.That(SpecContract.Compare(doc, spec), Is.Empty);

        var implemented = doc.Paths.SelectMany(p => p.Value.Operations.Keys.Select(t => t.ToString().ToUpperInvariant() + " " + SpecContract.Normalise(p.Key))).ToHashSet();
        var missing = spec.Where(e => !implemented.Contains(e.Method + " " + SpecContract.Normalise(e.Path)))
                          .Where(e => !Pending.Any(p => e.Path.StartsWith(p.prefix, StringComparison.Ordinal)))
                          .Select(e => $"{e.Id} (specs/08 line {e.Line})").ToList();
        Assert.That(missing, Is.Empty, "spec endpoints with neither a controller nor a pending issue");
    }

    [Test]
    public void The_comparison_accepts_matching_operations_and_reports_drift()
    {
        using var host = new PipelineHost();
        var doc = Document(host.Services);
        var spec = SpecContract.Endpoints();
        bool SpecRoutes(string id) => !id.Contains("/_test/", StringComparison.Ordinal);
        Assert.That(SpecContract.Compare(doc, spec, SpecRoutes).Where(p => !p.Contains(" no ", StringComparison.Ordinal) || p.StartsWith("POST /auth/guest", StringComparison.Ordinal)),
                    Is.Empty, "the stand-ins declare the spec's auth and limits, and /auth/guest its bodies");

        var drifted = spec.Select(e => e.Id switch
        {
            "POST /auth/guest" => e with { Auth = "jwt", Limits = new[] { new RateLimitRule(5, 60) }, Idempotent = false,
                                           BodyKeys = new Dictionary<string, HashSet<string>> { ["request"] = new() { "deviceId", "extra" } } },
            _ => e,
        }).Where(e => e.Id != "GET /time").ToList();
        var problems = SpecContract.Compare(doc, drifted, SpecRoutes);
        Assert.That(problems, Has.Some.StartsWith("POST /auth/guest: auth none"));
        Assert.That(problems, Has.Some.StartsWith("POST /auth/guest: rate limits"));
        Assert.That(problems, Has.Some.StartsWith("POST /auth/guest: idempotent true"));
        Assert.That(problems, Has.Some.StartsWith("POST /auth/guest request: properties"));
        Assert.That(problems, Has.Some.EqualTo("GET /time: not an endpoint in specs/08"));
    }

    [Test]
    public async Task The_document_is_served_in_development()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var r = await client.GetAsync(OpenApi.DocumentPath);
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.That(root.GetProperty("openapi").GetString(), Does.StartWith("3."));
        Assert.That(root.GetProperty("paths").TryGetProperty("/health", out _));
    }
}
