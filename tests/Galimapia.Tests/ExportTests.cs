using Galimapia.Canvas;
using SkiaSharp;

namespace Galimapia.Tests;

public class ExportTests : IDisposable
{
    private readonly string _root;

    public ExportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "galimapia-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Pdf_and_png_keep_the_scan_and_drop_the_cropped_margin()
    {
        var scan = WriteRed(Path.Combine(_root, "sheet.png"), 20, 20);
        var sheet = new MapFragment(scan, 0, 0, 20, 20);
        sheet.Crop.Add(new CropPoint(10, 0));
        sheet.Crop.Add(new CropPoint(20, 0));
        sheet.Crop.Add(new CropPoint(20, 20));
        sheet.Crop.Add(new CropPoint(10, 20));
        sheet.CropCommitted = true;
        Assert.True(ExportPage.TryFrom([sheet], out var page));

        var png = Path.Combine(_root, "map.png");
        var pdf = Path.Combine(_root, "map.pdf");
        PageExport.Write(png, page, [ExportSheet.From(sheet)]);
        PageExport.Write(pdf, page, [ExportSheet.From(sheet)]);

        var pdfBytes = File.ReadAllBytes(pdf);
        var pdfText = System.Text.Encoding.ASCII.GetString(pdfBytes);
        Assert.StartsWith("%PDF", pdfText, StringComparison.Ordinal);
        Assert.Contains("/DCTDecode", pdfText, StringComparison.Ordinal);
        Assert.DoesNotContain("/ColorTransform", pdfText, StringComparison.Ordinal);
        var jpegStart = pdfBytes.AsSpan().IndexOf(new byte[] { 0xFF, 0xD8, 0xFF });
        Assert.True(jpegStart >= 0);
        using var embedded = SKBitmap.Decode(pdfBytes.AsSpan(jpegStart).ToArray());
        var embeddedRed = embedded.GetPixel(embedded.Width / 2, embedded.Height / 2);
        Assert.True(embeddedRed.Red > 200 && embeddedRed.Green < 40 && embeddedRed.Blue < 40);
        using var image = SKImage.FromEncodedData(png);
        Assert.Equal(20, image.Width);
        using var bitmap = SKBitmap.FromImage(image);
        var inside = bitmap.GetPixel(10, 10);
        var margin = bitmap.GetPixel(2, 10);
        Assert.True(inside.Red > 200 && inside.Green < 40 && inside.Blue < 40);
        Assert.True(margin.Red > 200 && margin.Green > 200 && margin.Blue > 200);
    }

    [Fact]
    public void A_warped_sheet_is_a_real_image_in_the_pdf()
    {
        var scan = WriteRed(Path.Combine(_root, "warp.png"), 20, 20);
        var sheet = new MapFragment(scan, 0, 0, 20, 20);
        sheet.EnsureWarp();
        sheet.Warp[2] = new CropPoint(6, 4);
        Assert.True(ExportPage.TryFrom([sheet], out var page));
        var pdf = Path.Combine(_root, "warp.pdf");
        PageExport.Write(pdf, page, [ExportSheet.From(sheet)], ExportSettings.Screen with { Kind = ExportKind.Pdf }, null, default);
        var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdf));
        Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);
        Assert.True(new FileInfo(pdf).Length > 400);
    }

    [Fact]
    public void Warp_export_keeps_the_source_colors()
    {
        var scan = WriteSplit(Path.Combine(_root, "split.png"));
        var sheet = new MapFragment(scan, 0, 0, 40, 20);
        sheet.EnsureWarp();
        sheet.Warp[2] = new CropPoint(8, 6);
        Assert.True(ExportPage.TryFrom([sheet], out var page));
        var png = Path.Combine(_root, "split-out.png");
        PageExport.Write(png, page, [ExportSheet.From(sheet)]);
        using var image = SKImage.FromEncodedData(png);
        using var bitmap = SKBitmap.FromImage(image);
        var scale = bitmap.Width / page.Size;
        var red = bitmap.GetPixel((int)((8 - page.X) * scale), (int)((10 - page.Y) * scale));
        var blue = bitmap.GetPixel((int)((30 - page.X) * scale), (int)((10 - page.Y) * scale));
        Assert.True(red.Red > 200 && red.Blue < 40);
        Assert.True(blue.Blue > 200 && blue.Red < 40);
    }

    [Fact]
    public void Jpeg_export_is_a_readable_picture()
    {
        var scan = WriteRed(Path.Combine(_root, "wide.png"), 30, 10);
        var sheet = new MapFragment(scan, 0, 0, 30, 10);
        Assert.True(ExportPage.TryFrom([sheet], out var page));
        var jpeg = Path.Combine(_root, "map.jpg");
        PageExport.Write(jpeg, page, [ExportSheet.From(sheet)]);
        using var image = SKImage.FromEncodedData(jpeg);
        Assert.True(image.Width >= 30);
        Assert.True(image.Height >= 30);
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

    private static string WriteSplit(string path)
    {
        var info = new SKImageInfo(40, 20, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        using var red = new SKPaint { Color = SKColors.Red };
        using var blue = new SKPaint { Color = SKColors.Blue };
        canvas.DrawRect(0, 0, 20, 20, red);
        canvas.DrawRect(20, 0, 20, 20, blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private static string WriteRed(string path, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }
}
