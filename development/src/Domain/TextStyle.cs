using System.Globalization;

namespace VideoEditor.Domain;

/// <summary>
/// Styling of a generated text (title) event. Text events have no source
/// media (<see cref="TimelineEvent.MediaId"/> is empty); the UI rasterizes
/// this style into the frame cache and the compositor layers it like any
/// other visual. Position/scale come from the event's Transform.
/// </summary>
public class TextStyle
{
    public string Content { get; set; } = "Title";
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Font size in project pixels — scales with the export resolution.</summary>
    public double FontSize { get; set; } = 96;

    public string Color { get; set; } = "#FFFFFFFF";
    public string OutlineColor { get; set; } = "#FF000000";

    /// <summary>Outline thickness in project pixels; 0 = no outline.</summary>
    public double OutlineWidth { get; set; } = 3;

    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }

    public TextStyle Clone() => (TextStyle)MemberwiseClone();

    /// <summary>Stable identity of the rendered raster (frame cache key part).</summary>
    public string CacheKey => string.Join("|",
        Content, FontFamily,
        FontSize.ToString("0.##", CultureInfo.InvariantCulture),
        Color, OutlineColor,
        OutlineWidth.ToString("0.##", CultureInfo.InvariantCulture),
        Bold, Italic);
}
