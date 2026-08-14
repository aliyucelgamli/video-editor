using System.Globalization;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;
using VideoEditor.MediaEngine.Effects;

namespace VideoEditor.MediaEngine.Export;

/// <summary>
/// Plans the FFmpeg command that mixes every audible audio event of a timeline
/// range into one stream. Pure command construction — no processes — so it is
/// fully unit-testable.
/// </summary>
public static class AudioMixPlanner
{
    /// <summary>One audio event clipped to the export range.</summary>
    public record AudioSegment(
        MediaItem Media, TimelineEvent Event, Track Track,
        double SourceIn, double SourceDuration, double TimelineOffset);

    /// <summary>Collects audible segments honoring track mute/solo and range clipping.</summary>
    public static List<AudioSegment> CollectSegments(Project project, TimeRange range)
    {
        var audioTracks = project.Tracks.Where(t => t.Type == TrackType.Audio).ToList();
        var anySolo = audioTracks.Any(t => t.Solo);
        var segments = new List<AudioSegment>();

        foreach (var track in audioTracks)
        {
            if (track.Muted || (anySolo && !track.Solo)) continue;

            foreach (var evt in track.Events)
            {
                var visibleStart = Math.Max(evt.Start, range.Start);
                var visibleEnd = Math.Min(evt.End, range.End);
                if (visibleEnd - visibleStart <= 0.001) continue;

                var media = project.Media.FindById(evt.MediaId);
                if (media is null) continue;

                var rate = evt.PlaybackRate <= 0 ? 1.0 : evt.PlaybackRate;
                segments.Add(new AudioSegment(
                    media, evt, track,
                    SourceIn: evt.SourceIn + (visibleStart - evt.Start) * rate,
                    SourceDuration: (visibleEnd - visibleStart) * rate,
                    TimelineOffset: visibleStart - range.Start));
            }
        }
        return segments;
    }

    /// <summary>
    /// Builds full ffmpeg arguments producing a mixed stereo WAV for the range.
    /// With no audible segments the command renders silence, so the video
    /// encoder can always rely on an audio input being present.
    /// </summary>
    public static List<string> BuildMixArguments(
        Project project, IEffectCatalog catalog, TimeRange range, int sampleRate, string outputWavPath)
    {
        var segments = CollectSegments(project, range);
        var arguments = new List<string> { "-y", "-loglevel", "error" };
        var filters = new List<string>();
        var labels = new List<string>();

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            arguments.AddRange(new[]
            {
                "-ss", Num(segment.SourceIn),
                "-t", Num(segment.SourceDuration),
                "-i", segment.Media.FilePath
            });

            var chain = new List<string> { $"aresample={sampleRate}", "aformat=channel_layouts=stereo" };
            var eventFilter = AudioFilterGraphBuilder.BuildEventFilter(
                segment.Event, catalog, segment.Track.Volume, sampleRate);
            if (eventFilter.Length > 0) chain.Add(eventFilter);

            var delayMs = (int)Math.Round(segment.TimelineOffset * 1000);
            if (delayMs > 0) chain.Add($"adelay={delayMs}:all=1");

            var label = $"a{i}";
            filters.Add($"[{i}:a]{string.Join(",", chain)}[{label}]");
            labels.Add($"[{label}]");
        }

        string finalChain;
        if (segments.Count == 0)
        {
            arguments.AddRange(new[]
            {
                "-f", "lavfi",
                "-i", $"anullsrc=r={sampleRate}:cl=stereo"
            });
            finalChain = $"[0:a]atrim=0:{Num(range.Duration)}[mix]";
        }
        else
        {
            var mix = segments.Count == 1
                ? $"{labels[0]}anull"
                : $"{string.Concat(labels)}amix=inputs={segments.Count}:normalize=0";
            finalChain = $"{mix},apad,atrim=0:{Num(range.Duration)}[mix]";
        }

        filters.Add(finalChain);
        arguments.AddRange(new[]
        {
            "-filter_complex", string.Join(";", filters),
            "-map", "[mix]",
            "-ar", sampleRate.ToString(),
            "-ac", "2",
            outputWavPath
        });
        return arguments;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
