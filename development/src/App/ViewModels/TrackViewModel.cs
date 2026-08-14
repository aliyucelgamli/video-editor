using System.Collections.ObjectModel;
using System.Windows.Media;
using VideoEditor.App.Mvvm;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

/// <summary>Visual projection of a track and its events.</summary>
public class TrackViewModel
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

    public TrackViewModel(
        Track track,
        double pixelsPerSecond,
        Guid? selectedEventId,
        Action<Guid> onToggleMute,
        Action<Guid> onToggleSolo)
    {
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

        ToggleMuteCommand = new RelayCommand(() => onToggleMute(track.Id));
        ToggleSoloCommand = new RelayCommand(() => onToggleSolo(track.Id));

        foreach (var evt in track.Events.OrderBy(e => e.Start))
            Events.Add(new EventViewModel(evt, pixelsPerSecond, EventBrush)
            {
                IsSelected = evt.Id == selectedEventId
            });
    }

    public Guid Id { get; }
    public TrackType Type { get; }
    public string Name { get; }
    public string TypeLabel { get; }
    public string TypeGlyph { get; }
    public bool IsMuted { get; }
    public bool IsSolo { get; }
    public Brush HeaderBrush { get; }
    public Brush EventBrush { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand ToggleSoloCommand { get; }
    public ObservableCollection<EventViewModel> Events { get; } = new();

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
