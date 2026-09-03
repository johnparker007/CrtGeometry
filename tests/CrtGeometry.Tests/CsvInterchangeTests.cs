using System.IO.Compression;
using System.Text;
using CrtGeometry.Core;
using CrtGeometry.Data;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Tests;

public sealed class CsvInterchangeTests : IDisposable
{
    private readonly List<string> _paths=[];

    [Fact]
    public void ExportIsDeterministicOrderedAndRfc4180Safe()
    {
        var cs=Database(); SeedCatalogue(cs); var profiles=new GeometryProfileRepository(cs);
        profiles.Save(new GeometryProfile(17){HSH=33,VSL=11,VAM=30,VSC=13,VSH=63,Notes="Unicode 日本語, quote \"yes\"\r\nnext"});
        profiles.Save(new GeometryProfile(2){Notes=null});
        var calibration=new CalibrationRepository(cs); calibration.Apply(calibration.Preview("rtype",new(33,11,30,13,63)),new(33,11,30,13,63));
        calibration.AssignManual("zgame",2);
        var a=Temp(".zip");
        var b=Temp(".zip");
        var service=new CsvInterchangeService(cs); service.Export(a);service.Export(b);
        Assert.Equal(File.ReadAllBytes(a),File.ReadAllBytes(b));
        using var zip=ZipFile.OpenRead(a);
        var profileText=Text(zip,"profiles.csv"); Assert.True(profileText.IndexOf("2,",StringComparison.Ordinal)<profileText.IndexOf("17,",StringComparison.Ordinal));
        Assert.Contains("\"Unicode 日本語, quote \"\"yes\"\"\r\nnext\"",profileText); Assert.Contains("2,0,0,0,0,0,\r\n",profileText);
        var assignments=Text(zip,"assignments.csv"); Assert.True(assignments.IndexOf("rtype",StringComparison.OrdinalIgnoreCase)<assignments.IndexOf("zgame",StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RoundTripPreservesProfilesProvenanceMappingsAndBothAssignmentTypes()
    {
        var source=Database();SeedCatalogue(source);var profiles=new GeometryProfileRepository(source);
        profiles.Save(new GeometryProfile(17){HSH=33,VSL=11,VAM=30,VSC=13,VSH=63,Notes="R-Type, \"measured\"\n再検査"});profiles.Save(new GeometryProfile(4){Notes=null});
        var calibration=new CalibrationRepository(source);calibration.Apply(calibration.Preview("rtype",new(33,11,30,13,63)),new(33,11,30,13,63));calibration.AssignManual("zgame",4);
        var zip=Temp(".zip");new CsvInterchangeService(source).Export(zip);
        var target=Database();SeedCatalogue(target);var service=new CsvInterchangeService(target);var preview=service.Validate(zip,CsvImportMode.Replace);
        Assert.True(preview.IsValid,string.Join("\n",preview.Errors));Assert.Equal(2,preview.ProfilesFound);Assert.Equal(1,preview.CalibrationsFound);Assert.Equal(1,preview.MappingsFound);Assert.Equal(2,preview.AssignmentsFound);
        service.Apply(preview,CsvImportMode.Replace);
        Assert.Equal(State(source),State(target));
        using var c=SqliteConnectionFactory.Open(target);using var fk=c.CreateCommand();fk.CommandText="PRAGMA foreign_keys;";Assert.Equal(1L,fk.ExecuteScalar());
        using var machines=c.CreateCommand();machines.CommandText="SELECT COUNT(*) FROM MameMachines;";Assert.Equal(2L,machines.ExecuteScalar());
    }

    [Theory]
    [InlineData("profiles.csv","Id,HSH,VSL,VAM,VSC,VSH,Notes\r\n0,0,0,0,0,0,x\r\n","ID must be 1..255")]
    [InlineData("profiles.csv","Id,HSH,VSL,VAM,VSC,VSH,Notes\r\n1,64,0,0,0,0,x\r\n","geometry values")]
    [InlineData("profiles.csv","Id,HSH,VSL,VAM,VSC,VSH,Notes\r\n1,0,0,0,0,0,x\r\n1,0,0,0,0,0,y\r\n","Duplicate profile ID")]
    [InlineData("assignments.csv","RomName,ProfileId,AssignmentType,Width,Height,Rotation,RefreshMicroHz,UpdatedAtUtc\r\nrtype,99,Manual,,,,,2025-01-01T00:00:00Z\r\n","missing profile")]
    [InlineData("assignments.csv","RomName,ProfileId,AssignmentType,Width,Height,Rotation,RefreshMicroHz,UpdatedAtUtc\r\nmissing,1,Manual,,,,,2025-01-01T00:00:00Z\r\n","Unknown MAME ROM")]
    [InlineData("assignments.csv","RomName,ProfileId,AssignmentType,Width,Height,Rotation,RefreshMicroHz,UpdatedAtUtc\r\nrtype,1,Bogus,,,,,2025-01-01T00:00:00Z\r\n","type must be")]
    [InlineData("assignments.csv","RomName,ProfileId,AssignmentType,Width,Height,Rotation,RefreshMicroHz,UpdatedAtUtc\r\nrtype,1,Automatic,320,240,0,nope,2025-01-01T00:00:00Z\r\n","parse error")]
    public void ValidationReportsInvalidInput(string file,string replacement,string expected)
    {
        var cs=Database();SeedCatalogue(cs);new GeometryProfileRepository(cs).Save(new GeometryProfile(1));var zip=Baseline(cs);Replace(zip,file,replacement);
        var preview=new CsvInterchangeService(cs).Validate(zip,CsvImportMode.Merge);
        Assert.False(preview.IsValid);Assert.Contains(preview.Errors,x=>x.Contains(expected,StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MalformedCsvIsReportedAndInvalidPreviewCannotPartiallyApply()
    {
        var cs=Database();SeedCatalogue(cs);var zip=Baseline(cs);Replace(zip,"profiles.csv","Id,HSH,VSL,VAM,VSC,VSH,Notes\r\n2,1,2,3,4,5,\"unterminated");
        var service=new CsvInterchangeService(cs);var preview=service.Validate(zip,CsvImportMode.Merge);
        Assert.False(preview.IsValid);Assert.Throws<InvalidOperationException>(()=>service.Apply(preview,CsvImportMode.Merge));Assert.Empty(new GeometryProfileRepository(cs).GetAll());
    }

    [Fact]
    public void DatabaseFailureRollsBackReplaceAndDoesNotTouchCatalogue()
    {
        var source=Database();SeedCatalogue(source);var calibration=new CalibrationRepository(source);calibration.Apply(calibration.Preview("rtype",new(1,2,3,4,5)),new(1,2,3,4,5));var zip=Baseline(source);
        var target=Database();SeedCatalogue(target);new GeometryProfileRepository(target).Save(new GeometryProfile(99){Notes="keep on rollback"});
        var service=new CsvInterchangeService(target);var preview=service.Validate(zip,CsvImportMode.Replace);Assert.True(preview.IsValid);
        using(var c=SqliteConnectionFactory.Open(target)){using var command=c.CreateCommand();command.CommandText="DELETE FROM MameDisplays WHERE RomName='rtype'; DELETE FROM MameMachines WHERE RomName='rtype';";command.ExecuteNonQuery();}
        // The exported set itself has no MAME rows, so a concurrent catalogue change makes a FK fail during apply.
        // The profile deletion/insertion preceding that failure must be rolled back.
        Assert.Throws<SqliteException>(()=>service.Apply(preview,CsvImportMode.Replace));
        Assert.Equal(99,Assert.Single(new GeometryProfileRepository(target).GetAll()).Id);
        using var check=SqliteConnectionFactory.Open(target);using var count=check.CreateCommand();count.CommandText="SELECT COUNT(*) FROM MameMachines;";Assert.Equal(1L,count.ExecuteScalar());
    }

    private string Baseline(string cs){var p=Temp(".zip");new CsvInterchangeService(cs).Export(p);return p;}
    private string Database(){var p=Temp(".db");var cs=new SqliteConnectionStringBuilder{DataSource=p}.ToString();new DatabaseInitializer(cs).Initialize();return cs;}
    private string Temp(string ext){var p=Path.Combine(Path.GetTempPath(),$"crtgeometry-csv-{Guid.NewGuid():N}{ext}");_paths.Add(p);return p;}
    private static void SeedCatalogue(string cs)=>new MameImportService(cs).Import(new MemoryStream(Encoding.UTF8.GetBytes("<mame><game name='rtype'><description>R-Type</description><display type='raster' width='384' height='256' rotate='0' refresh='55.017606'/><input coins='1'/></game><game name='zgame'><description>Z Game</description><display type='raster' width='320' height='240' rotate='0' refresh='60'/><input coins='1'/></game></mame>")),"test.xml");
    private static string Text(ZipArchive z,string name){using var r=new StreamReader(z.GetEntry(name)!.Open());return r.ReadToEnd();}
    private static void Replace(string path,string name,string content){using var z=ZipFile.Open(path,ZipArchiveMode.Update);z.GetEntry(name)!.Delete();var e=z.CreateEntry(name);using var w=new StreamWriter(e.Open(),new UTF8Encoding(false));w.Write(content);}
    private static string State(string cs){using var c=SqliteConnectionFactory.Open(cs);var tables=new[]{"GeometryProfiles","CalibrationRecords","VideoProfileMappings","GameProfileAssignments"};var b=new StringBuilder();foreach(var table in tables){using var cmd=c.CreateCommand();cmd.CommandText=$"SELECT * FROM {table} ORDER BY 1,2,3,4";using var r=cmd.ExecuteReader();while(r.Read()){for(var i=0;i<r.FieldCount;i++)b.Append(r.IsDBNull(i)?"<null>":r.GetValue(i)).Append('|');b.AppendLine();}}return b.ToString();}
    public void Dispose(){SqliteConnection.ClearAllPools();foreach(var p in _paths)File.Delete(p);}
}
