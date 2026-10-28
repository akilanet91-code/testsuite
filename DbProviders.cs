using System.Data.Common;

namespace TestFlow;

/// <summary>ADO.NET providers. DbProvider values: SqlServer (default) | Oracle | PostgreSql | MySql | Sqlite.</summary>
public static class Db
{
    static string Key(string? provider)
    {
        var k = (provider ?? "").Trim().ToLowerInvariant();
        return k switch
        {
            "" or "sqlserver" or "mssql" or "sql server" => "sqlserver",
            "oracle" => "oracle",
            "postgresql" or "postgres" or "npgsql" => "postgresql",
            "mysql" or "mariadb" => "mysql",
            "sqlite" => "sqlite",
            _ => throw new NotSupportedException($"DbProvider “{provider}” is not supported (use SqlServer, Oracle, PostgreSql, MySql or Sqlite)")
        };
    }

    public static DbConnection Create(string? provider, string connectionString) => Key(provider) switch
    {
        "sqlserver" => new Microsoft.Data.SqlClient.SqlConnection(connectionString),
        "oracle" => new Oracle.ManagedDataAccess.Client.OracleConnection(connectionString),
        "postgresql" => new Npgsql.NpgsqlConnection(connectionString),
        "mysql" => new MySqlConnector.MySqlConnection(connectionString),
        _ => new Microsoft.Data.Sqlite.SqliteConnection(connectionString),
    };

    /// <summary>Placeholder text used in the SQL for the i-th parameter (Oracle uses :p0, the others @p0).</summary>
    public static string Marker(string? provider, int i) => (Key(provider) == "oracle" ? ":p" : "@p") + i;

    /// <summary>Name given to the DbParameter for the i-th parameter.</summary>
    public static string ParamName(string? provider, int i) => (Key(provider) == "oracle" ? "p" : "@p") + i;
}
