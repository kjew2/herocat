using System.Net;
using System.Text.Json;
using HeroCat.Sim;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace HeroCat.Server.Tests;

/// <summary>The api boots with no Postgres, Redis or S3 configured and serves /health, whose sim block comes from HeroCat.Sim.</summary>
[TestFixture]
public sealed class HealthTests
{
    WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void Start() =>
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("HeroCat:Version", "2026.11.01.1"));

    [OneTimeTearDown]
    public void Stop() => _factory.Dispose();

    async Task<JsonElement> GetHealth()
    {
        using var client = _factory.CreateClient();
        using var r = await client.GetAsync("/health");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(r.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Test]
    public async Task Health_reports_status_version_and_role()
    {
        var body = await GetHealth();
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ok"));
        Assert.That(body.GetProperty("version").GetString(), Is.EqualTo("2026.11.01.1"));
        Assert.That(body.GetProperty("role").GetString(), Is.EqualTo("api"));
    }

    [Test]
    public async Task Health_sim_block_is_computed_by_the_Sim_assembly()
    {
        var sim = (await GetHealth()).GetProperty("sim");
        Assert.That(sim.GetProperty("version").GetString(), Is.EqualTo(typeof(Simulation).Assembly.GetName().Version!.ToString()));
        Assert.That(sim.GetProperty("saveSchemaVersion").GetInt32(), Is.EqualTo(GameState.CurrentSchemaVersion));
        string hash = sim.GetProperty("newStateHash").GetString()!;
        Assert.That(hash, Is.EqualTo(StateHasher.Hash(new GameState())));
        Assert.That(hash, Does.Match("^[0-9a-f]{64}$"));
    }

    [Test]
    public async Task Unknown_route_is_404()
    {
        using var client = _factory.CreateClient();
        using var r = await client.GetAsync("/v1/nope");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public void Server_references_the_Sim_and_never_the_Runtime()
    {
        // specs/01 section 1.2: HeroCat.Server references HeroCat.Sim, never HeroCat.Runtime (own persistence).
        var refs = typeof(Program).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.That(refs, Does.Contain("HeroCat.Sim"));
        Assert.That(refs, Has.None.StartsWith("HeroCat.Runtime"));
        Assert.That(refs, Has.None.StartsWith("HeroCat.Remote"));
        Assert.That(refs, Has.None.StartsWith("HeroCat.Bots"));
    }
}
