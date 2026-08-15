using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Export;

namespace VideoEditor.App;

/// <summary>
/// Export dialog: pick a format (video or audio-only), resolution, frame rate
/// and quality. Confirming hands a filled <see cref="ExportSettings"/> (minus
/// the output path, chosen next in a save dialog) back to the caller.
/// </summary>
public partial class ExportWindow : Window
{
    private static readonly ExportFormat[] Formats =
    {
        ExportFormat.Mp4H264,
        ExportFormat.Mp4Hevc,
        ExportFormat.WebMVp9,
        ExportFormat.Mp3,
        ExportFormat.Wav
    };

    /// <summary>One-click platform targets; index 0 is "Custom" (no changes).</summary>
    private sealed record ExportPreset(
        string Name, ExportFormat Format, int Width, int Height, double Fps, double Quality);

    private static readonly ExportPreset[] Presets =
    {
        new("Custom", ExportFormat.Mp4H264, 0, 0, 0, -1),
        new("YouTube 1080p", ExportFormat.Mp4H264, 1920, 1080, 30, 75),
        new("YouTube 4K", ExportFormat.Mp4H264, 3840, 2160, 30, 80),
        new("TikTok / Reels 9:16", ExportFormat.Mp4H264, 1080, 1920, 30, 75),
        new("Instagram Square", ExportFormat.Mp4H264, 1080, 1080, 30, 75),
        new("Discord (small file)", ExportFormat.Mp4H264, 1280, 720, 30, 40)
    };

    private readonly string? _ffmpegPath;
    private bool _applyingPreset;

    public ExportFormat SelectedFormat { get; private set; } = ExportFormat.Mp4H264;
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public double OutputFps { get; private set; }
    public int Crf { get; private set; }
    public bool UseHardwareEncoder { get; private set; } = true;

    public ExportWindow(
        ProjectSettings projectSettings, TimeRange? explicitRange, double projectDuration,
        string? ffmpegPath = null, bool defaultHardwareEncoder = true)
    {
        _ffmpegPath = ffmpegPath;
        InitializeComponent();
        GpuCheck.IsChecked = defaultHardwareEncoder;

        FormatList.ItemsSource = Formats.Select(f => f.DisplayName()).ToList();
        FormatList.SelectedIndex = 0;
        PresetBox.ItemsSource = Presets.Select(p => p.Name).ToList();
        PresetBox.SelectedIndex = 0;

        WidthBox.Text = projectSettings.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = projectSettings.Height.ToString(CultureInfo.InvariantCulture);
        FpsBox.Text = projectSettings.FrameRate.ToString("0.###", CultureInfo.InvariantCulture);

        // Hand-editing any field turns the selection back into "Custom".
        WidthBox.TextChanged += (_, _) => MarkCustomPreset();
        HeightBox.TextChanged += (_, _) => MarkCustomPreset();
        FpsBox.TextChanged += (_, _) => MarkCustomPreset();
        QualitySlider.ValueChanged += (_, _) => MarkCustomPreset();

        var range = explicitRange?.Normalized();
        RangeInfo.Text = range != null
            ? $"Exports the yellow range: {range.Start:0.##}s – {range.End:0.##}s ({range.Duration:0.##}s)."
            : $"Exports the whole project ({projectDuration:0.##}s) — drag the yellow bars to narrow it.";
    }

    private void FormatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatList.SelectedIndex < 0) return;
        SelectedFormat = Formats[FormatList.SelectedIndex];
        VideoSettings.Visibility = SelectedFormat.IsAudioOnly() ? Visibility.Collapsed : Visibility.Visible;
        MarkCustomPreset();
        RefreshGpuPanel();
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedIndex <= 0) return; // "Custom" changes nothing
        var preset = Presets[PresetBox.SelectedIndex];

        _applyingPreset = true;
        FormatList.SelectedIndex = Array.IndexOf(Formats, preset.Format);
        WidthBox.Text = preset.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = preset.Height.ToString(CultureInfo.InvariantCulture);
        FpsBox.Text = preset.Fps.ToString("0.###", CultureInfo.InvariantCulture);
        QualitySlider.Value = preset.Quality;
        _applyingPreset = false;
    }

    private void MarkCustomPreset()
    {
        if (_applyingPreset || PresetBox is null) return;
        PresetBox.SelectedIndex = 0;
    }

    /// <summary>Shows the GPU option only for formats that have GPU encoders.</summary>
    private void RefreshGpuPanel()
    {
        var offersGpu = HardwareEncoders.CandidatesFor(SelectedFormat).Count > 0;
        GpuPanel.Visibility = offersGpu ? Visibility.Visible : Visibility.Collapsed;
        if (offersGpu)
            _ = UpdateGpuInfoAsync(SelectedFormat); // fire-and-forget: label fills in after probing
    }

    /// <summary>Probes for a working GPU encoder and reports it under the checkbox.</summary>
    private async Task UpdateGpuInfoAsync(ExportFormat format)
    {
        if (_ffmpegPath is null)
        {
            GpuInfo.Text = string.Empty;
            return;
        }

        GpuInfo.Text = "Checking for a GPU encoder…";
        try
        {
            var found = await HardwareEncoders.DetectAsync(_ffmpegPath, format);
            if (format != SelectedFormat) return; // the user switched formats meanwhile
            GpuInfo.Text = found != null
                ? $"{found.DisplayName} detected — export will be much faster."
                : "No working GPU encoder found — the CPU encoder will be used.";
        }
        catch
        {
            GpuInfo.Text = string.Empty; // the label is purely informational
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseSettings(out var error))
        {
            MessageBox.Show(error, "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private bool TryParseSettings(out string error)
    {
        error = string.Empty;
        if (SelectedFormat.IsAudioOnly())
        {
            OutputWidth = 2;
            OutputHeight = 2;
            OutputFps = 30;
            Crf = 20;
            return true;
        }

        if (!int.TryParse(WidthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(HeightBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            width < 16 || height < 16 || width > 7680 || height > 4320)
        {
            error = "Please enter a valid resolution (16–7680 × 16–4320).";
            return false;
        }
        if (!double.TryParse(FpsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ||
            fps < 1 || fps > 120)
        {
            error = "Please enter a valid frame rate (1–120).";
            return false;
        }

        OutputWidth = width;
        OutputHeight = height;
        OutputFps = fps;
        // Quality slider 0–100 → CRF 32 (low) … 16 (high).
        Crf = (int)Math.Round(32 - QualitySlider.Value / 100.0 * 16);
        UseHardwareEncoder = GpuCheck.IsChecked == true;
        return true;
    }
}
