namespace Galimapia.Canvas;

/// <summary>
/// Triangulates a simple polygon so a sheet can stretch at its corners, or at each crop point.
/// </summary>
public static class WarpMesh
{
    public const double GlueDips = 10;
    public const double HandleDips = 18;

    public static bool Triangulate(IReadOnlyList<CropPoint> polygon, List<int> indices)
    {
        indices.Clear();
        var count = polygon.Count;
        if (count < 3)
            return false;
        if (count == 3)
        {
            indices.Add(0);
            indices.Add(1);
            indices.Add(2);
            return true;
        }

        var sign = Math.Sign(SignedArea(polygon));
        if (sign == 0)
            return false;

        var remaining = new List<int>(count);
        for (var i = 0; i < count; i++)
            remaining.Add(i);

        var guard = count * count;
        while (remaining.Count > 3 && guard-- > 0)
        {
            var clipped = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var previous = remaining[(i + remaining.Count - 1) % remaining.Count];
                var current = remaining[i];
                var next = remaining[(i + 1) % remaining.Count];
                if (!IsEar(polygon, remaining, previous, current, next, sign))
                    continue;

                indices.Add(previous);
                indices.Add(current);
                indices.Add(next);
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
                return false;
        }

        if (remaining.Count != 3)
            return false;

        indices.Add(remaining[0]);
        indices.Add(remaining[1]);
        indices.Add(remaining[2]);
        return true;
    }

    /// <summary>
    /// Drops triangles that collapsed to a line or a point. A GPU mesh of those crashes the window.
    /// </summary>
    public static void KeepSolid(IReadOnlyList<CropPoint> placed, List<int> indices)
    {
        var kept = new List<int>(indices.Count);
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var a = indices[i];
            var b = indices[i + 1];
            var c = indices[i + 2];
            if ((uint)a >= (uint)placed.Count || (uint)b >= (uint)placed.Count || (uint)c >= (uint)placed.Count)
                continue;

            var pa = placed[a];
            var pb = placed[b];
            var pc = placed[c];
            if (!Finite(pa) || !Finite(pb) || !Finite(pc))
                continue;
            if (Math.Abs(Cross(pa, pb, pc)) < 0.5)
                continue;

            kept.Add(a);
            kept.Add(b);
            kept.Add(c);
        }

        indices.Clear();
        indices.AddRange(kept);
    }

    public static bool Collides(IReadOnlyList<CropPoint> placed, int index, double x, double y)
    {
        for (var i = 0; i < placed.Count; i++)
        {
            if (i == index)
                continue;

            var dx = placed[i].X - x;
            var dy = placed[i].Y - y;
            if (dx * dx + dy * dy < 2.25)
                return true;
        }

        return false;
    }

    private static bool Finite(CropPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static bool IsEar(
        IReadOnlyList<CropPoint> polygon,
        List<int> remaining,
        int previous,
        int current,
        int next,
        int sign)
    {
        var area = Cross(polygon[previous], polygon[current], polygon[next]);
        if (Math.Sign(area) != sign || Math.Abs(area) < 1e-6)
            return false;

        foreach (var index in remaining)
        {
            if (index == previous || index == current || index == next)
                continue;
            if (Contains(polygon[previous], polygon[current], polygon[next], polygon[index]))
                return false;
        }

        return true;
    }

    private static double SignedArea(IReadOnlyList<CropPoint> polygon)
    {
        double sum = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var next = polygon[(i + 1) % polygon.Count];
            sum += polygon[i].X * next.Y - next.X * polygon[i].Y;
        }

        return sum * 0.5;
    }

    private static double Cross(CropPoint a, CropPoint b, CropPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool Contains(CropPoint a, CropPoint b, CropPoint c, CropPoint point)
    {
        var ab = Cross(a, b, point);
        var bc = Cross(b, c, point);
        var ca = Cross(c, a, point);
        var hasNegative = ab < -1e-6 || bc < -1e-6 || ca < -1e-6;
        var hasPositive = ab > 1e-6 || bc > 1e-6 || ca > 1e-6;
        return !(hasNegative && hasPositive);
    }
}
