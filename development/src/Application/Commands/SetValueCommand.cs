namespace VideoEditor.Application.Commands;

/// <summary>
/// Generic undoable "change one value" command (volume, opacity, export range…).
/// Captures the old value at construction; avoids one command class per property.
/// </summary>
public class SetValueCommand<T> : IEditorCommand
{
    private readonly Action<T> _set;
    private readonly T _newValue;
    private readonly T _oldValue;

    public SetValueCommand(string description, T oldValue, T newValue, Action<T> set)
    {
        Description = description;
        _oldValue = oldValue;
        _newValue = newValue;
        _set = set;
    }

    public string Description { get; }

    public void Execute() => _set(_newValue);

    public void Undo() => _set(_oldValue);
}
