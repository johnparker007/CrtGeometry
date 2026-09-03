namespace CrtGeometry.Core;

public sealed class GeometryProfile
{
    private int _hsh;
    private int _vsl;
    private int _vam;
    private int _vsc;
    private int _vsh;

    public GeometryProfile(int id)
    {
        Id = ValidateId(id);
    }

    public int Id { get; }
    public int HSH { get => _hsh; set => _hsh = Validate(value, nameof(HSH)); }
    public int VSL { get => _vsl; set => _vsl = Validate(value, nameof(VSL)); }
    public int VAM { get => _vam; set => _vam = Validate(value, nameof(VAM)); }
    public int VSC { get => _vsc; set => _vsc = Validate(value, nameof(VSC)); }
    public int VSH { get => _vsh; set => _vsh = Validate(value, nameof(VSH)); }
    public string? Notes { get; set; }
    public string? CalibrationSourceRomName { get; set; }
    public string? CalibrationSourceTitle { get; set; }
    public int AssignedGameCount { get; set; }

    private static int ValidateId(int value)
    {
        if (value is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Id), value, "Profile IDs must be between 1 and 255.");
        }

        return value;
    }

    private static int Validate(int value, string propertyName)
    {
        if (value is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(propertyName, value, "Geometry values must be between 0 and 63.");
        }

        return value;
    }
}
