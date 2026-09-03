using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CrtGeometry.Core;
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
    private readonly CsvInterchangeService _csvService;
    private readonly FirmwareDatabaseGenerator _firmwareGenerator;
    private CsvImportPreview? _csvPreview;
    private CsvImportMode _csvPreviewMode;

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
        _csvService = new CsvInterchangeService(_connectionString);
        _firmwareGenerator = new FirmwareDatabaseGenerator(_connectionString);
        _calibrationViewModel = new CalibrationViewModel(new GameCatalogueRepository(_connectionString), _calibrationRepository);
        CalibrationPanel.DataContext = _calibrationViewModel;
        SetFirmwareDirectory(FindDefaultFirmwareDirectory() ?? string.Empty);
        RefreshFirmwareStatistics();
        Loaded += async (_, _) => await _gamesViewModel.RefreshAsync();
    }

    private void BrowseFirmware_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select CrtGeometryController firmware directory" };
        if (Directory.Exists(FirmwareDirectory.Text)) dialog.InitialDirectory = FirmwareDirectory.Text;
        if (dialog.ShowDialog(this) == true) SetFirmwareDirectory(dialog.FolderName);
    }

    private void GamesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(GamesGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row is null || row.IsSelected) return;
        GamesGrid.UnselectAll();
        row.IsSelected = true;
        row.Focus();
    }

    private void IncludeSelectedOnNano_Click(object sender, RoutedEventArgs e) => SetSelectedGamesIncludeOnNano(true);
    private void ExcludeSelectedFromNano_Click(object sender, RoutedEventArgs e) => SetSelectedGamesIncludeOnNano(false);

    private void SetSelectedGamesIncludeOnNano(bool included)
    {
        var games = GamesGrid.SelectedItems.Cast<GameCatalogueEntry>().ToArray();
        if (games.Length == 0) return;
        try
        {
            _gamesViewModel.SetIncludeOnNano(games, included);
            GamesGrid.Items.Refresh();
            RefreshFirmwareStatistics();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Nano inclusion update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshFirmware_Click(object sender, RoutedEventArgs e) => RefreshFirmwareStatistics();

    private void GenerateFirmware_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var statistics = _firmwareGenerator.Write(FirmwareDirectory.Text);
            ShowFirmwareStatistics(statistics);
            FirmwareStatus.Text = $"Generated {FirmwareOutputPath.Text} successfully.";
        }
        catch (Exception exception)
        {
            FirmwareStatus.Text = "Generation failed; the existing generated header was not changed.";
            MessageBox.Show(this, exception.Message, "Firmware generation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshFirmwareStatistics()
    {
        try
        {
            ShowFirmwareStatistics(_firmwareGenerator.Preview().Statistics);
            FirmwareStatus.Text = "Statistics loaded from SQLite.";
        }
        catch (Exception exception) { FirmwareStatus.Text = $"Cannot inspect profiles: {exception.Message}"; }
    }

    private void ShowFirmwareStatistics(FirmwareDatabaseStatistics statistics)
    {
        FirmwareProfileCount.Text = statistics.ProfileCount.ToString();
        FirmwareHighestId.Text = statistics.HighestProfileId.ToString();
        FirmwareTableBytes.Text = $"{statistics.ProfileTableBytes} bytes";
        FirmwareValidityBytes.Text = $"{statistics.ValidityBytes} bytes";
        FirmwareGameCount.Text = statistics.GameCount.ToString();
        FirmwareAssignedGameCount.Text = statistics.EffectiveAssignmentCount.ToString();
        FirmwareNanoSelectedCount.Text = statistics.NanoSelectedCount.ToString();
        FirmwareMahjongExcludedCount.Text = statistics.ExcludedMahjongCount.ToString();
        FirmwareNameBytes.Text = $"{statistics.PackedNameBytes} bytes";
        FirmwareOffsetBytes.Text = $"{statistics.OffsetBytes} bytes";
        FirmwareMappingBytes.Text = $"{statistics.MappingBytes} bytes";
        FirmwareJumpBytes.Text = $"{statistics.JumpTableBytes} bytes";
        FirmwareAverageName.Text = $"{statistics.AverageNameLength:0.0} chars";
        FirmwareLongestName.Text = $"{statistics.LongestNameLength} chars";
        FirmwareTotalBytes.Text = $"{statistics.TotalBytes} bytes";
    }

    private void SetFirmwareDirectory(string directory)
    {
        FirmwareDirectory.Text = directory;
        FirmwareOutputPath.Text = string.IsNullOrWhiteSpace(directory)
            ? "Select a directory"
            : Path.Combine(directory, FirmwareDatabaseGenerator.OutputFileName);
    }

    private static string? FindDefaultFirmwareDirectory()
    {
        foreach (var startingDirectory in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startingDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "firmware", "CrtGeometryController");
                if (Directory.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }
        return null;
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter="CSV backup ZIP (*.zip)|*.zip",DefaultExt=".zip",FileName="crtgeometry-csv.zip",Title="Export CSV set" };
        if(dialog.ShowDialog(this)!=true)return;
        try { _csvService.Export(dialog.FileName); CsvStatus.Text="Export complete."; CsvSummary.Text=$"Created {dialog.FileName}"; }
        catch(Exception ex){MessageBox.Show(this,ex.Message,"CSV export failed",MessageBoxButton.OK,MessageBoxImage.Error);}
    }

    private async void ValidateCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Filter="CSV backup ZIP (*.zip)|*.zip|All files (*.*)|*.*",Title="Select CSV import set"};
        if(dialog.ShowDialog(this)!=true)return;
        _csvPreviewMode=CsvMode.SelectedIndex==1?CsvImportMode.Replace:CsvImportMode.Merge;
        _csvPreview=await Task.Run(()=>_csvService.Validate(dialog.FileName,_csvPreviewMode));
        ApplyCsvButton.IsEnabled=_csvPreview.IsValid;
        CsvStatus.Text=_csvPreview.IsValid?"Validation passed. Review the summary, then apply.":"Validation failed; nothing has been changed.";
        var errors=_csvPreview.Errors.Count==0?"None":string.Join(Environment.NewLine,_csvPreview.Errors.Select(x=>"- "+x));
        CsvSummary.Text=$"Mode: {_csvPreviewMode}{Environment.NewLine}Profiles: {_csvPreview.ProfilesFound}{Environment.NewLine}Calibrations: {_csvPreview.CalibrationsFound}{Environment.NewLine}Active mappings: {_csvPreview.MappingsFound}{Environment.NewLine}Assignments: {_csvPreview.AssignmentsFound}{Environment.NewLine}Inserts: {_csvPreview.Inserts}{Environment.NewLine}Updates: {_csvPreview.Updates}{Environment.NewLine}Unresolved ROM names: {_csvPreview.UnresolvedRomNames.Count}{Environment.NewLine}{Environment.NewLine}Validation errors:{Environment.NewLine}{errors}";
    }

    private async void ApplyCsv_Click(object sender, RoutedEventArgs e)
    {
        if(_csvPreview is null||!_csvPreview.IsValid)return;
        if(MessageBox.Show(this,$"Apply this {_csvPreviewMode} import in one transaction?", "Confirm CSV import",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        try { await Task.Run(()=>_csvService.Apply(_csvPreview,_csvPreviewMode)); CsvStatus.Text="Import applied successfully.";ApplyCsvButton.IsEnabled=false;ReloadProfiles();await _gamesViewModel.RefreshAsync(); }
        catch(Exception ex){CsvStatus.Text="Import failed; all changes were rolled back.";MessageBox.Show(this,ex.Message,"CSV import failed",MessageBoxButton.OK,MessageBoxImage.Error);}
    }

    private async void PreviewCalibration_Click(object sender, RoutedEventArgs e)
    { try { await _calibrationViewModel.PreviewAsync(); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Cannot preview"); } }
    private async void ApplyCalibration_Click(object sender, RoutedEventArgs e)
    {
        try { await _calibrationViewModel.ApplyAsync(); await _gamesViewModel.RefreshAsync(); ReloadProfiles(); }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"Cannot apply calibration"); }
    }
    private void GeometryTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox) textBox.SelectAll();
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
