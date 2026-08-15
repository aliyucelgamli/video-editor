namespace VideoEditor.App.Ui;

/// <summary>
/// App-styled dialogs. View models depend on this instead of MessageBox, so
/// the look stays consistent and the calls stay testable.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows a dialog and returns the chosen button's result (null = dismissed).</summary>
    string? Show(DialogOptions options);

    /// <summary>Two-button question; true when the confirming button was chosen.</summary>
    bool Confirm(
        string title, string message, string confirmText = "OK", string cancelText = "Cancel",
        string? details = null, DialogTone tone = DialogTone.Question, bool destructive = false);

    /// <summary>Single-button notice.</summary>
    void Alert(
        string title, string message, string? details = null, DialogTone tone = DialogTone.Info);
}
