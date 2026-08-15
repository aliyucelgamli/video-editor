namespace VideoEditor.Application.Actions;

/// <summary>
/// One user-facing editor action: a stable id, a display name and a category.
/// The category grouping is generic on purpose — the shortcut editor uses it
/// today, menus / command palettes can reuse the same registry later.
/// </summary>
public sealed record ActionDescriptor(
    string Id,
    string Name,
    string Category,
    IReadOnlyList<string> DefaultGestures)
{
    public ActionDescriptor(string id, string name, string category, params string[] defaultGestures)
        : this(id, name, category, (IReadOnlyList<string>)defaultGestures) { }
}
