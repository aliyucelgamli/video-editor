using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VideoEditor.App.Ui;
using VideoEditor.Application.Settings;

namespace VideoEditor.App;

/// <summary>
/// App settings: export defaults, preview quality, the developer performance
/// probe, and a door into the shortcuts editor. OK writes into the live
/// settings and the caller persists them.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _openShortcuts;
    private readonly Func<IProgress<string>, CancellationToken, Task<string>>? _runPerformanceTest;
    private readonly IDialogService _dialogs = new DialogService();

    private CancellationTokenSource? _probeCts;

    public SettingsWindow(
        AppSettings settings,
        Action openShortcuts,
        Func<IProgress<string>, CancellationToken, Task<string>>? runPerformanceTest = null)
    {
        InitializeComponent();
        _settings = settings;
        _openShortcuts = openShortcuts;
        _runPerformanceTest = runPerformanceTest;

        ExportFolderBox.Text = settings.DefaultExportFolder ?? string.Empty;
        GpuDefaultCheck.IsChecked = settings.UseHardwareEncoderByDefault;
        ConfirmExitCheck.IsChecked = settings.ConfirmOnExit;
        GpuDecodeCheck.IsChecked = settings.UseHardwareDecoder;

        PreviewQualityBox.ItemsSource = PreviewQuality.All;
        PreviewQualityBox.SelectedItem = PreviewQuality.ForWidth(settings.PreviewWidth);
        PreviewQualityBox.SelectionChanged += PreviewQuality_Changed;
        UpdateQualityHint();

        PerfTestButton.IsEnabled = runPerformanceTest != null;
        Closed += (_, _) => _probeCts?.Cancel();
    }

    private void PreviewQuality_Changed(object sender, SelectionChangedEventArgs e) => UpdateQualityHint();

    private void UpdateQualityHint() =>
        PreviewQualityHint.Text = PreviewQualityBox.SelectedItem is PreviewQuality quality
            ? quality.Description
            : string.Empty;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Default export folder" };
        if (dialog.ShowDialog() == true)
            ExportFolderBox.Text = dialog.FolderName;
    }

    private void OpenShortcuts_Click(object sender, RoutedEventArgs e) => _openShortcuts();

    // ---------- Developer performance probe ----------

    private async void RunPerformanceTest_Click(object sender, RoutedEventArgs e)
    {
        if (_runPerformanceTest is null || _probeCts != null) return;

        var cts = new CancellationTokenSource();
        _probeCts = cts;
        PerfTestButton.IsEnabled = false;
        PerfStatus.Text = "Starting…";

        try
        {
            var progress = new Progress<string>(step => PerfStatus.Text = step);
            var path = await _runPerformanceTest(progress, cts.Token);
            PerfStatus.Text = "Report saved.";
            OfferReport(path);
        }
        catch (OperationCanceledException)
        {
            PerfStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            PerfStatus.Text = "Failed.";
            _dialogs.Alert("Performance test", "The test could not finish.", ex.Message, DialogTone.Error);
        }
        finally
        {
            cts.Dispose();
            _probeCts = null;
            PerfTestButton.IsEnabled = true;
        }
    }

    private void OfferReport(string path)
    {
        var choice = _dialogs.Show(new DialogOptions
        {
            Title = "Performance report",
            Message = "The report is ready. Open it and share the text to guide the next optimisation.",
            Details = path,
            Tone = DialogTone.Success,
            Buttons = new[]
            {
                new DialogButton("Close", "close"),
                new DialogButton("Show folder", "folder"),
                new DialogButton("Open report", "open", IsPrimary: true)
            },
            DismissResult = "close"
        });

        try
        {
            if (choice == "open")
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (choice == "folder")
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch
        {
            // No shell association for .txt is not worth an error dialog.
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var folder = ExportFolderBox.Text.Trim();
        _settings.DefaultExportFolder = folder.Length == 0 ? null : folder;
        _settings.UseHardwareEncoderByDefault = GpuDefaultCheck.IsChecked == true;
        _settings.ConfirmOnExit = ConfirmExitCheck.IsChecked == true;
        if (PreviewQualityBox.SelectedItem is PreviewQuality quality)
            _settings.PreviewWidth = quality.Width;
        _settings.UseHardwareDecoder = GpuDecodeCheck.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
