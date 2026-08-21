namespace VideoEditor.MediaEngine.Audio;

/// <summary>Options for one sound-editor export run.</summary>
public sealed class AudioExportSettings
{
    public const int DefaultSampleRate = 44100;
    public const int DefaultBitrate = 192;

    public string OutputPath { get; set; } = string.Empty;
    public AudioExportFormat Format { get; set; } = AudioExportFormat.Mp3;

    /// <summary>Target bitrate in kbps; ignored by the lossless formats.</summary>
    public int Bitrate { get; set; } = DefaultBitrate;

    public int SampleRate { get; set; } = DefaultSampleRate;

    /// <summary>1 = mono, 2 = stereo.</summary>
    public int Channels { get; set; } = 2;

    public WavBitDepth BitDepth { get; set; } = WavBitDepth.Pcm16;

    /// <summary>FLAC compression effort, 0 (fast) – 12 (small).</summary>
    public int FlacCompression { get; set; } = 5;

    public AudioNormalizeMode Normalize { get; set; } = AudioNormalizeMode.None;

    /// <summary>Peak target in dBFS when <see cref="Normalize"/> is Peak.</summary>
    public double PeakTargetDb { get; set; } = -1.0;

    /// <summary>Integrated loudness target in LUFS when Normalize is Loudness.</summary>
    public double LoudnessTargetLufs { get; set; } = -16.0;

    /// <summary>Removes leading and trailing near-silence before encoding.</summary>
    public bool TrimSilence { get; set; }

    /// <summary>Threshold in dBFS below which audio counts as silence.</summary>
    public double SilenceThresholdDb { get; set; } = -50.0;

    /// <summary>Sample rate the codec will actually accept.</summary>
    public int EffectiveSampleRate => Format.ClampSampleRate(SampleRate);

    /// <summary>Bytes one sample of one channel takes in an uncompressed file.</summary>
    public int BytesPerSample => BitDepth switch
    {
        WavBitDepth.Pcm24 => 3,
        WavBitDepth.Float32 => 4,
        _ => 2
    };

    public AudioExportSettings Copy() => (AudioExportSettings)MemberwiseClone();

    /// <summary>Estimated output size in bytes for a result of this length.</summary>
    public long EstimateBytes(double durationSeconds) => (long)Math.Max(0,
        durationSeconds * Format.BytesPerSecond(Bitrate, EffectiveSampleRate, Channels, BytesPerSample));
}

/// <summary>
/// Outcome of an export run. <c>Error</c> is a user-readable message;
/// <c>Cancelled</c> separates "the user stopped it" from "it went wrong", so the
/// caller does not put up an error dialog for a deliberate cancel.
/// </summary>
public sealed record AudioExportResult(
    bool Success, string OutputPath, string? Error = null, bool Cancelled = false)
{
    public static AudioExportResult Failed(string error) => new(false, string.Empty, error);

    public static AudioExportResult Stopped() =>
        new(false, string.Empty, "Export was cancelled.", Cancelled: true);
}
