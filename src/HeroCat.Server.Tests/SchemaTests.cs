using System.Globalization;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HeroCat.Server.Tests;

/// <summary>
/// The schema v1 migration set (server/db/migrations) held to specs/02 sections 12 and 13 without a database (issue
/// #154). PostgresSchemaTests checks the same model against a live PostgreSQL 16 where one is available.
/// </summary>
[TestFixture]
public sealed class SchemaTests
{
    static readonly SqlSchemaModel Spec = SqlSchemaModel.Parse(SqlSchemaModel.SpecTexts());
    static readonly SqlSchemaModel Set = SqlSchemaModel.Parse(SqlSchemaModel.MigrationTexts());

    static readonly string[] FinancialTables = { "purchases", "summon_log", "diamond_ledger" };

    [Test]
    public void Init_migration_is_the_spec_ddl_verbatim()
    {
        // specs/02 section 12: "0001_init.sql is the DDL below, verbatim". 0001 is immutable once merged; a later schema
        // change is a new migration, and the spec's sentence and this test change with it.
        string init = File.ReadAllText(RepoPaths.Of("server/db/migrations/0001_init.sql"));
        Assert.That(init, Is.EqualTo(SqlSchemaModel.SpecBlock()));
    }

    [Test]
    public void Migration_files_are_numbered_from_0001_without_gaps()
    {
        var set = HeroCat.Server.Infrastructure.Db.Migrations.MigrationScript.LoadDirectory(RepoPaths.Of("server/db/migrations"));
        Assert.That(set.Select(s => s.Version), Is.EqualTo(Enumerable.Range(1, set.Count)));
        Assert.That(set[0].FileName, Is.EqualTo("0001_init.sql"));
    }

    [Test]
    public void Every_table_in_the_issue_and_spec_exists()
    {
        string[] expected =
        {
            "accounts", "account_links", "devices", "refresh_tokens", "saves", "save_history", "diamond_ledger",
            "ticket_ledger", "granted_items", "idempotency", "purchases", "store_notifications", "inbox_items",
            "summon_log", "banner_pity", "dungeon_seeds", "config_versions", "mail_blasts", "moderation_actions",
            "account_exports", "events", "coupons", "coupon_redemptions", "guilds", "guild_members", "guild_chat",
        };
        Assert.That(Set.Tables.Keys, Is.EquivalentTo(expected));
        Assert.That(Spec.Tables.Keys, Is.EquivalentTo(expected));
        Assert.That(Set.MaterializedViews, Is.EqualTo(new[] { "currency_daily" }));
    }

    [Test]
    public void Columns_types_nullability_keys_and_indexes_match_the_spec()
    {
        foreach (var t in Spec.Tables.Values)
        {
            var got = Set.Tables[t.Name];
            Assert.That(got.Columns, Is.EqualTo(t.Columns), t.Name);
            Assert.That(got.PartitionBy, Is.EqualTo(t.PartitionBy), t.Name);
        }
        Assert.That(Set.ForeignKeys, Is.EquivalentTo(Spec.ForeignKeys));
        Assert.That(Set.Indexes, Is.EquivalentTo(Spec.Indexes));
    }

    [Test]
    public void Financial_tables_restrict_deletes_and_everything_else_cascades_as_13_2_lists()
    {
        // 00 decision on issue #32: purchases, summon_log and diamond_ledger use on delete restrict so a delete job that
        // skips the tombstone step fails instead of destroying financial records.
        var toAccounts = Set.ForeignKeys.Where(f => f.RefTable == "accounts").ToList();
        foreach (string t in FinancialTables)
            Assert.That(toAccounts.Single(f => f.Table == t).OnDelete, Is.EqualTo("restrict"), t);

        var cascades = toAccounts.Where(f => f.OnDelete == "cascade").Select(f => f.Table).ToList();
        Assert.That(cascades, Is.EquivalentTo(DeleteFlowCascadeList()), "13.2 step 4 lists exactly the tables that cascade");

        var setNull = toAccounts.Where(f => f.OnDelete == "set null").Select(f => (f.Table, f.Column)).ToList();
        Assert.That(setNull, Is.EquivalentTo(new[] { ("guilds", "leader_id"), ("guild_chat", "player_id") }));
        Assert.That(toAccounts.Count, Is.EqualTo(FinancialTables.Length + cascades.Count + setNull.Count), "no other delete rule references accounts");
    }

    [Test]
    public void Every_player_id_column_references_accounts_except_the_idempotency_table()
    {
        var withFk = Set.ForeignKeys.Where(f => f.RefTable == "accounts").Select(f => (f.Table, f.Column)).ToHashSet();
        var unreferenced = Set.Tables.Values
            .SelectMany(t => t.Columns.Where(c => c.Name == "player_id" && t.Name != "accounts").Select(c => (t.Name, c.Name)))
            .Where(tc => !withFk.Contains(tc))
            .Select(tc => tc.Item1)
            .ToList();
        // 13.2 step 4: "delete from idempotency where player_id = ? (no FK)".
        Assert.That(unreferenced, Is.EqualTo(new[] { "idempotency" }));
    }

