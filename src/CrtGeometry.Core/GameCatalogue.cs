using System.Globalization;

namespace CrtGeometry.Core;

public enum InclusionFilter { IncludedOnly, All, ExcludedOnly }
public enum PresenceFilter { PresentOnly, All, AbsentOnly }
public enum ProfileFilter { All, AssignedOnly, UnassignedOnly }

public sealed class GameCatalogueQuery
{
    public string SearchText { get; init; } = "";
    public InclusionFilter Inclusion { get; init; } = InclusionFilter.IncludedOnly;
    public PresenceFilter Presence { get; init; } = PresenceFilter.PresentOnly;
    public ProfileFilter Profile { get; init; } = ProfileFilter.All;
}

public sealed class GameCatalogueEntry
{
    public required string RomName { get; init; }
    public string? Description { get; init; }
    public string? Year { get; init; }
    public string? Manufacturer { get; init; }
    public string? CloneOf { get; init; }
    public int? CoinInputs { get; init; }
    public bool IsIncluded { get; init; }
    public bool IsPresent { get; init; }
    public MameExclusionReason ExclusionReasons { get; init; }
    public int? ProfileId { get; init; }
    public ProfileAssignmentType? AssignmentType { get; init; }
    public string? CalibrationSourceRomName { get; init; }
    public List<MameDisplay> Displays { get; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? RomName : Description;
    public string InclusionStatus => IsIncluded ? "Included" : $"Excluded: {ExclusionReasonText}";
    public string ProfileStatus => ProfileId is int id ? $"Profile {id} / {AssignmentType}" : "Unassigned";
    public VideoModeSelection VideoMode => new VideoSignatureService().SelectPrimary(Displays);
    public string ExclusionReasonText => ExclusionReasons == MameExclusionReason.None ? "None" :
        string.Join(", ", Enum.GetValues<MameExclusionReason>()
            .Where(value => value != MameExclusionReason.None && ExclusionReasons.HasFlag(value))
            .Select(FormatReason));
    public string ResolutionSummary => Displays.Count switch
    {
        0 => "Unknown",
        1 => FormatResolution(Displays[0]),
        _ => $"{FormatResolution(Displays[0])} (+{Displays.Count - 1} displays)"
    };
    public string RotationSummary => Displays.Count == 0 || Displays[0].Rotate is null ? "Unknown" : $"Rotate {Displays[0].Rotate}";
    public string RefreshSummary => Displays.Count == 0 || Displays[0].Refresh is null ? "Unknown" :
        Displays[0].Refresh.Value.ToString("0.######", CultureInfo.InvariantCulture) + " Hz";

    private static string FormatResolution(MameDisplay display) => display.Width is null || display.Height is null
        ? "Unknown" : $"{display.Width} x {display.Height}";
    private static string FormatReason(MameExclusionReason value) => value switch
    {
        MameExclusionReason.NotRunnable => "Not runnable",
        MameExclusionReason.NoDisplay => "No display",
        MameExclusionReason.NonRaster => "Non-raster",
        MameExclusionReason.NoCoinInput => "No coin input",
        _ => value.ToString()
    };
}

/// <summary>Reusable exact-game selection state for catalogue and future workflows.</summary>
public sealed class GameSelectionModel
{
    public string SearchText { get; set; } = "";
    public IReadOnlyList<GameCatalogueEntry> Candidates { get; private set; } = [];
    public GameCatalogueEntry? SelectedGame { get; set; }
    public string? SelectedRomName => SelectedGame?.RomName;
    public void SetCandidates(IReadOnlyList<GameCatalogueEntry> candidates)
    {
        Candidates = candidates;
        if (SelectedGame is not null && !candidates.Any(x => x.RomName == SelectedGame.RomName)) SelectedGame = null;
    }
}
