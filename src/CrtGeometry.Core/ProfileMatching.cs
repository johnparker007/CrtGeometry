using System.Globalization;

namespace CrtGeometry.Core;

public enum ProfileAssignmentType { Automatic = 1, Manual = 2 }

/// <summary>
/// Canonical raster timing key. Refresh is rounded to the nearest microhertz,
/// making representation differences below half a microhertz equivalent.
/// </summary>
public readonly record struct VideoSignature(int Width, int Height, int Rotation, long RefreshMicroHz)
{
    public override string ToString() =>
        $"{Width} x {Height} / rotate {Rotation} / {(RefreshMicroHz / 1_000_000d).ToString("0.######", CultureInfo.InvariantCulture)} Hz";
}

public enum VideoModeSelectionStatus { Usable, MissingRequiredFields, NoRasterDisplay, AmbiguousMultipleRasterDisplays }

public sealed record VideoModeSelection(VideoModeSelectionStatus Status, MameDisplay? Display, VideoSignature? Signature)
{
    public string Message => Status switch
    {
        VideoModeSelectionStatus.Usable => Signature!.Value.ToString(),
        VideoModeSelectionStatus.MissingRequiredFields => "The primary raster display is missing width, height, rotation, or refresh.",
        VideoModeSelectionStatus.NoRasterDisplay => "No raster display is available.",
        _ => "Multiple raster displays are present; automatic matching is intentionally disabled."
    };
}

/// <summary>Selects only an unambiguous single raster display and constructs signatures.</summary>
public sealed class VideoSignatureService
{
    public VideoModeSelection SelectPrimary(IReadOnlyList<MameDisplay> displays)
    {
        var raster = displays.Where(IsRaster).ToList();
        if (raster.Count == 0) return new(VideoModeSelectionStatus.NoRasterDisplay, null, null);
        if (raster.Count > 1) return new(VideoModeSelectionStatus.AmbiguousMultipleRasterDisplays, null, null);
        var display = raster[0];
        if (display.Width is null || display.Height is null || display.Rotate is null ||
            display.Refresh is null || !double.IsFinite(display.Refresh.Value) || display.Refresh <= 0)
            return new(VideoModeSelectionStatus.MissingRequiredFields, display, null);
        var refresh = checked((long)Math.Round(display.Refresh.Value * 1_000_000d, MidpointRounding.AwayFromZero));
        return new(VideoModeSelectionStatus.Usable, display,
            new VideoSignature(display.Width.Value, display.Height.Value, NormalizeRotation(display.Rotate.Value), refresh));
    }

    private static bool IsRaster(MameDisplay display) => string.IsNullOrWhiteSpace(display.Type) ||
        display.Type.Equals("raster", StringComparison.OrdinalIgnoreCase);
    private static int NormalizeRotation(int rotation) => ((rotation % 360) + 360) % 360;
}

public sealed record CalibrationValues(int HSH, int VSL, int VAM, int VSC, int VSH)
{
    public GeometryProfile ToProfile(int id, string? notes = null) => new(id)
        { HSH=HSH, VSL=VSL, VAM=VAM, VSC=VSC, VSH=VSH, Notes=notes };
}

public sealed record PropagationPreview(string SourceRomName, VideoSignature Signature, int ProfileId,
    bool ReusesExistingProfile, IReadOnlyList<GameCatalogueEntry> MatchingGames);
