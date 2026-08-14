using System.Windows.Media;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

public class MediaItemViewModel
{
    private static readonly Brush VideoBrush = Solid(0x4E, 0x86, 0xD8);
    private static readonly Brush AudioBrush = Solid(0x43, 0xA0, 0x6A);
    private static readonly Brush ImageBrush = Solid(0x9A, 0x6F, 0xD0);

    public MediaItemViewModel(MediaItem item)
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
        if (item.FileSizeBytes is long bytes) meta += $"  •  {FormatSize(bytes)}";
        if (item.DurationSeconds is double duration) meta += $"  •  {duration:0.#}s";
        MetaLabel = meta;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string FilePath { get; }
    public string Glyph { get; }
    public Brush GlyphBrush { get; }
    public string MetaLabel { get; }

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
