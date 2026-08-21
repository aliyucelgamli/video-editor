using System.Globalization;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;
using VideoEditor.Domain.Sound;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Audio;

/// <summary>
/// Builds the FFmpeg commands for a <see cref="SoundEditSession"/>: one input
/// per segment, joined with <c>concat</c>, then the master chain described by an
/// <see cref="AudioChainOptions"/>. Pure command construction — no processes are
/// started here, so every argument list is unit-testable, and auditioning and
/// exporting are guaranteed to run the same graph.
/// </summary>
public static class AudioClipPlanner
{
    /// <summary>Label the finished audio carries inside the filter graph.</summary>
    private const string OutLabel = "out";

    /// <summary>Silence shorter than this at an edge is left alone.</summary>
    private const double SilenceHold = 0.05;

    /// <summary>One "-ss/-t/-i" triple per segment, in output order.</summary>
    public static List<string> BuildInputArguments(SoundEditSession session)
    {
        var arguments = new List<string>();
        foreach (var segment in session.Segments)
        {
            arguments.Add("-ss");
            arguments.Add(Num(segment.SourceIn));
            arguments.Add("-t");
            arguments.Add(Num(segment.Duration));
            arguments.Add("-i");
            arguments.Add(session.SourcePath);
        }
        return arguments;
    }

    /// <summary>
    /// The whole filter graph: per-segment level and fades, the concat, then the
    /// master stage (silence trim, effects, gain, normalize) and finally the
    /// audition window, if one was asked for.
    /// </summary>
    public static string BuildFilterComplex(
        SoundEditSession session, IEffectCatalog catalog, AudioChainOptions options)
    {
        if (session.Segments.Count == 0)
            throw new InvalidOperationException(
                "There is nothing left to render — the sound clip has no audio in it.");

        var stages = new List<string>();
        var labels = new List<string>();

        for (var i = 0; i < session.Segments.Count; i++)
        {
            var label = $"s{i}";
            stages.Add($"[{i}:a]{string.Join(",", SegmentFilters(session.Segments[i], options.SampleRate))}[{label}]");
            labels.Add($"[{label}]");
        }

        stages.Add(labels.Count > 1
            ? $"{string.Concat(labels)}concat=n={labels.Count}:v=0:a=1[joined]"
            : $"{labels[0]}anull[joined]");

        stages.Add($"[joined]{string.Join(",", MasterFilters(session, catalog, options))}[{OutLabel}]");
        return string.Join(";", stages);
    }

    /// <summary>Full arguments that write the edited sound to its output file.</summary>
    public static List<string> BuildExportArguments(
        SoundEditSession session, IEffectCatalog catalog, AudioExportSettings settings,
        double extraGainDb = 0, bool withProgress = false)
    {
        var options = AudioChainOptions.For(settings, extraGainDb);
        var arguments = new List<string> { "-y", "-loglevel", "error" };
        if (withProgress) arguments.AddRange(new[] { "-progress", "pipe:1", "-nostats" });

        arguments.AddRange(BuildInputArguments(session));
        arguments.AddRange(OutputArguments(session, catalog, options, settings.Channels));
        arguments.AddRange(CodecArguments(settings));
        arguments.Add(settings.OutputPath);
        return arguments;
    }

    /// <summary>
    /// The measuring pass of a peak normalize: identical chain, volumedetect
    /// appended, decoded to nowhere. FFmpeg prints the peak on stderr.
    /// </summary>
    public static List<string> BuildMeasureArguments(
        SoundEditSession session, IEffectCatalog catalog, AudioExportSettings settings)
    {
        var options = AudioChainOptions.For(settings, analyzeOnly: true);
        var arguments = new List<string> { "-y", "-loglevel", "info" };
        arguments.AddRange(BuildInputArguments(session));
        arguments.AddRange(OutputArguments(session, catalog, options, settings.Channels));
        arguments.AddRange(new[] { "-f", "null", "-" });
        return arguments;
    }

