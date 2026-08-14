namespace VideoEditor.MediaEngine.Effects;

/// <summary>
/// A pixel-processing routine ("shader"): transforms one BGRA frame in place.
/// Kernels are stateless and composable; effects (built-in or .vefx) are
/// pipelines of kernels with resolved arguments.
/// </summary>
public interface IVideoKernel
{
    /// <summary>Stable kernel key referenced by effect steps (e.g. "blur").</summary>
    string Key { get; }

    void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args);
}

/// <summary>Shared helpers for kernels.</summary>
public static class KernelArgs
{
    public static double Get(this IReadOnlyDictionary<string, double> args, string key, double fallback) =>
        args.TryGetValue(key, out var value) ? value : fallback;

    public static byte ClampByte(double value) => (byte)Math.Clamp(value, 0, 255);
}
