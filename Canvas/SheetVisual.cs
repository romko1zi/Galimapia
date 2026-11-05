namespace Galimapia.Canvas;

sealed class SheetVisual : IDisposable
{
    public SheetVisual(
        ImageLease? lease,
        double x,
        double y,
        double width,
        double height,
        string label,
        bool selected,
        double rotation,
        double scale,
        bool flipX,
        bool flipY,
        float opacity,
        CropPoint[] crop,
        bool cropCommitted,
        CropPoint[] draft,
        double pivotX,
        double pivotY,
        double boxLeft,
        double boxTop,
        double boxRight,
        double boxBottom,
        float brightness,
        float contrast,
        float hue,
        CropPoint[] warp)
    {
        Lease = lease;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Label = label;
        Selected = selected;
        Rotation = rotation;
        Scale = scale;
        FlipX = flipX;
        FlipY = flipY;
        Opacity = opacity;
        Crop = crop;
        CropCommitted = cropCommitted;
        Draft = draft;
        PivotX = pivotX;
        PivotY = pivotY;
        BoxLeft = boxLeft;
        BoxTop = boxTop;
        BoxRight = boxRight;
        BoxBottom = boxBottom;
        Brightness = brightness;
        Contrast = contrast;
        Hue = hue;
        Warp = warp;
    }

    public ImageLease? Lease { get; }
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public string Label { get; }
    public bool Selected { get; }
    public double Rotation { get; }
    public double Scale { get; }
    public bool FlipX { get; }
    public bool FlipY { get; }
    public float Opacity { get; }
    public CropPoint[] Crop { get; }
    public bool CropCommitted { get; }
    public CropPoint[] Draft { get; }
    public double PivotX { get; }
    public double PivotY { get; }
    public double BoxLeft { get; }
    public double BoxTop { get; }
    public double BoxRight { get; }
    public double BoxBottom { get; }
    public float Brightness { get; }
    public float Contrast { get; }
    public float Hue { get; }
    public CropPoint[] Warp { get; }

    public bool HasWarp => MapFragment.IsWarped(MapFragment.ControlsFor(CropCommitted && Crop.Length >= 4, Crop.Length), Warp);

    public SheetFrame Frame => new(
        X,
        Y,
        Width,
        Height,
        Rotation,
        Scale,
        FlipX,
        FlipY,
        PivotX,
        PivotY,
        BoxLeft,
        BoxTop,
        BoxRight,
        BoxBottom);

    public void Dispose() => Lease?.Dispose();
}
