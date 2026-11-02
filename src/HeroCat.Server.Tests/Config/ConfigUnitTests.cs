using System.Net;
using System.Text.Json.Nodes;
using HeroCat.Server.Features.Config;
using HeroCat.Server.Hosting;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Config;

/// <summary>The Config feature's pure parts: the rollout bucket, the body, payload checks, kill switches, limit overrides, the cache.</summary>
[TestFixture]
public sealed class ConfigUnitTests
{
    [Test]
    public void The_bucket_admits_about_rolloutPct_of_players_and_is_stable()
    {
        // Acceptance 16: 1,000 synthetic players at rolloutPct = 25 land in the bucket 20-30% of the time.
        var rng = new Random(204);
        var players = Enumerable.Range(0, 1000).Select(_ =>
        {
            var b = new byte[16];
            rng.NextBytes(b);
            return new Guid(b);
        }).ToList();
        int inBucket = players.Count(p => RolloutBucket.InBucket(p, "2026.11.10", 25));
        Assert.That(inBucket, Is.InRange(200, 300));
        Assert.That(players.All(p => RolloutBucket.InBucket(p, "2026.11.10", 25) == RolloutBucket.InBucket(p, "2026.11.10", 25)));
        Assert.That(players.All(p => !RolloutBucket.InBucket(p, "2026.11.10", 25) || RolloutBucket.InBucket(p, "2026.11.10", 50)),
                    "raising rolloutPct adds players, never removes them");
        Assert.That(players.All(p => RolloutBucket.InBucket(p, "2026.11.10", 100) && !RolloutBucket.InBucket(p, "2026.11.10", 0)));
        Assert.That(players.Count(p => RolloutBucket.Bucket(p, "2026.11.10") != RolloutBucket.Bucket(p, "2026.11.17")), Is.GreaterThan(900),
                    "a new content version reshuffles the buckets");
    }