    [Test]
    public void Money_and_currency_are_never_float()
    {
        var doubles = Set.Tables.Values
            .SelectMany(t => t.Columns.Where(c => c.Type is "double precision" or "real").Select(c => t.Name + "." + c.Name))
            .ToList();
        // Floats are only progress scores and the logged summon uniforms; prices are integer minor units + char(3).
        Assert.That(doubles, Is.EquivalentTo(new[] { "saves.progress_score", "save_history.progress_score", "summon_log.u1", "summon_log.u2", "summon_log.u3" }));
        var purchases = Set.Tables["purchases"].Columns;
        Assert.That(purchases.Single(c => c.Name == "price_minor").Type, Is.EqualTo("integer"));
        Assert.That(purchases.Single(c => c.Name == "currency").Type, Is.EqualTo("character(3)"));
    }

    [Test]
    public void Save_history_trim_trigger_keeps_SAVE_HISTORY_KEEP()
    {
        Assert.That(Set.Triggers, Does.Contain(("save_history_trim", "save_history")));
        string sql = File.ReadAllText(RepoPaths.Of("server/db/migrations/0002_save_history_trim.sql"));
        var limits = Regex.Matches(sql, @"\blimit (\d+)").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.That(limits, Is.EqualTo(new[] { SpecConstant("SAVE_HISTORY_KEEP") }));
    }

    [Test]
    public void Every_partitioned_table_gets_partitions_at_its_grain()
    {
        // specs/02 section 12: diamond_ledger and summon_log one partition per month, events one per day.
        var partitioned = Set.Tables.Values.Where(t => t.PartitionBy != null).ToDictionary(t => t.Name, t => t.PartitionBy);
        Assert.That(partitioned, Is.EquivalentTo(new Dictionary<string, string?>
        {
            ["diamond_ledger"] = "range(created_at)", ["summon_log"] = "range(created_at)", ["events"] = "range(ts)",
        }));
        var calls = Set.OtherStatements
            .Select(s => Regex.Match(s, @"^select ensure_partitions\('(\w+)', '(\w+)', now\(\), now\(\) \+ interval '[^']+'\)$"))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Assert.That(calls, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["diamond_ledger"] = "month", ["summon_log"] = "month", ["events"] = "day",
        }));
        Assert.That(Set.Functions, Does.Contain("ensure_partitions"));
        Assert.That(Set.OtherStatements.Count, Is.EqualTo(calls.Count), "no other top-level statement");
    }

    [Test]
    public void The_reader_rejects_statements_it_does_not_model()
    {
        Assert.Throws<NotSupportedException>(() => SqlSchemaModel.Parse("create table accounts (a int);", "alter table accounts drop column a;"));
        var added = SqlSchemaModel.Parse("create table accounts (a int);", "alter table accounts add column b text not null default '-';");
        Assert.That(added.Tables["accounts"].Columns, Is.EqualTo(new[] { new SqlColumn("a", "int", false), new SqlColumn("b", "text", true) }));
        var m = SqlSchemaModel.Parse("create table t (a int primary key, -- it's a comment\n b text[] not null default '{}', c timestamptz, primary key (a));");
        Assert.That(m.Tables["t"].Columns, Is.EqualTo(new[]
        {
            new SqlColumn("a", "int", true), new SqlColumn("b", "text[]", true), new SqlColumn("c", "timestamp with time zone", false),
        }));
    }

    /// <summary>The backticked tables after "cascades to" in specs/02 section 13.2 step 4.</summary>
    internal static List<string> DeleteFlowCascadeList()
    {
        string spec = File.ReadAllText(RepoPaths.Of("specs/02-domain-model-and-persistence.md"));
        var line = spec.Split('\n').Single(l => l.StartsWith("4. `delete from accounts where player_id = ?` cascades to", StringComparison.Ordinal));
        string list = line[line.IndexOf("cascades to", StringComparison.Ordinal)..line.IndexOf(';')];
        return Regex.Matches(list, "`(\\w+)`").Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>A constant's value from specs/02's "Constants" table (section 2).</summary>
    internal static int SpecConstant(string name)
    {
        string spec = File.ReadAllText(RepoPaths.Of("specs/02-domain-model-and-persistence.md"));
        var m = Regex.Match(spec, @"^\| `" + name + @"` \| (\d+) \|", RegexOptions.Multiline);
        Assert.That(m.Success, $"specs/02 has no constant {name}");
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
