using System.Text;
using CrtGeometry.Core;
using CrtGeometry.Data;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Tests;

public sealed class MameImportTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"crtgeometry-mame-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;

    public MameImportTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _path }.ToString();
        new DatabaseInitializer(_connectionString).Initialize();
    }

    [Fact]
    public void ParserSupportsOldAndModernMachinesAndPreservesDisplaysAndTiming()
    {
        const string xml = """
            <?xml version="1.0"?><mame build="0.139 (test)" debug="no" mameconfig="10">
              <game name="pacman" cloneof="puckman" runnable="yes">
                <description>Pac-Man</description><year>1980</year><manufacturer>Namco</manufacturer>
                <display type="raster" rotate="90" width="288" height="224" refresh="60.606061" pixclock="6144000" htotal="384" hbend="0" hbstart="288" vtotal="264" vbend="0" vbstart="224" custom="retained"/>
                <display type="raster" rotate="0" width="100" height="50" refresh="55.5"/><input coins="2"/>
              </game>
              <machine name="minimal"><description>Minimal</description><display width="320" height="240"/></machine>
            </mame>
            """;
        var machines = new List<MameMachine>();
        var metadata = new MameXmlParser().Parse(Bytes(xml), machines.Add);

        Assert.Equal("0.139 (test)", metadata.Build);
        var pacman = machines[0];
        Assert.Equal("puckman", pacman.CloneOf); Assert.Equal("Pac-Man", pacman.Description);
        Assert.Equal("1980", pacman.Year); Assert.Equal("Namco", pacman.Manufacturer); Assert.Equal(2, pacman.CoinInputs);
        Assert.Equal(2, pacman.Displays.Count); Assert.Equal(60.606061, pacman.Displays[0].Refresh);
        Assert.Equal(6144000, pacman.Displays[0].PixelClock); Assert.Equal(384, pacman.Displays[0].HTotal);
        Assert.Contains("custom", pacman.Displays[0].RawAttributesJson);
        Assert.True(machines[1].Runnable); Assert.Null(machines[1].CoinInputs);
    }

    [Fact]
    public void FilterReportsEveryApplicableReason()
    {
        var machine = new MameMachine { RomName = "bad", Runnable = false, IsBios = true, IsDevice = true, IsMechanical = true, CoinInputs = 0 };
        var reasons = new MameFilterPolicy().Evaluate(machine);
        Assert.True(reasons.HasFlag(MameExclusionReason.Bios)); Assert.True(reasons.HasFlag(MameExclusionReason.Device));
        Assert.True(reasons.HasFlag(MameExclusionReason.Mechanical)); Assert.True(reasons.HasFlag(MameExclusionReason.NotRunnable));
        Assert.True(reasons.HasFlag(MameExclusionReason.NoDisplay)); Assert.True(reasons.HasFlag(MameExclusionReason.NoCoinInput));

        var vector = new MameMachine { RomName = "vector", CoinInputs = 1 };
        vector.Displays.Add(new MameDisplay { Type = "vector", RawAttributesJson = "{}" });
        Assert.Equal(MameExclusionReason.NonRaster, new MameFilterPolicy().Evaluate(vector));
    }

    [Fact]
    public void OldSchemaFixtureCoversInitialFilterOutcomes()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mame-old-cases.xml");
        var summary = new MameImportService(_connectionString).Import(fixture);
        Assert.Equal("0.139 (fixture)", summary.Build);
        Assert.Equal(10, summary.TotalMachines);
        Assert.Equal(3, summary.IncludedMachines); // arcade, missing optional fields, and clone
        Assert.Equal(9, summary.MachinesWithDisplays);
        Assert.Equal(1, summary.ExclusionCounts[MameExclusionReason.Bios]);
        Assert.Equal(1, summary.ExclusionCounts[MameExclusionReason.Device]);
        Assert.Equal(1, summary.ExclusionCounts[MameExclusionReason.Mechanical]);
        Assert.Equal(1, summary.ExclusionCounts[MameExclusionReason.NotRunnable]);
        Assert.Equal(1, summary.ExclusionCounts[MameExclusionReason.NoDisplay]);
        Assert.Equal(1, summary.ExclusionCounts[MameExclusionReason.NonRaster]);
        Assert.Equal(1, summary.ExclusionCounts[MameExclusionReason.NoCoinInput]);
    }

    [Fact]
    public void ImportUpsertsAndMalformedImportRollsBack()
    {
        var service = new MameImportService(_connectionString);
        var first = service.Import(Bytes(Xml("Original")), "old.xml");
        var second = service.Import(Bytes(Xml("Updated")), "new.xml");
        Assert.Equal(1, first.IncludedMachines); Assert.Equal(1, second.TotalMachines);
        Assert.ThrowsAny<Exception>(() => service.Import(Bytes("<mame><game name='broken'>"), "broken.xml"));

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Description, COUNT(*) OVER (), (SELECT COUNT(*) FROM MameImports) FROM MameMachines WHERE RomName='test';";
        using var reader = command.ExecuteReader(); Assert.True(reader.Read());
        Assert.Equal("Updated", reader.GetString(0)); Assert.Equal(1, reader.GetInt32(1)); Assert.Equal(2, reader.GetInt32(2));
    }

    [Fact]
    public void IncludeOnNanoDefaultsFalseAndSurvivesReimportAndReload()
    {
        var service = new MameImportService(_connectionString);
        service.Import(Bytes(Xml("Original")));
        var repository = new GameCatalogueRepository(_connectionString);
        Assert.False(Assert.Single(repository.Search(new())).IncludeOnNano);

        repository.SetIncludeOnNano("test", true);
        Assert.True(Assert.Single(new GameCatalogueRepository(_connectionString).Search(new())).IncludeOnNano);
        service.Import(Bytes(Xml("Updated")));
        var reloaded = Assert.Single(new GameCatalogueRepository(_connectionString).Search(new()));
        Assert.True(reloaded.IncludeOnNano);
        Assert.Equal("Updated", reloaded.Description);
    }

    [Fact]
    public void VersionOneMigratesWithoutChangingProfilesAndNewerVersionIsRejected()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE GeometryProfiles (Id INTEGER PRIMARY KEY,HSH INTEGER NOT NULL,VSL INTEGER NOT NULL,VAM INTEGER NOT NULL,VSC INTEGER NOT NULL,VSH INTEGER NOT NULL,Notes TEXT NULL); INSERT INTO GeometryProfiles VALUES (7,1,2,3,4,5,'keep'); PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
        }
        new DatabaseInitializer(_connectionString).Initialize();
        Assert.Equal("keep", new GeometryProfileRepository(_connectionString).GetAll().Single().Notes);
        using (var c = Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version=5;"; cmd.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(() => new DatabaseInitializer(_connectionString).Initialize());
    }

    [Fact]
    public void VersionTwoMigrationPreservesProfilesAndCatalogueRows()
    {
        var profiles=new GeometryProfileRepository(_connectionString);
        profiles.Save(new GeometryProfile(9){HSH=1,VSL=2,VAM=3,VSC=4,VSH=5,Notes="phase one"});
        new MameImportService(_connectionString).Import(Bytes(Xml("Phase three catalogue")));
        using(var c=Open()){using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA user_version=2;";cmd.ExecuteNonQuery();}
        // Simulate the exact released v2 shape by removing only Phase 4 objects.
        using(var c=Open()){using var cmd=c.CreateCommand();cmd.CommandText="DROP TABLE GameProfileAssignments; DROP TABLE VideoProfileMappings; DROP TABLE CalibrationRecords;";cmd.ExecuteNonQuery();}
        new DatabaseInitializer(_connectionString).Initialize();
        Assert.Equal("phase one",profiles.GetAll().Single().Notes);
        Assert.Equal("Phase three catalogue",new GameCatalogueRepository(_connectionString).Search(new()).Single().Description);
    }

    private static string Xml(string description) => $"<mame build='0.139'><game name='test'><description>{description}</description><display type='raster' width='320' height='240' rotate='0' refresh='60.0'/><input coins='1'/></game></mame>";
    private static MemoryStream Bytes(string value) => new(Encoding.UTF8.GetBytes(value));
    private SqliteConnection Open() => SqliteConnectionFactory.Open(_connectionString);
    public void Dispose() { SqliteConnection.ClearAllPools(); File.Delete(_path); }
}
