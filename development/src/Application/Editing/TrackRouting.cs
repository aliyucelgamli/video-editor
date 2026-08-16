using VideoEditor.Domain;

namespace VideoEditor.Application.Editing;

/// <summary>
/// Which lane may hold what. One home for the rule, used by media drops, by
/// dragging a clip to another lane, by paste/duplicate, and by the automatic
/// routing that creates a lane of the right kind when none exists.
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
    /// What a clip needs from a lane.
    ///
    /// The media item alone cannot answer this: importing a video with sound
    /// creates TWO events that reference the SAME item — the picture on a video
    /// lane and its linked sound on an audio lane. Reading the kind off the
    /// media would call that sound "video" and route it onto a picture lane.
    /// The lane a clip currently lives on is what settles it.
    /// </summary>
    public readonly record struct ClipKind(bool IsText, bool HasMedia, MediaType Media)
    {
        public static ClipKind Of(Project project, Track track, TimelineEvent evt)
        {
            if (evt.Text != null) return new ClipKind(IsText: true, HasMedia: false, MediaType.Image);

            var media = project.Media.FindById(evt.MediaId);
            if (media is null) return new ClipKind(IsText: false, HasMedia: false, MediaType.Video);

            var kind = track.Type == TrackType.Audio ? MediaType.Audio : media.Type;
            return new ClipKind(IsText: false, HasMedia: true, kind);
        }

        /// <summary>Titles go on any visual lane; media follows its own kind.</summary>
        public bool FitsOn(TrackType lane) =>
            IsText ? lane != TrackType.Audio
                : HasMedia && Accepts(Media, lane);
    }

    /// <summary>
    /// Whether <paramref name="target"/> can hold a clip that currently lives on
    /// <paramref name="source"/>. Pass the clip's own lane as the source when
    /// asking about a clip that is already on the timeline.
    /// </summary>
    public static bool Accepts(Project project, Track target, TimelineEvent evt, Track source) =>
        ClipKind.Of(project, source, evt).FitsOn(target.Type);

    /// <summary>The lane kind a media item belongs on when a new one must be created.</summary>
    public static TrackType LaneKindFor(MediaType media) =>
        media == MediaType.Audio ? TrackType.Audio : TrackType.Video;

    /// <summary>The lane kind a clip of this shape needs when none exists yet.</summary>
    public static TrackType LaneKindFor(ClipKind kind) =>
        kind.IsText ? TrackType.Overlay : LaneKindFor(kind.Media);

    /// <summary>
    /// The lane a pasted or duplicated clip should land on. The lane it came
    /// from wins whenever it still exists and still suits the clip — a duplicate
    /// belongs beside its original, not on whichever lane happens to be topmost,
    /// and the sound half of a pair belongs back on its audio lane. Only if that
    /// lane is gone does the first suitable one take over; null means the caller
    /// has to create a lane.
    /// </summary>
    public static Track? PreferredLane(Project project, ClipKind kind, Guid sourceTrackId)
    {
        if (project.FindTrack(sourceTrackId) is { } origin && kind.FitsOn(origin.Type))
            return origin;
        return project.Tracks.FirstOrDefault(track => kind.FitsOn(track.Type));
    }
}
