using CrtGeometry.Core;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public sealed class GameCatalogueRepository(string connectionString)
{
    public Task<IReadOnlyList<GameCatalogueEntry>> SearchAsync(GameCatalogueQuery query, CancellationToken cancellationToken = default) =>
        Task.Run(() => Search(query, cancellationToken), cancellationToken);

    public IReadOnlyList<GameCatalogueEntry> Search(GameCatalogueQuery query)
        => Search(query, CancellationToken.None);

    private IReadOnlyList<GameCatalogueEntry> Search(GameCatalogueQuery query, CancellationToken cancellationToken)
    {
        using var connection = SqliteConnectionFactory.Open(connectionString);
        using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (query.Inclusion == InclusionFilter.IncludedOnly) conditions.Add("m.IsIncluded=1");
        if (query.Inclusion == InclusionFilter.ExcludedOnly) conditions.Add("m.IsIncluded=0");
        if (query.Presence == PresenceFilter.PresentOnly) conditions.Add("m.IsPresent=1");
        if (query.Presence == PresenceFilter.AbsentOnly) conditions.Add("m.IsPresent=0");
        if (query.Profile == ProfileFilter.AssignedOnly) conditions.Add("a.ProfileId IS NOT NULL");
        if (query.Profile == ProfileFilter.UnassignedOnly) conditions.Add("a.ProfileId IS NULL");
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            conditions.Add("(instr(lower(COALESCE(m.Description,'')),lower($search))>0 OR instr(lower(m.RomName),lower($search))>0 OR instr(lower(COALESCE(m.Manufacturer,'')),lower($search))>0 OR instr(lower(COALESCE(m.Year,'')),lower($search))>0)");
            command.Parameters.AddWithValue("$search", query.SearchText.Trim());
        }
        command.CommandText = $$"""
            SELECT m.RomName,m.Description,m.Year,m.Manufacturer,m.CloneOf,m.CoinInputs,m.IsIncluded,m.IsPresent,m.ExclusionReasons,
                   a.ProfileId,a.AssignmentType,
                   (SELECT c.SourceRomName FROM CalibrationRecords c WHERE c.ProfileId=a.ProfileId ORDER BY c.Id DESC LIMIT 1),
                   d.DisplayIndex,d.Type,d.Width,d.Height,d.Rotate,d.Refresh,d.PixelClock,d.HTotal,d.HBEnd,d.HBStart,d.VTotal,d.VBEnd,d.VBStart,d.RawAttributesJson
            FROM MameMachines m LEFT JOIN GameProfileAssignments a ON a.RomName=m.RomName
            LEFT JOIN MameDisplays d ON d.RomName=m.RomName
            {{(conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions))}}
            ORDER BY COALESCE(NULLIF(m.Description,''),m.RomName) COLLATE NOCASE,m.RomName COLLATE NOCASE,d.DisplayIndex;
            """;
        var games = new List<GameCatalogueEntry>();
        GameCatalogueEntry? game = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var romName = reader.GetString(0);
            if (game?.RomName != romName)
            {
                game = new GameCatalogueEntry { RomName=romName, Description=Text(reader,1), Year=Text(reader,2), Manufacturer=Text(reader,3),
                    CloneOf=Text(reader,4), CoinInputs=Int(reader,5), IsIncluded=reader.GetBoolean(6), IsPresent=reader.GetBoolean(7),
                    ExclusionReasons=(MameExclusionReason)reader.GetInt32(8), ProfileId=Int(reader,9),
                    AssignmentType=reader.IsDBNull(10)?null:(ProfileAssignmentType)reader.GetInt32(10), CalibrationSourceRomName=Text(reader,11) };
                games.Add(game);
            }
            if (!reader.IsDBNull(12)) game.Displays.Add(new MameDisplay { Type=Text(reader,13), Width=Int(reader,14), Height=Int(reader,15),
                Rotate=Int(reader,16), Refresh=Double(reader,17), PixelClock=Long(reader,18), HTotal=Int(reader,19), HBEnd=Int(reader,20),
                HBStart=Int(reader,21), VTotal=Int(reader,22), VBEnd=Int(reader,23), VBStart=Int(reader,24), RawAttributesJson=reader.GetString(25) });
        }
        return games;
    }

    private static string? Text(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static int? Int(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt32(i);
    private static long? Long(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt64(i);
    private static double? Double(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetDouble(i);
}
