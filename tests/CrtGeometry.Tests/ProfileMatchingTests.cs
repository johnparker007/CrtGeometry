using System.Text;
using CrtGeometry.Core;
using CrtGeometry.Data;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Tests;

public sealed class ProfileMatchingTests : IDisposable
{
    private readonly string _path=Path.Combine(Path.GetTempPath(),$"crtgeometry-matching-{Guid.NewGuid():N}.db");
    private readonly string _cs;
    public ProfileMatchingTests()
    {
        _cs=new SqliteConnectionStringBuilder{DataSource=_path}.ToString(); new DatabaseInitializer(_cs).Initialize();
        new MameImportService(_cs).Import(Xml(Catalogue),"matching.xml");
        using var connection=SqliteConnectionFactory.Open(_cs); using var command=connection.CreateCommand();
        command.CommandText="UPDATE MameMachines SET IsPresent=0 WHERE RomName='absent';"; command.ExecuteNonQuery();
    }

    [Fact]
    public void SignatureUsesRequiredFieldsAndMicrohertzNormalization()
    {
        var service=new VideoSignatureService();
        Assert.Equal(Signature(320,240,0,60.6060611),Signature(320,240,360,60.6060614));
        Assert.NotEqual(Signature(321,240,0,60.606061),Signature(320,240,0,60.606061));
        Assert.NotEqual(Signature(320,241,0,60.606061),Signature(320,240,0,60.606061));
        Assert.NotEqual(Signature(320,240,90,60.606061),Signature(320,240,0,60.606061));
        Assert.NotEqual(Signature(320,240,0,60.606063),Signature(320,240,0,60.606061));
        Assert.Null(service.SelectPrimary([Display(320,null,0,60)]).Signature);
        Assert.Null(service.SelectPrimary([Display(320,240,0,null)]).Signature);
    }

    [Fact]
    public void MultipleRasterDisplaysAreAmbiguousRatherThanArbitrarilySelected()
    {
        var result=new VideoSignatureService().SelectPrimary([Display(320,240,0,60),Display(100,50,0,60)]);
        Assert.Equal(VideoModeSelectionStatus.AmbiguousMultipleRasterDisplays,result.Status); Assert.Null(result.Signature);
        Assert.Equal(VideoModeSelectionStatus.Usable,new VideoSignatureService().SelectPrimary([Display(320,240,0,60),Display(10,10,0,60,"vector")]).Status);
    }

    [Fact]
    public void PropagationReusesProfilesAndOnlyAssignsPresentIncludedMatches()
    {
        var profiles=new GeometryProfileRepository(_cs); profiles.Save(new GeometryProfile(7){HSH=1,VSL=2,VAM=3,VSC=4,VSH=5});
        var repository=new CalibrationRepository(_cs); var values=new CalibrationValues(1,2,3,4,5);
        var preview=repository.Preview("source",values); Assert.True(preview.ReusesExistingProfile); Assert.Equal(7,preview.ProfileId);
        Assert.DoesNotContain(preview.MatchingGames, game => game.RomName == "clone");
        repository.Apply(preview,values);
        var games=new GameCatalogueRepository(_cs);
        var assigned=games.Search(new(){Inclusion=InclusionFilter.All,Presence=PresenceFilter.All,Profile=ProfileFilter.AssignedOnly});
        Assert.Equal(["match","source"],assigned.Select(x=>x.RomName).Order());
        Assert.All(assigned,x=>Assert.Equal(ProfileAssignmentType.Automatic,x.AssignmentType));
        Assert.DoesNotContain(assigned,x=>x.RomName is "different" or "excluded" or "absent");
        var distinct=repository.Preview("different",new(6,2,3,4,5)); Assert.False(distinct.ReusesExistingProfile); Assert.NotEqual(7,distinct.ProfileId);
    }

