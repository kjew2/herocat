using System.Text;
using System.Text.RegularExpressions;

namespace HeroCat.Server.Tests;

/// <summary>A column as the DDL states it. <see cref="Type"/> is normalised to PostgreSQL's <c>format_type</c> spelling.</summary>
sealed record SqlColumn(string Name, string Type, bool NotNull);

/// <summary>A single-column foreign key. <see cref="OnDelete"/> is "no action" when the DDL names none.</summary>
sealed record SqlForeignKey(string Table, string Column, string RefTable, string OnDelete);

/// <summary>An index; <see cref="Columns"/> is the comma-joined column list.</summary>
sealed record SqlIndex(string Table, string Columns, bool Unique, bool Partial);

sealed class SqlTable
{
    public SqlTable(string name) { Name = name; }
    public string Name { get; }
    public List<SqlColumn> Columns { get; } = new();
    public string? PartitionBy { get; set; }
}

/// <summary>
/// A small reader for the DDL subset the schema uses (specs/02 section 12 and server/db/migrations): create table,
/// alter table ... add column, create [unique] index, create materialized view, create function and create trigger.
/// Anything else that changes the schema (drop, other alters, ...) throws, so a new kind of statement extends this reader
/// instead of being ignored.
/// It exists so tests can hold the migration set to the spec, statically and against a live database.
/// </summary>
sealed class SqlSchemaModel
{
    public Dictionary<string, SqlTable> Tables { get; } = new();
    public List<SqlForeignKey> ForeignKeys { get; } = new();
    public List<SqlIndex> Indexes { get; } = new();
    public List<string> MaterializedViews { get; } = new();
    public List<string> Functions { get; } = new();
    public List<(string Name, string Table)> Triggers { get; } = new();
    /// <summary>Top-level statements that are not DDL (for example <c>select ensure_partitions(...)</c>).</summary>
    public List<string> OtherStatements { get; } = new();

    public static SqlSchemaModel Parse(params string[] sqlTexts)
    {
        var m = new SqlSchemaModel();
        foreach (string sql in sqlTexts)
            foreach (string stmt in Statements(sql)) m.Add(stmt);
        return m;
    }