    /// <summary>
    /// Arguments for a preview WAV of the export, windowed to the span being
    /// auditioned. The window is cut out of the finished result, so the audition
    /// carries the same fades, effects, level and normalization the file will.
    /// </summary>
    public static List<string> BuildPreviewArguments(
        SoundEditSession session, IEffectCatalog catalog, AudioExportSettings settings,
        string outputWavPath, double fromOutputTime, double maxSeconds)
    {
        var options = AudioChainOptions.For(settings).ForPreview(fromOutputTime, maxSeconds);
        var arguments = new List<string> { "-y", "-loglevel", "error" };
        arguments.AddRange(BuildInputArguments(session));
        arguments.AddRange(OutputArguments(session, catalog, options, settings.Channels));
        arguments.AddRange(new[] { "-c:a", "pcm_s16le", outputWavPath });
        return arguments;
    }

    /// <summary>Encoder flags for the chosen container/codec.</summary>
    public static List<string> CodecArguments(AudioExportSettings settings)
    {
        var bitrate = $"{Math.Clamp(settings.Bitrate, 32, 512)}k";
        return settings.Format switch
        {
            AudioExportFormat.Mp3 => new List<string> { "-c:a", "libmp3lame", "-b:a", bitrate },
            AudioExportFormat.OggVorbis => new List<string> { "-c:a", "libvorbis", "-b:a", bitrate },
            AudioExportFormat.Opus => new List<string> { "-c:a", "libopus", "-b:a", bitrate },
            AudioExportFormat.M4aAac => new List<string> { "-c:a", "aac", "-b:a", bitrate },
            AudioExportFormat.Flac => new List<string>
            {
                "-c:a", "flac",
                "-compression_level", Math.Clamp(settings.FlacCompression, 0, 12)
                    .ToString(CultureInfo.InvariantCulture)
            },
            _ => new List<string> { "-c:a", PcmCodec(settings.BitDepth) }
        };
    }

