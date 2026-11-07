using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace HeroCat.Server.Observability;

/// <summary>
/// The redaction filter of specs/08 section 9.4: never logged are tokens, receipts, HMAC keys, display names and IP
/// addresses beyond <c>/24</c> (IPv6 beyond <c>/48</c>). Every structured value passes <see cref="Value"/> (by its name,
/// then by its content) and every rendered message and exception text passes <see cref="Scrub"/>, so a secret that
/// reaches a log call by mistake (a header, a request body, an exception message) still never reaches the log line.
/// </summary>
public static partial class LogRedactor
{
    public const string Redacted = "[redacted]";

    /// <summary>Name fragments (lowercase, separators removed) whose values are never logged.</summary>
    static readonly string[] SecretNameParts =
    {
        "token", "authorization", "bearer", "cookie", "password", "secret", "credential",
        "receipt", "jws", "signedpayload", "signedtransaction", "signature",
        "hmac", "devicekey", "privatekey", "signingkey", "datakey", "recoverykey", "apikey", "pem",
        "displayname", "nickname", "playername",
    };

    /// <summary>True when a value under this name must never be logged.</summary>
    public static bool IsSecretName(string name)
    {
        string n = Normalize(name);
        foreach (string part in SecretNameParts)
            if (n.Contains(part, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True when the name holds an IP address (<c>ip</c>, <c>clientIp</c>, <c>RemoteIpAddress</c>, <c>X-Forwarded-For</c>).</summary>
    public static bool IsIpName(string name)
    {
        string n = Normalize(name);
        return n == "ip" || n.EndsWith("ip", StringComparison.Ordinal) && name.Length > 2 && (name[^2] == 'I' || name[^3] is '_' or '-' or '.')
            || n.Contains("ipaddress", StringComparison.Ordinal) || n.Contains("forwardedfor", StringComparison.Ordinal)
            || n.Contains("remoteaddr", StringComparison.Ordinal);
    }

    static string Normalize(string name)
    {
        Span<char> buf = stackalloc char[Math.Min(name.Length, 128)];
        int len = 0;
        foreach (char ch in name)
        {
            if (len == buf.Length) break;
            if (char.IsLetterOrDigit(ch)) buf[len++] = char.ToLowerInvariant(ch);
        }
        return new string(buf[..len]);
    }

    /// <summary>The loggable form of a structured value: redacted by name, IPs truncated, strings scrubbed.</summary>
    public static object? Value(string name, object? value)
    {
        if (value == null) return null;
        if (IsSecretName(name)) return Redacted;
        if (value is IPAddress ip) return Truncate(ip);
        if (IsIpName(name))
        {
            string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            return string.Join(", ", s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                      .Select(part => IPAddress.TryParse(part, out var p) ? Truncate(p) : Redacted));
        }
        return value switch
        {
            string s => Scrub(s),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            Guid or DateTime or DateTimeOffset or TimeSpan or Enum => value,
            _ => Scrub(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
        };
    }

    /// <summary>An address cut to its <c>/24</c> (IPv4) or <c>/48</c> (IPv6) network, written with the prefix length.</summary>
    public static string Truncate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        byte[] b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            b[3] = 0;
            return new IPAddress(b) + "/24";
        }
        for (int i = 6; i < b.Length; i++) b[i] = 0;
        return new IPAddress(b) + "/48";
    }

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----[\s\S]*?(-----END [A-Z0-9 ]+-----|$)")]
    private static partial Regex Pem();

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex AuthScheme();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]{2,}(\.[A-Za-z0-9_-]*)?")]
    private static partial Regex Jwt();

    /// <summary><c>name=value</c>, <c>name: value</c> and JSON <c>"name":"value"</c> pairs whose name is a secret.</summary>
    [GeneratedRegex(@"(?i)(""?[A-Za-z0-9_-]*(token|authorization|receipt|jws|signedpayload|signedtransaction|signature|hmac|password|secret|apikey|displayname|nickname)[A-Za-z0-9_-]*""?\s*[:=]\s*)((?i:bearer|basic)\s+[^\s,&;}\]]+|""(?:[^""\\]|\\.)*""|[^\s,&;}\]]+)")]
    private static partial Regex SecretPair();

    /// <summary>A long opaque run (a store purchase token, a base64 receipt, a key). Plain hex digests and ids are spared.</summary>
    [GeneratedRegex(@"[A-Za-z0-9+/=_.-]{40,}")]
    private static partial Regex Opaque();

    [GeneratedRegex(@"^[0-9a-fA-F-]+$")]
    private static partial Regex HexOnly();

    [GeneratedRegex(@"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d./])")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?<![\w:.])(?=[0-9A-Fa-f]*:[0-9A-Fa-f]*:)[0-9A-Fa-f:.]{2,45}(?![\w:/])")]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_`]{0,31}(\.[A-Za-z_<][A-Za-z0-9_`<>]{0,31})+$")]
    private static partial Regex DottedName();

    /// <summary>
    /// A long run that is not a secret: a hex digest or id, a dotted type or member name (stack traces), or a path
    /// (has <c>/</c> but none of base64's <c>+</c> or trailing <c>=</c>).
    /// </summary>
    static bool LooksHarmless(string run) =>
        HexOnly().IsMatch(run) || DottedName().IsMatch(run)
        || run.Contains('/') && !run.Contains('+') && !run.EndsWith('=') && run.Split('/').All(seg => seg.Length < 40);

    /// <summary>Removes secrets and truncates IP addresses in free text.</summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string s = Pem().Replace(text, Redacted);
        s = SecretPair().Replace(s, m =>
        {
            string v = m.Groups[3].Value;
            if (v.StartsWith("[redacted", StringComparison.Ordinal) || v.StartsWith("\"[redacted", StringComparison.Ordinal)) return m.Value;
            return m.Groups[1].Value + (v.StartsWith('"') ? "\"" + Redacted + "\"" : Redacted);
        });
        s = AuthScheme().Replace(s, m => m.Groups[1].Value + " " + Redacted);
        s = Jwt().Replace(s, Redacted);
        s = Opaque().Replace(s, m => LooksHarmless(m.Value) ? m.Value : Redacted);
        s = Ipv4().Replace(s, m =>
        {
            for (int g = 1; g <= 4; g++)
                if (int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) > 255) return m.Value;
            return $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}.0/24";
        });
        s = Ipv6().Replace(s, m => IPAddress.TryParse(m.Value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? Truncate(ip) : m.Value);
        return s;
    }
}
