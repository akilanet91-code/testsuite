using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace TestFlow;

/// <summary>SQL Server access (Microsoft.Data.SqlClient). The connection string comes from appsettings.json.</summary>
public static class Db
{
    public static DbConnection Create(string connectionString) => new SqlConnection(connectionString);

    /// <summary>Placeholder text used in the SQL for the i-th parameter (@p0, @p1, ...).</summary>
    public static string Marker(int i) => "@p" + i;

    /// <summary>Name given to the DbParameter for the i-th parameter.</summary>
    public static string ParamName(int i) => "@p" + i;
}
