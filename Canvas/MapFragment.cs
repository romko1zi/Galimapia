namespace Galimapia.Canvas;

public sealed class MapFragment
{
    public MapFragment(
        string sourcePath,
        double x,
        double y,
        int pixelWidth,
        int pixelHeight,
        string? id = null,
        string? name = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("n") : id;
        SourcePath = sourcePath;
        Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(sourcePath) : name;
        X = x;
        Y = y;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Scale = 1;
        Opacity = 1;
        Brightness = 0;
        Contrast = 1;
        Hue = 0;
        Levels = [];
        Crop = [];
        Draft = [];
        Warp = [];
    }

    public string Id { get; }
    public string SourcePath { get; private set; }
    public string Name { get; }

    public void Retarget(string sourcePath) => SourcePath = sourcePath;

    /// <summary>Top-left of the unrotated full pixel box. An applied crop rotates around its own center.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double RotationDegrees { get; set; }
    public double Scale { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    public float Opacity { get; set; }
    public float Brightness { get; set; }
    public float Contrast { get; set; }
    public float Hue { get; set; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int LongSide => Math.Max(PixelWidth, PixelHeight);
    public List<CropPoint> Crop { get; }
    public List<CropPoint> Draft { get; }
    public List<CropPoint> Warp { get; }
    public bool CropIsRectangle { get; set; }
    public bool DraftIsRectangle { get; set; }

    /// <summary>The crop cuts the page only after the user confirms it. The source file stays whole.</summary>
    public bool CropCommitted { get; set; }

    public bool HasCut => CropCommitted && Crop.Count >= 4;

    public int ControlCount => HasCut ? Crop.Count : 4;

    public bool HasWarp => IsWarped(ControlCount, Warp);

    public static int ControlsFor(bool hasCut, int cropCount) => hasCut ? cropCount : 4;

    public static bool IsWarped(int controlCount, IReadOnlyList<CropPoint> warp)
    {
        if (warp.Count != controlCount)
            return false;

        foreach (var point in warp)
        {
            if (point.X * point.X + point.Y * point.Y > 0.01)
                return true;
        }

        return false;
    }

    public CropPoint ControlPoint(int index)
    {
        if (HasCut)
            return Crop[index];

        return index switch
        {
            0 => new CropPoint(0, 0),
            1 => new CropPoint(PixelWidth, 0),
            2 => new CropPoint(PixelWidth, PixelHeight),
            _ => new CropPoint(0, PixelHeight),
        };
    }

    public CropPoint WarpOffset(int index) =>
        index >= 0 && index < Warp.Count ? Warp[index] : default;

    public CropPoint WarpedLocal(int index)
    {
        var control = ControlPoint(index);
        var offset = WarpOffset(index);
        return new CropPoint(control.X + offset.X, control.Y + offset.Y);
    }

    public void EnsureWarp()
    {
        var count = ControlCount;
        if (Warp.Count == count)
            return;

        Warp.Clear();
        for (var i = 0; i < count; i++)
            Warp.Add(default);
    }

    public void SetWarp(IReadOnlyList<CropPoint> offsets)
    {
        Warp.Clear();
        Warp.AddRange(offsets);
    }

    public SheetFrame Frame
    {
        get
        {
            if (!HasCut || !TryCropBox(Crop, out var left, out var top, out var right, out var bottom))
                return new SheetFrame(X, Y, PixelWidth, PixelHeight, RotationDegrees, Scale, FlipX, FlipY);

            return new SheetFrame(
                X,
                Y,
                PixelWidth,
                PixelHeight,
                RotationDegrees,
                Scale,
                FlipX,
                FlipY,
                (left + right) * 0.5,
                (top + bottom) * 0.5,
                left,
                top,
                right,
                bottom);
        }
    }

    public static bool TryCropBox(
        IReadOnlyList<CropPoint> points,
        out double left,
        out double top,
        out double right,
        out double bottom)
    {
        left = top = right = bottom = 0;
        if (points.Count == 0)
            return false;

        left = right = points[0].X;
        top = bottom = points[0].Y;
        for (var i = 1; i < points.Count; i++)
        {
            left = Math.Min(left, points[i].X);
            top = Math.Min(top, points[i].Y);
            right = Math.Max(right, points[i].X);
            bottom = Math.Max(bottom, points[i].Y);
        }

        return right - left >= 1 && bottom - top >= 1;
    }

    /// <summary>Smallest pyramid file, used for the layer card. Skips a huge original.</summary>
    public string? PreviewPath
    {
        get
        {
            if (!PyramidReady)
                return null;

            LodFile? smallest = null;
            foreach (var level in Levels)
            {
                if (level.IsFull && LongSide > 512)
                    continue;
                if (smallest is null || level.LongSide < smallest.LongSide)
                    smallest = level;
            }

            return smallest?.Path;
        }
    }

    public bool ContainsFrame(double worldX, double worldY) => Frame.Contains(worldX, worldY);

    public bool ContainsWorld(double worldX, double worldY)
    {
        if (HasWarp)
            return ContainsWarped(worldX, worldY);
        if (!Frame.TryToLocal(worldX, worldY, out var localX, out var localY))
            return false;
        if (localX < 0 || localY < 0 || localX > PixelWidth || localY > PixelHeight)
            return false;
        if (HasCut)
            return CropPolygon.Contains(Crop, localX, localY);

        return true;
    }

    public void GetVisibleBounds(out double minX, out double minY, out double maxX, out double maxY)
    {
        if (HasWarp)
        {
            var start = Frame.ToWorld(WarpedLocal(0).X, WarpedLocal(0).Y);
            minX = maxX = start.X;
            minY = maxY = start.Y;
            for (var i = 1; i < ControlCount; i++)
            {
                var point = Frame.ToWorld(WarpedLocal(i).X, WarpedLocal(i).Y);
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            return;
        }

        if (!HasCut)
        {
            Frame.GetBounds(out minX, out minY, out maxX, out maxY);
            return;
        }

        var first = Frame.ToWorld(Crop[0].X, Crop[0].Y);
        minX = maxX = first.X;
        minY = maxY = first.Y;
        for (var i = 1; i < Crop.Count; i++)
        {
            var point = Frame.ToWorld(Crop[i].X, Crop[i].Y);
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
    }

    private bool ContainsWarped(double worldX, double worldY)
    {
        var polygon = new CropPoint[ControlCount];
        for (var i = 0; i < polygon.Length; i++)
        {
            var world = Frame.ToWorld(WarpedLocal(i).X, WarpedLocal(i).Y);
            polygon[i] = new CropPoint(world.X, world.Y);
        }

        return CropPolygon.Contains(polygon, worldX, worldY);
    }
    public IReadOnlyList<LodFile> Levels { get; private set; }
    public int? HeldLongSide { get; set; }
    public bool PyramidReady { get; set; }
    public bool Failed { get; set; }

    public void SetLevels(IReadOnlyList<LodFile> levels) => Levels = levels;

    public string CacheKey(int longSide) => Id + ":" + longSide;
}
