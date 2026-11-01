using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HeroCat.Server.Hosting;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace HeroCat.Server.Tests;

/// <summary>
/// appsettings.json's <c>HeroCat:Limits</c> holds the engineering constants of specs/08 "Constants introduced", keyed by
/// their spec names, with the spec's values (the numbers of a compound value in the order the spec writes them).
/// Changing either side alone fails here, so code and spec cannot drift apart.
/// </summary>
[TestFixture]
public sealed class ServerConstantsTests
{
    /// <summary>Table rows with a non-UPPER_SNAKE name (prose names) map to a config key here.</summary>
    static readonly Dictionary<string, string> ProseNames = new()
    {
        ["Age of digital consent default"] = "AGE_OF_DIGITAL_CONSENT_DEFAULT",
    };

    /// <summary>Rows whose values belong to their endpoints' issues, not to appsettings yet.</summary>
    static readonly HashSet<string> NotInAppsettings = new() { "Rate limits", "Alarm thresholds" };

    static Dictionary<string, List<double>> SpecTable()
    {
        string spec = File.ReadAllText(RepoPaths.Of("specs/08-backend-and-live-ops.md"));
        int at = spec.IndexOf("## Constants introduced", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), "specs/08 has no 'Constants introduced' section");
        var rows = new Dictionary<string, List<double>>();
        foreach (string line in spec[at..].Split('\n').Skip(1))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) break;
            if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 2 || cells[0] == "Name" || cells[0].StartsWith("---", StringComparison.Ordinal)) continue;
            string name = cells[0].Trim('`');
            if (NotInAppsettings.Contains(name)) continue;
            if (ProseNames.TryGetValue(name, out var key)) name = key;
            var numbers = Regex.Matches(cells[1], @"\d+(?:\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            rows[name] = numbers;
        }
        return rows;
    }

    static Dictionary<string, List<double>> AppsettingsLimits()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoPaths.Of("src/HeroCat.Server/appsettings.json")));
        var limits = doc.RootElement.GetProperty("HeroCat").GetProperty("Limits");
        var d = new Dictionary<string, List<double>>();
        foreach (var p in limits.EnumerateObject())
        {
            d[p.Name] = p.Value.ValueKind == JsonValueKind.Object
                ? p.Value.EnumerateObject().Select(x => x.Value.GetDouble()).ToList()
                : new List<double> { p.Value.GetDouble() };
        }
        return d;
    }

    [Test]
    public void Spec_table_parses_to_the_known_rows()
    {
        var spec = SpecTable();
        Assert.That(spec.Keys, Does.Contain("IDEMPOTENCY_TTL_H"));
        Assert.That(spec.Count, Is.GreaterThanOrEqualTo(17));
        Assert.That(spec.Values.All(v => v.Count > 0), "every row has a number");
    }

    [Test]
    public void Appsettings_has_every_spec_constant_with_the_spec_value()
    {
        var spec = SpecTable();
        var app = AppsettingsLimits();
        Assert.Multiple(() =>
        {
            foreach (var (name, values) in spec)
            {
                Assert.That(app.ContainsKey(name), $"appsettings.json HeroCat:Limits has no {name} (specs/08 'Constants introduced')");
                if (app.TryGetValue(name, out var have))
                    Assert.That(have, Is.EqualTo(values), $"{name}: appsettings {string.Join("/", have)} vs spec {string.Join("/", values)}");
            }
            foreach (string name in app.Keys)
                Assert.That(spec.ContainsKey(name), $"appsettings.json HeroCat:Limits {name} is not in specs/08 'Constants introduced'");
        });
    }

    [Test]
    public void Appsettings_binds_every_limit_to_ServerOptions()
    {
        var config = new ConfigurationBuilder().AddJsonFile(RepoPaths.Of("src/HeroCat.Server/appsettings.json")).Build();
        var o = new ServerOptions();
        config.GetSection(ServerOptions.Section).Bind(o);
        // A key the binder cannot match (a typo in appsettings or in a ConfigurationKeyName) leaves its property at 0;
        // every constant in the spec table is non-zero. (ErrorOnUnknownConfiguration ignores ConfigurationKeyName in .NET 8.)
        foreach (var (path, value) in Leaves(o.Limits, "Limits"))
            Assert.That(value, Is.Not.EqualTo(0.0), $"HeroCat:{path} did not bind");
        Assert.Multiple(() =>
        {
            Assert.That(o.Limits.IdempotencyTtlHours, Is.EqualTo(24));
            Assert.That(o.Limits.AccessTokenTtlSeconds, Is.EqualTo(3600));
            Assert.That(o.Limits.SummonLockTtlSeconds, Is.EqualTo(10));
            Assert.That(o.Limits.StageBoundMult.Suspicious, Is.EqualTo(1.5));
            Assert.That(o.Limits.StageBoundMult.Certain, Is.EqualTo(3));
            Assert.That(o.Limits.RefundAbuseCount.WindowDays, Is.EqualTo(90));
            Assert.That(o.Limits.SaveBlobMaxBytes, Is.EqualTo(262144));
            Assert.That(o.Database.MigrateOnStartup, Is.False);
            Assert.That(o.Blobs.Provider, Is.EqualTo(BlobProviders.S3));
        });
    }

    static IEnumerable<(string path, double value)> Leaves(object o, string prefix)
    {
        foreach (var p in o.GetType().GetProperties())
        {
            object v = p.GetValue(o)!;
            if (v is int i) yield return ($"{prefix}:{p.Name}", i);
            else if (v is double d) yield return ($"{prefix}:{p.Name}", d);
            else foreach (var leaf in Leaves(v, $"{prefix}:{p.Name}")) yield return leaf;
        }
    }

    [Test]
    public void Appsettings_holds_no_connection_secret()
    {
        // specs/08 section 9.3: no secret in an image layer; connection strings come from the environment.
        foreach (string file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            string text = File.ReadAllText(RepoPaths.Of("src/HeroCat.Server/" + file));
            Assert.That(text, Does.Not.Contain("Password=").IgnoreCase, file);
        }
    }
}
