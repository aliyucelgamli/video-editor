namespace VideoEditor.Application.Settings;

/// <summary>
/// Preview canvas presets. The preview composes at this width and the monitor
/// scales the result up, so dropping the width from 960 to 480 quarters the
/// pixel work of every blend, transform and effect — the cheapest lever there
/// is on playback smoothness. Presets live here so any UI can list them.
/// </summary>
public sealed record PreviewQuality(string Name, int Width, string Description)
{
    public static readonly PreviewQuality Draft =
        new("Draft", 480, "Fastest playback; titles look soft while scrubbing");

    public static readonly PreviewQuality Normal =
        new("Normal", 640, "Balanced — recommended");

    public static readonly PreviewQuality High =
        new("High", 960, "Sharpest preview; noticeably heavier to play back");

    /// <summary>Every preset, cheapest first.</summary>
    public static readonly IReadOnlyList<PreviewQuality> All = new[] { Draft, Normal, High };

    /// <summary>Nearest preset to a stored width (settings survive preset changes).</summary>
    public static PreviewQuality ForWidth(int width) =>
        All.OrderBy(quality => Math.Abs(quality.Width - width)).First();

    public override string ToString() => $"{Name} — {Width}px  ({Description})";
}
