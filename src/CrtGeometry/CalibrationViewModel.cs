using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CrtGeometry.Core;
using CrtGeometry.Data;

namespace CrtGeometry;

public sealed class CalibrationViewModel : INotifyPropertyChanged
{
    private readonly GameCatalogueRepository _games;
    private readonly CalibrationRepository _calibrations;
    private readonly DispatcherTimer _delay;
    private CancellationTokenSource? _searchCancellation;
    private string _searchText=""; private IReadOnlyList<GameCatalogueEntry> _candidates=[];
    private GameCatalogueEntry? _selectedGame; private PropagationPreview? _preview; private string? _status;
    public CalibrationViewModel(GameCatalogueRepository games, CalibrationRepository calibrations)
    {
        _games=games; _calibrations=calibrations; _delay=new(){Interval=TimeSpan.FromMilliseconds(180)};
        _delay.Tick += async (_,_) => { _delay.Stop(); await SearchAsync(); };
    }
    public string SearchText { get=>_searchText; set { _searchText=value; Changed(); _delay.Stop(); _delay.Start(); } }
    public IReadOnlyList<GameCatalogueEntry> Candidates { get=>_candidates; private set { _candidates=value; Changed(); } }
    public GameCatalogueEntry? SelectedGame { get=>_selectedGame; set { _selectedGame=value; Preview=null; Changed(); Changed(nameof(VideoModeMessage)); } }
    public string VideoModeMessage => SelectedGame is null ? "Select a game." : SelectedGame.VideoMode.Message;
    public int HSH { get; set; } public int VSL { get; set; } public int VAM { get; set; } public int VSC { get; set; } public int VSH { get; set; }
    public PropagationPreview? Preview { get=>_preview; private set { _preview=value; Changed(); Changed(nameof(MatchingGames)); Changed(nameof(PreviewSummary)); } }
    public IReadOnlyList<GameCatalogueEntry> MatchingGames => Preview?.MatchingGames ?? [];
    public string PreviewSummary => Preview is null ? "Enter geometry and preview propagation." :
        $"{(Preview.ReusesExistingProfile ? "Reuse" : "Create")} Profile {Preview.ProfileId}; {Preview.MatchingGames.Count} matching included/present games; {Preview.Signature}";
    public string? Status { get=>_status; private set { _status=value; Changed(); } }
    public async Task SearchAsync()
    {
        _searchCancellation?.Cancel(); var cancellation=new CancellationTokenSource(); _searchCancellation=cancellation;
        try
        {
            var result=await _games.SearchAsync(new(){SearchText=SearchText},cancellation.Token);
            if (_searchCancellation==cancellation) Candidates=result;
        }
        catch(OperationCanceledException) { }
    }
    public async Task PreviewAsync()
    {
        if (SelectedGame is null) throw new InvalidOperationException("Select an exact MAME game first.");
        Validate(); var values=new CalibrationValues(HSH,VSL,VAM,VSC,VSH);
        Preview=await Task.Run(()=>_calibrations.Preview(SelectedGame.RomName,values)); Status="Preview ready; inspect matches before applying.";
    }
    public async Task ApplyAsync()
    {
        if(Preview is null) throw new InvalidOperationException("Preview the propagation first."); Validate();
        var id=await Task.Run(()=>_calibrations.Apply(Preview,new(HSH,VSL,VAM,VSC,VSH)));
        Status=$"Profile {id} applied automatically; manual overrides were preserved.";
    }
    private void Validate() { foreach(var v in new[]{HSH,VSL,VAM,VSC,VSH}) if(v is <0 or >63) throw new InvalidOperationException("Geometry values must be between 0 and 63."); }
    private void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
