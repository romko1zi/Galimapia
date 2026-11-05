using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Galimapia.Canvas;

/// <summary>
/// Draws the camera grid and the visible sheets in control-local DIPs.
/// The leased <see cref="SKCanvas"/> already includes Avalonia's DPI matrix. Save and restore it; do not replace it.
/// </summary>
sealed class MapDrawOperation : ICustomDrawOperation
{
    private static readonly SKSamplingOptions ImageSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly double _centerX;
    private readonly double _centerY;
    private readonly double _zoom;
    private readonly SheetVisual[] _sheets;
    private readonly int _fragmentCount;
    private readonly bool _acceptingDrop;
    private readonly bool _cropping;
    private readonly bool _warping;
    private readonly bool _glue;
    private readonly double _glueX;
    private readonly double _glueY;
    private readonly bool _rubber;
    private readonly double _rubberX;
    private readonly double _rubberY;
    private readonly (double X, double Y)[] _marks;
    private readonly bool _hasPage;
    private readonly double _pageX;
    private readonly double _pageY;
    private readonly double _pageSize;
    private readonly int _version;

    public MapDrawOperation(
        Rect bounds,
        double centerX,
        double centerY,
        double zoom,
        IReadOnlyList<SheetVisual> sheets,
        int fragmentCount,
        bool acceptingDrop,
        bool cropping,
        bool warping,
        bool glue,
        double glueX,
        double glueY,
        bool rubber,
        double rubberX,
        double rubberY,
        IReadOnlyList<(double X, double Y)> marks,
        bool hasPage,
        double pageX,
        double pageY,
        double pageSize,
        int version)
    {
        Bounds = bounds;
        _centerX = centerX;
        _centerY = centerY;
        _zoom = zoom;
        _sheets = sheets as SheetVisual[] ?? sheets.ToArray();
        _fragmentCount = fragmentCount;
        _acceptingDrop = acceptingDrop;
        _cropping = cropping;
        _warping = warping;
        _glue = glue;
        _glueX = glueX;
        _glueY = glueY;
        _rubber = rubber;
        _rubberX = rubberX;
        _rubberY = rubberY;
        _marks = marks as (double X, double Y)[] ?? marks.ToArray();
        _hasPage = hasPage;
        _pageX = pageX;
        _pageY = pageY;
        _pageSize = pageSize;
        _version = version;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => Bounds.Contains(p);

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null || Bounds.Width <= 1 || Bounds.Height <= 1 || _zoom <= 0)
            return;

        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Save();
        try
        {
            Draw(canvas, (float)Bounds.Width, (float)Bounds.Height);
        }
        finally
        {
            canvas.Restore();
        }
    }

    public bool Equals(ICustomDrawOperation? other) =>
        other is MapDrawOperation operation
        && operation.Bounds == Bounds
        && operation._centerX == _centerX
        && operation._centerY == _centerY
        && operation._zoom == _zoom
        && operation._version == _version
        && operation._fragmentCount == _fragmentCount
        && operation._acceptingDrop == _acceptingDrop
        && operation._cropping == _cropping
        && operation._warping == _warping
        && operation._glue == _glue
        && operation._glueX == _glueX
        && operation._glueY == _glueY
        && operation._rubber == _rubber
        && operation._rubberX == _rubberX
        && operation._rubberY == _rubberY
        && MarksEqual(operation._marks, _marks)
        && operation._hasPage == _hasPage
        && operation._pageX == _pageX
        && operation._pageY == _pageY
        && operation._pageSize == _pageSize;

    public override bool Equals(object? obj) => obj is MapDrawOperation other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(Bounds, _centerX, _centerY, _zoom, _version, _fragmentCount, HashCode.Combine(_acceptingDrop, _cropping, _rubber, _hasPage, _pageX, _pageY, _pageSize, _marks.Length)),
            HashCode.Combine(_warping, _glue, _glueX, _glueY));

    public void Dispose()
    {
        foreach (var sheet in _sheets)
            sheet.Dispose();
    }

    private void Draw(SKCanvas canvas, float width, float height)
    {
        using var background = new SKPaint { Color = new SKColor(0x24, 0x22, 0x20) };
        canvas.DrawRect(0, 0, width, height, background);
        DrawGrid(canvas, width, height);
        DrawExport(canvas, width, height);
        DrawSheets(canvas, width, height);
        DrawCloseLoupe(canvas, width, height);

        if (_fragmentCount == 0)
            DrawMessage(canvas, width, height, "Drop scans here");

        if (_acceptingDrop)
        {
            using var hover = LinePaint(new SKColor(0xD7, 0xB9, 0x8A));
            hover.StrokeWidth = 2;
            canvas.DrawRect(8, 8, width - 16, height - 16, hover);
        }
    }

    private void DrawExport(SKCanvas canvas, float width, float height)
    {
        if (!_hasPage || _pageSize <= 0)
            return;

        var left = ScreenX(_pageX, width);
        var top = ScreenY(_pageY, height);
        var right = ScreenX(_pageX + _pageSize, width);
        var bottom = ScreenY(_pageY + _pageSize, height);
        using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        canvas.DrawRect(left, top, right - left, bottom - top, fill);
        using var edge = LinePaint(new SKColor(0xC4, 0xBE, 0xB4));
        edge.StrokeWidth = 1.5f;
        edge.IsAntialias = true;
        canvas.DrawRect(left, top, right - left, bottom - top, edge);
    }

    private void DrawSheets(SKCanvas canvas, float width, float height)
    {
        using var placeholder = new SKPaint { Color = new SKColor(0x3C, 0x34, 0x2C) };
        using var border = LinePaint(new SKColor(0x8A, 0x73, 0x55));
        border.IsAntialias = true;
        using var labelPaint = new SKPaint { Color = new SKColor(0xE6, 0xD7, 0xC3), IsAntialias = true };

        foreach (var sheet in _sheets)
            DrawSheet(canvas, width, height, sheet, placeholder, border, labelPaint);

        DrawHandles(canvas, width, height);
    }

    private void DrawSheet(
        SKCanvas canvas,
        float width,
        float height,
        SheetVisual sheet,
        SKPaint placeholder,
        SKPaint border,
        SKPaint labelPaint)
    {
        var pixelScale = (float)(_zoom * sheet.Scale);
        if (Math.Abs(pixelScale) < 1e-4f)
            return;

        var frame = sheet.Frame;
        var center = frame.Center;
        canvas.Save();
        canvas.Translate(ScreenX(center.X, width), ScreenY(center.Y, height));
        canvas.RotateDegrees((float)sheet.Rotation);
        canvas.Scale(pixelScale * (sheet.FlipX ? -1f : 1f), pixelScale * (sheet.FlipY ? -1f : 1f));

        var originX = (float)frame.PivotLocalX;
        var originY = (float)frame.PivotLocalY;
        var dest = new SKRect(-originX, -originY, (float)sheet.Width - originX, (float)sheet.Height - originY);
        var alpha = (byte)Math.Clamp((int)Math.Round(sheet.Opacity * 255d), 0, 255);
        var editingNewOutline = sheet.Draft.Length > 0
            && !(sheet.CropCommitted && CropPolygon.Same(sheet.Draft, sheet.Crop));
        var hideCut = sheet.Selected && _cropping && editingNewOutline;
        using var color = ColorAdjust.CreateFilter(sheet.Brightness, sheet.Contrast, sheet.Hue);
        if (!hideCut && sheet.HasWarp && DrawWarped(canvas, sheet, originX, originY, alpha, color, border))
        {
            canvas.Restore();
            return;
        }

        using var clip = sheet.CropCommitted && sheet.Crop.Length >= 4 && !hideCut ? CropPath(sheet, originX, originY) : null;
        if (clip is not null)
        {
            canvas.Save();
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);
        }

        if (sheet.Lease is { } lease)
        {
            using var imagePaint = new SKPaint { Color = new SKColor(255, 255, 255, alpha), ColorFilter = color };
            canvas.DrawImage(lease.Image, dest, ImageSampling, imagePaint);
        }
        else
        {
            placeholder.Color = new SKColor(0x3C, 0x34, 0x2C, alpha);
            canvas.DrawRect(dest, placeholder);
        }

        if (clip is not null)
            canvas.Restore();

        border.Color = sheet.Selected ? new SKColor(0xD7, 0xB9, 0x8A, alpha) : new SKColor(0x8A, 0x73, 0x55, alpha);
        border.StrokeWidth = 1.5f / Math.Abs(pixelScale);
        border.IsAntialias = true;
        if (clip is not null)
            canvas.DrawPath(clip, border);
        else
            canvas.DrawRect(dest, border);

        var screenHeight = sheet.Height * Math.Abs(pixelScale);
        var screenWidth = sheet.Width * Math.Abs(pixelScale);
        if (sheet.Lease is null && screenHeight >= 22 && screenWidth >= 40)
        {
            canvas.Save();
            canvas.ClipRect(dest);
            var textSize = 14f / Math.Abs(pixelScale);
            using var font = new SKFont(SKTypeface.Default, textSize);
            labelPaint.Color = new SKColor(0xE6, 0xD7, 0xC3, alpha);
            canvas.DrawText(sheet.Label, dest.Left + 8f / Math.Abs(pixelScale), dest.Top + 20f / Math.Abs(pixelScale), font, labelPaint);
            canvas.Restore();
        }

        canvas.Restore();
    }

    private bool DrawWarped(
        SKCanvas canvas,
        SheetVisual sheet,
        float originX,
        float originY,
        byte alpha,
        SKColorFilter? color,
        SKPaint border)
    {
        var controls = ControlsOf(sheet);
        var placed = new CropPoint[controls.Length];
        for (var i = 0; i < controls.Length; i++)
        {
            var offset = OffsetOf(sheet, i);
            placed[i] = new CropPoint(controls[i].X + offset.X, controls[i].Y + offset.Y);
        }

        var indices = new List<int>();
        if (!WarpMesh.Triangulate(controls, indices))
            return false;

        WarpMesh.KeepSolid(placed, indices);
        var image = sheet.Lease?.Image;
        if (image is not null && indices.Count >= 3 && sheet.Width > 0 && sheet.Height > 0)
        {
            var vertices = new SKPoint[indices.Count];
            var texture = new SKPoint[indices.Count];
            for (var i = 0; i < indices.Count; i++)
            {
                var index = indices[i];
                var control = controls[index];
                vertices[i] = new SKPoint((float)(placed[index].X - originX), (float)(placed[index].Y - originY));
                texture[i] = new SKPoint(
                    (float)(control.X / sheet.Width * image.Width),
                    (float)(control.Y / sheet.Height * image.Height));
            }

            using var shader = SKShader.CreateImage(image, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, ImageSampling);
            using var paint = new SKPaint
            {
                Shader = shader,
                Color = new SKColor(255, 255, 255, alpha),
                ColorFilter = color,
                IsAntialias = true,
            };
            try
            {
                canvas.DrawVertices(SKVertexMode.Triangles, vertices, texture, null, paint);
            }
            catch (Exception)
            {
                // A bad mesh must not take the process down. The outline is still drawn below.
            }
        }
        else if (image is null)
        {
            using var fill = new SKPaint { Color = new SKColor(0x3C, 0x34, 0x2C, alpha), IsAntialias = true };
            using var missing = WarpedPath(sheet, controls, originX, originY);
            canvas.DrawPath(missing, fill);
        }

        border.Color = sheet.Selected ? new SKColor(0xD7, 0xB9, 0x8A, alpha) : new SKColor(0x8A, 0x73, 0x55, alpha);
        border.StrokeWidth = 1.5f / Math.Max((float)Math.Abs(_zoom * sheet.Scale), 1e-4f);
        border.IsAntialias = true;
        using var outline = WarpedPath(sheet, controls, originX, originY);
        canvas.DrawPath(outline, border);
        return true;
    }

    private void DrawWarp(SKCanvas canvas, Camera camera, float width, float height, SheetVisual sheet, SheetFrame frame)
    {
        var controls = ControlsOf(sheet);
        if (controls.Length == 0)
            return;

        using var line = LinePaint(new SKColor(80, 190, 255));
        line.StrokeWidth = 1.5f;
        line.IsAntialias = true;
        using var fill = new SKPaint { Color = new SKColor(12, 24, 32), Style = SKPaintStyle.Fill, IsAntialias = true };
        var screens = new SKPoint[controls.Length];
        for (var i = 0; i < controls.Length; i++)
        {
            var offset = OffsetOf(sheet, i);
            var screen = frame.Screen(camera, width, height, controls[i].X + offset.X, controls[i].Y + offset.Y);
            screens[i] = new SKPoint((float)screen.X, (float)screen.Y);
        }

        for (var i = 0; i < screens.Length; i++)
        {
            var next = screens[(i + 1) % screens.Length];
            canvas.DrawLine(screens[i], next, line);
        }

        foreach (var screen in screens)
        {
            canvas.DrawCircle(screen, 7, line);
            canvas.DrawCircle(screen, 4, fill);
        }

        if (!_glue)
            return;

        var glue = camera.WorldToScreen(_glueX, _glueY, width, height);
        using var glueLine = LinePaint(new SKColor(0x8F, 0xDB, 0x6A));
        glueLine.StrokeWidth = 2f;
        glueLine.IsAntialias = true;
        canvas.DrawCircle((float)glue.X, (float)glue.Y, 11, glueLine);
    }

    private static CropPoint[] ControlsOf(SheetVisual sheet)
    {
        var hasCut = sheet.CropCommitted && sheet.Crop.Length >= 4;
        var count = MapFragment.ControlsFor(hasCut, sheet.Crop.Length);
        var points = new CropPoint[count];
        if (hasCut)
        {
            for (var i = 0; i < count; i++)
                points[i] = sheet.Crop[i];
            return points;
        }

        points[0] = new CropPoint(0, 0);
        points[1] = new CropPoint(sheet.Width, 0);
        points[2] = new CropPoint(sheet.Width, sheet.Height);
        points[3] = new CropPoint(0, sheet.Height);
        return points;
    }

    private static CropPoint OffsetOf(SheetVisual sheet, int index) =>
        index >= 0 && index < sheet.Warp.Length ? sheet.Warp[index] : default;

    private static SKPath WarpedPath(SheetVisual sheet, CropPoint[] controls, float originX, float originY)
    {
        var path = new SKPath();
        var first = OffsetOf(sheet, 0);
        path.MoveTo((float)(controls[0].X + first.X - originX), (float)(controls[0].Y + first.Y - originY));
        for (var i = 1; i < controls.Length; i++)
        {
            var offset = OffsetOf(sheet, i);
            path.LineTo((float)(controls[i].X + offset.X - originX), (float)(controls[i].Y + offset.Y - originY));
        }

        path.Close();
        return path;
    }

    private void DrawHandles(SKCanvas canvas, float width, float height)
    {
        var camera = new Camera { CenterX = _centerX, CenterY = _centerY, Zoom = _zoom };
        using var stroke = LinePaint(new SKColor(0xD7, 0xB9, 0x8A));
        stroke.StrokeWidth = 1.5f;
        stroke.IsAntialias = true;
        using var fill = new SKPaint { Color = new SKColor(0x1C, 0x1B, 0x19), Style = SKPaintStyle.Fill, IsAntialias = true };

        foreach (var sheet in _sheets)
        {
            if (!sheet.Selected)
                continue;

            var frame = sheet.Frame;
            if (_cropping)
            {
                DrawCrop(canvas, camera, width, height, sheet, frame);
                continue;
            }

            if (_warping)
            {
                DrawWarp(canvas, camera, width, height, sheet, frame);
                continue;
            }

            var onScreen = Math.Max(frame.EdgeRight - frame.EdgeLeft, frame.EdgeBottom - frame.EdgeTop) * Math.Abs(frame.Scale) * _zoom;
            if (onScreen < 48)
                continue;

            var midX = (frame.EdgeLeft + frame.EdgeRight) * 0.5;
            var top = frame.Screen(camera, width, height, midX, frame.EdgeTop);
            var rotate = frame.RotateHandle(camera, width, height);
            canvas.DrawLine((float)top.X, (float)top.Y, (float)rotate.X, (float)rotate.Y, stroke);
            DrawHandle(canvas, rotate, fill, stroke);
            DrawHandle(canvas, frame.Screen(camera, width, height, frame.EdgeLeft, frame.EdgeTop), fill, stroke);
            DrawHandle(canvas, frame.Screen(camera, width, height, frame.EdgeRight, frame.EdgeTop), fill, stroke);
            DrawHandle(canvas, frame.Screen(camera, width, height, frame.EdgeRight, frame.EdgeBottom), fill, stroke);
            DrawHandle(canvas, frame.Screen(camera, width, height, frame.EdgeLeft, frame.EdgeBottom), fill, stroke);
        }

        DrawGuides(canvas, camera, width, height);
    }

    private void DrawCrop(SKCanvas canvas, Camera camera, float width, float height, SheetVisual sheet, SheetFrame frame)
    {
        var crop = sheet.Draft;
        if (crop.Length == 0)
            return;

        using var line = LinePaint(new SKColor(255, 36, 48));
        line.StrokeWidth = 2.5f;
        line.IsAntialias = true;
        using var fill = new SKPaint { Color = new SKColor(255, 36, 48), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var startFill = new SKPaint { Color = new SKColor(255, 248, 230), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var ready = LinePaint(new SKColor(0x8F, 0xDB, 0x6A));
        ready.StrokeWidth = 2.5f;
        ready.IsAntialias = true;
        using var readyFill = new SKPaint { Color = new SKColor(0x8F, 0xDB, 0x6A), Style = SKPaintStyle.Fill, IsAntialias = true };

        var open = _rubber && _cropping;
        var snapped = TryCloseApproach(sheet, frame, camera, width, height, out var startX, out var startY, out var distance);

        for (var i = 1; i < crop.Length; i++)
        {
            var from = frame.Screen(camera, width, height, crop[i - 1].X, crop[i - 1].Y);
            var to = frame.Screen(camera, width, height, crop[i].X, crop[i].Y);
            canvas.DrawLine((float)from.X, (float)from.Y, (float)to.X, (float)to.Y, line);
        }

        if (open)
        {
            var last = frame.Screen(camera, width, height, crop[^1].X, crop[^1].Y);
            if (snapped)
                canvas.DrawLine((float)last.X, (float)last.Y, (float)startX, (float)startY, ready);
            else
                canvas.DrawLine((float)last.X, (float)last.Y, (float)_rubberX, (float)_rubberY, line);
        }
        else if (crop.Length >= 4)
        {
            var first = frame.Screen(camera, width, height, crop[0].X, crop[0].Y);
            var last = frame.Screen(camera, width, height, crop[^1].X, crop[^1].Y);
            canvas.DrawLine((float)last.X, (float)last.Y, (float)first.X, (float)first.Y, line);
        }

        for (var i = 0; i < crop.Length; i++)
        {
            var screen = frame.Screen(camera, width, height, crop[i].X, crop[i].Y);
            if (i == 0)
            {
                var grow = 1f;
                if (open && distance < CropPolygon.CloseLoupeDips)
                    grow = 1f + (float)(1d - distance / CropPolygon.CloseLoupeDips) * 1.8f;
                var ring = snapped ? ready : line;
                var core = snapped ? readyFill : startFill;
                canvas.DrawCircle((float)screen.X, (float)screen.Y, 9f * grow, ring);
                canvas.DrawCircle((float)screen.X, (float)screen.Y, 5f * grow, core);
                if (snapped)
                    DrawCheck(canvas, (float)screen.X + 16f * grow, (float)screen.Y - 14f * grow, 11f);
            }
            else
            {
                DrawHandle(canvas, screen, fill, line);
            }
        }
    }

    private bool TryCloseApproach(
        SheetVisual sheet,
        SheetFrame frame,
        Camera camera,
        float width,
        float height,
        out double startX,
        out double startY,
        out double distance)
    {
        startX = 0;
        startY = 0;
        distance = double.MaxValue;
        if (!_rubber || !_cropping || sheet.Draft.Length < 3)
            return false;

        var start = frame.Screen(camera, width, height, sheet.Draft[0].X, sheet.Draft[0].Y);
        startX = start.X;
        startY = start.Y;
        var dx = _rubberX - startX;
        var dy = _rubberY - startY;
        distance = Math.Sqrt(dx * dx + dy * dy);
        return distance <= CropPolygon.CloseSnapDips
            && sheet.Draft.Length >= 4
            && CropPolygon.CanClose(sheet.Draft);
    }

    private void DrawCloseLoupe(SKCanvas canvas, float width, float height)
    {
        if (!_rubber || !_cropping)
            return;

        SheetVisual? sheet = null;
        foreach (var candidate in _sheets)
        {
            if (candidate.Selected && candidate.Draft.Length >= 3)
                sheet = candidate;
        }

        if (sheet is null)
            return;

        var camera = new Camera { CenterX = _centerX, CenterY = _centerY, Zoom = _zoom };
        var frame = sheet.Frame;
        var start = frame.Screen(camera, width, height, sheet.Draft[0].X, sheet.Draft[0].Y);
        var dx = _rubberX - start.X;
        var dy = _rubberY - start.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > CropPolygon.CloseLoupeDips)
            return;

        var snapped = distance <= CropPolygon.CloseSnapDips
            && sheet.Draft.Length >= 4
            && CropPolygon.CanClose(sheet.Draft);
        var near = 1d - distance / CropPolygon.CloseLoupeDips;
        var mag = (float)(2.2 + near * 2.6);
        const float radius = 72f;
        var lx = (float)_rubberX;
        var ly = (float)_rubberY - radius - 28f;
        if (ly < radius + 8f)
            ly = (float)_rubberY + radius + 28f;
        lx = Math.Clamp(lx, radius + 8f, Math.Max(radius + 8f, width - radius - 8f));
        ly = Math.Clamp(ly, radius + 8f, Math.Max(radius + 8f, height - radius - 8f));

        using var clip = new SKPath();
        clip.AddCircle(lx, ly, radius);
        canvas.Save();
        canvas.Save();
        canvas.ClipPath(clip, SKClipOperation.Intersect, true);
        using (var backdrop = new SKPaint { Color = new SKColor(0x24, 0x22, 0x20) })
            canvas.DrawCircle(lx, ly, radius, backdrop);

        canvas.Save();
        canvas.Translate(lx, ly);
        canvas.Scale(mag);
        canvas.Translate(-(float)start.X, -(float)start.Y);
        using (var placeholder = new SKPaint { Color = new SKColor(0x3C, 0x34, 0x2C) })
        using (var border = LinePaint(new SKColor(0x8A, 0x73, 0x55)))
        using (var labelPaint = new SKPaint { Color = new SKColor(0xE6, 0xD7, 0xC3), IsAntialias = true })
        {
            border.IsAntialias = true;
            DrawSheet(canvas, width, height, sheet, placeholder, border, labelPaint);
        }

        using (var ring = LinePaint(snapped ? new SKColor(0x8F, 0xDB, 0x6A) : new SKColor(255, 36, 48)))
        using (var core = new SKPaint
        {
            Color = snapped ? new SKColor(0x8F, 0xDB, 0x6A) : new SKColor(255, 248, 230),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        })
        {
            ring.StrokeWidth = 2.5f;
            ring.IsAntialias = true;
            canvas.DrawCircle((float)start.X, (float)start.Y, 9, ring);
            canvas.DrawCircle((float)start.X, (float)start.Y, 5, core);
        }

        canvas.Restore();
        canvas.Restore();

        using var edge = LinePaint(snapped ? new SKColor(0x8F, 0xDB, 0x6A) : new SKColor(0xE6, 0xD7, 0xC3));
        edge.StrokeWidth = snapped ? 3.5f : 2f;
        edge.IsAntialias = true;
        canvas.DrawCircle(lx, ly, radius - 1.5f, edge);
        if (snapped)
            DrawCheck(canvas, lx + radius * 0.62f, ly + radius * 0.62f, 14f);
        canvas.Restore();
    }

    private static void DrawCheck(SKCanvas canvas, float x, float y, float size)
    {
        using var badge = new SKPaint { Color = new SKColor(0x8F, 0xDB, 0x6A), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(x, y, size, badge);
        using var mark = LinePaint(new SKColor(0x1C, 0x1B, 0x19));
        mark.StrokeWidth = Math.Max(2f, size * 0.18f);
        mark.StrokeCap = SKStrokeCap.Round;
        mark.IsAntialias = true;
        canvas.DrawLine(x - size * 0.45f, y, x - size * 0.12f, y + size * 0.38f, mark);
        canvas.DrawLine(x - size * 0.12f, y + size * 0.38f, x + size * 0.48f, y - size * 0.42f, mark);
    }

    private void DrawGuides(SKCanvas canvas, Camera camera, float width, float height)
    {
        if (_marks.Length == 0)
            return;

        using var line = LinePaint(new SKColor(80, 190, 255));
        line.StrokeWidth = 2;
        line.IsAntialias = true;
        using var fill = new SKPaint { Color = new SKColor(12, 18, 24), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var text = new SKPaint { Color = new SKColor(80, 190, 255), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 13);

        for (var i = 0; i < _marks.Length; i++)
        {
            var screen = camera.WorldToScreen(_marks[i].X, _marks[i].Y, width, height);
            if (i > 0 && i != 2)
            {
                var previous = camera.WorldToScreen(_marks[i - 1].X, _marks[i - 1].Y, width, height);
                canvas.DrawLine((float)previous.X, (float)previous.Y, (float)screen.X, (float)screen.Y, line);
            }

            canvas.DrawCircle((float)screen.X, (float)screen.Y, 10, line);
            canvas.DrawCircle((float)screen.X, (float)screen.Y, 8, fill);
            canvas.DrawText((i + 1).ToString(), (float)screen.X - 4, (float)screen.Y + 4, font, text);
        }

        if (_rubber && _marks.Length > 0)
        {
            var last = camera.WorldToScreen(_marks[^1].X, _marks[^1].Y, width, height);
            canvas.DrawLine((float)last.X, (float)last.Y, (float)_rubberX, (float)_rubberY, line);
        }
    }

    private static bool MarksEqual((double X, double Y)[] left, (double X, double Y)[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    private static SKPath CropPath(SheetVisual sheet, float originX, float originY)
    {
        var path = new SKPath();
        var crop = sheet.Crop;
        path.MoveTo((float)crop[0].X - originX, (float)crop[0].Y - originY);
        for (var i = 1; i < crop.Length; i++)
            path.LineTo((float)crop[i].X - originX, (float)crop[i].Y - originY);
        path.Close();
        return path;
    }

    private static void DrawHandle(SKCanvas canvas, (double X, double Y) point, SKPaint fill, SKPaint stroke)
    {
        var rect = new SKRect((float)point.X - 4, (float)point.Y - 4, (float)point.X + 4, (float)point.Y + 4);
        canvas.DrawRect(rect, fill);
        canvas.DrawRect(rect, stroke);
    }

    private void DrawMessage(SKCanvas canvas, float width, float height, string message)
    {
        using var paint = new SKPaint { Color = new SKColor(0xC8, 0xC2, 0xB8), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 18);
        font.MeasureText(message, out var bounds);
        canvas.DrawText(message, (width - bounds.Width) * 0.5f, height * 0.5f, font, paint);
    }

    private void DrawGrid(SKCanvas canvas, float width, float height)
    {
        var halfWidth = width / (2 * _zoom);
        var halfHeight = height / (2 * _zoom);
        var left = _centerX - halfWidth;
        var right = _centerX + halfWidth;
        var top = _centerY - halfHeight;
        var bottom = _centerY + halfHeight;
        var step = NiceStep(96 / _zoom);

        using var minor = LinePaint(new SKColor(0x3A, 0x37, 0x33));
        using var major = LinePaint(new SKColor(0x5C, 0x57, 0x4F));
        using var origin = LinePaint(new SKColor(0xD7, 0xB9, 0x8A));

        var index = (long)Math.Floor(left / step);
        var guard = 0;
        for (var worldX = index * step; worldX <= right && guard < 400; worldX += step, index++, guard++)
        {
            var screenX = ScreenX(worldX, width);
            var paint = index % 5 == 0 ? major : minor;
            if (index == 0)
                paint = origin;
            canvas.DrawLine(screenX, 0, screenX, height, paint);
        }

        index = (long)Math.Floor(top / step);
        guard = 0;
        for (var worldY = index * step; worldY <= bottom && guard < 400; worldY += step, index++, guard++)
        {
            var screenY = ScreenY(worldY, height);
            var paint = index % 5 == 0 ? major : minor;
            if (index == 0)
                paint = origin;
            canvas.DrawLine(0, screenY, width, screenY, paint);
        }
    }

    private float ScreenX(double worldX, float width) =>
        (float)((worldX - _centerX) * _zoom + width * 0.5);

    private float ScreenY(double worldY, float height) =>
        (float)((worldY - _centerY) * _zoom + height * 0.5);

    private static SKPaint LinePaint(SKColor color) => new()
    {
        Color = color,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1,
        IsAntialias = false,
    };

    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw))
            return 1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        var nice = normalized < 1.5 ? 1 : normalized < 3.5 ? 2 : normalized < 7.5 ? 5 : 10;
        return nice * magnitude;
    }
}
