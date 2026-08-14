using System.Windows.Threading;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.App.Services;

/// <summary>
/// Fills in real media metadata (duration, resolution, audio presence) with
/// ffprobe after import — asynchronously, so imports feel instant. Events that
/// still carry the placeholder duration are stretched to the real clip length.
/// </summary>
public class MediaEnrichmentService
{
    private readonly MediaProbe _probe;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<Guid> _inFlight = new();

    public MediaEnrichmentService(MediaProbe probe, Dispatcher dispatcher)
    {
        _probe = probe;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Probes the given items in the background. <paramref name="onItemUpdated"/>
    /// runs on the UI thread once per successfully probed item.
    /// </summary>
    public void Enrich(IEnumerable<MediaItem> items, Project project, double placeholderDuration, Action onItemUpdated)
    {
        foreach (var item in items)
        {
            if (item.Type == MediaType.Image || item.DurationSeconds is not null) continue;

            lock (_inFlight)
            {
                if (!_inFlight.Add(item.Id)) continue;
            }

            var captured = item;
            _ = Task.Run(async () =>
            {
                try
                {
                    var info = await _probe.ProbeAsync(captured.FilePath);
                    if (info is null) return;

                    _dispatcher.BeginInvoke(() =>
                    {
                        ApplyInfo(captured, info);
                        StretchPlaceholderEvents(project, captured, placeholderDuration);
                        onItemUpdated();
                    });
                }
                finally
                {
                    lock (_inFlight) _inFlight.Remove(captured.Id);
                }
            });
        }
    }

    private static void ApplyInfo(MediaItem item, MediaInfo info)
    {
        item.DurationSeconds = info.DurationSeconds;
        item.Width = info.Width;
        item.Height = info.Height;
        item.FrameRate = info.FrameRate;
        item.HasAudio = info.HasAudio;
    }

    /// <summary>
    /// Events created before probing use a placeholder length; once the real
    /// duration is known, untouched events grow/shrink to the full clip.
    /// </summary>
    private static void StretchPlaceholderEvents(Project project, MediaItem item, double placeholderDuration)
    {
        if (item.DurationSeconds is not { } duration || duration <= 0) return;

        foreach (var track in project.Tracks)
        {
            foreach (var evt in track.Events)
            {
                if (evt.MediaId != item.Id) continue;
                var isUntouchedPlaceholder =
                    Math.Abs(evt.Duration - placeholderDuration) < 0.001 &&
                    Math.Abs(evt.SourceOut - placeholderDuration) < 0.001 &&
                    evt.SourceIn == 0;
                if (!isUntouchedPlaceholder) continue;

                evt.Duration = duration;
                evt.SourceOut = duration;
            }
        }
    }
}
