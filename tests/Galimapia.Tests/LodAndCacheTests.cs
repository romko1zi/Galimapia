using Galimapia.Canvas;
using SkiaSharp;

namespace Galimapia.Tests;

public class LodAndCacheTests
{
    [Fact]
    public void Overview_picks_512_and_a_close_view_keeps_it_until_the_sheet_grows()
    {
        var levels = Levels(4000, 512, 1024, 2048);

        var far = LodPlan.Choose(levels, needed: 80, heldLongSide: null);
        Assert.Equal(512, far.LongSide);

        var still = LodPlan.Choose(levels, needed: 400, heldLongSide: 512);
        Assert.Equal(512, still.LongSide);

        var closer = LodPlan.Choose(levels, needed: 900, heldLongSide: 512);
        Assert.Equal(1024, closer.LongSide);

        var tight = LodPlan.Choose(levels, needed: 3000, heldLongSide: 2048);
        Assert.Equal(4000, tight.LongSide);
        Assert.True(tight.IsFull);
    }

    [Fact]
    public void Half_step_is_added_only_above_2048()
    {
        Assert.Equal([512, 1024, 2048], LodPlan.ReducedLongSides(4000));
        Assert.Equal([512, 1024, 2048, 3000], LodPlan.ReducedLongSides(6000));
        Assert.Empty(LodPlan.ReducedLongSides(400));
    }

    [Fact]
    public void Fit_keeps_the_rectangle_inside_the_view()
    {
        var camera = new Camera();
        camera.Fit(0, 0, 10_000, 5_000, viewWidth: 1000, viewHeight: 800, margin: 40);

        var topLeft = camera.ScreenToWorld(0, 0, 1000, 800);
        var bottomRight = camera.ScreenToWorld(1000, 800, 1000, 800);
        Assert.True(topLeft.X <= 0);
        Assert.True(topLeft.Y <= 0);
        Assert.True(bottomRight.X >= 10_000);
        Assert.True(bottomRight.Y >= 5_000);
    }

    [Fact]
    public void Cache_drops_the_oldest_image_and_a_lease_keeps_it_alive()
    {
        using var cache = new ImageCache(budget: 512);
        var first = Solid(1);
        cache.Add("a", first, 256);
        var lease = cache.TryLease("a");
        Assert.NotNull(lease);

        cache.Add("b", Solid(2), 256);
        cache.Add("c", Solid(3), 256);

        Assert.False(cache.Contains("a"));
        Assert.False(cache.Contains("b"));
        Assert.True(cache.Contains("c"));
        Assert.NotEqual(IntPtr.Zero, lease.Image.Handle);

        lease.Dispose();
        Assert.Equal(IntPtr.Zero, lease.Image.Handle);
        Assert.Equal(256, cache.LiveBytes);
    }

    private static IReadOnlyList<LodFile> Levels(int full, params int[] reduced)
    {
        var levels = reduced.Select(side => new LodFile(side, side + ".jpg", isFull: false)).ToList();
        levels.Add(new LodFile(full, "source.jpg", isFull: true));
        return levels;
    }

    private static SKImage Solid(byte red)
    {
        var info = new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(red, 0, 0));
        using var pixmap = bitmap.PeekPixels();
        return SKImage.FromPixelCopy(pixmap);
    }
}
