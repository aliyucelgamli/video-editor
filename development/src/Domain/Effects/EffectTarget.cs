namespace VideoEditor.Domain.Effects;

/// <summary>
/// What kind of timeline content an effect can attach to.
/// An effect declares its targets; the UI only offers it on compatible events.
/// </summary>
[Flags]
public enum EffectTarget
{
    None = 0,
    Video = 1,
    Audio = 2,
    Image = 4,

    /// <summary>Anything rendered as pixels (video + image).</summary>
    Visual = Video | Image,

    All = Video | Audio | Image
}

public static class EffectTargets
{
    /// <summary>Maps a media type to the effect target flag it satisfies.</summary>
    public static EffectTarget ForMediaType(MediaType type) => type switch
    {
        MediaType.Video => EffectTarget.Video,
        MediaType.Audio => EffectTarget.Audio,
        _ => EffectTarget.Image
    };
}
