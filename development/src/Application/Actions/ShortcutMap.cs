namespace VideoEditor.Application.Actions;

/// <summary>
/// Effective keyboard gestures per action: an action uses its defaults until
/// the user overrides it (including overriding to nothing). Pure data — the
/// view layer parses gesture strings into real key bindings.
/// </summary>
public class ShortcutMap
{
    private readonly Dictionary<string, string[]> _overrides;

    public ShortcutMap(IReadOnlyDictionary<string, string[]>? overrides = null) =>
        _overrides = overrides is null
            ? new Dictionary<string, string[]>()
            : new Dictionary<string, string[]>(overrides);

    /// <summary>The user's changes only (persisted; defaults never are).</summary>
    public Dictionary<string, string[]> ToOverrides() => new(_overrides);

    public IReadOnlyList<string> GesturesFor(ActionDescriptor action) =>
        _overrides.TryGetValue(action.Id, out var gestures) ? gestures : action.DefaultGestures;

    /// <summary>
    /// The action currently holding a gesture, or null. Comparison is
    /// case-insensitive so "ctrl+z" and "Ctrl+Z" collide.
    /// </summary>
    public ActionDescriptor? FindConflict(
        IEnumerable<ActionDescriptor> actions, string gesture, string exceptActionId)
    {
        foreach (var action in actions)
        {
            if (action.Id == exceptActionId) continue;
            if (GesturesFor(action).Any(g => string.Equals(g, gesture, StringComparison.OrdinalIgnoreCase)))
                return action;
        }
        return null;
    }

    /// <summary>Assigns a single gesture to the action (replacing its defaults).</summary>
    public void SetGesture(string actionId, string gesture) =>
        _overrides[actionId] = new[] { gesture };

    /// <summary>Removes one gesture from an action, keeping any others it has.</summary>
    public void RemoveGesture(ActionDescriptor action, string gesture) =>
        _overrides[action.Id] = GesturesFor(action)
            .Where(g => !string.Equals(g, gesture, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>Back to the built-in defaults for every action.</summary>
    public void ResetAll() => _overrides.Clear();
}
