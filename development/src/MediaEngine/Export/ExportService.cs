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

        try
        {
            var stdin = process.StandardInput.BaseStream;
            for (var frame = 0; frame < frameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var time = range.Start + frame / settings.FrameRate;
                var composed = await _compositor.ComposeAsync(project, time, width, height, cancellationToken)
                    .ConfigureAwait(false);
                await stdin.WriteAsync(composed.Bgra, cancellationToken).ConfigureAwait(false);
                progress?.Report((frame + 1) / (double)frameCount);
            }
            stdin.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException("Video encoding failed.\n" + Tail(stderr.ToString()));
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

        arguments.AddRange(settings.Format switch
        {
            ExportFormat.Mp4Hevc => new[]
            {
                "-c:v", "libx265", "-preset", "fast", "-crf", settings.Crf.ToString(),
                "-pix_fmt", "yuv420p", "-tag:v", "hvc1",
                "-c:a", "aac", "-b:a", "192k"
            },
            ExportFormat.WebMVp9 => new[]
            {
                "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", Math.Min(settings.Crf + 12, 45).ToString(),
                "-row-mt", "1", "-pix_fmt", "yuv420p",
                "-c:a", "libopus", "-b:a", "128k"
            },
            _ => new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-crf", settings.Crf.ToString(),
                "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k"
            }
        });

        arguments.Add("-shortest");
        arguments.Add(settings.OutputPath);
        return arguments;
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(6));
    }
}
