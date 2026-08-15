using System.Diagnostics;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Frames;

/// <summary>
/// Continuous frame stream from one media file: a single ffmpeg process
/// decodes from a source position onward and pipes raw BGRA frames at a fixed
/// preview frame rate. This is what makes playback smooth — spawning one
/// process per frame (the scrub path) costs 100+ ms each, while reading the
/// next frame from a running stream costs almost nothing.
/// Playback rate is baked in via setpts, so frame N always equals
/// timeline time N / fps.
/// </summary>
public sealed class StreamingFramePipe : IDisposable
{
    private readonly Process _process;
    private readonly byte[] _buffer;
    private bool _ended;

    public int Width { get; }
    public int Height { get; }
    public double Fps { get; }

    private StreamingFramePipe(Process process, int width, int height, double fps)
    {
        _process = process;
        Width = width;
        Height = height;
        Fps = fps;
        _buffer = new byte[width * height * 4];
    }

    /// <summary>
    /// Starts streaming <paramref name="mediaPath"/> from <paramref name="sourceStart"/>
    /// (source seconds) at the given playback rate. Returns null when ffmpeg is missing.
    /// </summary>
    public static StreamingFramePipe? Start(
        FFmpegLocator locator, string mediaPath, double sourceStart, double playbackRate,
        int width, int height, double fps)
    {
        if (locator.FfmpegPath is not { } ffmpeg || !File.Exists(mediaPath)) return null;

        width -= width % 2;
        height -= height % 2;
        var rate = playbackRate <= 0 ? 1.0 : playbackRate;

        var filters = new List<string>();
        if (Math.Abs(rate - 1.0) > 0.001)
            filters.Add($"setpts=PTS/{Num(rate)}");
        filters.Add($"fps={Num(fps)}");
        filters.Add($"scale={width}:{height}:force_original_aspect_ratio=decrease");
        filters.Add($"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black");

        var info = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in BuildArguments(mediaPath, sourceStart, filters))
            info.ArgumentList.Add(argument);

        var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            process.BeginErrorReadLine(); // drain stderr so ffmpeg never blocks on it
            return new StreamingFramePipe(process, width, height, fps);
        }
        catch
        {
            process.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Reads the next frame; null at end of stream. The returned array is
    /// reused between calls — consume it before reading again.
    /// </summary>
    public async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        if (_ended) return null;

        var stream = _process.StandardOutput.BaseStream;
        var offset = 0;
        while (offset < _buffer.Length)
        {
            var read = await stream
                .ReadAsync(_buffer.AsMemory(offset, _buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                _ended = true;
                return null; // clean EOF (only a partial frame is discarded)
            }
            offset += read;
        }
        return _buffer;
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
        _process.Dispose();
    }

    private static List<string> BuildArguments(
        string mediaPath, double sourceStart, IReadOnlyList<string> filters)
    {
        var arguments = new List<string> { "-loglevel", "error" };
        if (sourceStart > 0.001)
        {
            arguments.Add("-ss");
            arguments.Add(Num(sourceStart));
        }
        arguments.AddRange(new[]
        {
            "-i", mediaPath,
            "-an",
            "-vf", string.Join(",", filters),
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "pipe:1"
        });
        return arguments;
    }

    private static string Num(double value) => FfmpegFormat.Number(value);
}
