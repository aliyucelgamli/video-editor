using System.Windows;
using Microsoft.Win32;
using VideoEditor.Application.Settings;

namespace VideoEditor.App;

/// <summary>
/// App settings: default export folder and the GPU-encoder default, plus a
/// door into the shortcuts editor. OK writes into the live settings and the
/// caller persists them.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _openShortcuts;

    public SettingsWindow(AppSettings settings, Action openShortcuts)
    {
        InitializeComponent();
        _settings = settings;
        _openShortcuts = openShortcuts;

        ExportFolderBox.Text = settings.DefaultExportFolder ?? string.Empty;
        GpuDefaultCheck.IsChecked = settings.UseHardwareEncoderByDefault;
        ConfirmExitCheck.IsChecked = settings.ConfirmOnExit;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Default export folder" };
        if (dialog.ShowDialog() == true)
            ExportFolderBox.Text = dialog.FolderName;
    }

    private void OpenShortcuts_Click(object sender, RoutedEventArgs e) => _openShortcuts();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var folder = ExportFolderBox.Text.Trim();
        _settings.DefaultExportFolder = folder.Length == 0 ? null : folder;
        _settings.UseHardwareEncoderByDefault = GpuDefaultCheck.IsChecked == true;
        _settings.ConfirmOnExit = ConfirmExitCheck.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
