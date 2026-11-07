using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using HeroCat.Server.Infrastructure;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Observability;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Observability;

/// <summary><c>db_query_duration_seconds{name}</c> (specs/08 section 9.4, issue #318): every store command is named and timed.</summary>
[TestFixture]
public sealed class DbQueryMetricsTests
{
    sealed class Recorder : IDisposable
    {
        readonly MeterListener _listener = new();
        public readonly List<(string name, double seconds)> Points = new();

        public Recorder()
        {
            _listener.InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == ServerMetrics.MeterName && i.Name == DbMetrics.QueryDurationSeconds) l.EnableMeasurementEvents(i);
            };
            _listener.SetMeasurementEventCallback<double>((_, v, tags, _) =>
            {
                foreach (var t in tags)
                    if (t.Key == "name" && t.Value is string n && n.StartsWith("test.", StringComparison.Ordinal))
                        lock (Points) Points.Add((n, v));
            });
            _listener.Start();
        }

        public string[] Names() { lock (Points) return Points.Select(p => p.name).ToArray(); }

        public void Dispose() => _listener.Dispose();
    }

    [Test]
    public async Task Each_execute_of_a_named_command_records_its_duration_under_its_name()
    {
        using var rec = new Recorder();
        var c = new FakeConnection();

        await using (var cmd = c.Command("test.non_query", "update x set y = @y", null, ("y", 1)))
        {
            Assert.That(cmd.CommandText, Is.EqualTo("update x set y = @y"));
            Assert.That(cmd.Parameters[0].ParameterName, Is.EqualTo("y"), "parameters are the provider command's");
            Assert.That(await cmd.ExecuteNonQueryAsync(), Is.EqualTo(1));
        }
        await using (var cmd = c.Command("test.scalar", "select 1", null))
            Assert.That(await cmd.ExecuteScalarAsync(), Is.EqualTo(42));
        await using (var cmd = c.Command("test.reader", "select 1", null))
        await using (var r = await cmd.ExecuteReaderAsync())
            Assert.That(r, Is.Not.Null);
        using (var cmd = c.Command("test.sync", "select 1", null))
            Assert.That(cmd.ExecuteNonQuery(), Is.EqualTo(1));

        Assert.That(rec.Names(), Is.EqualTo(new[] { "test.non_query", "test.scalar", "test.reader", "test.sync" }));
        Assert.That(rec.Points.Select(p => p.seconds), Has.All.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void A_command_that_throws_still_records_its_duration()
    {
        using var rec = new Recorder();
        var c = new FakeConnection { Throw = true };
        using var cmd = c.Command("test.failing", "select 1", null);
        Assert.ThrowsAsync<InvalidOperationException>(() => cmd.ExecuteNonQueryAsync());
        Assert.That(rec.Names(), Is.EqualTo(new[] { "test.failing" }));
    }

    [TestCase("save.get", true)]
    [TestCase("rate_drift.count_pulls", true)]
    [TestCase("save", false)]
    [TestCase("Save.Get", false)]
    [TestCase("save.", false)]
    [TestCase("select 1", false)]
    public void Query_names_are_dotted_lowercase_words(string name, bool valid)
    {
        Assert.That(DbCommands.IsValidName(name), Is.EqualTo(valid));
        if (!valid) Assert.Throws<ArgumentException>(() => new FakeConnection().Command(name, "select 1", null));
    }

    /// <summary>Names are a bounded label set: each call site passes its own string literal, so a name is never built from data.</summary>
    [Test]
    public void Every_server_query_is_named_with_a_literal_unique_to_its_call_site()
    {
        var names = new List<string>();
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Of("src/HeroCat.Server"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string src = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(src, @"\.Command\(\s*(?<arg>[^,]*),"))
            {
                var lit = Regex.Match(m.Groups["arg"].Value, "^\"([^\"]*)\"$");
                if (!lit.Success) problems.Add($"{Path.GetFileName(file)}: query name is not a literal: {m.Groups["arg"].Value}");
                else if (!DbCommands.IsValidName(lit.Groups[1].Value)) problems.Add($"{Path.GetFileName(file)}: bad name {lit.Groups[1].Value}");
                else names.Add(lit.Groups[1].Value);
            }
        }
        Assert.That(problems, Is.Empty);
        Assert.That(names, Has.Count.GreaterThan(50), "found the stores' commands");
        Assert.That(names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key), Is.Empty, "names are unique");
    }

    internal sealed class FakeConnection : DbConnection
    {
        public bool Throw;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "16";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Open() { }
        public override void Close() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FakeCommand(this);
    }

    sealed class FakeCommand : DbCommand
    {
        readonly FakeConnection _c;
        readonly FakeParameters _params = new();
        public FakeCommand(FakeConnection c) => _c = c;
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        public override bool DesignTimeVisible { get; set; }
        protected override DbConnection? DbConnection { get => _c; set { } }
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeParameter();
        int Run(int v) => _c.Throw ? throw new InvalidOperationException("db down") : v;
        public override int ExecuteNonQuery() => Run(1);
        public override object ExecuteScalar() => Run(42);
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Run(0);
            return new DataTable().CreateDataReader();
        }
    }

    sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = "";
        [AllowNull] public override string SourceColumn { get; set; } = "";
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    sealed class FakeParameters : DbParameterCollection
    {
        readonly List<DbParameter> _list = new();
        public override int Count => _list.Count;
        public override object SyncRoot => _list;
        public override int Add(object value) { _list.Add((DbParameter)value); return _list.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v); }
        public override void Clear() => _list.Clear();
        public override bool Contains(object value) => _list.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_list).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _list.GetEnumerator();
        public override int IndexOf(object value) => _list.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _list.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _list.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _list.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _list.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _list.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => _list[index];
        protected override DbParameter GetParameter(string parameterName) => _list[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _list[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _list[IndexOf(parameterName)] = value;
    }
}