    [Fact]
    public void CloneIsSearchableAndCanBeExplicitCalibrationSourceOrManualOverride()
    {
        var catalogue = new GameCatalogueRepository(_cs);
        var clone = catalogue.Search(new() { SearchText = "clone" }).Single(x => x.RomName == "clone");
        Assert.Equal("match", clone.CloneOf);

        var repository = new CalibrationRepository(_cs);
        var direct = repository.PreviewAndApply("clone", new(1,2,3,4,5));
        Assert.Contains(direct.Preview.MatchingGames, game => game.RomName == "clone");
        Assert.Contains(direct.Preview.MatchingGames, game => game.RomName == "match");

        new GeometryProfileRepository(_cs).Save(new GeometryProfile(20));
        repository.AssignManual("clone", 20);
        clone = catalogue.Search(new() { SearchText = "clone" }).Single(x => x.RomName == "clone");
        Assert.Equal(20, clone.ProfileId);
        Assert.Equal(ProfileAssignmentType.Manual, clone.AssignmentType);
    }

    [Fact]
    public void DirectApplyRecomputesAndReusesProfilesWhilePreservingManualOverrides()
    {
        var repository = new CalibrationRepository(_cs);
        new GeometryProfileRepository(_cs).Save(new GeometryProfile(7){HSH=1,VSL=2,VAM=3,VSC=4,VSH=5});
        new GeometryProfileRepository(_cs).Save(new GeometryProfile(20));
        repository.AssignManual("match", 20);

        var first = repository.Preview("source", new(6,7,8,9,10)); // informational and deliberately stale
        Assert.False(first.ReusesExistingProfile);
        var applied = repository.PreviewAndApply("source", new(1,2,3,4,5));

        Assert.Equal(7, applied.ProfileId);
        var match = FindGame(new GameCatalogueRepository(_cs), "match");
        Assert.Equal(20, match.ProfileId);
        Assert.Equal(ProfileAssignmentType.Manual, match.AssignmentType);
    }

    [Fact]
    public void DirectApplyUsesCurrentSourceRatherThanAnEarlierPreviewSelection()
    {
        var repository = new CalibrationRepository(_cs);
        _ = repository.Preview("source", new(1,2,3,4,5));

        var applied = repository.PreviewAndApply("different", new(6,7,8,9,10));

        Assert.Equal("different", applied.Preview.SourceRomName);
        Assert.Single(applied.Preview.MatchingGames);
        Assert.Equal("different", applied.Preview.MatchingGames[0].RomName);
    }

    [Fact]
    public void ManualOverrideSurvivesRecalibrationAndResetRestoresCurrentMapping()
    {
        var repository=new CalibrationRepository(_cs);
        var first=repository.Preview("source",new(1,2,3,4,5)); repository.Apply(first,new(1,2,3,4,5));
        new GeometryProfileRepository(_cs).Save(new GeometryProfile(20){HSH=9,VSL=9,VAM=9,VSC=9,VSH=9});
        repository.AssignManual("match",20);
        var second=repository.Preview("source",new(6,7,8,9,10)); var newId=repository.Apply(second,new(6,7,8,9,10));
        var catalogue=new GameCatalogueRepository(_cs);
        Assert.Equal(20,FindGame(catalogue,"match").ProfileId);
        Assert.Equal(ProfileAssignmentType.Manual,FindGame(catalogue,"match").AssignmentType);
        Assert.Equal(newId,FindGame(catalogue,"source").ProfileId);
        repository.RemoveManualOverride("match");
        var reset=FindGame(catalogue,"match"); Assert.Equal(newId,reset.ProfileId); Assert.Equal(ProfileAssignmentType.Automatic,reset.AssignmentType);
    }

    [Fact]
    public void AssignmentsAndProvenanceSurviveRestartAndMameReimport()
    {
        var repository=new CalibrationRepository(_cs); var values=new CalibrationValues(1,2,3,4,5);
        repository.Apply(repository.Preview("source",values),values);
        new DatabaseInitializer(_cs).Initialize();
        new MameImportService(_cs).Import(Xml(Catalogue.Replace("Source Game","Updated Source Game")),"again.xml");
        var source=new GameCatalogueRepository(_cs).Search(new(){SearchText="source"}).Single(); Assert.NotNull(source.ProfileId);
        var profile=new GeometryProfileRepository(_cs).GetAll().Single();
        Assert.Equal("source",profile.CalibrationSourceRomName); Assert.Equal("Updated Source Game",profile.CalibrationSourceTitle);
    }

