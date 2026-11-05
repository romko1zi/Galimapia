using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
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
        var pdfText = Encoding.ASCII.GetString(pdfBytes);
        Assert.StartsWith("%PDF", pdfText, StringComparison.Ordinal);
        Assert.Contains("/DCTDecode", pdfText, StringComparison.Ordinal);
        Assert.DoesNotContain("/ColorTransform", pdfText, StringComparison.Ordinal);
        Assert.DoesNotContain("<< /Length  >>", pdfText, StringComparison.Ordinal);
        AssertPdfStreams(pdfBytes);
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
    public void A_translucent_sheet_keeps_the_color_of_the_sheet_underneath()
    {
        var blue = WriteSolid(Path.Combine(_root, "blue.png"), 48, 36, new SKColor(20, 40, 220));
        var red = WriteSolid(Path.Combine(_root, "red-over.png"), 48, 48, new SKColor(220, 30, 30));
        var under = new MapFragment(blue, 0, 0, 48, 36);
        var over = new MapFragment(red, 12, 8, 48, 48) { Opacity = 0.5f };
        Assert.True(ExportPage.TryFrom([under, over], out var page));

        var png = Path.Combine(_root, "stack.png");
        var pdf = Path.Combine(_root, "stack.pdf");
        var sheets = new[] { ExportSheet.From(under), ExportSheet.From(over) };
        PageExport.Write(png, page, sheets);
        PageExport.Write(pdf, page, sheets);

        var objects = AssertPdfStreams(File.ReadAllBytes(pdf));
        using var expected = SKBitmap.Decode(png);
        using var actual = CompositePdf(objects, expected.Width);
        var bad = 0;
        var worst = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var left = expected.GetPixel(x, y);
                var right = actual.GetPixel(x, y);
                var delta = Math.Abs(left.Red - right.Red) + Math.Abs(left.Green - right.Green) + Math.Abs(left.Blue - right.Blue);
                if (delta > worst)
                    worst = delta;
                if (delta > 24)
                    bad++;
            }
        }

        Assert.True(bad == 0, $"The PDF disagrees with the PNG in {bad} pixels. The worst channel sum is {worst}.");
    }

    [Fact]
    public void Fine_tiles_use_the_export_square_and_a_picture_stays_within_8192()
    {
        Assert.Equal(400, PageExport.TileSide(400, ExportSettings.Fine.LongSide));
        Assert.Equal(400, PageExport.TileSide(400, ExportSettings.Screen.LongSide));
        Assert.Equal(27078, PageExport.TileSide(27078, ExportSettings.Fine.LongSide));
        Assert.Equal(2048, PageExport.TileSide(27078, ExportSettings.Screen.LongSide));
        Assert.Equal(8192, PageExport.OutputSide(27078, ExportSettings.Fine.LongSide));
        Assert.Equal(20, PageExport.OutputSide(20, ExportSettings.Fine.LongSide));
    }

    [Fact]
    public void A_warped_corner_leaves_the_sheet_underneath_visible()
    {
        var blue = WriteSolid(Path.Combine(_root, "under-blue.png"), 60, 40, new SKColor(20, 40, 220));
        var red = WriteSolid(Path.Combine(_root, "over-red.png"), 60, 40, new SKColor(220, 30, 30));
        var under = new MapFragment(blue, 0, 0, 60, 40);
        var over = new MapFragment(red, 0, 0, 60, 40);
        over.EnsureWarp();
        over.Warp[2] = new CropPoint(-30, -20);
        Assert.True(ExportPage.TryFrom([under, over], out var page));
        var png = Path.Combine(_root, "warp-hole.png");
        PageExport.Write(png, page, [ExportSheet.From(under), ExportSheet.From(over)]);
        using var image = SKBitmap.Decode(png);
        var scale = image.Width / page.Size;
        var inside = At(image, page, scale, 15, 10);
        var outside = At(image, page, scale, 55, 32);
        Assert.True(inside.Red > 180 && inside.Blue < 80, inside.ToString());
        Assert.True(outside.Blue > 180 && outside.Red < 80, outside.ToString());
    }

    private static SKColor At(SKBitmap image, ExportPage page, double scale, double x, double y) =>
        image.GetPixel(
            (int)Math.Clamp((x - page.X) * scale, 0, image.Width - 1),
            (int)Math.Clamp((y - page.Y) * scale, 0, image.Height - 1));

    [Fact]
    public void The_video_card_option_does_not_throw()
    {
        var type = typeof(PageExport).Assembly.GetType("Galimapia.Canvas.ExportGpu");
        Assert.NotNull(type);
        var opened = type!.GetMethod("TryOpen")!.Invoke(null, null);
        (opened as IDisposable)?.Dispose();
    }

    [Fact]
    public void Threads_and_the_video_card_keep_a_warped_scan_red()
    {
        var red = WriteSolid(Path.Combine(_root, "fast.png"), 40, 30, new SKColor(220, 30, 30));
        var sheet = new MapFragment(red, 0, 0, 40, 30);
        sheet.EnsureWarp();
        sheet.Warp[2] = new CropPoint(-6, -4);
        Assert.True(ExportPage.TryFrom([sheet], out var page));
        var sheets = new[] { ExportSheet.From(sheet) };
        AssertRed(Path.Combine(_root, "cpu"), page, sheets, ExportSettings.Fine with { Kind = ExportKind.Tiles });
        AssertRed(Path.Combine(_root, "threads"), page, sheets, ExportSettings.Fine with { Kind = ExportKind.Tiles, Threads = true });
        AssertRed(Path.Combine(_root, "gpu"), page, sheets, ExportSettings.Fine with { Kind = ExportKind.Tiles, Gpu = true });
    }

    private static void AssertRed(string directory, ExportPage page, ExportSheet[] sheets, ExportSettings settings)
    {
        PageExport.Write(directory, page, sheets, settings, null, default);
        using var image = SKBitmap.Decode(Path.Combine(directory, "tiles", "0", "0", "0.jpg"));
        var pixel = image.GetPixel(image.Width / 2, image.Height / 2);
        Assert.True(pixel.Red > 180 && pixel.Blue < 80, pixel.ToString());
    }

    [Fact]
    public void A_warped_sheet_has_no_white_rays_across_the_scan()
    {
        var red = WriteSolid(Path.Combine(_root, "ray.png"), 80, 80, new SKColor(220, 30, 30));
        var sheet = new MapFragment(red, 0, 0, 80, 80);
        sheet.EnsureWarp();
        sheet.Warp[1] = new CropPoint(0, 8);
        sheet.Warp[2] = new CropPoint(-6, -4);
        Assert.True(ExportPage.TryFrom([sheet], out var page));
        var png = Path.Combine(_root, "ray-out.png");
        PageExport.Write(png, page, [ExportSheet.From(sheet)]);
        using var image = SKBitmap.Decode(png);
        var scale = image.Width / page.Size;
        var pale = 0;
        for (var y = 24; y < 56; y++)
        {
            for (var x = 24; x < 56; x++)
            {
                var pixel = At(image, page, scale, x, y);
                if (pixel.Red < 180)
                    pale++;
            }
        }

        Assert.Equal(0, pale);
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

    private static List<PdfObject> AssertPdfStreams(byte[] pdf)
    {
        var objects = PdfObjects(pdf);
        var text = Encoding.Latin1.GetString(pdf);
        var content = objects.Single(item => item.Data.AsSpan().StartsWith("1 1 1 rg"u8));
        Assert.Matches(new Regex(@"/Length \d+"), content.Dict);
        foreach (var item in objects.Where(item => item.Data.Length > 0))
        {
            var marker = $"\n{item.Id} 0 obj\n";
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0);
            var stream = text.IndexOf("\nstream\n", at, StringComparison.Ordinal);
            var dataAt = stream + "\nstream\n".Length;
            Assert.True(pdf.AsSpan(dataAt, item.Data.Length).SequenceEqual(item.Data));
            Assert.True(pdf.AsSpan(dataAt + item.Data.Length).StartsWith("\nendstream\n"u8));
        }

        return objects;
    }

    private static List<PdfObject> PdfObjects(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var objects = new List<PdfObject>();
        var index = 0;
        while (index < text.Length)
        {
            var marker = text.IndexOf(" 0 obj\n", index, StringComparison.Ordinal);
            if (marker < 0)
                break;

            var idStart = marker;
            while (idStart > 0 && char.IsDigit(text[idStart - 1]))
                idStart--;
            if (idStart > 0 && text[idStart - 1] != '\n')
            {
                index = marker + 1;
                continue;
            }

            var id = int.Parse(text[idStart..marker], CultureInfo.InvariantCulture);
            var body = marker + " 0 obj\n".Length;
            var streamAt = text.IndexOf("\nstream\n", body, StringComparison.Ordinal);
            var endAt = text.IndexOf("\nendobj\n", body, StringComparison.Ordinal);
            if (streamAt >= 0 && (endAt < 0 || streamAt < endAt))
            {
                var dict = text[body..streamAt];
                var length = StreamLength(dict);
                var dataAt = streamAt + "\nstream\n".Length;
                objects.Add(new PdfObject(id, dict, pdf.AsSpan(dataAt, length).ToArray()));
                index = dataAt + length;
            }
            else
            {
                var dictEnd = endAt < 0 ? text.Length : endAt;
                objects.Add(new PdfObject(id, text[body..dictEnd], []));
                index = dictEnd + 1;
            }
        }

        return objects;
    }

    private static int StreamLength(string dict)
    {
        var at = dict.IndexOf("/Length ", StringComparison.Ordinal);
        Assert.True(at >= 0, dict);
        var start = at + "/Length ".Length;
        var end = start;
        while (end < dict.Length && char.IsDigit(dict[end]))
            end++;
        Assert.True(end > start, dict);
        return int.Parse(dict[start..end], CultureInfo.InvariantCulture);
    }

    private static SKBitmap CompositePdf(List<PdfObject> objects, int side)
    {
        var content = Encoding.ASCII.GetString(objects.Single(item => item.Data.AsSpan().StartsWith("1 1 1 rg"u8)).Data);
        var images = objects.Where(item => item.Dict.Contains("/DCTDecode", StringComparison.Ordinal)).ToList();
        var page = new SKBitmap(side, side, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(page))
            canvas.Clear(SKColors.White);

        foreach (Match match in Regex.Matches(content, @"q\n([\d.]+) 0 0 ([\d.]+) (-?[\d.]+) (-?[\d.]+) cm\n/Im(\d+) Do\nQ\n"))
        {
            var width = (int)float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var height = (int)float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var pdfX = float.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            var pdfY = float.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            var image = images[int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture)];
            var maskId = int.Parse(Regex.Match(image.Dict, @"/SMask (\d+) 0 R").Groups[1].Value, CultureInfo.InvariantCulture);
            var mask = objects.Single(item => item.Id == maskId);
            var matte = mask.Dict.Contains("/Matte [1 1 1]", StringComparison.Ordinal);
            Blit(page, image.Data, Inflate(mask.Data), pdfX, pdfY, width, height, side, matte);
        }

        return page;
    }

    private static void Blit(SKBitmap page, byte[] jpeg, byte[] alpha, float pdfX, float pdfY, int width, int height, int side, bool matte)
    {
        using var shot = SKBitmap.Decode(jpeg);
        Assert.Equal(width, shot.Width);
        Assert.Equal(height, shot.Height);
        Assert.Equal(width * height, alpha.Length);
        var originX = (int)Math.Round(pdfX);
        var originY = (int)Math.Round(side - (pdfY + height));
        for (var iy = 0; iy < height; iy++)
        {
            var y = originY + iy;
            for (var ix = 0; ix < width; ix++)
            {
                var x = originX + ix;
                if (x < 0 || y < 0 || x >= page.Width || y >= page.Height)
                    continue;

                var coverage = alpha[iy * width + ix] / 255f;
                var source = shot.GetPixel(ix, iy);
                var destination = page.GetPixel(x, y);
                page.SetPixel(x, y, new SKColor(
                    Mix(source.Red, destination.Red, coverage, matte),
                    Mix(source.Green, destination.Green, coverage, matte),
                    Mix(source.Blue, destination.Blue, coverage, matte)));
            }
        }
    }

    private static byte Mix(byte source, byte destination, float coverage, bool matte)
    {
        var value = matte
            ? source - 255f * (1f - coverage) + destination * (1f - coverage)
            : source * coverage + destination * (1f - coverage);
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private sealed record PdfObject(int Id, string Dict, byte[] Data);

    private static string WriteSolid(string path, int width, int height, SKColor color)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
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
