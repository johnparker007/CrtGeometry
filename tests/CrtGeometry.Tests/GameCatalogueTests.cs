using System.Text;
using CrtGeometry.Core;
using CrtGeometry.Data;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Tests;

public sealed class GameCatalogueTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"crtgeometry-games-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly GameCatalogueRepository _repository;

    public GameCatalogueTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _path }.ToString();
        new DatabaseInitializer(_connectionString).Initialize();
        new MameImportService(_connectionString).Import(Xml(Catalogue), "catalogue.xml");
        _repository = new GameCatalogueRepository(_connectionString);
    }

    [Theory]
    [InlineData("Donkey Kong", "dkong")]
    [InlineData("DKONG", "dkong")]
    [InlineData("nintendo", "dkong")]
    [InlineData("1981", "dkong")]
    public void SearchMatchesDescriptionRomManufacturerAndYearCaseInsensitively(string search, string expected)
    {
        var result = _repository.Search(new() { SearchText=search, Inclusion=InclusionFilter.All });
        Assert.Contains(result, game => game.RomName == expected);
    }

    [Fact]
    public void DefaultAndExclusionFiltersExposeReasons()
    {
        var included = _repository.Search(new());
        Assert.Equal(["dkong", "multi"], included.Select(x => x.RomName));
        var excluded = _repository.Search(new() { Inclusion=InclusionFilter.ExcludedOnly });
        var bios = Assert.Single(excluded);
        Assert.Equal("bios", bios.RomName);
        Assert.Contains("Bios", bios.ExclusionReasonText);
        Assert.Contains("No display", bios.ExclusionReasonText);
    }

    [Fact]
    public void PresenceFiltersDistinguishCurrentAndHistoricalMachines()
    {
        new MameImportService(_connectionString).Import(Xml("<game name='dkong'><description>Donkey Kong</description><year>1981</year><manufacturer>Nintendo</manufacturer><display type='raster' width='256' height='224' rotate='90' refresh='60.606061'/><input coins='1'/></game>"));
        var absent = _repository.Search(new() { Inclusion=InclusionFilter.All, Presence=PresenceFilter.AbsentOnly });
        Assert.Equal(["bios", "multi"], absent.Select(x => x.RomName));
        Assert.Single(_repository.Search(new() { Inclusion=InclusionFilter.All, Presence=PresenceFilter.PresentOnly }));
    }

    [Fact]
    public void ResultsAreDescriptionOrderedAndDisplaysLoadedInOneShape()
    {
        var games = _repository.Search(new() { Inclusion=InclusionFilter.All });
        Assert.Equal(["bios", "dkong", "multi"], games.Select(x => x.RomName));
        var single = games.Single(x => x.RomName == "dkong");
        Assert.Single(single.Displays);
        Assert.Equal("256 x 224", single.ResolutionSummary);
        Assert.Equal("60.606061 Hz", single.RefreshSummary);
        var multiple = games.Single(x => x.RomName == "multi");
        Assert.Equal(2, multiple.Displays.Count);
        Assert.Contains("+1 displays", multiple.ResolutionSummary);
        Assert.Null(multiple.Displays[1].Refresh);
        Assert.Equal("Unknown", new GameCatalogueEntry { RomName="missing" }.RefreshSummary);
    }

    [Fact]
    public void ProfileFilterReflectsPhaseThreeUnassignedState()
    {
        Assert.Empty(_repository.Search(new() { Profile=ProfileFilter.AssignedOnly }));
        Assert.NotEmpty(_repository.Search(new() { Profile=ProfileFilter.UnassignedOnly }));
    }

    [Fact]
    public void ReusableSelectionExposesStableRomName()
    {
        var model = new GameSelectionModel { SearchText="Donkey Kong" };
        model.SetCandidates(_repository.Search(new() { SearchText=model.SearchText }));
        model.SelectedGame = Assert.Single(model.Candidates);
        Assert.Equal("dkong", model.SelectedRomName);
    }

    [Fact]
    public void NanoInclusionCanSetAndClearOneGameAndPersistsAfterReload()
    {
        _repository.SetIncludeOnNano(["dkong"], true);
        Assert.True(_repository.Search(new()).Single(game => game.RomName == "dkong").IncludeOnNano);

        _repository.SetIncludeOnNano(["dkong"], false);
        Assert.False(new GameCatalogueRepository(_connectionString).Search(new())
            .Single(game => game.RomName == "dkong").IncludeOnNano);
    }

    [Fact]
    public void BulkNanoInclusionHandlesMixedSelectionsWithoutChangingOtherGameState()
    {
        _repository.SetIncludeOnNano(["dkong"], true);
        using (var connection = SqliteConnectionFactory.Open(_connectionString))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO GeometryProfiles(Id,HSH,VSL,VAM,VSC,VSH,Notes) VALUES(7,1,2,3,4,5,'unchanged');
                INSERT INTO CalibrationRecords(ProfileId,SourceRomName,Width,Height,Rotation,RefreshMicroHz,CreatedAtUtc)
                  VALUES(7,'dkong',256,224,90,60606061,'unchanged');
                INSERT INTO GameProfileAssignments(RomName,ProfileId,AssignmentType,UpdatedAtUtc) VALUES('dkong',7,2,'unchanged');
                """;
            command.ExecuteNonQuery();
        }

        // dkong is already true and multi is false; both are changed as one idempotent bulk request.
        _repository.SetIncludeOnNano(["multi", "dkong", "multi"], true);
        var included = new GameCatalogueRepository(_connectionString).Search(new());
        Assert.All(included, game => Assert.True(game.IncludeOnNano));

        _repository.SetIncludeOnNano(["dkong", "multi"], false);
        var excluded = new GameCatalogueRepository(_connectionString).Search(new());
        Assert.All(excluded, game => Assert.False(game.IncludeOnNano));
        var dkong = excluded.Single(game => game.RomName == "dkong");
        Assert.Equal("Donkey Kong", dkong.Description);
        Assert.Equal("Nintendo", dkong.Manufacturer);
        Assert.Equal(7, dkong.ProfileId);
        Assert.Equal(ProfileAssignmentType.Manual, dkong.AssignmentType);

        using var checkConnection = SqliteConnectionFactory.Open(_connectionString);
        using var check = checkConnection.CreateCommand();
        check.CommandText = "SELECT HSH,VSL,VAM,VSC,VSH,Notes FROM GeometryProfiles WHERE Id=7;";
        using var reader = check.ExecuteReader(); Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0)); Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(3, reader.GetInt32(2)); Assert.Equal(4, reader.GetInt32(3));
        Assert.Equal(5, reader.GetInt32(4)); Assert.Equal("unchanged", reader.GetString(5));
        reader.Close();
        check.CommandText = "SELECT SourceRomName,Width,Height,Rotation,RefreshMicroHz,CreatedAtUtc FROM CalibrationRecords WHERE ProfileId=7;";
        using var calibration = check.ExecuteReader(); Assert.True(calibration.Read());
        Assert.Equal("dkong", calibration.GetString(0)); Assert.Equal(256, calibration.GetInt32(1));
        Assert.Equal(224, calibration.GetInt32(2)); Assert.Equal(90, calibration.GetInt32(3));
        Assert.Equal(60606061, calibration.GetInt32(4)); Assert.Equal("unchanged", calibration.GetString(5));
    }

    private const string Catalogue = """
      <game name='multi'><description>Multiple Monitor Game</description><year>1990</year><manufacturer>Namco</manufacturer>
       <display type='raster' width='320' height='240' rotate='0' refresh='59.94'/><display type='raster' width='100' height='50'/><input coins='1'/></game>
      <game name='dkong'><description>Donkey Kong</description><year>1981</year><manufacturer>Nintendo</manufacturer>
       <display type='raster' width='256' height='224' rotate='90' refresh='60.606061'/><input coins='1'/></game>
      <game name='bios' isbios='yes'><description>Arcade BIOS</description><year>1980</year><manufacturer>Test</manufacturer></game>
      """;
    private static MemoryStream Xml(string machines) => new(Encoding.UTF8.GetBytes($"<mame build='0.139'>{machines}</mame>"));
    public void Dispose() { SqliteConnection.ClearAllPools(); File.Delete(_path); }
}
