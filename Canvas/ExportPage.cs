namespace Galimapia.Canvas;

/// <summary>
/// Square world page behind every sheet. It is the future export boundary, not a bitmap.
/// </summary>
public readonly record struct ExportPage(double X, double Y, double Size)
{
    public static bool TryFrom(IReadOnlyList<MapFragment> sheets, out ExportPage page)
    {
        page = default;
        if (sheets.Count == 0)
            return false;

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var sheet in sheets)
        {
            sheet.GetVisibleBounds(out var left, out var top, out var right, out var bottom);
            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, right);
            maxY = Math.Max(maxY, bottom);
        }

        var width = maxX - minX;
        var height = maxY - minY;
        var side = Math.Max(width, height);
        if (side < 1 || double.IsInfinity(side))
            return false;

        var centerX = (minX + maxX) * 0.5;
        var centerY = (minY + maxY) * 0.5;
        page = new ExportPage(centerX - side * 0.5, centerY - side * 0.5, side);
        return true;
    }

    public bool Contains(double x, double y) =>
        x >= X - 1e-6 && y >= Y - 1e-6 && x <= X + Size + 1e-6 && y <= Y + Size + 1e-6;
}
