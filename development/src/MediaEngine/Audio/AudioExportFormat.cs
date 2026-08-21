namespace VideoEditor.MediaEngine.Audio;

/// <summary>Container/codec choices offered by the sound editor's export panel.</summary>
public enum AudioExportFormat
{
    Mp3,
    OggVorbis,
    Opus,
    Wav,
    Flac,
    M4aAac
}

/// <summary>How much the exported level is adjusted before writing the file.</summary>
public enum AudioNormalizeMode
{
    None,

    /// <summary>Two pass: measure the true peak, then lift it to the target dBFS.</summary>
    Peak,

    /// <summary>Single pass EBU R128 loudness (ffmpeg loudnorm).</summary>
    Loudness
}

/// <summary>Sample-format choice for the uncompressed formats.</summary>
public enum WavBitDepth
{
    Pcm16,
    Pcm24,
    Float32
}

public static class AudioExportFormats
{
    /// <summary>Sample rates Opus actually accepts; anything else is rejected by libopus.</summary>
    private static readonly int[] OpusRates = { 8000, 12000, 16000, 24000, 48000 };

    public static string Extension(this AudioExportFormat format) => format switch
    {
        AudioExportFormat.Mp3 => ".mp3",
        AudioExportFormat.OggVorbis => ".ogg",
        AudioExportFormat.Opus => ".opus",
        AudioExportFormat.Wav => ".wav",
        AudioExportFormat.Flac => ".flac",
        _ => ".m4a"
    };

    public static string DisplayName(this AudioExportFormat format) => format switch
    {
        AudioExportFormat.Mp3 => "MP3 — MPEG Layer III",
        AudioExportFormat.OggVorbis => "OGG — Vorbis",
        AudioExportFormat.Opus => "OPUS — Ogg Opus",
        AudioExportFormat.Wav => "WAV — uncompressed PCM",
        AudioExportFormat.Flac => "FLAC — lossless",
        _ => "M4A — AAC"
    };

    public static string SaveDialogFilter(this AudioExportFormat format) => format switch
    {
        AudioExportFormat.Mp3 => "MP3 Audio (*.mp3)|*.mp3",
        AudioExportFormat.OggVorbis => "OGG Vorbis Audio (*.ogg)|*.ogg",
        AudioExportFormat.Opus => "Opus Audio (*.opus)|*.opus",
        AudioExportFormat.Wav => "WAV Audio (*.wav)|*.wav",
        AudioExportFormat.Flac => "FLAC Audio (*.flac)|*.flac",
        _ => "M4A Audio (*.m4a)|*.m4a"
    };

    /// <summary>False for the lossless formats, where a bitrate means nothing.</summary>
    public static bool UsesBitrate(this AudioExportFormat format) =>
        format is AudioExportFormat.Mp3 or AudioExportFormat.OggVorbis
            or AudioExportFormat.Opus or AudioExportFormat.M4aAac;

    public static bool IsLossless(this AudioExportFormat format) =>
        format is AudioExportFormat.Wav or AudioExportFormat.Flac;

    /// <summary>
    /// Nearest sample rate the codec supports. Only Opus is fussy; everything
    /// else takes whatever the user picked.
    /// </summary>
    public static int ClampSampleRate(this AudioExportFormat format, int sampleRate)
    {
        if (format != AudioExportFormat.Opus) return sampleRate;

        var best = OpusRates[0];
        foreach (var rate in OpusRates)
            if (Math.Abs(rate - sampleRate) < Math.Abs(best - sampleRate)) best = rate;
        return best;
    }

    /// <summary>Rough bytes per second, used for the size estimate label.</summary>
    public static double BytesPerSecond(
        this AudioExportFormat format, int bitrateKbps, int sampleRate, int channels, int bytesPerSample = 2)
    {
        if (format.UsesBitrate()) return bitrateKbps * 1000.0 / 8.0;
        // PCM is exact; FLAC lands around 60% of it for typical material.
        // FLAC is always 16-bit here, so it ignores the WAV sample size.
        if (format == AudioExportFormat.Flac) return sampleRate * channels * 2.0 * 0.6;
        return sampleRate * channels * (double)Math.Clamp(bytesPerSample, 1, 4);
    }
}
