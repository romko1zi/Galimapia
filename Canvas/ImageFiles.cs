using SkiaSharp;

namespace Galimapia.Canvas;

static class ImageFiles
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff",
    };

    public static bool IsImage(string path) =>
        Extensions.Contains(Path.GetExtension(path));

    public static bool TryReadSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = Open(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                return false;

            width = codec.Info.Width;
            height = codec.Info.Height;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static SKImage DecodeLongSide(string path, int maxLongSide)
    {
        if (!TryReadSize(path, out var width, out var height))
            throw new InvalidDataException($"Cannot read {path}.");

        var sourceLong = Math.Max(width, height);
        if (maxLongSide <= 0 || sourceLong <= maxLongSide)
            return Decode(path);

        using var stream = Open(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException($"Cannot read {path}.");
        var desired = (float)maxLongSide / sourceLong;
        var scaled = codec.GetScaledDimensions(Math.Clamp(desired, 0.01f, 1f));
        if (scaled.Width <= 0 || scaled.Height <= 0)
            return Decode(path);

        var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.InvalidScale)
            return Decode(path);
        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            throw new InvalidDataException($"Cannot decode {path} ({result}).");

        using var pixmap = bitmap.PeekPixels() ?? throw new InvalidDataException($"Cannot read pixels of {path}.");
        return SKImage.FromPixelCopy(pixmap);
    }

    public static SKImage Decode(string path)
    {
        using var stream = Open(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException($"Cannot read {path}.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            throw new InvalidDataException($"Cannot decode {path} ({result}).");

        using var pixmap = bitmap.PeekPixels() ?? throw new InvalidDataException($"Cannot read pixels of {path}.");
        return SKImage.FromPixelCopy(pixmap);
    }

    public static void WriteScaledJpeg(string source, string destination, int targetLongSide, int sourceWidth, int sourceHeight)
    {
        using var stream = Open(source);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException($"Cannot read {source}.");
        var sourceLong = Math.Max(sourceWidth, sourceHeight);
        var desired = sourceLong <= 0 ? 1f : (float)targetLongSide / sourceLong;
        var scaled = codec.GetScaledDimensions(Math.Clamp(desired, 0.01f, 1f));
        if (scaled.Width <= 0 || scaled.Height <= 0)
            scaled = new SKSizeI(sourceWidth, sourceHeight);

        var decodedInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodedInfo);
        var result = codec.GetPixels(decodedInfo, decoded.GetPixels());
        if (result is SKCodecResult.InvalidScale)
        {
            WriteFullThenScale(source, destination, targetLongSide, sourceWidth, sourceHeight);
            return;
        }

        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            throw new InvalidDataException($"Cannot decode {source} ({result}).");

        EncodeFitted(decoded, destination, targetLongSide, sourceWidth, sourceHeight);
    }

    private static void WriteFullThenScale(string source, string destination, int targetLongSide, int sourceWidth, int sourceHeight)
    {
        using var stream = Open(source);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException($"Cannot read {source}.");
        var info = new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            throw new InvalidDataException($"Cannot decode {source} ({result}).");

        EncodeFitted(bitmap, destination, targetLongSide, sourceWidth, sourceHeight);
    }

    private static void EncodeFitted(SKBitmap decoded, string destination, int targetLongSide, int sourceWidth, int sourceHeight)
    {
        FitSize(sourceWidth, sourceHeight, targetLongSide, out var targetWidth, out var targetHeight);
        SKBitmap? resized = null;
        try
        {
            var sourceBitmap = decoded;
            if (decoded.Width != targetWidth || decoded.Height != targetHeight)
            {
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                resized = decoded.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul), sampling)
                    ?? throw new InvalidDataException($"Cannot resize {destination}.");
                sourceBitmap = resized;
            }

            using var pixmap = sourceBitmap.PeekPixels() ?? throw new InvalidDataException($"Cannot read pixels of {destination}.");
            using var image = SKImage.FromPixelCopy(pixmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80)
                ?? throw new InvalidDataException($"Cannot encode {destination}.");
            var temporary = destination + ".tmp";
            using (var output = File.Create(temporary))
                data.SaveTo(output);

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    public static void FitSize(int sourceWidth, int sourceHeight, int targetLongSide, out int width, out int height)
    {
        if (sourceWidth >= sourceHeight)
        {
            width = targetLongSide;
            height = Math.Max(1, (int)Math.Round(sourceHeight * (double)targetLongSide / sourceWidth));
        }
        else
        {
            height = targetLongSide;
            width = Math.Max(1, (int)Math.Round(sourceWidth * (double)targetLongSide / sourceHeight));
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}
