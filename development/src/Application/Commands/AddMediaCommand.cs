using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>Adds a media reference to the project library. Never touches the file on disk.</summary>
public class AddMediaCommand : IEditorCommand
{
    private readonly Project _project;
    private readonly MediaItem _item;

    public AddMediaCommand(Project project, MediaItem item)
    {
        _project = project;
        _item = item;
    }

    public string Description => $"Import '{_item.Name}'";

    public void Execute() => _project.Media.Items.Add(_item);

    public void Undo() => _project.Media.Items.Remove(_item);
}
