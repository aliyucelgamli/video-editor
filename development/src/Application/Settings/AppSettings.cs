namespace VideoEditor.Application.Settings;

/// <summary>
/// User preferences persisted in user/settings.json (project files never
/// carry these). Missing or corrupt files fall back to these defaults.
/// </summary>
public class AppSettings
{
    /// <summary>Export save dialogs start here; null/empty = user/exports.</summary>
    public string? DefaultExportFolder { get; set; }

    /// <summary>Initial state of the export dialog's GPU encoder checkbox.</summary>
    public bool UseHardwareEncoderByDefault { get; set; } = true;

    /// <summary>
    /// Warn about unsaved changes when closing the app. Off by default —
    /// New/Open always ask regardless, since those discard work mid-session.
    /// </summary>
    public bool ConfirmOnExit { get; set; }

    /// <summary>Keyboard shortcut overrides (action id → gestures).</summary>
    public Dictionary<string, string[]> Shortcuts { get; set; } = new();
}
