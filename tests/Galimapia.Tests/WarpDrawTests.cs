using System.Reflection;
using System.Reflection.Emit;
using Galimapia.Canvas;
using SkiaSharp;

namespace Galimapia.Tests;

public class WarpDrawTests
{
    [Fact]
    public void The_app_draws_vertices_only_from_an_SKVertices_it_owns()
    {
        var calls = CallsTo(typeof(MapScene).Assembly, typeof(SKCanvas), nameof(SKCanvas.DrawVertices));

        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Equal(typeof(SKVertices), call.GetParameters()[0].ParameterType));
    }

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

    private static List<MethodBase> CallsTo(Assembly assembly, Type owner, string name)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => (ushort)code.Value);
        var found = new List<MethodBase>();
        foreach (var type in assembly.GetTypes())
        {
            var typeArguments = type.IsGenericType ? type.GetGenericArguments() : null;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null)
                    continue;

                var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
                var i = 0;
                while (i < il.Length)
                {
                    var value = il[i] == 0xFE ? (ushort)(0xFE00 | il[i + 1]) : il[i];
                    i += il[i] == 0xFE ? 2 : 1;
                    var code = opcodes[value];
                    if (code.OperandType == OperandType.InlineMethod
                        && method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArguments, methodArguments) is { } target
                        && target.DeclaringType == owner
                        && target.Name == name)
                        found.Add(target);

                    i += code.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                        _ => 4,
                    };
                }
            }
        }

        return found;
    }

    private static SKImage MakeImage()
    {
        var info = new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Red);
        return surface.Snapshot();
    }
}
