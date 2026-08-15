using VideoEditor.Application.Actions;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// Backs the shortcuts editor: the action registry grouped by category, a
/// listening state, and assignment (with conflict stealing) applied straight
/// to the live <see cref="ShortcutMap"/>. Every change invokes
/// <c>onChanged</c> so the owner saves and rebinds immediately.
/// </summary>
public class ShortcutsViewModel
{
    private readonly ShortcutMap _map;
    private readonly Action _onChanged;
    private ShortcutActionViewModel? _listening;

    public ShortcutsViewModel(ShortcutMap map, Action onChanged)
    {
        _map = map;
        _onChanged = onChanged;
        Categories = EditorActions.GroupedByCategory()
            .Select(group => new ShortcutCategoryViewModel(
                group.Category,
                group.Actions.Select(action => new ShortcutActionViewModel(action)).ToList()))
            .ToList();
        RefreshAll();
    }

    public IReadOnlyList<ShortcutCategoryViewModel> Categories { get; }

    /// <summary>The row currently waiting for a key press, if any.</summary>
    public ShortcutActionViewModel? Listening => _listening;

    public void StartListening(ShortcutActionViewModel row)
    {
        StopListening();
        _listening = row;
        row.IsListening = true;
    }

    public void StopListening()
    {
        if (_listening is { } row) row.IsListening = false;
        _listening = null;
    }

    /// <summary>The action currently holding a gesture (assignment conflict), or null.</summary>
    public ActionDescriptor? FindConflict(string gesture, ShortcutActionViewModel target) =>
        _map.FindConflict(EditorActions.All, gesture, target.Descriptor.Id);

    /// <summary>
    /// Assigns the gesture to the listening row; when another action held it,
    /// that action loses just this gesture.
    /// </summary>
    public void Assign(ShortcutActionViewModel row, string gesture, ActionDescriptor? stealFrom)
    {
        if (stealFrom != null) _map.RemoveGesture(stealFrom, gesture);
        _map.SetGesture(row.Descriptor.Id, gesture);
        StopListening();
        RefreshAll();
        _onChanged();
    }

    public void ResetAll()
    {
        _map.ResetAll();
        StopListening();
        RefreshAll();
        _onChanged();
    }

    private void RefreshAll()
    {
        foreach (var category in Categories)
            foreach (var row in category.Actions)
            {
                var gestures = _map.GesturesFor(row.Descriptor);
                row.GestureText = gestures.Count == 0 ? "—" : string.Join("  /  ", gestures);
            }
    }
}
