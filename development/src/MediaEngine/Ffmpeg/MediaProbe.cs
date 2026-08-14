using System.Globalization;
using System.Text.Json;

namespace VideoEditor.MediaEngine.Ffmpeg;

/// <summary>Metadata extracted from a media file by ffprobe.</summary>
public record MediaInfo(
    double? DurationSeconds,
    bool HasVideo,
    bool HasAudio,
    int? Width,
    int? Height,
    double? FrameRate,
    int? AudioSampleRate);

/// <summary>Asynchronously probes media files with ffprobe.</summary>
public class MediaProbe
{
    private readonly FFmpegLocator _locator;

    public MediaProbe(FFmpegLocator locator) => _locator = locator;

    /// <summary>Returns null when ffprobe is unavailable or the file cannot be parsed.</summary>
    public async Task<MediaInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (_locator.FfprobePath is not { } ffprobe || !File.Exists(filePath)) return null;

        var result = await ProcessRunner.RunAsync(ffprobe, new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            filePath
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success) return null;

        try
        {
            return ParseProbeJson(result.StandardOutput);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses ffprobe JSON output (exposed for tests).</summary>
    public static MediaInfo ParseProbeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        double? duration = null;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationValue) &&
            double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            duration = d;

        bool hasVideo = false, hasAudio = false;
        int? width = null, height = null, sampleRate = null;
        double? frameRate = null;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                if (codecType == "video")
                {
                    // Cover art in audio files also reports a video stream; skip attached pictures.
                    var isAttachedPic = stream.TryGetProperty("disposition", out var disposition) &&
                                        disposition.TryGetProperty("attached_pic", out var pic) &&
                                        pic.GetInt32() == 1;
                    if (isAttachedPic) continue;

                    hasVideo = true;
                    if (stream.TryGetProperty("width", out var w)) width = w.GetInt32();
                    if (stream.TryGetProperty("height", out var h)) height = h.GetInt32();
                    if (stream.TryGetProperty("r_frame_rate", out var r))
                        frameRate = ParseRational(r.GetString());
                }
                else if (codecType == "audio")
                {
                    hasAudio = true;
                    if (stream.TryGetProperty("sample_rate", out var sr) &&
                        int.TryParse(sr.GetString(), out var rate))
                        sampleRate = rate;
                }
            }
        }

        return new MediaInfo(duration, hasVideo, hasAudio, width, height, frameRate, sampleRate);
    }

    /// <summary>Parses ffprobe rationals like "30000/1001" into a double.</summary>
    public static double? ParseRational(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
            return numerator / denominator;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            return plain;
        return null;
    }
}
