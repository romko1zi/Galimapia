namespace Galimapia.Canvas;

/// <summary>
/// Places a drop on a roughly square grid so sheets do not form one long row.
/// </summary>
public static class SheetGrid
{
    public static int Columns(int count) =>
        count <= 1 ? 1 : (int)Math.Ceiling(Math.Sqrt(count));

    public static (double X, double Y) Cell(
        int index,
        int columns,
        double originX,
        double originY,
        double cellWidth,
        double cellHeight,
        double gap)
    {
        var column = index % columns;
        var row = index / columns;
        return (
            originX + column * (cellWidth + gap),
            originY + row * (cellHeight + gap));
    }
}
