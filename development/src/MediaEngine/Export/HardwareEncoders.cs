using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Export;

/// <summary>
/// Detects GPU video encoders (NVIDIA NVENC, Intel Quick Sync, AMD AMF)
/// available in the local ffmpeg build AND actually working on this machine.
/// An encoder listed by "ffmpeg -encoders" can still fail at runtime (nvenc
/// without an NVIDIA card, for example), so each candidate is verified once
/// with a tiny throw-away encode; results are cached per ffmpeg binary.
/// GPU encoders are typically 5-20x faster than libx264/libx265.
/// </summary>
public static class HardwareEncoders
{
    /// <summary>One GPU encoder option: the ffmpeg codec name + a friendly label.</summary>
    public record Candidate(string Encoder, string DisplayName);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Candidate?> VerifiedCache = new();

    /// <summary>GPU candidates for a format, best first. Empty = CPU only.</summary>
    public static IReadOnlyList<Candidate> CandidatesFor(ExportFormat format) => format switch
    {
        ExportFormat.Mp4H264 => new[]
        {
            new Candidate("h264_nvenc", "NVIDIA NVENC"),
            new Candidate("h264_qsv", "Intel Quick Sync"),
            new Candidate("h264_amf", "AMD AMF")
        },
        ExportFormat.Mp4Hevc => new[]
        {
            new Candidate("hevc_nvenc", "NVIDIA NVENC"),
            new Candidate("hevc_qsv", "Intel Quick Sync"),
            new Candidate("hevc_amf", "AMD AMF")
        },
        _ => Array.Empty<Candidate>()
    };

    /// <summary>Encoder names offered by an "ffmpeg -encoders" listing (pure, testable).</summary>
    public static HashSet<string> ParseEncoderNames(string encodersOutput)
    {
        // Data lines look like: " V....D h264_nvenc   NVIDIA NVENC H.264 encoder".
        // Legend lines (" V..... = Video") and headers are filtered out.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in encodersOutput.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] == "=") continue;
            if (parts[0].Length == 6 && (parts[0][0] == 'V' || parts[0][0] == 'A' || parts[0][0] == 'S'))
                names.Add(parts[1]);
        }
        return names;
    }

    /// <summary>
    /// Returns the best working GPU encoder for the format, or null when none
    /// works (→ software encoding). The first call probes ffmpeg (a few hundred
    /// milliseconds); later calls answer from the cache.
    /// </summary>
    public static async Task<Candidate?> DetectAsync(
        string ffmpegPath, ExportFormat format, CancellationToken cancellationToken = default)
    {
        var candidates = CandidatesFor(format);
        if (candidates.Count == 0) return null;

        var cacheKey = ffmpegPath + "|" + format;
        lock (Gate)
        {
            if (VerifiedCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        Candidate? working = null;
        var listing = await ProcessRunner
            .RunAsync(ffmpegPath, new[] { "-hide_banner", "-encoders" }, cancellationToken)
            .ConfigureAwait(false);
        if (listing.Success)
        {
            var available = ParseEncoderNames(listing.StandardOutput);
            foreach (var candidate in candidates)
            {
                if (!available.Contains(candidate.Encoder)) continue;
                if (await VerifyAsync(ffmpegPath, candidate.Encoder, cancellationToken).ConfigureAwait(false))
                {
                    working = candidate;
                    break;
                }
            }
        }

        lock (Gate) VerifiedCache[cacheKey] = working;
        return working;
    }

    /// <summary>Proves the encoder initializes on this hardware: two black frames to null.</summary>
    private static async Task<bool> VerifyAsync(
        string ffmpegPath, string encoder, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ffmpegPath, new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=black:size=256x144:rate=30:duration=0.2",
            "-frames:v", "2", "-c:v", encoder, "-f", "null", "-"
        }, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }
}
