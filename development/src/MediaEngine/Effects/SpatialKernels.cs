namespace VideoEditor.MediaEngine.Effects;

/// <summary>
/// Blur — three-pass box blur (a close gaussian approximation) applied
/// separably per axis, which keeps it O(n) regardless of radius.
/// radius = pixels at the frame's own resolution.
/// </summary>
public sealed class BlurKernel : IVideoKernel
{
    public string Key => "blur";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var radius = (int)Math.Round(Math.Clamp(args.Get("radius", 4), 0, 100));
        if (radius < 1) return;

        var temp = new byte[bgra.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxBlurHorizontal(bgra, temp, width, height, radius);
            BoxBlurVertical(temp, bgra, width, height, radius);
        }
    }

    private static void BoxBlurHorizontal(byte[] source, byte[] target, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

            for (var x = -radius; x <= radius; x++)
            {
                var index = row + Math.Clamp(x, 0, width - 1) * 4;
                sumB += source[index];
                sumG += source[index + 1];
                sumR += source[index + 2];
                sumA += source[index + 3];
            }

            for (var x = 0; x < width; x++)
            {
                var index = row + x * 4;
                target[index] = (byte)(sumB / window);
                target[index + 1] = (byte)(sumG / window);
                target[index + 2] = (byte)(sumR / window);
                target[index + 3] = (byte)(sumA / window);

                var addIndex = row + Math.Clamp(x + radius + 1, 0, width - 1) * 4;
                var removeIndex = row + Math.Clamp(x - radius, 0, width - 1) * 4;
                sumB += source[addIndex] - source[removeIndex];
                sumG += source[addIndex + 1] - source[removeIndex + 1];
                sumR += source[addIndex + 2] - source[removeIndex + 2];
                sumA += source[addIndex + 3] - source[removeIndex + 3];
            }
        }
    }

    private static void BoxBlurVertical(byte[] source, byte[] target, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        var stride = width * 4;
        for (var x = 0; x < width; x++)
        {
            var column = x * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

            for (var y = -radius; y <= radius; y++)
            {
                var index = Math.Clamp(y, 0, height - 1) * stride + column;
                sumB += source[index];
                sumG += source[index + 1];
                sumR += source[index + 2];
                sumA += source[index + 3];
            }

            for (var y = 0; y < height; y++)
            {
                var index = y * stride + column;
                target[index] = (byte)(sumB / window);
                target[index + 1] = (byte)(sumG / window);
                target[index + 2] = (byte)(sumR / window);
                target[index + 3] = (byte)(sumA / window);

                var addIndex = Math.Clamp(y + radius + 1, 0, height - 1) * stride + column;
                var removeIndex = Math.Clamp(y - radius, 0, height - 1) * stride + column;
                sumB += source[addIndex] - source[removeIndex];
                sumG += source[addIndex + 1] - source[removeIndex + 1];
                sumR += source[addIndex + 2] - source[removeIndex + 2];
                sumA += source[addIndex + 3] - source[removeIndex + 3];
            }
        }
    }
}

/// <summary>Vignette — darkens toward the corners. amount 0..1.</summary>
public sealed class VignetteKernel : IVideoKernel
{
    public string Key => "vignette";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var amount = Math.Clamp(args.Get("amount", 0.5), 0, 1);
        if (amount <= 0) return;

        var centerX = (width - 1) / 2.0;
        var centerY = (height - 1) / 2.0;
        var maxDistance = Math.Sqrt(centerX * centerX + centerY * centerY);

        for (var y = 0; y < height; y++)
        {
            var dy = y - centerY;
            for (var x = 0; x < width; x++)
            {
                var dx = x - centerX;
                var distance = Math.Sqrt(dx * dx + dy * dy) / maxDistance;
                // Keep the center clean, ramp darkening toward the edges.
                var falloff = Math.Max(0, distance - 0.35) / 0.65;
                var factor = 1.0 - amount * falloff * falloff;

                var index = (y * width + x) * 4;
                bgra[index] = (byte)(bgra[index] * factor);
                bgra[index + 1] = (byte)(bgra[index + 1] * factor);
                bgra[index + 2] = (byte)(bgra[index + 2] * factor);
            }
        }
    }
}
