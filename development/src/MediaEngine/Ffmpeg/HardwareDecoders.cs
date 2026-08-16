namespace VideoEditor.MediaEngine.Ffmpeg;

/// <summary>
/// Detects a GPU video decoder that actually works on this machine (NVIDIA
/// CUDA/NVDEC, D3D11VA, DXVA2, Intel Quick Sync).
///
/// Landing on a new preview frame means seeking to the nearest keyframe and
/// decoding forward to the wanted position — up to a whole GOP of full
/// resolution frames. On the GPU that work is far cheaper, and since we do not
/// ask ffmpeg to keep frames in GPU memory the decoded pixels come back to
/// system RAM automatically, so the rest of the pipeline is untouched.
///
/// Listing is not proof: an accelerator present in the build can still fail to
/// initialise (no driver, headless session, busy device). Each candidate is
/// therefore verified once with a throw-away decode and the answer is cached
/// per ffmpeg binary. Everything falls back to software decoding.
/// </summary>
public static class HardwareDecoders
{
    /// <summary>Accelerators worth trying, best first.</summary>
    private static readonly string[] Candidates = { "cuda", "d3d11va", "qsv", "dxva2" };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> VerifiedCache = new();

    /// <summary>Accelerator names from an "ffmpeg -hwaccels" listing (pure, testable).</summary>
    public static HashSet<string> ParseAcceleratorNames(string hwaccelsOutput)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in hwaccelsOutput.Split('\n'))
        {
            var line = raw.Trim();
            // The listing starts with "Hardware acceleration methods:" then one name per line.
            if (line.Length == 0 || line.EndsWith(':')) continue;
            if (line.Contains(' ')) continue;
            names.Add(line);
        }
        return names;
    }

    /// <summary>
    /// The ffmpeg <c>-hwaccel</c> value to use, or null for software decoding.
    /// The first call probes ffmpeg (a few hundred milliseconds); later calls
    /// answer from the cache.
    /// </summary>
    public static async Task<string?> DetectAsync(
        string ffmpegPath, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            if (VerifiedCache.TryGetValue(ffmpegPath, out var cached)) return cached;
        }

        string? working = null;
        string? sample = null;
        try
        {
            var listing = await ProcessRunner
                .RunAsync(ffmpegPath, new[] { "-hide_banner", "-hwaccels" }, cancellationToken)
                .ConfigureAwait(false);
            if (listing.Success)
            {
                var available = ParseAcceleratorNames(listing.StandardOutput);
                if (available.Overlaps(Candidates))
                    sample = await CreateSampleAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);

                if (sample != null)
                    foreach (var candidate in Candidates)
                    {
                        if (!available.Contains(candidate)) continue;
                        if (!await VerifyAsync(ffmpegPath, candidate, sample, cancellationToken)
                                .ConfigureAwait(false))
                            continue;
                        working = candidate;
                        break;
                    }
            }
        }
        catch
        {
            working = null; // detection must never break decoding
        }
        finally
        {
            TryDelete(sample);
        }

        lock (Gate)
        {
            VerifiedCache[ffmpegPath] = working;
        }
        return working;
    }

    /// <summary>Cached answer without probing — null when unknown or unsupported.</summary>
    public static string? Known(string ffmpegPath)
    {
        lock (Gate)
        {
            return VerifiedCache.TryGetValue(ffmpegPath, out var cached) ? cached : null;
        }
    }

    /// <summary>A tiny real H.264 file — lavfi output would never exercise a decoder.</summary>
    private static async Task<string?> CreateSampleAsync(
        string ffmpegPath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ve_hwdec_{Guid.NewGuid():N}.mp4");
        var result = await ProcessRunner.RunAsync(ffmpegPath, new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x480:rate=25:duration=0.4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path
        }, cancellationToken).ConfigureAwait(false);

        if (result.Success && File.Exists(path)) return path;
        TryDelete(path);
        return null;
    }

    /// <summary>
    /// Decodes one frame of the sample through the accelerator. A full frame of
    /// pixels AND a silent stderr are required: ffmpeg happily falls back to
    /// software after printing an init error, which would look like success.
    /// </summary>
    private static async Task<bool> VerifyAsync(
        string ffmpegPath, string accelerator, string samplePath, CancellationToken cancellationToken)
    {
        try
        {
            var (result, bytes) = await ProcessRunner.RunBytesAsync(ffmpegPath, new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-hwaccel", accelerator,
                "-i", samplePath,
                "-an", "-sn", "-dn",
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"
            }, cancellationToken).ConfigureAwait(false);

            return result.Success
                   && bytes.Length >= 640 * 480 * 4
                   && string.IsNullOrWhiteSpace(result.StandardError);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch { /* temp file cleanup is best effort */ }
    }
}
