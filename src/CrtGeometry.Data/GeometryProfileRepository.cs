using CrtGeometry.Core;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public sealed class GeometryProfileRepository(string connectionString)
{
    public IReadOnlyList<GeometryProfile> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id,p.HSH,p.VSL,p.VAM,p.VSC,p.VSH,p.Notes,
              (SELECT c.SourceRomName FROM CalibrationRecords c WHERE c.ProfileId=p.Id ORDER BY c.Id DESC LIMIT 1),
              (SELECT m.Description FROM CalibrationRecords c JOIN MameMachines m ON m.RomName=c.SourceRomName WHERE c.ProfileId=p.Id ORDER BY c.Id DESC LIMIT 1),
              (SELECT COUNT(*) FROM GameProfileAssignments a WHERE a.ProfileId=p.Id)
            FROM GeometryProfiles p ORDER BY p.Id;
            """;
        using var reader = command.ExecuteReader();
        var profiles = new List<GeometryProfile>();
        while (reader.Read())
        {
            profiles.Add(new GeometryProfile(reader.GetInt32(0))
            {
                HSH = reader.GetInt32(1), VSL = reader.GetInt32(2),
                VAM = reader.GetInt32(3), VSC = reader.GetInt32(4), VSH = reader.GetInt32(5),
                Notes = reader.IsDBNull(6) ? null : reader.GetString(6),
                CalibrationSourceRomName = reader.IsDBNull(7) ? null : reader.GetString(7),
                CalibrationSourceTitle = reader.IsDBNull(8) ? null : reader.GetString(8),
                AssignedGameCount = reader.GetInt32(9)
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
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON;"; command.ExecuteNonQuery();
        return connection;
    }
}
