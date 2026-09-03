using System.Globalization;
using System.Text;
using CrtGeometry.Core;
using Microsoft.Data.Sqlite;

namespace CrtGeometry.Data;

public sealed record FirmwareGame(string RomName, string Description, int ProfileId);

public sealed record GeneratedFirmwareGame(string RomName, string DisplayName, byte ProfileId, uint NameBitOffset);

public sealed record FirmwareDatabaseStatistics(
    int ProfileCount, int HighestProfileId, int ProfileTableBytes, int ValidityBytes,
    int GameCount, int PackedNameBytes, int OffsetBytes, int MappingBytes, int JumpTableBytes,
    int TotalNameBits, double AverageNameLength, int LongestNameLength)
{
    public int TotalBytes => ProfileTableBytes + ValidityBytes + PackedNameBytes + OffsetBytes + MappingBytes + JumpTableBytes;
}

public sealed record FirmwareDatabaseGeneration(
    string Content, FirmwareDatabaseStatistics Statistics,
    IReadOnlyList<GeneratedFirmwareGame> Games, byte[] PackedNames, ushort[] AlphabetJumps);

/// <summary>Generates the complete AVR profile/game include from authoritative SQLite state.</summary>
public sealed class FirmwareDatabaseGenerator(string connectionString)
{
    public const string OutputFileName = "GeneratedDatabase.h";
    public const int ProfileSlotCount = 256;
    public const int GeometryBytesPerProfile = 5;
    public const int ValidityByteCount = ProfileSlotCount / 8;
    public const string NameAlphabet = " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/.\'&+:!?,()[]*=_%#@$<>;^~|";
    public const int AlphabetGroupCount = 27; // #, A..Z

    public FirmwareDatabaseGeneration Preview() => Generate(LoadProfiles(), LoadEligibleGames());

    public FirmwareDatabaseStatistics Write(string firmwareDirectory)
    {
        var generation = Preview(); // Complete validation before touching the destination.
        if (string.IsNullOrWhiteSpace(firmwareDirectory))
            throw new ArgumentException("Select a firmware directory.", nameof(firmwareDirectory));
        Directory.CreateDirectory(firmwareDirectory);
        var target = Path.Combine(firmwareDirectory, OutputFileName);
        var temporary = Path.Combine(firmwareDirectory, $".{OutputFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, generation.Content, new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return generation.Statistics;
    }

    public static FirmwareDatabaseGeneration Generate(IEnumerable<GeometryProfile> profiles) => Generate(profiles, []);

    public static FirmwareDatabaseGeneration Generate(IEnumerable<GeometryProfile> source, IEnumerable<FirmwareGame> gameSource)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(gameSource);
        if (NameAlphabet.Length != 64 || NameAlphabet.Distinct().Count() != 64)
            throw new InvalidOperationException("The firmware name alphabet must contain exactly 64 unique characters.");
        var profiles = source.OrderBy(p => p.Id).ToArray();
        var ids = new HashSet<int>();
        foreach (var p in profiles)
        {
            if (p.Id is < 1 or > 255) throw new InvalidDataException($"Profile ID {p.Id} must be between 1 and 255.");
            if (!ids.Add(p.Id)) throw new InvalidDataException($"Duplicate profile ID {p.Id}.");
            ValidateGeometry(p.HSH, nameof(p.HSH), p.Id); ValidateGeometry(p.VSL, nameof(p.VSL), p.Id);
            ValidateGeometry(p.VAM, nameof(p.VAM), p.Id); ValidateGeometry(p.VSC, nameof(p.VSC), p.Id); ValidateGeometry(p.VSH, nameof(p.VSH), p.Id);
        }

        var rawGames = gameSource.ToArray();
        if (rawGames.Length > ushort.MaxValue) throw new InvalidDataException("Generated game count exceeds uint16_t capacity.");
        var roms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<(FirmwareGame Game, string Name)>();
        foreach (var game in rawGames)
        {
            if (string.IsNullOrWhiteSpace(game.RomName) || !roms.Add(game.RomName)) throw new InvalidDataException($"Duplicate or empty ROM name '{game.RomName}'.");
            if (game.ProfileId is < 1 or > 255) throw new InvalidDataException($"Game {game.RomName} has invalid profile ID {game.ProfileId}.");
            if (!ids.Contains(game.ProfileId)) throw new InvalidDataException($"Game {game.RomName} references missing profile {game.ProfileId}.");
            var name = NormalizeDisplayName(game.Description);
            if (name.Length == 0) throw new InvalidDataException($"Game {game.RomName} has no usable description.");
            if (name.Length > ushort.MaxValue) throw new InvalidDataException($"Game {game.RomName} name exceeds uint16_t decoder length capacity.");
            normalized.Add((game, name));
        }
        var collisions = normalized.GroupBy(x => x.Name, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        // Sort by the normalized MAME description and use RomName only as the
        // deterministic secondary key.  Collision suffixes are presentation data;
        // allowing them to participate in sorting would invert prefix ROM names
        // (for example, DKONGB sorts before DKONG because 'B' precedes ']').
        var ordered = normalized.OrderBy(x => x.Name[0] is >= 'A' and <= 'Z' ? x.Name[0] - 'A' + 1 : 0)
            .ThenBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Game.RomName, StringComparer.Ordinal).ToArray();

        var symbols = new List<byte>(); var games = new List<GeneratedFirmwareGame>(ordered.Length);
        foreach (var item in ordered)
        {
            var displayName = collisions.Contains(item.Name)
                ? $"{item.Name} [{NormalizeDisplayName(item.Game.RomName)}]"
                : item.Name;
            var bitOffset = checked((uint)(symbols.Count * 6L));
            games.Add(new(item.Game.RomName, displayName, (byte)item.Game.ProfileId, bitOffset));
            symbols.AddRange(displayName.Select(c => checked((byte)NameAlphabet.IndexOf(c))));
        }
        var totalBitsLong = symbols.Count * 6L;
        ValidateTotalNameBits(totalBitsLong);
        var totalBits = (uint)totalBitsLong;
        var packed = PackSymbols(symbols);
        var jumps = BuildAlphabetJumps(games);

        var bitmap = new byte[ValidityByteCount]; foreach (var p in profiles) bitmap[p.Id >> 3] |= (byte)(1 << (p.Id & 7));
        var byId = profiles.ToDictionary(p => p.Id); var highest = profiles.Length == 0 ? 0 : profiles[^1].Id;
        var stats = new FirmwareDatabaseStatistics(profiles.Length, highest, 1280, 32, games.Count, packed.Length,
            games.Count * sizeof(uint), games.Count, AlphabetGroupCount * sizeof(ushort), (int)totalBits,
            games.Count == 0 ? 0 : games.Average(g => g.DisplayName.Length), games.Count == 0 ? 0 : games.Max(g => g.DisplayName.Length));
        return new(BuildHeader(byId, profiles.Length, highest, bitmap, games, packed, jumps, totalBits, stats), stats, games, packed, jumps);
    }

