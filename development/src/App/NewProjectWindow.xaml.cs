using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace VideoEditor.App;

/// <summary>
/// New Project dialog: name, resolution presets (or custom size) and frame
/// rate. Everything defaults to a standard 1080p project, so OK-through works.
/// </summary>
public partial class NewProjectWindow : Window
{
    private static readonly (string Label, int Width, int Height)[] Presets =
    {
        ("1080p — 1920 × 1080 (16:9)", 1920, 1080),
        ("720p — 1280 × 720 (16:9)", 1280, 720),
        ("1440p — 2560 × 1440 (16:9)", 2560, 1440),
        ("4K UHD — 3840 × 2160 (16:9)", 3840, 2160),
        ("Vertical — 1080 × 1920 (9:16, Shorts/Reels)", 1080, 1920),
        ("Square — 1080 × 1080 (1:1)", 1080, 1080)
    };

    public string ProjectName { get; private set; } = "Untitled Project";
    public int ProjectWidth { get; private set; } = 1920;
    public int ProjectHeight { get; private set; } = 1080;
    public double ProjectFps { get; private set; } = 30;

    public NewProjectWindow()
    {
        InitializeComponent();
        PresetList.ItemsSource = Presets.Select(p => p.Label).ToList();
        PresetList.SelectedIndex = 0;
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetList.SelectedIndex < 0) return;
        var preset = Presets[PresetList.SelectedIndex];
        WidthBox.Text = preset.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = preset.Height.ToString(CultureInfo.InvariantCulture);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(HeightBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            width < 16 || height < 16 || width > 7680 || height > 4320)
        {
            MessageBox.Show("Please enter a valid resolution (16–7680 × 16–4320).",
                "New Project", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!double.TryParse(FpsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ||
            fps < 1 || fps > 120)
        {
            MessageBox.Show("Please enter a valid frame rate (1–120).",
                "New Project", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProjectName = NameBox.Text;
        ProjectWidth = width;
        ProjectHeight = height;
        ProjectFps = fps;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
