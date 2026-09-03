using CrtGeometry.Core;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public sealed class GameCatalogueRepository(string connectionString)
{
    public IReadOnlyList<GameCatalogueEntry> Search(GameCatalogueQuery query)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (query.Inclusion == InclusionFilter.IncludedOnly) conditions.Add("m.IsIncluded=1");
        if (query.Inclusion == InclusionFilter.ExcludedOnly) conditions.Add("m.IsIncluded=0");
        if (query.Presence == PresenceFilter.PresentOnly) conditions.Add("m.IsPresent=1");
        if (query.Presence == PresenceFilter.AbsentOnly) conditions.Add("m.IsPresent=0");
        // Profile relationships intentionally belong to Phase 4. All imported games are currently unassigned.
        if (query.Profile == ProfileFilter.AssignedOnly) conditions.Add("0=1");
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            conditions.Add("(instr(lower(COALESCE(m.Description,'')),lower($search))>0 OR instr(lower(m.RomName),lower($search))>0 OR instr(lower(COALESCE(m.Manufacturer,'')),lower($search))>0 OR instr(lower(COALESCE(m.Year,'')),lower($search))>0)");
            command.Parameters.AddWithValue("$search", query.SearchText.Trim());
        }
        command.CommandText = $$"""
            SELECT m.RomName,m.Description,m.Year,m.Manufacturer,m.CloneOf,m.CoinInputs,m.IsIncluded,m.IsPresent,m.ExclusionReasons,
                   d.DisplayIndex,d.Type,d.Width,d.Height,d.Rotate,d.Refresh,d.PixelClock,d.HTotal,d.HBEnd,d.HBStart,d.VTotal,d.VBEnd,d.VBStart,d.RawAttributesJson
            FROM MameMachines m LEFT JOIN MameDisplays d ON d.RomName=m.RomName
            {{(conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions))}}
            ORDER BY COALESCE(NULLIF(m.Description,''),m.RomName) COLLATE NOCASE,m.RomName COLLATE NOCASE,d.DisplayIndex;
            """;
        var games = new List<GameCatalogueEntry>();
        GameCatalogueEntry? game = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var romName = reader.GetString(0);
            if (game?.RomName != romName)
            {
                game = new GameCatalogueEntry { RomName=romName, Description=Text(reader,1), Year=Text(reader,2), Manufacturer=Text(reader,3),
                    CloneOf=Text(reader,4), CoinInputs=Int(reader,5), IsIncluded=reader.GetBoolean(6), IsPresent=reader.GetBoolean(7),
                    ExclusionReasons=(MameExclusionReason)reader.GetInt32(8) };
                games.Add(game);
            }
            if (!reader.IsDBNull(9)) game.Displays.Add(new MameDisplay { Type=Text(reader,10), Width=Int(reader,11), Height=Int(reader,12),
                Rotate=Int(reader,13), Refresh=Double(reader,14), PixelClock=Long(reader,15), HTotal=Int(reader,16), HBEnd=Int(reader,17),
                HBStart=Int(reader,18), VTotal=Int(reader,19), VBEnd=Int(reader,20), VBStart=Int(reader,21), RawAttributesJson=reader.GetString(22) });
        }
        return games;
    }

    private static string? Text(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static int? Int(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt32(i);
    private static long? Long(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt64(i);
    private static double? Double(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetDouble(i);
}
