using HeroCat.Server.Content;
using HeroCat.Server.Pipeline;
using HeroCat.Sim.Content;
using Microsoft.Extensions.Options;
using ContentVersion = HeroCat.Server.Pipeline.ContentVersion;

namespace HeroCat.Server.Features.Summon;

/// <summary>
/// <c>HeroCat:Content</c>: the content set directories the server rolls with (each a <c>manifest.json</c> plus tables).
/// Relative paths resolve against the app's base directory. Empty means the set shipped in the image (<c>content</c>).
/// </summary>
public sealed class ContentSetOptions
{
    public const string Section = "HeroCat:Content";
    public const string DefaultDirectory = "content";

    public List<string> Directories { get; set; } = new();
}

/// <summary>
/// The content sets the server holds, by manifest version (specs/08 section 4.3: "content = ContentDb for
/// req.clientContentVersion"). The pull uses exactly the client's set, so pools and rate tables match what the client
/// shows and applies. As built (issue #199): a version the server does not hold is <c>409 CONTENT_TOO_OLD</c>
/// (<c>details.minContentVersion</c> = the newest held) when it is older than the newest held set, else
/// <c>400 VALIDATION</c> (<c>details.reason = UNKNOWN_CONTENT_VERSION</c>).
/// </summary>
public interface IContentSets
{
    /// <summary>The newest held set (what <c>/summon/state</c> derives banner levels with).</summary>
    ContentDb Active { get; }

    ContentDb Resolve(string version);
}

/// <summary>Loads every configured directory once, on first use, with the MVP build tier.</summary>
public sealed class DirectoryContentSets : IContentSets
{
    public const string UnknownContentVersion = "UNKNOWN_CONTENT_VERSION";

    readonly Lazy<IReadOnlyList<ContentDb>> _sets;

    public DirectoryContentSets(IOptions<ContentSetOptions> options)
    {
        var dirs = options.Value.Directories.Count > 0 ? options.Value.Directories.ToList() : new List<string> { ContentSetOptions.DefaultDirectory };
        _sets = new Lazy<IReadOnlyList<ContentDb>>(() => Load(dirs));
    }

    public DirectoryContentSets(IEnumerable<ContentDb> sets)
    {
        var list = Order(sets.ToList());
        _sets = new Lazy<IReadOnlyList<ContentDb>>(() => list);
    }

    public ContentDb Active => _sets.Value[^1];

    public ContentDb Resolve(string version)
    {
        foreach (var db in _sets.Value)
            if (string.Equals(db.Version, version, StringComparison.Ordinal)) return db;
        if (!ContentVersion.TryParse(version, out var asked))
            throw ApiException.Of(ErrorCodes.VALIDATION, "clientContentVersion is not a content version.", new { field = "clientContentVersion" });
        ContentVersion.TryParse(Active.Version, out var newest);
        if (newest != null && asked < newest)
            throw ApiException.Of(ErrorCodes.CONTENT_TOO_OLD, details: new { minContentVersion = Active.Version });
        throw ApiException.Of(ErrorCodes.VALIDATION, "This content version is not published.",
                              new { field = "clientContentVersion", reason = UnknownContentVersion });
    }

    static IReadOnlyList<ContentDb> Load(List<string> dirs)
    {
        var sets = new List<ContentDb>();
        foreach (string d in dirs)
        {
            string dir = Path.IsPathRooted(d) ? d : Path.Combine(AppContext.BaseDirectory, d);
            sets.Add(ContentLoader.LoadFromDirectory(dir, Tier.MVP));
        }
        return Order(sets);
    }

    static IReadOnlyList<ContentDb> Order(List<ContentDb> sets)
    {
        if (sets.Count == 0) throw new InvalidOperationException("no content set configured");
        foreach (var s in sets)
            if (!ContentVersion.TryParse(s.Version, out _)) throw new InvalidOperationException($"content version '{s.Version}' does not parse");
        sets.Sort((a, b) => Parse(a).CompareTo(Parse(b)));
        return sets;
    }

    static ContentVersion Parse(ContentDb db)
    {
        ContentVersion.TryParse(db.Version, out var v);
        return v!;
    }
}
