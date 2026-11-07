using System.Net;
using System.Text.Json;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Hosting;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Summon;

/// <summary>
/// <c>GET /summon/rates</c> and the odds export (specs/08 sections 4.5 and 10.3, acceptance 8; issue #200): every
/// published table sums to 1 with L and M fixed across levels, its hash is the one the pull path logs, its
/// <c>effective</c> block is what <c>tools/gacha_model.py</c> printed into the vectors, and the export carries the
/// endpoint's bodies unchanged.
/// </summary>
[TestFixture]
public sealed class PublishedRatesTests
{
    static readonly Lazy<IContentSets> Shipped = new(() => new DirectoryContentSets(Options.Create(new ContentSetOptions())));
    static ContentDb Db => Shipped.Value.Active;

    static readonly Lazy<JsonElement> Vectors = new(() =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.Of("Fixtures/Summon/vectors.json"))).RootElement.Clone());

    static IEnumerable<BannerDef> Published => Db.Banners.Values.Where(d => d.Levels.Count > 0).OrderBy(d => d.SortOrder);

    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;

    [OneTimeSetUp]
    public void StartApi()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IRateLimitStore>(new NoRateLimits());
            s.AddSingleton<IMaintenanceFlag>(new InMemoryMaintenanceFlag());
        }));
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void StopApi()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    async Task<(HttpResponseMessage response, JsonElement body)> Get(string query)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/v1/summon/rates" + query);
        r.Headers.Add(ApiHeaders.ClientVersion, PipelineHost.ClientVersion);
        var response = await _client.SendAsync(r);
        string text = await response.Content.ReadAsStringAsync();
        return (response, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Test]
    public void Three_banners_publish_ten_levels()
    {
        Assert.That(Published.Select(d => d.Id), Is.EquivalentTo(new[] { Banner.WEAPON, Banner.SKILL, Banner.COMPANION }));
        Assert.That(Published.Select(d => d.Levels.Count), Is.All.EqualTo(10));
    }

    /// <summary>Acceptance 8, through the endpoint: every level sums to 1 within 1e-9 and serves level 1's L and M.</summary>
    [Test]
    public async Task Every_published_table_sums_to_one_and_keeps_level_one_L_and_M()
    {
        foreach (var def in Published)
        {
            var (_, first) = await Get($"?banner={def.Id}&level=1");
            double l1 = first.GetProperty("rates").GetProperty("L").GetDouble(), m1 = first.GetProperty("rates").GetProperty("M").GetDouble();
            for (int level = 1; level <= def.Levels.Count; level++)
            {
                var (response, body) = await Get($"?banner={def.Id}&level={level}");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{def.Id} {level}: {body}");
                var rates = body.GetProperty("rates");
                Assert.That(rates.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "C", "R", "E", "L", "M" }));
                double sum = rates.EnumerateObject().Sum(p => p.Value.GetDouble());
                Assert.That(Math.Abs(sum - 1), Is.LessThanOrEqualTo(1e-9), $"{def.Id} level {level} sums to {sum:R}");
                Assert.That(rates.GetProperty("L").GetDouble(), Is.EqualTo(l1), $"{def.Id} level {level} L");
                Assert.That(rates.GetProperty("M").GetDouble(), Is.EqualTo(m1), $"{def.Id} level {level} M");
                foreach (var r in PublishedRates.Order)
                    Assert.That(rates.GetProperty(RarityCodes.Of(r)).GetDouble(), Is.EqualTo(def.Levels[level - 1].Rates[r]), "the content row, unchanged");
                Assert.That(body.GetProperty("level").GetInt32(), Is.EqualTo(level));
                Assert.That(body.GetProperty("banner").GetString(), Is.EqualTo(def.Id.ToString()));
                Assert.That(body.GetProperty("contentVersion").GetString(), Is.EqualTo(Db.Version));
            }
        }
    }

    [Test]
    public async Task The_body_has_the_spec_shape_and_is_cacheable_without_a_token()
    {
        var (response, body) = await Get("?banner=WEAPON&level=4");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo("max-age=3600"));
        Assert.That(body.EnumerateObject().Select(p => p.Name),
                    Is.EqualTo(new[] { "banner", "level", "contentVersion", "rateTableHash", "rates", "pity", "tenPullGuarantee", "effective", "items" }));
        var def = Db.Banners[Banner.WEAPON];
        var pity = body.GetProperty("pity");
        Assert.That(pity.GetProperty("softStart").GetInt32(), Is.EqualTo(def.PitySoftStart));
        Assert.That(pity.GetProperty("softStepPerPull").GetDouble(), Is.EqualTo(def.PitySoftStep));
        Assert.That(pity.GetProperty("hard").GetInt32(), Is.EqualTo(def.PityHard));
        Assert.That(pity.GetProperty("lplusAtSoftStart").GetDouble(), Is.EqualTo(def.BaseLplus));
        Assert.That(body.GetProperty("tenPullGuarantee").GetString(), Is.EqualTo("EPIC_OR_BETTER"));
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.That(items.Select(i => i.GetProperty("itemId").GetString()), Is.EqualTo(PublishedRates.Order.SelectMany(r => def.Pool[r])));
        Assert.That(items.Select(i => i.GetProperty("weight").GetInt32()), Is.All.EqualTo(1));
        Assert.That(items.Count(i => i.GetProperty("rarity").GetString() == "M"), Is.EqualTo(def.Pool[Rarity.MYTHIC].Count));

        var (defaulted, level1) = await Get("?banner=WEAPON");
        Assert.That(defaulted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(level1.GetProperty("level").GetInt32(), Is.EqualTo(1), "level defaults to 1");
    }

    /// <summary>The published hash is the hash the pull path stores on summon_log for a pull at that level (issue #200).</summary>
    [Test]
    public async Task The_published_hash_is_the_hash_a_pull_at_that_level_logs()
    {
        foreach (var def in Published)
            for (int level = 1; level <= def.Levels.Count; level++)
            {
                var (_, body) = await Get($"?banner={def.Id}&level={level}&contentVersion={Db.Version}");
                var st = new BannerState { Exp = level > 1 ? def.Levels[level - 1].ExpRequired - def.ExpPerPull : 0 };
                var roll = SummonEngine.Roll(Db, def, st, new CollectionState(), new ScriptedUniforms(new[] { 0.5, 0.5, 0.5 }), 1);
                Assert.That(roll.Audits[0].BannerLevel, Is.EqualTo(level));
                Assert.That(body.GetProperty("rateTableHash").GetString(), Is.EqualTo(RateTableHash.Hex(roll.Audits[0].RateTableHash)),
                            $"{def.Id} level {level}");
            }
    }

    /// <summary>The <c>effective</c> block is the model's (Fixtures/Summon/vectors.json, written by tools/gacha_model.py).</summary>
    [Test]
    public async Task Effective_is_what_the_gacha_model_printed()
    {
        var model = Vectors.Value.GetProperty("effective");
        Assert.That(model.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(Published.Select(d => d.Id.ToString())));
        foreach (var def in Published)
        {
            var want = model.GetProperty(def.Id.ToString());
            for (int level = 1; level <= def.Levels.Count; level++)
            {
                var (_, body) = await Get($"?banner={def.Id}&level={level}");
                var got = body.GetProperty("effective");
                Assert.That(got.GetProperty("lplusPerPullAveraged").GetDouble(),
                            Is.EqualTo(want.GetProperty("lplusPerPullAveraged").GetDouble()).Within(1e-12).Percent);
                Assert.That(got.GetProperty("expectedPullsToLplus").GetDouble(),
                            Is.EqualTo(want.GetProperty("expectedPullsToLplus").GetDouble()).Within(1e-12).Percent);
                Assert.That(got.GetProperty("p99PullsToLplus").GetInt32(), Is.EqualTo(want.GetProperty("p99PullsToLplus").GetInt32()));
            }
        }
        // 04 section 3.1's printed numbers.
        var w = PublishedRates.Effective(Db.Banners[Banner.WEAPON]);
        Assert.That(Math.Round(w.ExpectedPullsToLplus, 2), Is.EqualTo(27.35));
        Assert.That(Math.Round(w.LplusPerPullAveraged * 100, 3), Is.EqualTo(3.657));
        Assert.That(w.P99PullsToLplus, Is.EqualTo(62));
    }

    [TestCase("", HttpStatusCode.BadRequest, "VALIDATION")]
    [TestCase("?banner=weapon", HttpStatusCode.BadRequest, "VALIDATION")]
    [TestCase("?banner=WEAPON&level=0", HttpStatusCode.BadRequest, "VALIDATION")]
    [TestCase("?banner=WEAPON&level=11", HttpStatusCode.BadRequest, "VALIDATION")]
    [TestCase("?banner=RUNE", HttpStatusCode.NotFound, "NOT_FOUND")]
    [TestCase("?banner=WEAPON&event=evt_1", HttpStatusCode.NotFound, "NOT_FOUND")]
    [TestCase("?banner=WEAPON&contentVersion=2099.01.01", HttpStatusCode.BadRequest, "VALIDATION")]
    [TestCase("?banner=WEAPON&contentVersion=2000.01.01", HttpStatusCode.Conflict, "CONTENT_TOO_OLD")]
    public async Task Bad_queries_are_refused(string query, HttpStatusCode status, string code)
    {
        var (response, body) = await Get(query);
        Assert.That(response.StatusCode, Is.EqualTo(status), body.ToString());
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo(code));
    }

    [Test]
    public void Pull_probabilities_sum_to_one_from_every_pity_with_and_without_the_guarantee()
    {
        foreach (var def in Published)
            for (int level = 1; level <= def.Levels.Count; level++)
                for (int pity = 0; pity < def.PityHard; pity++)
                    foreach (bool g in new[] { false, true })
                    {
                        var p = PublishedRates.PullProbabilities(def, level, pity, g);
                        Assert.That(p.Values.Sum(), Is.EqualTo(1).Within(1e-12), $"{def.Id} {level} {pity} {g}");
                        Assert.That(p.Values, Is.All.GreaterThanOrEqualTo(0));
                        if (g) Assert.That(p[Rarity.COMMON] + p[Rarity.RARE], Is.Zero);
                    }
        var w = Db.Banners[Banner.WEAPON];
        var below = PublishedRates.PullProbabilities(w, 4, 0, false);
        foreach (var r in PublishedRates.Order)
            Assert.That(below[r], Is.EqualTo(w.Levels[3].Rates[r]).Within(1e-15), "below soft pity a pull rolls the table's rates");
        Assert.That(PublishedRates.PullProbabilities(w, 1, w.PityHard - 1, false)[Rarity.LEGENDARY] +
                    PublishedRates.PullProbabilities(w, 1, w.PityHard - 1, false)[Rarity.MYTHIC], Is.EqualTo(1), "hard pity");
    }

    /// <summary>The odds export (08 section 10.3): every level's endpoint body unchanged, per-item odds, the Japan figures.</summary>
    [Test]
    public void The_odds_export_carries_every_levels_endpoint_body_and_the_store_figures()
    {
        var doc = OddsExport.Build(Db);
        Assert.That(doc.ContentVersion, Is.EqualTo(Db.Version));
        Assert.That(doc.ContentHash, Is.EqualTo(Db.ContentHash));
        Assert.That(doc.Banners.Select(b => b.Banner), Is.EqualTo(Published.Select(d => d.Id.ToString())));
        foreach (var b in doc.Banners)
        {
            var def = Db.Banners[Enum.Parse<Banner>(b.Banner)];
            Assert.That(b.HardPity, Is.EqualTo(def.PityHard));
            Assert.That(b.HardPityCostDiamonds, Is.EqualTo((long)def.PityHard * def.PullCostDiamonds));
            Assert.That(b.ExpectedPullsToLplus, Is.EqualTo(PublishedRates.Effective(def).ExpectedPullsToLplus));
            Assert.That(b.Levels.Length, Is.EqualTo(def.Levels.Count));
            for (int level = 1; level <= def.Levels.Count; level++)
            {
                var l = b.Levels[level - 1];
                Assert.That(JsonSerializer.Serialize(l.Published), Is.EqualTo(JsonSerializer.Serialize(PublishedRates.Build(Db, def, level))));
                Assert.That(l.ItemOdds.Sum(o => o.Probability), Is.EqualTo(1).Within(1e-9), $"{b.Banner} level {level}");
                Assert.That(l.ItemOdds.Select(o => o.ItemId), Is.EqualTo(l.Published.Items.Select(i => i.ItemId)));
            }
        }
        string text = OddsExport.Serialize(doc);
        var root = JsonDocument.Parse(text).RootElement;
        Assert.That(root.GetProperty("banners")[0].GetProperty("levels")[0].GetProperty("published").GetProperty("rateTableHash").GetString(),
                    Has.Length.EqualTo(64));
    }

    [Test]
    public void The_export_odds_role_parses_and_takes_an_out_path()
    {
        var (role, rest) = ServerRoles.Parse(new[] { "--export-odds", "--out", "build/odds.json", "--HeroCat:Content:Directories:0=content" });
        Assert.That(role, Is.EqualTo(ServerRole.ExportOdds));
        Assert.That(ServerRoles.Name(role), Is.EqualTo("export-odds"));
        var (path, config) = OddsExport.ParseArgs(rest);
        Assert.That(path, Is.EqualTo("build/odds.json"));
        Assert.That(config, Is.EqualTo(new[] { "--HeroCat:Content:Directories:0=content" }));
        Assert.That(OddsExport.ParseArgs(new[] { "--out=x.json" }).outPath, Is.EqualTo("x.json"));
        Assert.Throws<ArgumentException>(() => OddsExport.ParseArgs(new[] { "--out" }));
        Assert.Throws<ArgumentException>(() => ServerRoles.Parse(new[] { "--export-odds", "--worker" }));
    }

    [Test]
    public async Task The_export_odds_role_writes_the_file()
    {
        string path = Path.Combine(Path.GetTempPath(), "herocat_odds_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            int code = await ServerHost.RunAsync(new[] { "--export-odds", "--out", path });
            Assert.That(code, Is.Zero);
            Assert.That(File.ReadAllText(path), Is.EqualTo(OddsExport.Serialize(OddsExport.Build(Db))));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
