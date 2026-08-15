using System.Windows;

namespace VideoEditor.App.Ui;

/// <summary>
/// Shows <see cref="DialogWindow"/> centered on the active window. Falls back
/// to the main window when the call comes from a view model that has no window
/// of its own.
/// </summary>
public class DialogService : IDialogService
{
    private readonly Func<Window?>? _ownerProvider;

    public DialogService(Func<Window?>? ownerProvider = null) => _ownerProvider = ownerProvider;

    public string? Show(DialogOptions options)
    {
        var window = new DialogWindow(options);
        var owner = _ownerProvider?.Invoke() ?? ActiveWindow();
        if (owner != null && !ReferenceEquals(owner, window) && owner.IsLoaded)
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
        return window.Result;
    }

    public bool Confirm(
        string title, string message, string confirmText = "OK", string cancelText = "Cancel",
        string? details = null, DialogTone tone = DialogTone.Question, bool destructive = false) =>
        Show(new DialogOptions
        {
            Title = title,
            Message = message,
            Details = details,
            Tone = tone,
            Buttons = new[]
            {
                new DialogButton(cancelText, "cancel"),
                new DialogButton(confirmText, "confirm", IsPrimary: true, IsDestructive: destructive)
            },
            DismissResult = "cancel"
        }) == "confirm";

    public void Alert(
        string title, string message, string? details = null, DialogTone tone = DialogTone.Info) =>
        Show(new DialogOptions
        {
            Title = title,
            Message = message,
            Details = details,
            Tone = tone,
            Buttons = new[] { new DialogButton("OK", "ok", IsPrimary: true) },
            DismissResult = "ok"
        });

    private static Window? ActiveWindow() =>
        System.Windows.Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive)
        ?? System.Windows.Application.Current?.MainWindow;
}
