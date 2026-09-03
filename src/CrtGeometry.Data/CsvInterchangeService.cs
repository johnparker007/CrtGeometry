using System.Globalization;
using System.IO.Compression;
using CsvHelper;
using CsvHelper.Configuration;
using CrtGeometry.Core;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public enum CsvImportMode { Merge, Replace }

public sealed class CsvImportPreview
{
    internal CsvImportPreview(CsvImportData data, CsvImportMode mode) { Data = data; Mode = mode; }
    internal CsvImportData Data { get; }
    internal CsvImportMode Mode { get; }
    public int ProfilesFound => Data.Profiles.Count;
    public int CalibrationsFound => Data.Calibrations.Count;
    public int MappingsFound => Data.Mappings.Count;
    public int AssignmentsFound => Data.Assignments.Count;
    public int Inserts { get; internal set; }
    public int Updates { get; internal set; }
    public IReadOnlyList<string> Errors => Data.Errors;
    public IReadOnlyList<string> UnresolvedRomNames => Data.UnresolvedRomNames.Order(StringComparer.OrdinalIgnoreCase).ToList();
    public bool IsValid => Errors.Count == 0;
}

internal sealed class CsvImportData
{
    public List<ProfileCsv> Profiles { get; } = [];
    public List<CalibrationCsv> Calibrations { get; } = [];
    public List<MappingCsv> Mappings { get; } = [];
    public List<AssignmentCsv> Assignments { get; } = [];
    public List<string> Errors { get; } = [];
    public HashSet<string> UnresolvedRomNames { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record ProfileCsv(int Id, int HSH, int VSL, int VAM, int VSC, int VSH, string? Notes);
internal sealed record CalibrationCsv(long CalibrationId, int ProfileId, string SourceRomName, int Width, int Height, int Rotation, long RefreshMicroHz, string CreatedAtUtc);
internal sealed record MappingCsv(int Width, int Height, int Rotation, long RefreshMicroHz, int ProfileId, long CalibrationId);
internal sealed record AssignmentCsv(string RomName, int ProfileId, string AssignmentType, int? Width, int? Height, int? Rotation, long? RefreshMicroHz, string UpdatedAtUtc);

/// <summary>Deterministic, transactional ZIP-of-CSV interchange for application-owned state.</summary>
public sealed class CsvInterchangeService(string connectionString)
{
    private static readonly CsvConfiguration Configuration = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        NewLine = "\r\n",
        Mode = CsvMode.RFC4180
    };

    public void Export(string zipPath)
    {
        using var connection = SqliteConnectionFactory.Open(connectionString);
        using var file = File.Create(zipPath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        Write(archive, "metadata.csv", [new { FormatVersion = 1 }]);
        WriteQuery(archive, connection, "profiles.csv", "SELECT Id,HSH,VSL,VAM,VSC,VSH,Notes FROM GeometryProfiles ORDER BY Id", r =>
            new ProfileCsv(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),r.IsDBNull(6)?null:r.GetString(6)));
        WriteQuery(archive, connection, "calibrations.csv", "SELECT Id,ProfileId,SourceRomName,Width,Height,Rotation,RefreshMicroHz,CreatedAtUtc FROM CalibrationRecords ORDER BY Width,Height,Rotation,RefreshMicroHz,Id", r =>
            new CalibrationCsv(r.GetInt64(0),r.GetInt32(1),r.GetString(2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),r.GetInt64(6),r.GetString(7)));
        WriteQuery(archive, connection, "mappings.csv", "SELECT Width,Height,Rotation,RefreshMicroHz,ProfileId,CalibrationId FROM VideoProfileMappings ORDER BY Width,Height,Rotation,RefreshMicroHz", r =>
            new MappingCsv(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt64(3),r.GetInt32(4),r.GetInt64(5)));
        WriteQuery(archive, connection, "assignments.csv", "SELECT RomName,ProfileId,CASE AssignmentType WHEN 1 THEN 'Automatic' ELSE 'Manual' END,Width,Height,Rotation,RefreshMicroHz,UpdatedAtUtc FROM GameProfileAssignments ORDER BY RomName COLLATE NOCASE,RomName", r =>
            new AssignmentCsv(r.GetString(0),r.GetInt32(1),r.GetString(2),NullableInt(r,3),NullableInt(r,4),NullableInt(r,5),NullableLong(r,6),r.GetString(7)));
    }

    public CsvImportPreview Validate(string zipPath, CsvImportMode mode)
    {
        var data = new CsvImportData();
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            ValidateMetadata(archive, data);
            Read(archive,"profiles.csv",data.Profiles,data); Read(archive,"calibrations.csv",data.Calibrations,data);
            Read(archive,"mappings.csv",data.Mappings,data); Read(archive,"assignments.csv",data.Assignments,data);
            for (var i = 0; i < data.Profiles.Count; i++)
                if (data.Profiles[i].Notes == string.Empty) data.Profiles[i] = data.Profiles[i] with { Notes = null };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        { data.Errors.Add($"Archive parse error: {ex.Message}"); }
        ValidateData(data, mode);
        var preview = new CsvImportPreview(data, mode);
        CountChanges(preview, mode);
        return preview;
    }

    public void Apply(CsvImportPreview preview, CsvImportMode mode)
    {
        if (!preview.IsValid) throw new InvalidOperationException("The import contains validation errors.");
        if (preview.Mode != mode) throw new InvalidOperationException("Apply mode must match the validated preview mode.");
        using var c=SqliteConnectionFactory.Open(connectionString); using var tx=c.BeginTransaction();
        if (mode == CsvImportMode.Replace)
        {
            Execute(c,tx,"DELETE FROM GameProfileAssignments; DELETE FROM VideoProfileMappings; DELETE FROM CalibrationRecords; DELETE FROM GeometryProfiles;");
        }
        foreach(var p in preview.Data.Profiles) Execute(c,tx,"""
            INSERT INTO GeometryProfiles(Id,HSH,VSL,VAM,VSC,VSH,Notes) VALUES($id,$h,$l,$a,$s,$v,$n)
            ON CONFLICT(Id) DO UPDATE SET HSH=excluded.HSH,VSL=excluded.VSL,VAM=excluded.VAM,VSC=excluded.VSC,VSH=excluded.VSH,Notes=excluded.Notes;
            """,("$id",p.Id),("$h",p.HSH),("$l",p.VSL),("$a",p.VAM),("$s",p.VSC),("$v",p.VSH),("$n",p.Notes));
        foreach(var x in preview.Data.Calibrations) Execute(c,tx,"""
            INSERT INTO CalibrationRecords(Id,ProfileId,SourceRomName,Width,Height,Rotation,RefreshMicroHz,CreatedAtUtc) VALUES($id,$p,$rom,$w,$h,$r,$f,$at)
            ON CONFLICT(Id) DO UPDATE SET ProfileId=excluded.ProfileId,SourceRomName=excluded.SourceRomName,Width=excluded.Width,Height=excluded.Height,Rotation=excluded.Rotation,RefreshMicroHz=excluded.RefreshMicroHz,CreatedAtUtc=excluded.CreatedAtUtc;
            """,("$id",x.CalibrationId),("$p",x.ProfileId),("$rom",x.SourceRomName),("$w",x.Width),("$h",x.Height),("$r",x.Rotation),("$f",x.RefreshMicroHz),("$at",x.CreatedAtUtc));
        foreach(var x in preview.Data.Mappings) Execute(c,tx,"""
            INSERT INTO VideoProfileMappings(Width,Height,Rotation,RefreshMicroHz,ProfileId,CalibrationId) VALUES($w,$h,$r,$f,$p,$c)
            ON CONFLICT(Width,Height,Rotation,RefreshMicroHz) DO UPDATE SET ProfileId=excluded.ProfileId,CalibrationId=excluded.CalibrationId;
            """,("$w",x.Width),("$h",x.Height),("$r",x.Rotation),("$f",x.RefreshMicroHz),("$p",x.ProfileId),("$c",x.CalibrationId));
        foreach(var x in preview.Data.Assignments) Execute(c,tx,"""
            INSERT INTO GameProfileAssignments(RomName,ProfileId,AssignmentType,Width,Height,Rotation,RefreshMicroHz,UpdatedAtUtc) VALUES($rom,$p,$t,$w,$h,$r,$f,$at)
            ON CONFLICT(RomName) DO UPDATE SET ProfileId=excluded.ProfileId,AssignmentType=excluded.AssignmentType,Width=excluded.Width,Height=excluded.Height,Rotation=excluded.Rotation,RefreshMicroHz=excluded.RefreshMicroHz,UpdatedAtUtc=excluded.UpdatedAtUtc;
            """,("$rom",x.RomName),("$p",x.ProfileId),("$t",x.AssignmentType=="Automatic"?1:2),("$w",x.Width),("$h",x.Height),("$r",x.Rotation),("$f",x.RefreshMicroHz),("$at",x.UpdatedAtUtc));
        tx.Commit();
    }

    private void ValidateData(CsvImportData d, CsvImportMode mode)
    {
        Duplicate(d.Profiles.Select(x=>x.Id),"profile ID",d); Duplicate(d.Calibrations.Select(x=>x.CalibrationId),"calibration ID",d);
        Duplicate(d.Mappings.Select(x=>(x.Width,x.Height,x.Rotation,x.RefreshMicroHz)),"mapping signature",d);
        Duplicate(d.Assignments.Select(x=>x.RomName.ToUpperInvariant()),"assignment ROM",d);
        foreach(var p in d.Profiles)
        { if(p.Id is <1 or >255)d.Errors.Add($"Profile {p.Id}: ID must be 1..255."); if(new[]{p.HSH,p.VSL,p.VAM,p.VSC,p.VSH}.Any(v=>v is <0 or >63))d.Errors.Add($"Profile {p.Id}: geometry values must be 0..63."); }
        using var c=SqliteConnectionFactory.Open(connectionString);
        var existingProfiles = mode==CsvImportMode.Merge ? Values<int>(c,"SELECT Id FROM GeometryProfiles") : [];
        var profiles=d.Profiles.Select(x=>x.Id).Concat(existingProfiles).ToHashSet();
        var calibrationIds=d.Calibrations.Select(x=>x.CalibrationId).ToHashSet();
        var roms=Values<string>(c,"SELECT RomName FROM MameMachines").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var x in d.Calibrations) { CheckProfile(x.ProfileId,$"Calibration {x.CalibrationId}",profiles,d); CheckSignature(x.Width,x.Height,x.Rotation,x.RefreshMicroHz,$"Calibration {x.CalibrationId}",d); CheckRom(x.SourceRomName,roms,d); if(x.CalibrationId<1)d.Errors.Add($"Calibration ID {x.CalibrationId} must be positive."); if(!DateTimeOffset.TryParse(x.CreatedAtUtc,CultureInfo.InvariantCulture,DateTimeStyles.None,out _))d.Errors.Add($"Calibration {x.CalibrationId}: invalid CreatedAtUtc."); }
        foreach(var x in d.Mappings) { CheckProfile(x.ProfileId,"Mapping",profiles,d); CheckSignature(x.Width,x.Height,x.Rotation,x.RefreshMicroHz,"Mapping",d); if(!calibrationIds.Contains(x.CalibrationId))d.Errors.Add($"Mapping references missing calibration {x.CalibrationId}."); else { var cal=d.Calibrations.First(y=>y.CalibrationId==x.CalibrationId); if(cal.ProfileId!=x.ProfileId || (cal.Width,cal.Height,cal.Rotation,cal.RefreshMicroHz)!=(x.Width,x.Height,x.Rotation,x.RefreshMicroHz))d.Errors.Add($"Mapping calibration {x.CalibrationId} does not match its profile/signature."); } }
        foreach(var x in d.Assignments) { CheckProfile(x.ProfileId,$"Assignment {x.RomName}",profiles,d); CheckRom(x.RomName,roms,d); if(x.AssignmentType is not ("Automatic" or "Manual"))d.Errors.Add($"Assignment {x.RomName}: type must be Automatic or Manual."); if(x.AssignmentType=="Automatic") { if(x.Width is null||x.Height is null||x.Rotation is null||x.RefreshMicroHz is null)d.Errors.Add($"Assignment {x.RomName}: automatic signature is required."); else CheckSignature(x.Width.Value,x.Height.Value,x.Rotation.Value,x.RefreshMicroHz.Value,$"Assignment {x.RomName}",d); } else if(x.Width is not null||x.Height is not null||x.Rotation is not null||x.RefreshMicroHz is not null)d.Errors.Add($"Assignment {x.RomName}: manual signature fields must be empty."); if(!DateTimeOffset.TryParse(x.UpdatedAtUtc,CultureInfo.InvariantCulture,DateTimeStyles.None,out _))d.Errors.Add($"Assignment {x.RomName}: invalid UpdatedAtUtc."); }
    }

