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
    /// <summary>
    /// Cache budget in bytes rather than entries: a frame is 0.9 MB at 640×360
    /// but 2 MB at 960×540, and it is the memory that has to stay bounded.
    /// 64 MB holds ~70 preview frames — a comfortable scrub history.
    /// </summary>
    private const long CacheBudgetBytes = 64L * 1024 * 1024;

    private readonly FFmpegLocator _locator;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, RawFrame> _cache = new();
    private readonly LinkedList<string> _recency = new();
    private long _cachedBytes;

    public FrameExtractor(FFmpegLocator locator) => _locator = locator;

    /// <summary>
    /// ffmpeg <c>-hwaccel</c> value for single-frame decoding, or null for
    /// software. Landing on a new position decodes a whole GOP, which is where
    /// a GPU decoder can pay off; set from the user setting after
    /// <see cref="HardwareDecoders.DetectAsync"/> has verified one works.
    /// </summary>
    public string? HardwareAccelerator { get; set; }

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
        if (HardwareAccelerator is { Length: > 0 } accelerator)
        {
            // No -hwaccel_output_format: frames come back in system memory, so
            // the filter chain and everything downstream stay unchanged.
            arguments.Add("-hwaccel");
            arguments.Add(accelerator);
        }
        if (sourceTime > 0)
        {
            arguments.Add("-ss");
            arguments.Add(sourceTime.ToString("0.###", CultureInfo.InvariantCulture));
        }
        arguments.AddRange(new[]
        {
            "-i", mediaPath,
            "-an", "-sn", "-dn",  // one video frame — never open audio/subtitle/data streams
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
            _cachedBytes += frame.Bgra.Length;

            while (_cachedBytes > CacheBudgetBytes && _recency.Last is { } oldest)
            {
                if (_cache.Remove(oldest.Value, out var evicted))
                    _cachedBytes -= evicted.Bgra.Length;
                _recency.RemoveLast();
            }
        }
    }

    private static RawFrame Copy(RawFrame frame) =>
        new((byte[])frame.Bgra.Clone(), frame.Width, frame.Height);
}
