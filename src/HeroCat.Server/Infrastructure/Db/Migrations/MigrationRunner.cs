using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeroCat.Server.Infrastructure.Db.Migrations;

/// <summary>A version recorded in <c>schema_migrations</c>.</summary>
public sealed record AppliedMigration(int Version, string Name, string Checksum);

/// <summary>
/// Where the runner records what it applied. <see cref="NpgsqlMigrationJournal"/> is the real one; tests use an
/// in-memory journal so the runner's rules are covered without a database.
/// </summary>
public interface IMigrationJournal
{
    /// <summary>Held for the whole run, so two runners (two deploys, or api replicas with MigrateOnStartup) never interleave.</summary>
    Task<IAsyncDisposable> LockAsync(CancellationToken ct);

    /// <summary>Creates the journal table if it does not exist.</summary>
    Task EnsureCreatedAsync(CancellationToken ct);

    Task<IReadOnlyList<AppliedMigration>> AppliedAsync(CancellationToken ct);

    /// <summary>Runs the script and records it, atomically: a failing script leaves no journal row.</summary>
    Task ApplyAsync(MigrationScript script, CancellationToken ct);
}

/// <summary>
/// The small in-house runner of specs/02 section 12 and 00 decision 65 (plain versioned SQL, no EF Core migrations):
/// applies pending files in version order and records each in <c>schema_migrations</c>. Expand/contract is a rule for
/// migration authors (specs/08 section 9.1); the runner enforces what it can see: an applied file may not change or
/// disappear, and a pending file may not sit below the highest applied version.
/// </summary>
public sealed class MigrationRunner
{
    readonly IMigrationJournal _journal;
    readonly ILogger _log;

    public MigrationRunner(IMigrationJournal journal, ILogger<MigrationRunner>? log = null)
    {
        _journal = journal;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>Applies every pending script and returns them, in order. Throws <see cref="MigrationException"/> on any rule break, before applying anything.</summary>
    public async Task<IReadOnlyList<MigrationScript>> RunAsync(IReadOnlyList<MigrationScript> scripts, CancellationToken ct = default)
    {
        var ordered = MigrationScript.Ordered(scripts);
        await using var _ = await _journal.LockAsync(ct).ConfigureAwait(false);
        await _journal.EnsureCreatedAsync(ct).ConfigureAwait(false);
        var applied = (await _journal.AppliedAsync(ct).ConfigureAwait(false)).ToDictionary(a => a.Version);

        var byVersion = ordered.ToDictionary(s => s.Version);
        foreach (var a in applied.Values.OrderBy(a => a.Version))
        {
            if (!byVersion.TryGetValue(a.Version, out var s))
                throw new MigrationException($"{a.Version:D4}_{a.Name}.sql is applied but missing from the migration set (files are never removed)");
            if (s.Checksum != a.Checksum)
                throw new MigrationException($"{s.FileName} changed after it was applied (checksum {a.Checksum} -> {s.Checksum}); add a new migration instead");
        }

        int highest = applied.Count == 0 ? 0 : applied.Keys.Max();
        var pending = ordered.Where(s => !applied.ContainsKey(s.Version)).ToList();
        var stray = pending.FirstOrDefault(s => s.Version < highest);
        if (stray != null)
            throw new MigrationException($"{stray.FileName} is below the highest applied version {highest:D4}; renumber it above");

        foreach (var s in pending)
        {
            _log.LogInformation("applying migration {File}", s.FileName);
            await _journal.ApplyAsync(s, ct).ConfigureAwait(false);
        }
        _log.LogInformation("migrations: {Applied} applied now, {Total} in the set", pending.Count, ordered.Count);
        return pending;
    }
}
