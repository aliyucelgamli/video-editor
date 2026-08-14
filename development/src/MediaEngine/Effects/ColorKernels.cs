namespace VideoEditor.MediaEngine.Effects;

/// <summary>Black &amp; white. amount 0..1 blends between original and grayscale.</summary>
public sealed class GrayscaleKernel : IVideoKernel
{
    public string Key => "grayscale";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var amount = Math.Clamp(args.Get("amount", 1), 0, 1);
        if (amount <= 0) return;

        for (var i = 0; i < bgra.Length; i += 4)
        {
            var gray = 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
            bgra[i] = KernelArgs.ClampByte(bgra[i] + (gray - bgra[i]) * amount);
            bgra[i + 1] = KernelArgs.ClampByte(bgra[i + 1] + (gray - bgra[i + 1]) * amount);
            bgra[i + 2] = KernelArgs.ClampByte(bgra[i + 2] + (gray - bgra[i + 2]) * amount);
        }
    }
}

/// <summary>Vintage brown tone. amount 0..1.</summary>
public sealed class SepiaKernel : IVideoKernel
{
    public string Key => "sepia";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var amount = Math.Clamp(args.Get("amount", 1), 0, 1);
        if (amount <= 0) return;

        for (var i = 0; i < bgra.Length; i += 4)
        {
            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            var sr = 0.393 * r + 0.769 * g + 0.189 * b;
            var sg = 0.349 * r + 0.686 * g + 0.168 * b;
            var sb = 0.272 * r + 0.534 * g + 0.131 * b;
            bgra[i] = KernelArgs.ClampByte(b + (sb - b) * amount);
            bgra[i + 1] = KernelArgs.ClampByte(g + (sg - g) * amount);
            bgra[i + 2] = KernelArgs.ClampByte(r + (sr - r) * amount);
        }
    }
}

/// <summary>Warm/cold shift. amount -1 (cold) .. +1 (warm).</summary>
public sealed class TemperatureKernel : IVideoKernel
{
    public string Key => "temperature";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var amount = Math.Clamp(args.Get("amount", 0), -1, 1);
        if (Math.Abs(amount) < 0.001) return;

        var shift = amount * 40.0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = KernelArgs.ClampByte(bgra[i] - shift);       // blue
            bgra[i + 2] = KernelArgs.ClampByte(bgra[i + 2] + shift); // red
        }
    }
}

/// <summary>Brightness. amount -1..+1 (adds up to ±100% of full scale).</summary>
public sealed class BrightnessKernel : IVideoKernel
{
    public string Key => "brightness";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var amount = Math.Clamp(args.Get("amount", 0), -1, 1);
        if (Math.Abs(amount) < 0.001) return;

        var offset = amount * 255.0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = KernelArgs.ClampByte(bgra[i] + offset);
            bgra[i + 1] = KernelArgs.ClampByte(bgra[i + 1] + offset);
            bgra[i + 2] = KernelArgs.ClampByte(bgra[i + 2] + offset);
        }
    }
}

/// <summary>Contrast. amount -1..+1 around the mid gray point.</summary>
public sealed class ContrastKernel : IVideoKernel
{
    public string Key => "contrast";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var amount = Math.Clamp(args.Get("amount", 0), -1, 1);
        if (Math.Abs(amount) < 0.001) return;

        var factor = Math.Tan((amount + 1) * Math.PI / 4);
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = KernelArgs.ClampByte((bgra[i] - 128) * factor + 128);
            bgra[i + 1] = KernelArgs.ClampByte((bgra[i + 1] - 128) * factor + 128);
            bgra[i + 2] = KernelArgs.ClampByte((bgra[i + 2] - 128) * factor + 128);
        }
    }
}

/// <summary>Saturation. amount 0 (grayscale) .. 1 (original) .. 2 (boosted).</summary>
public sealed class SaturationKernel : IVideoKernel
{
    public string Key => "saturation";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var amount = Math.Clamp(args.Get("amount", 1), 0, 4);
        if (Math.Abs(amount - 1) < 0.001) return;

        for (var i = 0; i < bgra.Length; i += 4)
        {
            var gray = 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
            bgra[i] = KernelArgs.ClampByte(gray + (bgra[i] - gray) * amount);
            bgra[i + 1] = KernelArgs.ClampByte(gray + (bgra[i + 1] - gray) * amount);
            bgra[i + 2] = KernelArgs.ClampByte(gray + (bgra[i + 2] - gray) * amount);
        }
    }
}

/// <summary>Color negative. amount 0..1 blends toward the inverted image.</summary>
public sealed class InvertKernel : IVideoKernel
{
    public string Key => "invert";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var amount = Math.Clamp(args.Get("amount", 1), 0, 1);
        if (amount <= 0) return;

        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = KernelArgs.ClampByte(bgra[i] + (255 - 2 * bgra[i]) * amount);
            bgra[i + 1] = KernelArgs.ClampByte(bgra[i + 1] + (255 - 2 * bgra[i + 1]) * amount);
            bgra[i + 2] = KernelArgs.ClampByte(bgra[i + 2] + (255 - 2 * bgra[i + 2]) * amount);
        }
    }
}
