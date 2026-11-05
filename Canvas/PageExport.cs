using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Galimapia.Canvas;

/// <summary>
/// One sheet frozen for export. Built on the UI thread so the writer can run in the background.
/// </summary>
public sealed class ExportSheet
{
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Rotation { get; init; }
    public required double Scale { get; init; }
    public required bool FlipX { get; init; }
    public required bool FlipY { get; init; }
    public required float Opacity { get; init; }
    public required float Brightness { get; init; }
    public required float Contrast { get; init; }
    public required float Hue { get; init; }
    public required bool HasCut { get; init; }
    public required CropPoint[] Crop { get; init; }
    public required CropPoint[] Warp { get; init; }
    public required double PivotX { get; init; }
    public required double PivotY { get; init; }

    public static ExportSheet From(MapFragment fragment)
    {
        var frame = fragment.Frame;
        return new ExportSheet
        {
            Name = fragment.Name,
            SourcePath = fragment.SourcePath,
            PixelWidth = fragment.PixelWidth,
            PixelHeight = fragment.PixelHeight,
            X = fragment.X,
            Y = fragment.Y,
            Rotation = fragment.RotationDegrees,
            Scale = fragment.Scale,
            FlipX = fragment.FlipX,
            FlipY = fragment.FlipY,
            Opacity = fragment.Opacity,
            Brightness = fragment.Brightness,
            Contrast = fragment.Contrast,
            Hue = fragment.Hue,
            HasCut = fragment.HasCut,
            Crop = fragment.Crop.ToArray(),
            Warp = fragment.Warp.ToArray(),
            PivotX = frame.PivotX,
            PivotY = frame.PivotY,
        };
    }

    public SheetFrame Frame => new(X, Y, PixelWidth, PixelHeight, Rotation, Scale, FlipX, FlipY, PivotX, PivotY);
}

/// <summary>
/// Writes the white export square. PDF places each full scan on the page. JPEG and PNG are one
/// flattened picture whose long side stays within <see cref="MaxRasterSide"/>.
/// </summary>
public enum ExportKind
{
    Pdf,
    Jpeg,
    Png,
    Tiles,
}

public readonly record struct ExportSettings(ExportKind Kind, int LongSide, int JpegQuality, bool Threads = false, bool Gpu = false)
{
    public static ExportSettings Screen { get; } = new(ExportKind.Pdf, 2048, 75);
    public static ExportSettings Print { get; } = new(ExportKind.Pdf, 4096, 85);
    /// <summary>The whole export square. A single picture is still limited to <see cref="PageExport.MaxRasterSide"/>.</summary>
    public static ExportSettings Fine { get; } = new(ExportKind.Pdf, int.MaxValue, 92);

    public static ExportSettings For(ExportKind kind, int preset)
    {
        var basis = preset switch
        {
            1 => Print,
            2 => Fine,
            _ => Screen,
        };
        return basis with { Kind = kind };
    }
}

public readonly record struct ExportReport(double Fraction, string Detail);

public static class PageExport
{
    public const float MaxPdfPoints = 14400;
    public const int MaxRasterSide = 8192;
    public const int MaxTileSide = 65536;
    public const int TileSize = 512;

    public static void Write(string path, ExportPage page, IReadOnlyList<ExportSheet> sheets) =>
        Write(path, page, sheets, ExportSettings.Fine with { Kind = KindOf(path) }, null, default);

