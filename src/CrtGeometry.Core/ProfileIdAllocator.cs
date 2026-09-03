namespace CrtGeometry.Core;

public static class ProfileIdAllocator
{
    public static int GetLowestAvailable(IEnumerable<int> existingIds)
    {
        var usedIds = existingIds.ToHashSet();
        for (var candidate = 1; candidate <= byte.MaxValue; candidate++)
        {
            if (!usedIds.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No profile IDs remain (the maximum is 255).");
    }
}
