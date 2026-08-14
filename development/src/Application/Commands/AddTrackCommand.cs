using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

public class AddTrackCommand : IEditorCommand
{
    private readonly Project _project;
    private readonly Track _track;
    private readonly int? _index;

    public AddTrackCommand(Project project, Track track, int? index = null)
    {
        _project = project;
        _track = track;
        _index = index;
    }

    public string Description => $"Add track '{_track.Name}'";

    public void Execute()
    {
        if (_index is int i && i >= 0 && i <= _project.Tracks.Count)
            _project.Tracks.Insert(i, _track);
        else
            _project.Tracks.Add(_track);
    }

    public void Undo() => _project.Tracks.Remove(_track);
}
