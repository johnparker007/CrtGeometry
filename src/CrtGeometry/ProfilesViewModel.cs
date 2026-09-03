using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CrtGeometry.Core;
using CrtGeometry.Data;

namespace CrtGeometry;

public sealed class ProfilesViewModel : INotifyPropertyChanged
{
    private readonly GeometryProfileRepository _repository;
    private GeometryProfile? _selectedProfile;
    private string? _statusMessage;

    public ProfilesViewModel(GeometryProfileRepository repository)
    {
        _repository = repository;
        Profiles = new ObservableCollection<GeometryProfile>(_repository.GetAll());
    }

    public ObservableCollection<GeometryProfile> Profiles { get; }

    public GeometryProfile? SelectedProfile
    {
        get => _selectedProfile;
        set { _selectedProfile = value; OnPropertyChanged(); }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public void Add()
    {
        var nextId = ProfileIdAllocator.GetLowestAvailable(Profiles.Select(profile => profile.Id));
        var profile = new GeometryProfile(nextId);
        _repository.Save(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        StatusMessage = $"Created profile {profile.Id}.";
    }

    public void Save()
    {
        foreach (var profile in Profiles)
        {
            _repository.Save(profile);
        }
        StatusMessage = "Profiles saved.";
    }

    public void DeleteSelected()
    {
        if (SelectedProfile is null) return;
        var deletedId = SelectedProfile.Id;
        _repository.Delete(deletedId);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = null;
        StatusMessage = $"Deleted profile {deletedId}.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
