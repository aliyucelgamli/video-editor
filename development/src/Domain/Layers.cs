namespace VideoEditor.Domain;

/// <summary>
/// Z-order rules for compositing. A clip's layer decides what covers what:
/// higher layers render on top, in preview and in the export alike. Defaults
/// follow how footage is normally stacked — video at the bottom, stills and
/// graphics over it, titles on top — and every clip can be moved afterwards.
/// A track's layer is added to its clips, so a whole lane can be lifted.
/// </summary>
public static class Layers
{
    public const int Video = 0;
    public const int Image = 1;
    public const int Text = 2;

    public const int Min = -100;
    public const int Max = 100;

    /// <summary>The layer a newly placed clip starts on.</summary>
    public static int DefaultFor(MediaType mediaType) => mediaType switch
    {
        MediaType.Image => Image,
        _ => Video
    };

    /// <summary>Track layer + clip layer, clamped to the supported range.</summary>
    public static int Effective(Track track, TimelineEvent evt) =>
        Math.Clamp(track.Layer + evt.Layer, Min * 2, Max * 2);

    public static int Clamp(int layer) => Math.Clamp(layer, Min, Max);
}
