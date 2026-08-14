using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Waveform;

/// <summary>
/// Extracts audio peak data for waveform rendering. FFmpeg decodes the audio
/// to mono 16-bit PCM; we reduce it to N peaks per second and cache the result
/// on disk (cache/waveform) so a file is only decoded once.
/// </summary>
public class WaveformService
{
    private const int DecodeSampleRate = 8000;

    private readonly FFmpegLocator _locator;
    private readonly CachePaths _cache;
    private readonly Dictionary<string, Task<float[]?>> _inFlight = new();
    private readonly object _gate = new();

    public WaveformService(FFmpegLocator locator, CachePaths cache)
    {
        _locator = locator;
        _cache = cache;
    }

    /// <summary>
    /// Returns normalized peaks (0..1), <paramref name="peaksPerSecond"/> values
    /// per second of audio, or null when the file has no decodable audio.
    /// </summary>
    public Task<float[]?> GetPeaksAsync(
        string mediaPath, int peaksPerSecond = 50, CancellationToken cancellationToken = default)
    {
        var key = CachePaths.KeyFor(mediaPath, "peaks", peaksPerSecond);
        var cacheFile = Path.Combine(_cache.Waveform, key + ".peaks");

        if (TryReadCache(cacheFile, out var cached)) return Task.FromResult<float[]?>(cached);

        lock (_gate)
        {
            if (_inFlight.TryGetValue(cacheFile, out var running)) return running;
            var task = GenerateAsync(mediaPath, peaksPerSecond, cacheFile, cancellationToken);
            _inFlight[cacheFile] = task;
            _ = task.ContinueWith(_ => { lock (_gate) _inFlight.Remove(cacheFile); },
                TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>Reduces 16-bit little-endian mono PCM to per-bucket peaks (exposed for tests).</summary>
    public static float[] ComputePeaks(ReadOnlySpan<byte> pcm, int samplesPerBucket)
    {
        if (samplesPerBucket < 1) samplesPerBucket = 1;
        var sampleCount = pcm.Length / 2;
        var bucketCount = (sampleCount + samplesPerBucket - 1) / samplesPerBucket;
        var peaks = new float[bucketCount];

        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = bucket * samplesPerBucket;
            var end = Math.Min(start + samplesPerBucket, sampleCount);
            var max = 0;
            for (var i = start; i < end; i++)
            {
                int sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                var magnitude = Math.Abs(sample);
                if (magnitude > max) max = magnitude;
            }
            peaks[bucket] = max / 32768f;
        }
        return peaks;
    }

    private async Task<float[]?> GenerateAsync(
        string mediaPath, int peaksPerSecond, string cacheFile, CancellationToken cancellationToken)
    {
        if (_locator.FfmpegPath is not { } ffmpeg || !File.Exists(mediaPath)) return null;

        var (result, pcm) = await ProcessRunner.RunBytesAsync(ffmpeg, new[]
        {
            "-loglevel", "error",
            "-i", mediaPath,
            "-vn",
            "-ac", "1",
            "-ar", DecodeSampleRate.ToString(),
            "-f", "s16le",
            "pipe:1"
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success || pcm.Length < 2) return null;

        var peaks = ComputePeaks(pcm, DecodeSampleRate / peaksPerSecond);
        TryWriteCache(cacheFile, peaks);
        return peaks;
    }

    private static bool TryReadCache(string path, out float[]? peaks)
    {
        peaks = null;
        try
        {
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            peaks = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, peaks, 0, peaks.Length * 4);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryWriteCache(string path, float[] peaks)
    {
        try
        {
            Directory.CreateDirectory(_cache.Waveform);
            var bytes = new byte[peaks.Length * 4];
            Buffer.BlockCopy(peaks, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(path, bytes);
        }
        catch { /* cache is best effort */ }
    }
}
