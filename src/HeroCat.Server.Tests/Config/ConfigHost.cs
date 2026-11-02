using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeroCat.Server.Features.Config;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HeroCat.Server.Tests.Config;

/// <summary>
/// The real api with the Config feature: a fake clock, fake tokens and admins, in-memory limits and maintenance, a fake
/// CDN, and the config store given (in-memory by default; Postgres when a connection string is given).
/// </summary>
sealed class ConfigHost : IDisposable
{
    public readonly FakeClock Clock;
    public readonly InMemoryMaintenanceFlag Maintenance = new();
    public readonly FakeCdn Cdn;
    readonly WebApplicationFactory<Program> _factory;

    public ConfigHost(FakeClock? clock = null, IConfigStore? store = null, FakeCdn? cdn = null, string? connectionString = null, string? env = null)
    {
        Clock = clock ?? new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
        Cdn = cdn ?? new FakeCdn();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (connectionString != null) b.UseSetting("HeroCat:Database:ConnectionString", connectionString);
            if (env != null) b.UseSetting("HeroCat:Env", env);
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton<IMaintenanceFlag>(Maintenance);
                s.AddSingleton<IAccessTokenValidator>(new FakeTokens());
                s.AddSingleton<IAdminAuthenticator>(new FakeAdmins());
                s.AddSingleton<IRateLimitStore>(new InMemoryRateLimitStore());
                s.AddSingleton<IContentManifestFetcher>(Cdn);
                if (store != null) s.AddSingleton(store);
            });
        });
    }

    public IServiceProvider Services => _factory.Services;
    public HttpClient Client() => _factory.CreateClient();
    public void Dispose() => _factory.Dispose();

    public static HttpRequestMessage GetConfig(Guid? player = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/v1/config");
        r.Headers.Add(ApiHeaders.ClientVersion, PipelineHost.ClientVersion);
        if (player is Guid p) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FakeTokens.For(p, PipelineHost.DeviceId));
        return r;
    }

    public static HttpRequestMessage Admin(string path, object? body, string actor = "ops@herocat.example")
    {
        var r = new HttpRequestMessage(HttpMethod.Post, path);
        r.Headers.Add(FakeAdmins.Header, actor);
        r.Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return r;
    }

    /// <summary>Publishes and activates a payload; returns the new id.</summary>
    public static async Task<int> PublishAndActivate(HttpClient client, JsonObject payload)
    {
        int id = await Publish(client, payload);
        using var a = await client.SendAsync(Admin($"/v1/admin/config/{id}/activate", new { }));
        if (!a.IsSuccessStatusCode) throw new InvalidOperationException($"activate {id}: {(int)a.StatusCode} {await a.Content.ReadAsStringAsync()}");
        return id;
    }

    public static async Task<int> Publish(HttpClient client, JsonObject payload)
    {
        using var r = await client.SendAsync(Admin("/v1/admin/config", payload.ToJsonString()));
        string text = await r.Content.ReadAsStringAsync();
        if ((int)r.StatusCode != 201) throw new InvalidOperationException($"publish: {(int)r.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetInt32();
    }

    public static async Task<JsonElement> Body(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>A 6.1-shaped payload whose content manifests are on <paramref name="cdn"/>.</summary>
    public static JsonObject Payload(FakeCdn cdn, string version = "2026.11.10", int rolloutPct = 100, string? defaultVersion = "2026.11.03",
                                     bool weaponSummon = true, int? summonPerMinute = null)
    {
        var p = new JsonObject
        {
            ["minClientVersion"] = "1.4.0",
            ["latestClientVersion"] = "1.4.2",
            ["storeUrls"] = new JsonObject { ["ios"] = "https://apps.apple.com/x", ["android"] = "https://play.google.com/x" },
            ["content"] = cdn.Content(version, rolloutPct),
            ["minContentVersionByClient"] = new JsonObject { ["1.4.0"] = "2026.10.20", ["1.4.1"] = "2026.11.03" },
            ["killSwitches"] = new JsonObject
            {
                ["shopEnabled"] = true,
                ["summonEnabled"] = new JsonObject { ["WEAPON"] = weaponSummon, ["SKILL"] = true, ["COMPANION"] = true, ["RUNE"] = false },
                ["dungeonsEnabled"] = true,
            },
            ["maintenance"] = new JsonObject { ["active"] = false, ["messageKey"] = null, ["until"] = null },
            ["banners"] = new JsonArray(),
            ["events"] = new JsonArray(),
            ["limits"] = new JsonObject { ["summonPerMinute"] = summonPerMinute ?? 20, ["saveUploadsPerDay"] = 400 },
            ["features"] = new JsonObject { ["stageSelect"] = true },
        };
        if (defaultVersion != null) p["defaultContent"] = cdn.Content(defaultVersion, 100);
        return p;
    }
}

/// <summary>The CDN: manifests by URL. <see cref="Content"/> uploads one and returns its 6.1 content pointer.</summary>
sealed class FakeCdn : IContentManifestFetcher
{
    readonly Dictionary<string, byte[]> _files = new();

    public static string Url(string version) => $"https://cdn.whiskerhero.example/content/{version}/manifest.json";

    public void Put(string url, byte[] bytes)
    {
        lock (_files) _files[url] = bytes;
    }

    public void Remove(string url)
    {
        lock (_files) _files.Remove(url);
    }

    public JsonObject Content(string version, int rolloutPct)
    {
        byte[] manifest = Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"hot_apply\":false}}");
        Put(Url(version), manifest);
        return new JsonObject
        {
            ["version"] = version,
            ["url"] = Url(version),
            ["sha256"] = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant(),
            ["sizeBytes"] = 1843200,
            ["minClientVersion"] = "1.4.1",
            ["rolloutPct"] = rolloutPct,
            ["hotApplyTables"] = new JsonArray("localization", "shop_products", "shop_packages", "inbox_templates"),
        };
    }

    public Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        lock (_files) return Task.FromResult(_files.TryGetValue(url, out var b) ? b : null);
    }
}