    [Fact]
    public void ProductionConnectionFactoryEnforcesForeignKeysAndBlocksReferencedProfileDeletion()
    {
        using(var connection=SqliteConnectionFactory.Open(_cs))
        { using var command=connection.CreateCommand(); command.CommandText="PRAGMA foreign_keys;"; Assert.Equal(1L,command.ExecuteScalar()); }
        var repository=new CalibrationRepository(_cs); var values=new CalibrationValues(1,2,3,4,5);
        var profileId=repository.Apply(repository.Preview("source",values),values);

        Assert.Throws<SqliteException>(()=>new GeometryProfileRepository(_cs).Delete(profileId));
        Assert.Contains(new GameCatalogueRepository(_cs).Search(new()),game=>game.ProfileId==profileId);
    }

    [Fact]
    public void ManualAssignmentRejectsNonexistentProfileAndRom()
    {
        var repository=new CalibrationRepository(_cs);
        Assert.Throws<SqliteException>(()=>repository.AssignManual("source",254));

        new GeometryProfileRepository(_cs).Save(new GeometryProfile(7));
        Assert.Throws<SqliteException>(()=>repository.AssignManual("not-a-real-rom",7));
        Assert.Empty(new GameCatalogueRepository(_cs).Search(new(){Profile=ProfileFilter.AssignedOnly}));
    }

    [Fact]
    public void InvalidCalibrationSourceRollsBackProfileAndCalibrationAtomically()
    {
        var repository=new CalibrationRepository(_cs);
        var invalid=new PropagationPreview("not-a-real-rom",Signature(320,240,0,60),1,false,[]);

        Assert.Throws<SqliteException>(()=>repository.Apply(invalid,new(1,2,3,4,5)));
        Assert.Empty(new GeometryProfileRepository(_cs).GetAll());
        using var connection=SqliteConnectionFactory.Open(_cs); using var command=connection.CreateCommand();
        command.CommandText="SELECT (SELECT COUNT(*) FROM CalibrationRecords) + (SELECT COUNT(*) FROM VideoProfileMappings);";
        Assert.Equal(0L,command.ExecuteScalar());
    }

    private VideoSignature Signature(int w,int h,int r,double refresh)=>new VideoSignatureService().SelectPrimary([Display(w,h,r,refresh)]).Signature!.Value;
    private static GameCatalogueEntry FindGame(GameCatalogueRepository catalogue, string romName) =>
        catalogue.Search(new() { SearchText=romName, Inclusion=InclusionFilter.All, Presence=PresenceFilter.All })
            .Single(game => game.RomName == romName);
    private static MameDisplay Display(int? w,int? h,int? r,double? f,string? type="raster")=>new(){Type=type,Width=w,Height=h,Rotate=r,Refresh=f,RawAttributesJson="{}"};
    private const string Catalogue="""
      <game name='source'><description>Source Game</description><display type='raster' width='320' height='240' rotate='0' refresh='60.606061'/><input coins='1'/></game>
      <game name='match'><description>Matching Game</description><display type='raster' width='320' height='240' rotate='0' refresh='60.6060614'/><input coins='1'/></game>
      <game name='clone' cloneof='match'><description>Matching Game Clone</description><display type='raster' width='320' height='240' rotate='0' refresh='60.6060614'/><input coins='1'/></game>
      <game name='different'><description>Different Game</description><display type='raster' width='321' height='240' rotate='0' refresh='60.606061'/><input coins='1'/></game>
      <game name='excluded' isbios='yes'><description>Excluded</description><display type='raster' width='320' height='240' rotate='0' refresh='60.606061'/><input coins='1'/></game>
      <game name='absent'><description>Absent</description><display type='raster' width='320' height='240' rotate='0' refresh='60.606061'/><input coins='1'/></game>
      """;
    private static MemoryStream Xml(string value)=>new(Encoding.UTF8.GetBytes($"<mame>{value}</mame>"));
    public void Dispose(){SqliteConnection.ClearAllPools();File.Delete(_path);}
}
