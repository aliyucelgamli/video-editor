namespace VideoEditor.MediaEngine.Audio;

/// <summary>
/// What the master stage of a sound clip's filter graph does after its pieces
/// are joined. It exists as one record so auditioning and exporting cannot
/// drift apart: both derive their options from the same
/// <see cref="AudioExportSettings"/>, and the differences between them are
/// stated in one place (<see cref="ForPreview"/>) instead of being scattered
/// across two argument builders.
/// </summary>
public sealed record AudioChainOptions
{
    public int SampleRate { get; init; } = AudioExportSettings.DefaultSampleRate;
    public AudioNormalizeMode Normalize { get; init; } = AudioNormalizeMode.None;
    public bool TrimSilence { get; init; }
    public double SilenceThresholdDb { get; init; } = -50;
    public double PeakTargetDb { get; init; } = -1;
    public double LoudnessTargetLufs { get; init; } = -16;

    /// <summary>Measured peak correction fed back in by a two-pass normalize.</summary>
    public double ExtraGainDb { get; init; }

    /// <summary>Appends volumedetect and touches no levels — the measuring pass.</summary>
    public bool AnalyzeOnly { get; init; }

    /// <summary>Where the kept window of the FINISHED result starts, in output seconds.</summary>
    public double WindowStart { get; init; }

    /// <summary>Length of that window; 0 keeps everything.</summary>
    public double WindowDuration { get; init; }

    public bool HasWindow => WindowDuration > 0 || WindowStart > 0;

    /// <summary>The chain an export run needs.</summary>
    public static AudioChainOptions For(
        AudioExportSettings settings, double extraGainDb = 0, bool analyzeOnly = false) => new()
    {
        SampleRate = settings.EffectiveSampleRate,
        // The measuring pass must read the level the chain produces on its own.
        Normalize = analyzeOnly ? AudioNormalizeMode.None : settings.Normalize,
        TrimSilence = settings.TrimSilence,
        SilenceThresholdDb = settings.SilenceThresholdDb,
        PeakTargetDb = settings.PeakTargetDb,
        LoudnessTargetLufs = settings.LoudnessTargetLufs,
        ExtraGainDb = analyzeOnly ? 0 : extraGainDb,
        AnalyzeOnly = analyzeOnly
    };

    /// <summary>
    /// The same chain, windowed to the span being auditioned. The window is cut
    /// out of the FINISHED result rather than out of the model, so cuts, fades,
    /// levels, effects and the silence trim are sample-for-sample what the file
    /// will contain — even when the playhead sits inside a fade.
    ///
    /// Normalization is the one stage left out, and it has to be: a peak
    /// normalize only knows its gain after a measuring pass, and single-pass
    /// loudnorm is adaptive, so its output for a window genuinely differs from
    /// the same window of a full render (measured: a 3 s window came out 45 ms
    /// shorter and 16 dB off nulling). Auditioning therefore plays the edit at
    /// its natural level, and the level is set when the file is written. The UI
    /// says so when a normalize is armed.
    /// </summary>
    public AudioChainOptions ForPreview(double windowStart, double windowDuration) => this with
    {
        Normalize = AudioNormalizeMode.None,
        ExtraGainDb = 0,
        AnalyzeOnly = false,
        WindowStart = Math.Max(0, windowStart),
        WindowDuration = Math.Max(0, windowDuration)
    };
}
