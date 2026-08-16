using System.Collections.ObjectModel;
using System.Windows.Media;
using VideoEditor.App.Mvvm;
using VideoEditor.App.Services;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

/// <summary>Callbacks a track view model reports user actions through.</summary>
public record TrackCallbacks(
    Action<Guid> ToggleMute,
    Action<Guid> ToggleSolo,
    Action<Guid, double, double> CommitVolume,
    Action<Guid, double, double> CommitOpacity,
    Action<Guid> Delete);

/// <summary>Visual projection of a track and its events.</summary>
public class TrackViewModel : ObservableObject
{
    private static readonly Color VideoColor = Color.FromRgb(0x4E, 0x86, 0xD8);
    private static readonly Color AudioColor = Color.FromRgb(0x43, 0xA0, 0x6A);
    private static readonly Color OverlayColor = Color.FromRgb(0x9A, 0x6F, 0xD0);

    private static readonly Brush VideoHeader = Solid(VideoColor);
    private static readonly Brush AudioHeader = Solid(AudioColor);
    private static readonly Brush OverlayHeader = Solid(OverlayColor);
    private static readonly Brush VideoEvent = Gradient(VideoColor);
    private static readonly Brush AudioEvent = Gradient(AudioColor);
    private static readonly Brush OverlayEvent = Gradient(OverlayColor);

    private readonly Track _track;
    private readonly TrackCallbacks _callbacks;
    private double _volumeEditStart;
    private bool _isEditingVolume;
    private double _opacityEditStart;
    private bool _isEditingOpacity;

    public TrackViewModel(
        Track track,
        Project project,
        double pixelsPerSecond,
        Guid? selectedEventId,
        TrackCallbacks callbacks,
        TimelineVisualsService? visuals)
    {
        _track = track;
        _callbacks = callbacks;

        Id = track.Id;
        Type = track.Type;
        Name = track.Name;
        TypeLabel = track.Type.ToString();
        IsMuted = track.Muted;
        IsSolo = track.Solo;

        (HeaderBrush, EventBrush, TypeGlyph) = track.Type switch
        {
            TrackType.Video => (VideoHeader, VideoEvent, "\uE714"),
            TrackType.Audio => (AudioHeader, AudioEvent, "\uE767"),
            _ => (OverlayHeader, OverlayEvent, "\uE8B9")
        };

        ToggleMuteCommand = new RelayCommand(() => _callbacks.ToggleMute(track.Id));
        ToggleSoloCommand = new RelayCommand(() => _callbacks.ToggleSolo(track.Id));
        DeleteCommand = new RelayCommand(() => _callbacks.Delete(track.Id));

        foreach (var evt in track.Events.OrderBy(e => e.Start))
            Events.Add(new EventViewModel(evt, track, project, pixelsPerSecond, EventBrush, visuals)
            {
                IsSelected = evt.Id == selectedEventId
            });
    }

    public Guid Id { get; }
    public TrackType Type { get; }

    /// <summary>Audio tracks get volume + solo; visual tracks get opacity instead.</summary>
    public bool IsAudio => Type == TrackType.Audio;
    public bool IsVisual => Type != TrackType.Audio;
    public string MuteToolTip => IsAudio ? "Mute track" : "Hide track";
    public string Name { get; }
    public string TypeLabel { get; }
    public string TypeGlyph { get; }
    public bool IsMuted { get; }
    public bool IsSolo { get; }
    public Brush HeaderBrush { get; }
    public Brush EventBrush { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand ToggleSoloCommand { get; }

    /// <summary>The round X on the header: removes this lane and its clips.</summary>
    public RelayCommand DeleteCommand { get; }
    public ObservableCollection<EventViewModel> Events { get; } = new();

    // ---------- Volume (0–200%, default 100) ----------

    public double VolumePercent
    {
        get => Math.Round(VolumeLimits.Clamp(_track.Volume) * 100);
        set
        {
            var clamped = VolumeLimits.Clamp(value / 100.0);
            if (Math.Abs(_track.Volume - clamped) < 0.0001) return;
            _track.Volume = clamped; // live while dragging; committed on release
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeLabel));
        }
    }

    public string VolumeLabel => $"{VolumePercent:0}%";

    public void BeginVolumeEdit()
    {
        if (_isEditingVolume) return;
        _isEditingVolume = true;
        _volumeEditStart = _track.Volume;
    }

    public void EndVolumeEdit()
    {
        if (!_isEditingVolume) return;
        _isEditingVolume = false;
        if (Math.Abs(_volumeEditStart - _track.Volume) > 0.0001)
            _callbacks.CommitVolume(_track.Id, _volumeEditStart, _track.Volume);
    }

    // ---------- Opacity (visual tracks, 0-100%; rendered by the compositor) ----------

    public double OpacityPercent
    {
        get => Math.Round(Math.Clamp(_track.Opacity, 0, 1) * 100);
        set
        {
            var clamped = Math.Clamp(value / 100.0, 0, 1);
            if (Math.Abs(_track.Opacity - clamped) < 0.0001) return;
            _track.Opacity = clamped; // live while dragging; committed on release
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpacityLabel));
        }
    }

    public string OpacityLabel => $"{OpacityPercent:0}%";

    public void BeginOpacityEdit()
    {
        if (_isEditingOpacity) return;
        _isEditingOpacity = true;
        _opacityEditStart = _track.Opacity;
    }

    public void EndOpacityEdit()
    {
        if (!_isEditingOpacity) return;
        _isEditingOpacity = false;
        if (Math.Abs(_opacityEditStart - _track.Opacity) > 0.0001)
            _callbacks.CommitOpacity(_track.Id, _opacityEditStart, _track.Opacity);
    }

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(Color color)
    {
        var brush = new LinearGradientBrush(
            Blend(color, Colors.White, 0.20),
            Blend(color, Colors.Black, 0.12),
            new System.Windows.Point(0, 0),
            new System.Windows.Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));
}
