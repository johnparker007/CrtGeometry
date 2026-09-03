using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

/// <summary>
/// The single application entry point for SQLite connections. SQLite foreign-key
/// enforcement is connection-local, so the connection string is normalized here
/// before every open rather than relying on callers to remember a PRAGMA.
/// </summary>
public static class SqliteConnectionFactory
{
    public static SqliteConnection Open(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            ForeignKeys = true
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }
}
