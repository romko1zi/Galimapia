using SkiaSharp;

namespace Galimapia.Canvas;

/// <summary>
/// Brightness, contrast, and hue applied while drawing. The source file is not rewritten.
/// Brightness is -1..1, contrast is 0..2, hue is degrees. Zero, one, and zero are the original scan.
/// </summary>
public static class ColorAdjust
{
    public static bool IsIdentity(float brightness, float contrast, float hue) =>
        Math.Abs(brightness) < 1e-4f && Math.Abs(contrast - 1f) < 1e-4f && Math.Abs(hue) < 0.05f;

    public static float[] Matrix(float brightness, float contrast, float hue)
    {
        var tone = Tone(brightness, contrast);
        if (Math.Abs(hue) < 0.05f)
            return tone;

        return Multiply(tone, Hue(hue));
    }

    public static SKColorFilter? CreateFilter(float brightness, float contrast, float hue)
    {
        if (IsIdentity(brightness, contrast, hue))
            return null;

        return SKColorFilter.CreateColorMatrix(Matrix(brightness, contrast, hue));
    }

    private static float[] Tone(float brightness, float contrast)
    {
        var shift = (1f - contrast) * 0.5f + brightness;
        return
        [
            contrast, 0, 0, 0, shift,
            0, contrast, 0, 0, shift,
            0, 0, contrast, 0, shift,
            0, 0, 0, 1, 0,
        ];
    }

    private static float[] Hue(float degrees)
    {
        var radians = degrees * (MathF.PI / 180f);
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        const float lumR = 0.213f;
        const float lumG = 0.715f;
        const float lumB = 0.072f;
        return
        [
            lumR + cos * (1f - lumR) + sin * (-lumR), lumG + cos * (-lumG) + sin * (-lumG), lumB + cos * (-lumB) + sin * (1f - lumB), 0, 0,
            lumR + cos * (-lumR) + sin * 0.143f, lumG + cos * (1f - lumG) + sin * 0.140f, lumB + cos * (-lumB) + sin * -0.283f, 0, 0,
            lumR + cos * (-lumR) + sin * -(1f - lumR), lumG + cos * (-lumG) + sin * lumG, lumB + cos * (1f - lumB) + sin * lumB, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    private static float[] Multiply(float[] left, float[] right)
    {
        var result = new float[20];
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                result[row * 5 + column] =
                    left[row * 5] * right[column]
                    + left[row * 5 + 1] * right[5 + column]
                    + left[row * 5 + 2] * right[10 + column]
                    + left[row * 5 + 3] * right[15 + column];
            }

            result[row * 5 + 4] =
                left[row * 5] * right[4]
                + left[row * 5 + 1] * right[9]
                + left[row * 5 + 2] * right[14]
                + left[row * 5 + 3] * right[19]
                + left[row * 5 + 4];
        }

        return result;
    }
}
