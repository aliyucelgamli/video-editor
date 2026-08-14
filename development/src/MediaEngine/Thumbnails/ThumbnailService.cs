using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Thumbnails;

/// <summary>
/// Generates and caches PNG thumbnails for video and image files via ffmpeg.
/// All work is async and cached on disk (cache/thumbnails); concurrent requests
/// for the same thumbnail share one generation task.
/// </summary>
public class ThumbnailService
{
    private readonly FFmpegLocator _locator;
    private readonly CachePaths _cache;
    private readonly Dictionary<string, Task<string?>> _inFlight = new();
    private readonly object _gate = new();

    public ThumbnailService(FFmpegLocator locator, CachePaths cache)
    {
        _locator = locator;
        _cache = cache;
    }

    /// <summary>
    /// Returns the path of a cached thumbnail PNG for the given media at the
    /// given source time, or null when generation is impossible.
    /// </summary>
    public Task<string?> GetThumbnailAsync(
        string mediaPath, double timeSeconds, int width, CancellationToken cancellationToken = default)
    {
        var key = CachePaths.KeyFor(mediaPath, "thumb", Math.Round(timeSeconds, 2), width);
        var target = Path.Combine(_cache.Thumbnails, key + ".png");
        if (File.Exists(target)) return Task.FromResult<string?>(target);

        lock (_gate)
        {
            if (_inFlight.TryGetValue(target, out var running)) return running;
            var task = GenerateAsync(mediaPath, timeSeconds, width, target, cancellationToken);
            _inFlight[target] = task;
            _ = task.ContinueWith(_ => { lock (_gate) _inFlight.Remove(target); },
                TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>
    /// Returns thumbnails evenly spread across [sourceIn, sourceOut] — the film
    /// strip drawn inside video events on the timeline.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetFilmstripAsync(
        string mediaPath, double sourceIn, double sourceOut, int frameCount, int width,
        CancellationToken cancellationToken = default)
    {
        var results = new List<string>();
        if (frameCount < 1) return results;

        var span = Math.Max(0, sourceOut - sourceIn);
        for (var i = 0; i < frameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var time = frameCount == 1 ? sourceIn : sourceIn + span * i / (frameCount - 1);
            // Nudge off the exact end of the file, where seeks often return nothing.
            time = Math.Min(time, Math.Max(sourceIn, sourceOut - 0.1));
            var path = await GetThumbnailAsync(mediaPath, time, width, cancellationToken).ConfigureAwait(false);
            if (path != null) results.Add(path);
        }
        return results;
    }

    private async Task<string?> GenerateAsync(
        string mediaPath, double timeSeconds, int width, string targetPath, CancellationToken cancellationToken)
    {
        if (_locator.FfmpegPath is not { } ffmpeg || !File.Exists(mediaPath)) return null;

        Directory.CreateDirectory(_cache.Thumbnails);
        var temp = targetPath + ".tmp.png";

        var arguments = new List<string> { "-y", "-loglevel", "error" };
        if (timeSeconds > 0)
        {
            arguments.Add("-ss");
            arguments.Add(timeSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        arguments.AddRange(new[]
        {
            "-i", mediaPath,
            "-frames:v", "1",
            "-vf", $"scale={width}:-2",
            temp
        });

        var result = await ProcessRunner.RunAsync(ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Success || !File.Exists(temp))
        {
            TryDelete(temp);
            return null;
        }

        try
        {
            File.Move(temp, targetPath, overwrite: true);
            return targetPath;
        }
        catch (IOException)
        {
            // Another request may have completed first — the cached file wins.
            TryDelete(temp);
            return File.Exists(targetPath) ? targetPath : null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
