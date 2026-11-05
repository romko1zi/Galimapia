namespace Galimapia.Canvas;

public readonly record struct CropPoint(double X, double Y);

/// <summary>
/// Even-odd test for a crop polygon in sheet pixel space.
/// </summary>
public static class CropPolygon
{
    public const double CloseSnapDips = 22;
    public const double CloseLoupeDips = 88;

    public static bool Contains(IReadOnlyList<CropPoint> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var yi = polygon[i].Y;
            var yj = polygon[j].Y;
            if ((yi > y) == (yj > y))
                continue;

            var xi = polygon[i].X;
            var xj = polygon[j].X;
            var cross = (xj - xi) * (y - yi) / (yj - yi) + xi;
            if (x < cross)
                inside = !inside;
        }

        return inside;
    }

    public static CropPoint[] Rectangle(double x0, double y0, double x1, double y1)
    {
        var left = Math.Min(x0, x1);
        var right = Math.Max(x0, x1);
        var top = Math.Min(y0, y1);
        var bottom = Math.Max(y0, y1);
        return [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];
    }

    public static bool CanAppend(IReadOnlyList<CropPoint> points, CropPoint next)
    {
        if (points.Count == 0)
            return true;

        var start = points[^1];
        for (var i = 0; i < points.Count - 2; i++)
        {
            if (SegmentsCross(points[i], points[i + 1], start, next))
                return false;
        }

        return true;
    }

    public static bool CanClose(IReadOnlyList<CropPoint> points)
    {
        if (points.Count < 3)
            return false;

        var start = points[^1];
        var end = points[0];
        for (var i = 1; i < points.Count - 2; i++)
        {
            if (SegmentsCross(points[i], points[i + 1], start, end))
                return false;
        }

        return true;
    }

    public static bool IsSimple(IReadOnlyList<CropPoint> points)
    {
        var count = points.Count;
        if (count < 4)
            return true;

        for (var i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % count];
            for (var j = i + 1; j < count; j++)
            {
                if (j == i + 1 || (i == 0 && j == count - 1))
                    continue;

                var c = points[j];
                var d = points[(j + 1) % count];
                if (SegmentsCross(a, b, c, d))
                    return false;
            }
        }

        return true;
    }

    public static bool SegmentsCross(CropPoint a, CropPoint b, CropPoint c, CropPoint d)
    {
        var d1 = Cross(c, d, a);
        var d2 = Cross(c, d, b);
        var d3 = Cross(a, b, c);
        var d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    public static bool Same(IReadOnlyList<CropPoint> left, IReadOnlyList<CropPoint> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    private static double Cross(CropPoint a, CropPoint b, CropPoint point) =>
        (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
}
