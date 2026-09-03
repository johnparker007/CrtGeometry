using CrtGeometry.Core;

namespace CrtGeometry.Tests;

public sealed class GeometryProfileTests
{
    private static readonly Action<GeometryProfile, int>[] GeometrySetters =
    [
        (profile, value) => profile.HSH = value,
        (profile, value) => profile.VSL = value,
        (profile, value) => profile.VAM = value,
        (profile, value) => profile.VSC = value,
        (profile, value) => profile.VSH = value
    ];

    [Fact]
    public void GeometryValuesAcceptRangeBoundaries()
    {
        foreach (var setValue in GeometrySetters)
        {
            var profile = new GeometryProfile();
            setValue(profile, 0);
            setValue(profile, 63);
        }
    }

    [Fact]
    public void GeometryValuesRejectValuesOutsideRange()
    {
        foreach (var setValue in GeometrySetters)
        {
            var profile = new GeometryProfile();
            Assert.Throws<ArgumentOutOfRangeException>(() => setValue(profile, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => setValue(profile, 64));
        }
    }
}
