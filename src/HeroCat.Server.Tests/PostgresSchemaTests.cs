using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Db;
using HeroCat.Server.Infrastructure.Db.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace HeroCat.Server.Tests;

/// <summary>
/// specs/02 acceptance 16 against a real PostgreSQL 16 (issue #154): the schema v1 set creates exactly section 12's
/// tables, columns, types, nullability, foreign-key delete rules and indexes; the save_history trigger keeps
/// SAVE_HISTORY_KEEP rows; the partitions exist; and the 13.2 hard delete leaves no row on the old player_id, with a
/// purchaser's financial rows re-pointed to a tombstone account, while a delete that skips that step fails. Like
/// PostgresMigrationTests it runs only when HEROCAT_TEST_POSTGRES is set, in its own scratch database.
/// </summary>
[TestFixture]
[Category("Postgres")]
public sealed class PostgresSchemaTests
{
    string _admin = "";
    string _db = "";
    NpgsqlDataSource? _ds;

    [OneTimeSetUp]
    public async Task CreateAndMigrate()
    {
        _admin = Environment.GetEnvironmentVariable(PostgresMigrationTests.EnvVar) ?? "";
        if (string.IsNullOrWhiteSpace(_admin)) Assert.Ignore($"{PostgresMigrationTests.EnvVar} not set: no Postgres");
        _db = "herocat_schema_" + Guid.NewGuid().ToString("N")[..12];
        await using (var c = new NpgsqlConnection(_admin))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_db}", c);
            await cmd.ExecuteNonQueryAsync();
        }
        string scratch = new NpgsqlConnectionStringBuilder(_admin) { Database = _db }.ConnectionString;
        var o = Options.Create(new ServerOptions { Database = new DatabaseOptions { ConnectionString = scratch } });
        await using (var factory = new NpgsqlConnectionFactory(o))
            await new MigrationRunner(new NpgsqlMigrationJournal(factory)).RunAsync(MigrationScript.LoadDirectory(RepoPaths.Of("server/db/migrations")));
        _ds = NpgsqlDataSource.Create(scratch);
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_ds != null) await _ds.DisposeAsync();
        if (_db.Length == 0) return;
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(_admin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_db} WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
    }

    async Task Exec(string sql)
    {
        await using var cmd = _ds!.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    async Task<long> Count(string sql)
    {
        await using var cmd = _ds!.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    async Task<List<object[]>> Rows(string sql)
    {
        await using var cmd = _ds!.CreateCommand(sql);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await r.ReadAsync())
        {
            var row = new object[r.FieldCount];
            r.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    static readonly SqlSchemaModel Spec = SqlSchemaModel.Parse(SqlSchemaModel.SpecBlock());

    [Test]
    public async Task Tables_columns_types_and_nullability_are_section_12()
    {
        var rows = await Rows(@"
select c.relname::text, a.attname::text, format_type(a.atttypid, a.atttypmod), a.attnotnull
from pg_attribute a join pg_class c on c.oid = a.attrelid join pg_namespace n on n.oid = c.relnamespace
where n.nspname = 'public' and c.relkind in ('r', 'p') and not c.relispartition and a.attnum > 0 and not a.attisdropped
  and c.relname <> 'schema_migrations'
order by c.relname, a.attnum");
        var live = rows.GroupBy(r => (string)r[0])
            .ToDictionary(g => g.Key, g => g.Select(r => new SqlColumn((string)r[1], (string)r[2], (bool)r[3])).ToList());
        Assert.That(live.Keys, Is.EquivalentTo(Spec.Tables.Keys));
        foreach (var t in Spec.Tables.Values) Assert.That(live[t.Name], Is.EqualTo(t.Columns), t.Name);
        Assert.That(await Count("select count(*) from pg_matviews where matviewname = 'currency_daily'"), Is.EqualTo(1));
    }

    [Test]
    public async Task Foreign_key_delete_rules_are_section_12()
    {
        var rows = await Rows(@"
select c.relname::text, a.attname::text, f.relname::text, con.confdeltype::text
from pg_constraint con
join pg_class c on c.oid = con.conrelid
join pg_class f on f.oid = con.confrelid
join pg_attribute a on a.attrelid = con.conrelid and a.attnum = con.conkey[1]
where con.contype = 'f' and not c.relispartition and con.conparentid = 0");
        string Rule(string t) => t switch { "a" => "no action", "r" => "restrict", "c" => "cascade", "n" => "set null", "d" => "set default", _ => t };
        var live = rows.Select(r => new SqlForeignKey((string)r[0], (string)r[1], (string)r[2], Rule((string)r[3]))).ToList();
        Assert.That(live, Is.EquivalentTo(Spec.ForeignKeys));
    }

    [Test]
    public async Task Section_12_indexes_exist()
    {
        var rows = await Rows(@"
select t.relname::text, ix.indisunique, ix.indpred is not null,
       (select string_agg(a.attname::text, ', ' order by k.ord)
        from unnest(ix.indkey::int2[]) with ordinality k(attnum, ord)
        join pg_attribute a on a.attrelid = t.oid and a.attnum = k.attnum)
from pg_index ix join pg_class t on t.oid = ix.indrelid join pg_namespace n on n.oid = t.relnamespace
where n.nspname = 'public' and not t.relispartition");
        var live = rows.Select(r => new SqlIndex((string)r[0], (string)r[3], (bool)r[1], (bool)r[2])).ToList();
        foreach (var ix in Spec.Indexes) Assert.That(live, Does.Contain(ix), ix.ToString());
    }

    [Test]
    public async Task Partitions_cover_now_and_ensure_partitions_is_idempotent()
    {
        foreach (string t in new[] { "diamond_ledger", "summon_log", "events" })
            Assert.That(await Count($"select count(*) from pg_inherits where inhparent = '{t}'::regclass"), Is.GreaterThanOrEqualTo(4), t);
        // The first calls may create a partition if the migration ran before a UTC day or month boundary; reruns create none.
        await Count("select ensure_partitions('diamond_ledger', 'month', now(), now() + interval '4 months')");
        Assert.That(await Count("select ensure_partitions('diamond_ledger', 'month', now(), now() + interval '4 months')"), Is.EqualTo(0));
        Assert.That(await Count("select count(*) from pg_inherits where inhparent = 'diamond_ledger'::regclass"), Is.GreaterThanOrEqualTo(5));
        await Count("select ensure_partitions('events', 'day', now(), now() + interval '15 days')");
        Assert.That(await Count("select ensure_partitions('events', 'day', now(), now() + interval '15 days')"), Is.EqualTo(0));
        Assert.Throws<PostgresException>(() => Count("select ensure_partitions('events', 'week', now(), now())").GetAwaiter().GetResult());
        await Exec("insert into events (ts, name, player_id_hash, env) values (now(), 'currency_changed', '\\x00', 'test')");
        await Exec("refresh materialized view currency_daily");
    }

    static string NewAccount(Guid p) => $"insert into accounts (player_id, display_name, region) values ('{p}', 'Cat', 'JP');";

    static string NewDevice(Guid p, Guid d) =>
        $"insert into devices (device_id, player_id, hmac_key_enc) values ('{d}', '{p}', '\\x00');";

    [Test]
    public async Task An_eleventh_save_history_row_leaves_SAVE_HISTORY_KEEP()
    {
        Guid p = Guid.NewGuid(), d = Guid.NewGuid();
        await Exec(NewAccount(p) + NewDevice(p, d));
        for (int seq = 1; seq <= 11; seq++)
        {
            await Exec($@"insert into save_history (player_id, save_seq, device_id, blob_key, sha256, progress_score)
                values ('{p}', {seq}, '{d}', 'saves/{p}/{seq}.sav', '\x00', {seq})");
        }
        int keep = SchemaTests.SpecConstant("SAVE_HISTORY_KEEP");
        Assert.That(await Count($"select count(*) from save_history where player_id = '{p}'"), Is.EqualTo(keep));
        Assert.That(await Count($"select min(save_seq) from save_history where player_id = '{p}'"), Is.EqualTo(11 - keep + 1));
    }

    /// <summary>One row in every player-keyed table (financial ones only when <paramref name="purchaser"/>).</summary>
    async Task Populate(Guid p, bool purchaser, Guid guild)
    {
        Guid d = Guid.NewGuid();
        await Exec(NewAccount(p) + NewDevice(p, d) + $@"
insert into account_links (provider, provider_user_id, player_id) values ('apple', '{Guid.NewGuid()}', '{p}');
insert into refresh_tokens (token_hash, player_id, device_id, family_id, issued_at, expires_at)
  values (decode(md5('{Guid.NewGuid()}'), 'hex'), '{p}', '{d}', '{Guid.NewGuid()}', now(), now() + interval '60 days');
insert into saves (player_id, save_seq, device_id, schema_version, content_version, client_version, last_saved_server_time,
  clock_authoritative, size_bytes, sha256, hmac_valid, blob_key, etag, progress_score, summary)
  values ('{p}', 1, '{d}', 1, 'c1', '1.0.0', 0, true, 10, '\x00', true, 'saves/{p}/1.sav', 'e1', 1, '{{}}');
insert into save_history (player_id, save_seq, device_id, blob_key, sha256, progress_score) values ('{p}', 1, '{d}', 'k', '\x00', 1);
insert into ticket_ledger (player_id, ticket, delta, reason) values ('{p}', 'TICKET_SUMMON_WEAPON', 5, 'DAILY_GIFT');
insert into granted_items (player_id, item_id, amount, source_ref) values ('{p}', 'wpn_017', 1, '{Guid.NewGuid()}');
insert into idempotency (player_id, request_id, route, status_code, response) values ('{p}', 'r1', 'summon/pull', 200, '{{}}');
insert into inbox_items (inbox_item_id, player_id, kind, title_key, body_key, grants) values ('{Guid.NewGuid()}', '{p}', 'mail', 't', 'b', '[]');
insert into banner_pity (player_id, banner) values ('{p}', 'WEAPON');
insert into dungeon_seeds (grant_id, player_id, request_id, kind, floor, day_key, seed, expires_at)
  values ('{Guid.NewGuid()}', '{p}', 'r2', 'GOLD', 1, 20000, 42, now() + interval '1 hour');
insert into moderation_actions (player_id, action, flags, actor) values ('{p}', 'log', '{{}}', 'test');
insert into account_exports (export_id, player_id) values ('{Guid.NewGuid()}', '{p}');
insert into coupons (code, grants, created_by) values ('C{p:N}', '[]', 'test');
insert into coupon_redemptions (code, player_id, inbox_item_id) values ('C{p:N}', '{p}', '{Guid.NewGuid()}');
insert into guilds (guild_id, name, leader_id) values ('{guild}', 'g{p:N}', '{p}');
insert into guild_members (guild_id, player_id, role) values ('{guild}', '{p}', 'leader');
insert into guild_chat (guild_id, player_id, body) values ('{guild}', '{p}', 'meow');");
        if (!purchaser) return;
        await Exec($@"
insert into purchases (purchase_id, player_id, platform, sku, transaction_id, price_minor, currency, status)
  values ('{Guid.NewGuid()}', '{p}', 'apple', 'pack_small', '{Guid.NewGuid()}', 99, 'USD', 'granted');
insert into summon_log (player_id, request_id, banner, batch_index, item_id, rarity, pity_before, pity_after, banner_level,
  guarantee, rate_table_hash, u1, u2, u3, payment)
  values ('{p}', 'r3', 'WEAPON', 0, 'wpn_001', 'C', 0, 1, 1, false, '\x00', 0.1, 0.2, 0.3, 'DIAMOND');
insert into diamond_ledger (player_id, delta, reason, ref_type, ref_id) values ('{p}', 1200, 'IAP', 'purchase', 'x');");
    }

    /// <summary>specs/02 section 13.2 steps 2-4 as SQL (the job itself is #197): tombstone for a purchaser, else delete.</summary>
    async Task HardDelete(Guid p, Guid? tombstone)
    {
        string financial = tombstone is Guid t
            ? $@"insert into accounts (player_id, display_name, avatar_id, region, age_gate, status)
                   values ('{t}', '', 'avatar_default', 'ZZ', 0, 'deleted') on conflict do nothing;
                 update purchases set player_id = '{t}' where player_id = '{p}';
                 update summon_log set player_id = '{t}' where player_id = '{p}';
                 update diamond_ledger set player_id = '{t}' where player_id = '{p}';"
            : $@"delete from summon_log where player_id = '{p}';
                 delete from diamond_ledger where player_id = '{p}';";
        await using var conn = await _ds!.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand($@"{financial}
delete from accounts where player_id = '{p}';
delete from idempotency where player_id = '{p}';", conn, tx))
            await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    async Task<Dictionary<string, long>> RowsReferencing(Guid id)
    {
        var cols = await Rows(@"
select c.relname::text, a.attname::text
from pg_attribute a join pg_class c on c.oid = a.attrelid join pg_namespace n on n.oid = c.relnamespace
where n.nspname = 'public' and c.relkind in ('r', 'p') and not c.relispartition and a.attnum > 0 and not a.attisdropped
  and format_type(a.atttypid, a.atttypmod) = 'uuid' and a.attname in ('player_id', 'leader_id')");
        var d = new Dictionary<string, long>();
        foreach (var r in cols)
        {
            long n = await Count($"select count(*) from {(string)r[0]} where {(string)r[1]} = '{id}'");
            if (n > 0) d[(string)r[0] + "." + (string)r[1]] = n;
        }
        return d;
    }

    [Test]
    public async Task Hard_delete_of_a_purchaser_leaves_only_tombstoned_financial_rows()
    {
        Guid p = Guid.NewGuid(), t = Guid.NewGuid(), guild = Guid.NewGuid();
        await Populate(p, purchaser: true, guild);
        Assert.That((await RowsReferencing(p)).Count, Is.GreaterThanOrEqualTo(18), "the fixture touches every player-keyed table");

        await HardDelete(p, t);
        // Rerunning the tombstone insert is harmless (on conflict do nothing).
        await HardDelete(p, t);

        Assert.That(await RowsReferencing(p), Is.Empty);
        Assert.That(await RowsReferencing(t), Is.EquivalentTo(new Dictionary<string, long>
        {
            ["accounts.player_id"] = 1, ["purchases.player_id"] = 1, ["summon_log.player_id"] = 1, ["diamond_ledger.player_id"] = 1,
        }));
        Assert.That(await Count($"select count(*) from accounts where player_id = '{t}' and status = 'deleted'"), Is.EqualTo(1));
        Assert.That(await Count($"select count(*) from guilds where guild_id = '{guild}' and leader_id is null"), Is.EqualTo(1));
        Assert.That(await Count($"select count(*) from guild_chat where guild_id = '{guild}' and player_id is null"), Is.EqualTo(1));
    }

    [Test]
    public async Task Hard_delete_of_a_non_purchaser_leaves_nothing()
    {
        Guid p = Guid.NewGuid();
        await Populate(p, purchaser: false, Guid.NewGuid());
        await Exec($"insert into diamond_ledger (player_id, delta, reason) values ('{p}', 30, 'LOGIN')");
        await HardDelete(p, null);
        Assert.That(await RowsReferencing(p), Is.Empty);
    }

    [TestCase("purchases")]
    [TestCase("summon_log")]
    [TestCase("diamond_ledger")]
    public async Task Deleting_an_account_that_still_has_financial_rows_fails(string table)
    {
        Guid p = Guid.NewGuid();
        await Exec(NewAccount(p));
        string insert = table switch
        {
            "purchases" => $"insert into purchases (purchase_id, player_id, platform, sku, transaction_id, status) values ('{Guid.NewGuid()}', '{p}', 'google_play', 'pack', '{Guid.NewGuid()}', 'granted')",
            "summon_log" => $@"insert into summon_log (player_id, request_id, banner, batch_index, item_id, rarity, pity_before, pity_after,
                banner_level, guarantee, rate_table_hash, u1, u2, u3, payment) values ('{p}', 'r', 'SKILL', 0, 'skl_001', 'R', 0, 1, 1, false, '\x00', 0, 0, 0, 'TICKET')",
            _ => $"insert into diamond_ledger (player_id, delta, reason) values ('{p}', 30, 'LOGIN')",
        };
        await Exec(insert);
        var e = Assert.ThrowsAsync<PostgresException>(() => Exec($"delete from accounts where player_id = '{p}'"));
        Assert.That(e!.SqlState, Is.EqualTo(PostgresErrorCodes.ForeignKeyViolation));
        Assert.That(await Count($"select count(*) from accounts where player_id = '{p}'"), Is.EqualTo(1));
    }
}
