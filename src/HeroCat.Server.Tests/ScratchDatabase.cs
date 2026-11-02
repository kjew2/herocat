using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Infrastructure.Db.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace HeroCat.Server.Tests;

/// <summary>
/// A scratch PostgreSQL database with the schema v1 migrations applied, on the server named by HEROCAT_TEST_POSTGRES;
/// ignores the fixture when that is unset. Create it in <c>[OneTimeSetUp]</c>, dispose it in <c>[OneTimeTearDown]</c>.
/// </summary>
sealed class ScratchDatabase : IAsyncDisposable
{
    readonly string _admin;
    readonly string _name;

    public string ConnectionString { get; }
    public NpgsqlConnectionFactory Factory { get; }
    public NpgsqlDataSource DataSource { get; }

    ScratchDatabase(string admin, string name)
    {
        _admin = admin;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        Factory = new NpgsqlConnectionFactory(Options.Create(new ServerOptions { Database = new DatabaseOptions { ConnectionString = ConnectionString } }));
        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public static async Task<ScratchDatabase> CreateAsync(string prefix)
    {
        string admin = Environment.GetEnvironmentVariable(PostgresMigrationTests.EnvVar) ?? "";
        if (string.IsNullOrWhiteSpace(admin)) Assert.Ignore($"{PostgresMigrationTests.EnvVar} not set: no Postgres");
        string name = prefix + "_" + Guid.NewGuid().ToString("N")[..12];
        await using (var c = new NpgsqlConnection(admin))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", c);
            await cmd.ExecuteNonQueryAsync();
        }
        var db = new ScratchDatabase(admin, name);
        await new MigrationRunner(new NpgsqlMigrationJournal(db.Factory)).RunAsync(MigrationScript.LoadDirectory(RepoPaths.Of("server/db/migrations")));
        return db;
    }

    public async Task<long> CountAsync(string sql, params (string name, object value)[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task ExecAsync(string sql, params (string name, object value)[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(_admin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
    }
}
