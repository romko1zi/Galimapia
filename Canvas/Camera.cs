using System;

namespace Galimapia.Canvas;

/// <summary>
/// Looks at an infinite plane. <see cref="Zoom"/> is device-independent pixels per world unit.
/// Zoom 1 means one world unit covers one DIP.
/// </summary>
public sealed class Camera
{
    public const double MinZoom = 0.002;
    public const double MaxZoom = 64;

    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Zoom { get; set; } = 1;

    public (double X, double Y) ScreenToWorld(double screenX, double screenY, double viewWidth, double viewHeight)
    {
        return (
            CenterX + (screenX - viewWidth * 0.5) / Zoom,
            CenterY + (screenY - viewHeight * 0.5) / Zoom);
    }

    public (double X, double Y) WorldToScreen(double worldX, double worldY, double viewWidth, double viewHeight)
    {
        return (
            (worldX - CenterX) * Zoom + viewWidth * 0.5,
            (worldY - CenterY) * Zoom + viewHeight * 0.5);
    }

    public void PanByScreenDelta(double deltaX, double deltaY)
    {
        CenterX -= deltaX / Zoom;
        CenterY -= deltaY / Zoom;
    }

    /// <summary>
    /// Scales <see cref="Zoom"/> by <paramref name="factor"/> while keeping the world point under
    /// the cursor fixed on screen.
    /// </summary>
    public void ZoomAt(double screenX, double screenY, double viewWidth, double viewHeight, double factor)
    {
        if (factor <= 0 || viewWidth <= 0 || viewHeight <= 0)
            return;

        var before = ScreenToWorld(screenX, screenY, viewWidth, viewHeight);
        var next = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        if (next == Zoom)
            return;

        Zoom = next;
        var after = ScreenToWorld(screenX, screenY, viewWidth, viewHeight);
        CenterX += before.X - after.X;
        CenterY += before.Y - after.Y;
    }

    /// <summary>
    /// Centers on a world rectangle and zooms so it fits in the view.
    /// </summary>
    public void Fit(double minX, double minY, double maxX, double maxY, double viewWidth, double viewHeight, double margin)
    {
        var contentWidth = Math.Max(maxX - minX, 1);
        var contentHeight = Math.Max(maxY - minY, 1);
        CenterX = (minX + maxX) * 0.5;
        CenterY = (minY + maxY) * 0.5;

        var usableWidth = Math.Max(viewWidth - margin * 2, viewWidth * 0.5);
        var usableHeight = Math.Max(viewHeight - margin * 2, viewHeight * 0.5);
        var zoom = Math.Min(usableWidth / contentWidth, usableHeight / contentHeight);
        if (double.IsNaN(zoom) || double.IsInfinity(zoom) || zoom <= 0)
            zoom = 1;

        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
    }
}
