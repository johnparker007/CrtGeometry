using CrtGeometry.Core;

namespace CrtGeometry.Tests;

public sealed class ProfileIdAllocatorTests
{
    [Theory]
    [InlineData(new[] { 1, 2, 3 }, 4)]
    [InlineData(new[] { 1, 2, 4 }, 3)]
    [InlineData(new[] { 2, 3 }, 1)]
    public void GetLowestAvailableReturnsFirstGap(int[] existingIds, int expected)
    {
        Assert.Equal(expected, ProfileIdAllocator.GetLowestAvailable(existingIds));
    }

    [Fact]
    public void GetLowestAvailableThrowsWhenEveryIdIsUsed()
    {
        var existingIds = Enumerable.Range(1, 255);

        Assert.Throws<InvalidOperationException>(() => ProfileIdAllocator.GetLowestAvailable(existingIds));
    }
}
