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
        using var connection=new SqliteConnection(_cs); connection.Open(); using var command=connection.CreateCommand();
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
        repository.Apply(preview,values);
        var games=new GameCatalogueRepository(_cs);
        var assigned=games.Search(new(){Inclusion=InclusionFilter.All,Presence=PresenceFilter.All,Profile=ProfileFilter.AssignedOnly});
        Assert.Equal(["match","source"],assigned.Select(x=>x.RomName).Order());
        Assert.All(assigned,x=>Assert.Equal(ProfileAssignmentType.Automatic,x.AssignmentType));
        Assert.DoesNotContain(assigned,x=>x.RomName is "different" or "excluded" or "absent");
        var distinct=repository.Preview("different",new(6,2,3,4,5)); Assert.False(distinct.ReusesExistingProfile); Assert.NotEqual(7,distinct.ProfileId);
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
        Assert.Equal(20,catalogue.Search(new(){SearchText="match"}).Single().ProfileId);
        Assert.Equal(ProfileAssignmentType.Manual,catalogue.Search(new(){SearchText="match"}).Single().AssignmentType);
        Assert.Equal(newId,catalogue.Search(new(){SearchText="source"}).Single().ProfileId);
        repository.RemoveManualOverride("match");
        var reset=catalogue.Search(new(){SearchText="match"}).Single(); Assert.Equal(newId,reset.ProfileId); Assert.Equal(ProfileAssignmentType.Automatic,reset.AssignmentType);
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

    private VideoSignature Signature(int w,int h,int r,double refresh)=>new VideoSignatureService().SelectPrimary([Display(w,h,r,refresh)]).Signature!.Value;
    private static MameDisplay Display(int? w,int? h,int? r,double? f,string? type="raster")=>new(){Type=type,Width=w,Height=h,Rotate=r,Refresh=f,RawAttributesJson="{}"};
    private const string Catalogue="""
      <game name='source'><description>Source Game</description><display type='raster' width='320' height='240' rotate='0' refresh='60.606061'/><input coins='1'/></game>
      <game name='match'><description>Matching Game</description><display type='raster' width='320' height='240' rotate='0' refresh='60.6060614'/><input coins='1'/></game>
      <game name='different'><description>Different Game</description><display type='raster' width='321' height='240' rotate='0' refresh='60.606061'/><input coins='1'/></game>
      <game name='excluded' isbios='yes'><description>Excluded</description><display type='raster' width='320' height='240' rotate='0' refresh='60.606061'/><input coins='1'/></game>
      <game name='absent'><description>Absent</description><display type='raster' width='320' height='240' rotate='0' refresh='60.606061'/><input coins='1'/></game>
      """;
    private static MemoryStream Xml(string value)=>new(Encoding.UTF8.GetBytes($"<mame>{value}</mame>"));
    public void Dispose(){SqliteConnection.ClearAllPools();File.Delete(_path);}
}