    private void CountChanges(CsvImportPreview p, CsvImportMode mode)
    {
        if(mode==CsvImportMode.Replace){p.Inserts=p.ProfilesFound+p.CalibrationsFound+p.MappingsFound+p.AssignmentsFound;return;}
        using var c=SqliteConnectionFactory.Open(connectionString);
        var existingProfiles=Values<int>(c,"SELECT Id FROM GeometryProfiles").ToHashSet(); var existingCal=Values<long>(c,"SELECT Id FROM CalibrationRecords").ToHashSet(); var existingRoms=Values<string>(c,"SELECT RomName FROM GameProfileAssignments").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings=Values<string>(c,"SELECT Width||':'||Height||':'||Rotation||':'||RefreshMicroHz FROM VideoProfileMappings").ToHashSet();
        var updates=p.Data.Profiles.Count(x=>existingProfiles.Contains(x.Id))+p.Data.Calibrations.Count(x=>existingCal.Contains(x.CalibrationId))+p.Data.Assignments.Count(x=>existingRoms.Contains(x.RomName))+p.Data.Mappings.Count(x=>mappings.Contains($"{x.Width}:{x.Height}:{x.Rotation}:{x.RefreshMicroHz}"));
        p.Updates=updates;p.Inserts=p.ProfilesFound+p.CalibrationsFound+p.MappingsFound+p.AssignmentsFound-updates;
    }

