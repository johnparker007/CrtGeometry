using System.IO;
using System.Windows;
using CrtGeometry.Data;
using Microsoft.Data.Sqlite;

namespace CrtGeometry;

public partial class MainWindow : Window
{
    private readonly ProfilesViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrtGeometry");
        Directory.CreateDirectory(dataDirectory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "profiles.db")
        }.ToString();
        new DatabaseInitializer(connectionString).Initialize();
        _viewModel = new ProfilesViewModel(new GeometryProfileRepository(connectionString));
        DataContext = _viewModel;
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
        if (MessageBox.Show(this, $"Delete profile {_viewModel.SelectedProfile.Id}?", "Delete profile",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _viewModel.DeleteSelected();
        }
    }
}
