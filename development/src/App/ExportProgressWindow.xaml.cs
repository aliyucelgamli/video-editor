using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VideoEditor.App.ViewModels;

namespace VideoEditor.App;

/// <summary>
/// Modal window shown while a project renders: live percentage, progress bar,
/// elapsed/remaining time and a Cancel button. When the export finishes it
/// switches to a summary with the output location and Play / Open folder /
/// Close actions; a failure shows the friendly error inline.
/// </summary>
public partial class ExportProgressWindow : Window
{
    private readonly ExportSessionViewModel _session;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;

    public ExportProgressWindow(ExportSessionViewModel session)
    {
        InitializeComponent();
        _session = session;
        RunningFileText.Text = Path.GetFileName(session.OutputPath);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateRunningUi();
        _timer.Start();

        _session.PropertyChanged += Session_PropertyChanged;
        UpdateRunningUi();
        ApplyState(); // a very short export may already be finished
    }

    /// <summary>Session updates arrive on the UI thread (Progress&lt;T&gt; posts there).</summary>
    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportSessionViewModel.State)) ApplyState();
        else if (e.PropertyName == nameof(ExportSessionViewModel.Progress)) UpdateRunningUi();
    }

    private void ApplyState()
    {
        switch (_session.State)
        {
            case ExportSessionState.Completed:
                _timer.Stop();
                _elapsed.Stop();
                RunningPanel.Visibility = Visibility.Collapsed;
                DonePanel.Visibility = Visibility.Visible;
                DonePathText.Text = _session.OutputPath;
                DoneTimeText.Text = $"Finished in {FormatSpan(_elapsed.Elapsed)}";
                break;

            case ExportSessionState.Failed:
                _timer.Stop();
                RunningPanel.Visibility = Visibility.Collapsed;
                FailedPanel.Visibility = Visibility.Visible;
                FailedMessageText.Text = _session.ErrorMessage;
                break;

            case ExportSessionState.Cancelled:
                // May fire from the constructor (cancelled before the window
                // ever showed) — closing is only legal once the window loaded.
                if (IsLoaded) Close();
                else Loaded += (_, _) => Close();
                break;
        }
    }

    private void UpdateRunningUi()
    {
        if (_session.State != ExportSessionState.Running) return;

        var progress = Math.Clamp(_session.Progress, 0, 1);
        PercentText.Text = ((int)Math.Round(progress * 100))
            .ToString(CultureInfo.InvariantCulture) + "%";
        Bar.Value = progress * 100;

        var text = "Elapsed " + FormatSpan(_elapsed.Elapsed);
        if (progress > 0.02)
        {
            var remaining = TimeSpan.FromSeconds(
                _elapsed.Elapsed.TotalSeconds * (1 - progress) / progress);
            text += " — about " + FormatSpan(remaining) + " left";
        }
        TimeText.Text = text;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Cancelling…";
        _session.RequestCancel();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_session.OutputPath) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show("The file could not be opened. It may have been moved or deleted.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_session.OutputPath}\"");
        }
        catch
        {
            // Fall back to just opening the directory.
            try
            {
                var directory = Path.GetDirectoryName(_session.OutputPath);
                if (directory != null)
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch { /* nothing sensible left to do */ }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing the window while rendering counts as a cancel.</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_session.State == ExportSessionState.Running)
            _session.RequestCancel();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _session.PropertyChanged -= Session_PropertyChanged;
    }

    private static string FormatSpan(TimeSpan span) => span.TotalHours >= 1
        ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
