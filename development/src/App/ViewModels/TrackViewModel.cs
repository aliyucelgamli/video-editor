using System.Collections.ObjectModel;
using System.Windows.Media;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

/// <summary>Read-only visual projection of a track and its events.</summary>
public class TrackViewModel
{
    private static readonly Brush VideoBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x6E, 0xA5)));
    private static readonly Brush AudioBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0x8D, 0x4E)));
    private static readonly Brush OverlayBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x6A, 0xBF)));

    public TrackViewModel(Track track, double pixelsPerSecond)
    {
        Name = track.Name;
        TypeLabel = track.Type.ToString();
        HeaderBrush = track.Type switch
        {
            TrackType.Video => VideoBrush,
            TrackType.Audio => AudioBrush,
            _ => OverlayBrush
        };

        foreach (var evt in track.Events.OrderBy(e => e.Start))
            Events.Add(new EventViewModel(evt, pixelsPerSecond, HeaderBrush));
    }

    public string Name { get; }
    public string TypeLabel { get; }
    public Brush HeaderBrush { get; }
    public ObservableCollection<EventViewModel> Events { get; } = new();

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
