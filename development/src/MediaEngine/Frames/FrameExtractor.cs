using System.Globalization;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Frames;

/// <summary>A decoded video frame: tightly packed BGRA pixels.</summary>
public record RawFrame(byte[] Bgra, int Width, int Height);

/// <summary>
/// Extracts single frames from video/image files as raw BGRA using ffmpeg.
/// Frames are letterboxed into the requested canvas size, so composition can
/// blend them without further scaling.
/// </summary>
public class FrameExtractor
{
    private readonly FFmpegLocator _locator;

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
}
