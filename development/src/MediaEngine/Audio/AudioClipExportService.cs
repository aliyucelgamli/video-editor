using VideoEditor.Domain.Effects;
using VideoEditor.Domain.Sound;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Audio;

/// <summary>
/// Renders a <see cref="SoundEditSession"/> to an audio file. One ffmpeg run
/// normally; a peak normalize adds a measuring run first, because the true peak
/// of the edited result can only be known after the whole chain has been decoded.
/// Degrades gracefully: with no ffmpeg it reports a friendly failure instead of
/// throwing.
/// </summary>
public sealed class AudioClipExportService
{
    private readonly FFmpegLocator _locator;
    private readonly IEffectCatalog _catalog;

    public AudioClipExportService(FFmpegLocator locator, IEffectCatalog catalog)
    {
        _locator = locator;
        _catalog = catalog;
    }

    /// <summary>
    /// Exports the session. <paramref name="progress"/> receives 0–1 and
    /// <paramref name="status"/> a short phase label; both are called from a
    /// background thread.
    /// </summary>
    public async Task<AudioExportResult> ExportAsync(
        SoundEditSession session,
        AudioExportSettings settings,
        IProgress<double>? progress = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (_locator.FfmpegPath is not { } ffmpeg)
            return AudioExportResult.Failed(
                "FFmpeg was not found, so audio cannot be encoded. Install it with Tools → Download FFmpeg.");

        if (session.IsEmpty)
            return AudioExportResult.Failed("There is nothing to export — the sound clip is empty.");

        if (string.IsNullOrWhiteSpace(settings.OutputPath))
            return AudioExportResult.Failed("No output file was chosen.");

        try
        {
            var duration = session.OutputDuration;
            var extraGainDb = 0.0;

            if (settings.Normalize == AudioNormalizeMode.Peak)
            {
                status?.Report("Measuring the peak level…");
                extraGainDb = await MeasurePeakGainAsync(ffmpeg, session, settings, cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(0.35);
            }

            status?.Report("Encoding…");
            var arguments = AudioClipPlanner.BuildExportArguments(
                session, _catalog, settings, extraGainDb, withProgress: true);

            // The measuring pass already ate a third of the wait, so the encode
            // reports into whatever is left of the bar.
            var encodeFloor = settings.Normalize == AudioNormalizeMode.Peak ? 0.35 : 0.0;
            var result = await ProcessRunner.RunAsync(ffmpeg, arguments, line =>
            {
                if (duration <= 0) return;
                if (AudioClipPlanner.ParseProgressSeconds(line) is not { } seconds) return;
                var fraction = Math.Clamp(seconds / duration, 0, 1);
                progress?.Report(encodeFloor + fraction * (1 - encodeFloor));
            }, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
                return AudioExportResult.Failed(DescribeFailure(settings, result.StandardError));

            progress?.Report(1);
            return new AudioExportResult(true, settings.OutputPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AudioExportResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Decibels of correction needed to land the result's peak on the target.
    /// Zero when the peak could not be read, which leaves the audio untouched.
    /// </summary>
    private async Task<double> MeasurePeakGainAsync(
        string ffmpeg, SoundEditSession session, AudioExportSettings settings,
        CancellationToken cancellationToken)
    {
        var arguments = AudioClipPlanner.BuildMeasureArguments(session, _catalog, settings);
        var result = await ProcessRunner.RunAsync(ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
        if (AudioClipPlanner.ParseMaxVolumeDb(result.StandardError) is not { } peakDb) return 0;

        // A silent clip measures at -91 dB or lower; lifting that is pointless.
        if (peakDb <= -90) return 0;
        return settings.PeakTargetDb - peakDb;
    }

    /// <summary>
    /// Turns raw ffmpeg stderr into something a user can act on. A missing
    /// external encoder is by far the most common cause here.
    /// </summary>
    private static string DescribeFailure(AudioExportSettings settings, string stderr)
    {
        var encoder = settings.Format switch
        {
            AudioExportFormat.Mp3 => "libmp3lame",
            AudioExportFormat.OggVorbis => "libvorbis",
            AudioExportFormat.Opus => "libopus",
            _ => null
        };

        if (encoder != null &&
            (stderr.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase) ||
             stderr.Contains(encoder, StringComparison.OrdinalIgnoreCase) &&
             stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return $"This FFmpeg build has no {encoder} encoder, so {settings.Format.Extension()} " +
                   "files cannot be written. Use WAV or FLAC, or install a full FFmpeg build " +
                   "(Tools → Download FFmpeg).";
        }

        var trimmed = stderr.Trim();
        return trimmed.Length == 0 ? "FFmpeg failed without reporting a reason." : trimmed;
    }
}
