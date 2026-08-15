using System.Diagnostics;
using System.Globalization;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;
using VideoEditor.MediaEngine.Ffmpeg;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.MediaEngine.Export;

/// <summary>
/// Renders a timeline range to an MP4 (H.264 + AAC).
/// Video frames go through the same <see cref="FrameCompositor"/> as the
/// preview; audio is mixed by FFmpeg from the original source files.
/// Everything runs off the UI thread; progress is 0..1.
/// </summary>
public class ExportService
{
    private readonly FFmpegLocator _locator;
    private readonly FrameCompositor _compositor;
    private readonly IEffectCatalog _catalog;

    public ExportService(FFmpegLocator locator, FrameCompositor compositor, IEffectCatalog catalog)
    {
        _locator = locator;
        _compositor = compositor;
        _catalog = catalog;
    }

    public async Task ExportAsync(
        Project project,
        ExportSettings settings,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_locator.FfmpegPath is not { } ffmpeg)
            throw new InvalidOperationException(
                "FFmpeg was not found. Install it or set the VIDEOEDITOR_FFMPEG_DIR environment variable.");

        var range = (settings.Range ?? new TimeRange { Start = 0, End = project.Duration }).Normalized();
        if (range.Duration <= 0.001)
            throw new InvalidOperationException("The export range is empty — nothing to render.");

        var width = settings.Width - settings.Width % 2;
        var height = settings.Height - settings.Height % 2;
        var frameCount = Math.Max(1, (int)Math.Round(range.Duration * settings.FrameRate));

