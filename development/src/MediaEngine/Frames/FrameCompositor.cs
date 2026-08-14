using VideoEditor.Domain;
using VideoEditor.MediaEngine.Effects;

namespace VideoEditor.MediaEngine.Frames;

/// <summary>
/// Composes the frame visible at a given timeline position: visual tracks are
/// painted bottom-up (the topmost track in the UI renders on top), each event
/// runs through its effect chain, then opacity/fades blend it onto the canvas.
/// Preview and export share this class so they can never diverge.
/// </summary>
public class FrameCompositor
{
    private readonly FrameExtractor _extractor;
    private readonly VideoEffectPipeline _effects;

    public FrameCompositor(FrameExtractor extractor, VideoEffectPipeline effects)
    {
        _extractor = extractor;
        _effects = effects;
    }

    /// <summary>
    /// Renders the timeline at <paramref name="time"/> into a BGRA canvas.
    /// Returns a black frame when nothing is visible.
    /// </summary>
    public async Task<RawFrame> ComposeAsync(
        Project project, double time, int width, int height,
        CancellationToken cancellationToken = default)
    {
        width -= width % 2;
        height -= height % 2;
        var canvas = new byte[width * height * 4];
        FillBlack(canvas);

        // Track index 0 is the top lane in the UI → paint it last (on top).
        foreach (var track in EnumerateVisualTracksBottomUp(project))
        {
            if (track.Muted) continue;

            foreach (var evt in track.Events)
            {
                if (!evt.Contains(time)) continue;
                cancellationToken.ThrowIfCancellationRequested();

                var media = project.Media.FindById(evt.MediaId);
                if (media is null || media.Type == MediaType.Audio) continue;

                var sourceTime = evt.SourceIn + (time - evt.Start) * evt.PlaybackRate;
                var frame = await _extractor
                    .GetFrameAsync(media.FilePath, media.Type == MediaType.Image ? 0 : sourceTime, width, height, cancellationToken)
                    .ConfigureAwait(false);
                if (frame is null) continue;

                // Event effects animate on clip-local time, track effects on timeline time.
                _effects.Apply(frame.Bgra, frame.Width, frame.Height, evt.Effects, time - evt.Start);
                _effects.Apply(frame.Bgra, frame.Width, frame.Height, track.Effects, time);

                var opacity = Math.Clamp(evt.Opacity, 0, 1) *
                              Math.Clamp(track.Opacity, 0, 1) *
                              FadeFactor(evt, time);
                BlendOnto(canvas, frame.Bgra, opacity);
            }
        }

        return new RawFrame(canvas, width, height);
    }

    /// <summary>One visual layer visible at a point in time (playback fast path).</summary>
    public record VisualLayer(Track Track, TimelineEvent Event, MediaItem Media);

    /// <summary>
    /// Returns the single visual layer visible at <paramref name="time"/>, or
    /// null when zero or multiple layers overlap. Playback streams this single
    /// layer directly (fast); overlaps fall back to full composition.
    /// </summary>
    public static VisualLayer? FindSingleVisualLayer(Project project, double time)
    {
        VisualLayer? found = null;
        foreach (var track in EnumerateVisualTracksBottomUp(project))
        {
            if (track.Muted) continue;
            foreach (var evt in track.Events)
            {
                if (!evt.Contains(time)) continue;
                var media = project.Media.FindById(evt.MediaId);
                if (media is null || media.Type == MediaType.Audio) continue;
                if (found != null) return null; // more than one layer → composite path
                found = new VisualLayer(track, evt, media);
            }
        }
        return found;
    }

    /// <summary>Multiplies a frame toward black (event/track opacity + fades).</summary>
    public static void ApplyOpacity(byte[] bgra, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity >= 0.999) return;

        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(bgra[i] * opacity);
            bgra[i + 1] = (byte)(bgra[i + 1] * opacity);
            bgra[i + 2] = (byte)(bgra[i + 2] * opacity);
        }
    }

    /// <summary>Fade-in/out progress at the given timeline position (0..1).</summary>
    public static double FadeFactor(TimelineEvent evt, double time)
    {
        var factor = 1.0;
        if (evt.FadeInDuration > 0 && time < evt.Start + evt.FadeInDuration)
            factor = Math.Min(factor, (time - evt.Start) / evt.FadeInDuration);
        if (evt.FadeOutDuration > 0 && time > evt.End - evt.FadeOutDuration)
            factor = Math.Min(factor, (evt.End - time) / evt.FadeOutDuration);
        return Math.Clamp(factor, 0, 1);
    }

    private static IEnumerable<Track> EnumerateVisualTracksBottomUp(Project project)
    {
        for (var i = project.Tracks.Count - 1; i >= 0; i--)
        {
            var track = project.Tracks[i];
            if (track.Type is TrackType.Video or TrackType.Overlay)
                yield return track;
        }
    }

    private static void FillBlack(byte[] canvas)
    {
        for (var i = 3; i < canvas.Length; i += 4) canvas[i] = 255;
    }

    private static void BlendOnto(byte[] canvas, byte[] layer, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity <= 0) return;

        for (var i = 0; i < canvas.Length; i += 4)
        {
            // Combine the layer's own alpha (transparent PNGs) with event opacity.
            var alpha = opacity * layer[i + 3] / 255.0;
            if (alpha <= 0) continue;

            canvas[i] = (byte)(canvas[i] + (layer[i] - canvas[i]) * alpha);
            canvas[i + 1] = (byte)(canvas[i + 1] + (layer[i + 1] - canvas[i + 1]) * alpha);
            canvas[i + 2] = (byte)(canvas[i + 2] + (layer[i + 2] - canvas[i + 2]) * alpha);
            canvas[i + 3] = 255;
        }
    }
}
