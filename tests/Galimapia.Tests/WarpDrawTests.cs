using SkiaSharp;

namespace Galimapia.Tests;

public class WarpDrawTests
{
    [Fact]
    public void Four_meshes_meeting_at_one_point_do_not_crash()
    {
        var info = new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        using var image = MakeImage();
        using var filter = SKColorFilter.CreateColorMatrix(
        [
            1f, 0, 0, 0, 0,
            0, 1f, 0, 0, 0,
            0, 0, 1f, 0, 0,
            0, 0, 0, 1f, 0,
        ]);

        for (var n = 0; n < 4; n++)
        {
            using var shader = SKShader.CreateImage(image, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            using var paint = new SKPaint
            {
                Shader = shader,
                ColorFilter = filter,
                Color = SKColors.White,
                IsAntialias = true,
            };
            var origin = n switch
            {
                0 => new SKPoint(8, 8),
                1 => new SKPoint(40, 8),
                2 => new SKPoint(8, 40),
                _ => new SKPoint(40, 40),
            };
            var meet = new SKPoint(32, 32);
            var far = new SKPoint(origin.X == 8 ? 4 : 60, origin.Y == 8 ? 4 : 60);
            var mid = new SKPoint(origin.X, origin.Y == 8 ? 4 : 60);
            SKPoint[] vertices =
            [
                origin, mid, meet,
                origin, meet, far,
            ];
            SKPoint[] texture =
            [
                new(0, 0), new(8, 0), new(8, 8),
                new(0, 0), new(8, 8), new(0, 8),
            ];
            canvas.DrawVertices(SKVertexMode.Triangles, vertices, texture, null, paint);
        }

        canvas.Flush();
        using var snapshot = surface.Snapshot();
        Assert.True(snapshot.Width == 64);
    }

    [Fact]
    public void Disposing_a_paint_does_not_destroy_a_shared_filter()
    {
        using var filter = SKColorFilter.CreateColorMatrix(
        [
            1.2f, 0, 0, 0, 0.1f,
            0, 1.2f, 0, 0, 0.1f,
            0, 0, 1.2f, 0, 0.1f,
            0, 0, 0, 1f, 0,
        ]);
        var paint = new SKPaint { ColorFilter = filter };
        paint.Dispose();
        using var again = new SKPaint { ColorFilter = filter };
        Assert.NotNull(again);
    }

    private static SKImage MakeImage()
    {
        var info = new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Red);
        return surface.Snapshot();
    }
}
