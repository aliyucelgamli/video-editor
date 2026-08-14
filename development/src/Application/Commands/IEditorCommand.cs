namespace VideoEditor.Application.Commands;

/// <summary>
/// An undoable editing operation (Command pattern).
/// Named IEditorCommand to avoid clashing with WPF's System.Windows.Input.ICommand.
/// </summary>
public interface IEditorCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}
