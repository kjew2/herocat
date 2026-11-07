using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using HeroCat.Server.Observability;

namespace HeroCat.Server.Infrastructure.Db;

/// <summary>
/// Parameterised commands over a provider-neutral <see cref="DbConnection"/> (names are <c>@name</c> in the SQL). Every
/// command is named (<c>area.operation</c>, e.g. <c>save.get</c>), and each execution records
/// <c>db_query_duration_seconds{name}</c> (specs/08 section 9.4, issue #318).
/// </summary>
public static partial class DbCommands
{
    /// <summary>A query name: lowercase dotted words, at least two (<c>area.operation</c>), so the label set stays small and readable.</summary>
    public static bool IsValidName(string name) => NameRule().IsMatch(name);

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex NameRule();

    /// <param name="name">The query's <c>name</c> label: a constant per call site, never built from data.</param>
    public static DbCommand Command(this DbConnection c, string name, string sql, DbTransaction? tx, params (string name, object? value)[] args)
    {
        if (!IsValidName(name)) throw new ArgumentException($"query name '{name}' is not area.operation", nameof(name));
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (pname, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = pname;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return new NamedDbCommand(name, cmd);
    }
}

/// <summary>
/// Wraps a provider command and times each execute: from the call until it returns (for a reader, until the first
/// result is ready, not while its rows are read), recorded under the query's name whether it succeeds or throws.
/// Parameters, connection and transaction are the inner command's, so provider-specific parameter settings still apply.
/// </summary>
sealed class NamedDbCommand : DbCommand
{
    readonly DbCommand _inner;

    public NamedDbCommand(string name, DbCommand inner)
    {
        Name = name;
        _inner = inner;
    }

    public string Name { get; }

    [AllowNull]
    public override string CommandText { get => _inner.CommandText; set => _inner.CommandText = value; }
    public override int CommandTimeout { get => _inner.CommandTimeout; set => _inner.CommandTimeout = value; }
    public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }
    public override UpdateRowSource UpdatedRowSource { get => _inner.UpdatedRowSource; set => _inner.UpdatedRowSource = value; }
    public override bool DesignTimeVisible { get => _inner.DesignTimeVisible; set => _inner.DesignTimeVisible = value; }
    protected override DbConnection? DbConnection { get => _inner.Connection; set => _inner.Connection = value; }
    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;
    protected override DbTransaction? DbTransaction { get => _inner.Transaction; set => _inner.Transaction = value; }

    public override void Cancel() => _inner.Cancel();
    public override void Prepare() => _inner.Prepare();
    public override Task PrepareAsync(CancellationToken ct = default) => _inner.PrepareAsync(ct);
    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    T Time<T>(Func<T> run)
    {
        long start = Stopwatch.GetTimestamp();
        try { return run(); }
        finally { DbMetrics.RecordQuery(Name, Stopwatch.GetElapsedTime(start)); }
    }

    async Task<T> TimeAsync<T>(Func<Task<T>> run)
    {
        long start = Stopwatch.GetTimestamp();
        try { return await run().ConfigureAwait(false); }
        finally { DbMetrics.RecordQuery(Name, Stopwatch.GetElapsedTime(start)); }
    }

    public override int ExecuteNonQuery() => Time(_inner.ExecuteNonQuery);
    public override object? ExecuteScalar() => Time(_inner.ExecuteScalar);
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Time(() => _inner.ExecuteReader(behavior));

    public override Task<int> ExecuteNonQueryAsync(CancellationToken ct) => TimeAsync(() => _inner.ExecuteNonQueryAsync(ct));
    public override Task<object?> ExecuteScalarAsync(CancellationToken ct) => TimeAsync(() => _inner.ExecuteScalarAsync(ct));
    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct) =>
        TimeAsync(() => _inner.ExecuteReaderAsync(behavior, ct));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
