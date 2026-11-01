using System.Text;
using HeroCat.Server.Infrastructure.Db.Migrations;
using NUnit.Framework;

namespace HeroCat.Server.Tests;

/// <summary>An in-memory schema_migrations: the runner's rules without a database.</summary>
sealed class MemoryJournal : IMigrationJournal
{
    public readonly List<AppliedMigration> Applied = new();
    public readonly List<string> Executed = new();
    public int Locks;
    public int Unlocks;
    public bool Created;
    public Func<MigrationScript, bool> Fails = _ => false;

    public Task<IAsyncDisposable> LockAsync(CancellationToken ct)
    {
        Locks++;
        return Task.FromResult<IAsyncDisposable>(new Release(this));
    }

    public Task EnsureCreatedAsync(CancellationToken ct) { Created = true; return Task.CompletedTask; }

    public Task<IReadOnlyList<AppliedMigration>> AppliedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AppliedMigration>>(Applied.ToList());

    public Task ApplyAsync(MigrationScript script, CancellationToken ct)
    {
        if (Fails(script)) throw new InvalidOperationException("syntax error in " + script.FileName);
        Executed.Add(script.FileName);
        Applied.Add(new AppliedMigration(script.Version, script.Name, script.Checksum));
        return Task.CompletedTask;
    }

    sealed class Release : IAsyncDisposable
    {
        readonly MemoryJournal _j;
        public Release(MemoryJournal j) { _j = j; }
        public ValueTask DisposeAsync() { _j.Unlocks++; return ValueTask.CompletedTask; }
    }
}

[TestFixture]
public sealed class MigrationScriptTests
{
    static MigrationScript S(string file, string sql = "SELECT 1;") => MigrationScript.FromBytes(file, Encoding.UTF8.GetBytes(sql));

    [Test]
    public void File_name_gives_version_and_name()
    {
        var s = S("0007_add_inbox_index.sql");
        Assert.That(s.Version, Is.EqualTo(7));
        Assert.That(s.Name, Is.EqualTo("add_inbox_index"));
        Assert.That(s.FileName, Is.EqualTo("0007_add_inbox_index.sql"));
    }

    [TestCase("7_init.sql")]
    [TestCase("0001-init.sql")]
    [TestCase("0001_Init.sql")]
    [TestCase("00001_init.sql")]
    [TestCase("0000_init.sql")]
    public void Misnamed_files_are_rejected(string file) =>
        Assert.Throws<MigrationException>(() => S(file));

    [Test]
    public void Checksum_is_sha256_of_the_bytes_and_a_bom_is_not_sql()
    {
        var a = S("0001_init.sql", "CREATE TABLE t (id int);");
        string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("CREATE TABLE t (id int);"))).ToLowerInvariant();
        Assert.That(a.Checksum, Is.EqualTo(expected));
        Assert.That(S("0001_init.sql", "CREATE TABLE t (id bigint);").Checksum, Is.Not.EqualTo(a.Checksum));
        var bom = MigrationScript.FromBytes("0001_init.sql", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("SELECT 1;")).ToArray());
        Assert.That(bom.Sql, Is.EqualTo("SELECT 1;"));
    }

    [Test]
    public void Two_files_with_one_version_are_rejected() =>
        Assert.Throws<MigrationException>(() => MigrationScript.Ordered(new[] { S("0002_a.sql"), S("0002_b.sql") }));

    [Test]
    public void Directory_load_orders_by_version_and_needs_the_directory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "herocat-migrations-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<MigrationException>(() => MigrationScript.LoadDirectory(dir));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.That(MigrationScript.LoadDirectory(dir), Is.Empty);
            File.WriteAllText(Path.Combine(dir, "0010_b.sql"), "SELECT 10;");
            File.WriteAllText(Path.Combine(dir, "0002_a.sql"), "SELECT 2;");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a migration");
            Assert.That(MigrationScript.LoadDirectory(dir).Select(s => s.Version), Is.EqualTo(new[] { 2, 10 }));
            File.WriteAllText(Path.Combine(dir, "bad.sql"), "SELECT 0;");
            Assert.Throws<MigrationException>(() => MigrationScript.LoadDirectory(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void The_repo_migration_set_is_valid_and_ships_with_the_build()
    {
        // server/db/migrations (specs/02 section 12) is copied into the server's output as db/migrations.
        var repo = MigrationScript.LoadDirectory(RepoPaths.Of("server/db/migrations"));
        string shipped = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "db", "migrations");
        var built = MigrationScript.LoadDirectory(shipped);
        Assert.That(built.Select(s => (s.FileName, s.Checksum)), Is.EqualTo(repo.Select(s => (s.FileName, s.Checksum))));
    }
}

