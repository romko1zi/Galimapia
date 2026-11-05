namespace Galimapia.Canvas;

public enum SheetPart
{
    Rotate,
    NorthWest,
    NorthEast,
    SouthWest,
    SouthEast,
}

/// <summary>
/// A sheet's world pose. <see cref="X"/> and <see cref="Y"/> are the top-left of the unrotated
/// full pixel box. Rotation and scale are around the pivot: the page center, or the center of
/// an applied crop. Scale 1 and rotation 0 still leave the full-page corner at (X, Y).
/// </summary>
public readonly record struct SheetFrame(
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    double Scale,
    bool FlipX = false,
    bool FlipY = false,
    double PivotX = double.NaN,
    double PivotY = double.NaN,
    double BoxLeft = double.NaN,
    double BoxTop = double.NaN,
    double BoxRight = double.NaN,
    double BoxBottom = double.NaN)
{
    public double PivotLocalX => double.IsNaN(PivotX) ? Width * 0.5 : PivotX;
    public double PivotLocalY => double.IsNaN(PivotY) ? Height * 0.5 : PivotY;
    public double EdgeLeft => double.IsNaN(BoxLeft) ? 0 : BoxLeft;
    public double EdgeTop => double.IsNaN(BoxTop) ? 0 : BoxTop;
    public double EdgeRight => double.IsNaN(BoxRight) ? Width : BoxRight;
    public double EdgeBottom => double.IsNaN(BoxBottom) ? Height : BoxBottom;

    /// <summary>World position of the rotation pivot. For a full page this is the page center.</summary>
    public (double X, double Y) Center => (X + PivotLocalX, Y + PivotLocalY);

    public (double X, double Y) ToWorld(double localX, double localY)
    {
        var sx = (localX - PivotLocalX) * (FlipX ? -Scale : Scale);
        var sy = (localY - PivotLocalY) * (FlipY ? -Scale : Scale);
        var radians = RotationDegrees * (Math.PI / 180d);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return (X + PivotLocalX + sx * cos - sy * sin, Y + PivotLocalY + sx * sin + sy * cos);
    }

    public bool TryToLocal(double worldX, double worldY, out double localX, out double localY)
    {
        localX = 0;
        localY = 0;
        if (Width <= 0 || Height <= 0 || Math.Abs(Scale) < 1e-8)
            return false;

        var dx = worldX - (X + PivotLocalX);
        var dy = worldY - (Y + PivotLocalY);
        var radians = -RotationDegrees * (Math.PI / 180d);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var sx = (dx * cos - dy * sin) / Scale;
        var sy = (dx * sin + dy * cos) / Scale;
        if (FlipX)
            sx = -sx;
        if (FlipY)
            sy = -sy;

        localX = sx + PivotLocalX;
        localY = sy + PivotLocalY;
        return true;
    }

    public bool Contains(double worldX, double worldY)
    {
        if (!TryToLocal(worldX, worldY, out var localX, out var localY))
            return false;

        return localX >= 0 && localY >= 0 && localX <= Width && localY <= Height;
    }

    public void GetBounds(out double minX, out double minY, out double maxX, out double maxY)
    {
        var a = ToWorld(0, 0);
        var b = ToWorld(Width, 0);
        var c = ToWorld(Width, Height);
        var d = ToWorld(0, Height);
        minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
        minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
        maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
    }

    public bool TryPickHandle(
        Camera camera,
        double viewWidth,
        double viewHeight,
        double screenX,
        double screenY,
        out SheetPart part)
    {
        part = default;
        var onScreen = Math.Max(EdgeRight - EdgeLeft, EdgeBottom - EdgeTop) * Math.Abs(Scale) * camera.Zoom;
        if (onScreen < 48)
            return false;

        if (Near(RotateHandle(camera, viewWidth, viewHeight), screenX, screenY))
        {
            part = SheetPart.Rotate;
            return true;
        }

        if (Near(Screen(camera, viewWidth, viewHeight, EdgeLeft, EdgeTop), screenX, screenY))
        {
            part = SheetPart.NorthWest;
            return true;
        }

        if (Near(Screen(camera, viewWidth, viewHeight, EdgeRight, EdgeTop), screenX, screenY))
        {
            part = SheetPart.NorthEast;
            return true;
        }

        if (Near(Screen(camera, viewWidth, viewHeight, EdgeRight, EdgeBottom), screenX, screenY))
        {
            part = SheetPart.SouthEast;
            return true;
        }

        if (Near(Screen(camera, viewWidth, viewHeight, EdgeLeft, EdgeBottom), screenX, screenY))
        {
            part = SheetPart.SouthWest;
            return true;
        }

        return false;
    }

    public (double X, double Y) RotateHandle(Camera camera, double viewWidth, double viewHeight)
    {
        var midX = (EdgeLeft + EdgeRight) * 0.5;
        var midY = (EdgeTop + EdgeBottom) * 0.5;
        var center = Screen(camera, viewWidth, viewHeight, midX, midY);
        var top = Screen(camera, viewWidth, viewHeight, midX, EdgeTop);
        var dx = top.X - center.X;
        var dy = top.Y - center.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
        {
            dx = 0;
            dy = -1;
            length = 1;
        }

        return (top.X + dx / length * 22, top.Y + dy / length * 22);
    }

    public (double X, double Y) Screen(Camera camera, double viewWidth, double viewHeight, double localX, double localY)
    {
        var world = ToWorld(localX, localY);
        return camera.WorldToScreen(world.X, world.Y, viewWidth, viewHeight);
    }

    private static bool Near((double X, double Y) point, double x, double y) =>
        (point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y) <= 10 * 10;
}
