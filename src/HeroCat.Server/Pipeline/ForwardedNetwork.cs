using System.Globalization;
using System.Net;
using HeroCat.Server.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace HeroCat.Server.Pipeline;

/// <summary>
/// Which proxy hops may report the client address (specs/08 section 9.1, issue #263). The api runs behind the ALB, so
/// the connection's peer is the balancer; per-IP rate limits need the client address the balancer appends to
/// <c>X-Forwarded-For</c>. Only a peer inside <c>HeroCat:Network:TrustedProxies</c> is believed, and only for its own
/// (last) hop (<c>ForwardLimit = 1</c>), so an <c>X-Forwarded-For</c> a client forges is either ignored (untrusted peer)
/// or sits to the left of the entry the balancer appended. An empty list trusts nobody and forwards nothing.
/// </summary>
public static class ForwardedNetwork
{
    public static void Apply(ForwardedHeadersOptions o, NetworkOptions network)
    {
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
        o.ForwardLimit = 1;
        foreach (string entry in network.TrustedProxies)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (TryParseNetwork(entry, out var net)) o.KnownNetworks.Add(net);
            else if (IPAddress.TryParse(entry.Trim(), out var ip)) o.KnownProxies.Add(ip);
            else throw new InvalidOperationException($"HeroCat:Network:TrustedProxies: '{entry}' is neither a CIDR nor an IP address");
        }
        // The middleware trusts every peer when both lists are empty; with no trusted hop, forward nothing instead.
        o.ForwardedHeaders = o.KnownNetworks.Count + o.KnownProxies.Count == 0
            ? ForwardedHeaders.None
            : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    }

    static bool TryParseNetwork(string s, out IPNetwork network)
    {
        network = null!;
        int slash = s.IndexOf('/');
        if (slash < 0) return false;
        if (!IPAddress.TryParse(s[..slash].Trim(), out var prefix)) return false;
        if (!int.TryParse(s[(slash + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int length)) return false;
        int max = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (length > max) return false;
        network = new IPNetwork(prefix, length);
        return true;
    }
}
