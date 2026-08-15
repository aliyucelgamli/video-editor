using System.Collections.Concurrent;
using VideoEditor.Domain;

namespace VideoEditor.MediaEngine.Frames;

/// <summary>
/// Thread-safe store of rasterized text layers. The UI renders text with
/// WPF's text stack (the media engine has no text renderer) and puts the
/// frames here; compositing then works from any thread, for preview and
/// export alike. Bounded: a style change re-renders, stale entries age out.
/// </summary>
public class TextRasterCache
{
    private const int MaxEntries = 64;

    private readonly ConcurrentDictionary<string, RawFrame> _frames = new();

    public static string KeyFor(TextStyle style, int width, int height) =>
        FormattableString.Invariant($"{style.CacheKey}|{width}x{height}");

    /// <summary>
    /// The cached raster, shared instance — callers must copy before mutating
    /// (effects run in place).
    /// </summary>
    public RawFrame? TryGetShared(TextStyle style, int width, int height) =>
        _frames.TryGetValue(KeyFor(style, width, height), out var frame) ? frame : null;

    public void Store(TextStyle style, int width, int height, RawFrame frame)
    {
        if (_frames.Count >= MaxEntries) _frames.Clear(); // crude but safe bound
        _frames[KeyFor(style, width, height)] = frame;
    }
}
