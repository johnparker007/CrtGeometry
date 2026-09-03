using System.IO;
using System.Windows;
using CrtGeometry.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace CrtGeometry;

public partial class MainWindow : Window
{
    private readonly ProfilesViewModel _viewModel;
    private readonly string _connectionString;
    private readonly GamesViewModel _gamesViewModel;
    private readonly CalibrationViewModel _calibrationViewModel;
    private readonly CalibrationRepository _calibrationRepository;

    public MainWindow()
    {
        InitializeComponent();
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrtGeometry");
        Directory.CreateDirectory(dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "profiles.db"),
            ForeignKeys = true
        }.ToString();
        new DatabaseInitializer(_connectionString).Initialize();
        _viewModel = new ProfilesViewModel(new GeometryProfileRepository(_connectionString));
        DataContext = _viewModel;
        _gamesViewModel = new GamesViewModel(new GameCatalogueRepository(_connectionString));
        GamesPanel.DataContext = _gamesViewModel;
        _calibrationRepository = new CalibrationRepository(_connectionString);
        _calibrationViewModel = new CalibrationViewModel(new GameCatalogueRepository(_connectionString), _calibrationRepository);
        CalibrationPanel.DataContext = _calibrationViewModel;
        Loaded += async (_, _) => await _gamesViewModel.RefreshAsync();
    }

    private async void PreviewCalibration_Click(object sender, RoutedEventArgs e)
    { try { await _calibrationViewModel.PreviewAsync(); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Cannot preview"); } }
    private async void ApplyCalibration_Click(object sender, RoutedEventArgs e)
    {
        try { await _calibrationViewModel.ApplyAsync(); await _gamesViewModel.RefreshAsync(); ReloadProfiles(); }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"Cannot apply calibration"); }
    }
    private async void ManualAssign_Click(object sender, RoutedEventArgs e)
    {
        if(_gamesViewModel.SelectedGame is null || !int.TryParse(ManualProfileId.Text,out var id)) return;
        try { await Task.Run(()=>_calibrationRepository.AssignManual(_gamesViewModel.SelectedGame.RomName,id)); await _gamesViewModel.RefreshAsync(); ReloadProfiles(); }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"Cannot assign profile"); }
    }
    private async void ResetOverride_Click(object sender, RoutedEventArgs e)
    {
        if(_gamesViewModel.SelectedGame is null) return;
        try { await Task.Run(()=>_calibrationRepository.RemoveManualOverride(_gamesViewModel.SelectedGame.RomName)); await _gamesViewModel.RefreshAsync(); ReloadProfiles(); }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"Cannot reset override"); }
    }
    private void ReloadProfiles()
    {
        _viewModel.Reload();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "MAME XML (*.xml)|*.xml|All files (*.*)|*.*", Title = "Select MAME -listxml file" };
        if (dialog.ShowDialog(this) != true) return;
        ImportButton.IsEnabled = false; ImportProgress.IsIndeterminate = true; ImportSummary.Text = "";
        var progress = new Progress<CrtGeometry.Core.MameParseProgress>(p =>
            ImportStatus.Text = $"Parsed {p.MachinesParsed:N0} machines{(p.CurrentRomName is null ? "" : $" ({p.CurrentRomName})")}...");
        try
        {
            var summary = await Task.Run(() => new MameImportService(_connectionString).Import(dialog.FileName, progress));
            var reasons = string.Join(Environment.NewLine, summary.ExclusionCounts.OrderBy(x => x.Key).Select(x => $"  {x.Key}: {x.Value:N0}"));
            ImportStatus.Text = "Import complete.";
            ImportSummary.Text = $"MAME build: {summary.Build ?? "not supplied"}{Environment.NewLine}Total machines: {summary.TotalMachines:N0}{Environment.NewLine}Included: {summary.IncludedMachines:N0}{Environment.NewLine}Excluded: {summary.ExcludedMachines:N0}{Environment.NewLine}Machines with displays: {summary.MachinesWithDisplays:N0}{Environment.NewLine}Duration: {summary.Duration:g}{Environment.NewLine}Exclusion reasons:{Environment.NewLine}{reasons}";
            await _gamesViewModel.RefreshAsync();
        }
        catch (Exception exception)
        {
            ImportStatus.Text = "Import failed; the previous catalogue was preserved.";
            MessageBox.Show(this, exception.Message, "MAME import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ImportButton.IsEnabled = true; ImportProgress.IsIndeterminate = false; }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try { _viewModel.Add(); ProfilesGrid.ScrollIntoView(_viewModel.SelectedProfile); }
        catch (InvalidOperationException exception) { MessageBox.Show(this, exception.Message, "Cannot create profile"); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ProfilesGrid.CommitEdit() || !ProfilesGrid.CommitEdit())
        {
            MessageBox.Show(this, "Correct values marked in red before saving.", "Invalid profile");
            return;
        }
        _viewModel.Save();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedProfile is null) return;
        var profileId = _viewModel.SelectedProfile.Id;
        if (MessageBox.Show(this, $"Delete profile {profileId}?", "Delete profile",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            try { _viewModel.DeleteSelected(); }
            catch (SqliteException) { MessageBox.Show(this, $"Profile {profileId} is currently assigned or referenced and cannot be deleted.", "Profile in use"); }
        }
    }
}