    public static string NormalizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD); var output = new StringBuilder(); var pendingSpace = false;
        foreach (var source in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(source) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToUpperInvariant(source);
            if (c is '’' or '‘' or '`') c = '\''; else if (c is '–' or '—' or '−') c = '-';
            if (char.IsWhiteSpace(c) || NameAlphabet.IndexOf(c) < 0) { pendingSpace = output.Length > 0; continue; }
            if (pendingSpace) { output.Append(' '); pendingSpace = false; }
            output.Append(c);
        }
        return output.ToString().Trim();
    }

    public static byte[] PackSymbols(IEnumerable<byte> source)
    {
        var symbols = source.ToArray(); var result = new byte[(symbols.Length * 6 + 7) / 8];
        for (var i = 0; i < symbols.Length; i++)
        {
            if (symbols[i] > 63) throw new InvalidDataException("A packed name symbol exceeds six bits.");
            var bit = i * 6; result[bit >> 3] |= (byte)(symbols[i] << (bit & 7));
            if ((bit & 7) > 2) result[(bit >> 3) + 1] |= (byte)(symbols[i] >> (8 - (bit & 7)));
        }
        return result;
    }

    public static void ValidateTotalNameBits(long totalBits)
    {
        if (totalBits is < 0 or > uint.MaxValue)
            throw new InvalidDataException("Packed game-name offsets exceed uint32_t capacity.");
    }

    public static string DecodeName(byte[] packed, uint offset, uint bitLength)
    {
        var result = new StringBuilder();
        for (uint bit = offset; bit < offset + bitLength; bit += 6)
        {
            var value = packed[bit >> 3] >> (int)(bit & 7);
            if ((bit & 7) > 2 && (bit >> 3) + 1 < packed.Length) value |= packed[(bit >> 3) + 1] << (8 - (int)(bit & 7));
            result.Append(NameAlphabet[value & 63]);
        }
        return result.ToString();
    }

    private static ushort[] BuildAlphabetJumps(IReadOnlyList<GeneratedFirmwareGame> games)
    {
        var jumps = Enumerable.Repeat((ushort)games.Count, AlphabetGroupCount).ToArray();
        for (var i = 0; i < games.Count; i++) { var c = games[i].DisplayName[0]; var group = c is >= 'A' and <= 'Z' ? c - 'A' + 1 : 0; if (jumps[group] == games.Count) jumps[group] = (ushort)i; }
        return jumps;
    }

    private IReadOnlyList<GeometryProfile> LoadProfiles() => new GeometryProfileRepository(connectionString).GetAll();
    private IReadOnlyList<FirmwareGame> LoadEligibleGames()
    {
        using var connection = SqliteConnectionFactory.Open(connectionString); using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.RomName,m.Description,a.ProfileId FROM MameMachines m JOIN GameProfileAssignments a ON a.RomName=m.RomName WHERE m.IsPresent=1 AND m.IsIncluded=1 ORDER BY m.RomName";
        var games = new List<FirmwareGame>(); using var reader = command.ExecuteReader();
        while (reader.Read()) games.Add(new(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetInt32(2)));
        return games;
    }

    private static string BuildHeader(Dictionary<int, GeometryProfile> byId, int profileCount, int highest, byte[] bitmap,
        IReadOnlyList<GeneratedFirmwareGame> games, byte[] packed, ushort[] jumps, uint totalBits, FirmwareDatabaseStatistics stats)
    {
        var t = new StringBuilder(20000 + packed.Length * 6);
        t.Append("// AUTO-GENERATED BY CRT GEOMETRY\n// DO NOT EDIT MANUALLY\n\n#ifndef CRT_GEOMETRY_GENERATED_DATABASE_H\n#define CRT_GEOMETRY_GENERATED_DATABASE_H\n\n#include <Arduino.h>\n#include <avr/pgmspace.h>\n\n")
         .Append("struct GeneratedGeometryProfile\n{\n    uint8_t hsh;\n    uint8_t vsl;\n    uint8_t vam;\n    uint8_t vsc;\n    uint8_t vsh;\n};\n\n")
         .Append($"const uint16_t GENERATED_PROFILE_COUNT = {profileCount};\nconst uint8_t GENERATED_MAX_PROFILE_ID = {highest};\nconst uint8_t GENERATED_PROFILE_VALIDITY_BYTES = 32;\n")
         .Append($"const uint16_t GENERATED_GAME_COUNT = {games.Count};\nconst uint32_t GENERATED_TOTAL_NAME_BITS = {totalBits}UL;\nconst uint32_t GENERATED_PACKED_NAME_BYTES = {packed.Length}UL;\nconst uint32_t GENERATED_DATABASE_BYTES = {stats.TotalBytes}UL;\n\n")
         .Append("const char GENERATED_NAME_ALPHABET[65] PROGMEM = \"").Append(NameAlphabet.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\";\n\n")
         .Append("const GeneratedGeometryProfile GENERATED_PROFILES[256] PROGMEM =\n{\n");
        for (var id=0;id<256;id++) { var v=byId.TryGetValue(id,out var p)?$"{p.HSH}, {p.VSL}, {p.VAM}, {p.VSC}, {p.VSH}":"0, 0, 0, 0, 0"; t.Append("    { ").Append(v).Append(" }"); if(id!=255)t.Append(','); t.Append(" // ").Append(id).Append('\n'); }
        t.Append("};\n\n");
        AppendArray(t,"uint8_t","GENERATED_PROFILE_VALIDITY",bitmap.Select(x=>$"0x{x:X2}"));
        AppendArray(t,"uint8_t","GENERATED_GAME_NAME_BITS",packed.Select(x=>$"0x{x:X2}"));
        AppendArray(t,"uint32_t","GENERATED_GAME_NAME_BIT_OFFSETS",games.Select(x=>$"{x.NameBitOffset}UL"));
        AppendArray(t,"uint8_t","GENERATED_GAME_PROFILE_IDS",games.Select(x=>x.ProfileId.ToString()));
        AppendArray(t,"uint16_t","GENERATED_ALPHABET_JUMPS",jumps.Select(x=>x.ToString()));
        t.Append("#endif\n"); return t.ToString();
    }

    private static void AppendArray(StringBuilder t,string type,string name,IEnumerable<string> values)
    {
        var a=values.ToArray(); t.Append("const ").Append(type).Append(' ').Append(name).Append('[').Append(Math.Max(1,a.Length)).Append("] PROGMEM =\n{\n    ");
        if (a.Length == 0) a = ["0"];
        for(var i=0;i<a.Length;i++){if(i>0)t.Append(i%12==0?",\n    ":", ");t.Append(a[i]);} t.Append("\n};\n\n");
    }

    private static void ValidateGeometry(int value,string field,int id) { if(value is <0 or >63) throw new InvalidDataException($"Profile {id} {field} value {value} must be between 0 and 63."); }
}
