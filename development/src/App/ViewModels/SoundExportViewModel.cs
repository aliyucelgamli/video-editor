using System.IO;
using VideoEditor.App.Mvvm;
using VideoEditor.MediaEngine.Audio;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// The sound editor's export panel: format, quality and processing options,
/// plus the one-click presets. Owns an <see cref="AudioExportSettings"/> and
/// nothing else, so the editor view model stays about editing.
/// Every list is exposed as plain strings with an index, which keeps the XAML
/// combo boxes trivial and their bindings impossible to get subtly wrong.
/// </summary>
public sealed class SoundExportViewModel : ObservableObject
{
    private static readonly AudioExportFormat[] FormatValues =
    {
        AudioExportFormat.Mp3,
        AudioExportFormat.OggVorbis,
        AudioExportFormat.Opus,
        AudioExportFormat.Wav,
        AudioExportFormat.Flac,
        AudioExportFormat.M4aAac
    };

    private static readonly int[] BitrateValues = { 64, 96, 128, 160, 192, 256, 320 };
    private static readonly int[] SampleRateValues = { 22050, 32000, 44100, 48000 };

    private static readonly WavBitDepth[] BitDepthValues =
        { WavBitDepth.Pcm16, WavBitDepth.Pcm24, WavBitDepth.Float32 };

    private static readonly AudioNormalizeMode[] NormalizeValues =
        { AudioNormalizeMode.None, AudioNormalizeMode.Peak, AudioNormalizeMode.Loudness };

    /// <summary>One-click targets; index 0 is "Custom" and changes nothing.</summary>
    private sealed record Preset(
        string Name, AudioExportFormat Format, int Bitrate, int SampleRate, int Channels,
        AudioNormalizeMode Normalize);

    private static readonly Preset[] Presets =
    {
        new("Custom", AudioExportFormat.Mp3, 0, 0, 0, AudioNormalizeMode.None),
        new("Game SFX — OGG mono", AudioExportFormat.OggVorbis, 128, 44100, 1, AudioNormalizeMode.Peak),
        new("Game music — OGG stereo", AudioExportFormat.OggVorbis, 160, 44100, 2, AudioNormalizeMode.Loudness),
        new("Unity / Unreal — WAV 16-bit", AudioExportFormat.Wav, 0, 44100, 2, AudioNormalizeMode.Peak),
        new("Web / podcast — MP3", AudioExportFormat.Mp3, 192, 44100, 2, AudioNormalizeMode.Loudness),
        new("Voice — OPUS mono", AudioExportFormat.Opus, 64, 48000, 1, AudioNormalizeMode.Loudness),
        new("Archive — FLAC", AudioExportFormat.Flac, 0, 48000, 2, AudioNormalizeMode.None)
    };

    private readonly AudioExportSettings _settings = new();
    private bool _applyingPreset;
    private int _presetIndex;
    private double _clipDuration;

    public IReadOnlyList<string> PresetNames { get; } = Presets.Select(p => p.Name).ToList();

    public IReadOnlyList<string> FormatNames { get; } =
        FormatValues.Select(f => f.DisplayName()).ToList();

    public IReadOnlyList<string> BitrateNames { get; } =
        BitrateValues.Select(b => $"{b} kbps").ToList();

    public IReadOnlyList<string> SampleRateNames { get; } =
        SampleRateValues.Select(r => $"{r / 1000.0:0.#} kHz").ToList();

    public IReadOnlyList<string> ChannelNames { get; } = new[] { "Mono", "Stereo" };

    public IReadOnlyList<string> BitDepthNames { get; } =
        new[] { "16-bit PCM", "24-bit PCM", "32-bit float" };

    public IReadOnlyList<string> NormalizeNames { get; } =
        new[] { "Off — leave the level alone", "Peak — lift to −1 dBFS", "Loudness — −16 LUFS" };

    public AudioExportFormat Format => FormatValues[FormatIndex];

    public int PresetIndex
    {
        get => _presetIndex;
        set
        {
            if (!SetProperty(ref _presetIndex, value)) return;
            if (value > 0) ApplyPreset(Presets[value]);
        }
    }

    public int FormatIndex
    {
        get => Math.Max(0, Array.IndexOf(FormatValues, _settings.Format));
        set
        {
            var format = FormatValues[Math.Clamp(value, 0, FormatValues.Length - 1)];
            if (_settings.Format == format) return;
            _settings.Format = format;
            OnPropertyChanged();
            MarkCustom();
            RaiseFormatDependents();
        }
    }

    public int BitrateIndex
    {
        get => NearestIndex(BitrateValues, _settings.Bitrate);
        set
        {
            var bitrate = BitrateValues[Math.Clamp(value, 0, BitrateValues.Length - 1)];
            if (_settings.Bitrate == bitrate) return;
            _settings.Bitrate = bitrate;
            OnPropertyChanged();
            MarkCustom();
            OnPropertyChanged(nameof(SizeEstimateLabel));
        }
    }

    public int SampleRateIndex
    {
        get => NearestIndex(SampleRateValues, _settings.SampleRate);
        set
        {
            var rate = SampleRateValues[Math.Clamp(value, 0, SampleRateValues.Length - 1)];
            if (_settings.SampleRate == rate) return;
            _settings.SampleRate = rate;
            OnPropertyChanged();
            MarkCustom();
            OnPropertyChanged(nameof(SampleRateNoteLabel));
            OnPropertyChanged(nameof(SizeEstimateLabel));
        }
    }

