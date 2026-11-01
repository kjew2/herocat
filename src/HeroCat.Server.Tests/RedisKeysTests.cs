using HeroCat.Server.Infrastructure.Redis;
using NUnit.Framework;

namespace HeroCat.Server.Tests;

/// <summary>RedisKeys against the key table of specs/02 section 12 (issue #154).</summary>
[TestFixture]
public sealed class RedisKeysTests
{
    static readonly Guid Player = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    static List<string[]> SpecRows()
    {
        string spec = File.ReadAllText(RepoPaths.Of("specs/02-domain-model-and-persistence.md"));
        int at = spec.IndexOf("Redis structures", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), "specs/02 section 12 has no Redis table");
        var rows = new List<string[]>();
        foreach (string line in spec[at..].Split('\n').Skip(1))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) break;
            if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells[0] == "Key" || cells[0].StartsWith("---", StringComparison.Ordinal)) continue;
            rows.Add(cells);
        }
        return rows;
    }

    static TimeSpan? Ttl(string text) => text switch
    {
        "none" or "window length" => null,
        "14 days" => TimeSpan.FromDays(14),
        "1 day" => TimeSpan.FromDays(1),
        "1 h" => TimeSpan.FromHours(1),
        "10 s" => TimeSpan.FromSeconds(10),
        _ => throw new FormatException("unknown TTL in specs/02's Redis table, extend this test: " + text),
    };

    [Test]
    public void Patterns_types_and_ttls_are_the_spec_table()
    {
        var expected = SpecRows().Select(r => new RedisKeySpec(r[0].Trim('`'), r[1].Split(' ')[0], Ttl(r[3]), r[3] == "window length")).ToList();
        Assert.That(expected, Is.Not.Empty);
        Assert.That(RedisKeys.Patterns, Is.EqualTo(expected));
    }

    [Test]
    public void Builders_fill_the_patterns()
    {
        string id = "0f8fad5b-d9cb-469f-a165-70867728950e";
        Assert.That(RedisKeys.StageBoard(2915), Is.EqualTo("lb:stage:2915"));
        Assert.That(RedisKeys.FishBoard(-1), Is.EqualTo("lb:fish:-1"));
        Assert.That(RedisKeys.BoardName(Player), Is.EqualTo("lb:name:" + id));
        Assert.That(RedisKeys.RateLimit("summon/pull", Player), Is.EqualTo("rl:summon/pull:" + id));
        Assert.That(RedisKeys.Session(Player), Is.EqualTo("sess:" + id));
        Assert.That(RedisKeys.SummonLock(Player), Is.EqualTo("summon:lock:" + id));
        Assert.That(RedisKeys.Maintenance, Is.EqualTo("maint"));
        Assert.That(RedisKeys.RateLimitTtl(TimeSpan.FromMinutes(1)), Is.EqualTo(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public void Every_builder_matches_its_pattern()
    {
        string[] built =
        {
            RedisKeys.StageBoard(1), RedisKeys.FishBoard(1), RedisKeys.BoardName(Player), RedisKeys.RateLimit("save", Player),
            RedisKeys.Session(Player), RedisKeys.SummonLock(Player), RedisKeys.Maintenance,
        };
        for (int i = 0; i < built.Length; i++)
        {
            string regex = "^" + System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Escape(RedisKeys.Patterns[i].Pattern), @"\\\{\w+}", "[^:]+") + "$";
            Assert.That(built[i], Does.Match(regex), RedisKeys.Patterns[i].Pattern);
        }
    }

    [TestCase("")]
    [TestCase("a b")]
    [TestCase("{route}")]
    public void A_route_that_is_not_a_key_segment_is_rejected(string route) =>
        Assert.Throws<ArgumentException>(() => RedisKeys.RateLimit(route, Player));
}
