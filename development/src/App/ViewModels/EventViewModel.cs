using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using VideoEditor.App.Mvvm;
using VideoEditor.App.Services;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// Visual projection of a timeline event, including its waveform (audio) or
/// film strip (video/image). Heavy visuals load asynchronously through the
/// visuals service and appear as soon as they are ready.
/// </summary>
public class EventViewModel : ObservableObject
{
    public const double LaneContentHeight = 48;

    private readonly TimelineEvent _event;
    private bool _isSelected;
    private PointCollection? _waveformPoints;

    public EventViewModel(
        TimelineEvent evt,
        Track track,
        Project project,
        double pixelsPerSecond,
        Brush brush,
        TimelineVisualsService? visuals)
    {
        _event = evt;
        Id = evt.Id;
        Name = evt.Name;
        StartSeconds = evt.Start;
        DurationSeconds = evt.Duration;
        X = evt.Start * pixelsPerSecond;
        Width = Math.Max(6, evt.Duration * pixelsPerSecond);
        Brush = brush;
        DurationLabel = $"{evt.Duration:0.#}s";
        IsLinked = evt.LinkedEventId != null;
        HasEffects = evt.Effects.Count > 0;

        var media = project.Media.FindById(evt.MediaId);
        IsAudio = track.Type == TrackType.Audio;
        IsVisual = !IsAudio && media != null;

        ToolTip = BuildToolTip(evt);

        if (visuals is null || media is null) return;
        if (IsAudio) LoadWaveform(media, visuals);
        else LoadFilmstrip(media, visuals);
    }

    public Guid Id { get; }
    public string Name { get; }
    public double StartSeconds { get; }
    public double DurationSeconds { get; }
    public double X { get; }
    public double Width { get; }
    public Brush Brush { get; }
    public string DurationLabel { get; }
    public string ToolTip { get; }
    public bool IsLinked { get; }
    public bool HasEffects { get; }
    public bool IsAudio { get; }
    public bool IsVisual { get; }

    public ObservableCollection<ImageSource> Thumbnails { get; } = new();

    public PointCollection? WaveformPoints
    {
        get => _waveformPoints;
        private set
        {
            if (SetProperty(ref _waveformPoints, value))
                OnPropertyChanged(nameof(HasWaveform));
        }
    }

    public bool HasWaveform => _waveformPoints is { Count: > 2 };
    public bool HasThumbnails => Thumbnails.Count > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private static string BuildToolTip(TimelineEvent evt)
    {
        var text = $"{evt.Name}\n{evt.Start:0.##}s – {evt.End:0.##}s  ({evt.Duration:0.##}s)";
        if (Math.Abs(evt.Volume - 1.0) > 0.001) text += $"\nVolume {evt.Volume * 100:0}%";
        if (evt.Effects.Count > 0) text += $"\n{evt.Effects.Count} effect(s)";
        if (evt.LinkedEventId != null) text += "\nLinked audio/video";
        return text;
    }

    // ---------- Waveform ----------

    private void LoadWaveform(MediaItem media, TimelineVisualsService visuals)
    {
        if (visuals.TryGetPeaks(media.FilePath, out var peaks)) WaveformPoints = BuildPolygon(peaks);
        else visuals.RequestPeaks(media.FilePath, p => WaveformPoints = BuildPolygon(p));
    }

    /// <summary>
    /// Builds a closed polygon (top edge left→right, bottom edge right→left)
    /// covering the event's visible source range.
    /// </summary>
    private PointCollection BuildPolygon(float[] peaks)
    {
        var pointCount = Math.Clamp((int)(Width / 2), 8, 400);
        var center = LaneContentHeight / 2;
        var amplitude = center - 3;
        var sourceSpan = Math.Max(0.01, _event.SourceOut - _event.SourceIn);

        var top = new List<Point>(pointCount + 1);
        var bottom = new List<Point>(pointCount + 1);

        for (var i = 0; i <= pointCount; i++)
        {
            var x = Width * i / pointCount;
            var sourceSecond = _event.SourceIn + sourceSpan * i / pointCount;
            var index = (int)(sourceSecond * TimelineVisualsService.WaveformPeaksPerSecond);
            var peak = index >= 0 && index < peaks.Length ? peaks[index] : 0f;
            var y = Math.Max(1.5, peak * amplitude);
            top.Add(new Point(x, center - y));
            bottom.Add(new Point(x, center + y));
        }

        bottom.Reverse();
        var points = new PointCollection(top.Concat(bottom));
        points.Freeze();
        return points;
    }

    // ---------- Film strip ----------

    private void LoadFilmstrip(MediaItem media, TimelineVisualsService visuals)
    {
        if (media.Type == MediaType.Image)
        {
            visuals.RequestThumbnail(media.FilePath, 0, isStillImage: true, image =>
            {
                Thumbnails.Add(image);
                OnPropertyChanged(nameof(HasThumbnails));
            });
            return;
        }

        var frameCount = Math.Clamp((int)(Width / 72), 1, 12);
        visuals.RequestFilmstrip(media.FilePath, _event.SourceIn, _event.SourceOut, frameCount, images =>
        {
            Thumbnails.Clear();
            foreach (var image in images) Thumbnails.Add(image);
            OnPropertyChanged(nameof(HasThumbnails));
        });
    }
}
