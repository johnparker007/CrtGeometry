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
