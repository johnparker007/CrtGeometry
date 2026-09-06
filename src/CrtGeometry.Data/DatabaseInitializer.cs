using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public sealed class DatabaseInitializer(string connectionString)
{
    public const int CurrentVersion = 5;
    public void Initialize()
    {
        using var connection = SqliteConnectionFactory.Open(connectionString);

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());

        if (version < 1)
        {
            ApplyVersion1(connection);
            version = 1;
        }

        if (version < 2)
        {
            ApplyVersion2(connection);
            version = 2;
        }

        if (version < 3)
        {
            ApplyVersion3(connection);
            version = 3;
        }

        if (version < 4)
        {
            ApplyVersion4(connection);
            version = 4;
        }

        if (version < 5)
        {
            ApplyVersion5(connection);
            version = 5;
        }

        if (version > CurrentVersion)
        {
            throw new InvalidOperationException($"Database version {version} is newer than this application supports.");
        }
    }

    private static void ApplyVersion5(SqliteConnection connection)
    {
        // Refuse to guess when two formerly distinct keys have different owners.
        using (var conflict = connection.CreateCommand())
        {
            conflict.CommandText = """
                SELECT Width,Height,((Rotation%180)+180)%180,RefreshMicroHz,
                       group_concat(DISTINCT ProfileId)
                FROM VideoProfileMappings
                GROUP BY Width,Height,((Rotation%180)+180)%180,RefreshMicroHz
                HAVING COUNT(DISTINCT ProfileId)>1
                ORDER BY Width,Height,RefreshMicroHz LIMIT 1;
                """;
            using var reader = conflict.ExecuteReader();
            if (reader.Read())
                throw new InvalidOperationException($"Canonical video-signature conflict for {reader.GetInt32(0)}x{reader.GetInt32(1)}, orientation {reader.GetInt32(2)}, refresh {reader.GetInt64(3)} microHz: profiles {reader.GetString(4)}. Resolve the conflicting calibration mappings manually before upgrading.");
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TEMP TABLE NormalizedMappings AS
              SELECT Width,Height,((Rotation%180)+180)%180 AS Rotation,RefreshMicroHz,
                     ProfileId,MAX(CalibrationId) AS CalibrationId
              FROM VideoProfileMappings
              GROUP BY Width,Height,((Rotation%180)+180)%180,RefreshMicroHz,ProfileId;
            DELETE FROM VideoProfileMappings;
            INSERT INTO VideoProfileMappings SELECT * FROM NormalizedMappings;
            DROP TABLE NormalizedMappings;
            UPDATE CalibrationRecords SET Rotation=((Rotation%180)+180)%180;
            UPDATE GameProfileAssignments SET Rotation=((Rotation%180)+180)%180 WHERE AssignmentType=1;

            INSERT INTO GameProfileAssignments(RomName,ProfileId,AssignmentType,Width,Height,Rotation,RefreshMicroHz,UpdatedAtUtc)
            SELECT m.RomName,v.ProfileId,1,d.Width,d.Height,((d.Rotate%180)+180)%180,
                   CAST(round(d.Refresh*1000000.0) AS INTEGER),strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM MameMachines m
            JOIN MameDisplays d ON d.RomName=m.RomName
            JOIN VideoProfileMappings v ON v.Width=d.Width AND v.Height=d.Height
              AND v.Rotation=((d.Rotate%180)+180)%180
              AND v.RefreshMicroHz=CAST(round(d.Refresh*1000000.0) AS INTEGER)
            WHERE m.IsIncluded=1 AND m.IsPresent=1
              AND (m.CloneOf IS NULL OR trim(m.CloneOf)='')
              AND (d.Type IS NULL OR trim(d.Type)='' OR lower(d.Type)='raster')
              AND (SELECT COUNT(*) FROM MameDisplays rd WHERE rd.RomName=m.RomName
                   AND (rd.Type IS NULL OR trim(rd.Type)='' OR lower(rd.Type)='raster'))=1
            ON CONFLICT(RomName) DO UPDATE SET ProfileId=excluded.ProfileId,AssignmentType=1,
              Width=excluded.Width,Height=excluded.Height,Rotation=excluded.Rotation,
              RefreshMicroHz=excluded.RefreshMicroHz,UpdatedAtUtc=excluded.UpdatedAtUtc
              WHERE GameProfileAssignments.AssignmentType=1;
            PRAGMA user_version = 5;
            """;
        command.ExecuteNonQuery(); transaction.Commit();
    }

    private static void ApplyVersion4(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(MameMachines);";
        var hasColumn = false;
        using (var reader = command.ExecuteReader())
            while (reader.Read()) hasColumn |= string.Equals(reader.GetString(1), "IncludeOnNano", StringComparison.OrdinalIgnoreCase);
        command.CommandText = (hasColumn ? "" :
            "ALTER TABLE MameMachines ADD COLUMN IncludeOnNano INTEGER NOT NULL DEFAULT 0 CHECK (IncludeOnNano IN (0,1));") + """

            CREATE INDEX IF NOT EXISTS IX_MameMachines_IncludeOnNano ON MameMachines(IncludeOnNano);
            PRAGMA user_version = 4;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ApplyVersion3(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE CalibrationRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId INTEGER NOT NULL REFERENCES GeometryProfiles(Id) ON DELETE RESTRICT,
                SourceRomName TEXT NOT NULL REFERENCES MameMachines(RomName) ON DELETE RESTRICT,
                Width INTEGER NOT NULL, Height INTEGER NOT NULL, Rotation INTEGER NOT NULL,
                RefreshMicroHz INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE VideoProfileMappings (
                Width INTEGER NOT NULL, Height INTEGER NOT NULL, Rotation INTEGER NOT NULL,
                RefreshMicroHz INTEGER NOT NULL,
                ProfileId INTEGER NOT NULL REFERENCES GeometryProfiles(Id) ON DELETE RESTRICT,
                CalibrationId INTEGER NOT NULL REFERENCES CalibrationRecords(Id) ON DELETE RESTRICT,
                PRIMARY KEY (Width, Height, Rotation, RefreshMicroHz)
            );
            CREATE TABLE GameProfileAssignments (
                RomName TEXT PRIMARY KEY REFERENCES MameMachines(RomName) ON DELETE RESTRICT,
                ProfileId INTEGER NOT NULL REFERENCES GeometryProfiles(Id) ON DELETE RESTRICT,
                AssignmentType INTEGER NOT NULL CHECK (AssignmentType IN (1,2)),
                Width INTEGER NULL, Height INTEGER NULL, Rotation INTEGER NULL, RefreshMicroHz INTEGER NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CHECK (AssignmentType=2 OR (Width IS NOT NULL AND Height IS NOT NULL AND Rotation IS NOT NULL AND RefreshMicroHz IS NOT NULL))
            );
            CREATE INDEX IX_GameProfileAssignments_Profile ON GameProfileAssignments(ProfileId);
            CREATE INDEX IX_GameProfileAssignments_Signature ON GameProfileAssignments(Width,Height,Rotation,RefreshMicroHz);
            PRAGMA user_version = 3;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ApplyVersion2(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE MameImports (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Build TEXT NULL,
                Debug TEXT NULL,
                MameConfig TEXT NULL,
                SourceFileName TEXT NULL,
                ImportedAtUtc TEXT NOT NULL,
                DurationMilliseconds INTEGER NOT NULL,
                TotalMachines INTEGER NOT NULL,
                IncludedMachines INTEGER NOT NULL,
                MachinesWithDisplays INTEGER NOT NULL
            );
            CREATE TABLE MameMachines (
                RomName TEXT PRIMARY KEY,
                Description TEXT NULL,
                Year TEXT NULL,
                Manufacturer TEXT NULL,
                CloneOf TEXT NULL,
                Runnable INTEGER NOT NULL,
                IsBios INTEGER NOT NULL,
                IsDevice INTEGER NOT NULL,
                IsMechanical INTEGER NOT NULL,
                CoinInputs INTEGER NULL,
                ExclusionReasons INTEGER NOT NULL,
                IsIncluded INTEGER NOT NULL,
                LastImportId INTEGER NOT NULL REFERENCES MameImports(Id),
                IsPresent INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE MameDisplays (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RomName TEXT NOT NULL REFERENCES MameMachines(RomName) ON DELETE CASCADE,
                DisplayIndex INTEGER NOT NULL,
                Type TEXT NULL, Width INTEGER NULL, Height INTEGER NULL, Rotate INTEGER NULL,
                Refresh REAL NULL, PixelClock INTEGER NULL,
                HTotal INTEGER NULL, HBEnd INTEGER NULL, HBStart INTEGER NULL,
                VTotal INTEGER NULL, VBEnd INTEGER NULL, VBStart INTEGER NULL,
                RawAttributesJson TEXT NOT NULL,
                UNIQUE (RomName, DisplayIndex)
            );
            CREATE INDEX IX_MameMachines_Included ON MameMachines(IsIncluded, IsPresent);
            PRAGMA user_version = 2;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ApplyVersion1(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE GeometryProfiles (
                Id INTEGER PRIMARY KEY CHECK (Id BETWEEN 1 AND 255),
                HSH INTEGER NOT NULL CHECK (HSH BETWEEN 0 AND 63),
                VSL INTEGER NOT NULL CHECK (VSL BETWEEN 0 AND 63),
                VAM INTEGER NOT NULL CHECK (VAM BETWEEN 0 AND 63),
                VSC INTEGER NOT NULL CHECK (VSC BETWEEN 0 AND 63),
                VSH INTEGER NOT NULL CHECK (VSH BETWEEN 0 AND 63),
                Notes TEXT NULL
            );
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
