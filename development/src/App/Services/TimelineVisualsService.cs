using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoEditor.MediaEngine.Thumbnails;
using VideoEditor.MediaEngine.Waveform;

namespace VideoEditor.App.Services;

/// <summary>
/// UI-side cache over the media engine's thumbnail/waveform services.
/// View models ask synchronously (Try*) and get an async callback on the UI
/// thread when data had to be generated — so timeline rebuilds stay cheap.
/// </summary>
public class TimelineVisualsService
{
    public const int WaveformPeaksPerSecond = 50;
    private const int ThumbnailPixelWidth = 160;

    private readonly ThumbnailService _thumbnails;
    private readonly WaveformService _waveform;
    private readonly Dispatcher _dispatcher;

    private readonly ConcurrentDictionary<string, float[]> _peaks = new();
    private readonly ConcurrentDictionary<string, ImageSource> _images = new();
    private readonly ConcurrentDictionary<string, byte> _pendingPeaks = new();

    public TimelineVisualsService(ThumbnailService thumbnails, WaveformService waveform, Dispatcher dispatcher)
    {
        _thumbnails = thumbnails;
        _waveform = waveform;
        _dispatcher = dispatcher;
    }

    // ---------- Waveforms ----------

    public bool TryGetPeaks(string mediaPath, out float[] peaks) =>
        _peaks.TryGetValue(mediaPath, out peaks!);

    /// <summary>Generates peaks in the background; the callback runs on the UI thread.</summary>
    public void RequestPeaks(string mediaPath, Action<float[]> onReady)
    {
        if (_peaks.TryGetValue(mediaPath, out var cached))
        {
            onReady(cached);
            return;
        }
        if (!_pendingPeaks.TryAdd(mediaPath, 0)) return; // a request is already running

        _ = Task.Run(async () =>
        {
            try
            {
                var peaks = await _waveform.GetPeaksAsync(mediaPath, WaveformPeaksPerSecond);
                if (peaks is null) return;
                _peaks[mediaPath] = peaks;
                _ = _dispatcher.BeginInvoke(() => onReady(peaks)); // fire-and-forget UI callback
            }
            catch { /* waveforms are cosmetic — never crash the UI */ }
            finally { _pendingPeaks.TryRemove(mediaPath, out _); }
        });
    }

    // ---------- Thumbnails ----------

    /// <summary>
    /// Loads a thumbnail for the media at the given source time.
    /// Image files are loaded directly; videos go through ffmpeg (cached).
    /// The callback runs on the UI thread and may fire immediately.
    /// </summary>
    public void RequestThumbnail(string mediaPath, double timeSeconds, bool isStillImage, Action<ImageSource> onReady)
    {
        if (isStillImage)
        {
            if (LoadImageCached(mediaPath) is { } direct) onReady(direct);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var path = await _thumbnails.GetThumbnailAsync(mediaPath, timeSeconds, ThumbnailPixelWidth);
                if (path is null) return;
                _ = _dispatcher.BeginInvoke(() => // fire-and-forget UI callback
                {
                    if (LoadImageCached(path) is { } image) onReady(image);
                });
            }
            catch { /* cosmetic */ }
        });
    }

    /// <summary>Loads evenly spaced frames of [sourceIn, sourceOut] for event film strips.</summary>
    public void RequestFilmstrip(
        string mediaPath, double sourceIn, double sourceOut, int frameCount, Action<IReadOnlyList<ImageSource>> onReady)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var paths = await _thumbnails.GetFilmstripAsync(
                    mediaPath, sourceIn, sourceOut, frameCount, ThumbnailPixelWidth);
                if (paths.Count == 0) return;
                _ = _dispatcher.BeginInvoke(() => // fire-and-forget UI callback
                {
                    var images = paths.Select(LoadImageCached).Where(i => i != null).Cast<ImageSource>().ToList();
                    if (images.Count > 0) onReady(images);
                });
            }
            catch { /* cosmetic */ }
        });
    }

    private ImageSource? LoadImageCached(string filePath)
    {
        if (_images.TryGetValue(filePath, out var cached)) return cached;
        try
        {
            if (!File.Exists(filePath)) return null;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = ThumbnailPixelWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            _images[filePath] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