    private static void ValidateMetadata(ZipArchive a,CsvImportData d){var rows=new List<MetadataCsv>();Read(a,"metadata.csv",rows,d);if(rows.Count!=1||rows[0].FormatVersion!=1)d.Errors.Add("metadata.csv must contain exactly format version 1.");}
    private sealed record MetadataCsv(int FormatVersion);
    private static void Read<T>(ZipArchive a,string name,List<T> target,CsvImportData d){var e=a.GetEntry(name);if(e is null){d.Errors.Add($"Missing required file {name}.");return;}try{using var s=e.Open();using var reader=new StreamReader(s,new System.Text.UTF8Encoding(false),true);using var csv=new CsvReader(reader,Configuration);target.AddRange(csv.GetRecords<T>());}catch(Exception ex){d.Errors.Add($"{name} parse error: {ex.Message}");}}
    private static void Write<T>(ZipArchive a,string name,IEnumerable<T> rows){var e=a.CreateEntry(name,CompressionLevel.Optimal);e.LastWriteTime=new DateTimeOffset(1980,1,1,0,0,0,TimeSpan.Zero);using var s=e.Open();using var writer=new StreamWriter(s,new System.Text.UTF8Encoding(false),1024,false);using var csv=new CsvWriter(writer,Configuration);csv.WriteHeader<T>();csv.NextRecord();csv.WriteRecords(rows);}
    private static void WriteQuery<T>(ZipArchive a,SqliteConnection c,string name,string sql,Func<SqliteDataReader,T> map){using var cmd=c.CreateCommand();cmd.CommandText=sql;using var r=cmd.ExecuteReader();var rows=new List<T>();while(r.Read())rows.Add(map(r));Write(a,name,rows);}
    private static int? NullableInt(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt32(i); private static long? NullableLong(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt64(i);
    private static List<T> Values<T>(SqliteConnection c,string sql){using var cmd=c.CreateCommand();cmd.CommandText=sql;using var r=cmd.ExecuteReader();var result=new List<T>();while(r.Read())result.Add((T)Convert.ChangeType(r.GetValue(0),typeof(T),CultureInfo.InvariantCulture));return result;}
    private static void Duplicate<T>(IEnumerable<T> values,string label,CsvImportData d) where T:notnull {foreach(var x in values.GroupBy(x=>x).Where(g=>g.Count()>1))d.Errors.Add($"Duplicate {label}: {x.Key}.");}
    private static void CheckProfile(int id,string context,HashSet<int> ids,CsvImportData d){if(!ids.Contains(id))d.Errors.Add($"{context} references missing profile {id}.");}
    private static void CheckRom(string rom,HashSet<string> roms,CsvImportData d){if(string.IsNullOrWhiteSpace(rom)||!roms.Contains(rom)){d.UnresolvedRomNames.Add(rom);d.Errors.Add($"Unknown MAME ROM: '{rom}'.");}}
    private static void CheckSignature(int w,int h,int r,long f,string context,CsvImportData d){if(w<=0||h<=0||r is <0 or >359||f<=0)d.Errors.Add($"{context}: signature must have positive dimensions/refresh and rotation 0..359.");}
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object? Value)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var x in values)cmd.Parameters.AddWithValue(x.Name,x.Value??DBNull.Value);cmd.ExecuteNonQuery();}
}