    /// <summary>0 = mono, 1 = stereo.</summary>
    public int ChannelIndex
    {
        get => _settings.Channels <= 1 ? 0 : 1;
        set
        {
            var channels = value <= 0 ? 1 : 2;
            if (_settings.Channels == channels) return;
            _settings.Channels = channels;
            OnPropertyChanged();
            MarkCustom();
            OnPropertyChanged(nameof(SizeEstimateLabel));
        }
    }

    public int BitDepthIndex
    {
        get => Math.Max(0, Array.IndexOf(BitDepthValues, _settings.BitDepth));
        set
        {
            var depth = BitDepthValues[Math.Clamp(value, 0, BitDepthValues.Length - 1)];
            if (_settings.BitDepth == depth) return;
            _settings.BitDepth = depth;
            OnPropertyChanged();
            MarkCustom();
        }
    }

    public int NormalizeIndex
    {
        get => Math.Max(0, Array.IndexOf(NormalizeValues, _settings.Normalize));
        set
        {
            var mode = NormalizeValues[Math.Clamp(value, 0, NormalizeValues.Length - 1)];
            if (_settings.Normalize == mode) return;
            _settings.Normalize = mode;
            OnPropertyChanged();
            MarkCustom();
        }
    }

    public bool TrimSilence
    {
        get => _settings.TrimSilence;
        set
        {
            if (_settings.TrimSilence == value) return;
            _settings.TrimSilence = value;
            OnPropertyChanged();
        }
    }

    public double FlacCompression
    {
        get => _settings.FlacCompression;
        set
        {
            var level = (int)Math.Round(Math.Clamp(value, 0, 12));
            if (_settings.FlacCompression == level) return;
            _settings.FlacCompression = level;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FlacCompressionLabel));
        }
    }

    public string FlacCompressionLabel => $"level {_settings.FlacCompression}";

    // ---------- Which controls make sense for the chosen format ----------

    public bool ShowsBitrate => Format.UsesBitrate();
    public bool ShowsBitDepth => Format == AudioExportFormat.Wav;
    public bool ShowsFlacCompression => Format == AudioExportFormat.Flac;

    /// <summary>Warns when the codec will override the chosen sample rate.</summary>
    public string SampleRateNoteLabel =>
        _settings.EffectiveSampleRate == _settings.SampleRate
            ? string.Empty
            : $"Opus only accepts a few rates — it will be written at {_settings.EffectiveSampleRate / 1000.0:0.#} kHz.";

    public string ExtensionLabel => Format.Extension();

    public string SizeEstimateLabel => _clipDuration <= 0
        ? "—"
        : FormatSize(_settings.EstimateBytes(_clipDuration));

    /// <summary>Refreshes the size estimate after an edit changed the clip length.</summary>
    public void SetClipDuration(double seconds)
    {
        if (Math.Abs(_clipDuration - seconds) < 0.001) return;
        _clipDuration = seconds;
        OnPropertyChanged(nameof(SizeEstimateLabel));
    }

    /// <summary>A copy of the current options aimed at a chosen file.</summary>
    public AudioExportSettings BuildSettings(string outputPath)
    {
        var copy = _settings.Copy();
        copy.OutputPath = outputPath;
        return copy;
    }

    /// <summary>Suggested file name for the save dialog.</summary>
    public string SuggestFileName(string clipName)
    {
        var stem = Path.GetFileNameWithoutExtension(clipName);
        if (string.IsNullOrWhiteSpace(stem)) stem = "sound";
        foreach (var invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
        return stem + "_edit" + Format.Extension();
    }

    private void ApplyPreset(Preset preset)
    {
        _applyingPreset = true;
        FormatIndex = Array.IndexOf(FormatValues, preset.Format);
        if (preset.Bitrate > 0) BitrateIndex = NearestIndex(BitrateValues, preset.Bitrate);
        SampleRateIndex = NearestIndex(SampleRateValues, preset.SampleRate);
        ChannelIndex = preset.Channels <= 1 ? 0 : 1;
        NormalizeIndex = Array.IndexOf(NormalizeValues, preset.Normalize);
        _applyingPreset = false;
    }

    /// <summary>Hand-editing any field drops the selection back to "Custom".</summary>
    private void MarkCustom()
    {
        if (_applyingPreset || _presetIndex == 0) return;
        _presetIndex = 0;
        OnPropertyChanged(nameof(PresetIndex));
    }

    private void RaiseFormatDependents()
    {
        OnPropertyChanged(nameof(ShowsBitrate));
        OnPropertyChanged(nameof(ShowsBitDepth));
        OnPropertyChanged(nameof(ShowsFlacCompression));
        OnPropertyChanged(nameof(SampleRateNoteLabel));
        OnPropertyChanged(nameof(ExtensionLabel));
        OnPropertyChanged(nameof(SizeEstimateLabel));
    }

    private static int NearestIndex(int[] values, int target)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
            if (Math.Abs(values[i] - target) < Math.Abs(values[best] - target)) best = i;
        return best;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"≈ {bytes / 1_048_576.0:0.#} MB",
        >= 1024 => $"≈ {bytes / 1024.0:0.#} KB",
        _ => $"≈ {bytes} B"
    };
}
