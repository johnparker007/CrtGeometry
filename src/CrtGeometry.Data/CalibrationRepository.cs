using CrtGeometry.Core;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public sealed record CalibrationApplyResult(int ProfileId, PropagationPreview Preview);

/// <summary>Owns profile reuse, signature mappings, propagation, and override precedence.</summary>
public sealed class CalibrationRepository(string connectionString, VideoSignatureService? signatures = null)
{
    private readonly VideoSignatureService _signatures = signatures ?? new();

    public PropagationPreview Preview(string sourceRomName, CalibrationValues values)
    {
        var catalogue = new GameCatalogueRepository(connectionString);
        var source = catalogue.Search(new() { SearchText=sourceRomName, Inclusion=InclusionFilter.All, Presence=PresenceFilter.All })
            .SingleOrDefault(x => x.RomName.Equals(sourceRomName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected MAME game no longer exists.");
        var selection = _signatures.SelectPrimary(source.Displays);
        if (selection.Signature is not VideoSignature signature) throw new InvalidOperationException(selection.Message);
        var profiles = new GeometryProfileRepository(connectionString).GetAll();
        var existing = profiles.FirstOrDefault(p => Same(p, values));
        var profileId = existing?.Id ?? ProfileIdAllocator.GetLowestAvailable(profiles.Select(p => p.Id));
        var matches = catalogue.Search(new()).Where(g =>
            _signatures.SelectPrimary(g.Displays).Signature == signature &&
            (string.IsNullOrWhiteSpace(g.CloneOf) || g.RomName.Equals(source.RomName, StringComparison.OrdinalIgnoreCase))).ToList();
        return new(source.RomName, signature, profileId, existing is not null, matches);
    }

    /// <summary>Rebuilds the preview from current database and form values immediately before applying it.</summary>
    public CalibrationApplyResult PreviewAndApply(string sourceRomName, CalibrationValues values, string? notes = null)
    {
        var preview = Preview(sourceRomName, values);
        return new(Apply(preview, values, notes), preview);
    }

    public int Apply(PropagationPreview preview, CalibrationValues values, string? notes = null)
    {
        using var connection = Open(); using var tx = connection.BeginTransaction();
        var identical = FindIdenticalProfile(connection, tx, values);
        var profileId = identical ?? preview.ProfileId;
        if (identical is null)
        {
            using var profile = connection.CreateCommand(); profile.Transaction = tx;
            profile.CommandText = "INSERT INTO GeometryProfiles(Id,HSH,VSL,VAM,VSC,VSH,Notes) VALUES($id,$h,$l,$a,$s,$v,$n);";
            profile.Parameters.AddWithValue("$id", profileId); profile.Parameters.AddWithValue("$h", values.HSH);
            profile.Parameters.AddWithValue("$l", values.VSL); profile.Parameters.AddWithValue("$a", values.VAM);
            profile.Parameters.AddWithValue("$s", values.VSC); profile.Parameters.AddWithValue("$v", values.VSH);
            profile.Parameters.AddWithValue("$n", (object?)notes ?? DBNull.Value); profile.ExecuteNonQuery();
        }
        var calibrationId = InsertCalibration(connection, tx, profileId, preview.SourceRomName, preview.Signature);
        using (var mapping = connection.CreateCommand())
        {
            mapping.Transaction = tx; mapping.CommandText = """
                INSERT INTO VideoProfileMappings(Width,Height,Rotation,RefreshMicroHz,ProfileId,CalibrationId)
                VALUES($w,$h,$r,$f,$p,$c) ON CONFLICT(Width,Height,Rotation,RefreshMicroHz)
                DO UPDATE SET ProfileId=excluded.ProfileId,CalibrationId=excluded.CalibrationId;
                """;
            SignatureParameters(mapping, preview.Signature); mapping.Parameters.AddWithValue("$p",profileId);
            mapping.Parameters.AddWithValue("$c",calibrationId); mapping.ExecuteNonQuery();
        }
        // Remove legacy automatic clone rows for this signature. Manual overrides
        // are intentionally untouched, and an explicit clone source is re-added
        // below as the sole clone exception.
        using (var removeClones = connection.CreateCommand())
        {
            removeClones.Transaction = tx;
            removeClones.CommandText = """
                DELETE FROM GameProfileAssignments
                WHERE AssignmentType=1 AND Width=$w AND Height=$h AND Rotation=$r AND RefreshMicroHz=$f
                  AND RomName IN (SELECT RomName FROM MameMachines WHERE CloneOf IS NOT NULL AND trim(CloneOf)<>'');
                """;
            SignatureParameters(removeClones, preview.Signature);
            removeClones.ExecuteNonQuery();
        }
        foreach (var game in preview.MatchingGames.Where(x => x.IsIncluded && x.IsPresent &&
                     (string.IsNullOrWhiteSpace(x.CloneOf) || x.RomName.Equals(preview.SourceRomName, StringComparison.OrdinalIgnoreCase))))
            UpsertAutomatic(connection, tx, game.RomName, profileId, preview.Signature);
        tx.Commit(); return profileId;
    }

    public void AssignManual(string romName, int profileId)
    {
        using var c=Open(); using var command=c.CreateCommand(); command.CommandText="""
            INSERT INTO GameProfileAssignments(RomName,ProfileId,AssignmentType,UpdatedAtUtc)
            VALUES($r,$p,2,$at) ON CONFLICT(RomName) DO UPDATE SET ProfileId=excluded.ProfileId,AssignmentType=2,
            Width=NULL,Height=NULL,Rotation=NULL,RefreshMicroHz=NULL,UpdatedAtUtc=excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$r",romName); command.Parameters.AddWithValue("$p",profileId);
        command.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }

    public void RemoveManualOverride(string romName)
    {
        var game = new GameCatalogueRepository(connectionString).Search(new() { SearchText=romName, Inclusion=InclusionFilter.All, Presence=PresenceFilter.All })
            .Single(x => x.RomName == romName);
        var signature = _signatures.SelectPrimary(game.Displays).Signature;
        using var c=Open(); using var tx=c.BeginTransaction();
        using var remove=c.CreateCommand(); remove.Transaction=tx;
        remove.CommandText="DELETE FROM GameProfileAssignments WHERE RomName=$r AND AssignmentType=2;";
        remove.Parameters.AddWithValue("$r",romName); remove.ExecuteNonQuery();
        if (signature is VideoSignature s)
        {
            using var mapping=c.CreateCommand(); mapping.Transaction=tx;
            mapping.CommandText="SELECT ProfileId FROM VideoProfileMappings WHERE Width=$w AND Height=$h AND Rotation=$r AND RefreshMicroHz=$f;";
            SignatureParameters(mapping,s); var value=mapping.ExecuteScalar();
            if (value is not null) UpsertAutomatic(c,tx,romName,Convert.ToInt32(value),s);
        }
        tx.Commit();
    }

    private static void UpsertAutomatic(SqliteConnection c, SqliteTransaction tx, string rom, int profile, VideoSignature s)
    {
        using var command=c.CreateCommand(); command.Transaction=tx; command.CommandText="""
            INSERT INTO GameProfileAssignments(RomName,ProfileId,AssignmentType,Width,Height,Rotation,RefreshMicroHz,UpdatedAtUtc)
            VALUES($rom,$p,1,$w,$h,$r,$f,$at) ON CONFLICT(RomName) DO UPDATE SET ProfileId=excluded.ProfileId,
            AssignmentType=1,Width=excluded.Width,Height=excluded.Height,Rotation=excluded.Rotation,
            RefreshMicroHz=excluded.RefreshMicroHz,UpdatedAtUtc=excluded.UpdatedAtUtc
            WHERE GameProfileAssignments.AssignmentType=1;
            """;
        command.Parameters.AddWithValue("$rom",rom); command.Parameters.AddWithValue("$p",profile);
        command.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); SignatureParameters(command,s); command.ExecuteNonQuery();
    }
    private static long InsertCalibration(SqliteConnection c, SqliteTransaction tx, int profile, string rom, VideoSignature s)
    {
        using var command=c.CreateCommand(); command.Transaction=tx; command.CommandText="""
            INSERT INTO CalibrationRecords(ProfileId,SourceRomName,Width,Height,Rotation,RefreshMicroHz,CreatedAtUtc)
            VALUES($p,$rom,$w,$h,$r,$f,$at); SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$p",profile); command.Parameters.AddWithValue("$rom",rom);
        command.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); SignatureParameters(command,s);
        return (long)command.ExecuteScalar()!;
    }
    private static int? FindIdenticalProfile(SqliteConnection c, SqliteTransaction tx, CalibrationValues v)
    {
        using var command=c.CreateCommand(); command.Transaction=tx;
        command.CommandText="SELECT Id FROM GeometryProfiles WHERE HSH=$h AND VSL=$l AND VAM=$a AND VSC=$s AND VSH=$v ORDER BY Id LIMIT 1;";
        command.Parameters.AddWithValue("$h",v.HSH); command.Parameters.AddWithValue("$l",v.VSL); command.Parameters.AddWithValue("$a",v.VAM);
        command.Parameters.AddWithValue("$s",v.VSC); command.Parameters.AddWithValue("$v",v.VSH);
        var result=command.ExecuteScalar(); return result is null?null:Convert.ToInt32(result);
    }
    private static bool Same(GeometryProfile p, CalibrationValues v) => p.HSH==v.HSH&&p.VSL==v.VSL&&p.VAM==v.VAM&&p.VSC==v.VSC&&p.VSH==v.VSH;
    private static void SignatureParameters(SqliteCommand c, VideoSignature s)
    { c.Parameters.AddWithValue("$w",s.Width); c.Parameters.AddWithValue("$h",s.Height); c.Parameters.AddWithValue("$r",s.Rotation); c.Parameters.AddWithValue("$f",s.RefreshMicroHz); }
    private SqliteConnection Open()
    {
        return SqliteConnectionFactory.Open(connectionString);
    }
}
