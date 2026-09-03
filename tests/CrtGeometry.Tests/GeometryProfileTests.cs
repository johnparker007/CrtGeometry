using CrtGeometry.Core;

namespace CrtGeometry.Tests;

public sealed class GeometryProfileTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public void IdAcceptsValidRangeBoundaries(int id)
    {
        Assert.Equal(id, new GeometryProfile(id).Id);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(256)]
    public void IdRejectsValuesOutsideValidRange(int id)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeometryProfile(id));
    }

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
            var profile = new GeometryProfile(1);
            setValue(profile, 0);
            setValue(profile, 63);
        }
    }

    [Fact]
    public void GeometryValuesRejectValuesOutsideRange()
    {
        foreach (var setValue in GeometrySetters)
        {
            var profile = new GeometryProfile(1);
            Assert.Throws<ArgumentOutOfRangeException>(() => setValue(profile, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => setValue(profile, 64));
        }
    }
}
