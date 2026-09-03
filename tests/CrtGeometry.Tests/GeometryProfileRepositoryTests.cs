using CrtGeometry.Core;
using CrtGeometry.Data;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Tests;

public sealed class GeometryProfileRepositoryTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"crtgeometry-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;

    public GeometryProfileRepositoryTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
        new DatabaseInitializer(_connectionString).Initialize();
    }

    [Fact]
    public void InitializeCreatesCurrentSchemaAndCanRunAgain()
    {
        new DatabaseInitializer(_connectionString).Initialize();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void SaveReloadUpdateAndDeleteProfile()
    {
        var repository = new GeometryProfileRepository(_connectionString);
        repository.Save(new GeometryProfile(7) { HSH = 1, VSL = 2, VAM = 3, VSC = 4, VSH = 5, Notes = "Initial" });

        var saved = Assert.Single(repository.GetAll());
        Assert.Equal(7, saved.Id);
        Assert.Equal("Initial", saved.Notes);
        saved.HSH = 63;
        saved.Notes = null;
        repository.Save(saved);

        var updated = Assert.Single(repository.GetAll());
        Assert.Equal(63, updated.HSH);
        Assert.Null(updated.Notes);

        repository.Delete(updated.Id);
        Assert.Empty(repository.GetAll());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(256)]
    public void DatabaseRejectsInvalidProfileIds(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GeometryProfiles (Id, HSH, VSL, VAM, VSC, VSH)
            VALUES ($id, 0, 0, 0, 0, 0);
            """;
        command.Parameters.AddWithValue("$id", id);

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }
}
