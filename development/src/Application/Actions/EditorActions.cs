namespace VideoEditor.Application.Actions;

/// <summary>
/// The built-in action registry, in display order. Gesture strings use the
/// canonical "Ctrl+Shift+Key" form; keys use WPF Key names with the friendly
/// aliases "+", "-", "Num+", "Num-".
/// </summary>
public static class EditorActions
{
    public static readonly IReadOnlyList<ActionDescriptor> All = new[]
    {
        new ActionDescriptor("file.new", "New Project", "File", "Ctrl+N"),
        new ActionDescriptor("file.open", "Open Project", "File", "Ctrl+O"),
        new ActionDescriptor("file.save", "Save", "File", "Ctrl+S"),
        new ActionDescriptor("file.saveAs", "Save As", "File", "Ctrl+Shift+S"),
        new ActionDescriptor("file.import", "Import Media", "File"),
        new ActionDescriptor("file.export", "Export", "File"),

        new ActionDescriptor("edit.undo", "Undo", "Edit", "Ctrl+Z", "Z"),
        new ActionDescriptor("edit.redo", "Redo", "Edit", "Ctrl+Y", "Y"),
        new ActionDescriptor("edit.delete", "Delete Selected", "Edit", "Delete"),
        new ActionDescriptor("edit.split", "Split at Playhead", "Edit", "S", "X"),
        new ActionDescriptor("edit.unlink", "Unlink Audio/Video", "Edit", "T"),
        new ActionDescriptor("edit.addText", "Add Text", "Edit"),

        new ActionDescriptor("playback.toggle", "Play / Pause", "Playback", "Space"),

        new ActionDescriptor("timeline.rangeStart", "Set Export Range Start", "Timeline", "I"),
        new ActionDescriptor("timeline.rangeEnd", "Set Export Range End", "Timeline", "O"),
        new ActionDescriptor("timeline.clearRange", "Clear Export Range", "Timeline", "Ctrl+Shift+R"),

        new ActionDescriptor("view.zoomIn", "Zoom In", "View", "+", "Num+"),
        new ActionDescriptor("view.zoomOut", "Zoom Out", "View", "-", "Num-")
    };

    /// <summary>Actions grouped by category, both levels in declaration order.</summary>
    public static IReadOnlyList<(string Category, IReadOnlyList<ActionDescriptor> Actions)> GroupedByCategory()
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<ActionDescriptor>>();
        foreach (var action in All)
        {
            if (!groups.TryGetValue(action.Category, out var list))
            {
                list = new List<ActionDescriptor>();
                groups[action.Category] = list;
                order.Add(action.Category);
            }
            list.Add(action);
        }
        return order.Select(category =>
            (category, (IReadOnlyList<ActionDescriptor>)groups[category])).ToList();
    }
}