    /// <summary>
    /// Reads "max_volume: -3.5 dB" out of a volumedetect run. Null when the
    /// line is missing, which makes the caller skip the gain correction.
    /// </summary>
    public static double? ParseMaxVolumeDb(string ffmpegOutput)
    {
        const string marker = "max_volume:";
        var index = ffmpegOutput.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;

        var tail = ffmpegOutput[(index + marker.Length)..].TrimStart();
        var end = tail.IndexOf(' ');
        var number = end < 0 ? tail : tail[..end];
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Reads the elapsed output time out of an "-progress pipe:1" line
    /// ("out_time_us=1234567"). Null for every other line.
    /// </summary>
    public static double? ParseProgressSeconds(string progressLine)
    {
        var separator = progressLine.IndexOf('=');
        if (separator <= 0) return null;

        var key = progressLine[..separator].Trim();
        var value = progressLine[(separator + 1)..].Trim();
        var divisor = key switch
        {
            "out_time_us" => 1_000_000.0,
            "out_time_ms" => 1_000_000.0, // ffmpeg reports microseconds under both keys
            _ => 0.0
        };
        if (divisor == 0) return null;

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? Math.Max(0, ticks / divisor)
            : null;
    }

    // ---------- Graph pieces ----------

    /// <summary>Level and fades of one piece, in its own input's clock.</summary>
    private static IEnumerable<string> SegmentFilters(SoundSegment segment, int sampleRate)
    {
        yield return $"aresample={sampleRate}";
        yield return "aformat=sample_fmts=fltp:channel_layouts=stereo";

        var gain = segment.EffectiveGain;
        if (Math.Abs(gain - 1.0) > 0.001) yield return $"volume={Precise(gain)}";

        // "-ss" as an INPUT option restarts the piece's timestamps at zero, so
        // both fades are positioned against the piece, not the source file.
        if (segment.FadeIn > 0)
            yield return $"afade=t=in:st=0:d={Num(segment.FadeIn)}" +
                         $":curve={AudioFilterGraphBuilder.AfadeCurveFor(segment.FadeInEasing)}";

        if (segment.FadeOut > 0)
            yield return $"afade=t=out:st={Num(Math.Max(0, segment.Duration - segment.FadeOut))}" +
                         $":d={Num(segment.FadeOut)}" +
                         $":curve={AudioFilterGraphBuilder.AfadeCurveFor(segment.FadeOutEasing)}";
    }

    /// <summary>Everything that happens once the pieces are one stream.</summary>
    private static List<string> MasterFilters(
        SoundEditSession session, IEffectCatalog catalog, AudioChainOptions options)
    {
        var master = new List<string>();

        // Silence goes first: measuring loudness across dead air skews the result.
        if (options.TrimSilence) master.AddRange(SilenceTrimFilters(options.SilenceThresholdDb));

        var effects = AudioFilterGraphBuilder.BuildEffectFilter(session.Effects, catalog, options.SampleRate);
        if (effects.Length > 0) master.Add(effects);

        var masterGain = VolumeLimits.Clamp(session.MasterGain);
        if (Math.Abs(masterGain - 1.0) > 0.001) master.Add($"volume={Precise(masterGain)}");

        if (Math.Abs(options.ExtraGainDb) > 0.01) master.Add($"volume={Precise(options.ExtraGainDb)}dB");

        if (options.Normalize == AudioNormalizeMode.Loudness)
        {
            // loudnorm rejects a true-peak ceiling outside [-9, 0].
            var truePeak = Math.Clamp(options.PeakTargetDb, -9, -0.5);
            master.Add($"loudnorm=I={Num(options.LoudnessTargetLufs)}:TP={Num(truePeak)}:LRA=11");
        }

        // The window comes last, so what is auditioned is a slice of the very
        // samples the export writes.
        if (options.HasWindow)
        {
            var trim = $"atrim=start={Num(options.WindowStart)}";
            if (options.WindowDuration > 0)
                trim += $":end={Num(options.WindowStart + options.WindowDuration)}";
            master.Add(trim);
            master.Add("asetpts=N/SR/TB");
        }

        if (options.AnalyzeOnly) master.Add("volumedetect");
        if (master.Count == 0) master.Add("anull");
        return master;
    }

    /// <summary>Mapping and output-format arguments shared by all three runs.</summary>
    private static IEnumerable<string> OutputArguments(
        SoundEditSession session, IEffectCatalog catalog, AudioChainOptions options, int channels)
    {
        yield return "-filter_complex";
        yield return BuildFilterComplex(session, catalog, options);
        yield return "-map";
        yield return $"[{OutLabel}]";
        yield return "-vn";
        yield return "-ar";
        yield return options.SampleRate.ToString(CultureInfo.InvariantCulture);
        yield return "-ac";
        yield return Math.Clamp(channels, 1, 2).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Cuts near-silence off both ends. silenceremove only works at the head, so
    /// the tail is handled by reversing the stream and doing it again.
    /// <c>start_duration</c> must stay 0: it is the amount of non-silence
    /// silenceremove buffers before it stops trimming, and that buffer is
    /// DISCARDED — a non-zero value eats the first transient. The "leave a short
    /// silence alone" behaviour is <c>start_silence</c>.
    /// </summary>
    private static IEnumerable<string> SilenceTrimFilters(double thresholdDb)
    {
        var trim = $"silenceremove=start_periods=1:start_duration=0" +
                   $":start_silence={Num(SilenceHold)}" +
                   $":start_threshold={Num(thresholdDb)}dB:detection=peak";
        yield return trim;
        yield return "areverse";
        yield return trim;
        yield return "areverse";
    }

    private static string PcmCodec(WavBitDepth depth) => depth switch
    {
        WavBitDepth.Pcm24 => "pcm_s24le",
        WavBitDepth.Float32 => "pcm_f32le",
        _ => "pcm_s16le"
    };

    private static string Num(double value) => FfmpegFormat.Number(value);

    private static string Precise(double value) => FfmpegFormat.PreciseNumber(value);
}
