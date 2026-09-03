using System.Diagnostics;
using CrtGeometry.Core;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public sealed record MameImportSummary(long ImportId, string? Build, int TotalMachines, int IncludedMachines,
    int MachinesWithDisplays, TimeSpan Duration, IReadOnlyDictionary<MameExclusionReason, int> ExclusionCounts)
{
    public int ExcludedMachines => TotalMachines - IncludedMachines;
}

public sealed class MameImportService(string connectionString, MameXmlParser? parser = null,
    MameFilterPolicy? filter = null)
{
    private readonly MameXmlParser _parser = parser ?? new();
    private readonly MameFilterPolicy _filter = filter ?? new();

    public MameImportSummary Import(string fileName, IProgress<MameParseProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan);
        return Import(stream, Path.GetFileName(fileName), progress, cancellationToken);
    }

    public MameImportSummary Import(Stream stream, string? sourceFileName = null,
        IProgress<MameParseProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        using var connection = SqliteConnectionFactory.Open(connectionString);
        using var transaction = connection.BeginTransaction();
        var importId = CreateImport(connection, transaction, sourceFileName);
        var total = 0; var included = 0; var withDisplays = 0;
        var reasonCounts = new Dictionary<MameExclusionReason, int>();
        try
        {
            var metadata = _parser.Parse(stream, machine =>
            {
                machine.ExclusionReasons = _filter.Evaluate(machine);
                total++;
                if (machine.IsIncluded) included++;
                if (machine.Displays.Count > 0) withDisplays++;
                foreach (var reason in Enum.GetValues<MameExclusionReason>().Where(r => r != 0 && machine.ExclusionReasons.HasFlag(r)))
                    reasonCounts[reason] = reasonCounts.GetValueOrDefault(reason) + 1;
                UpsertMachine(connection, transaction, importId, machine);
            }, progress, cancellationToken);

            using (var absent = connection.CreateCommand())
            {
                absent.Transaction = transaction;
                absent.CommandText = "UPDATE MameMachines SET IsPresent=0 WHERE LastImportId <> $id;";
                absent.Parameters.AddWithValue("$id", importId);
                absent.ExecuteNonQuery();
            }
            timer.Stop();
            CompleteImport(connection, transaction, importId, metadata, timer.Elapsed, total, included, withDisplays);
            transaction.Commit();
            return new(importId, metadata.Build, total, included, withDisplays, timer.Elapsed, reasonCounts);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static long CreateImport(SqliteConnection connection, SqliteTransaction transaction, string? source)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO MameImports (SourceFileName, ImportedAtUtc, DurationMilliseconds, TotalMachines, IncludedMachines, MachinesWithDisplays)
            VALUES ($source, $at, 0, 0, 0, 0); SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        return (long)(command.ExecuteScalar() ?? throw new InvalidOperationException("Could not create import."));
    }

    private static void UpsertMachine(SqliteConnection c, SqliteTransaction tx, long importId, MameMachine m)
    {
        using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO MameMachines (RomName,Description,Year,Manufacturer,CloneOf,Runnable,IsBios,IsDevice,IsMechanical,CoinInputs,ExclusionReasons,IsIncluded,LastImportId,IsPresent)
            VALUES ($name,$description,$year,$manufacturer,$clone,$runnable,$bios,$device,$mechanical,$coins,$reasons,$included,$import,1)
            ON CONFLICT(RomName) DO UPDATE SET Description=excluded.Description,Year=excluded.Year,Manufacturer=excluded.Manufacturer,
              CloneOf=excluded.CloneOf,Runnable=excluded.Runnable,IsBios=excluded.IsBios,IsDevice=excluded.IsDevice,
              IsMechanical=excluded.IsMechanical,CoinInputs=excluded.CoinInputs,ExclusionReasons=excluded.ExclusionReasons,
              IsIncluded=excluded.IsIncluded,LastImportId=excluded.LastImportId,IsPresent=1;
            DELETE FROM MameDisplays WHERE RomName=$name;
            """;
        command.Parameters.AddWithValue("$name", m.RomName);
        Add(command, "$description", m.Description); Add(command, "$year", m.Year); Add(command, "$manufacturer", m.Manufacturer);
        Add(command, "$clone", m.CloneOf); Add(command, "$coins", m.CoinInputs);
        command.Parameters.AddWithValue("$runnable", m.Runnable); command.Parameters.AddWithValue("$bios", m.IsBios);
        command.Parameters.AddWithValue("$device", m.IsDevice); command.Parameters.AddWithValue("$mechanical", m.IsMechanical);
        command.Parameters.AddWithValue("$reasons", (int)m.ExclusionReasons); command.Parameters.AddWithValue("$included", m.IsIncluded);
        command.Parameters.AddWithValue("$import", importId); command.ExecuteNonQuery();
        for (var i = 0; i < m.Displays.Count; i++) InsertDisplay(c, tx, m.RomName, i, m.Displays[i]);
    }

    private static void InsertDisplay(SqliteConnection c, SqliteTransaction tx, string name, int index, MameDisplay d)
    {
        using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO MameDisplays (RomName,DisplayIndex,Type,Width,Height,Rotate,Refresh,PixelClock,HTotal,HBEnd,HBStart,VTotal,VBEnd,VBStart,RawAttributesJson)
            VALUES ($name,$index,$type,$width,$height,$rotate,$refresh,$pixel,$htotal,$hbend,$hbstart,$vtotal,$vbend,$vbstart,$raw);
            """;
        command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$index", index);
        Add(command, "$type", d.Type); Add(command, "$width", d.Width); Add(command, "$height", d.Height);
        Add(command, "$rotate", d.Rotate); Add(command, "$refresh", d.Refresh); Add(command, "$pixel", d.PixelClock);
        Add(command, "$htotal", d.HTotal); Add(command, "$hbend", d.HBEnd); Add(command, "$hbstart", d.HBStart);
        Add(command, "$vtotal", d.VTotal); Add(command, "$vbend", d.VBEnd); Add(command, "$vbstart", d.VBStart);
        command.Parameters.AddWithValue("$raw", d.RawAttributesJson); command.ExecuteNonQuery();
    }

    private static void CompleteImport(SqliteConnection c, SqliteTransaction tx, long id, MameSourceMetadata m,
        TimeSpan duration, int total, int included, int displays)
    {
        using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE MameImports SET Build=$build,Debug=$debug,MameConfig=$config,DurationMilliseconds=$duration,TotalMachines=$total,IncludedMachines=$included,MachinesWithDisplays=$displays WHERE Id=$id;";
        Add(command, "$build", m.Build); Add(command, "$debug", m.Debug); Add(command, "$config", m.MameConfig);
        command.Parameters.AddWithValue("$duration", (long)duration.TotalMilliseconds); command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$included", included); command.Parameters.AddWithValue("$displays", displays);
        command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
