using System.Windows.Media;
using VideoEditor.App.Mvvm;
using VideoEditor.App.Services;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

/// <summary>A media library entry with an async-loaded preview thumbnail.</summary>
public class MediaItemViewModel : ObservableObject
{
    private static readonly Brush VideoBrush = Solid(0x4E, 0x86, 0xD8);
    private static readonly Brush AudioBrush = Solid(0x43, 0xA0, 0x6A);
    private static readonly Brush ImageBrush = Solid(0x9A, 0x6F, 0xD0);

    private ImageSource? _thumbnail;

    public MediaItemViewModel(MediaItem item, TimelineVisualsService? visuals)
    {
        Id = item.Id;
        Name = item.Name;
        FilePath = item.FilePath;
        (Glyph, GlyphBrush) = item.Type switch
        {
            MediaType.Video => ("\uE714", VideoBrush),
            MediaType.Audio => ("\uE767", AudioBrush),
            _ => ("\uE8B9", ImageBrush)
        };

        var meta = item.Type.ToString();
        if (item.Width is int w && item.Height is int h) meta += $"  •  {w}×{h}";
        if (item.DurationSeconds is double duration) meta += $"  •  {FormatDuration(duration)}";
        if (item.FileSizeBytes is long bytes) meta += $"  •  {FormatSize(bytes)}";
        MetaLabel = meta;

        if (visuals != null && item.Type != MediaType.Audio)
        {
            // Video: grab a frame slightly in so black lead-ins don't win.
            var time = item.Type == MediaType.Video
                ? Math.Min(0.5, (item.DurationSeconds ?? 1) * 0.1)
                : 0;
            visuals.RequestThumbnail(item.FilePath, time, item.Type == MediaType.Image,
                image => Thumbnail = image);
        }
    }

    public Guid Id { get; }
    public string Name { get; }
    public string FilePath { get; }
    public string Glyph { get; }
    public Brush GlyphBrush { get; }
    public string MetaLabel { get; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (SetProperty(ref _thumbnail, value))
                OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail != null;

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalMinutes >= 1 ? ts.ToString(@"m\:ss") : $"{seconds:0.#}s";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.#} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B"
    };

    private static Brush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