    public static void Write(
        string path,
        ExportPage page,
        IReadOnlyList<ExportSheet> sheets,
        ExportSettings settings,
        IProgress<ExportReport>? progress,
        CancellationToken cancellation)
    {
        try
        {
            switch (settings.Kind)
            {
                case ExportKind.Pdf:
                    WritePdf(path, page, sheets, settings, progress, cancellation);
                    break;
                case ExportKind.Png:
                    WriteRaster(path, page, sheets, SKEncodedImageFormat.Png, 100, settings, progress, cancellation);
                    break;
                case ExportKind.Tiles:
                    WriteTiles(path, page, sheets, settings, progress, cancellation);
                    break;
                default:
                    WriteRaster(path, page, sheets, SKEncodedImageFormat.Jpeg, settings.JpegQuality, settings, progress, cancellation);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(path, settings.Kind);
            throw;
        }
    }

    public static int OutputSide(double pageSize, int requested)
    {
        if (pageSize < 1)
            return 1;

        var page = (int)Math.Round(pageSize);
        var wanted = requested >= page ? page : Math.Min(page, requested);
        return Math.Clamp(wanted, 1, MaxRasterSide);
    }

    /// <summary>
    /// Tile pyramid side. Fine is the export square itself, so a large survey stays as sharp
    /// at the closest zoom as a small one. A single JPEG or PNG still stops at <see cref="MaxRasterSide"/>.
    /// </summary>
    public static int TileSide(double pageSize, int requested)
    {
        if (pageSize < 1)
            return 1;

        var page = (int)Math.Round(pageSize);
        var wanted = requested >= page ? page : Math.Min(page, requested);
        return Math.Clamp(wanted, 1, MaxTileSide);
    }

    private static void WritePdf(
        string path,
        ExportPage page,
        IReadOnlyList<ExportSheet> sheets,
        ExportSettings settings,
        IProgress<ExportReport>? progress,
        CancellationToken cancellation)
    {
        var side = (float)Math.Min(OutputSide(page.Size, settings.LongSide), MaxPdfPoints);
        var scale = side / page.Size;
        var pdf = new PdfDocument(side, side);
        var placed = new SheetPicture?[sheets.Count];
        using var gpu = OpenGpu(settings);
        var cores = WorkerCount(settings);
        if (cores == 1)
        {
            for (var i = 0; i < sheets.Count; i++)
                placed[i] = RenderPdfSheet(page, sheets, scale, side, settings, gpu?.Context, progress, i, sheets.Count, cancellation);
        }
        else
        {
            Parallel.For(0, sheets.Count, new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = cancellation }, i =>
                placed[i] = RenderPdfSheet(page, sheets, scale, side, settings, null, progress, i, sheets.Count, cancellation));
        }

        foreach (var picture in placed)
        {
            if (picture is { } item)
                pdf.AddImage(item.Jpeg, item.Mask, item.Width, item.Height, item.X, item.Y);
        }

        pdf.Save(path);
    }

    private static bool TryRenderSheet(
        ExportPage page,
        ExportSheet sheet,
        double scale,
        int quality,
        CancellationToken cancellation,
        GRContext? gpu,
        out byte[] jpeg,
        out byte[] mask,
        out int width,
        out int height,
        out double left,
        out double top)
    {
        jpeg = [];
        mask = [];
        width = 0;
        height = 0;
        left = 0;
        top = 0;
        if (!TryWorldBounds(sheet, out left, out top, out var right, out var bottom))
            return false;

        left = Math.Max(left, page.X);
        top = Math.Max(top, page.Y);
        right = Math.Min(right, page.X + page.Size);
        bottom = Math.Min(bottom, page.Y + page.Size);
        if (right - left < 0.5 || bottom - top < 0.5)
            return false;

        width = Math.Max(1, (int)Math.Ceiling((right - left) * scale));
        height = Math.Max(1, (int)Math.Ceiling((bottom - top) * scale));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = CreateSurface(info, gpu);
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate((float)(-left * scale), (float)(-top * scale));
        DrawSheet(surface.Canvas, sheet, scale, null, cancellation, gpu: gpu);
        using var shot = surface.Snapshot();
        using var copy = shot.PeekPixels() is null ? RasterCopy(shot, info) : null;
        var pixmap = shot.PeekPixels() ?? copy?.PeekPixels() ?? throw new InvalidDataException($"Cannot read the export of {sheet.Name}.");
        mask = AlphaMask(pixmap);
        // The JPEG is this sheet already laid on white. The mask's /Matte entry tells a viewer
        // to take that white back off, so a sheet underneath shows through opacity and soft edges.
        using var flat = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        flat.Canvas.Clear(SKColors.White);
        flat.Canvas.DrawImage(shot, 0, 0);
        using var color = flat.Snapshot();
        using var data = color.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new InvalidDataException($"Cannot encode {sheet.Name}.");
        jpeg = data.ToArray();
        return true;
    }

