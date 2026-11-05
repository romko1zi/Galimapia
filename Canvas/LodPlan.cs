namespace Galimapia.Canvas;

/// <summary>
/// One stored resolution of a scan. <see cref="IsFull"/> reads the original file.
/// Reduced levels are JPEG copies whose long side is <see cref="LongSide"/>.
/// </summary>
public sealed class LodFile
{
    public LodFile(int longSide, string path, bool isFull)
    {
        LongSide = longSide;
        Path = path;
        IsFull = isFull;
    }

    public int LongSide { get; }
    public string Path { get; }
    public bool IsFull { get; }
}

/// <summary>
/// Picks which pyramid step a sheet needs from how many screen pixels it covers.
/// </summary>
public static class LodPlan
{
    public static int[] ReducedLongSides(int sourceLong)
    {
        if (sourceLong <= 1)
            return [];

        var sides = new List<int>(4);
        foreach (var side in new[] { 512, 1024, 2048 })
        {
            if (side < sourceLong)
                sides.Add(side);
        }

        var half = sourceLong / 2;
        if (half > 2048 && half < sourceLong)
            sides.Add(half);

        return sides.ToArray();
    }

    /// <summary>
    /// <paramref name="levels"/> is sorted by long side, ascending, and includes the original.
    /// Keeps <paramref name="heldLongSide"/> until the sheet grows past it or shrinks below 70% of it.
    /// </summary>
    public static LodFile Choose(IReadOnlyList<LodFile> levels, int needed, int? heldLongSide)
    {
        if (levels.Count == 0)
            throw new ArgumentException("A sheet needs at least the original image.", nameof(levels));

        var wanted = Math.Max(needed, 1);
        if (heldLongSide is int held)
        {
            LodFile? current = null;
            foreach (var level in levels)
            {
                if (level.LongSide == held)
                {
                    current = level;
                    break;
                }
            }

            if (current is not null && wanted <= current.LongSide && wanted >= current.LongSide * 0.7)
                return current;
        }

        foreach (var level in levels)
        {
            if (level.LongSide >= wanted)
                return level;
        }

        return levels[^1];
    }
}