[TestFixture]
public sealed class MigrationRunnerTests
{
    static MigrationScript S(int v, string name, string sql = "SELECT 1;") =>
        MigrationScript.FromBytes($"{v:D4}_{name}.sql", Encoding.UTF8.GetBytes(sql));

    static readonly MigrationScript[] Set = { S(1, "init"), S(2, "add_x"), S(3, "add_y") };

    [Test]
    public async Task Applies_everything_from_empty_in_order_under_the_lock()
    {
        var j = new MemoryJournal();
        var applied = await new MigrationRunner(j).RunAsync(new[] { Set[2], Set[0], Set[1] });
        Assert.That(applied.Select(s => s.Version), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(j.Executed, Is.EqualTo(new[] { "0001_init.sql", "0002_add_x.sql", "0003_add_y.sql" }));
        Assert.That(j.Created, Is.True);
        Assert.That((j.Locks, j.Unlocks), Is.EqualTo((1, 1)));
    }

    [Test]
    public async Task Second_run_is_a_no_op_and_a_new_file_applies_alone()
    {
        var j = new MemoryJournal();
        var runner = new MigrationRunner(j);
        await runner.RunAsync(Set.Take(2).ToList());
        Assert.That(await runner.RunAsync(Set.Take(2).ToList()), Is.Empty);
        var next = await runner.RunAsync(Set);
        Assert.That(next.Select(s => s.Version), Is.EqualTo(new[] { 3 }));
        Assert.That(j.Executed.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task An_edited_applied_file_stops_the_run_before_anything_applies()
    {
        var j = new MemoryJournal();
        await new MigrationRunner(j).RunAsync(new[] { Set[0] });
        var edited = new[] { S(1, "init", "SELECT 2;"), Set[1] };
        var e = Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(j).RunAsync(edited));
        Assert.That(e!.Message, Does.Contain("0001_init.sql changed"));
        Assert.That(j.Executed, Is.EqualTo(new[] { "0001_init.sql" }));
        Assert.That(j.Unlocks, Is.EqualTo(j.Locks), "the lock is released on failure");
    }

    [Test]
    public async Task An_applied_file_missing_from_the_set_stops_the_run()
    {
        var j = new MemoryJournal();
        await new MigrationRunner(j).RunAsync(Set.Take(2).ToList());
        var e = Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(j).RunAsync(new[] { Set[0], Set[2] }));
        Assert.That(e!.Message, Does.Contain("0002_add_x.sql is applied but missing"));
        Assert.That(j.Executed.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task A_pending_file_below_the_highest_applied_version_stops_the_run()
    {
        var j = new MemoryJournal();
        await new MigrationRunner(j).RunAsync(new[] { Set[0], Set[2] });
        var e = Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(j).RunAsync(Set));
        Assert.That(e!.Message, Does.Contain("0002_add_x.sql is below the highest applied version 0003"));
    }

    [Test]
    public void A_failing_script_stops_the_run_and_later_scripts_do_not_apply()
    {
        var j = new MemoryJournal { Fails = s => s.Version == 2 };
        Assert.ThrowsAsync<InvalidOperationException>(() => new MigrationRunner(j).RunAsync(Set));
        Assert.That(j.Applied.Select(a => a.Version), Is.EqualTo(new[] { 1 }));
        Assert.That(j.Unlocks, Is.EqualTo(1));
    }
}
