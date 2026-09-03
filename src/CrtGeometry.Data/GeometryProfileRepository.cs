using CrtGeometry.Core;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public sealed class GeometryProfileRepository(string connectionString)
{
    public IReadOnlyList<GeometryProfile> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, HSH, VSL, VAM, VSC, VSH, Notes FROM GeometryProfiles ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var profiles = new List<GeometryProfile>();
        while (reader.Read())
        {
            profiles.Add(new GeometryProfile
            {
                Id = reader.GetInt32(0), HSH = reader.GetInt32(1), VSL = reader.GetInt32(2),
                VAM = reader.GetInt32(3), VSC = reader.GetInt32(4), VSH = reader.GetInt32(5),
                Notes = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return profiles;
    }

    public void Save(GeometryProfile profile)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GeometryProfiles (Id, HSH, VSL, VAM, VSC, VSH, Notes)
            VALUES ($id, $hsh, $vsl, $vam, $vsc, $vsh, $notes)
            ON CONFLICT(Id) DO UPDATE SET HSH=$hsh, VSL=$vsl, VAM=$vam, VSC=$vsc, VSH=$vsh, Notes=$notes;
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$hsh", profile.HSH);
        command.Parameters.AddWithValue("$vsl", profile.VSL);
        command.Parameters.AddWithValue("$vam", profile.VAM);
        command.Parameters.AddWithValue("$vsc", profile.VSC);
        command.Parameters.AddWithValue("$vsh", profile.VSH);
        command.Parameters.AddWithValue("$notes", (object?)profile.Notes ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM GeometryProfiles WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
