using System.IO;
using System.Windows;
using System.Windows.Threading;
using VideoEditor.App.Ui;

namespace VideoEditor.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        DarkTitleBar.ApplyToAllWindows(); // dark chrome instead of the white default
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        new DialogService().Alert(
            "Unexpected Error",
            "Something went wrong, but the editor is still running.",
            "Technical details were written to logs/app.log (Help > Open Logs Folder).",
            DialogTone.Error);
        e.Handled = true;
    }

    private static void LogException(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(Environment.CurrentDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "app.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