    private static bool TryWorldBounds(ExportSheet sheet, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = double.PositiveInfinity;
        minY = double.PositiveInfinity;
        maxX = double.NegativeInfinity;
        maxY = double.NegativeInfinity;
        var frame = sheet.Frame;
        var controls = Controls(sheet);
        if (controls.Length == 0)
            return false;

        for (var i = 0; i < controls.Length; i++)
        {
            var offset = i < sheet.Warp.Length ? sheet.Warp[i] : default;
            var world = frame.ToWorld(controls[i].X + offset.X, controls[i].Y + offset.Y);
            minX = Math.Min(minX, world.X);
            minY = Math.Min(minY, world.Y);
            maxX = Math.Max(maxX, world.X);
            maxY = Math.Max(maxY, world.Y);
        }

        return maxX > minX && maxY > minY;
    }

    private static byte[] AlphaMask(SKPixmap pixmap)
    {
        var width = pixmap.Width;
        var height = pixmap.Height;
        var raw = new byte[width * height];
        var pixels = pixmap.GetPixelSpan();
        var stride = pixmap.RowBytes;
        var channels = pixmap.BytesPerPixel;
        var alphaIndex = channels - 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
                raw[y * width + x] = pixels[row + x * channels + alphaIndex];
        }

        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);
        return output.ToArray();
    }

    private static void WriteRaster(
        string path,
        ExportPage page,
        IReadOnlyList<ExportSheet> sheets,
        SKEncodedImageFormat format,
        int quality,
        ExportSettings settings,
        IProgress<ExportReport>? progress,
        CancellationToken cancellation)
    {
        var side = OutputSide(page.Size, settings.LongSide);
        var scale = side / page.Size;
        var info = new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var gpu = OpenGpu(settings);
        using var surface = CreateSurface(info, gpu?.Context);
        DrawPage(surface.Canvas, page, sheets, side, scale, settings, progress, cancellation, gpu?.Context);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, quality) ?? throw new InvalidDataException("Could not encode the export.");
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void WriteTiles(
        string directory,
        ExportPage page,
        IReadOnlyList<ExportSheet> sheets,
        ExportSettings settings,
        IProgress<ExportReport>? progress,
        CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        var finest = TileSide(page.Size, settings.LongSide);
        var maxLevel = 0;
        while ((TileSize << maxLevel) < finest)
            maxLevel++;

        var total = 0;
        for (var level = 0; level <= maxLevel; level++)
        {
            var across = 1 << level;
            total += across * across;
        }

        var done = 0;
        var cores = WorkerCount(settings);
        using var gpu = cores == 1 ? OpenGpu(settings) : null;
        for (var level = 0; level <= maxLevel; level++)
        {
            var across = 1 << level;
            var mapPixels = TileSize * across;
            var scale = mapPixels / page.Size;
            var worldPerTile = page.Size / across;
            for (var column = 0; column < across; column++)
                Directory.CreateDirectory(Path.Combine(directory, "tiles", level.ToString(), column.ToString()));

            var tiles = new (int X, int Y)[across * across];
            var index = 0;
            for (var y = 0; y < across; y++)
            {
                for (var x = 0; x < across; x++)
                    tiles[index++] = (x, y);
            }

            var pictures = new SheetPictures();
            try
            {
                void DrawTile((int X, int Y) tile)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var tileX = page.X + tile.X * worldPerTile;
                    var tileY = page.Y + tile.Y * worldPerTile;
                    var info = new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using var surface = CreateSurface(info, gpu?.Context);
                    surface.Canvas.Clear(SKColors.White);
                    surface.Canvas.Translate((float)(-tileX * scale), (float)(-tileY * scale));
                    foreach (var sheet in sheets)
                    {
                        if (!Intersects(sheet, tileX, tileY, tileX + worldPerTile, tileY + worldPerTile))
                            continue;
                        DrawSheet(surface.Canvas, sheet, scale, null, cancellation, pictures, gpu?.Context);
                    }

                    gpu?.Context.Flush();
                    using var image = surface.Snapshot();
                    using var data = image.Encode(SKEncodedImageFormat.Jpeg, settings.JpegQuality)
                        ?? throw new InvalidDataException("Could not encode a map tile.");
                    var folder = Path.Combine(directory, "tiles", level.ToString(), tile.X.ToString());
                    using var stream = File.Create(Path.Combine(folder, tile.Y + ".jpg"));
                    data.SaveTo(stream);
                    var finished = Interlocked.Increment(ref done);
                    progress?.Report(new ExportReport(finished / (double)total, $"Tile {finished} of {total}, level {level}"));
                }

                if (cores == 1)
                {
                    foreach (var tile in tiles)
                        DrawTile(tile);
                }
                else
                {
                    Parallel.ForEach(
                        tiles,
                        new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = cancellation },
                        DrawTile);
                }
            }
            finally
            {
                pictures.Dispose();
            }
        }

        File.WriteAllText(Path.Combine(directory, "index.html"), Viewer(maxLevel, page.Size));
    }

    private static void DrawPage(
        SKCanvas canvas,
        ExportPage page,
        IReadOnlyList<ExportSheet> sheets,
        float side,
        double scale,
        ExportSettings settings,
        IProgress<ExportReport>? progress,
        CancellationToken cancellation,
        GRContext? gpu)
    {
        canvas.Clear(SKColors.White);
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, side, side));
        for (var i = 0; i < sheets.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(new ExportReport((i + 1d) / Math.Max(sheets.Count, 1), $"Sheet {i + 1} of {sheets.Count}: {sheets[i].Name}"));
            DrawSheet(canvas, sheets[i], scale, page, cancellation, gpu: gpu);
        }

        canvas.Restore();
    }

    private static void DrawSheet(
        SKCanvas canvas,
        ExportSheet sheet,
        double scale,
        ExportPage? page,
        CancellationToken cancellation,
        SheetPictures? cache = null,
        GRContext? gpu = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (sheet.Opacity <= 0 || sheet.PixelWidth <= 0 || sheet.PixelHeight <= 0)
            return;

        var frame = sheet.Frame;
        var center = frame.Center;
        var originX = page?.X ?? 0;
        var originY = page?.Y ?? 0;
        canvas.Save();
        canvas.Translate(
            (float)((center.X - originX) * scale),
            (float)((center.Y - originY) * scale));
        canvas.RotateDegrees((float)sheet.Rotation);
        canvas.Scale(
            (float)(scale * sheet.Scale * (sheet.FlipX ? -1 : 1)),
            (float)(scale * sheet.Scale * (sheet.FlipY ? -1 : 1)));

        var needed = (int)Math.Clamp(
            Math.Ceiling(Math.Max(sheet.PixelWidth, sheet.PixelHeight) * Math.Abs(sheet.Scale) * scale),
            1,
            MaxRasterSide);
        var owned = cache is null;
        var image = cache is null
            ? ImageFiles.DecodeLongSide(sheet.SourcePath, needed)
            : cache.Get(sheet.SourcePath, needed);

        try
        {
            if (MapFragment.IsWarped(sheet.HasCut ? sheet.Crop.Length : 4, sheet.Warp))
                DrawWarpedImage(canvas, sheet, frame, image, scale, gpu);
            else
                DrawLocal(canvas, sheet, frame, image);
        }
        finally
        {
            if (owned)
                image.Dispose();
        }

        canvas.Restore();
    }

    private static void DrawLocal(SKCanvas canvas, ExportSheet sheet, SheetFrame frame, SKImage image)
    {
        var originX = (float)frame.PivotLocalX;
        var originY = (float)frame.PivotLocalY;
        var alpha = (byte)Math.Clamp((int)Math.Round(sheet.Opacity * 255d), 0, 255);
        using var color = ColorAdjust.CreateFilter(sheet.Brightness, sheet.Contrast, sheet.Hue);
        using var clip = sheet.HasCut ? CropPath(sheet, originX, originY) : null;
        if (clip is not null)
        {
            canvas.Save();
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);
        }

        var dest = new SKRect(-originX, -originY, sheet.PixelWidth - originX, sheet.PixelHeight - originY);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha), ColorFilter = color, IsAntialias = true };
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
        if (clip is not null)
            canvas.Restore();
    }

    private static void DrawWarpedImage(SKCanvas canvas, ExportSheet sheet, SheetFrame frame, SKImage image, double outputScale, GRContext? gpu)
    {
        var controls = Controls(sheet);
        var placed = new CropPoint[controls.Length];
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (var i = 0; i < controls.Length; i++)
        {
            var offset = i < sheet.Warp.Length ? sheet.Warp[i] : default;
            placed[i] = new CropPoint(controls[i].X + offset.X, controls[i].Y + offset.Y);
            minX = Math.Min(minX, placed[i].X);
            minY = Math.Min(minY, placed[i].Y);
            maxX = Math.Max(maxX, placed[i].X);
            maxY = Math.Max(maxY, placed[i].Y);
        }

        var pixelScale = Math.Max(Math.Abs(outputScale * sheet.Scale), 1e-6);
        var spanX = Math.Max(maxX - minX, 1);
        var spanY = Math.Max(maxY - minY, 1);
        var bitmapWidth = Math.Max(1, (int)Math.Min(MaxRasterSide, Math.Ceiling(spanX * pixelScale)));
        var bitmapHeight = Math.Max(1, (int)Math.Min(MaxRasterSide, Math.Ceiling(spanY * pixelScale)));
        var indices = new List<int>();
        if (!WarpMesh.Triangulate(controls, indices))
        {
            DrawLocal(canvas, sheet, frame, image);
            return;
        }

        WarpMesh.KeepSolid(placed, indices);
        // Transparent, not white. The box around a warped sheet is larger than the triangles.
        // One mesh, not a clip per triangle: antialiased clips leave white rays along the shared edges.
        var info = new SKImageInfo(bitmapWidth, bitmapHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = CreateSurface(info, gpu);
        var mesh = surface.Canvas;
        mesh.Clear(SKColors.Transparent);
        if (indices.Count >= 3)
        {
            var vertices = new SKPoint[indices.Count];
            var texture = new SKPoint[indices.Count];
            var toBitmapX = bitmapWidth / spanX;
            var toBitmapY = bitmapHeight / spanY;
            for (var i = 0; i < indices.Count; i++)
            {
                var index = indices[i];
                vertices[i] = BitmapPoint(placed[index], minX, minY, toBitmapX, toBitmapY);
                texture[i] = ImagePoint(sheet, image, controls[index]);
            }

            using var shader = SKShader.CreateImage(image, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            using var meshPaint = new SKPaint { Shader = shader, IsAntialias = false };
            using var verticesMesh = SKVertices.CreateCopy(SKVertexMode.Triangles, vertices, texture, null);
            mesh.DrawVertices(verticesMesh, SKBlendMode.SrcOver, meshPaint);
        }

        using var shot = surface.Snapshot();
        var originX = (float)frame.PivotLocalX;
        var originY = (float)frame.PivotLocalY;
        var dest = new SKRect((float)minX - originX, (float)minY - originY, (float)maxX - originX, (float)maxY - originY);
        var alpha = (byte)Math.Clamp((int)Math.Round(sheet.Opacity * 255d), 0, 255);
        using var color = ColorAdjust.CreateFilter(sheet.Brightness, sheet.Contrast, sheet.Hue);
        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, alpha),
            ColorFilter = color,
            IsAntialias = true,
        };
        canvas.DrawImage(shot, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
    }

    private static SKPoint ImagePoint(ExportSheet sheet, SKImage image, CropPoint local) =>
        new(
            (float)(local.X / sheet.PixelWidth * image.Width),
            (float)(local.Y / sheet.PixelHeight * image.Height));

    private static SKPoint BitmapPoint(CropPoint placed, double minX, double minY, double scaleX, double scaleY) =>
        new((float)((placed.X - minX) * scaleX), (float)((placed.Y - minY) * scaleY));

    private static SheetPicture? RenderPdfSheet(
        ExportPage page,
        IReadOnlyList<ExportSheet> sheets,
        double scale,
        float side,
        ExportSettings settings,
        GRContext? gpu,
        IProgress<ExportReport>? progress,
        int index,
        int count,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new ExportReport((index + 1d) / Math.Max(count, 1), $"Sheet {index + 1} of {count}: {sheets[index].Name}"));
        if (!TryRenderSheet(page, sheets[index], scale, settings.JpegQuality, cancellation, gpu, out var jpeg, out var mask, out var width, out var height, out var left, out var top))
            return null;

        var pdfX = (float)((left - page.X) * scale);
        var pdfY = (float)(side - (top - page.Y) * scale - height);
        return new SheetPicture(jpeg, mask, width, height, pdfX, pdfY);
    }

    private static int WorkerCount(ExportSettings settings) =>
        settings.Threads ? Math.Max(1, Environment.ProcessorCount) : 1;

    private static ExportGpu? OpenGpu(ExportSettings settings)
    {
        if (!settings.Gpu || settings.Threads)
            return null;

        var gpu = ExportGpu.TryOpen();
        if (gpu is null)
            SessionLog.Action("export video card unavailable, using the cpu");
        return gpu;
    }

    private static SKSurface CreateSurface(SKImageInfo info, GRContext? gpu)
    {
        if (gpu is not null)
        {
            var created = SKSurface.Create(gpu, true, info);
            if (created is not null)
                return created;
        }

        return SKSurface.Create(info);
    }

    private static SKBitmap RasterCopy(SKImage image, SKImageInfo info)
    {
        var bitmap = new SKBitmap(info);
        if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
        {
            bitmap.Dispose();
            throw new InvalidDataException("Cannot read the export.");
        }

        return bitmap;
    }

    private static ExportKind KindOf(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".pdf")
            return ExportKind.Pdf;
        if (extension == ".png")
            return ExportKind.Png;
        if (extension == ".tiles")
            return ExportKind.Tiles;
        return ExportKind.Jpeg;
    }

    private static bool Intersects(ExportSheet sheet, double left, double top, double right, double bottom)
    {
        var frame = sheet.Frame;
        var controls = Controls(sheet);
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        for (var i = 0; i < controls.Length; i++)
        {
            var offset = i < sheet.Warp.Length ? sheet.Warp[i] : default;
            var world = frame.ToWorld(controls[i].X + offset.X, controls[i].Y + offset.Y);
            minX = Math.Min(minX, world.X);
            minY = Math.Min(minY, world.Y);
            maxX = Math.Max(maxX, world.X);
            maxY = Math.Max(maxY, world.Y);
        }

        return minX < right && maxX > left && minY < bottom && maxY > top;
    }

    private static void TryDelete(string path, ExportKind kind)
    {
        try
        {
            if (kind == ExportKind.Tiles)
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Viewer(int maxLevel, double pageSize) =>
        $$"""
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <title>{{AppInfo.Name}}</title>
        <style>
        html,body{margin:0;height:100%;background:#242220;overflow:hidden;font-family:Consolas,monospace;color:#C8C2B8}
        #hud{position:fixed;left:12px;top:12px;z-index:2;background:#262421;padding:8px 10px}
        img.tile{position:absolute;width:{{TileSize}}px;height:{{TileSize}}px}
        </style>
        </head>
        <body>
        <div id="hud"></div>
        <div id="stage"></div>
        <script>
        const TILE={{TileSize}}, MAXZ={{maxLevel}}, PAGE={{pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
        let z=0, ox=(TILE-innerWidth)/2, oy=(TILE-innerHeight)/2, drag=null;
        const stage=document.getElementById('stage');
        const hud=document.getElementById('hud');
        function draw(){
          stage.replaceChildren();
          const n=1<<z;
          const x0=Math.floor(ox/TILE), y0=Math.floor(oy/TILE);
          const x1=Math.floor((ox+innerWidth)/TILE), y1=Math.floor((oy+innerHeight)/TILE);
          for(let y=y0;y<=y1;y++) for(let x=x0;x<=x1;x++){
            if(x<0||y<0||x>=n||y>=n) continue;
            const img=document.createElement('img');
            img.className='tile';
            img.src=`tiles/${z}/${x}/${y}.jpg`;
            img.style.left=(x*TILE-ox)+'px';
            img.style.top=(y*TILE-oy)+'px';
            stage.appendChild(img);
          }
          hud.textContent=`${MAXZ===0?'Map':`Zoom ${z} / ${MAXZ}`} · drag, wheel`;
        }
        addEventListener('pointerdown',e=>{drag={x:e.clientX,y:e.clientY,ox,oy};});
        addEventListener('pointerup',()=>drag=null);
        addEventListener('pointermove',e=>{
          if(!drag) return;
          ox=drag.ox-(e.clientX-drag.x);
          oy=drag.oy-(e.clientY-drag.y);
          draw();
        });
        addEventListener('wheel',e=>{
          e.preventDefault();
          const dz=e.deltaY<0?1:-1, nz=Math.max(0,Math.min(MAXZ,z+dz));
          if(nz===z) return;
          const f=Math.pow(2,nz-z);
          ox=(ox+e.clientX)*f-e.clientX;
          oy=(oy+e.clientY)*f-e.clientY;
          z=nz; draw();
        },{passive:false});
        addEventListener('resize',draw);
        draw();
        </script>
        </body>
        </html>
        """;

    private static CropPoint[] Controls(ExportSheet sheet)
    {
        if (sheet.HasCut)
            return sheet.Crop;

        return
        [
            new(0, 0),
            new(sheet.PixelWidth, 0),
            new(sheet.PixelWidth, sheet.PixelHeight),
            new(0, sheet.PixelHeight),
        ];
    }

    private static SKPath CropPath(ExportSheet sheet, float originX, float originY)
    {
        var path = new SKPath();
        path.MoveTo((float)sheet.Crop[0].X - originX, (float)sheet.Crop[0].Y - originY);
        for (var i = 1; i < sheet.Crop.Length; i++)
            path.LineTo((float)sheet.Crop[i].X - originX, (float)sheet.Crop[i].Y - originY);
        path.Close();
        return path;
    }
}

sealed class SheetPictures : IDisposable
{
    private readonly Dictionary<string, SKImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public SKImage Get(string path, int needed)
    {
        var key = path + "|" + needed;
        lock (_gate)
        {
            if (_images.TryGetValue(key, out var found))
                return found;
        }

        var image = ImageFiles.DecodeLongSide(path, needed);
        lock (_gate)
        {
            if (_images.TryGetValue(key, out var found))
            {
                image.Dispose();
                return found;
            }

            _images[key] = image;
            return image;
        }
    }

    public void Dispose()
    {
        foreach (var image in _images.Values)
            image.Dispose();
        _images.Clear();
    }
}

sealed record SheetPicture(byte[] Jpeg, byte[] Mask, int Width, int Height, float X, float Y);

sealed class PdfDocument
{
    private readonly float _width;
    private readonly float _height;
    private readonly List<byte[]> _jpegs = [];
    private readonly List<byte[]> _masks = [];
    private readonly List<(int Width, int Height, float X, float Y)> _places = [];

    public PdfDocument(float width, float height)
    {
        _width = width;
        _height = height;
    }

    public void AddImage(byte[] jpeg, byte[] mask, int width, int height, float x, float y)
    {
        _jpegs.Add(jpeg);
        _masks.Add(mask);
        _places.Add((width, height, x, y));
    }

    public void Save(string path)
    {
        using var stream = File.Create(path);
        stream.Write("%PDF-1.4\n"u8);
        stream.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        var count = 4 + _jpegs.Count * 2;
        var offsets = new long[count + 1];
        WriteObject(stream, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>"u8);
        WriteObject(stream, offsets, 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"u8);

        var resources = new StringBuilder("<< /XObject << ");
        for (var i = 0; i < _jpegs.Count; i++)
        {
            var imageId = 5 + i * 2;
            resources.Append(CultureInfo.InvariantCulture, $"/Im{i} {imageId} 0 R ");
        }

        resources.Append(">> >>");
        var content = Contents();
        var contentBytes = Encoding.ASCII.GetBytes(content);
        var page = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {_width:0.###} {_height:0.###}] /Contents 4 0 R /Resources {resources} >>"));
        var contentDict = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< /Length {contentBytes.Length} >>"));
        WriteObject(stream, offsets, 3, page);
        WriteStream(stream, offsets, 4, contentDict, contentBytes);
        for (var i = 0; i < _jpegs.Count; i++)
        {
            var place = _places[i];
            var imageId = 5 + i * 2;
            var maskId = imageId + 1;
            var imageDict = Encoding.ASCII.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"<< /Type /XObject /Subtype /Image /Width {place.Width} /Height {place.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /SMask {maskId} 0 R /Length {_jpegs[i].Length} >>"));
            var maskDict = Encoding.ASCII.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"<< /Type /XObject /Subtype /Image /Width {place.Width} /Height {place.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Matte [1 1 1] /Length {_masks[i].Length} >>"));
            WriteStream(stream, offsets, imageId, imageDict, _jpegs[i]);
            WriteStream(stream, offsets, maskId, maskDict, _masks[i]);
        }

        var xref = stream.Position;
        var head = Encoding.ASCII.GetBytes($"xref\n0 {count + 1}\n");
        stream.Write(head);
        stream.Write("0000000000 65535 f \n"u8);
        for (var i = 1; i <= count; i++)
        {
            var line = Encoding.ASCII.GetBytes(offsets[i].ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
            stream.Write(line);
        }

        var trailer = Encoding.ASCII.GetBytes($"trailer\n<< /Size {count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        stream.Write(trailer);
    }

    private string Contents()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"1 1 1 rg\n0 0 {_width:0.###} {_height:0.###} re\nf\n");
        for (var i = 0; i < _places.Count; i++)
        {
            var place = _places[i];
            text.Append(CultureInfo.InvariantCulture, $"q\n{place.Width:0.###} 0 0 {place.Height:0.###} {place.X:0.###} {place.Y:0.###} cm\n/Im{i} Do\nQ\n");
        }

        return text.ToString();
    }

    private static void WriteObject(Stream stream, long[] offsets, int id, ReadOnlySpan<byte> body)
    {
        offsets[id] = stream.Position;
        var prefix = Encoding.ASCII.GetBytes($"{id} 0 obj\n");
        stream.Write(prefix);
        stream.Write(body);
        stream.Write("\nendobj\n"u8);
    }

    private static void WriteStream(Stream stream, long[] offsets, int id, ReadOnlySpan<byte> dictionary, ReadOnlySpan<byte> data)
    {
        offsets[id] = stream.Position;
        var prefix = Encoding.ASCII.GetBytes($"{id} 0 obj\n");
        stream.Write(prefix);
        stream.Write(dictionary);
        stream.Write("\nstream\n"u8);
        stream.Write(data);
        stream.Write("\nendstream\nendobj\n"u8);
    }
}
