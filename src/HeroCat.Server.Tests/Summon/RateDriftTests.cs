using System.Globalization;
using System.Text.RegularExpressions;
using HeroCat.Server.Content;
using HeroCat.Server.Features.Summon;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Summon;

/// <summary>
/// The realised-rate job's check (specs/08 section 10.3, acceptance 22; issue #200), on simulated stage traffic: pulls
/// rolled through the server's own pull path (<see cref="SummonEngine"/>) and grouped as the job's query groups
/// <c>summon_log</c>. Honest tables stay inside the Wilson interval at every banner level and rarity, pity and guarantee
/// included; a mis-set <c>banner_levels</c> row raises <c>RB-SUMMON-RATES</c> in one run.
/// </summary>
[TestFixture]
public sealed class RateDriftTests
{
    static readonly string ContentDir = Path.Combine(AppContext.BaseDirectory, ContentSetOptions.DefaultDirectory);
    static readonly Lazy<ContentDb> Db = new(() => ContentLoader.LoadFromDirectory(ContentDir, Tier.MVP));
    static readonly DateTimeOffset To = DateTimeOffset.FromUnixTimeSeconds(1_793_670_900);
    static readonly DateTimeOffset From = To - RateDrift.Window;

    /// <summary>The shipped content with WEAPON level 1 mis-set: 5 pp moved from R to C (still sums to 1 and passes 09's rules).</summary>
    static readonly Lazy<ContentDb> MisSet = new(() =>
    {
        var dtos = ContentLoader.LoadDtos(new FileContentSource(ContentDir));
        var row = dtos.BannerLevels.Single(r => r.Banner == "WEAPON" && r.Level == 1);
        row.Rates["COMMON"] += 0.05;
        row.Rates["RARE"] -= 0.05;
        return ContentDb.Build(dtos, Tier.MVP);
    });

    /// <summary>
    /// Thirty days of stage traffic on <paramref name="rolled"/>'s banners: <paramref name="players"/> players, each with
    /// a mix of 10-pulls and single pulls, grouped by banner, level, logged hash, pity, guarantee and rarity.
    /// <paramref name="logged"/> is the table whose hash the log records (a server that rolls one table and logs another).
    /// </summary>
    static List<RealisedPulls> StageTraffic(ContentDb rolled, ContentDb logged, int players, int pullsPerPlayer, ulong seed)
    {
        var groups = new Dictionary<(string, int, string, int, bool, string), (byte[] hash, long n)>();
        var rng = new StreamUniformSource(seed, 0);
        foreach (var def in rolled.Banners.Values.Where(d => d.Levels.Count > 0))
        {
            var logDef = logged.Banners[def.Id];
            for (int p = 0; p < players; p++)
            {
                var st = new BannerState();
                var col = new CollectionState();
                int done = 0;
                while (done < pullsPerPlayer)
                {
                    int count = rng.Next() < 0.7 ? def.GuaranteeBatchSize : 1;
                    var roll = SummonEngine.Roll(rolled, def, st, col, rng, count);
                    for (int i = 0; i < roll.Results.Length; i++)
                    {
                        var a = roll.Audits[i];
                        byte[] hash = ReferenceEquals(rolled, logged) ? a.RateTableHash : RateTableHash.Of(logDef, a.BannerLevel);
                        var key = (def.Id.ToString(), a.BannerLevel, RateTableHash.Hex(hash), a.PityBefore, a.Guarantee, RarityCodes.Of(roll.Results[i].Rarity));
                        groups[key] = (hash, groups.TryGetValue(key, out var g) ? g.n + 1 : 1);
                    }
                    done += roll.Results.Length;
                }
            }
        }
        return groups.Select(kv => new RealisedPulls(kv.Key.Item1, kv.Key.Item2, kv.Value.hash, kv.Key.Item4, kv.Key.Item5, kv.Key.Item6, kv.Value.n))
                     .ToList();
    }

    static IReadOnlyDictionary<string, (BannerDef Def, int Level)> Held => RateDrift.TableIndex(new[] { Db.Value });

    [Test]
    public void Thirty_days_of_honest_stage_traffic_stay_inside_every_interval()
    {
        var traffic = StageTraffic(Db.Value, Db.Value, players: 200, pullsPerPlayer: 900, seed: 0x5EED_0200);
        var report = RateDrift.Evaluate(traffic, Held, From, To);

        Assert.That(report.UnknownTables, Is.Empty);
        Assert.That(report.Z, Is.EqualTo(3));
        var levels = report.Rarities.Select(r => (r.Banner, r.Level)).Distinct().ToList();
        Assert.That(levels.Count, Is.EqualTo(30), "every level of the three banners was reached");
        Assert.That(report.Rarities.Count, Is.EqualTo(150));
        foreach (var g in report.Rarities.GroupBy(r => (r.Banner, r.Level)))
            Assert.That(g.Sum(r => r.Observed), Is.EqualTo(g.First().Pulls), "every pull lands on one rarity");
        Assert.That(report.Drifted.Select(d => $"{d.Banner} {d.Level} {d.Rarity}: {d.Expected:R} not in [{d.WilsonLow:R}, {d.WilsonHigh:R}]"), Is.Empty);
        Assert.That(report.Alarm, Is.False);

        // The pity ramp and the guarantee are in the published rate, not drift: L+ over the window is near the pity
        // cycle's average, well above level 1's table rate.
        var wl1 = report.Rarities.Where(r => r.Banner == "WEAPON" && r.Level == 1).ToDictionary(r => r.Rarity);
        double lplusTable = Db.Value.Banners[Banner.WEAPON].BaseLplus;
        Assert.That(wl1["L"].Expected + wl1["M"].Expected, Is.GreaterThan(lplusTable));
    }

