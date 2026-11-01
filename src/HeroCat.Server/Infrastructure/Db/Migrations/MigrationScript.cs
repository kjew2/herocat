using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HeroCat.Server.Infrastructure.Db.Migrations;

/// <summary>
/// One file of the SQL migration set: <c>server/db/migrations/NNNN_&lt;name&gt;.sql</c> (specs/02 section 12, issue #33).
/// The checksum is the SHA-256 of the file's bytes; a file is immutable once merged, so a changed checksum of an applied
/// version stops the runner.
/// </summary>
public sealed record MigrationScript(int Version, string Name, string Sql, string Checksum)
{
    static readonly Regex FilePattern = new(@"^(?<v>\d{4})_(?<n>[a-z0-9_]+)\.sql$", RegexOptions.CultureInvariant);

    public string FileName => $"{Version:D4}_{Name}.sql";

    public static MigrationScript FromBytes(string fileName, byte[] bytes)
    {
        var m = FilePattern.Match(fileName);
        if (!m.Success)
            throw new MigrationException($"{fileName}: migration files are named NNNN_<lower_snake_name>.sql");
        int version = int.Parse(m.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (version == 0) throw new MigrationException($"{fileName}: versions start at 0001");
        string sql = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart((char)0xFEFF);
        return new MigrationScript(version, m.Groups["n"].Value, sql, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>
    /// Every <c>*.sql</c> in <paramref name="directory"/>, ordered by version. A missing directory, a misnamed .sql file
    /// or two files with one version are errors (a silently empty or ambiguous set must never reach a database).
    /// </summary>
    public static IReadOnlyList<MigrationScript> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory)) throw new MigrationException($"migration directory not found: {directory}");
        var scripts = Directory.GetFiles(directory, "*.sql")
            .Select(p => FromBytes(Path.GetFileName(p), File.ReadAllBytes(p)))
            .ToList();
        return Ordered(scripts);
    }

    public static IReadOnlyList<MigrationScript> Ordered(IEnumerable<MigrationScript> scripts)
    {
        var list = scripts.OrderBy(s => s.Version).ToList();
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].Version == list[i - 1].Version)
                throw new MigrationException($"two migrations share version {list[i].Version:D4}: {list[i - 1].FileName} and {list[i].FileName}");
        }
        return list;
    }
}

/// <summary>A migration set or journal problem that must stop a deploy.</summary>
public sealed class MigrationException : Exception
{
    public MigrationException(string message) : base(message) { }
}
