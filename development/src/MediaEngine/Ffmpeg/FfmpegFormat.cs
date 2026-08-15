using System.Globalization;

namespace VideoEditor.MediaEngine.Ffmpeg;

/// <summary>
/// Culture-invariant number formatting for everything that reaches FFmpeg
/// command lines and filter strings — the single home for the rule that
/// media numbers never use the OS culture.
/// </summary>
public static class FfmpegFormat
{
    /// <summary>Seconds, sizes, rates: up to three decimals.</summary>
    public static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Filter parameters that need extra precision (audio ratios).</summary>
    public static string PreciseNumber(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
