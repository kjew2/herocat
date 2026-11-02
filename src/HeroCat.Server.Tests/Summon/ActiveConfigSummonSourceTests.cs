using System.Text.Json.Nodes;
using HeroCat.Server.Features.Config;
using HeroCat.Server.Features.Summon;
using HeroCat.Sim.Content;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Summon;

/// <summary>The summon handler reads the active config's kill switch and banner schedule (specs/08 section 6.1, acceptance 17).</summary>
public class ActiveConfigSummonSourceTests
{
    static ActiveConfig Config(string json) => new(1, JsonNode.Parse(json)!.AsObject());

    [Test]
    public void A_per_banner_kill_switch_disables_only_that_banner()
    {
        var s = ActiveConfigSummonSource.From(Config("""{ "killSwitches": { "summonEnabled": { "WEAPON": false, "SKILL": true } } }"""));
        Assert.That(s.IsEnabled(Banner.WEAPON), Is.False);
        Assert.That(s.IsEnabled(Banner.SKILL), Is.True);
        Assert.That(s.IsEnabled(Banner.COMPANION), Is.True, "absent means enabled");
    }

    [Test]
    public void A_single_bool_kill_switch_covers_every_banner()
    {
        var s = ActiveConfigSummonSource.From(Config("""{ "killSwitches": { "summonEnabled": false } }"""));
        foreach (Banner b in Enum.GetValues<Banner>()) Assert.That(s.IsEnabled(b), Is.False, b.ToString());
    }

    [Test]
    public void The_banners_schedule_restricts_only_the_banners_it_lists()
    {
        var s = ActiveConfigSummonSource.From(Config(
            """{ "banners": [ { "banner": "WEAPON", "openFrom": 0, "openUntil": null }, { "banner": "COMPANION", "openFrom": 1000, "openUntil": 2000 }, { "banner": "NOPE", "openFrom": 0 } ] }"""));
        Assert.That(s.IsOpen(Banner.WEAPON, 5), Is.True);
        Assert.That(s.IsOpen(Banner.COMPANION, 999), Is.False);
        Assert.That(s.IsOpen(Banner.COMPANION, 1500), Is.True);
        Assert.That(s.IsOpen(Banner.COMPANION, 2000), Is.False);
        Assert.That(s.IsOpen(Banner.SKILL, 5), Is.True, "an unlisted banner is open");
    }

    [Test]
    public void The_api_host_wires_the_config_source_over_the_default()
    {
        var services = new ServiceCollection().AddSummonFeature().AddConfigFeature();
        var d = services.Last(x => x.ServiceType == typeof(ISummonConfigSource));
        Assert.That(d.ImplementationType, Is.EqualTo(typeof(ActiveConfigSummonSource)));
    }
}