    [Test]
    public void A_mis_set_row_rolled_under_the_published_hash_raises_the_alarm_in_one_run()
    {
        var traffic = StageTraffic(MisSet.Value, Db.Value, players: 200, pullsPerPlayer: 900, seed: 0x5EED_0201);
        var report = RateDrift.Evaluate(traffic, Held, From, To);

        Assert.That(report.Alarm, Is.True);
        Assert.That(report.UnknownTables, Is.Empty, "the log claims the published table");
        var drifted = report.Drifted.Select(d => (d.Banner, d.Level, d.Rarity)).ToList();
        Assert.That(drifted, Does.Contain(("WEAPON", 1, "C")).And.Contain(("WEAPON", 1, "R")));
        Assert.That(drifted.All(d => d.Banner == "WEAPON" && d.Level == 1), Is.True, string.Join(", ", drifted));
    }

    [Test]
    public void A_mis_set_row_logged_under_its_own_hash_is_an_unknown_table()
    {
        var traffic = StageTraffic(MisSet.Value, MisSet.Value, players: 50, pullsPerPlayer: 100, seed: 0x5EED_0202);
        var report = RateDrift.Evaluate(traffic, Held, From, To);

        Assert.That(report.Alarm, Is.True);
        var unknown = report.UnknownTables.Single();
        Assert.That((unknown.Banner, unknown.Level), Is.EqualTo(("WEAPON", 1)));
        Assert.That(unknown.RateTableHash, Is.EqualTo(RateTableHash.Hex(RateTableHash.Of(MisSet.Value.Banners[Banner.WEAPON], 1))));
        Assert.That(unknown.Pulls, Is.GreaterThan(0));
        Assert.That(report.Rarities.Any(r => r.Banner == "WEAPON" && r.Level == 1), Is.False, "unknown-table pulls are not compared");
    }

    [Test]
    public void A_hash_logged_under_another_level_is_an_unknown_table()
    {
        var w = Db.Value.Banners[Banner.WEAPON];
        var rows = new[] { new RealisedPulls("WEAPON", 2, RateTableHash.Of(w, 1), 0, false, "C", 5) };
        var report = RateDrift.Evaluate(rows, Held, From, To);
        Assert.That(report.UnknownTables.Single().Pulls, Is.EqualTo(5));
    }

    [Test]
    public void An_empty_window_reports_nothing_and_does_not_alarm()
    {
        var report = RateDrift.Evaluate(Array.Empty<RealisedPulls>(), Held, From, To);
        Assert.That(report.Rarities, Is.Empty);
        Assert.That(report.Alarm, Is.False);
    }

    [Test]
    public void The_wilson_interval_matches_known_values()
    {
        var (lo, hi) = RateDrift.Wilson(30, 1000, 3);
        // p = 0.03, n = 1000, z = 3: center (0.03 + 0.0045) / 1.009, half 3 sqrt(0.0000291 + 0.00000225) / 1.009.
        Assert.That(lo, Is.EqualTo((0.0345 - 3 * Math.Sqrt(0.03 * 0.97 / 1000 + 9.0 / 4e6)) / 1.009).Within(1e-15));
        Assert.That(hi, Is.EqualTo((0.0345 + 3 * Math.Sqrt(0.03 * 0.97 / 1000 + 9.0 / 4e6)) / 1.009).Within(1e-15));
        Assert.That(RateDrift.Wilson(0, 100, 3).Low, Is.Zero);
        Assert.That(RateDrift.Wilson(100, 100, 3).High, Is.EqualTo(1).Within(1e-15));
        Assert.That(RateDrift.Wilson(0, 0, 3), Is.EqualTo((0.0, 1.0)));
    }

    /// <summary>The job's z is specs/10's <c>ODDS_ALARM_Z</c>.</summary>
    [Test]
    public void The_z_is_the_specs_odds_alarm_z()
    {
        string spec = File.ReadAllText(RepoPaths.Of("specs/10-testing-release-and-ops.md"));
        var m = Regex.Match(spec, @"^\| `ODDS_ALARM_Z` \| (\d+(?:\.\d+)?)", RegexOptions.Multiline);
        Assert.That(m.Success, "specs/10 has an ODDS_ALARM_Z constants row");
        Assert.That(RateDrift.OddsAlarmZ, Is.EqualTo(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
    }

    [Test]
    public void The_log_alarm_sink_reports_both_outcomes()
    {
        var sink = new LogRateDriftAlarm(Microsoft.Extensions.Logging.Abstractions.NullLogger<LogRateDriftAlarm>.Instance);
        var quiet = RateDrift.Evaluate(Array.Empty<RealisedPulls>(), Held, From, To);
        var loud = RateDrift.Evaluate(new[] { new RealisedPulls("WEAPON", 1, new byte[32], 0, false, "C", 1) }, Held, From, To);
        Assert.That(loud.Alarm, Is.True);
        Assert.DoesNotThrow(() => sink.Report(quiet));
        Assert.DoesNotThrow(() => sink.Report(loud));
    }
}
