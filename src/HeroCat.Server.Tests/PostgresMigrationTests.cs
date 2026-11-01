using System.Text;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Infrastructure.Db.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace HeroCat.Server.Tests;

/// <summary>
/// The runner against a real PostgreSQL, in a scratch database it creates and drops. Runs only when
/// HEROCAT_TEST_POSTGRES is a connection string to a server where it may create databases (./check.sh --server sets
/// it in CI); elsewhere it is ignored, so it adds nothing to the counted tests. The Testcontainers harness that starts
/// Postgres itself is #219.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresMigrationTests
{
    public const string EnvVar = "HEROCAT_TEST_POSTGRES";

    string _admin = "";
    string _db = "";

    [OneTimeSetUp]
    public async Task CreateScratchDatabase()
    {
        _admin = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        if (string.IsNullOrWhiteSpace(_admin)) Assert.Ignore($"{EnvVar} not set: no Postgres");
        _db = "herocat_test_" + Guid.NewGuid().ToString("N")[..12];
        await using var c = new NpgsqlConnection(_admin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_db}", c);
        await cmd.ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task DropScratchDatabase()
    {
        if (_db.Length == 0) return;
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(_admin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_db} WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
    }

    string Scratch => new NpgsqlConnectionStringBuilder(_admin) { Database = _db }.ConnectionString;

    MigrationRunner Runner(out NpgsqlConnectionFactory db)
    {
        var o = Options.Create(new ServerOptions { Database = new DatabaseOptions { ConnectionString = Scratch } });
        db = new NpgsqlConnectionFactory(o);
        return new MigrationRunner(new NpgsqlMigrationJournal(db));
    }

    static MigrationScript S(int v, string name, string sql) => MigrationScript.FromBytes($"{v:D4}_{name}.sql", Encoding.UTF8.GetBytes(sql));

    [Test, Order(1)]
    public async Task The_repo_set_applies_from_empty_and_twice_is_a_no_op()
    {
        var set = MigrationScript.LoadDirectory(RepoPaths.Of("server/db/migrations"));
        var runner = Runner(out var db);
        await using var _ = db;
        Assert.That((await runner.RunAsync(set)).Count, Is.EqualTo(set.Count));
        Assert.That(await runner.RunAsync(set), Is.Empty);
    }

    [Test, Order(2)]
    public async Task The_migrate_role_applies_the_shipped_set_and_exits_0()
    {
        // The deploy pipeline's pre-deploy job (specs/08 section 9.1): the image's own db/migrations, then exit.
        string[] args = { ServerRoles.MigrateFlag, "--HeroCat:Database:ConnectionString=" + Scratch };
        Assert.That(await ServerHost.RunAsync(args), Is.EqualTo(0));
        Assert.That(await ServerHost.RunAsync(args), Is.EqualTo(0));
        await using var c = new NpgsqlConnection(Scratch);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass('schema_migrations') IS NOT NULL", c);
        Assert.That(await cmd.ExecuteScalarAsync(), Is.True);
    }

    [Test, Order(3)]
    public async Task A_failing_script_rolls_back_with_no_journal_row()
    {
        var runner = Runner(out var db);
        await using var _ = db;
        var bad = S(9001, "scratch_bad", "CREATE TABLE scratch_t (id int); SELECT no_such_function();");
        Assert.ThrowsAsync<PostgresException>(() => runner.RunAsync(
            MigrationScript.LoadDirectory(RepoPaths.Of("server/db/migrations")).Append(bad).ToList()));
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM schema_migrations WHERE version = 9001; ";
        Assert.That(Convert.ToInt64(await cmd.ExecuteScalarAsync()), Is.EqualTo(0));
        cmd.CommandText = "SELECT to_regclass('scratch_t') IS NULL";
        Assert.That(await cmd.ExecuteScalarAsync(), Is.True);
    }
}
