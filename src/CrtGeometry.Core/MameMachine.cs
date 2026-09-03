namespace CrtGeometry.Core;

[Flags]
public enum MameExclusionReason
{
    None = 0,
    Bios = 1,
    Device = 2,
    Mechanical = 4,
    NotRunnable = 8,
    NoDisplay = 16,
    NonRaster = 32,
    NoCoinInput = 64
}

public sealed class MameMachine
{
    public required string RomName { get; init; }
    public string? Description { get; set; }
    public string? Year { get; set; }
    public string? Manufacturer { get; set; }
    public string? CloneOf { get; init; }
    public bool Runnable { get; init; } = true;
    public bool IsBios { get; init; }
    public bool IsDevice { get; init; }
    public bool IsMechanical { get; init; }
    public int? CoinInputs { get; set; }
    public List<MameDisplay> Displays { get; } = [];
    public MameExclusionReason ExclusionReasons { get; set; }
    public bool IsIncluded => ExclusionReasons == MameExclusionReason.None;
}

public sealed class MameDisplay
{
    public string? Type { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Rotate { get; init; }
    public double? Refresh { get; init; }
    public long? PixelClock { get; init; }
    public int? HTotal { get; init; }
    public int? HBEnd { get; init; }
    public int? HBStart { get; init; }
    public int? VTotal { get; init; }
    public int? VBEnd { get; init; }
    public int? VBStart { get; init; }
    public required string RawAttributesJson { get; init; }
}

public sealed record MameSourceMetadata(string? Build, string? Debug, string? MameConfig);

public sealed record MameParseProgress(int MachinesParsed, string? CurrentRomName);
