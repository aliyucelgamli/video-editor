namespace VideoEditor.App.Ui;

/// <summary>Visual tone of a dialog — picks the icon and its accent color.</summary>
public enum DialogTone
{
    Info,
    Question,
    Warning,
    Error,
    Success
}

/// <summary>
/// One button in a dialog. <paramref name="Result"/> is what the caller gets
/// back, so callers compare against their own keys instead of a fixed enum.
/// </summary>
public sealed record DialogButton(
    string Text,
    string Result,
    bool IsPrimary = false,
    bool IsDestructive = false);

/// <summary>
/// Everything a dialog needs. Buttons are supplied by the caller, so the same
/// window serves confirmations, warnings, error reports and multi-choice
/// questions ("Save / Don't save / Cancel") without new dialog classes.
/// </summary>
public sealed record DialogOptions
{
    public string Title { get; init; } = "Video Editor";
    public string Message { get; init; } = string.Empty;

    /// <summary>Secondary text: paths, technical details, consequences.</summary>
    public string? Details { get; init; }

    public DialogTone Tone { get; init; } = DialogTone.Info;

    /// <summary>Left to right; the primary one is highlighted and focused.</summary>
    public IReadOnlyList<DialogButton> Buttons { get; init; } =
        new[] { new DialogButton("OK", "ok", IsPrimary: true) };

    /// <summary>Result returned when the dialog is closed with Esc or the X button.</summary>
    public string? DismissResult { get; init; }
}
