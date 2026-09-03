using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public sealed class DatabaseInitializer(string connectionString)
{
    public void Initialize()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());

        if (version < 1)
        {
            ApplyVersion1(connection);
            version = 1;
        }

        if (version > 1)
        {
            throw new InvalidOperationException($"Database version {version} is newer than this application supports.");
        }
    }

    private static void ApplyVersion1(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE GeometryProfiles (
                Id INTEGER PRIMARY KEY CHECK (Id BETWEEN 1 AND 255),
                HSH INTEGER NOT NULL CHECK (HSH BETWEEN 0 AND 63),
                VSL INTEGER NOT NULL CHECK (VSL BETWEEN 0 AND 63),
                VAM INTEGER NOT NULL CHECK (VAM BETWEEN 0 AND 63),
                VSC INTEGER NOT NULL CHECK (VSC BETWEEN 0 AND 63),
                VSH INTEGER NOT NULL CHECK (VSH BETWEEN 0 AND 63),
                Notes TEXT NULL
            );
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
