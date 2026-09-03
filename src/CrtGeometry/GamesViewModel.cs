using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CrtGeometry.Core;
using CrtGeometry.Data;

namespace CrtGeometry;

public sealed class GamesViewModel : INotifyPropertyChanged
{
    private readonly GameCatalogueRepository _repository;
    private readonly DispatcherTimer _searchDelay;
    private string _searchText = "";
    private InclusionFilter _inclusion = InclusionFilter.IncludedOnly;
    private PresenceFilter _presence = PresenceFilter.PresentOnly;
    private ProfileFilter _profile = ProfileFilter.All;
    private NanoInclusionFilter _nanoInclusion = NanoInclusionFilter.All;
    private GameCatalogueEntry? _selectedGame;
    private IReadOnlyList<GameCatalogueEntry> _games = [];
    private CancellationTokenSource? _refreshCancellation;

    public GamesViewModel(GameCatalogueRepository repository)
    {
        _repository = repository;
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDelay.Tick += async (_, _) => { _searchDelay.Stop(); await RefreshAsync(); };
    }

    public IReadOnlyList<GameCatalogueEntry> Games { get => _games; private set { _games=value; Changed(); Changed(nameof(ResultSummary)); } }
    public Array InclusionOptions => Enum.GetValues<InclusionFilter>();
    public Array PresenceOptions => Enum.GetValues<PresenceFilter>();
    public Array ProfileOptions => Enum.GetValues<ProfileFilter>();
    public Array NanoInclusionOptions => Enum.GetValues<NanoInclusionFilter>();
    public string ResultSummary => $"{Games.Count:N0} machine{(Games.Count == 1 ? "" : "s")}";
    public string SearchText { get=>_searchText; set { if(Set(ref _searchText,value)) Schedule(); } }
    public InclusionFilter Inclusion { get=>_inclusion; set { if(Set(ref _inclusion,value)) Schedule(); } }
    public PresenceFilter Presence { get=>_presence; set { if(Set(ref _presence,value)) Schedule(); } }
    public ProfileFilter Profile { get=>_profile; set { if(Set(ref _profile,value)) Schedule(); } }
    public NanoInclusionFilter NanoInclusion { get=>_nanoInclusion; set { if(Set(ref _nanoInclusion,value)) Schedule(); } }
    public GameCatalogueEntry? SelectedGame { get=>_selectedGame; set=>Set(ref _selectedGame,value); }
    public string? SelectedRomName => SelectedGame?.RomName;

    public async Task RefreshAsync()
    {
        _refreshCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        var query = new GameCatalogueQuery { SearchText=SearchText, Inclusion=Inclusion, Presence=Presence, Profile=Profile, NanoInclusion=NanoInclusion };
        IReadOnlyList<GameCatalogueEntry> results;
        try { results = await _repository.SearchAsync(query, cancellation.Token); }
        catch (OperationCanceledException) { return; }
        if (_refreshCancellation != cancellation) return;
        Games = results;
        if (SelectedGame is not null) SelectedGame = results.FirstOrDefault(x => x.RomName == SelectedGame.RomName);
    }

    public void SetIncludeOnNano(GameCatalogueEntry game, bool included)
    {
        _repository.SetIncludeOnNano(game.RomName, included);
        game.IncludeOnNano = included;
    }

    private void Schedule() { _searchDelay.Stop(); _searchDelay.Start(); }
    private bool Set<T>(ref T field,T value,[CallerMemberName]string? name=null)
    { if(EqualityComparer<T>.Default.Equals(field,value)) return false; field=value; Changed(name); return true; }
    private void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
