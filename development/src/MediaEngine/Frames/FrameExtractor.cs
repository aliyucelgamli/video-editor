using System.Globalization;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Frames;

/// <summary>A decoded video frame: tightly packed BGRA pixels.</summary>
public record RawFrame(byte[] Bgra, int Width, int Height);

/// <summary>
/// Extracts single frames from video/image files as raw BGRA using ffmpeg.
/// Frames are letterboxed into the requested canvas size, so composition can
/// blend them without further scaling.
///
/// A small LRU cache keeps recently extracted frames: re-rendering the same
/// playhead position (typical while tweaking effect sliders) skips the
/// expensive decode and responds in milliseconds. Callers receive a private
/// copy, so applying effects in place never corrupts the cache.
/// </summary>
public class FrameExtractor
{
    private const int CacheCapacity = 24; // ~24 × 0.9 MB at 640×360 — bounded and cheap

    private readonly FFmpegLocator _locator;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, RawFrame> _cache = new();
    private readonly LinkedList<string> _recency = new();

    public FrameExtractor(FFmpegLocator locator) => _locator = locator;

    public async Task<RawFrame?> GetFrameAsync(
        string mediaPath, double sourceTime, int width, int height,
        CancellationToken cancellationToken = default)
    {
        if (_locator.FfmpegPath is not { } ffmpeg || !File.Exists(mediaPath)) return null;
        if (width < 2 || height < 2) return null;

        // Even dimensions keep every downstream pixel format happy.
        width -= width % 2;
        height -= height % 2;

        var key = FormattableString.Invariant($"{mediaPath}|{sourceTime:0.###}|{width}x{height}");
        if (TryGetCached(key) is { } cached) return cached;

        var frame = await DecodeFrameAsync(ffmpeg, mediaPath, sourceTime, width, height, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null) return null;

        StoreInCache(key, frame);
        return Copy(frame);
    }

    private async Task<RawFrame?> DecodeFrameAsync(
        string ffmpeg, string mediaPath, double sourceTime, int width, int height,
        CancellationToken cancellationToken)
    {
        var scale = $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
                    $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black";

        var arguments = new List<string> { "-loglevel", "error" };
        if (sourceTime > 0)
        {
            arguments.Add("-ss");
            arguments.Add(sourceTime.ToString("0.###", CultureInfo.InvariantCulture));
        }
        arguments.AddRange(new[]
        {
            "-i", mediaPath,
            "-frames:v", "1",
            "-vf", scale,
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "pipe:1"
        });

        var (result, bytes) = await ProcessRunner.RunBytesAsync(ffmpeg, arguments, cancellationToken)
            .ConfigureAwait(false);

        var expected = width * height * 4;
        if (!result.Success || bytes.Length < expected) return null;
        if (bytes.Length > expected) bytes = bytes[..expected];

        return new RawFrame(bytes, width, height);
    }

    // ---------- LRU cache ----------

    private RawFrame? TryGetCached(string key)
    {
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(key, out var frame)) return null;
            _recency.Remove(key);
            _recency.AddFirst(key);
            return Copy(frame);
        }
    }

    private void StoreInCache(string key, RawFrame frame)
    {
        lock (_cacheGate)
        {
            if (_cache.ContainsKey(key)) return;
            _cache[key] = frame;
            _recency.AddFirst(key);
            while (_cache.Count > CacheCapacity && _recency.Last is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _recency.RemoveLast();
            }
        }
    }

    private static RawFrame Copy(RawFrame frame) =>
        new((byte[])frame.Bgra.Clone(), frame.Width, frame.Height);
}
