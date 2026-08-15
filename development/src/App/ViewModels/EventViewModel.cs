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

    private const int FadeCurveSamples = 24;

    private readonly TimelineEvent _event;
    private readonly double _pixelsPerSecond;
    private bool _isSelected;
    private PointCollection? _waveformPoints;
    private PointCollection? _fadeInFill;
    private PointCollection? _fadeInLine;
    private PointCollection? _fadeOutFill;
    private PointCollection? _fadeOutLine;

    public EventViewModel(
        TimelineEvent evt,
        Track track,
        Project project,
        double pixelsPerSecond,
        Brush brush,
        TimelineVisualsService? visuals)
    {
        _event = evt;
        _pixelsPerSecond = pixelsPerSecond;
        Id = evt.Id;
        Name = evt.Name;
        StartSeconds = evt.Start;
        DurationSeconds = evt.Duration;
        X = evt.Start * pixelsPerSecond;
        Width = Math.Max(6, evt.Duration * pixelsPerSecond);
        Brush = brush;
        DurationLabel = $"{evt.Duration:0.#}s";
        LinkedEventId = evt.LinkedEventId;
        IsLinked = evt.LinkedEventId != null;
        HasEffects = evt.Effects.Count > 0;

        var media = project.Media.FindById(evt.MediaId);
        IsAudio = track.Type == TrackType.Audio;
        IsVisual = !IsAudio && media != null;

        ToolTip = BuildToolTip(evt);
        RefreshFadeVisuals();

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
    public Guid? LinkedEventId { get; }
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

    // ---------- Fade envelopes (corner grips + eased curve overlay) ----------

    public PointCollection? FadeInFill { get => _fadeInFill; private set => SetProperty(ref _fadeInFill, value); }
    public PointCollection? FadeInLine { get => _fadeInLine; private set => SetProperty(ref _fadeInLine, value); }
    public PointCollection? FadeOutFill { get => _fadeOutFill; private set => SetProperty(ref _fadeOutFill, value); }
    public PointCollection? FadeOutLine { get => _fadeOutLine; private set => SetProperty(ref _fadeOutLine, value); }
    public bool HasFadeIn => _fadeInLine != null;
    public bool HasFadeOut => _fadeOutLine != null;

    /// <summary>
    /// Rebuilds the fade curve overlays from the model. Called on creation and
    /// live while a corner grip is dragged.
    /// </summary>
    public void RefreshFadeVisuals()
    {
        var fadeInWidth = Math.Min(Width, _event.FadeInDuration * _pixelsPerSecond);
        var fadeOutWidth = Math.Min(Width, _event.FadeOutDuration * _pixelsPerSecond);

        if (fadeInWidth > 1)
        {
            var (fill, line) = BuildFadeShape(0, fadeInWidth, _event.FadeInEasing, isFadeIn: true);
            FadeInFill = fill;
            FadeInLine = line;
        }
        else
        {
            FadeInFill = null;
            FadeInLine = null;
        }

        if (fadeOutWidth > 1)
        {
            var (fill, line) = BuildFadeShape(
                Width - fadeOutWidth, fadeOutWidth, _event.FadeOutEasing, isFadeIn: false);
            FadeOutFill = fill;
            FadeOutLine = line;
        }
        else
        {
            FadeOutFill = null;
            FadeOutLine = null;
        }

        OnPropertyChanged(nameof(HasFadeIn));
        OnPropertyChanged(nameof(HasFadeOut));
    }

    /// <summary>
    /// The eased opacity envelope over a fade region: a line following the
    /// easing curve plus a fill covering the faded-away area above it.
    /// </summary>
    private static (PointCollection Fill, PointCollection Line) BuildFadeShape(
        double left, double width, EasingType easing, bool isFadeIn)
    {
        var line = new List<Point>(FadeCurveSamples + 1);
        for (var i = 0; i <= FadeCurveSamples; i++)
        {
            var x = left + width * i / FadeCurveSamples;
            var progress = isFadeIn
                ? (double)i / FadeCurveSamples
                : 1 - (double)i / FadeCurveSamples;
            var factor = Math.Clamp(Easing.Evaluate(easing, progress), 0, 1);
            line.Add(new Point(x, LaneContentHeight * (1 - factor)));
        }

        // Close the fill along the top edge toward the clip's outer corner.
        var fill = new List<Point>(line) { new(isFadeIn ? left : left + width, 0) };

        var linePoints = new PointCollection(line);
        var fillPoints = new PointCollection(fill);
        linePoints.Freeze();
        fillPoints.Freeze();
        return (fillPoints, linePoints);
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
