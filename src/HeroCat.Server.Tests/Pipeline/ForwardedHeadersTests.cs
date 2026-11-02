using System.Net;
using System.Text.Json;
using HeroCat.Server.Hosting;
using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Pipeline;

/// <summary>
/// Which hop's address the per-IP rate limits see (specs/08 section 9.1, issue #263): only a peer inside
/// <c>HeroCat:Network:TrustedProxies</c> may report the client through <c>X-Forwarded-For</c>, and only its own last hop.
/// </summary>
[TestFixture]
public sealed class ForwardedHeadersTests
{
    const string Alb = "10.0.1.5";
    const string Outsider = "203.0.113.9";

    static readonly Dictionary<string, string?> TrustVpc = new() { ["HeroCat:Network:TrustedProxies:0"] = "10.0.0.0/16" };

    static async Task<(HttpStatusCode status, string? ip)> Peer(HttpClient client, string peer, string? forwardedFor)
    {
        var r = PipelineHost.Request(HttpMethod.Get, "/v1/_test/peer");
        r.Headers.Add(TestPeerStartupFilter.Header, peer);
        if (forwardedFor != null) r.Headers.Add("X-Forwarded-For", forwardedFor);
        using var resp = await client.SendAsync(r);
        if (resp.StatusCode != HttpStatusCode.OK) return (resp.StatusCode, null);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return (resp.StatusCode, body.GetProperty("ip").GetString());
    }

    [Test]
    public async Task With_no_trusted_proxy_a_forged_header_keeps_the_peer_address()
    {
        using var host = new PipelineHost();
        using var client = host.Client();
        var (_, ip) = await Peer(client, Outsider, "1.2.3.4");
        Assert.That(ip, Is.EqualTo(Outsider));
    }

    [Test]
    public async Task A_forged_header_from_an_untrusted_peer_keeps_the_peer_address()
    {
        using var host = new PipelineHost(TrustVpc);
        using var client = host.Client();
        var (_, ip) = await Peer(client, Outsider, "1.2.3.4");
        Assert.That(ip, Is.EqualTo(Outsider));
    }

    [Test]
    public async Task The_trusted_balancer_reports_the_client_and_only_its_own_hop_counts()
    {
        using var host = new PipelineHost(TrustVpc);
        using var client = host.Client();
        // A client forged "1.2.3.4"; the ALB appended the address it saw.
        var (_, ip) = await Peer(client, Alb, "1.2.3.4, 198.51.100.7");
        Assert.That(ip, Is.EqualTo("198.51.100.7"));
    }

    [Test]
    public async Task Per_ip_windows_behind_the_balancer_are_per_client_not_shared()
    {
        using var host = new PipelineHost(TrustVpc);
        using var client = host.Client();
        // /_test/peer allows 2/min per IP: two clients behind the same ALB each get their own window.
        for (int i = 0; i < 2; i++) Assert.That((await Peer(client, Alb, "198.51.100.7")).status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Peer(client, Alb, "198.51.100.7")).status, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That((await Peer(client, Alb, "198.51.100.8")).status, Is.EqualTo(HttpStatusCode.OK));
        // A client cannot pick a fresh window by forging the header: the ALB's entry is the last one.
        Assert.That((await Peer(client, Alb, "9.9.9.9, 198.51.100.7")).status, Is.EqualTo(HttpStatusCode.TooManyRequests));
    }

    [Test]
    public void Options_trust_only_the_configured_hops()
    {
        var none = new ForwardedHeadersOptions();
        ForwardedNetwork.Apply(none, new NetworkOptions());
        Assert.That(none.ForwardedHeaders, Is.EqualTo(ForwardedHeaders.None), "no trusted hop forwards nothing (the middleware would otherwise trust every peer)");
        Assert.That(none.KnownNetworks, Is.Empty);
        Assert.That(none.KnownProxies, Is.Empty, "the loopback default is dropped");

        var o = new ForwardedHeadersOptions();
        ForwardedNetwork.Apply(o, new NetworkOptions { TrustedProxies = { "10.0.0.0/16", "fd00::/8", "192.0.2.10" } });
        Assert.That(o.ForwardLimit, Is.EqualTo(1));
        Assert.That(o.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        Assert.That(o.KnownNetworks.Select(n => n.Prefix + "/" + n.PrefixLength), Is.EquivalentTo(new[] { "10.0.0.0/16", "fd00::/8" }));
        Assert.That(o.KnownProxies, Is.EquivalentTo(new[] { IPAddress.Parse("192.0.2.10") }));

        Assert.Throws<InvalidOperationException>(() => ForwardedNetwork.Apply(new ForwardedHeadersOptions(), new NetworkOptions { TrustedProxies = { "10.0.0.0/40" } }));
        Assert.Throws<InvalidOperationException>(() => ForwardedNetwork.Apply(new ForwardedHeadersOptions(), new NetworkOptions { TrustedProxies = { "alb" } }));
    }
}
