using System.Globalization;
using System.Text.RegularExpressions;

namespace HeroCat.Server.Pipeline;

/// <summary>
/// A client version, semver plus build (specs/08 section 6.2: <c>1.4.2+318</c>). Ordering is semver precedence: the
/// numeric core, then a pre-release sorts before its release; the build metadata is ignored.
/// </summary>
public sealed partial record ClientVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<ClientVersion>
{
    [GeneratedRegex(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string? s, out ClientVersion version)
    {
        version = null!;
        if (s == null) return false;
        var m = Pattern().Match(s.Trim());
        if (!m.Success) return false;
        version = new ClientVersion(Int(m.Groups[1].Value), Int(m.Groups[2].Value), Int(m.Groups[3].Value),
                                    m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

    public int CompareTo(ClientVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (PreRelease == null) return other.PreRelease == null ? 0 : 1;
        if (other.PreRelease == null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>semver 11.4: dot-separated identifiers, numeric ones numerically and below alphanumeric ones.</summary>
    static int ComparePreRelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xn = long.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out long xv);
            bool yn = long.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out long yv);
            int c = xn && yn ? xv.CompareTo(yv) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    public static bool operator <(ClientVersion a, ClientVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ClientVersion a, ClientVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ClientVersion a, ClientVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ClientVersion a, ClientVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => PreRelease == null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

/// <summary>A content version, <c>YYYY.MM.DD[.n]</c> (specs/08 section 6.2); a missing <c>.n</c> is 0.</summary>
public sealed partial record ContentVersion(int Year, int Month, int Day, int N) : IComparable<ContentVersion>
{
    [GeneratedRegex(@"^(\d{4})\.(\d{2})\.(\d{2})(?:\.(\d{1,9}))?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string? s, out ContentVersion version)
    {
        version = null!;
        if (s == null) return false;
        var m = Pattern().Match(s.Trim());
        if (!m.Success) return false;
        version = new ContentVersion(Int(m.Groups[1].Value), Int(m.Groups[2].Value), Int(m.Groups[3].Value),
                                     m.Groups[4].Success ? Int(m.Groups[4].Value) : 0);
        return true;
    }

    static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

    public int CompareTo(ContentVersion? other)
    {
        if (other is null) return 1;
        int c = Year.CompareTo(other.Year);
        if (c == 0) c = Month.CompareTo(other.Month);
        if (c == 0) c = Day.CompareTo(other.Day);
        if (c == 0) c = N.CompareTo(other.N);
        return c;
    }

    public static bool operator <(ContentVersion a, ContentVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ContentVersion a, ContentVersion b) => a.CompareTo(b) > 0;
}
