using Galimapia.Canvas;
using SkiaSharp;

namespace Galimapia.Tests;

public class ColorAndWarpTests : IDisposable
{
    private readonly string _root;

    public ColorAndWarpTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "galimapia-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Identity_color_is_the_original_scan()
    {
        Assert.True(ColorAdjust.IsIdentity(0, 1, 0));
        var matrix = ColorAdjust.Matrix(0, 1, 0);
        Assert.Equal(1, matrix[0], 3);
        Assert.Equal(0, matrix[4], 3);
        Assert.Equal(0.5f, ColorAdjust.Matrix(0.5f, 1, 0)[4], 3);
        Assert.False(ColorAdjust.IsIdentity(0, 1, 20));
    }

    [Fact]
    public void A_collapsed_corner_is_not_sent_to_the_mesh()
    {
        var indices = new List<int>();
        Assert.True(WarpMesh.Triangulate(
            [new(0, 0), new(10, 0), new(10, 8), new(0, 8)],
            indices));
        var placed = new CropPoint[] { new(10, 0), new(10, 0), new(10, 8), new(0, 8) };
        WarpMesh.KeepSolid(placed, indices);
        Assert.Equal(3, indices.Count);
        Assert.True(WarpMesh.Collides(placed, 3, 10, 0));
    }

    [Fact]
    public void A_rectangle_becomes_two_triangles()
    {
        var indices = new List<int>();
        Assert.True(WarpMesh.Triangulate(
            [new(0, 0), new(10, 0), new(10, 8), new(0, 8)],
            indices));
        Assert.Equal(6, indices.Count);
    }

    [Fact]
    public void Stretching_a_corner_does_not_move_the_sheet()
    {
        var sheet = new MapFragment(Path.Combine(_root, "a.jpg"), 0, 0, 100, 80);
        sheet.EnsureWarp();
        sheet.Warp[0] = new CropPoint(10, -5);
        Assert.Equal(0, sheet.X);
        Assert.Equal(0, sheet.Y);
        sheet.GetVisibleBounds(out var minX, out var minY, out var maxX, out var maxY);
        Assert.Equal(-5, minY, 4);
        Assert.Equal(100, maxX, 4);
        Assert.True(minX <= 0);

        sheet.X = 5;
        Assert.Equal(10, sheet.Warp[0].X, 4);
        sheet.GetVisibleBounds(out minX, out minY, out _, out _);
        Assert.Equal(5, minX, 4);
        Assert.Equal(-5, minY, 4);
    }

    [Fact]
    public async Task Color_and_warp_undo_without_moving_the_sheet()
    {
        var scan = WriteScan(Path.Combine(_root, "parcel.jpg"));
        var scene = new MapScene(new LodStore(Path.Combine(_root, "lod")), new ImageCache(8 * 1024 * 1024), action =>
        {
            action();
            return Task.CompletedTask;
        });
        try
        {
            await scene.ImportAsync([scan], 3, 4);
            var sheet = scene.Fragments[0];
            var x = sheet.X;
            scene.BeginEdit(sheet);
            scene.SetBrightness(sheet, 0.25f);
            scene.EndEdit();
            Assert.Equal(x, sheet.X, 4);
            Assert.Equal(0.25f, sheet.Brightness, 3);
            Assert.True(scene.Undo());
            Assert.Equal(0, sheet.Brightness, 3);
            Assert.Equal(x, sheet.X, 4);

            scene.BeginWarp(sheet);
            scene.SetWarpOffset(sheet, 1, 12, 0);
            scene.EndWarp();
            Assert.Equal(x, sheet.X, 4);
            Assert.Equal(12, sheet.WarpOffset(1).X, 4);
            Assert.True(scene.Undo());
            Assert.False(sheet.HasWarp);
            Assert.Equal(x, sheet.X, 4);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Project_file_keeps_color_and_warp()
    {
        var scan = WriteScan(Path.Combine(_root, "kept.jpg"));
        var sheet = new MapFragment(scan, 1, 2, 800, 600);
        sheet.Brightness = 0.2f;
        sheet.Contrast = 1.4f;
        sheet.Hue = -30;
        sheet.EnsureWarp();
        sheet.Warp[2] = new CropPoint(4, 5);
        var folder = Path.Combine(_root, "project");
        ProjectFile.Write(folder, [SheetSnapshot.From(sheet)], 0, 0, 1, null, folder);
        var document = ProjectFile.Read(folder);
        var loaded = document.Sheets[0].Sheet;
        Assert.Equal(0.2f, loaded.Brightness, 3);
        Assert.Equal(1.4f, loaded.Contrast, 3);
        Assert.Equal(-30, loaded.Hue, 3);
        Assert.Equal(4, loaded.Warp[2].X, 4);
        Assert.Equal(5, loaded.Warp[2].Y, 4);
        Assert.Equal(1, loaded.X, 4);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string WriteScan(string path)
    {
        var info = new SKImageInfo(800, 600, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(80, 90, 70));
        using var pixmap = bitmap.PeekPixels();
        using var image = SKImage.FromPixelCopy(pixmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }
}
