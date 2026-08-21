namespace VideoEditor.App.Ui;

/// <summary>
/// Names of the app's private drag &amp; drop payloads. They live here because
/// more than one window is a drop target for them (the timeline and the sound
/// editor both accept a library item), and a typo in one of the strings would
/// silently break dragging.
/// </summary>
public static class DragFormats
{
    /// <summary>A media library entry, carried as its <c>MediaItem.Id</c> string.</summary>
    public const string MediaId = "VideoEditorMediaId";

    /// <summary>An effect from the Effects panel, carried as its definition id.</summary>
    public const string EffectId = "VideoEditorEffectId";
}
