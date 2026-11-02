using System.Data.Common;

namespace HeroCat.Server.Infrastructure.Db;

/// <summary>Parameterised commands over a provider-neutral <see cref="DbConnection"/> (names are <c>@name</c> in the SQL).</summary>
public static class DbCommands
{
    public static DbCommand Command(this DbConnection c, string sql, DbTransaction? tx, params (string name, object? value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}
