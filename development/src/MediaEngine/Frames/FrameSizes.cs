namespace VideoEditor.MediaEngine.Frames;

/// <summary>Shared frame-size math (preview monitors, editor stages).</summary>
public static class FrameSizes
{
    /// <summary>
    /// A render size that keeps the project's aspect ratio, capped at
    /// <paramref name="maxWidth"/>, with both dimensions even (every pixel
    /// format downstream is happy with even sizes).
    /// </summary>
    public static (int Width, int Height) FitWithin(int projectWidth, int projectHeight, int maxWidth)
    {
        var aspect = projectWidth > 0 && projectHeight > 0
            ? (double)projectHeight / projectWidth
            : 9.0 / 16.0;
        var width = Math.Min(maxWidth, Math.Max(64, projectWidth));
        var height = (int)Math.Round(width * aspect);
        return (width - width % 2, Math.Max(2, height - height % 2));
    }
}
