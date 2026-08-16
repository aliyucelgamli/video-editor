using VideoEditor.Domain;

namespace VideoEditor.Application.Editing;

/// <summary>
/// Which lane may hold what. One home for the rule, used by media drops, by
/// dragging a clip to another lane, and by the automatic routing that creates
/// a lane of the right kind when none exists.
/// </summary>
public static class TrackRouting
{
    /// <summary>Audio lanes take sound; video and overlay lanes take pictures.</summary>
    public static bool Accepts(MediaType media, TrackType track) => track switch
    {
        TrackType.Audio => media == MediaType.Audio,
        _ => media is MediaType.Video or MediaType.Image
    };

    /// <summary>
    /// Whether a clip that already exists can live on this lane. Titles carry
    /// no media item, so they are judged on being visual.
    /// </summary>
    public static bool Accepts(Project project, Track track, TimelineEvent evt)
    {
        if (evt.Text != null) return track.Type != TrackType.Audio;
        var media = project.Media.FindById(evt.MediaId);
        return media != null && Accepts(media.Type, track.Type);
    }

    /// <summary>The lane kind a media item belongs on when a new one must be created.</summary>
    public static TrackType LaneKindFor(MediaType media) =>
        media == MediaType.Audio ? TrackType.Audio : TrackType.Video;
}