        var mixedWav = Path.Combine(Path.GetTempPath(), $"veexport_{Guid.NewGuid():N}.wav");
        try
        {
            await MixAudioAsync(project, range, settings, mixedWav, cancellationToken).ConfigureAwait(false);

            if (settings.Format.IsAudioOnly())
            {
                await WriteAudioOnlyAsync(settings, ffmpeg, mixedWav, cancellationToken).ConfigureAwait(false);
                progress?.Report(1.0);
                return;
            }

            settings.VideoEncoder = settings.UseHardwareEncoder
                ? (await HardwareEncoders.DetectAsync(ffmpeg, settings.Format, cancellationToken)
                    .ConfigureAwait(false))?.Encoder
                : null;

            await EncodeVideoAsync(
                project, range, settings, ffmpeg, mixedWav, width, height, frameCount, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(mixedWav)) File.Delete(mixedWav); } catch { /* temp cleanup */ }
        }
    }

    /// <summary>MP3/WAV exports skip video entirely: the mixed track is the product.</summary>
    private static async Task WriteAudioOnlyAsync(
        ExportSettings settings, string ffmpeg, string mixedWav, CancellationToken cancellationToken)
    {
        if (settings.Format == ExportFormat.Wav)
        {
            File.Copy(mixedWav, settings.OutputPath, overwrite: true);
            return;
        }

        var result = await ProcessRunner.RunAsync(ffmpeg, new[]
        {
            "-y", "-loglevel", "error",
            "-i", mixedWav,
            "-c:a", "libmp3lame", "-b:a", "192k",
            settings.OutputPath
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException("Audio encoding failed.\n" + Tail(result.StandardError));
    }

    private async Task MixAudioAsync(
        Project project, TimeRange range, ExportSettings settings, string wavPath,
        CancellationToken cancellationToken)
    {
        var arguments = AudioMixPlanner.BuildMixArguments(
            project, _catalog, range, settings.AudioSampleRate, wavPath);
        var result = await ProcessRunner.RunAsync(_locator.FfmpegPath!, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException("Audio mixing failed.\n" + Tail(result.StandardError));
    }

    private async Task EncodeVideoAsync(
        Project project, TimeRange range, ExportSettings settings, string ffmpeg, string mixedWav,
        int width, int height, int frameCount, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (var argument in BuildEncoderArguments(settings, mixedWav, width, height))
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        var stderr = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginErrorReadLine();

        // Streaming decoders per event + double buffering: frame N+1 is
        // composed while the encoder is still swallowing frame N.
        using var renderer = new SequentialCompositor(_locator, _compositor);
        var buffers = new[] { new byte[width * height * 4], new byte[width * height * 4] };
        var pendingWrite = Task.CompletedTask;

        try
        {
            var stdin = process.StandardInput.BaseStream;
            for (var frame = 0; frame < frameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var time = range.Start + frame / settings.FrameRate;
                var canvas = buffers[frame & 1];
                await renderer
                    .RenderAsync(project, time, frame, settings.FrameRate, canvas, width, height, cancellationToken)
                    .ConfigureAwait(false);

                await pendingWrite.ConfigureAwait(false); // previous buffer is free again
                pendingWrite = stdin.WriteAsync(canvas, 0, canvas.Length, cancellationToken);
                progress?.Report((frame + 1) / (double)frameCount);
            }
            await pendingWrite.ConfigureAwait(false);
            stdin.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            throw;
        }
        catch (IOException)
        {
            // The encoder died mid-stream (broken pipe) — surface its stderr.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            throw new InvalidOperationException(EncodingFailureMessage(settings, stderr.ToString()));
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(EncodingFailureMessage(settings, stderr.ToString()));
    }

    private static string EncodingFailureMessage(ExportSettings settings, string stderr)
    {
        var message = "Video encoding failed.\n" + Tail(stderr);
        if (settings.VideoEncoder is { } encoder)
            message += $"\n(GPU encoder '{encoder}' was used — disabling \"Hardware encoder\" " +
                       "in the export dialog forces the CPU encoder.)";
        return message;
    }

    /// <summary>Raw BGRA pipe in, chosen container/codec out (exposed for tests).</summary>
    public static List<string> BuildEncoderArguments(
        ExportSettings settings, string mixedWav, int width, int height)
    {
        var arguments = new List<string>
        {
            "-y", "-loglevel", "error",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-s", $"{width}x{height}",
            "-r", settings.FrameRate.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-i", mixedWav,
            "-map", "0:v", "-map", "1:a"
        };

        arguments.AddRange(VideoCodecArguments(settings));
        arguments.AddRange(settings.Format == ExportFormat.WebMVp9
            ? new[] { "-c:a", "libopus", "-b:a", "128k" }
            : new[] { "-c:a", "aac", "-b:a", "192k" });

        arguments.Add("-shortest");
        arguments.Add(settings.OutputPath);
        return arguments;
    }

    /// <summary>Video codec arguments: resolved GPU encoder first, CPU fallback otherwise.</summary>
    private static IEnumerable<string> VideoCodecArguments(ExportSettings settings)
    {
        var crf = settings.Crf.ToString(CultureInfo.InvariantCulture);
        var hevcTag = settings.Format == ExportFormat.Mp4Hevc
            ? new[] { "-tag:v", "hvc1" }
            : Array.Empty<string>();

        if (settings.VideoEncoder is { } gpu && !settings.Format.IsAudioOnly())
        {
            // Quality knobs differ per vendor; all map the CRF slider 1:1.
            string[] gpuArguments = gpu switch
            {
                "h264_nvenc" or "hevc_nvenc" => new[]
                    { "-c:v", gpu, "-preset", "p4", "-rc", "vbr", "-cq", crf, "-b:v", "0", "-pix_fmt", "nv12" },
                "h264_qsv" or "hevc_qsv" => new[]
                    { "-c:v", gpu, "-preset", "fast", "-global_quality", crf, "-pix_fmt", "nv12" },
                "h264_amf" or "hevc_amf" => new[]
                    { "-c:v", gpu, "-quality", "balanced", "-rc", "cqp", "-qp_i", crf, "-qp_p", crf, "-pix_fmt", "nv12" },
                _ => Array.Empty<string>()
            };
            if (gpuArguments.Length > 0) return gpuArguments.Concat(hevcTag);
        }

        return settings.Format switch
        {
            ExportFormat.Mp4Hevc => new[]
            {
                "-c:v", "libx265", "-preset", "fast", "-crf", crf,
                "-pix_fmt", "yuv420p"
            }.Concat(hevcTag),
            ExportFormat.WebMVp9 => new[]
            {
                "-c:v", "libvpx-vp9", "-b:v", "0",
                "-crf", Math.Min(settings.Crf + 12, 45).ToString(CultureInfo.InvariantCulture),
                "-row-mt", "1", "-cpu-used", "4", "-pix_fmt", "yuv420p"
            },
            _ => new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-crf", crf,
                "-pix_fmt", "yuv420p"
            }
        };
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(6));
    }
}