    /// <summary>The ```sql block of specs/02 section 12.</summary>
    public static string SpecBlock()
    {
        string spec = File.ReadAllText(RepoPaths.Of("specs/02-domain-model-and-persistence.md"));
        int at = spec.IndexOf("## 12. Server-side relational schema", StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("specs/02 has no section 12");
        int b = spec.IndexOf("```sql\n", at, StringComparison.Ordinal) + "```sql\n".Length;
        int e = spec.IndexOf("```\n", b, StringComparison.Ordinal);
        return spec[b..e];
    }

    /// <summary>
    /// Every ```sql block of specs/02 section 12, in order: the first is 0001_init.sql verbatim (<see cref="SpecBlock"/>);
    /// each later one is a later migration file verbatim, its first line <c>-- NNNN_name.sql ...</c> naming the file.
    /// </summary>
    public static List<string> SpecBlocks()
    {
        string spec = File.ReadAllText(RepoPaths.Of("specs/02-domain-model-and-persistence.md"));
        int at = spec.IndexOf("## 12. Server-side relational schema", StringComparison.Ordinal);
        int end = spec.IndexOf("## 13. ", at, StringComparison.Ordinal);
        if (at < 0 || end < 0) throw new InvalidOperationException("specs/02 has no sections 12 and 13");
        var blocks = new List<string>();
        for (int b = spec.IndexOf("```sql\n", at, StringComparison.Ordinal); b >= 0 && b < end; b = spec.IndexOf("```sql\n", b, StringComparison.Ordinal))
        {
            b += "```sql\n".Length;
            int e = spec.IndexOf("```\n", b, StringComparison.Ordinal);
            blocks.Add(spec[b..e]);
            b = e + 4;
        }
        return blocks;
    }

    public static string[] MigrationTexts() =>
        Infrastructure.Db.Migrations.MigrationScript.LoadDirectory(RepoPaths.Of("server/db/migrations")).Select(s => s.Sql).ToArray();

    /// <summary>Splits on top-level semicolons with comments removed; respects quotes, parentheses and $$ bodies.</summary>
    public static List<string> Statements(string sql)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        int depth = 0;
        bool quote = false, dollar = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (!quote && !dollar && c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                cur.Append('\n');
                continue;
            }
            if (!quote && c == '$' && i + 1 < sql.Length && sql[i + 1] == '$')
            {
                dollar = !dollar;
                cur.Append("$$");
                i++;
                continue;
            }
            if (!dollar && c == '\'') quote = !quote;
            if (!quote && !dollar)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ';' && depth == 0)
                {
                    Flush();
                    continue;
                }
            }
            cur.Append(c);
        }
        Flush();
        return list;

        void Flush()
        {
            string s = Regex.Replace(cur.ToString(), @"\s+", " ").Trim();
            if (s.Length > 0) list.Add(s);
            cur.Clear();
        }
    }

    void Add(string stmt)
    {
        string low = stmt.ToLowerInvariant();
        Match x;
        if ((x = Regex.Match(low, @"^create table (\w+) \(")).Success)
        {
            string name = x.Groups[1].Value;
            int open = x.Length - 1;
            int close = Matching(low, open);
            var t = new SqlTable(name);
            var pk = new HashSet<string>();
            foreach (string entry in SplitTopLevel(low[(open + 1)..close])) AddEntry(t, entry, pk);
            for (int i = 0; i < t.Columns.Count; i++)
                if (pk.Contains(t.Columns[i].Name)) t.Columns[i] = t.Columns[i] with { NotNull = true };
            var part = Regex.Match(low[(close + 1)..], @"^\s*partition by (\w+) \((\w+)\)");
            if (part.Success) t.PartitionBy = part.Groups[1].Value + "(" + part.Groups[2].Value + ")";
            Tables.Add(name, t);
        }
        else if ((x = Regex.Match(low, @"^alter table (\w+) (add column .*)$")).Success)
        {
            var t = Tables[x.Groups[1].Value];
            var none = new HashSet<string>();
            foreach (string add in SplitTopLevel(x.Groups[2].Value))
            {
                if (!add.StartsWith("add column ", StringComparison.Ordinal))
                    throw new NotSupportedException("SqlSchemaModel reads only add column in an alter table; extend it: " + stmt);
                AddEntry(t, add["add column ".Length..], none);
            }
        }
        else if ((x = Regex.Match(low, @"^create (unique )?index (?:\w+ )?on (\w+) \(([^)]*)\)( where .*)?$")).Success)
        {
            string cols = string.Join(", ", x.Groups[3].Value.Split(',').Select(c => c.Trim()));
            Indexes.Add(new SqlIndex(x.Groups[2].Value, cols, x.Groups[1].Success, x.Groups[4].Success));
        }
        else if ((x = Regex.Match(low, @"^alter table (\w+) add column (.+)$")).Success)
        {
            if (!Tables.TryGetValue(x.Groups[1].Value, out var t)) throw new InvalidOperationException("alter of an unknown table: " + stmt);
            if (x.Groups[2].Value.Contains(", add ", StringComparison.Ordinal))
                throw new NotSupportedException("one column per alter table statement: " + stmt);
            AddEntry(t, x.Groups[2].Value, new HashSet<string>());
        }
        else if ((x = Regex.Match(low, @"^create materialized view (\w+) as ")).Success) MaterializedViews.Add(x.Groups[1].Value);
        else if ((x = Regex.Match(low, @"^create function (\w+)\(")).Success) Functions.Add(x.Groups[1].Value);
        else if ((x = Regex.Match(low, @"^create trigger (\w+) (?:before|after) insert on (\w+) ")).Success) Triggers.Add((x.Groups[1].Value, x.Groups[2].Value));
        else if (low.StartsWith("select ", StringComparison.Ordinal)) OtherStatements.Add(low);
        else throw new NotSupportedException("SqlSchemaModel does not read this statement; extend it: " + stmt);
    }

    static readonly string[] ColumnEnd = { "not", "null", "default", "primary", "references", "unique", "check", "generated", "collate" };

    void AddEntry(SqlTable t, string entry, HashSet<string> pk)
    {
        var tableConstraint = Regex.Match(entry, @"^(primary key|unique|check|constraint|foreign key)\b");
        if (tableConstraint.Success)
        {
            if (tableConstraint.Value == "primary key")
                foreach (string c in Regex.Match(entry, @"\(([^)]*)\)").Groups[1].Value.Split(',')) pk.Add(c.Trim());
            else if (tableConstraint.Value is "constraint" or "foreign key")
                throw new NotSupportedException("named or table-level foreign key constraints are not read yet: " + entry);
            return;
        }
        var words = entry.Split(' ');
        string name = words[0];
        int k = 1;
        while (k < words.Length && !ColumnEnd.Contains(words[k])) k++;
        string type = Normalise(string.Join(" ", words[1..k]));
        string rest = " " + string.Join(" ", words[k..]) + " ";
        bool notNull = rest.Contains(" not null ") || rest.Contains(" primary key ") || words[1] is "serial" or "bigserial";
        t.Columns.Add(new SqlColumn(name, type, notNull));
        var fk = Regex.Match(rest, @" references (\w+)(?: \((\w+)\))?(?: on delete (cascade|restrict|set null|set default|no action))?");
        if (fk.Success)
            ForeignKeys.Add(new SqlForeignKey(t.Name, name, fk.Groups[1].Value, fk.Groups[3].Success ? fk.Groups[3].Value : "no action"));
    }

    /// <summary>The DDL type spelled as PostgreSQL's format_type prints it.</summary>
    public static string Normalise(string type)
    {
        var ch = Regex.Match(type, @"^char\((\d+)\)$");
        if (ch.Success) return $"character({ch.Groups[1].Value})";
        return type switch
        {
            "timestamptz" => "timestamp with time zone",
            "serial" => "integer",
            "bigserial" => "bigint",
            _ => type,
        };
    }

    static int Matching(string s, int open)
    {
        int depth = 0;
        bool quote = false;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '\'') quote = !quote;
            if (quote) continue;
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        throw new FormatException("unbalanced parentheses: " + s);
    }

    static IEnumerable<string> SplitTopLevel(string body)
    {
        int depth = 0, start = 0;
        bool quote = false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '\'') quote = !quote;
            if (quote) continue;
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return body[start..i].Trim();
                start = i + 1;
            }
        }
        if (body[start..].Trim().Length > 0) yield return body[start..].Trim();
    }
}