    [Test]
    public void The_bucket_hash_is_pinned()
    {
        // SHA-256 over "<playerId D-format><version>", first 8 bytes big-endian, mod 100 (specs/08 section 6.1).
        var p = Guid.Parse("6b1f2c40-0000-4000-8000-000000000001");
        byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("6b1f2c40-0000-4000-8000-000000000001" + "2026.11.10"));
        ulong v = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(h);
        Assert.That(RolloutBucket.Bucket(p, "2026.11.10"), Is.EqualTo((int)(v % 100)));
    }

    static JsonObject Payload(int pct = 25) => ConfigHost.Payload(new FakeCdn(), rolloutPct: pct);

    [Test]
    public void The_body_applies_the_bucket_only_with_a_player()
    {
        var config = new ActiveConfig(7, Payload());
        var anon = ConfigResponse.Build(config, null, null, 100);
        Assert.That(anon["configVersion"]!.GetValue<int>(), Is.EqualTo(7));
        Assert.That(anon["serverTime"]!.GetValue<long>(), Is.EqualTo(100));
        Assert.That(anon.ContainsKey("defaultContent"), Is.False);
        Assert.That(anon["content"]!["version"]!.GetValue<string>(), Is.EqualTo("2026.11.03"));
        Assert.That(((JsonObject)anon["content"]!).ContainsKey("inBucket"), Is.False);

        Guid inside = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).First(g => RolloutBucket.InBucket(g, "2026.11.10", 25));
        Guid outside = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).First(g => !RolloutBucket.InBucket(g, "2026.11.10", 25));
        var a = ConfigResponse.Build(config, inside, null, 100)["content"]!;
        Assert.That(a["version"]!.GetValue<string>(), Is.EqualTo("2026.11.10"));
        Assert.That(a["inBucket"]!.GetValue<bool>(), Is.True);
        var b = ConfigResponse.Build(config, outside, null, 100)["content"]!;
        Assert.That(b["version"]!.GetValue<string>(), Is.EqualTo("2026.11.03"));
        Assert.That(b["inBucket"]!.GetValue<bool>(), Is.False);

        var full = new ActiveConfig(8, Payload(100));
        Assert.That(ConfigResponse.Build(full, null, null, 1)["content"]!["version"]!.GetValue<string>(), Is.EqualTo("2026.11.10"), "fully rolled out: everyone");
        Assert.That(config.Payload.ContainsKey("defaultContent"), "building never changes the stored payload");
    }

    [Test]
    public void A_live_maintenance_flag_replaces_the_announcement()
    {
        var config = new ActiveConfig(1, Payload(100));
        var m = ConfigResponse.Build(config, null, new MaintenanceState(true, "maint.db_upgrade", 5), 1)["maintenance"]!;
        Assert.That(m["active"]!.GetValue<bool>());
        Assert.That(m["until"]!.GetValue<long>(), Is.EqualTo(5));
        var off = ConfigResponse.Build(config, null, null, 1)["maintenance"]!;
        Assert.That(off["active"]!.GetValue<bool>(), Is.False);
    }

    [Test]
    public void Normalise_strips_server_fields_and_returns_the_columns()
    {
        var p = Payload();
        p["configVersion"] = 3;
        p["serverTime"] = 9;
        p["content"]!["inBucket"] = true;
        var cols = ConfigPayload.Normalise(p);
        Assert.That(cols, Is.EqualTo(new ConfigColumns("1.4.0", "2026.11.10", 25)));
        Assert.That(p.ContainsKey("configVersion") || p.ContainsKey("serverTime") || ((JsonObject)p["content"]!).ContainsKey("inBucket"), Is.False);

        var noPct = Payload(100);
        ((JsonObject)noPct["content"]!).Remove("rolloutPct");
        Assert.That(ConfigPayload.Normalise(noPct).RolloutPct, Is.EqualTo(100));
    }

    [TestCase("minClientVersion", "one")]
    [TestCase("content.version", "11/10/2026")]
    [TestCase("content.url", "not a url")]
    [TestCase("content.rolloutPct", 101)]
    [TestCase("limits.summonPerMinute", 0)]
    [TestCase("minContentVersionByClient.1.4.0", "soon")]
    public void Normalise_refuses_bad_fields(string path, object value)
    {
        var p = Payload();
        string[] parts = path.Split('.', 2);
        JsonNode node = value is int i ? JsonValue.Create(i) : JsonValue.Create((string)value);
        if (parts.Length == 1) p[parts[0]] = node;
        else p[parts[0]]![parts[1]] = node;
        var e = Assert.Throws<ApiException>(() => ConfigPayload.Normalise(p))!;
        Assert.That(e.Code, Is.EqualTo(ErrorCodes.VALIDATION));
    }

    [Test]
    public void Kill_switches_default_to_enabled()
    {
        var c = new ActiveConfig(1, Payload());
        Assert.That(c.SummonEnabled("WEAPON"), Is.True);
        Assert.That(c.SummonEnabled("RUNE"), Is.False);
        Assert.That(c.SummonEnabled("NEW_BANNER"), Is.True);
        Assert.That(c.Enabled("shopEnabled"), Is.True);
        Assert.That(c.Enabled("guildEnabled"), Is.True, "absent");
        var all = Payload();
        all["killSwitches"]!["summonEnabled"] = false;
        all["killSwitches"]!["shopEnabled"] = false;
        var off = new ActiveConfig(2, all);
        Assert.That(off.SummonEnabled("WEAPON"), Is.False);
        Assert.That(off.Enabled("shopEnabled"), Is.False);
        Assert.That(new ActiveConfig(3, new JsonObject()).SummonEnabled("WEAPON"), Is.True);
    }

    [Test]
    public void The_gates_read_floors_and_limits_from_the_payload()
    {
        var g = new ActiveConfig(1, Payload()).Gates;
        Assert.That(g.MinClientVersion, Is.EqualTo("1.4.0"));
        Assert.That(g.MinContentVersionByClient["1.4.1"], Is.EqualTo("2026.11.03"));
        Assert.That(g.Limits, Is.EqualTo(new RemoteLimits(20, 400)));
    }

    [Test]
    public void Remote_limits_override_the_two_named_rules_only()
    {
        var limits = new RemoteLimits(5, 100);
        var summon = new[] { new RateLimitRule(20, 60) };
        Assert.That(limits.Apply("post/summon/pull", summon), Is.EqualTo(new[] { new RateLimitRule(5, 60) }));
        var save = new[] { new RateLimitRule(6, 60), new RateLimitRule(400, 86400) };
        Assert.That(limits.Apply("put/save", save), Is.EqualTo(new[] { new RateLimitRule(6, 60), new RateLimitRule(100, 86400) }), "the per-minute rule keeps its declared value");
        Assert.That(limits.Apply("get/config", new[] { new RateLimitRule(30, 60) }), Is.EqualTo(new[] { new RateLimitRule(30, 60) }));
        Assert.That(new RemoteLimits(null, null).Apply("post/summon/pull", summon), Is.SameAs(summon));
        Assert.That(new RemoteLimits(0, -1).Apply("put/save", save), Is.SameAs(save));
    }

    [Test]
    public async Task Config_limits_override_the_declared_rate_limits_in_the_pipeline()
    {
        using var host = new PipelineHost();
        using var client = host.Client();
        host.Gates.Config = new ClientGateConfig(null, new Dictionary<string, string>(), new RemoteLimits(2, 3));
        for (int i = 0; i < 2; i++)
            Assert.That((await client.SendAsync(PipelineHost.Request(HttpMethod.Post, "/v1/summon/pull"))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await client.SendAsync(PipelineHost.Request(HttpMethod.Post, "/v1/summon/pull"))).StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));

        // PUT /save: the per-day rule drops to 3; the declared 6/min would otherwise allow a 4th.
        for (int i = 0; i < 3; i++)
            Assert.That((await client.SendAsync(PipelineHost.Request(HttpMethod.Put, "/v1/save"))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.That((await client.SendAsync(PipelineHost.Request(HttpMethod.Put, "/v1/save"))).StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
    }

    sealed class FlakyStore : IConfigStore
    {
        public readonly InMemoryConfigStore Inner = new();
        public bool Down;
        public int Reads;
        public Task<ConfigVersionRow?> GetActiveAsync(string env, CancellationToken ct)
        {
            Reads++;
            return Down ? throw new TimeoutException("db down") : Inner.GetActiveAsync(env, ct);
        }
        public Task<ConfigVersionRow?> GetAsync(string env, int id, CancellationToken ct) => Inner.GetAsync(env, id, ct);
        public Task<int> InsertAsync(string env, JsonObject payload, ConfigColumns columns, string createdBy, DateTimeOffset now, CancellationToken ct) =>
            Inner.InsertAsync(env, payload, columns, createdBy, now, ct);
        public Task<bool> ActivateAsync(string env, int id, CancellationToken ct) => Inner.ActivateAsync(env, id, ct);
    }

    [Test]
    public async Task The_cache_refreshes_after_the_interval_and_keeps_serving_while_the_store_is_down()
    {
        var store = new FlakyStore();
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_000));
        var options = Options.Create(new ServerOptions { Env = "dev", Limits = new ServerLimits { ConfigRefreshSeconds = 30 } });
        var cache = new ActiveConfigCache(store, clock, options, NullLogger<ActiveConfigCache>.Instance);
        IClientGateConfigSource gates = cache;

        Assert.That(await cache.GetActiveAsync(default), Is.Null);
        int id = await store.InsertAsync("dev", Payload(), new ConfigColumns("1.4.0", "2026.11.10", 25), "t", clock.GetUtcNow(), default);
        await store.ActivateAsync("dev", id, default);
        Assert.That(await cache.GetActiveAsync(default), Is.Null, "the copy is younger than CONFIG_REFRESH_S");
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.That((await cache.GetActiveAsync(default))!.Id, Is.EqualTo(id));
        int reads = store.Reads;
        await cache.GetActiveAsync(default);
        Assert.That(store.Reads, Is.EqualTo(reads), "a fresh copy reads nothing");

        store.Down = true;
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That((await cache.GetActiveAsync(default))!.Id, Is.EqualTo(id), "stale copy while the store is down");
        Assert.That((await gates.GetAsync(default)).MinClientVersion, Is.EqualTo("1.4.0"));

        var cold = new ActiveConfigCache(store, clock, options, NullLogger<ActiveConfigCache>.Instance);
        Assert.ThrowsAsync<TimeoutException>(async () => await cold.GetActiveAsync(default));
        Assert.That(await ((IClientGateConfigSource)cold).GetAsync(default), Is.SameAs(ClientGateConfig.None), "gates fail open with nothing loaded");
    }
}
