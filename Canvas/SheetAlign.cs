namespace Galimapia.Canvas;

/// <summary>
/// Places a sheet so two of its pixel points land on two world points.
/// The result is one translation, one rotation, and one scale.
/// </summary>
public static class SheetAlign
{
    public static bool TryPlace(
        SheetFrame frame,
        double local1X,
        double local1Y,
        double local2X,
        double local2Y,
        double world1X,
        double world1Y,
        double world2X,
        double world2Y,
        double minScale,
        double maxScale,
        out double x,
        out double y,
        out double rotationDegrees,
        out double scale)
    {
        x = frame.X;
        y = frame.Y;
        rotationDegrees = frame.RotationDegrees;
        scale = frame.Scale;

        var originX = frame.PivotLocalX;
        var originY = frame.PivotLocalY;
        var origin1 = Offset(frame, local1X, local1Y, originX, originY);
        var origin2 = Offset(frame, local2X, local2Y, originX, originY);
        var localX = origin2.X - origin1.X;
        var localY = origin2.Y - origin1.Y;
        var localLength = Math.Sqrt(localX * localX + localY * localY);
        var worldX = world2X - world1X;
        var worldY = world2Y - world1Y;
        var worldLength = Math.Sqrt(worldX * worldX + worldY * worldY);
        if (localLength < 1 || worldLength < 1)
            return false;

        scale = Math.Clamp(worldLength / localLength, minScale, maxScale);
        rotationDegrees = (Math.Atan2(worldY, worldX) - Math.Atan2(localY, localX)) * (180d / Math.PI);
        var radians = rotationDegrees * (Math.PI / 180d);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var rotatedX = scale * (origin1.X * cos - origin1.Y * sin);
        var rotatedY = scale * (origin1.X * sin + origin1.Y * cos);
        var centerX = world1X - rotatedX;
        var centerY = world1Y - rotatedY;
        x = centerX - originX;
        y = centerY - originY;
        return true;
    }

    private static (double X, double Y) Offset(SheetFrame frame, double localX, double localY, double originX, double originY)
    {
        var x = (localX - originX) * (frame.FlipX ? -1 : 1);
        var y = (localY - originY) * (frame.FlipY ? -1 : 1);
        return (x, y);
    }
}
