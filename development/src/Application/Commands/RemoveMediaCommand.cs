using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>
/// Removes a media reference from the project library (never touches the file
/// on disk). The caller must ensure no timeline event still uses the item.
/// </summary>
public class RemoveMediaCommand : IEditorCommand
{
    private readonly Project _project;
    private readonly MediaItem _item;
    private int _index;

    public RemoveMediaCommand(Project project, MediaItem item)
    {
        _project = project;
        _item = item;
    }

    public string Description => $"Remove '{_item.Name}' from library";

    public void Execute()
    {
        _index = _project.Media.Items.IndexOf(_item);
        if (_index >= 0) _project.Media.Items.RemoveAt(_index);
    }

    public void Undo()
    {
        if (_index < 0) return;
        _project.Media.Items.Insert(Math.Min(_index, _project.Media.Items.Count), _item);
    }
}
