namespace VideoEditor.Application.Commands;

/// <summary>
/// Groups multiple commands into a single undo/redo step
/// (e.g. splitting linked audio+video, importing multiple files).
/// </summary>
public class CompositeCommand : IEditorCommand
{
    private readonly List<IEditorCommand> _commands;

    public CompositeCommand(string description, IEnumerable<IEditorCommand> commands)
    {
        Description = description;
        _commands = commands.ToList();
    }

    public string Description { get; }

    public void Execute()
    {
        foreach (var command in _commands) command.Execute();
    }

    public void Undo()
    {
        for (var i = _commands.Count - 1; i >= 0; i--) _commands[i].Undo();
    }
}
